using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Mailnix.Api.Hosting;

/// <summary>
/// Behind a reverse proxy: the client's address (rate limits per IP) and scheme (Secure cookies) come from
/// X-Forwarded-For/-Proto, but only from the senders in Mailnix:TrustedProxies (addresses or networks, comma-separated).
/// Anyone else could make up those headers, e.g. to get around the sign-in limit.
/// </summary>
internal static class TrustedProxies
{
    public const string Key = "Mailnix:TrustedProxies";

    public static IReadOnlyList<IPNetwork> Parse(string? value) =>
        [.. (value ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Contains('/', StringComparison.Ordinal)
                ? IPNetwork.Parse(entry)
                : new IPNetwork(IPAddress.Parse(entry), IPAddress.Parse(entry).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))];

    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IReadOnlyList<IPNetwork> proxies)
    {
        if (proxies.Count > 0)
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                foreach (var proxy in proxies)
                {
                    options.KnownIPNetworks.Add(proxy);
                }
            });
        }

        return services;
    }
}
