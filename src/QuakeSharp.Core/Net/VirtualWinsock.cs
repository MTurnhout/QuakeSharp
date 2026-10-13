using System.Globalization;
using System.Text;
using QuakeSharp.Core.Common;
using static QuakeSharp.Core.Net.Winsock;

namespace QuakeSharp.Core.Net;

/// <summary>
/// The virtual machine's Winsock (design.md §11.5), as the oracle's tools/oracle/src/winsock_oracle.c:
/// <list type="bullet">
/// <item>its name quake&lt;n&gt; and address 10.0.0.&lt;n&gt; (-oraclehost &lt;n&gt;, 1 by default), on a virtual LAN whose hosts are
/// quake1-quake254 at 10.0.0.1-254 (gethostbyname, gethostbyaddr); localhost is 127.0.0.1;</item>
/// <item>UDP sockets (IPX's family is refused, as today's Windows has none): bound to a port (0: the next free one from
/// 49152), non-blocking with FIONBIO, broadcast-capable with SO_BROADCAST, each with a queue of what arrived;</item>
/// <item>what the machine sends to itself (its address, the loopback, or a broadcast, which also leaves) arrives at the
/// next step of the clock; one to a port no socket has gives the sender WSAECONNRESET at the next step, as Windows
/// reports the ICMP answer; what is sent to another host leaves (<see cref="Sent"/>, the trace's U line);</item>
/// <item>the other hosts' datagrams (<see cref="Arrive"/>: -tracedatagrams' file, or another engine of a game between
/// hosts), each arriving at the machine's port at a step of the clock; at each step they come before the machine's
/// own. A frame that waits for an answer moves the clock (<see cref="Platform.VirtualSystem"/>), so what is due
/// arrives while it waits.</item>
/// </list>
/// A recorded machine (design.md §11.5, step 4) has a real machine's name and address and goes out through an
/// <see cref="IMachineNetwork"/>: recording a game on a real network (-recordevents), the real one, whose datagrams the
/// recorder makes arrive at each step; replaying it, the answers its datagrams file holds (a "machine" line,
/// <see cref="RecordedNetwork"/>). Its binds and lookups are the network's; what it sends to itself stays when one of its
/// sockets has the port, and leaves otherwise, without an ICMP answer of its own (the network gives the real one).
/// </summary>
public sealed class VirtualWinsock : IWinsock
{
    public const int FirstEphemeral = 49152;
    private const int MaxSockets = 64;
    private const int FirstSocket = 1;
    private const int MaxDatagramSize = 65507;
    private const ulong FNV_OFFSET = 0xcbf29ce484222325UL;
    private const ulong FNV_PRIME = 0x100000001b3UL;

    private readonly Func<long> steps;
    private string hostName;
    private uint hostAddr;
    private IMachineNetwork? network;
    private readonly VSocket?[] sockets = new VSocket?[MaxSockets];
    private readonly LinkedList<Pending> remote = new();
    private readonly Queue<Pending> own = new();
    private int nextSocketNumber = FirstSocket;
    private int nextEphemeral = FirstEphemeral;
    private int lastError;
    private int started;
    private long deliveredStep;

    // the U line: what was sent and received since it was last taken
    private ulong datagramHash = FNV_OFFSET;
    private int datagramCount;

    // -tracedump's datagram lines of the frame running
    private StringBuilder datagramLog = new();

    /// <param name="host">The machine's number on the virtual LAN (-oraclehost).</param>
    /// <param name="steps">The clock's steps so far (<see cref="Platform.VirtualSystem.Steps"/>).</param>
    public VirtualWinsock(int host, Func<long> steps)
    {
        if (host < 1 || host > 254)
        {
            throw QuakeEngine.Sys_Error("-oraclehost: %d is not a host of the virtual LAN (1-254)", host);
        }

        Host = host;
        this.steps = steps;
        hostName = HostName(host);
        hostAddr = HostAddr(host);
    }

    /// <summary>A recorded machine: a real one's name and address, going out through <paramref name="network"/>.</summary>
    public VirtualWinsock(string name, uint addr, Func<long> steps, IMachineNetwork network)
    {
        this.steps = steps;
        hostName = name;
        hostAddr = addr;
        this.network = network;
    }

