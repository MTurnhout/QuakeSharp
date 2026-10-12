using System.Buffers.Binary;
using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Net;

/// <summary>
/// The virtual TCP/IP driver (design.md decision 33), as the oracle's tools/oracle/src/net_oracle.c: net_wins.c with
/// the sockets taken out. TCP/IP is available at a fixed address (127.0.0.1 once listening), the driver prints what
/// net_wins.c prints, no name or address resolves (so nothing waits for an answer that cannot come) and no packet ever
/// arrives; what is written goes nowhere. An address is a sockaddr_in: the family, then the port and the address in
/// network byte order.
/// </summary>
public sealed class VirtualLanDriver : NetLanDriver
{
    private const short AF_INET = 2;
    private const uint INADDR_ANY = 0x00000000;
    private const uint INADDR_BROADCAST = 0xffffffff;

    /// <summary>The address the machine has: 127.0.0.1.</summary>
    private const uint LOOPBACK = 0x7f000001;

    private readonly QuakeEngine engine;

    private int net_acceptsocket = -1; // socket for fielding new connections
    private int net_controlsocket;
    private int net_broadcastsocket;
    private QSockAddr broadcastaddr;

    /// <summary>The machine's address, in network byte order as the C keeps it (its bytes in memory order).</summary>
    private uint myAddr;

    /// <summary>The sockets' numbers, as the system gives them.</summary>
    private int nextsocket = 1;

    public VirtualLanDriver(QuakeEngine engine)
        : base("Winsock TCPIP")
    {
        this.engine = engine;
    }

    // the sockaddr_in fields: sin_port at sa_data[0], sin_addr at sa_data[2] (network byte order: read here as the
    // C reads them on x86, the bytes in memory order)
    private static ushort Port(in QSockAddr a) => BinaryPrimitives.ReadUInt16LittleEndian(a.sa_data[0..2]);

    private static void SetPort(ref QSockAddr a, ushort port) => BinaryPrimitives.WriteUInt16LittleEndian(a.sa_data[0..2], port);

    private static uint Addr(in QSockAddr a) => BinaryPrimitives.ReadUInt32LittleEndian(a.sa_data[2..6]);

    private static void SetAddr(ref QSockAddr a, uint addr) => BinaryPrimitives.WriteUInt32LittleEndian(a.sa_data[2..6], addr);

    private static ushort htons(ushort v) => BinaryPrimitives.ReverseEndianness(v);

    private static uint htonl(uint v) => BinaryPrimitives.ReverseEndianness(v);

    private static ushort ntohs(ushort v) => BinaryPrimitives.ReverseEndianness(v);

    private static uint ntohl(uint v) => BinaryPrimitives.ReverseEndianness(v);

    // net_wins.c: WINS_GetLocalAddress (the machine's address: 127.0.0.1)
    private void GetLocalAddress()
    {
        uint addr;

        if (myAddr != INADDR_ANY)
        {
            return;
        }

        myAddr = htonl(LOOPBACK);

        addr = ntohl(myAddr);
        engine.my_tcpip_address = CFormat.Sprintf("%d.%d.%d.%d", (int)((addr >> 24) & 0xff), (int)((addr >> 16) & 0xff), (int)((addr >> 8) & 0xff), (int)(addr & 0xff));
    }

    // net_wins.c: WINS_Init (the machine has no name of its own: hostname is left as it is)
    public override int Init()
    {
        int i;

        if (engine.COM_CheckParm("-noudp") != 0)
        {
            return -1;
        }

        i = engine.COM_CheckParm("-ip");
        if (i != 0)
        {
            if (i < engine.com_argv.Argc - 1)
            {
                string ip = engine.com_argv.Argv(i + 1);
                string[] parts = ip.Split('.');
                if (parts.Length < 4 || !TryLeadingInt(parts[0], out int ha1) || !TryLeadingInt(parts[1], out int ha2)
                    || !TryLeadingInt(parts[2], out int ha3) || !TryLeadingInt(parts[3], out int ha4))
                {
                    throw QuakeEngine.Sys_Error("%s is not a valid IP address", ip);
                }

                myAddr = htonl((uint)((ha1 << 24) | (ha2 << 16) | (ha3 << 8) | ha4));
                engine.my_tcpip_address = ip;
            }
            else
            {
                throw QuakeEngine.Sys_Error("NET_Init: you must specify an IP address after -ip");
            }
        }
        else
        {
            myAddr = INADDR_ANY;
            engine.my_tcpip_address = "INADDR_ANY";
        }

        if ((net_controlsocket = OpenSocket(0)) == -1)
        {
            engine.Con_Printf("WINS_Init: Unable to open control socket\n");
            return -1;
        }

        broadcastaddr.sa_family = AF_INET;
        SetAddr(ref broadcastaddr, INADDR_BROADCAST);
        SetPort(ref broadcastaddr, htons((ushort)engine.net_hostport));

        engine.Con_Printf("Winsock TCP/IP Initialized\n");
        engine.tcpipAvailable = true;

        return net_controlsocket;
    }

