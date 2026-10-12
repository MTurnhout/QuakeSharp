using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.Sound;
using static QuakeSharp.Core.Sound.Mci;

namespace QuakeSharp.Core.Tests;

/// <summary>
/// cd_win.c's port where the traces never go: the oracle's drive never notifies (no track ends) and always opens. A
/// scripted drive (the virtual disc, with notifications posted by the test and an open that can fail) stands in for
/// the file drive (design.md §11.2 step 3).
/// </summary>
public class CdAudioTests
{
    [Fact]
    public void ALoopingTrackStartsAgainAtItsEnd()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out _);

        e.CDAudio_Play(3, true);
        Assert.Equal(3, drive.Inner.PlayingTrack);
        drive.Commands.Clear();

        Notify(e, drive, MCI_NOTIFY_SUCCESSFUL);
        Assert.True(e.cd_playing);
        Assert.Equal(3, e.playTrack);
        Assert.Contains(MCI_PLAY, drive.Commands);
        Assert.Equal(3, drive.Inner.PlayingTrack);
    }

    [Fact]
    public void ATrackPlayedOnceStopsAtItsEnd()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out StringBuilder console);

        e.CDAudio_Play(4, false);
        drive.Commands.Clear();
        Notify(e, drive, MCI_NOTIFY_SUCCESSFUL);
        Assert.False(e.cd_playing);
        Assert.Empty(drive.Commands);

        console.Clear();
        Run(e, "cd info");
        Assert.Equal("11 tracks\nVolume is 1.000000\n", console.ToString());
    }

    [Fact]
    public void AFailureNotificationStopsTheCd()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out _);

        e.CDAudio_Play(2, true);
        Notify(e, drive, MCI_NOTIFY_FAILURE);
        Assert.False(e.cd_playing);
        Assert.False(e.cdValid);
        Assert.Contains(MCI_STOP, drive.Commands);
    }

    [Fact]
    public void NotificationsOfAnotherDeviceAreNotTheCds()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out _);

        e.CDAudio_Play(2, false);
        Assert.Equal(1, e.CDAudio_MessageHandler(MCI_NOTIFY_SUCCESSFUL, e.wDeviceID + 1));
        Assert.True(e.cd_playing);
    }

    [Fact]
    public void WithoutADriveOpeningFails()
    {
        QuakeEngine e = Engine(null, out StringBuilder console);

        Assert.Contains("CDAudio_Init: MCI_OPEN failed (266)\n", console.ToString(), StringComparison.Ordinal);
        Assert.False(e.cd_enabled);
        e.CDAudio_Play(2, true);
        Assert.False(e.cd_playing);
    }

    [Fact]
    public void ADriveThatCannotOpenIsLeftAlone()
    {
        var drive = new ScriptedCdDrive { FailOpen = true };
        QuakeEngine e = Engine(drive, out StringBuilder console);

        Assert.Contains("CDAudio_Init: MCI_OPEN failed (262)\n", console.ToString(), StringComparison.Ordinal);
        Assert.Equal([MCI_OPEN], drive.Commands);
    }

    [Fact]
    public void TheMusicVolumeIsOnOrOff()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out _);

        e.CDAudio_Play(2, true);
        Assert.Equal(1f, e.cdvolume);

        e.Cvar_SetValue("bgmvolume", 0.5f);
        e.CDAudio_Update(); // any change from on is off, and pauses
        Assert.Equal(0f, e.bgmvolume.value);
        Assert.Equal(0f, e.cdvolume);
        Assert.False(e.cd_playing);
        Assert.Equal(0, drive.Inner.PlayingTrack);

        e.Cvar_SetValue("bgmvolume", 0.25f);
        e.CDAudio_Update(); // any change from off is on, and resumes
        Assert.Equal(1f, e.bgmvolume.value);
        Assert.True(e.cd_playing);
        Assert.Equal(2, drive.Inner.PlayingTrack);
    }

    [Fact]
    public void RemappedTracksPlayTheirTargets()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out StringBuilder console);

        Run(e, "cd remap 5 6");
        e.CDAudio_Play(2, true);
        Assert.Equal(6, e.playTrack);

        console.Clear();
        Run(e, "cd remap");
        Assert.Equal("  1 -> 5\n  2 -> 6\n", console.ToString());

        // track 1 is the disc's data
        console.Clear();
        Run(e, "cd remap 1 1");
        e.CDAudio_Play(1, false);
        Assert.Equal("CDAudio: track 1 is not audio\n", console.ToString());
    }

    [Fact]
    public void AnEjectedDiscIsNotInThePlayer()
    {
        var drive = new ScriptedCdDrive();
        QuakeEngine e = Engine(drive, out StringBuilder console);

        e.CDAudio_Play(2, true);
        Run(e, "cd eject");
        Assert.False(e.cd_playing);
        Assert.False(e.cdValid);

        console.Clear();
        Run(e, "cd play 3");
        Assert.Equal("No CD in player.\n", console.ToString());

        Run(e, "cd close");
        Run(e, "cd play 3");
        Assert.Equal(3, drive.Inner.PlayingTrack);
    }

    private static void Notify(QuakeEngine e, ScriptedCdDrive drive, uint wParam)
    {
        drive.Post(wParam);
        while (drive.PeekNotify(out uint w, out uint l))
        {
            e.CDAudio_MessageHandler(w, l);
        }
    }

    private static void Run(QuakeEngine e, string command)
    {
        e.Cbuf_AddText(command + "\n");
        e.Cbuf_Execute();
    }

    private static QuakeEngine Engine(ICdDrive? drive, out StringBuilder console)
    {
        var text = new StringBuilder();
        console = text;
        var e = new QuakeEngine(new QuakeParms(".", new CommandLine(["quake"])), new VirtualSystem(t => text.Append(t)), cd: drive);
        e.Memory_Init();
        e.Cbuf_Init();
        e.Cmd_Init();
        e.Host_InitLocal();
        e.Cvar_RegisterVariable(e.bgmvolume);
        e.CDAudio_Init();
        e.CDAudio_Update(); // as the first frame does: cdvolume starts at 0, so bgmvolume's 1 turns the music on
        return e;
    }

    // the virtual disc, with the notifications the test posts and an open that can fail; it notes the commands sent
    private sealed class ScriptedCdDrive : ICdDrive
    {
        private readonly Queue<uint> notifications = new();
        private uint device;

        public VirtualCdDrive Inner { get; } = new();

        public bool FailOpen { get; init; }

        public List<uint> Commands { get; } = [];

        public void Post(uint wParam) => notifications.Enqueue(wParam);

        public uint mciSendCommand(uint id, uint msg, uint flags, MciParms? parms)
        {
            Commands.Add(msg);
            if (msg == MCI_OPEN && FailOpen)
            {
                return MCIERR_HARDWARE;
            }

            uint result = Inner.mciSendCommand(id, msg, flags, parms);
            if (msg == MCI_OPEN)
            {
                device = parms!.wDeviceID;
            }

            return result;
        }

        public bool PeekNotify(out uint wParam, out uint lParam)
        {
            lParam = device;
            return notifications.TryDequeue(out wParam);
        }
    }
}
