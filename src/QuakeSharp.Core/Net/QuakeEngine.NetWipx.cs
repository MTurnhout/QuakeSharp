using System.Buffers.Binary;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Net;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Net.NetDef;
using static QuakeSharp.Core.Net.Winsock;

namespace QuakeSharp.Core;

// net_wipx.c: the IPX LAN driver over Winsock. Today's Windows has no IPX, so its control socket cannot be opened and
// it stays down (design.md §3.4); the rest is ported as the C has it. Its statics that net_wins.c also has are named
// wipx_*. An IPX address in a qsockaddr is a sockaddr_ipx: the network number at sa_data[0], the node at sa_data[4],
// the socket at sa_data[10] (network byte order).
public sealed partial class QuakeEngine
{
    private const int IPXSOCKETS = 18;

    private int wipx_acceptsocket = -1; // socket for fielding new connections
    private int wipx_controlsocket;
    private QSockAddr wipx_broadcastaddr;

    private readonly int[] ipxsocket = new int[IPXSOCKETS];
    private readonly int[] wipx_sequence = new int[IPXSOCKETS];

    // net_wipx.c: packetBuffer, a datagram with DOS IPX's sequence number before it
    private readonly byte[] wipx_packetBuffer = new byte[NET_DATAGRAMSIZE + 4];

    private static Span<byte> SaNetnum(ref QSockAddr a) => a.sa_data[0..4];

    private static Span<byte> SaNodenum(ref QSockAddr a) => a.sa_data[4..10];

    private static ushort SaSocket(in QSockAddr a) => BinaryPrimitives.ReadUInt16LittleEndian(a.sa_data[10..12]);

    private static void SetSaSocket(ref QSockAddr a, ushort socket) => BinaryPrimitives.WriteUInt16LittleEndian(a.sa_data[10..12], socket);

    // net_wipx.c: WIPX_Init
    private int WIPX_Init()
    {
        int i;
        QSockAddr addr = default;
        int r;

        if (COM_CheckParm("-noipx") != 0)
        {
            return -1;
        }

        // make sure LoadLibrary has happened successfully
        if (!winsock_lib_initialized)
        {
            return -1;
        }

        if (winsock_initialized == 0)
        {
            r = winsock!.WSAStartup(0x0101);

            if (r != 0)
            {
                Con_Printf("Winsock initialization failed.\n");
                return -1;
            }
        }

        winsock_initialized++;

        for (i = 0; i < IPXSOCKETS; i++)
        {
            ipxsocket[i] = 0;
        }

        // determine my name & address
        if (winsock!.gethostname(out string buff, MAXHOSTNAMELEN) == 0)
        {
            // if the quake hostname isn't set, set it to the machine name
            if (Q_strcmp(hostname.@string, "UNNAMED") == 0)
            {
                Cvar_Set("hostname", MachineName(buff));
            }
        }

        if ((wipx_controlsocket = WIPX_OpenSocket(0)) == -1)
        {
            Con_Printf("WIPX_Init: Unable to open control socket\n");
            if (--winsock_initialized == 0)
            {
                winsock.WSACleanup();
            }

            return -1;
        }

        wipx_broadcastaddr = default;
        wipx_broadcastaddr.sa_family = AF_IPX;
        SaNetnum(ref wipx_broadcastaddr).Clear();
        SaNodenum(ref wipx_broadcastaddr).Fill(0xff);
        SetSaSocket(ref wipx_broadcastaddr, htons((ushort)net_hostport));

        WIPX_GetSocketAddr(wipx_controlsocket, ref addr);
        my_ipx_address = WIPX_AddrToString(addr);
        int p = my_ipx_address.LastIndexOf(':');
        if (p >= 0)
        {
            my_ipx_address = my_ipx_address[..p];
        }

        Con_Printf("Winsock IPX Initialized\n");
        ipxAvailable = true;

        return wipx_controlsocket;
    }

    // net_wipx.c: WIPX_Shutdown
    private void WIPX_Shutdown()
    {
        WIPX_Listen(false);
        WIPX_CloseSocket(wipx_controlsocket);
        if (--winsock_initialized == 0)
        {
            winsock!.WSACleanup();
        }
    }

    // net_wipx.c: WIPX_Listen
    private void WIPX_Listen(bool state)
    {
        // enable listening
        if (state)
        {
            if (wipx_acceptsocket != -1)
            {
                return;
            }

            if ((wipx_acceptsocket = WIPX_OpenSocket(net_hostport)) == -1)
            {
                throw Sys_Error("WIPX_Listen: Unable to open accept socket\n");
            }

            return;
        }

        // disable listening
        if (wipx_acceptsocket == -1)
        {
            return;
        }

        WIPX_CloseSocket(wipx_acceptsocket);
        wipx_acceptsocket = -1;
    }

