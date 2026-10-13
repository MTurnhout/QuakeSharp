using System.Globalization;
using System.Text;
using QuakeSharp.Core.Net;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// A game between hosts: its file (tools/oracle/events/&lt;game&gt;.net): a host's command line on each line, host 1
/// first (an -traceevents file relative to the game's file); "lanloss &lt;n&gt;" for a lossy LAN; "datagram &lt;step&gt;
/// &lt;host&gt; &lt;port&gt; &lt;a.b.c.d:port&gt; &lt;hex bytes&gt;" for a datagram from no host of the game arriving at a host's
/// port at a step of the clock (what only another program sends: refused versions, malformed requests); '#' comments.
/// </summary>
public sealed record NetGame(IReadOnlyList<IReadOnlyList<string>> Hosts, int LanLoss, IReadOnlyList<NetGame.Injection> Datagrams)
{
    /// <summary>A datagram from no host of the game (addresses and ports in network byte order).</summary>
    public sealed record Injection(long Step, int Host, int Port, uint FromAddr, ushort FromPort, byte[] Data);

    public static NetGame Read(string path)
    {
        var hosts = new List<IReadOnlyList<string>>();
        var datagrams = new List<Injection>();
        int lanLoss = 0;
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words[0] == "lanloss" && words.Length == 2)
            {
                lanLoss = int.Parse(words[1], CultureInfo.InvariantCulture);
                continue;
            }

            if (words[0] == "datagram" && words.Length == 6 && VirtualWinsock.TryReadAddress(words[4], out uint addr, out ushort port))
            {
                datagrams.Add(new Injection(long.Parse(words[1], CultureInfo.InvariantCulture),
                    int.Parse(words[2], CultureInfo.InvariantCulture), int.Parse(words[3], CultureInfo.InvariantCulture),
                    addr, port, Convert.FromHexString(words[5])));
                continue;
            }

            for (int i = 0; i < words.Length - 1; i++)
            {
                if (words[i] == "-traceevents")
                {
                    words[i + 1] = Path.Combine(dir, words[i + 1]);
                }
            }

            hosts.Add(words);
        }

        return new NetGame(hosts, lanLoss, datagrams);
    }
}

/// <summary>
/// A game between hosts (design.md §11.5): several engines in one process, each the virtual machine quake&lt;n&gt; with
/// its own <see cref="VirtualWinsock"/> on one virtual LAN, in lockstep on one virtual clock. At each step every host
/// runs a frame or, while a frame waits for the network, one step of its wait; a datagram sent during a step arrives at
/// the next. Each host runs as the oracle runs one (<see cref="TraceRun"/>, its own command line), and what it received
/// is kept as -tracedatagrams lines, so the oracle can replay each host alone. A host whose command line has -netunplug
/// leaves the LAN when it has run its -oracleframes (a machine unplugged: the others see it time out).
/// </summary>
public sealed class NetRun
{
    private readonly Host[] hosts;
    private readonly int lanLoss;
    private long routed;

    /// <param name="gameDir">The Quake folder (holding id1/).</param>
    /// <param name="userDirs">Each host's folder for what it writes (an empty folder each).</param>
    /// <param name="game">The hosts' command lines (host n is quake&lt;n&gt;: -oraclehost is added), the LAN's loss, the
    /// datagrams from no host of the game.</param>
    /// <param name="print">Each host's Sys_Printf (its number, the text).</param>
    public NetRun(string gameDir, IReadOnlyList<string> userDirs, NetGame game, Action<int, string>? print = null)
    {
        lanLoss = game.LanLoss;
        hosts = new Host[game.Hosts.Count];
        for (int i = 0; i < hosts.Length; i++)
        {
            int number = i + 1;
            var run = new TraceRun(gameDir, userDirs[i], ["-oraclehost", number.ToString(CultureInfo.InvariantCulture), .. game.Hosts[i]],
                print: print == null ? null : text => print(number, text));
            hosts[i] = new Host(number, run, game.Hosts[i]);
            int from = i;
            run.Winsock.Sent = (port, addr, addrPort, data) => Route(from, port, addr, addrPort, data);
        }

        foreach (NetGame.Injection d in game.Datagrams)
        {
            Host host = hosts[d.Host - 1];
            host.Run.Winsock.Arrive(d.Step, d.Port, d.FromAddr, d.FromPort, d.Data);
            host.Record(d.Step, d.Port, d.FromAddr, d.FromPort, d.Data);
        }
    }

    /// <summary>The hosts' runs (host n is index n - 1).</summary>
    public IReadOnlyList<TraceRun> Runs => [.. hosts.Select(h => h.Run)];

    /// <summary>
    /// Runs the game to its end (every host has quit or run its -oracleframes); onFrame gets each host's lines (the
    /// host's number, its lines) as its frames end, in the order they end.
    /// </summary>
    public void Run(Action<int, IReadOnlyList<string>> onFrame)
    {
        foreach (Host host in hosts)
        {
            host.Start(onFrame);
        }

        try
        {
            while (hosts.Any(h => !h.Finished))
            {
                foreach (Host host in hosts)
                {
                    if (!host.Finished)
                    {
                        host.Turn();
                    }
                }
            }
        }
        finally
        {
            foreach (Host host in hosts)
            {
                host.Stop();
            }
        }
    }

