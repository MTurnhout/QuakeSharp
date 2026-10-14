using System.Net;
using QuakeSharp.Platform.Sdl;

namespace QuakeSharp.Platform.Tests;

/// <summary>
/// The machine's own address as net_wins.c takes it (design.md decision 80): the address the default route leaves from
/// first, so a listening game does not bind to and announce a virtual adapter's (WSL, Hyper-V, a VPN) or 127.0.1.1.
/// </summary>
public sealed class DefaultRouteTests
{
    private static uint A(string dotted) => SocketWinsock.FromAddress(IPAddress.Parse(dotted));

    [Fact]
    public void ThePreferredAddressComesFirstAndTheRestKeepTheirOrder() =>
        Assert.Equal([A("192.168.1.161"), A("172.18.176.1"), A("172.18.192.1")],
            SocketWinsock.PreferRoute([A("172.18.176.1"), A("192.168.1.161"), A("172.18.192.1")], A("192.168.1.161")));

    [Fact]
    public void AnAddressTheNameDoesNotGiveIsPutFirst() =>
        Assert.Equal([A("192.168.1.161"), A("127.0.1.1")], SocketWinsock.PreferRoute([A("127.0.1.1")], A("192.168.1.161")));

    [Fact]
    public void WithoutARouteTheOrderIsKept() =>
        Assert.Equal([A("172.18.176.1"), A("192.168.1.161")], SocketWinsock.PreferRoute([A("172.18.176.1"), A("192.168.1.161")], null));

    [Fact]
    public void TheMachinesOwnNameGivesTheRoutesAddressFirst()
    {
        uint? route = SocketWinsock.DefaultRouteAddress();
        Assert.SkipWhen(route == null, "This machine has no default route.");
        using var winsock = new SocketWinsock();
        Assert.Equal(route, winsock.gethostbyname(Dns.GetHostName())?.h_addr_list[0]);
        Assert.False(IPAddress.IsLoopback(SocketWinsock.ToAddress(route!.Value)));
    }
}
