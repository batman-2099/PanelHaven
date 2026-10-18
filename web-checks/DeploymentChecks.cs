using ComicRack.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

internal static class DeploymentChecks
{
    public static async Task RunAsync()
    {
        var local = Configuration();
        foreach (var remote in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1" })
        {
            var accepted = await Request(local, "http", remote, "localhost", "198.51.100.9", "https");
            Check(accepted.Response.StatusCode == 204 && accepted.Request.Scheme == "http"
                && accepted.Connection.RemoteIpAddress!.Equals(IPAddress.Parse(remote)), "Local HTTP or ignored spoofed headers failed.");
        }
        foreach (var remote in new[] { "198.51.100.9", "::ffff:198.51.100.9", null })
        {
            var rejected = await Request(local, "http", remote, "localhost", "127.0.0.1", "https");
            Check(rejected.Response.StatusCode == 403 && !rejected.Items.ContainsKey("endpoint"), "Remote HTTP reached the local-mode endpoint.");
        }

        var direct = Configuration(("RequireHttps", "true"), ("AllowedHosts:0", "comics.example.com"));
        var https = await Request(direct, "https", "198.51.100.9", "comics.example.com:443");
        Check(https.Response.StatusCode == 204 && https.Response.Headers.StrictTransportSecurity.ToString().Contains("max-age="),
            "Direct HTTPS or HSTS failed.");
        var http = await Request(direct, "http", "127.0.0.1", "comics.example.com", "198.51.100.9", "https");
        Check(http.Response.StatusCode == 403 && !http.Response.Headers.ContainsKey("Location"), "Implicit loopback proxy trust or HTTPS redirect was enabled.");
        foreach (var host in new[] { "evil.example", "", "sub.comics.example.com" })
        {
            var badHost = await Request(direct, "https", "198.51.100.9", host);
            Check(badHost.Response.StatusCode == 400 && !badHost.Items.ContainsKey("endpoint"), "The host allowlist was bypassed.");
        }

        var proxy = Configuration(("RequireHttps", "true"), ("AllowedHosts:0", "comics.example.com"), ("KnownProxies:0", "127.0.0.1"));
        foreach (var remote in new[] { "127.0.0.1", "::ffff:127.0.0.1" })
        {
            var forwarded = await Request(proxy, "http", remote, "comics.example.com", "198.51.100.9", "https", "evil.example");
            Check(forwarded.Response.StatusCode == 204 && forwarded.Request.IsHttps
                && forwarded.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("198.51.100.9"))
                && forwarded.Request.Host.Value == "comics.example.com"
                && forwarded.Response.Headers.ContainsKey("Strict-Transport-Security"), "Trusted proxy processing or forwarded-host isolation failed.");
        }
        var untrusted = await Request(proxy, "http", "198.51.100.8", "comics.example.com", "127.0.0.1", "https");
        Check(untrusted.Response.StatusCode == 403 && !untrusted.Items.ContainsKey("endpoint"), "An untrusted proxy bypassed the HTTPS gate.");
        var hiddenHost = await Request(proxy, "http", "127.0.0.1", "evil.example", "198.51.100.9", "https", "comics.example.com");
        Check(hiddenHost.Response.StatusCode == 400, "X-Forwarded-Host bypassed the host allowlist.");
        var wrongScheme = await Request(proxy, "http", "127.0.0.1", "comics.example.com", "198.51.100.9", "http");
        Check(wrongScheme.Response.StatusCode == 403, "A trusted proxy's insecure scheme was accepted.");
        var asymmetric = await Request(proxy, "http", "127.0.0.1", "comics.example.com", null, "https");
        Check(asymmetric.Response.StatusCode == 403, "Incomplete proxy headers bypassed the HTTPS gate.");
        var chain = await Request(proxy, "http", "127.0.0.1", "comics.example.com", "203.0.113.4, 198.51.100.9", "http, https");
        Check(chain.Response.StatusCode == 204 && chain.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("198.51.100.9"))
            && chain.Request.Headers["X-Forwarded-For"] == "203.0.113.4", "Forwarding processed more than one hop.");
        var mappedProxy = Configuration(("RequireHttps", "true"), ("AllowedHosts:0", "comics.example.com"), ("KnownProxies:0", "::ffff:127.0.0.1"));
        Check((await Request(mappedProxy, "http", "127.0.0.1", "comics.example.com", "198.51.100.9", "https")).Response.StatusCode == 204,
            "Mapped IPv6 proxy configuration was not normalized.");

        Reject(("RequireHttps", "not-a-boolean"));
        Reject(("RequireHttps", "true"));
        Reject(("KnownProxies:0", "127.0.0.1"));
        foreach (var host in new[] { "*", "*.example.com", "0.0.0.0", "[::]", "::1", "https://comics.example.com", "comics.example.com:443", "", " comics.example.com" })
            Reject(("RequireHttps", "true"), ("AllowedHosts:0", host));
        foreach (var address in new[] { "*", "0.0.0.0", "::", "::ffff:0.0.0.0", "127.0.0.0/8", "localhost", "255.255.255.255", "224.0.0.1", "ff02::1", "" })
            Reject(("RequireHttps", "true"), ("AllowedHosts:0", "comics.example.com"), ("KnownProxies:0", address));
        Reject(("RequireHttps", "true"), ("AllowedHosts", "*"));
        Reject(("RequireHttps", "true"), ("AllowedHosts:0", "comics.example.com"), ("KnownProxies", "127.0.0.1"));
    }

    private static async Task<DefaultHttpContext> Request(IConfiguration configuration, string scheme, string? remote, string host,
        string? forwardedFor = null, string? forwardedProto = null, string? forwardedHost = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Simulate platform automatic forwarding being enabled: deployment options must override it.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        });
        var requireHttps = DeploymentSecurity.Configure(services, configuration);
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        DeploymentSecurity.Use(app, requireHttps);
        app.Run(context =>
        {
            context.Items["endpoint"] = true;
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (forwardedProto is not null) context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        if (forwardedHost is not null) context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        await app.Build()(context);
        return context;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>("Deployment:" + pair.Key, pair.Value))).Build();

    private static void Reject(params (string Key, string Value)[] values)
    {
        try { DeploymentSecurity.Configure(new ServiceCollection(), Configuration(values)); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid deployment configuration was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
