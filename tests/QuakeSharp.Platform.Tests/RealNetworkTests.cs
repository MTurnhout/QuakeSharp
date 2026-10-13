using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QuakeSharp.Core;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;
using QuakeSharp.Platform.Sdl;

namespace QuakeSharp.Platform.Tests;

/// <summary>
/// The real network (design.md §11.5, step 4): two engines in one process, each on a thread of its own on the real
/// clock as two programs would be, over <see cref="SocketWinsock"/> on this machine: a dedicated server, and a client that
/// connects to it, enters the game and talks. Not traced (real time); it needs an id1/pak0.pak (the shareware's in CI)
/// in QUAKESHARP_QUAKE_DIR and skips without one.
/// </summary>
public sealed class RealNetworkTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ClientJoinsDedicatedServerOverRealSockets()
    {
        string gameDir = CopyPak0();
        int port = FreeUdpPort();
        string udpport = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // the server: -dedicated 2, quake.rc's startdemos starting the start map
        using var server = new Host(gameDir, Path.Combine(dir, "server"), ["-dedicated", "2", "-udpport", udpport]);
        server.Start(engine => engine.sv.active);
        Assert.True(server.Ready.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), $"the server did not start:\n{server.Text}");

        // the client: connects by the machine's address, and once in the game says hello. Not 127.0.0.1: once WinQuake
        // has broadcast (the server search before a connect) its sockets bind to the machine's address, which can't send
        // to the loopback on Windows (design.md §3.4)
        bool said = false;
        using var winsock = new SocketWinsock();
        _ = winsock.gethostname(out string name, 256);
        string address = Core.Net.Winsock.inet_ntoa(winsock.gethostbyname(name)?.h_addr_list[0] ?? Core.Net.Winsock.inet_addr("127.0.0.1"));
        using var client = new Host(gameDir, Path.Combine(dir, "client"), ["-udpport", udpport, "+connect", address]);
        client.Start(engine =>
        {
            if (engine.cls.signon == QuakeEngine.SIGNONS && !said)
            {
                engine.Cbuf_AddText("say hello over the wire\n");
                said = true;
            }

            return said;
        });
        Assert.True(client.Ready.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), $"the client did not enter the game:\n{client.Text}\nthe server:\n{server.Text}");

        // the server prints what the client said
        var clock = Stopwatch.StartNew();
        while (!server.Text.Contains("player: hello over the wire", StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(50);
        }

        client.Stop();
        server.Stop();
        Assert.Null(client.Failure);
        Assert.Null(server.Failure);
        Assert.Contains("player entered the game", server.Text, StringComparison.Ordinal);
        Assert.Contains("player: hello over the wire", server.Text, StringComparison.Ordinal);
    }

    // id1/pak0.pak from QUAKESHARP_QUAKE_DIR, alone in a game folder of the test's (no config.cfg of an installation's)
    private string CopyPak0()
    {
        string? setting = Environment.GetEnvironmentVariable("QUAKESHARP_QUAKE_DIR");
        foreach (string quake in string.IsNullOrEmpty(setting) ? [] : setting.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string? id1 = Directory.Exists(quake)
                ? Directory.EnumerateDirectories(quake).FirstOrDefault(d => Path.GetFileName(d).Equals("id1", StringComparison.OrdinalIgnoreCase))
                : null;
            string? pak = id1 == null ? null
                : Directory.EnumerateFiles(id1).FirstOrDefault(f => Path.GetFileName(f).Equals("pak0.pak", StringComparison.OrdinalIgnoreCase));
            if (pak != null)
            {
                string gameDir = Path.Combine(dir, "quake");
                Directory.CreateDirectory(Path.Combine(gameDir, "id1"));
                File.Copy(pak, Path.Combine(gameDir, "id1", "pak0.pak"));
                return gameDir;
            }
        }

        Assert.Skip("Set QUAKESHARP_QUAKE_DIR to a Quake folder with an id1/pak0.pak.");
        return "";
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    // an engine on its own thread, on the real clock, as the executable runs one (sys_win.c's loops)
    private sealed class Host : IDisposable
    {
        private readonly QuakeEngine engine;
        private readonly SocketWinsock winsock = new();
        private readonly RealClock system = new();
        private Thread? thread;
        private volatile bool stopping;

        public Host(string gameDir, string userDir, string[] args)
        {
            engine = new QuakeEngine(new QuakeParms(".", new CommandLine(["quake", .. args])) { workdir = gameDir, userdir = userDir },
                system, winsock: winsock);
        }

        public ManualResetEventSlim Ready { get; } = new();

        public Exception? Failure { get; private set; }

        public string Text => system.Text;

        // frames until stopped; ready once the condition holds (tested in the engine's thread, after a frame)
        public void Start(Func<QuakeEngine, bool> ready)
        {
            thread = new Thread(() =>
            {
                try
                {
                    engine.Host_Init();
                    double oldtime = system.FloatTime();
                    while (!stopping)
                    {
                        double newtime = system.FloatTime();
                        double time = newtime - oldtime;
                        while (engine.isDedicated && time < engine.sys_ticrate.value)
                        {
                            system.Sleep();
                            newtime = system.FloatTime();
                            time = newtime - oldtime;
                        }

                        engine.Host_Frame((float)time);
                        oldtime = newtime;
                        if (ready(engine))
                        {
                            Ready.Set();
                        }

                        Thread.Sleep(1);
                    }

                    _ = engine.Sys_Quit();
                }
                catch (QuitException)
                {
                }
                catch (Exception e)
                {
                    Failure = e;
                }
            })
            {
                IsBackground = true,
            };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            thread?.Join(TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            Stop();
            winsock.Dispose();
            Ready.Dispose();
        }
    }

    private sealed class RealClock : ISystem
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly StringBuilder text = new();

        public string Text
        {
            get
            {
                lock (text)
                {
                    return text.ToString();
                }
            }
        }

        public void Printf(string text)
        {
            lock (this.text)
            {
                this.text.Append(text);
            }
        }

        public double FloatTime() => clock.Elapsed.TotalSeconds;

        public void Sleep() => Thread.Sleep(1);
    }
}