    /// <summary>The machine's name and address (its bytes in memory order).</summary>
    public string MachineName => hostName;

    public uint MachineAddr => hostAddr;

    /// <summary>The machine's number on the virtual LAN.</summary>
    public int Host { get; }

    /// <summary>
    /// A datagram leaving the machine for another host or the LAN's broadcast: the sending socket's port, the address
    /// and port it goes to (network byte order) and its bytes. Games between hosts deliver it.
    /// </summary>
    public Action<int, uint, ushort, byte[]>? Sent { get; set; }

    public static string HostName(int host) => CFormat.Sprintf("quake%d", host);

    public static uint HostAddr(int host) => MakeAddr(10, 0, 0, host);

    public static uint MakeAddr(int a, int b, int c, int d) => htonl((uint)((a << 24) | (b << 16) | (c << 8) | d));

    /// <summary>The host number of an address on the virtual LAN, or 0.</summary>
    public static int LanHost(uint addr)
    {
        uint h = ntohl(addr);
        return (h >> 8) == 0x0a0000 && (h & 0xff) >= 1 && (h & 0xff) <= 254 ? (int)(h & 0xff) : 0;
    }

    private static bool IsLoopback(uint addr) => (ntohl(addr) >> 24) == 127;

    /// <summary>Whether a socket is bound to the port (a datagram sent there arrives; otherwise the ICMP answer).</summary>
    public bool HasPort(int port) => SocketByPort(port) != null;

    /// <summary>
    /// Another host's datagram (or with data null, the ICMP answer to what the port's socket sent there), arriving at
    /// the machine's port at a step of the clock; at a step, arrivals come in the order they were given.
    /// </summary>
    public void Arrive(long step, int port, uint fromAddr, ushort fromPort, byte[]? data)
    {
        var p = new Pending(step, port, new Datagram(data == null, fromAddr, fromPort, data ?? []));
        LinkedListNode<Pending>? after = remote.Last;
        while (after != null && after.Value.Step > step)
        {
            after = after.Previous;
        }

        if (after == null)
        {
            remote.AddFirst(p);
        }
        else
        {
            remote.AddAfter(after, p);
        }
    }

