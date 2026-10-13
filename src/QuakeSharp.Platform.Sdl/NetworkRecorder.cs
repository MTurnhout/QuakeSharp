using System.Globalization;
using System.Net;
using System.Net.Sockets;
using QuakeSharp.Core.Net;
using static QuakeSharp.Core.Net.Winsock;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// A game on a real network recorded (-recordevents, design.md §11.5): the recorded machine (a
/// <see cref="VirtualWinsock"/> with this machine's name and address) goes out through real UDP sockets, one for each
/// of its bound sockets on the same port, and the real name service. What the real network delivers is taken at each
/// step of the virtual clock (<see cref="Poll"/>) and arrives at the machine at that step, so the engine sees it as a
/// replay will; it is written to the datagrams file with the machine's lines (<see cref="MachineLines"/>): the oracle
/// and quakesharp-tools trace replay the game from the events and this file (-tracedatagrams). The machine's own
/// datagrams to itself stay on it; their echo from the real network (a broadcast's) is passed over.
/// </summary>
public sealed class NetworkRecorder : IMachineNetwork, IDisposable
{
    private readonly SortedDictionary<int, Socket> sockets = [];
    private readonly StreamWriter file;
    private readonly byte[] buffer = new byte[65536];

    /// <param name="path">The datagrams file to write.</param>
    public NetworkRecorder(string path)
    {
        file = new StreamWriter(path) { NewLine = "\n", AutoFlush = true };
        // as net_wins.c finds its own: gethostname, then gethostbyname's first address
        var winsock = new SocketWinsock();
        _ = winsock.gethostname(out string name, 256);
        Name = name;
        Addr = winsock.gethostbyname(name)?.h_addr_list[0] ?? SocketWinsock.FromAddress(IPAddress.Loopback);

        file.WriteLine(MachineLines.Machine(Name, Addr));
    }

    /// <summary>The machine's name and first IPv4 address (its bytes in memory order).</summary>
    public string Name { get; }

    public uint Addr { get; }

    /// <summary>The recorded machine, which what arrives is given to.</summary>
    public VirtualWinsock? Winsock { get; set; }

    public int Bind(uint addr, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
            EnableBroadcast = true,
        };
        int result;
        try
        {
            socket.Bind(new IPEndPoint(SocketWinsock.ToAddress(addr), port));
            result = ((IPEndPoint)socket.LocalEndPoint!).Port;
            sockets[result] = socket;
        }
        catch (SocketException e)
        {
            socket.Dispose();
            result = -(int)e.SocketErrorCode;
        }

        file.WriteLine(MachineLines.Bind(port, result));
        return result;
    }

    public void Close(int port)
    {
        if (sockets.Remove(port, out Socket? socket))
        {
            socket.Dispose();
        }
    }

    public void Send(int port, uint addr, ushort addrPort, byte[] data)
    {
        if (sockets.TryGetValue(port, out Socket? socket))
        {
            try
            {
                socket.SendTo(data, new IPEndPoint(SocketWinsock.ToAddress(addr), ntohs(addrPort)));
            }
            catch (SocketException)
            {
                // as a datagram lost on the way
            }
        }
    }

    public HostEnt? Resolve(string name)
    {
        HostEnt? entry = new SocketWinsock().gethostbyname(name);
        if (MachineLines.Writable(name))
        {
            file.WriteLine(MachineLines.Resolve(name, entry));
        }

        return entry;
    }

    public HostEnt? ResolveAddress(uint addr)
    {
        HostEnt? entry = new SocketWinsock().gethostbyaddr(addr);
        if (entry != null && !MachineLines.Writable(entry.h_name))
        {
            entry = null;
        }

        file.WriteLine(MachineLines.Reverse(addr, entry));
        return entry;
    }

    /// <summary>
    /// What the real network has delivered, arriving at the machine at <paramref name="step"/> (the next step of the
    /// clock, before it moves): datagrams, and on Windows the ICMP answers to what was sent to a port nobody has.
    /// </summary>
    public void Poll(long step)
    {
        foreach ((int port, Socket socket) in sockets)
        {
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    Arrive(step, port, 0, 0, null);
                    continue;
                }
                catch (SocketException)
                {
                    break; // nothing more (would block)
                }

                var from = (IPEndPoint)remote;
                uint fromAddr = SocketWinsock.FromAddress(from.Address.MapToIPv4());
                bool own = fromAddr == Addr || (ntohl(fromAddr) >> 24) == 127;
                if (own && sockets.ContainsKey(from.Port))
                {
                    continue; // the machine's own, which stayed on it
                }

                Arrive(step, port, fromAddr, htons((ushort)from.Port), buffer.AsSpan(0, n).ToArray());
            }
        }
    }

    public void Dispose()
    {
        foreach (Socket socket in sockets.Values)
        {
            socket.Dispose();
        }

        sockets.Clear();
        file.Dispose();
    }

    private void Arrive(long step, int port, uint addr, ushort addrPort, byte[]? data)
    {
        Winsock?.Arrive(step, port, addr, addrPort, data);
        uint a = ntohl(addr);
        file.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{step} {port} {a >> 24}.{(a >> 16) & 0xff}.{(a >> 8) & 0xff}.{a & 0xff}:{ntohs(addrPort)} {(data == null ? "reset" : Convert.ToHexStringLower(data))}"));
    }
}
