using System.Text.Json.Serialization;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ServerSettingsMutation
{
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] public long ExpectedRevision { get; init; }
    public string? CurrentPassword { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveServerSettingsRequest : ServerSettingsMutation
{
    [JsonRequired] public ServerSettingsValues Settings { get; init; } = null!;
    public string? ComicVineApiKey { get; init; }
    public bool ClearComicVineApiKey { get; init; }
    public string? CertificatePassword { get; init; }
    public bool ClearCertificatePassword { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerStartupRequest : ServerSettingsMutation
{
    [JsonRequired] public bool Enabled { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerRelocationRequest : ServerSettingsMutation
{
    [JsonRequired] public string Destination { get; init; } = null!;
}

public static class ServerSettingsEndpoints
{
    public static void MapServerSettingsEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/server-settings").RequireAuthorization(policy => policy.RequireRole("admin"));
        api.AddEndpointFilter(async (context, next) =>
        {
            var request = context.Arguments.OfType<ServerSettingsMutation>().SingleOrDefault();
            var store = context.HttpContext.RequestServices.GetRequiredService<ServerSettings>();
            await store.MutationGate.WaitAsync(context.HttpContext.RequestAborted);
            try
            {
                if (request is not null)
                {
                    var accounts = context.HttpContext.RequestServices.GetRequiredService<Accounts>();
                    var principal = context.HttpContext.User;
                    var actor = accounts.ValidateSession(principal);
                    if (actor is null || actor.Role != "admin" || actor.MustChangePassword)
                        return Results.Json(new { error = "Administrator session changed. Sign in again." }, statusCode: 403);
                    var verified = accounts.Authenticate(actor.Username, request.CurrentPassword);
                    if (verified is null) return Results.BadRequest(new { error = "Current password is incorrect." });
                    if (verified.Id != actor.Id || verified.SessionVersion != actor.SessionVersion || verified.Role != "admin" || verified.MustChangePassword)
                        return Results.Json(new { error = "Administrator session changed. Sign in again." }, statusCode: 403);
                    store.RequireRevision(request.ExpectedRevision);
                }
                return await next(context);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
            catch (NotSupportedException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogError(exception, "Server settings operation failed");
                return Results.Json(new { error = "The server settings operation failed. Check filesystem permissions and the server log; reload settings before retrying." }, statusCode: 503);
            }
            finally { store.MutationGate.Release(); }
        });
        api.MapGet("", async (ServerSettings store, ServerHost host) => Results.Ok(await Snapshot(store, host)));
        api.MapPut("", async (SaveServerSettingsRequest request, ServerSettings store, ServerHost host) =>
        {
            store.Update(request.ExpectedRevision, request.Settings, request.ComicVineApiKey, request.ClearComicVineApiKey,
                request.CertificatePassword, request.ClearCertificatePassword);
            await host.ApplyBackupAsync(store.Document.Settings.Backup);
            return Results.Ok(await Snapshot(store, host));
        }).RequireRateLimiting("password");
        api.MapPost("/service", async (ServerStartupRequest request, ServerSettings store, ServerHost host) =>
        {
            await host.SetStartupAsync(request.Enabled);
            store.Save(store.Document with { Revision = store.Document.Revision + 1 });
            await host.ApplyBackupAsync(store.Document.Settings.Backup);
            return Results.Ok(await Snapshot(store, host));
        }).RequireRateLimiting("password");
        api.MapPost("/relocate", async (ServerRelocationRequest request, ServerSettings store, ServerHost host) =>
        {
            if (store.Document.PendingDataPath is not null)
                throw new InvalidOperationException("A data relocation is already pending. Restart before requesting another move.");
            DataRelocation.Validate(store.Document, request.Destination, store.Path);
            store.Save(store.Document with { Revision = store.Document.Revision + 1,
                PendingDataPath = request.Destination, RelocationError = null });
            return Results.Ok(await Snapshot(store, host));
        }).RequireRateLimiting("password");
        api.MapPost("/restart", async (ServerSettingsMutation request, HttpContext context, ServerHost host) =>
        {
            var status = await host.GetStatusAsync();
            if (!status.Supported || !status.Installed)
                return Results.BadRequest(new { error = "Install the managed user service using the startup control before restarting." });
            context.Response.OnCompleted(async () =>
            {
                try
                {
                    await host.RestartAsync();
                    app.Lifetime.StopApplication();
                }
                catch (Exception exception) { app.Logger.LogError(exception, "Could not restart the managed server service"); }
            });
            return Results.Json(new { message = "Restart requested. Reconnect using the saved listen URL after the server starts." }, statusCode: 202);
        }).RequireRateLimiting("password");
    }

    private static async Task<object> Snapshot(ServerSettings store, ServerHost host)
    {
        var document = store.Document;
        return new
        {
            document.Revision, document.Settings, activeSettings = store.Active.Settings,
            secrets = new { comicVineApiKey = !string.IsNullOrEmpty(document.ComicVineApiKey), certificatePassword = !string.IsNullOrEmpty(document.CertificatePassword) },
            store.RestartRequired, settingsPath = store.Path, recoveryPath = store.RecoveryPath,
            document.PendingDataPath, document.RelocationError, service = await host.GetStatusAsync()
        };
    }
}