    /// <summary>
    /// -tracedatagrams' file: lines "&lt;step&gt; &lt;port&gt; &lt;a.b.c.d:port&gt; &lt;hex bytes&gt;" or "... reset", as
    /// winsock_oracle.c reads them (lines it cannot read are skipped); with a "machine" line, a recorded machine
    /// replayed (<see cref="MachineLines"/>).
    /// </summary>
    public void ArriveFromFile(string path)
    {
        if (RecordedNetwork.Read(path) is { } recorded)
        {
            hostName = recorded.Name;
            hostAddr = recorded.Addr;
            network = recorded;
        }

        foreach (string line in File.ReadLines(path, Encoding.Latin1))
        {
            string[] fields = line.Split([' ', '\t'], 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int step)
                || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                || !TryReadAddress(fields[2], out uint addr, out ushort addrPort))
            {
                continue;
            }

            string bytes = fields.Length > 3 ? fields[3].TrimEnd('\r', '\n') : "";
            Arrive(step, port, addr, addrPort, bytes == "reset" ? null : Convert.FromHexString(bytes[..(bytes.Length & ~1)]));
        }
    }

    /// <summary>"a.b.c.d:port" as an address and a port in network byte order.</summary>
    public static bool TryReadAddress(string text, out uint addr, out ushort port)
    {
        addr = 0;
        port = 0;
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        string[] parts = colon < 0 ? [] : text[..colon].Split('.');
        if (parts.Length != 4
            || !int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
        {
            return false;
        }

        int[] n = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i]))
            {
                return false;
            }
        }

        addr = MakeAddr(n[0], n[1], n[2], n[3]);
        port = htons((ushort)p);
        return true;
    }

    /// <summary>The trace's U line: the hash of what was sent and received since the last call, and how many.</summary>
    public ulong Datagrams(out int count)
    {
        ulong h = datagramHash;
        count = datagramCount;
        datagramHash = FNV_OFFSET;
        datagramCount = 0;
        LastDatagramLog = datagramLog.ToString();
        datagramLog = new StringBuilder();
        return h;
    }

    /// <summary>
    /// -tracedump: the datagrams of the frame <see cref="Datagrams"/> last took, as "datagram &lt;kind&gt; &lt;port&gt;
    /// &lt;a.b.c.d:port&gt; &lt;size&gt; &lt;bytes&gt;" lines (winsock_oracle.c's Oracle_DatagramLog).
    /// </summary>
    public string LastDatagramLog { get; private set; } = "";

    // ---- the hosts ----

    public HostEnt? gethostbyname(string name)
    {
        if (network != null)
        {
            HostEnt? entry = network.Resolve(name);
            if (entry == null)
            {
                lastError = WSAHOST_NOT_FOUND;
            }

            return entry;
        }

        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new HostEnt("localhost", [MakeAddr(127, 0, 0, 1)]);
        }

        // quake<n>, the whole name ("quake1:26001" resolves no more than on Windows)
        if (name.Length > 5 && name.Length <= 8 && name.StartsWith("quake", StringComparison.OrdinalIgnoreCase)
            && name[5..].All(char.IsAsciiDigit))
        {
            int n = int.Parse(name[5..], CultureInfo.InvariantCulture);
            if (n >= 1 && n <= 254)
            {
                return new HostEnt(HostName(n), [HostAddr(n)]);
            }
        }

        uint addr = inet_addr(name);
        if (addr != INADDR_NONE)
        {
            return new HostEnt(name, [addr]);
        }

        lastError = WSAHOST_NOT_FOUND;
        return null;
    }

    public HostEnt? gethostbyaddr(uint addr)
    {
        if (network != null)
        {
            HostEnt? entry = network.ResolveAddress(addr);
            if (entry == null)
            {
                lastError = WSAHOST_NOT_FOUND;
            }

            return entry;
        }

        if (LanHost(addr) != 0)
        {
            return new HostEnt(HostName(LanHost(addr)), [addr]);
        }

        if (IsLoopback(addr))
        {
            return new HostEnt("localhost", [addr]);
        }

        lastError = WSAHOST_NOT_FOUND;
        return null;
    }

    public int gethostname(out string name, int namelen)
    {
        if (hostName.Length + 1 > namelen)
        {
            name = "";
            lastError = WSAEFAULT;
            return SOCKET_ERROR;
        }

        name = hostName;
        return 0;
    }

    // ---- delivery ----

    private VSocket? SocketByNumber(int s)
    {
        foreach (VSocket? v in sockets)
        {
            if (v != null && v.Number == s)
            {
                return v;
            }
        }

        return null;
    }

    private VSocket? SocketByPort(int port)
    {
        foreach (VSocket? v in sockets)
        {
            if (v != null && v.Bound && v.Port == port)
            {
                return v;
            }
        }

        return null;
    }

    // an arrival at the machine's port: queued at the socket bound to it, or dropped
    private void ArriveAt(int port, Datagram d) => SocketByPort(port)?.Queue.Enqueue(d);

    // what has arrived by now, step by step: the other hosts', then the machine's own
    private void Deliver()
    {
        long now = steps();
        for (long step = deliveredStep + 1; step <= now; step++)
        {
            while (remote.First != null && remote.First.Value.Step <= step)
            {
                Pending p = remote.First.Value;
                remote.RemoveFirst();
                ArriveAt(p.Port, p.Datagram);
            }

            while (own.Count > 0 && own.Peek().Step <= step)
            {
                Pending p = own.Dequeue();
                ArriveAt(p.Port, p.Datagram);
            }
        }

        if (now > deliveredStep)
        {
            deliveredStep = now;
        }
    }

    private void SendOwn(int port, Datagram d) => own.Enqueue(new Pending(steps() + 1, port, d));

    private void Hash(int v)
    {
        for (int i = 0; i < 4; i++)
        {
            datagramHash ^= (byte)(v >> (8 * i));
            datagramHash *= FNV_PRIME;
        }
    }

    // the U line: 0 sent, 1 received, 2 the ICMP answer received; the socket's port, the other end, the bytes
    private void HashDatagram(int kind, int port, uint addr, ushort addrPort, ReadOnlySpan<byte> data)
    {
        Hash(kind);
        Hash(port);
        Hash((int)ntohl(addr));
        Hash(ntohs(addrPort));
        Hash(data.Length);
        foreach (byte b in data)
        {
            datagramHash ^= b;
            datagramHash *= FNV_PRIME;
        }

        datagramCount++;

        uint a = ntohl(addr);
        datagramLog.Append(CultureInfo.InvariantCulture,
            $"datagram {kind} {port} {a >> 24}.{(a >> 16) & 0xff}.{(a >> 8) & 0xff}.{a & 0xff}:{ntohs(addrPort)} {data.Length} ");
        datagramLog.Append(Convert.ToHexStringLower(data));
        datagramLog.Append('\n');
    }

    // ---- the sockets ----

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

        int i = Array.IndexOf(sockets, null);
        if (i < 0)
        {
            lastError = WSAEMFILE;
            return INVALID_SOCKET;
        }

        sockets[i] = new VSocket(nextSocketNumber++);
        return sockets[i]!.Number;
    }

    public int ioctlsocket(int s, int cmd, ref uint argp)
    {
        VSocket? v = SocketByNumber(s);
        if (v == null)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (cmd == FIONREAD)
        {
            // for a datagram socket, the size of the first datagram queued
            Deliver();
            argp = v.Queue.Count > 0 && !v.Queue.Peek().Reset ? (uint)v.Queue.Peek().Data.Length : 0;
            return 0;
        }

        if (cmd != FIONBIO)
        {
            lastError = WSAEINVAL;
            return SOCKET_ERROR;
        }

        v.NonBlocking = argp != 0;
        return 0;
    }

    public int setsockopt(int s, int level, int optname, int optval)
    {
        VSocket? v = SocketByNumber(s);
        if (v == null)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (level != SOL_SOCKET || optname != SO_BROADCAST)
        {
            lastError = WSAENOPROTOOPT;
            return SOCKET_ERROR;
        }

        v.Broadcast = optval != 0;
        return 0;
    }

    public int bind(int s, in QSockAddr name)
    {
        VSocket? v = SocketByNumber(s);
        if (v == null)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (v.Bound || name.sa_family != AF_INET)
        {
            lastError = WSAEINVAL;
            return SOCKET_ERROR;
        }

        uint addr = SinAddr(name);
        // (a recorded machine's binds are the network's, to any of its addresses)
        if (network == null && addr != INADDR_ANY && addr != hostAddr && !IsLoopback(addr))
        {
            lastError = WSAEADDRNOTAVAIL;
            return SOCKET_ERROR;
        }

        int port = ntohs(SinPort(name));
        if (network != null)
        {
            // a recorded machine's ports are the network's
            int result = network.Bind(addr, port);
            if (result < 0)
            {
                lastError = -result;
                return SOCKET_ERROR;
            }

            port = result;
        }
        else if (port == 0)
        {
            while (SocketByPort(nextEphemeral) != null)
            {
                nextEphemeral = nextEphemeral == 65535 ? FirstEphemeral : nextEphemeral + 1;
            }

            port = nextEphemeral;
            nextEphemeral = nextEphemeral == 65535 ? FirstEphemeral : nextEphemeral + 1;
        }
        else if (SocketByPort(port) != null)
        {
            lastError = WSAEADDRINUSE;
            return SOCKET_ERROR;
        }

        v.Port = port;
        v.Bound = true;
        return 0;
    }

    public int recvfrom(int s, byte[] buf, int len, int flags, ref QSockAddr from, bool fromNull = false)
    {
        VSocket? v = SocketByNumber(s);
        if (v == null)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (!v.Bound)
        {
            lastError = WSAEINVAL;
            return SOCKET_ERROR;
        }

        Deliver();
        if (v.Queue.Count == 0)
        {
            lastError = WSAEWOULDBLOCK;
            return SOCKET_ERROR;
        }

        Datagram d = v.Queue.Peek();
        if (!fromNull)
        {
            from = SockAddrIn(d.FromAddr, d.FromPort);
        }

        if (d.Reset)
        {
            v.Queue.Dequeue();
            HashDatagram(2, v.Port, d.FromAddr, d.FromPort, []);
            lastError = WSAECONNRESET;
            return SOCKET_ERROR;
        }

        int n = Math.Min(d.Data.Length, len);
        d.Data.AsSpan(0, n).CopyTo(buf);
        if ((flags & MSG_PEEK) != 0)
        {
            if (d.Data.Length > len)
            {
                lastError = WSAEMSGSIZE;
                return SOCKET_ERROR;
            }

            return n;
        }

        v.Queue.Dequeue();
        HashDatagram(1, v.Port, d.FromAddr, d.FromPort, d.Data.AsSpan(0, n));
        if (d.Data.Length > len)
        {
            lastError = WSAEMSGSIZE;
            return SOCKET_ERROR;
        }

        return n;
    }

    public int sendto(int s, byte[] buf, int len, int flags, in QSockAddr to)
    {
        VSocket? v = SocketByNumber(s);
        if (v == null)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (!v.Bound)
        {
            lastError = WSAEINVAL;
            return SOCKET_ERROR;
        }

        if (to.sa_family != AF_INET)
        {
            lastError = WSAEAFNOSUPPORT;
            return SOCKET_ERROR;
        }

        if (len > MaxDatagramSize)
        {
            lastError = WSAEMSGSIZE;
            return SOCKET_ERROR;
        }

        uint addr = SinAddr(to);
        ushort toPort = SinPort(to);
        int port = ntohs(toPort);
        if (addr == INADDR_BROADCAST && !v.Broadcast)
        {
            lastError = WSAEACCES;
            return SOCKET_ERROR;
        }

        byte[] data = buf.AsSpan(0, len).ToArray();
        HashDatagram(0, v.Port, addr, toPort, data);

        // to the machine itself: from its address, or from 127.0.0.1 when sent to the loopback
        bool own = addr == hostAddr || IsLoopback(addr);
        bool stays = false;
        if (own || addr == INADDR_BROADCAST)
        {
            uint fromAddr = IsLoopback(addr) ? MakeAddr(127, 0, 0, 1) : hostAddr;
            if (SocketByPort(port) != null)
            {
                SendOwn(port, new Datagram(false, fromAddr, htons((ushort)v.Port), data));
                stays = own;
            }
            else if (own && network == null)
            {
                // no socket there: the ICMP answer
                SendOwn(v.Port, new Datagram(true, addr, toPort, []));
                stays = true;
            }
        }

        if (network != null)
        {
            // a recorded machine: what is not for one of its own sockets leaves (another program may have the port)
            if (!stays)
            {
                network.Send(v.Port, addr, toPort, data);
            }
        }
        else if (!own)
        {
            Sent?.Invoke(v.Port, addr, toPort, data);
        }

        return len;
    }

    public int closesocket(int s)
    {
        int i = Array.FindIndex(sockets, v => v != null && v.Number == s);
        if (i < 0)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (sockets[i]!.Bound)
        {
            network?.Close(sockets[i]!.Port);
        }

        sockets[i] = null;
        return 0;
    }

    public int getsockname(int s, ref QSockAddr name)
    {
        VSocket? v = SocketByNumber(s);
        if (v == null)
        {
            lastError = WSAENOTSOCK;
            return SOCKET_ERROR;
        }

        if (!v.Bound)
        {
            lastError = WSAEINVAL;
            return SOCKET_ERROR;
        }

        name = SockAddrIn(INADDR_ANY, htons((ushort)v.Port));
        return 0;
    }

    private sealed class VSocket(int number)
    {
        public int Number { get; } = number;

        public int Port { get; set; }

        public bool Bound { get; set; }

        public bool NonBlocking { get; set; }

        public bool Broadcast { get; set; }

        public Queue<Datagram> Queue { get; } = new();
    }

    private sealed record Datagram(bool Reset, uint FromAddr, ushort FromPort, byte[] Data);

    private sealed record Pending(long Step, int Port, Datagram Datagram);
}
