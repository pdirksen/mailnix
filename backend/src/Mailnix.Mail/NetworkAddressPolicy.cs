using System.Net;

namespace Mailnix.Mail;

internal static class NetworkAddressPolicy
{
    private static readonly IPNetwork[] NonPublicV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"), // carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("224.0.0.0/4"), // multicast
        IPNetwork.Parse("240.0.0.0/4"), // reserved + broadcast
    ];

    private static readonly IPNetwork[] NonPublicV6 =
    [
        IPNetwork.Parse("::/96"), // unspecified, loopback and IPv4-compatible (deprecated)
        IPNetwork.Parse("64:ff9b::/96"), // NAT64: carries an IPv4 address, possibly an internal one
        IPNetwork.Parse("64:ff9b:1::/48"), // local-use NAT64
        IPNetwork.Parse("100::/64"), // discard-only
        IPNetwork.Parse("2001::/32"), // Teredo: tunnels to an embedded IPv4 address
        IPNetwork.Parse("2002::/16"), // 6to4: likewise
        IPNetwork.Parse("fc00::/7"), // unique local
        IPNetwork.Parse("fe80::/10"), // link-local
        IPNetwork.Parse("fec0::/10"), // site-local (deprecated)
        IPNetwork.Parse("ff00::/8"), // multicast
    ];

    /// <summary>True for addresses that are not reachable on the public internet (loopback, private, link-local, …).</summary>
    public static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var networks = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? NonPublicV4 : NonPublicV6;
        return networks.Any(network => network.Contains(address));
    }
}
