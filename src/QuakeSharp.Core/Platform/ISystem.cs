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

/// <summary>A system that prints nothing and whose clock moves only when told: for tests and tools.</summary>
public sealed class VirtualSystem : ISystem
{
    private readonly Action<string>? print;

    public VirtualSystem(Action<string>? print = null)
    {
        this.print = print;
    }

    /// <summary>The virtual clock, in seconds.</summary>
    public double Time { get; set; }

    public void Printf(string text) => print?.Invoke(text);

    public double FloatTime() => Time;
}