    // net_wipx.c: WIPX_OpenSocket
    private int WIPX_OpenSocket(int port)
    {
        int handle;
        int newsocket;
        QSockAddr address;
        uint _true = 1;

        for (handle = 0; handle < IPXSOCKETS; handle++)
        {
            if (ipxsocket[handle] == 0)
            {
                break;
            }
        }

        if (handle == IPXSOCKETS)
        {
            return -1;
        }

        if ((newsocket = winsock!.socket(AF_IPX, SOCK_DGRAM, NSPROTO_IPX)) == INVALID_SOCKET)
        {
            return -1;
        }

        if (winsock.ioctlsocket(newsocket, FIONBIO, ref _true) == -1)
        {
            goto ErrorReturn;
        }

        if (winsock.setsockopt(newsocket, SOL_SOCKET, SO_BROADCAST, (int)_true) < 0)
        {
            goto ErrorReturn;
        }

        address = default;
        address.sa_family = AF_IPX;
        SaNetnum(ref address).Clear();
        SaNodenum(ref address).Clear();
        SetSaSocket(ref address, htons((ushort)port));
        if (winsock.bind(newsocket, address) == 0)
        {
            ipxsocket[handle] = newsocket;
            wipx_sequence[handle] = 0;
            return handle;
        }

        throw Sys_Error("Winsock IPX bind failed\n");
    ErrorReturn:
        winsock.closesocket(newsocket);
        return -1;
    }

    // net_wipx.c: WIPX_CloseSocket
    private int WIPX_CloseSocket(int handle)
    {
        int socket = ipxsocket[handle];
        int ret;

        ret = winsock!.closesocket(socket);
        ipxsocket[handle] = 0;
        return ret;
    }

    // net_wipx.c: WIPX_Connect
    private int WIPX_Connect(int handle, in QSockAddr addr) => 0;

    // net_wipx.c: WIPX_CheckNewConnections
    private int WIPX_CheckNewConnections()
    {
        uint available = 0;

        if (wipx_acceptsocket == -1)
        {
            return -1;
        }

        if (winsock!.ioctlsocket(ipxsocket[wipx_acceptsocket], FIONREAD, ref available) == -1)
        {
            throw Sys_Error("WIPX: ioctlsocket (FIONREAD) failed\n");
        }

        if (available != 0)
        {
            return wipx_acceptsocket;
        }

        return -1;
    }

    // net_wipx.c: WIPX_Read
    private int WIPX_Read(int handle, byte[] buf, int len, ref QSockAddr addr)
    {
        int socket = ipxsocket[handle];
        int ret;

        ret = winsock!.recvfrom(socket, wipx_packetBuffer, len + 4, 0, ref addr);
        if (ret == -1)
        {
            int errno = winsock.WSAGetLastError();

            if (errno == WSAEWOULDBLOCK || errno == WSAECONNREFUSED)
            {
                return 0;
            }
        }

        if (ret < 4)
        {
            return 0;
        }

        // remove sequence number, it's only needed for DOS IPX
        ret -= 4;
        wipx_packetBuffer.AsSpan(4, ret).CopyTo(buf);

        return ret;
    }

    // net_wipx.c: WIPX_Broadcast
    private int WIPX_Broadcast(int handle, byte[] buf, int len) => WIPX_Write(handle, buf, len, wipx_broadcastaddr);

    // net_wipx.c: WIPX_Write
    private int WIPX_Write(int handle, byte[] buf, int len, in QSockAddr addr)
    {
        int socket = ipxsocket[handle];
        int ret;

        // build packet with sequence number
        BinaryPrimitives.WriteInt32LittleEndian(wipx_packetBuffer, wipx_sequence[handle]);
        wipx_sequence[handle]++;
        buf.AsSpan(0, len).CopyTo(wipx_packetBuffer.AsSpan(4));
        len += 4;

        ret = winsock!.sendto(socket, wipx_packetBuffer, len, 0, addr);
        if (ret == -1)
        {
            if (winsock.WSAGetLastError() == WSAEWOULDBLOCK)
            {
                return 0;
            }
        }

        return ret;
    }

    // net_wipx.c: WIPX_AddrToString
    private string WIPX_AddrToString(in QSockAddr addr)
    {
        QSockAddr a = addr;
        Span<byte> net = SaNetnum(ref a);
        Span<byte> node = SaNodenum(ref a);

        return CFormat.Sprintf("%02x%02x%02x%02x:%02x%02x%02x%02x%02x%02x:%u",
            net[0] & 0xff, net[1] & 0xff, net[2] & 0xff, net[3] & 0xff, node[0] & 0xff, node[1] & 0xff, node[2] & 0xff,
            node[3] & 0xff, node[4] & 0xff, node[5] & 0xff, (uint)ntohs(SaSocket(a)));
    }

