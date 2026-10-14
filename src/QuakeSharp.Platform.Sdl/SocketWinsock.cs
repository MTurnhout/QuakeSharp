using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using QuakeSharp.Core.Net;
using static QuakeSharp.Core.Net.Winsock;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The real network's Winsock (design.md §11.5): what net_wins.c and net_wipx.c ask of wsock32, answered with .NET's
/// UDP sockets, on Windows and Linux alike, with Winsock 1.1's semantics and error codes (.NET's
/// <see cref="SocketError"/> values are Winsock's): IPv4 datagram sockets, non-blocking with FIONBIO, broadcast with
/// SO_BROADCAST, MSG_PEEK (.NET's peek takes the datagram on Windows, so a peek holds it for the next read); the
/// machine's name and its IPv4 addresses, the one its default route leaves from first (decision 80); IPX's family
/// refused, as today's Windows has none. Windows reports the ICMP answer to a datagram sent to a port nobody has as
/// WSAECONNRESET on the next recvfrom; Linux reports nothing on an unconnected socket.
/// </summary>
public sealed partial class SocketWinsock : IWinsock, IDisposable
{
    private const int FirstSocket = 1;

    private readonly Dictionary<int, Socket> sockets = [];
    private readonly Dictionary<int, (byte[] Data, IPEndPoint From)> held = [];
    private readonly byte[] scratch = new byte[65536];
    private int nextSocketNumber = FirstSocket;
    private int lastError;
    private int started;

    /// <summary>How many sockets are open (for tests).</summary>
    public int OpenSockets => sockets.Count;

    public int WSAStartup(ushort wVersionRequired)
    {
        started++;
        return 0;
    }

    public int WSACleanup()
    {
        if (started == 0)
        {
            lastError = WSANOTINITIALISED;
            return SOCKET_ERROR;
        }

        started--;
        return 0;
    }

    public int WSAGetLastError() => lastError;

