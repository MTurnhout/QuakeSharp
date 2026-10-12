using QuakeSharp.Core.Common;

namespace QuakeSharp.Core;

// host.c: the host's state (its functions are ported with the frame loop)
public sealed partial class QuakeEngine
{
    /// <summary>true if into command execution</summary>
    public bool host_initialized;

    public double host_frametime;

    public double host_time;

    /// <summary>without any filtering or bounding</summary>
    public double realtime;

    /// <summary>last frame run</summary>
    public double oldrealtime;

    public int host_framecount;

    // host.c: the host's cvars
    public readonly Cvar host_framerate = new("host_framerate", "0"); // set for slow motion
    public readonly Cvar host_speeds = new("host_speeds", "0"); // set for running times
    public readonly Cvar sys_ticrate = new("sys_ticrate", "0.05");
    public readonly Cvar serverprofile = new("serverprofile", "0");
    public readonly Cvar fraglimit = new("fraglimit", "0", false, true);
    public readonly Cvar timelimit = new("timelimit", "0", false, true);
    public readonly Cvar teamplay = new("teamplay", "0", false, true);
    public readonly Cvar samelevel = new("samelevel", "0");
    public readonly Cvar noexit = new("noexit", "0", false, true);
    public readonly Cvar developer = new("developer", "0");
    public readonly Cvar skill = new("skill", "1"); // 0 - 3
    public readonly Cvar deathmatch = new("deathmatch", "0"); // 0, 1, or 2
    public readonly Cvar coop = new("coop", "0"); // 0 or 1
    public readonly Cvar pausable = new("pausable", "1");
    public readonly Cvar temp1 = new("temp1", "0");
}