    // net_wipx.c: WIPX_StringToAddr
    private int WIPX_StringToAddr(string @string, ref QSockAddr addr)
    {
        addr = default;
        addr.sa_family = AF_IPX;

        // DO(src, dest): the two characters at src as "%x"
        int[] sources = [0, 2, 4, 6, 9, 11, 13, 15, 17, 19];
        for (int i = 0; i < sources.Length; i++)
        {
            string buf = Chars(@string, sources[i], 2);
            if (!ScanHex(buf, out int val))
            {
                return -1;
            }

            addr.sa_data[i] = (byte)val;
        }

        // sscanf (&string[22], "%u", &val): val keeps what the last DO left when nothing is read
        if (@string.Length > 22 && ScanUnsigned(@string[22..], out uint port))
        {
            SetSaSocket(ref addr, htons((ushort)port));
        }
        else
        {
            SetSaSocket(ref addr, htons(addr.sa_data[9]));
        }

        return 0;

        static string Chars(string s, int at, int count) => at >= s.Length ? "" : s.Substring(at, Math.Min(count, s.Length - at));
    }

    // sscanf's %x on two characters: white space, a sign, hexadecimal digits (an optional 0x)
    private static bool ScanHex(string s, out int value)
    {
        value = 0;
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        bool negative = i < s.Length && s[i] == '-';
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            i++;
        }

        int start = i;
        while (i < s.Length && char.IsAsciiHexDigit(s[i]))
        {
            value = (value * 16) + Convert.ToInt32(s[i].ToString(), 16);
            i++;
        }

        if (i == start)
        {
            return false;
        }

        value = negative ? -value : value;
        return true;
    }

    // sscanf's %u: white space, digits
    private static bool ScanUnsigned(string s, out uint value)
    {
        value = 0;
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        int start = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            value = unchecked((value * 10) + (uint)(s[i] - '0'));
            i++;
        }

        return i > start;
    }

    // net_wipx.c: WIPX_GetSocketAddr
    private int WIPX_GetSocketAddr(int handle, ref QSockAddr addr)
    {
        int socket = ipxsocket[handle];

        addr = default;
        if (winsock!.getsockname(socket, ref addr) != 0)
        {
            _ = winsock.WSAGetLastError();
        }

        return 0;
    }

    // net_wipx.c: WIPX_GetNameFromAddr
    private int WIPX_GetNameFromAddr(in QSockAddr addr, out string name)
    {
        name = WIPX_AddrToString(addr);
        return 0;
    }

    // net_wipx.c: WIPX_GetAddrFromName
    private int WIPX_GetAddrFromName(string name, ref QSockAddr addr)
    {
        int n;
        string buf;

        n = name.Length;

        if (n == 12)
        {
            buf = CFormat.Sprintf("00000000:%s:%u", name, (uint)net_hostport);
            return WIPX_StringToAddr(buf, ref addr);
        }

        if (n == 21)
        {
            buf = CFormat.Sprintf("%s:%u", name, (uint)net_hostport);
            return WIPX_StringToAddr(buf, ref addr);
        }

        if (n > 21 && n <= 27)
        {
            return WIPX_StringToAddr(name, ref addr);
        }

        return -1;
    }

    // net_wipx.c: WIPX_AddrCompare
    private int WIPX_AddrCompare(in QSockAddr addr1, in QSockAddr addr2)
    {
        QSockAddr a1 = addr1;
        QSockAddr a2 = addr2;

        if (a1.sa_family != a2.sa_family)
        {
            return -1;
        }

        if (SaNetnum(ref a1)[0] != 0 && SaNetnum(ref a2)[0] != 0)
        {
            if (!SaNetnum(ref a1).SequenceEqual(SaNetnum(ref a2)))
            {
                return -1;
            }
        }

        if (!SaNodenum(ref a1).SequenceEqual(SaNodenum(ref a2)))
        {
            return -1;
        }

        if (SaSocket(a1) != SaSocket(a2))
        {
            return 1;
        }

        return 0;
    }

    // net_wipx.c: WIPX_GetSocketPort
    private int WIPX_GetSocketPort(in QSockAddr addr) => ntohs(SaSocket(addr));

    // net_wipx.c: WIPX_SetSocketPort
    private int WIPX_SetSocketPort(ref QSockAddr addr, int port)
    {
        SetSaSocket(ref addr, htons((ushort)port));
        return 0;
    }
}