    public int socket(int af, int type, int protocol)
    {
        if (af != AF_INET)
        {
            lastError = WSAEAFNOSUPPORT;
            return INVALID_SOCKET;
        }

        if (type != SOCK_DGRAM)
        {
            lastError = WSAESOCKTNOSUPPORT;
            return INVALID_SOCKET;
        }

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            int s = nextSocketNumber++;
            sockets[s] = socket;
            return s;
        }
        catch (SocketException e)
        {
            lastError = (int)e.SocketErrorCode;
            return INVALID_SOCKET;
        }
    }

    public int ioctlsocket(int s, int cmd, ref uint argp)
    {
        if (!sockets.TryGetValue(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (cmd == FIONBIO)
        {
            socket.Blocking = argp == 0;
            return 0;
        }

        if (cmd == FIONREAD)
        {
            argp = held.TryGetValue(s, out (byte[] Data, IPEndPoint From) d) ? (uint)d.Data.Length : (uint)socket.Available;
            return 0;
        }

        lastError = WSAEINVAL;
        return SOCKET_ERROR;
    }

    public int setsockopt(int s, int level, int optname, int optval)
    {
        if (!sockets.TryGetValue(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (level != SOL_SOCKET || optname != SO_BROADCAST)
        {
            lastError = WSAENOPROTOOPT;
            return SOCKET_ERROR;
        }

        return Try(() => socket.EnableBroadcast = optval != 0);
    }

    public int bind(int s, in QSockAddr name)
    {
        if (!sockets.TryGetValue(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (name.sa_family != AF_INET)
        {
            lastError = WSAEAFNOSUPPORT;
            return SOCKET_ERROR;
        }

        var endPoint = new IPEndPoint(ToAddress(SinAddr(name)), ntohs(SinPort(name)));
        return Try(() => socket.Bind(endPoint));
    }

    public int recvfrom(int s, byte[] buf, int len, int flags, ref QSockAddr from, bool fromNull = false)
    {
        if (!sockets.TryGetValue(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        // MSG_PEEK: .NET's Peek takes the datagram on Windows, so a peek receives it and holds it for the next read
        if (!held.TryGetValue(s, out (byte[] Data, IPEndPoint From) d))
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                int n = socket.ReceiveFrom(scratch, 0, scratch.Length, SocketFlags.None, ref remote);
                d = (scratch.AsSpan(0, n).ToArray(), (IPEndPoint)remote);
            }
            catch (SocketException e)
            {
                lastError = (int)e.SocketErrorCode;
                return SOCKET_ERROR;
            }

            if ((flags & MSG_PEEK) != 0)
            {
                held[s] = d;
            }
        }
        else if ((flags & MSG_PEEK) == 0)
        {
            held.Remove(s);
        }

        d.Data.AsSpan(0, Math.Min(d.Data.Length, Math.Min(len, buf.Length))).CopyTo(buf);
        if (!fromNull)
        {
            from = ToSockAddr(d.From);
        }

        if (d.Data.Length > len)
        {
            lastError = WSAEMSGSIZE;
            return SOCKET_ERROR;
        }

        return d.Data.Length;
    }

    public int sendto(int s, byte[] buf, int len, int flags, in QSockAddr to)
    {
        if (!sockets.TryGetValue(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (to.sa_family != AF_INET)
        {
            lastError = WSAEAFNOSUPPORT;
            return SOCKET_ERROR;
        }

        var endPoint = new IPEndPoint(ToAddress(SinAddr(to)), ntohs(SinPort(to)));
        try
        {
            return socket.SendTo(buf, 0, len, SocketFlags.None, endPoint);
        }
        catch (SocketException e)
        {
            lastError = (int)e.SocketErrorCode;
            return SOCKET_ERROR;
        }
    }

    public int closesocket(int s)
    {
        if (!sockets.Remove(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        held.Remove(s);

        socket.Dispose();
        return 0;
    }

    public int gethostname(out string name, int namelen)
    {
        name = Dns.GetHostName();
        if (name.Length + 1 > namelen)
        {
            name = "";
            lastError = WSAEFAULT;
            return SOCKET_ERROR;
        }

        return 0;
    }

    public HostEnt? gethostbyname(string name)
    {
        // a dotted address is its own entry, without a lookup (as Winsock answers it)
        if (IPAddress.TryParse(name, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return new HostEnt(name, [FromAddress(parsed)]);
        }

        // the machine's own name: its addresses with the one its default route leaves from first (decision 80).
        // net_wins.c takes the first for its own, binds a listening game's sockets to it and tells it to searching
        // clients; Winsock's first is often a virtual adapter's (WSL, Hyper-V, a VPN), and Linux's often 127.0.1.1,
        // which the other machines on the LAN cannot reach
        HostEnt? entry = LookUp(name);
        if (entry != null && string.Equals(name, Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
        {
            entry = entry with { h_addr_list = PreferRoute(entry.h_addr_list, DefaultRouteAddress()) };
        }

        return entry;
    }

    /// <summary>
    /// The addresses with <paramref name="preferred"/> first: moved there if it is among them, put there if it is not
    /// (the name resolved to a loopback address); as they are without one.
    /// </summary>
    public static uint[] PreferRoute(uint[] addresses, uint? preferred) =>
        preferred is uint p ? [p, .. addresses.Where(a => a != p)] : addresses;

    /// <summary>
    /// The IPv4 address the machine's default route leaves from, as the system picks the source of a datagram to the
    /// Internet: a UDP socket "connected" to a public address, which sends nothing; null without a route (no network).
    /// </summary>
    public static uint? DefaultRouteAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 26000)); // TEST-NET-1: never sent to
            return probe.LocalEndPoint is IPEndPoint { Address: var local } && !IPAddress.IsLoopback(local)
                && !local.Equals(IPAddress.Any)
                ? FromAddress(local)
                : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // gethostbyname's lookup: Winsock's own on Windows, .NET's resolver elsewhere
    private HostEnt? LookUp(string name)
    {
        // Windows: Winsock's own answer, whose addresses come in its order (the machine's first, which WinQuake takes
        // for its own, can be another adapter's than .NET's first)
        if (OperatingSystem.IsWindows())
        {
            HostEnt? winsock = WinsockGetHostByName(name);
            if (winsock == null)
            {
                lastError = WSAHOST_NOT_FOUND;
            }

            return winsock;
        }

        try
        {
            IPHostEntry entry = Dns.GetHostEntry(name);
            uint[] addresses = [.. entry.AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(FromAddress)];
            if (addresses.Length > 0)
            {
                return new HostEnt(entry.HostName, addresses);
            }
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
        }

        lastError = WSAHOST_NOT_FOUND;
        return null;
    }

    // ws2_32's gethostbyname: hostent's h_name, h_aliases, h_addrtype, h_length, h_addr_list
    private static HostEnt? WinsockGetHostByName(string name)
    {
        _ = Socket.OSSupportsIPv4; // .NET's sockets start Winsock (WSAStartup)
        IntPtr entry = ws2_gethostbyname(name);
        if (entry == IntPtr.Zero || Marshal.ReadInt16(entry, 2 * IntPtr.Size) != AF_INET)
        {
            return null;
        }

        string hostName = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(entry, 0)) ?? name;
        IntPtr list = Marshal.ReadIntPtr(entry, ((2 * IntPtr.Size) + 4 + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size);
        var addresses = new List<uint>();
        for (int i = 0; Marshal.ReadIntPtr(list, i * IntPtr.Size) is IntPtr addr && addr != IntPtr.Zero; i++)
        {
            addresses.Add(unchecked((uint)Marshal.ReadInt32(addr)));
        }

        return addresses.Count > 0 ? new HostEnt(hostName, [.. addresses]) : null;
    }

    [LibraryImport("ws2_32.dll", EntryPoint = "gethostbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ws2_gethostbyname(string name);

    public HostEnt? gethostbyaddr(uint addr)
    {
        try
        {
            IPHostEntry entry = Dns.GetHostEntry(ToAddress(addr));
            return new HostEnt(entry.HostName, [addr]);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            lastError = WSAHOST_NOT_FOUND;
            return null;
        }
    }

    public int getsockname(int s, ref QSockAddr name)
    {
        if (!sockets.TryGetValue(s, out Socket? socket))
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (socket.LocalEndPoint is not IPEndPoint local)
        {
            lastError = WSAEINVAL;
            return SOCKET_ERROR;
        }

        name = ToSockAddr(local);
        return 0;
    }

    public void Dispose()
    {
        foreach (Socket socket in sockets.Values)
        {
            socket.Dispose();
        }

        sockets.Clear();
    }

    /// <summary>An address's bytes in memory order (a sockaddr_in's sin_addr) as .NET's address.</summary>
    public static IPAddress ToAddress(uint addr) => new(BitConverter.GetBytes(addr));

    /// <summary>A .NET IPv4 address as its bytes in memory order.</summary>
    public static uint FromAddress(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes());

    private static QSockAddr ToSockAddr(IPEndPoint endPoint) =>
        SockAddrIn(FromAddress(endPoint.Address.MapToIPv4()), htons((ushort)endPoint.Port));

    private int Try(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (SocketException e)
        {
            lastError = (int)e.SocketErrorCode;
            return SOCKET_ERROR;
        }
    }
}
