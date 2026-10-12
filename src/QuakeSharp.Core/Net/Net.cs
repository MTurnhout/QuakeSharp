using System.Runtime.CompilerServices;
using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Net;

/// <summary>net.h: the constants of the network layer.</summary>
public static class NetDef
{
    public const int NET_NAMELEN = 64;

    public const int NET_MAXMESSAGE = 8192;

    /// <summary>2 * sizeof(unsigned int)</summary>
    public const int NET_HEADERSIZE = 2 * 4;

    public const int NET_DATAGRAMSIZE = QuakeDef.MAX_DATAGRAM + NET_HEADERSIZE;

    // NetHeader flags
    public const int NETFLAG_LENGTH_MASK = 0x0000ffff;
    public const int NETFLAG_DATA = 0x00010000;
    public const int NETFLAG_ACK = 0x00020000;
    public const int NETFLAG_NAK = 0x00040000;
    public const int NETFLAG_EOM = 0x00080000;
    public const int NETFLAG_UNRELIABLE = 0x00100000;
    public const int NETFLAG_CTL = unchecked((int)0x80000000);

    public const int NET_PROTOCOL_VERSION = 3;

    public const int CCREQ_CONNECT = 0x01;
    public const int CCREQ_SERVER_INFO = 0x02;
    public const int CCREQ_PLAYER_INFO = 0x03;
    public const int CCREQ_RULE_INFO = 0x04;

    public const int CCREP_ACCEPT = 0x81;
    public const int CCREP_REJECT = 0x82;
    public const int CCREP_SERVER_INFO = 0x83;
    public const int CCREP_PLAYER_INFO = 0x84;
    public const int CCREP_RULE_INFO = 0x85;

    public const int MAX_NET_DRIVERS = 8;

    public const int HOSTCACHESIZE = 8;
}

/// <summary>net.h: struct qsockaddr's sa_data</summary>
[InlineArray(14)]
public struct SockAddrData
{
    private byte element0;
}

/// <summary>
/// net.h: struct qsockaddr, an address as a LAN driver keeps it: a family and 14 bytes (for TCP/IP a sockaddr_in: the
/// port, then the address, both in network byte order). Copied by value, as the C copies it.
/// </summary>
public struct QSockAddr
{
    public short sa_family;
    public SockAddrData sa_data;
}

/// <summary>net.h: qsocket_t, a connection.</summary>
public sealed class QSocket
{
    public QSocket? next;
    public double connecttime;
    public double lastMessageTime;
    public double lastSendTime;

    public bool disconnected;
    public bool canSend;
    public bool sendNext;

    public int driver;
    public int landriver;
    public int socket;

    /// <summary>net_loop.c: the other end of a loopback connection</summary>
    public QSocket? driverdata;

    public uint ackSequence;
    public uint sendSequence;
    public uint unreliableSendSequence;
    public int sendMessageLength;
    public readonly byte[] sendMessage = new byte[NetDef.NET_MAXMESSAGE];

    public uint receiveSequence;
    public uint unreliableReceiveSequence;
    public int receiveMessageLength;
    public readonly byte[] receiveMessage = new byte[NetDef.NET_MAXMESSAGE];

    public QSockAddr addr;
    public string address = "";
}

/// <summary>net.h: net_driver_t, a network driver (loopback, datagram): its functions are the C's pointers.</summary>
public sealed class NetDriver
{
    public required string name;
    public bool initialized;
    public required Func<int> Init;
    public required Action<bool> Listen;
    public required Action<bool> SearchForHosts;
    public required Func<string?, QSocket?> Connect;
    public required Func<QSocket?> CheckNewConnections;
    public required Func<QSocket, int> QGetMessage;
    public required Func<QSocket, SizeBuf, int> QSendMessage;
    public required Func<QSocket, SizeBuf, int> SendUnreliableMessage;
    public required Func<QSocket, bool> CanSendMessage;
    public required Func<QSocket, bool> CanSendUnreliableMessage;
    public required Action<QSocket> Close;
    public required Action Shutdown;
    public int controlSock;
}

/// <summary>
/// net.h: net_landriver_t, a LAN driver under the datagram driver (net_wins.c's TCP/IP in WinQuake). Its functions
/// are the C's pointers, in the C's order; buffers are a byte array and a length.
/// </summary>
public abstract class NetLanDriver
{
    protected NetLanDriver(string name)
    {
        this.name = name;
    }

    public string name;
    public bool initialized;
    public int controlSock;

    public abstract int Init();

    public abstract void Shutdown();

    public abstract void Listen(bool state);

    public abstract int OpenSocket(int port);

    public abstract int CloseSocket(int socket);

    public abstract int Connect(int socket, in QSockAddr addr);

    public abstract int CheckNewConnections();

    public abstract int Read(int socket, byte[] buf, int len, ref QSockAddr addr);

    public abstract int Write(int socket, byte[] buf, int len, in QSockAddr addr);

    public abstract int Broadcast(int socket, byte[] buf, int len);

    public abstract string AddrToString(in QSockAddr addr);

    public abstract int StringToAddr(string @string, ref QSockAddr addr);

    public abstract int GetSocketAddr(int socket, ref QSockAddr addr);

    public abstract int GetNameFromAddr(in QSockAddr addr, out string name);

    public abstract int GetAddrFromName(string name, ref QSockAddr addr);

    public abstract int AddrCompare(in QSockAddr addr1, in QSockAddr addr2);

    public abstract int GetSocketPort(in QSockAddr addr);

    public abstract int SetSocketPort(ref QSockAddr addr, int port);
}

/// <summary>net.h: hostcache_t, a server the search found.</summary>
public sealed class HostCache
{
    public string name = "";
    public string map = "";
    public string cname = "";
    public int users;
    public int maxusers;
    public int driver;
    public int ldriver;
    public QSockAddr addr;
}

/// <summary>net.h: PollProcedure, a function NET_Poll calls at a time.</summary>
public sealed class PollProcedure
{
    public PollProcedure(Action procedure)
    {
        this.procedure = procedure;
    }

    public PollProcedure? next;
    public double nextTime;
    public Action procedure;
}
