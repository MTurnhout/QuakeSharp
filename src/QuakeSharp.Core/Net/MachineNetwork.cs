using System.Globalization;
using System.Text;
using static QuakeSharp.Core.Net.Winsock;

namespace QuakeSharp.Core.Net;

/// <summary>
/// A virtual machine's way out to a network beyond it (design.md §11.5): its sockets' ports, what it sends that does not
/// stay on the machine, and the names it looks up. Recording a game on a real network (-recordevents), the real one;
/// replaying it, the answers the recording got (<see cref="RecordedNetwork"/>).
/// </summary>
public interface IMachineNetwork
{
    /// <summary>A socket bound to the port (0: the system's choice): the port it got, or a negative Winsock error.</summary>
    int Bind(uint addr, int port);

    void Close(int port);

    /// <summary>A datagram from the machine's port to an address and port (network byte order) off the machine.</summary>
    void Send(int port, uint addr, ushort addrPort, byte[] data);

    HostEnt? Resolve(string name);

    HostEnt? ResolveAddress(uint addr);
}

/// <summary>
/// The lines a recorded machine's datagrams file (-tracedatagrams) holds besides the datagrams, in the order they
/// happened: "machine &lt;name&gt; &lt;a.b.c.d&gt;", the machine's name and address; "bind &lt;port&gt; &lt;result&gt;", a
/// bind's port asked and the port it got or the negative Winsock error; "resolve &lt;name&gt; &lt;a.b.c.d&gt;|none" and
/// "reverse &lt;a.b.c.d&gt; &lt;name&gt;|none", gethostbyname's and gethostbyaddr's answers (winsock_oracle.c reads
/// the same).
/// </summary>
public static class MachineLines
{
    public static string Machine(string name, uint addr) => $"machine {name} {inet_ntoa(addr)}";

    public static string Bind(int port, int result) => string.Create(CultureInfo.InvariantCulture, $"bind {port} {result}");

    public static string Resolve(string name, HostEnt? entry) =>
        $"resolve {name} {(entry == null ? "none" : inet_ntoa(entry.h_addr_list[0]))}";

    public static string Reverse(uint addr, HostEnt? entry) => $"reverse {inet_ntoa(addr)} {entry?.h_name ?? "none"}";

    /// <summary>Whether a name can be written in a line (one word).</summary>
    public static bool Writable(string name) => name.Length > 0 && !name.Any(c => c <= ' ');
}

/// <summary>
/// A recorded machine replayed (-tracedatagrams with a "machine" line): its binds get the ports the recording's did,
/// its lookups the answers it got, each in turn; what it sends goes nowhere (the recording's answers are the file's
/// datagrams).
/// </summary>
public sealed class RecordedNetwork : IMachineNetwork
{
    private readonly Queue<int> binds = new();
    private readonly List<(string Name, HostEnt? Entry)> resolves = [];
    private readonly List<(uint Addr, HostEnt? Entry)> reverses = [];

    private RecordedNetwork(string name, uint addr)
    {
        Name = name;
        Addr = addr;
    }

    public string Name { get; }

    public uint Addr { get; }

    /// <summary>The recorded machine of a datagrams file, or null when it has no "machine" line.</summary>
    public static RecordedNetwork? Read(string path)
    {
        RecordedNetwork? network = null;
        foreach (string line in File.ReadLines(path, Encoding.Latin1))
        {
            string[] w = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 3 && w[0] == "machine" && inet_addr(w[2]) is uint machineAddr && machineAddr != INADDR_NONE)
            {
                network = new RecordedNetwork(w[1], machineAddr);
            }
            else if (network == null)
            {
                continue;
            }
            else if (w.Length == 3 && w[0] == "bind"
                && int.TryParse(w[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            {
                network.binds.Enqueue(result);
            }
            else if (w.Length == 3 && w[0] == "resolve")
            {
                uint a = inet_addr(w[2]);
                network.resolves.Add((w[1], w[2] == "none" || a == INADDR_NONE ? null : new HostEnt(w[1], [a])));
            }
            else if (w.Length == 3 && w[0] == "reverse" && inet_addr(w[1]) is uint reverseAddr && reverseAddr != INADDR_NONE)
            {
                network.reverses.Add((reverseAddr, w[2] == "none" ? null : new HostEnt(w[2], [reverseAddr])));
            }
        }

        return network;
    }

    public int Bind(uint addr, int port) => binds.TryDequeue(out int result) ? result : -WSAEADDRNOTAVAIL;

    public void Close(int port)
    {
    }

    public void Send(int port, uint addr, ushort addrPort, byte[] data)
    {
    }

    // the first answer not yet given for the name (the calls come in the recording's order)
    public HostEnt? Resolve(string name)
    {
        int i = resolves.FindIndex(r => string.Equals(r.Name, name, StringComparison.Ordinal));
        if (i < 0)
        {
            return null;
        }

        HostEnt? entry = resolves[i].Entry;
        resolves.RemoveAt(i);
        return entry;
    }

    public HostEnt? ResolveAddress(uint addr)
    {
        int i = reverses.FindIndex(r => r.Addr == addr);
        if (i < 0)
        {
            return null;
        }

        HostEnt? entry = reverses[i].Entry;
        reverses.RemoveAt(i);
        return entry;
    }
}
