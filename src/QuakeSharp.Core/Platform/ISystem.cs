namespace QuakeSharp.Core.Platform;

/// <summary>
/// sys.h: what the engine asks of the system driver (sys_win.c) besides files, which <see cref="Common.FileSystem"/>
/// reads itself. Sys_Error is a <see cref="QuakeException"/>.
/// </summary>
public interface ISystem
{
    // Sys_Printf: the debugging console (WinQuake prints nothing outside a dedicated server, whose console is
    // ConsoleWrite's): the tools' logs
    void Printf(string text);

    // Sys_FloatTime: seconds; the executable's is real time, the tests' a virtual clock (design.md §6.3)
    double FloatTime();

    // Sys_Sleep: Sleep (1), between a dedicated server's frames
    void Sleep()
    {
    }

    // A dedicated server's console (sys_win.c's AllocConsole): what WriteFile writes to houtput
    void ConsoleWrite(ReadOnlySpan<byte> text)
    {
    }

    // GetNumberOfConsoleInputEvents and ReadConsoleInput: the character (AsciiChar) of the next key released in the
    // console, false when none waits (key presses and other events are passed over, as Sys_ConsoleInput passes them)
    bool ConsoleRead(out byte ch)
    {
        ch = 0;
        return false;
    }
}

/// <summary>
/// A system that prints nothing and whose clock moves only when told, a step at a time, as the oracle's
/// (tools/oracle/src/sys_oracle.c): for traces, tests and tools. Its dedicated server's console is typed into with
/// <see cref="ConsoleType"/> (-traceevents' @stdin lines).
/// </summary>
/// <remarks>
/// Waiting (design.md §11.5): WinQuake waits within a frame in loops that read the clock until an answer comes or a
/// time has passed (NET_Connect's server search, _Datagram_Connect, NET_SendToAll, Host_ShutdownServer's flush). On a
/// clock that moves only between frames they would never end, so a frame that reads the clock <see cref="WaitReads"/>
/// times without it moving is waiting: that read moves the clock one step first, the time a real machine spends there.
/// Only the engine's reads count (<see cref="FloatTime"/>), as only Sys_FloatTime's do in the oracle. A dedicated
/// server's Sys_Sleep moves it a step too.
/// </remarks>
public sealed class VirtualSystem : ISystem
{
    /// <summary>The reads of an unmoving clock that make a frame a waiting one (the oracle's ORACLE_WAIT_READS).</summary>
    public const int WaitReads = 1000;

    // the oracle's ORACLE_CONSOLE_KEYS: the keys typed and not yet read (one less fits)
    private const int ConsoleKeys = 4096;

    private readonly Action<string>? print;
    private readonly Queue<byte> consoleKeys = new();
    private int reads;

    public VirtualSystem(Action<string>? print = null, double step = 1.0 / 64)
    {
        this.print = print;
        StepSize = step;
    }

    /// <summary>The virtual clock, in seconds; reading it here does not count as the engine's read.</summary>
    public double Time { get; set; }

    /// <summary>The engine's reads of the clock so far (for the tests' guard of the enhanced mode).</summary>
    public long Reads { get; private set; }

    /// <summary>A step of the clock, in seconds.</summary>
    public double StepSize { get; }

    /// <summary>The clock's steps so far: frame 1 runs at step 1; a frame that waits takes more.</summary>
    public long Steps { get; private set; }

    /// <summary>
    /// Called before the clock moves while the engine waits or sleeps, and before each frame of a run (games between
    /// hosts give the turn to the others there).
    /// </summary>
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
        Reads++;
        if (++reads >= WaitReads)
        {
            Waiting?.Invoke();
            Step();
        }

        return Time;
    }

    // sys_oracle.c: Sys_Sleep: Sleep (1), which on Windows' default timer of 15.6 ms takes about a step of the clock
    public void Sleep()
    {
        Waiting?.Invoke();
        Step();
    }

    public bool ConsoleRead(out byte ch) => consoleKeys.TryDequeue(out ch);

    /// <summary>
    /// sys_oracle.c: Oracle_ConsoleType: "@stdin &lt;text&gt;", its characters typed in the console; \r is Enter, \b
    /// Backspace, \\ a backslash, \xHH any byte.
    /// </summary>
    public void ConsoleType(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int c = (byte)text[i++];
            if (c == '\\' && i < text.Length)
            {
                c = (byte)text[i++];
                if (c == 'r')
                {
                    c = '\r';
                }
                else if (c == 'b')
                {
                    c = '\b';
                }
                else if (c == 'x')
                {
                    c = 0;
                    for (int n = 0; n < 2 && i < text.Length; n++, i++)
                    {
                        char d = text[i];
                        int digit = d >= 'a' ? d - 'a' + 10 : d >= 'A' ? d - 'A' + 10 : d - '0';
                        c = (c * 16) + digit;
                    }
                }
            }

            if (consoleKeys.Count < ConsoleKeys - 1)
            {
                consoleKeys.Enqueue((byte)c);
            }
        }
    }
}
