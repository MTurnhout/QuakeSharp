namespace QuakeSharp.Core.Platform;

/// <summary>
/// sys.h: what the engine asks of the system driver (sys_win.c) besides files, which <see cref="Common.FileSystem"/>
/// reads itself. Sys_Error is a <see cref="QuakeException"/>.
/// </summary>
public interface ISystem
{
    // Sys_Printf: the debugging console (WinQuake prints nothing outside a dedicated server)
    void Printf(string text);

    // Sys_FloatTime: seconds; the executable's is real time, the tests' a virtual clock (design.md §6.3)
    double FloatTime();
}

/// <summary>
/// A system that prints nothing and whose clock moves only when told, a step at a time, as the oracle's
/// (tools/oracle/src/sys_oracle.c): for traces, tests and tools.
/// </summary>
/// <remarks>
/// Waiting (design.md §11.5): WinQuake waits within a frame in loops that read the clock until an answer comes or a
/// time has passed (NET_Connect's server search, _Datagram_Connect, NET_SendToAll, Host_ShutdownServer's flush). On a
/// clock that moves only between frames they would never end, so a frame that reads the clock <see cref="WaitReads"/>
/// times without it moving is waiting: that read moves the clock one step first, the time a real machine spends there.
/// Only the engine's reads count (<see cref="FloatTime"/>), as only Sys_FloatTime's do in the oracle.
/// </remarks>
public sealed class VirtualSystem : ISystem
{
    /// <summary>The reads of an unmoving clock that make a frame a waiting one (the oracle's ORACLE_WAIT_READS).</summary>
    public const int WaitReads = 1000;

    private readonly Action<string>? print;
    private int reads;

    public VirtualSystem(Action<string>? print = null, double step = 1.0 / 64)
    {
        this.print = print;
        StepSize = step;
    }

    /// <summary>The virtual clock, in seconds; reading it here does not count as the engine's read.</summary>
    public double Time { get; set; }

    /// <summary>A step of the clock, in seconds.</summary>
    public double StepSize { get; }

    /// <summary>The clock's steps so far: frame 1 runs at step 1; a frame that waits takes more.</summary>
    public long Steps { get; private set; }

    /// <summary>Called when a waiting frame is about to move the clock (games between hosts run the others first).</summary>
    public Action? Waiting { get; set; }

    /// <summary>Moves the clock one step: before each frame, and while a frame waits.</summary>
    public void Step()
    {
        Time += StepSize;
        Steps++;
        reads = 0;
    }

    public void Printf(string text) => print?.Invoke(text);

    // sys_oracle.c: Sys_FloatTime
    public double FloatTime()
    {
        if (++reads >= WaitReads)
        {
            Waiting?.Invoke();
            Step();
        }

        return Time;
    }
}
