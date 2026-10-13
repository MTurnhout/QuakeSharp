using QuakeSharp.Core.Common;
using QuakeSharp.Core.Net;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Net.NetDef;
using static QuakeSharp.Core.Net.Winsock;

namespace QuakeSharp.Core;

// net_wins.c: the TCP/IP LAN driver over Winsock (design.md §11.5): the engine's IWinsock is the library
// LoadLibrary ("wsock32.dll") loads, null on a machine without one. Its statics that net_wipx.c also has are named
// wins_*.
public sealed partial class QuakeEngine
{
    private const int MAXHOSTNAMELEN = 256;

    private int wins_acceptsocket = -1; // socket for fielding new connections
    private int wins_controlsocket;
    private int net_broadcastsocket;
    private QSockAddr wins_broadcastaddr;

    private uint myAddr;

    // net_wins.c: winsock_lib_initialized, winsock_initialized (net_wipx.c shares them)
    private bool winsock_lib_initialized;
    private int winsock_initialized;

    private double blocktime;

    /// <summary>The library LoadLibrary ("wsock32.dll") loads: the virtual machine's or the real network's.</summary>
    public IWinsock? WinsockLibrary => winsock;

    // net_wins.c: WINS_GetLocalAddress. gethostbyname runs with BlockingHook installed, which pumps the window's
    // messages and gives up after 2 s; nothing here blocks, so it is never called (the oracle's WSASetBlockingHook
    // installs nothing either), but its clock is read as the C reads it.
    private void WINS_GetLocalAddress()
    {
        HostEnt? local;
        uint addr;

        if (myAddr != INADDR_ANY)
        {
            return;
        }

        if (winsock!.gethostname(out string buff, MAXHOSTNAMELEN) == SOCKET_ERROR)
        {
            return;
        }

        blocktime = Sys_FloatTime();
        local = winsock.gethostbyname(buff);
        if (local == null)
        {
            return;
        }

        myAddr = local.h_addr_list[0];

        addr = ntohl(myAddr);
        my_tcpip_address = CFormat.Sprintf("%d.%d.%d.%d", (int)((addr >> 24) & 0xff), (int)((addr >> 16) & 0xff), (int)((addr >> 8) & 0xff), (int)(addr & 0xff));
    }

    // net_wins.c: WINS_Init
    private int WINS_Init()
    {
        int i;
        int r;

        // initialize the Winsock function vectors (we do this instead of statically linking
        // so we can run on Win 3.1, where there isn't necessarily Winsock)
        if (winsock == null)
        {
            Con_SafePrintf("Failed to load winsock.dll\n");
            winsock_lib_initialized = false;
            return -1;
        }

        winsock_lib_initialized = true;

        if (COM_CheckParm("-noudp") != 0)
        {
            return -1;
        }

        if (winsock_initialized == 0)
        {
            r = winsock.WSAStartup(0x0101);

            if (r != 0)
            {
                Con_SafePrintf("Winsock initialization failed.\n");
                return -1;
            }
        }

        winsock_initialized++;

        // determine my name
        if (winsock.gethostname(out string buff, MAXHOSTNAMELEN) == SOCKET_ERROR)
        {
            Con_DPrintf("Winsock TCP/IP Initialization failed.\n");
            if (--winsock_initialized == 0)
            {
                winsock.WSACleanup();
            }

            return -1;
        }

        // if the quake hostname isn't set, set it to the machine name
        if (Q_strcmp(hostname.@string, "UNNAMED") == 0)
        {
            Cvar_Set("hostname", MachineName(buff));
        }

        i = COM_CheckParm("-ip");
        if (i != 0)
        {
            if (i < com_argv.Argc - 1)
            {
                myAddr = inet_addr(com_argv.Argv(i + 1));
                if (myAddr == INADDR_NONE)
                {
                    throw Sys_Error("%s is not a valid IP address", com_argv.Argv(i + 1));
                }

                my_tcpip_address = com_argv.Argv(i + 1);
            }
            else
            {
                throw Sys_Error("NET_Init: you must specify an IP address after -ip");
            }
        }
        else
        {
            myAddr = INADDR_ANY;
            my_tcpip_address = "INADDR_ANY";
        }

        if ((wins_controlsocket = WINS_OpenSocket(0)) == -1)
        {
            Con_Printf("WINS_Init: Unable to open control socket\n");
            if (--winsock_initialized == 0)
            {
                winsock.WSACleanup();
            }

            return -1;
        }

        wins_broadcastaddr = SockAddrIn(INADDR_BROADCAST, htons((ushort)net_hostport));

        Con_Printf("Winsock TCP/IP Initialized\n");
        tcpipAvailable = true;

        return wins_controlsocket;
    }

