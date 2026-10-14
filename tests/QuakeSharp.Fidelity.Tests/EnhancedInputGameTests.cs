using QuakeSharp.Core;
using QuakeSharp.Core.Diagnostics;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Fidelity.Tests;

/// <summary>
/// The enhanced mode's mouse in a game (design.md decision 76), headless: the scripted mouse of the traces, in a window
/// with <c>_windowed_mouse</c> 0. WinQuake's rules leave the mouse alone there; the enhanced input takes it, looks freely and
/// lets the wheel change weapons. Nothing is sent to the real keyboard or mouse.
/// </summary>
public sealed class EnhancedInputGameTests : IDisposable
{
    private readonly string userdir = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N"));
    private readonly string events = Path.Combine(Path.GetTempPath(), "quakesharp-tests", Guid.NewGuid().ToString("N") + ".events");

    public void Dispose()
    {
        if (Directory.Exists(userdir))
        {
            Directory.Delete(userdir, recursive: true);
        }

        if (File.Exists(events))
        {
            File.Delete(events);
        }
    }

    private (float Yaw, float Pitch, int Weapon) Play(EnhancedInput? input)
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        Directory.CreateDirectory(Path.GetDirectoryName(events)!);
        File.WriteAllText(events, string.Join('\n',
            "5 map e1m1",
            "100 god",
            "110 @mouse 30 20",
            "112 @mouse 30 20",
            "114 @mouse 30 20",
            "130 @key 239 1",
            "131 @key 239 0",
            ""));
        var run = new TraceRun(baseDir, userdir, ["-traceevents", events, "-oracleframes", "160"]);
        run.Engine.EIN_Set(input);
        float yaw = 0, pitch = 0;
        int frame = 0;
        int weapon = 0;
        foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: false, maxFrames: 160))
        {
            if (++frame == 109)
            {
                yaw = run.Engine.cl.viewangles[1];
                pitch = run.Engine.cl.viewangles[0];
                weapon = run.Engine.cl.stats[(int)QuakeDef.STAT_ACTIVEWEAPON];
            }
        }

        return (run.Engine.cl.viewangles[1] - yaw, run.Engine.cl.viewangles[0] - pitch,
            run.Engine.cl.stats[(int)QuakeDef.STAT_ACTIVEWEAPON] - weapon);
    }

    [Fact]
    public void WithoutTheEnhancedInputAWindowedMouseAndTheWheelDoNothing()
    {
        (float yaw, float pitch, int weapon) = Play(null);
        Assert.Equal(0f, yaw);
        Assert.Equal(0f, pitch);
        Assert.Equal(0, weapon);
    }

    [Fact]
    public void WithItTheMouseIsTakenLooksFreelyAndTheWheelChangesWeapon()
    {
        (float yaw, float pitch, int weapon) = Play(new EnhancedInput(mouse: true, gamepad: false));
        Assert.True(yaw < -1f, $"the mouse turned {yaw}");
        Assert.True(pitch > 1f, $"the mouse looked {pitch}");
        Assert.NotEqual(0, weapon);
    }

    [Fact]
    public void WithOnlyTheControllerOnTheMouseKeepsWinQuakesRules()
    {
        (float yaw, _, _) = Play(new EnhancedInput(mouse: false, gamepad: true));
        Assert.Equal(0f, yaw);
    }

    // a held forward key as the move it sends (the arrow key's `+forward`, from frame 120)
    private float Forward(EnhancedInput? input)
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        Directory.CreateDirectory(Path.GetDirectoryName(events)!);
        File.WriteAllText(events, "5 map e1m1\n100 god\n120 @key 128 1\n");
        var run = new TraceRun(baseDir, userdir, ["-traceevents", events, "-oracleframes", "140"]);
        run.Engine.EIN_Set(input);
        foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: false, maxFrames: 140))
        {
        }

        return run.Engine.cl.cmd.forwardmove;
    }

    [Fact]
    public void WalkingIsTheOriginalsAndAlwaysRunMakesRunningTheDefault()
    {
        Assert.Equal(200f, Forward(null));
        Assert.Equal(200f, Forward(new EnhancedInput(true, true)));
        Assert.Equal(400f, Forward(new EnhancedInput(true, true, alwaysRun: true)));
    }

    // The menu pointer (design.md §11.9): over the main menu's third item it is chosen, a click enters the options; there
    // a click on the screen size slider's right half widens it; the right button goes back
    [Fact]
    public void ThePointerChoosesAndClicksMenuItems()
    {
        string baseDir = GameDataLocator.RequireBaseDir();
        Directory.CreateDirectory(Path.GetDirectoryName(events)!);
        File.WriteAllText(events, "5 map e1m1\n100 @key 27 1\n101 @key 27 0\n");
        var run = new TraceRun(baseDir, userdir, ["-traceevents", events, "-oracleframes", "140"]);
        run.Engine.EIN_Set(new EnhancedInput(mouse: true, gamepad: false));
        QuakeEngine e = run.Engine;
        int frame = 0;
        float viewsize = 0;
        foreach (IReadOnlyList<string> _ in run.Frames(quitAfterDemo: false, maxFrames: 140))
        {
            int left = (int)(e.vid.width - 320) >> 1;
            switch (++frame)
            {
                case 110:
                    Assert.Equal(m_state_t.m_main, e.m_state);
                    e.EIN_MenuPoint(left + 150, 32 + (20 * 2) + 10); // Options
                    Assert.Equal(2, e.m_main_cursor);
                    e.EIN_MenuClick(left + 150, 32 + (20 * 2) + 10, 1);
                    break;
                case 115:
                    Assert.Equal(m_state_t.m_options, e.m_state);
                    viewsize = e.scr_viewsize.value;
                    e.EIN_MenuPoint(left + 100, 32 + (8 * 3) + 4); // the screen size slider
                    Assert.Equal(3, e.options_cursor);
                    e.EIN_MenuClick(left + 300, 32 + (8 * 3) + 4, 1);
                    break;
                case 120:
                    Assert.Equal(viewsize + 10, e.scr_viewsize.value);
                    e.EIN_MenuPoint(left + 100, 32 + (8 * 20)); // below the options: nothing
                    Assert.Equal(3, e.options_cursor);
                    e.EIN_MenuClick(0, 0, 2);
                    break;
                case 125:
                    Assert.Equal(m_state_t.m_main, e.m_state);
                    break;
            }
        }

        Assert.Equal(140, frame);
    }
}
