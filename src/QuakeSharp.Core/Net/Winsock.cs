using System.Buffers.Binary;
using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Net;

/// <summary>
/// winsock.h and wsipx.h: what net_wins.c and net_wipx.c use of Winsock 1.1, with the SDK's values, and the functions
/// they call from wsock32 directly. Addresses and ports in a <see cref="QSockAddr"/> are in network byte order, as the
/// C keeps them; a uint address holds its bytes in memory order (read as x86 reads them).
/// </summary>
public static class Winsock
{
    public const short AF_INET = 2;
    public const short AF_IPX = 6;
    public const int PF_INET = AF_INET;
    public const int SOCK_DGRAM = 2;
    public const int IPPROTO_UDP = 17;
    public const int NSPROTO_IPX = 1000;

    public const int INVALID_SOCKET = -1;
    public const int SOCKET_ERROR = -1;

    public const int FIONBIO = unchecked((int)0x8004667e);
    public const int FIONREAD = 0x4004667f;
    public const int SOL_SOCKET = 0xffff;
    public const int SO_BROADCAST = 0x0020;
    public const int MSG_PEEK = 0x2;

    public const uint INADDR_ANY = 0x00000000;
    public const uint INADDR_BROADCAST = 0xffffffff;
    public const uint INADDR_NONE = 0xffffffff;

    public const int WSAEACCES = 10013;
    public const int WSAEFAULT = 10014;
    public const int WSAEINVAL = 10022;
    public const int WSAEMFILE = 10024;
    public const int WSAEWOULDBLOCK = 10035;
    public const int WSAENOTSOCK = 10038;
    public const int WSAEMSGSIZE = 10040;
    public const int WSAENOPROTOOPT = 10042;
    public const int WSAESOCKTNOSUPPORT = 10044;
    public const int WSAEAFNOSUPPORT = 10047;
    public const int WSAEADDRINUSE = 10048;
    public const int WSAEADDRNOTAVAIL = 10049;
    public const int WSAECONNRESET = 10054;
    public const int WSAECONNREFUSED = 10061;
    public const int WSANOTINITIALISED = 10093;
    public const int WSAHOST_NOT_FOUND = 11001;

    public static ushort htons(ushort hostshort) => BinaryPrimitives.ReverseEndianness(hostshort);

    public static ushort ntohs(ushort netshort) => BinaryPrimitives.ReverseEndianness(netshort);

    public static uint htonl(uint hostlong) => BinaryPrimitives.ReverseEndianness(hostlong);

    public static uint ntohl(uint netlong) => BinaryPrimitives.ReverseEndianness(netlong);

    // sockaddr_in's fields in a qsockaddr: sin_port at sa_data[0], sin_addr at sa_data[2], both in network byte order
    public static ushort SinPort(in QSockAddr a) => BinaryPrimitives.ReadUInt16LittleEndian(a.sa_data[0..2]);

    public static void SetSinPort(ref QSockAddr a, ushort port) => BinaryPrimitives.WriteUInt16LittleEndian(a.sa_data[0..2], port);

    public static uint SinAddr(in QSockAddr a) => BinaryPrimitives.ReadUInt32LittleEndian(a.sa_data[2..6]);

    public static void SetSinAddr(ref QSockAddr a, uint addr) => BinaryPrimitives.WriteUInt32LittleEndian(a.sa_data[2..6], addr);

    /// <summary>A sockaddr_in: the family, the port and the address in network byte order, the rest zeros.</summary>
    public static QSockAddr SockAddrIn(uint addr, ushort port)
    {
        var a = new QSockAddr { sa_family = AF_INET };
        SetSinPort(ref a, port);
        SetSinAddr(ref a, addr);
        return a;
    }

    /// <summary>
    /// inet_addr: "a.b.c.d" (each part decimal, octal with 0 or hexadecimal with 0x; fewer parts fill the last one's
    /// low bytes, as the BSD function does) to the address's bytes in memory order, INADDR_NONE when invalid.
    /// </summary>
    public static uint inet_addr(string cp)
    {
        var parts = new List<uint>();
        int i = 0;

        while (true)
        {
            if (i >= cp.Length || !char.IsAsciiDigit(cp[i]))
            {
                return INADDR_NONE;
            }

            uint value = 0;
            uint radix = 10;
            if (cp[i] == '0')
            {
                radix = 8;
                i++;
                if (i < cp.Length && (cp[i] == 'x' || cp[i] == 'X'))
                {
                    radix = 16;
                    i++;
                }
            }

            while (i < cp.Length)
            {
                int d = char.IsAsciiDigit(cp[i]) ? cp[i] - '0'
                    : radix == 16 && char.IsAsciiHexDigit(cp[i]) ? (cp[i] | 0x20) - 'a' + 10 : -1;
                if (d < 0 || d >= radix)
                {
                    break;
                }

                value = unchecked((value * radix) + (uint)d);
                i++;
            }

            parts.Add(value);
            if (i < cp.Length && cp[i] == '.' && parts.Count < 4)
            {
                i++;
                continue;
            }

            break;
        }

        if (i < cp.Length && cp[i] != ' ')
        {
            return INADDR_NONE;
        }

        uint host = parts.Count switch
        {
            1 => parts[0],
            2 when parts[0] <= 0xff && parts[1] <= 0xffffff => (parts[0] << 24) | parts[1],
            3 when parts[0] <= 0xff && parts[1] <= 0xff && parts[2] <= 0xffff => (parts[0] << 24) | (parts[1] << 16) | parts[2],
            4 when parts.TrueForAll(p => p <= 0xff) => (parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | parts[3],
            _ => INADDR_NONE,
        };
        return host == INADDR_NONE ? INADDR_NONE : htonl(host);
    }

    /// <summary>inet_ntoa: an address (its bytes in memory order) as "a.b.c.d".</summary>
    public static string inet_ntoa(uint addr) =>
        CFormat.Sprintf("%d.%d.%d.%d", (int)(addr & 0xff), (int)((addr >> 8) & 0xff), (int)((addr >> 16) & 0xff), (int)(addr >> 24));
}

/// <summary>winsock.h: struct hostent, a host's name and its addresses (bytes in memory order).</summary>
public sealed record HostEnt(string h_name, uint[] h_addr_list);

/// <summary>
/// The wsock32 functions net_wins.c and net_wipx.c take through GetProcAddress, and bind, which they call directly
/// (design.md §11.5): the virtual machine's (<see cref="VirtualWinsock"/>) or the real network's. The engine without one
/// is a machine whose LoadLibrary ("wsock32.dll") fails.
/// </summary>
public interface IWinsock
{
    int WSAStartup(ushort wVersionRequired);

    int WSACleanup();

    int WSAGetLastError();

    int socket(int af, int type, int protocol);

    int ioctlsocket(int s, int cmd, ref uint argp);

    int setsockopt(int s, int level, int optname, int optval);

    int bind(int s, in QSockAddr name);

    /// <summary>recvfrom with an address to fill (from), or none (fromNull: the C's NULL, NULL).</summary>
    int recvfrom(int s, byte[] buf, int len, int flags, ref QSockAddr from, bool fromNull = false);

    int sendto(int s, byte[] buf, int len, int flags, in QSockAddr to);

    int closesocket(int s);

    /// <summary>gethostname: the machine's name, SOCKET_ERROR when it does not fit namelen.</summary>
    int gethostname(out string name, int namelen);

    HostEnt? gethostbyname(string name);

    /// <summary>gethostbyaddr for an AF_INET address (its bytes in memory order).</summary>
    HostEnt? gethostbyaddr(uint addr);

    int getsockname(int s, ref QSockAddr name);
}
