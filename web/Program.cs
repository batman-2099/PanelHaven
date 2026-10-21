using PanelHaven.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Threading.RateLimiting;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
var bootstrapDataPath = Path.GetFullPath(builder.Configuration["DataPath"] ?? "data", builder.Environment.ContentRootPath);
ServerSettings? serverSettings = null;
ServerHost? serverHost = null;
try
{
    // Offline maintenance must retain its explicit CLI paths and never apply server-only overrides.
    if (!ServerSettings.IsMaintenance(builder.Configuration))
    {
        serverSettings = ServerSettings.Load(builder.Configuration, builder.Environment.ContentRootPath);
        DataRelocation.Execute(serverSettings);
        serverSettings.Apply(builder.Configuration);
        serverHost = new ServerHost(serverSettings.Path, bootstrapDataPath);
        builder.Services.AddSingleton(serverSettings);
        builder.Services.AddSingleton(serverHost);
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}
var dataPath = Path.GetFullPath(builder.Configuration["DataPath"] ?? "data", builder.Environment.ContentRootPath);
bool liveBackup;
try { liveBackup = StateMaintenance.ValidateOptions(builder.Configuration, dataPath); }
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}
var databasePath = Path.Combine(dataPath, "panelhaven.sqlite");
var roots = builder.Configuration.GetSection("Libraries").Get<string[]>() ?? [];
roots = roots.Select(path => Path.GetFullPath(path, builder.Environment.ContentRootPath)).ToArray();
string? SpecialRoot(string key) => string.IsNullOrEmpty(builder.Configuration[key]) ? null
    : Path.GetFullPath(builder.Configuration[key]!, builder.Environment.ContentRootPath);