    // net_wins.c and net_wipx.c: the machine's name as hostname takes it. A text IP address is kept; of a real name
    // only the host stays: up to its first '.' among the first 15 characters, and no more than 15
    private static string MachineName(string buff)
    {
        int p;
        int i;

        // see if it's a text IP address (well, close enough)
        for (p = 0; p < buff.Length; p++)
        {
            if ((buff[p] < '0' || buff[p] > '9') && buff[p] != '.')
            {
                break;
            }
        }

        // if it is a real name, strip off the domain; we only want the host
        if (p < buff.Length)
        {
            for (i = 0; i < 15 && i < buff.Length; i++)
            {
                if (buff[i] == '.')
                {
                    break;
                }
            }

            buff = buff[..i];
        }

        return buff;
    }

    // net_wins.c: WINS_Shutdown
    private void WINS_Shutdown()
    {
        WINS_Listen(false);
        WINS_CloseSocket(wins_controlsocket);
        if (--winsock_initialized == 0)
        {
            winsock!.WSACleanup();
        }
    }

    // net_wins.c: WINS_Listen
    private void WINS_Listen(bool state)
    {
        // enable listening
        if (state)
        {
            if (wins_acceptsocket != -1)
            {
                return;
            }

            WINS_GetLocalAddress();
            if ((wins_acceptsocket = WINS_OpenSocket(net_hostport)) == -1)
            {
                throw Sys_Error("WINS_Listen: Unable to open accept socket\n");
            }

            return;
        }

        // disable listening
        if (wins_acceptsocket == -1)
        {
            return;
        }

        WINS_CloseSocket(wins_acceptsocket);
        wins_acceptsocket = -1;
    }

    // net_wins.c: WINS_OpenSocket
    private int WINS_OpenSocket(int port)
    {
        int newsocket;
        QSockAddr address;
        uint _true = 1;

        if ((newsocket = winsock!.socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP)) == -1)
        {
            return -1;
        }

        if (winsock.ioctlsocket(newsocket, FIONBIO, ref _true) == -1)
        {
            goto ErrorReturn;
        }

        address = SockAddrIn(myAddr, htons((ushort)port));
        if (winsock.bind(newsocket, address) == 0)
        {
            return newsocket;
        }

