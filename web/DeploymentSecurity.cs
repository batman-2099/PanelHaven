using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace PanelHaven.Web;

public static class DeploymentSecurity
{
    // Call before AddCookie; the return value selects Always vs SameAsRequest cookie security.
    // Environment: Deployment__RequireHttps=true, Deployment__AllowedHosts__0=comics.example.com,
    // and (only behind a proxy) Deployment__KnownProxies__0=127.0.0.1. Lists use indexed keys.
    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        var requireHttps = configuration.GetValue<bool>("Deployment:RequireHttps");
        var hosts = ReadList(configuration, "AllowedHosts");
        var proxyValues = ReadList(configuration, "KnownProxies");
        if (requireHttps && hosts.Length == 0)
            throw new InvalidOperationException("Deployment:AllowedHosts must explicitly list hosts when HTTPS is required.");
        if (!requireHttps && proxyValues.Length != 0)
            throw new InvalidOperationException("Deployment:KnownProxies requires Deployment:RequireHttps=true; local mode never trusts forwarded headers.");

        for (var index = 0; index < hosts.Length; index++)
        {
            var host = hosts[index];
            var kind = Uri.CheckHostName(host);
            if (string.IsNullOrWhiteSpace(host) || host != host.Trim() || host.Contains('*') || kind == UriHostNameType.Unknown
                || (kind == UriHostNameType.IPv6 && !(host.StartsWith('[') && host.EndsWith(']'))))
                throw new InvalidOperationException("Deployment:AllowedHosts requires literal hostnames or IP addresses without ports, schemes or wildcards (IPv6 in brackets).");
            if (kind is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                var address = IPAddress.Parse(host.Trim('[', ']'));
                var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (normalized.Equals(IPAddress.Any) || normalized.Equals(IPAddress.IPv6Any))
                    throw new InvalidOperationException("Deployment:AllowedHosts must not contain wildcard listen addresses.");
                hosts[index] = kind == UriHostNameType.IPv6 ? $"[{address}]" : address.ToString();
            }
        }

        var proxies = new List<IPAddress>();
        foreach (var value in proxyValues)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || !IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException("Deployment:KnownProxies requires individual IP addresses, not names, networks or wildcards.");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)
                || address.IsIPv6Multicast || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239))
                throw new InvalidOperationException("Deployment:KnownProxies requires individual unicast IP addresses.");
            proxies.Add(address);
        }

        // PostConfigure also overrides the platform's opt-in automatic forwarding defaults.
        services.PostConfigure<ForwardedHeadersOptions>(options =>
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
            options.ForwardedHeaders = proxies.Count == 0 ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
        });
        if (requireHttps)
        {
            services.AddHostFiltering(options =>
            {
                options.AllowedHosts = hosts;
                options.AllowEmptyHosts = false;
                options.IncludeFailureMessage = false;
            });
            services.AddHsts(options => options.ExcludedHosts.Clear());
        }
        return requireHttps;
    }

    // Call before security headers, static files, authentication and rate limiting.
    public static void Use(IApplicationBuilder app, bool requireHttps)
    {
        app.UseForwardedHeaders();
        if (requireHttps) app.UseHostFiltering();
        app.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (requireHttps ? !context.Request.IsHttps : remote is null || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = requireHttps ? "HTTPS is required." : "Local mode only accepts loopback connections." });
                return;
            }
            await next(context);
        });
        if (requireHttps) app.UseHsts();
    }

    private static string[] ReadList(IConfiguration configuration, string name)
    {
        var section = configuration.GetSection("Deployment:" + name);
        if (section.Value is not null)
            throw new InvalidOperationException($"Deployment:{name} must be an indexed list (for example Deployment__{name}__0).");
        return section.Get<string[]>() ?? [];
    }
}