var archivePath = SpecialRoot("ArchivePath");
var unsortedPath = SpecialRoot("UnsortedPath");
if (!liveBackup)
{
    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dataPath);
    else Directory.CreateDirectory(dataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
using var stateLock = liveBackup ? null : StateMaintenance.Acquire(dataPath);
try
{
    if (StateMaintenance.Run(builder.Configuration, dataPath)
        || RelocateCatalog.Run(builder.Configuration, dataPath, roots.Concat(new[] { archivePath, unsortedPath }.OfType<string>()).ToArray())) return;
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException
    or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Xml.XmlException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}
var requireHttps = DeploymentSecurity.Configure(builder.Services, builder.Configuration);
// Startup migrations must not overlap a live snapshot; the lifetime lease still excludes a second server.
using var startupBackupGate = StateMaintenance.AcquireBackupGate(dataPath);
var library = new LibraryStore(databasePath, roots, archivePath, unsortedPath);
var accounts = new Accounts(databasePath);
accounts.Bootstrap(Environment.GetEnvironmentVariable("PANELHAVEN_ADMIN_USERNAME"), Environment.GetEnvironmentVariable("PANELHAVEN_ADMIN_PASSWORD"));
builder.Services.AddSingleton(accounts);
builder.Services.AddSingleton(library);
builder.Services.AddSingleton(_ => new ComicVine(builder.Configuration["ComicVine:ApiKey"]));
builder.Services.AddSingleton(provider => new ThumbnailStore(provider.GetRequiredService<LibraryStore>(), Path.Combine(dataPath, "thumbnails")));
builder.Services.AddSingleton<ScanWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ScanWorker>());
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys"))).SetApplicationName("PanelHaven.Web");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "PanelHaven.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context =>
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<Accounts>();
        var account = context.Principal is null ? null : store.ValidateSession(context.Principal);
        if (account is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
        else context.ReplacePrincipal(Accounts.Principal(account));
    };
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = 401;
        return context.Response.WriteAsJsonAsync(new { error = "Sign in to continue." });
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = 403;
        return context.Response.WriteAsJsonAsync(new { error = "Administrator access required." });
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, token) =>
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many attempts. Try again in a minute." }, token);
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    options.AddPolicy("password", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
startupBackupGate.Dispose();
if (serverSettings!.Persisted) await serverHost!.ApplyBackupAsync(serverSettings.Document.Settings.Backup);
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (error is BadHttpRequestException badRequest)
    {
        context.Response.StatusCode = badRequest.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid request." });
    }
    else
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "The request failed. Check the server log." });
    }
}));
DeploymentSecurity.Use(app, requireHttps);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data: https://comicvine.gamespot.com/a/uploads/; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        // Custom headers cannot be sent by cross-origin forms; CORS is deliberately not enabled.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && context.Request.Headers["X-PanelHaven-Request"] != "1")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Missing request protection header." });
            return;
        }
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")
        && context.Request.Path != "/api/login" && context.Request.Path != "/api/session"
        && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Sign in to continue." });
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api")
        && context.User.FindFirstValue("must_change_password") == "true"
        && context.Request.Path != "/api/session" && context.Request.Path != "/api/logout"
        && context.Request.Path != "/api/account/password")
    {
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "Change your password before continuing.", mustChangePassword = true });
        return;
    }
    await next(context);
});
app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
{
    authenticated = context.User.Identity?.IsAuthenticated == true,
    id = context.User.FindFirstValue(ClaimTypes.NameIdentifier),
    username = context.User.Identity?.Name,
    role = context.User.IsInRole("admin") ? "admin" : context.User.Identity?.IsAuthenticated == true ? "reader" : null,
    mustChangePassword = context.User.FindFirstValue("must_change_password") == "true"
}));
app.MapPost("/api/login", async (LoginRequest request, HttpContext context, Accounts store) =>
{
    var account = store.Authenticate(request.Username, request.Password);
    if (account is null) return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Accounts.Principal(account));
    return Results.Ok(new { authenticated = true, id = account.Id, username = account.Username,
        role = account.Role, mustChangePassword = account.MustChangePassword });
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();
var accountApi = app.MapGroup("/api").RequireAuthorization();
accountApi.AddEndpointFilter(async (context, next) =>
{
    try { return await next(context); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    catch (KeyNotFoundException) { return Results.NotFound(new { error = "Account not found." }); }
    catch (UnauthorizedAccessException) { return Results.Json(new { error = "Account authorization changed. Sign in again." }, statusCode: 403); }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { error = "The account conflicts with existing data." });
    }
    catch (SqliteException) { return Results.Json(new { error = "Account database unavailable. Try again later." }, statusCode: 503); }
});
accountApi.MapPost("/account/password", async (PasswordChangeRequest request, HttpContext context, Accounts store) =>
{
    var actor = AccountActor(context.User);
    if (!store.ChangePassword(actor.Id, actor.Version, request.CurrentPassword, request.NewPassword))
        return Results.BadRequest(new { error = "Current password is incorrect." });
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireRateLimiting("password");
accountApi.MapGet("/users", (Accounts store) => Results.Ok(store.ListUsers().Select(user =>
    new { user.Id, user.Username, user.Role, user.MustChangePassword })))
    .RequireAuthorization(policy => policy.RequireRole("admin"));
accountApi.MapPost("/users", (CreateUserRequest request, ClaimsPrincipal principal, Accounts store) =>
{
    var actor = AccountActor(principal);
    var user = store.CreateManaged(actor.Id, actor.Version, request.Username!, request.Password!, request.Role!);
    return Results.Json(new { user.Id, user.Username, user.Role, user.MustChangePassword }, statusCode: 201);
}).RequireAuthorization(policy => policy.RequireRole("admin"));
accountApi.MapPut("/users/{id}/password", (string id, PasswordResetRequest request, ClaimsPrincipal principal, Accounts store) =>
{
    var actor = AccountActor(principal);
    store.ResetPassword(actor.Id, actor.Version, id, request.Password);
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole("admin"));
accountApi.MapPut("/users/{id}/role", async (string id, RoleChangeRequest request, HttpContext context, Accounts store) =>
{
    var actor = AccountActor(context.User);
    store.ChangeRole(actor.Id, actor.Version, id, request.Role);
    if (actor.Id == id && context.User.FindFirstValue(ClaimTypes.Role) != request.Role)
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole("admin"));
accountApi.MapDelete("/users/{id}", async (string id, HttpContext context, Accounts store) =>
{
    var actor = AccountActor(context.User);
    store.DeleteUser(actor.Id, actor.Version, id);
    if (actor.Id == id) await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole("admin"));
app.MapLibraryEndpoints();
app.MapServerSettingsEndpoints();
app.Run();

static (string Id, long Version) AccountActor(ClaimsPrincipal principal) =>
    (principal.FindFirstValue(ClaimTypes.NameIdentifier)!,
        long.Parse(principal.FindFirstValue("session_version")!, CultureInfo.InvariantCulture));

public sealed record PasswordChangeRequest([property: JsonRequired] string CurrentPassword, [property: JsonRequired] string NewPassword);
public sealed record PasswordResetRequest([property: JsonRequired] string Password);
public sealed record RoleChangeRequest([property: JsonRequired] string Role);