        throw Sys_Error("Unable to bind to %s", WINS_AddrToString(address));
    ErrorReturn:
        winsock.closesocket(newsocket);
        return -1;
    }

    // net_wins.c: WINS_CloseSocket
    private int WINS_CloseSocket(int socket)
    {
        if (socket == net_broadcastsocket)
        {
            net_broadcastsocket = 0;
        }

        return winsock!.closesocket(socket);
    }

    // net_wins.c: PartialIPAddress: this lets you type only as much of the net address as required, using the local
    // network components to fill in the rest
    private int PartialIPAddress(string @in, ref QSockAddr hostaddr)
    {
        string buff;
        int b;
        int addr;
        int num;
        int mask;
        int run;
        int port;

        // buff holds '.' and the text, read as C reads it: past its end is the 0 that ends it
        buff = "." + @in;
        b = 0;
        if (At(buff, 1) == '.')
        {
            b++;
        }

        addr = 0;
        mask = -1;
        while (At(buff, b) == '.')
        {
            b++;
            num = 0;
            run = 0;
            while (!(At(buff, b) < '0' || At(buff, b) > '9'))
            {
                num = (num * 10) + At(buff, b++) - '0';
                if (++run > 3)
                {
                    return -1;
                }
            }

            if ((At(buff, b) < '0' || At(buff, b) > '9') && At(buff, b) != '.' && At(buff, b) != ':' && At(buff, b) != 0)
            {
                return -1;
            }

            if (num < 0 || num > 255)
            {
                return -1;
            }

            mask <<= 8;
            addr = (addr << 8) + num;
        }

        if (At(buff, b++) == ':')
        {
            port = Q_atoi(b < buff.Length ? buff[b..] : "");
        }
        else
        {
            port = net_hostport;
        }

        hostaddr.sa_family = AF_INET;
        SetSinPort(ref hostaddr, htons((ushort)(short)port));
        SetSinAddr(ref hostaddr, (myAddr & htonl((uint)mask)) | htonl((uint)addr));

        return 0;

        static char At(string s, int i) => i < s.Length ? s[i] : '\0';
    }

    // net_wins.c: WINS_Connect
    private int WINS_Connect(int socket, in QSockAddr addr) => 0;

    // net_wins.c: WINS_CheckNewConnections
    private int WINS_CheckNewConnections()
    {
        byte[] buf = new byte[4096];
        QSockAddr unused = default;

        if (wins_acceptsocket == -1)
        {
            return -1;
        }

        if (winsock!.recvfrom(wins_acceptsocket, buf, buf.Length, MSG_PEEK, ref unused, fromNull: true) > 0)
        {
            return wins_acceptsocket;
        }

        return -1;
    }

    // net_wins.c: WINS_Read
    private int WINS_Read(int socket, byte[] buf, int len, ref QSockAddr addr)
    {
        int ret;

        ret = winsock!.recvfrom(socket, buf, len, 0, ref addr);
        if (ret == -1)
        {
            int errno = winsock.WSAGetLastError();

            if (errno == WSAEWOULDBLOCK || errno == WSAECONNREFUSED)
            {
                return 0;
            }
        }

        return ret;
    }

    // net_wins.c: WINS_MakeSocketBroadcastCapable
    private int WINS_MakeSocketBroadcastCapable(int socket)
    {
        int i = 1;

        // make this socket broadcast capable
        if (winsock!.setsockopt(socket, SOL_SOCKET, SO_BROADCAST, i) < 0)
        {
            return -1;
        }

        net_broadcastsocket = socket;

        return 0;
    }

    // net_wins.c: WINS_Broadcast
    private int WINS_Broadcast(int socket, byte[] buf, int len)
    {
        int ret;

        if (socket != net_broadcastsocket)
        {
            if (net_broadcastsocket != 0)
            {
                throw Sys_Error("Attempted to use multiple broadcasts sockets\n");
            }

            WINS_GetLocalAddress();
            ret = WINS_MakeSocketBroadcastCapable(socket);
            if (ret == -1)
            {
                Con_Printf("Unable to make socket broadcast capable\n");
                return ret;
            }
        }

        return WINS_Write(socket, buf, len, wins_broadcastaddr);
    }

    // net_wins.c: WINS_Write
    private int WINS_Write(int socket, byte[] buf, int len, in QSockAddr addr)
    {
        int ret;

        ret = winsock!.sendto(socket, buf, len, 0, addr);
        if (ret == -1)
        {
            if (winsock.WSAGetLastError() == WSAEWOULDBLOCK)
            {
                return 0;
            }
        }

        return ret;
    }

    // net_wins.c: WINS_AddrToString
    private string WINS_AddrToString(in QSockAddr addr)
    {
        int haddr;

        haddr = (int)ntohl(SinAddr(addr));
        return CFormat.Sprintf("%d.%d.%d.%d:%d", (haddr >> 24) & 0xff, (haddr >> 16) & 0xff, (haddr >> 8) & 0xff, haddr & 0xff, (int)ntohs(SinPort(addr)));
    }

    // net_wins.c: WINS_StringToAddr (sscanf's "%d.%d.%d.%d:%d"; what it does not reach stays 0 here, the C's stack
    // there: net_dgrm.c never calls it)
    private int WINS_StringToAddr(string @string, ref QSockAddr addr)
    {
        int[] h = new int[5];
        int ipaddr;
        int at = 0;
        char[] separators = ['.', '.', '.', ':'];

        for (int f = 0; f < 5; f++)
        {
            if (f > 0)
            {
                if (at >= @string.Length || @string[at] != separators[f - 1])
                {
                    break;
                }

                at++;
            }

            if (!ScanInt(@string, ref at, out h[f]))
            {
                break;
            }
        }

        ipaddr = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];

        addr.sa_family = AF_INET;
        SetSinAddr(ref addr, htonl((uint)ipaddr));
        SetSinPort(ref addr, htons((ushort)h[4]));
        return 0;
    }

    // sscanf's %d: white space, a sign, digits
    private static bool ScanInt(string s, ref int at, out int value)
    {
        int i = at;
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
        value = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            value = (value * 10) + (s[i++] - '0');
        }

        if (i == start)
        {
            return false;
        }

        value = negative ? -value : value;
        at = i;
        return true;
    }

    // net_wins.c: WINS_GetSocketAddr
    private int WINS_GetSocketAddr(int socket, ref QSockAddr addr)
    {
        uint a;

        addr = default;
        winsock!.getsockname(socket, ref addr);
        a = SinAddr(addr);
        if (a == 0 || a == inet_addr("127.0.0.1"))
        {
            SetSinAddr(ref addr, myAddr);
        }

        return 0;
    }

    // net_wins.c: WINS_GetNameFromAddr
    private int WINS_GetNameFromAddr(in QSockAddr addr, out string name)
    {
        HostEnt? hostentry;

        hostentry = winsock!.gethostbyaddr(SinAddr(addr));
        if (hostentry != null)
        {
            // Q_strncpy (name, hostentry->h_name, NET_NAMELEN - 1)
            name = hostentry.h_name.Length > NET_NAMELEN - 1 ? hostentry.h_name[..(NET_NAMELEN - 1)] : hostentry.h_name;
            return 0;
        }

        name = WINS_AddrToString(addr);
        return 0;
    }

    // net_wins.c: WINS_GetAddrFromName
    private int WINS_GetAddrFromName(string name, ref QSockAddr addr)
    {
        HostEnt? hostentry;

        if (name.Length > 0 && name[0] >= '0' && name[0] <= '9')
        {
            return PartialIPAddress(name, ref addr);
        }

        hostentry = winsock!.gethostbyname(name);
        if (hostentry == null)
        {
            return -1;
        }

        addr.sa_family = AF_INET;
        SetSinPort(ref addr, htons((ushort)net_hostport));
        SetSinAddr(ref addr, hostentry.h_addr_list[0]);

        return 0;
    }

    // net_wins.c: WINS_AddrCompare
    private int WINS_AddrCompare(in QSockAddr addr1, in QSockAddr addr2)
    {
        if (addr1.sa_family != addr2.sa_family)
        {
            return -1;
        }

        if (SinAddr(addr1) != SinAddr(addr2))
        {
            return -1;
        }

        if (SinPort(addr1) != SinPort(addr2))
        {
            return 1;
        }

        return 0;
    }

    // net_wins.c: WINS_GetSocketPort
    private int WINS_GetSocketPort(in QSockAddr addr) => ntohs(SinPort(addr));

    // net_wins.c: WINS_SetSocketPort
    private int WINS_SetSocketPort(ref QSockAddr addr, int port)
    {
        SetSinPort(ref addr, htons((ushort)port));
        return 0;
    }
}