    // sscanf's %d: an optional sign and digits at the start, the rest ignored
    private static bool TryLeadingInt(string s, out int value)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
        {
            i++;
        }

        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            i++;
        }

        int digits = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
        }

        value = 0;
        return i > digits && int.TryParse(s.AsSpan(start, i - start), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    // net_wins.c: WINS_Shutdown
    public override void Shutdown()
    {
        Listen(false);
        CloseSocket(net_controlsocket);
    }

    // net_wins.c: WINS_Listen
    public override void Listen(bool state)
    {
        // enable listening
        if (state)
        {
            if (net_acceptsocket != -1)
            {
                return;
            }

            GetLocalAddress();
            if ((net_acceptsocket = OpenSocket(engine.net_hostport)) == -1)
            {
                throw QuakeEngine.Sys_Error("WINS_Listen: Unable to open accept socket\n");
            }

            return;
        }

        // disable listening
        if (net_acceptsocket == -1)
        {
            return;
        }

        CloseSocket(net_acceptsocket);
        net_acceptsocket = -1;
    }

    // net_wins.c: WINS_OpenSocket (a socket is a number; binding never fails)
    public override int OpenSocket(int port) => nextsocket++;

    // net_wins.c: WINS_CloseSocket
    public override int CloseSocket(int socket)
    {
        if (socket == net_broadcastsocket)
        {
            net_broadcastsocket = 0;
        }

        return 0;
    }

    // net_wins.c: WINS_Connect
    public override int Connect(int socket, in QSockAddr addr) => 0;

    // net_wins.c: WINS_CheckNewConnections (nothing ever arrives)
    public override int CheckNewConnections() => -1;

    // net_wins.c: WINS_Read (nothing ever arrives)
    public override int Read(int socket, byte[] buf, int len, ref QSockAddr addr) => 0;

    // net_wins.c: WINS_MakeSocketBroadcastCapable
    private int MakeSocketBroadcastCapable(int socket)
    {
        net_broadcastsocket = socket;
        return 0;
    }

    // net_wins.c: WINS_Broadcast
    public override int Broadcast(int socket, byte[] buf, int len)
    {
        int ret;

        if (socket != net_broadcastsocket)
        {
            if (net_broadcastsocket != 0)
            {
                throw QuakeEngine.Sys_Error("Attempted to use multiple broadcasts sockets\n");
            }

            GetLocalAddress();
            ret = MakeSocketBroadcastCapable(socket);
            if (ret == -1)
            {
                engine.Con_Printf("Unable to make socket broadcast capable\n");
                return ret;
            }
        }

        return Write(socket, buf, len, broadcastaddr);
    }

    // net_wins.c: WINS_Write (sent, to nowhere)
    public override int Write(int socket, byte[] buf, int len, in QSockAddr addr) => len;

    // net_wins.c: WINS_AddrToString
    public override string AddrToString(in QSockAddr addr)
    {
        int haddr = (int)ntohl(Addr(addr));
        return CFormat.Sprintf("%d.%d.%d.%d:%d", (haddr >> 24) & 0xff, (haddr >> 16) & 0xff, (haddr >> 8) & 0xff, haddr & 0xff, (int)ntohs(Port(addr)));
    }

    // net_wins.c: WINS_StringToAddr (what sscanf does not reach is 0, rather than the C's stack)
    public override int StringToAddr(string @string, ref QSockAddr addr)
    {
        int[] h = new int[5];
        int i = 0;
        char[] separators = ['.', '.', '.', ':'];
        int pos = 0;

        for (i = 0; i < 5; i++)
        {
            int end = pos;
            while (end < @string.Length && (char.IsAsciiDigit(@string[end]) || (end == pos && (@string[end] == '-' || @string[end] == '+'))))
            {
                end++;
            }

            if (!TryLeadingInt(@string[pos..end], out h[i]))
            {
                break;
            }

            if (i == 4 || end >= @string.Length || @string[end] != separators[i])
            {
                break;
            }

            pos = end + 1;
        }

        int ipaddr = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];

        addr.sa_family = AF_INET;
        SetAddr(ref addr, htonl((uint)ipaddr));
        SetPort(ref addr, htons((ushort)h[4]));
        return 0;
    }

    // net_wins.c: WINS_GetSocketAddr (every socket is bound to the machine's address, port 0)
    public override int GetSocketAddr(int socket, ref QSockAddr addr)
    {
        addr = default;
        addr.sa_family = AF_INET;
        SetAddr(ref addr, myAddr);
        return 0;
    }

    // net_wins.c: WINS_GetNameFromAddr (no name is known)
    public override int GetNameFromAddr(in QSockAddr addr, out string name)
    {
        name = AddrToString(addr);
        return 0;
    }

    // net_wins.c: WINS_GetAddrFromName (nothing resolves, not even a number: a connection would wait for an answer)
    public override int GetAddrFromName(string name, ref QSockAddr addr) => -1;

    // net_wins.c: WINS_AddrCompare
    public override int AddrCompare(in QSockAddr addr1, in QSockAddr addr2)
    {
        if (addr1.sa_family != addr2.sa_family)
        {
            return -1;
        }

        if (Addr(addr1) != Addr(addr2))
        {
            return -1;
        }

        if (Port(addr1) != Port(addr2))
        {
            return 1;
        }

        return 0;
    }

    // net_wins.c: WINS_GetSocketPort
    public override int GetSocketPort(in QSockAddr addr) => ntohs(Port(addr));

    // net_wins.c: WINS_SetSocketPort
    public override int SetSocketPort(ref QSockAddr addr, int port)
    {
        SetPort(ref addr, htons((ushort)port));
        return 0;
    }
}