    /// <summary>
    /// A host's datagrams, as -tracedatagrams lines: in the order of their steps, and at a step in the order they
    /// arrived (as <see cref="VirtualWinsock.Arrive"/> delivers them).
    /// </summary>
    public string Datagrams(int host)
    {
        var sb = new StringBuilder();
        foreach ((long _, string line) in hosts[host - 1].Datagrams.OrderBy(d => d.Step))
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    // a datagram leaving host from: to another host of the LAN, which has a socket at that port (or the sender gets the
    // ICMP answer), or broadcast to every other host that has one; what no host of the run has an address for is lost
    private void Route(int from, int port, uint addr, ushort addrPort, byte[] data)
    {
        Host sender = hosts[from];
        if (sender.Unplugged)
        {
            return;
        }

        long step = sender.Run.System.Steps + 1;
        uint fromAddr = VirtualWinsock.HostAddr(sender.Number);
        ushort fromPort = Winsock.htons((ushort)port);
        int toPort = Winsock.ntohs(addrPort);

        foreach (Host host in hosts)
        {
            if (host == sender || host.Unplugged
                || (addr != Winsock.INADDR_BROADCAST && VirtualWinsock.LanHost(addr) != host.Number))
            {
                continue;
            }

            if (lanLoss != 0 && ++routed % lanLoss == 0)
            {
                continue;
            }

            if (host.Run.Winsock.HasPort(toPort))
            {
                host.Run.Winsock.Arrive(step, toPort, fromAddr, fromPort, data);
                host.Record(step, toPort, fromAddr, fromPort, data);
            }
            else if (addr != Winsock.INADDR_BROADCAST)
            {
                // no socket there: the ICMP answer
                sender.Run.Winsock.Arrive(step, port, addr, addrPort, null);
                sender.Record(step, port, addr, addrPort, null);
            }
        }
    }

    // a host: its engine on a thread of its own, which runs only during its turns
    private sealed class Host(int number, TraceRun run, IReadOnlyList<string> args)
    {
        private readonly SemaphoreSlim go = new(0);
        private readonly SemaphoreSlim done = new(0);
        private Thread? thread;
        private Exception? failure;
        private volatile bool stopping;

        public int Number { get; } = number;

        public TraceRun Run { get; } = run;

        /// <summary>What arrived, as -tracedatagrams lines with their steps, in the order they were given.</summary>
        public List<(long Step, string Line)> Datagrams { get; } = [];

        public bool Finished { get; private set; }

        /// <summary>-netunplug: off the LAN once it has run its -oracleframes, so what it sends while quitting is lost
        /// (the others see it time out), as when the oracle replays it alone.</summary>
        public bool Unplugged { get; private set; }

        public void Record(long step, int port, uint addr, ushort addrPort, byte[]? data)
        {
            uint a = Winsock.ntohl(addr);
            Datagrams.Add((step, string.Create(CultureInfo.InvariantCulture,
                $"{step} {port} {a >> 24}.{(a >> 16) & 0xff}.{(a >> 8) & 0xff}.{a & 0xff}:{Winsock.ntohs(addrPort)} {(data == null ? "reset" : Convert.ToHexStringLower(data))}")));
        }

        public void Start(Action<int, IReadOnlyList<string>> onFrame)
        {
            var commandLine = new Common.CommandLine(["quake", .. args]);
            int f = commandLine.CheckParm("-oracleframes");
            int frames = f != 0 && f < commandLine.Argc - 1 ? Common.QString.Q_atoi(commandLine.Argv(f + 1)) : int.MaxValue;
            bool quitAfterDemo = commandLine.CheckParm("-tracequitafterdemo") != 0;
            bool unplug = commandLine.CheckParm("-netunplug") != 0;
            int count = 0;

            // a frame that waits gives the turn back until the next step
            Run.System.Waiting = () =>
            {
                done.Release();
                go.Wait();
                if (stopping)
                {
                    throw new OperationCanceledException();
                }
            };

            thread = new Thread(() =>
            {
                try
                {
                    go.Wait();
                    if (stopping)
                    {
                        return;
                    }

                    foreach (IReadOnlyList<string> lines in Run.Frames(quitAfterDemo, frames))
                    {
                        onFrame(Number, lines);
                        if (unplug && ++count >= frames)
                        {
                            Unplugged = true;
                        }

                        done.Release();
                        go.Wait();
                        if (stopping)
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    failure = e;
                }

                Finished = true;
                done.Release();
            })
            {
                IsBackground = true,
                Name = $"quake{Number}",
            };
            thread.Start();
        }

        // the host's turn at a step: until its frame ends, it waits, or it finishes
        public void Turn()
        {
            go.Release();
            done.Wait();
            if (failure != null)
            {
                throw new InvalidOperationException($"quake{Number} failed: {failure.Message}", failure);
            }
        }

        public void Stop()
        {
            if (thread != null && thread.IsAlive)
            {
                stopping = true;
                go.Release();
                thread.Join();
            }
        }
    }
}
