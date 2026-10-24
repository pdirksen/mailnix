using System.Net;

namespace Mailnix.Mail.Tests;

public sealed class NetworkAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.178.1")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:192.168.1.1")] // IPv4-mapped
    public void Non_public_addresses_are_detected(string address)
    {
        NetworkAddressPolicy.IsNonPublic(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("193.168.0.1")]
    [InlineData("2a00:1450:4001:80b::200e")]
    public void Public_addresses_are_allowed(string address)
    {
        NetworkAddressPolicy.IsNonPublic(IPAddress.Parse(address)).ShouldBeFalse();
    }
}
