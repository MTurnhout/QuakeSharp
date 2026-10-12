using System.Collections.Concurrent;
using QuakeSharp.Core.Sound;
using static QuakeSharp.Core.Sound.Mci;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The executable's CD drive (design.md §7.2): it answers cd_win.c's MCI commands as a drive holding a disc of the
/// user's track files (<see cref="CdTracks"/>), track 1 data unless there is a file for it, a track without a file not
/// audio. Each track plays on its own SDL3 stream; at its end, with MCI_NOTIFY, the drive posts MCI_NOTIFY_SUCCESSFUL,
/// which Sys_SendKeyEvents hands to CDAudio_MessageHandler (so a looping track starts again). Without files it is a
/// drive with no disc.
/// </summary>
public sealed class FileCdDrive : ICdDrive, IDisposable
{
    private const uint Device = 1;

    private readonly CdTracks tracks;
    private readonly CdTrackPlayer player = new();

    // MM_MCINOTIFY messages, each with the play it ends: one superseded since is not posted
    private readonly ConcurrentQueue<(uint WParam, int Play)> notifications = new();

    private bool open;
    private bool doorOpen;
    private int track; // the track playing, paused or stopped in (0 before any)
    private bool paused;
    private volatile int play; // counts the plays started, so a track's end belongs to one
    private bool notify; // the play asked for MCI_NOTIFY

    public FileCdDrive(CdTracks tracks)
    {
        this.tracks = tracks;
        player.Ended = () =>
        {
            int ended = play;
            if (notify)
            {
                notifications.Enqueue((MCI_NOTIFY_SUCCESSFUL, ended));
            }
        };
    }

    private bool Ready => !doorOpen && tracks.Count > 0;

    private bool IsAudio(uint t) => tracks.File((int)t) != null;

    public uint mciSendCommand(uint id, uint msg, uint flags, MciParms? parms)
    {
        if (msg == MCI_OPEN)
        {
            parms!.wDeviceID = Device;
            open = true;
            return 0;
        }

        if (id != Device || !open)
        {
            return MCIERR_INVALID_DEVICE_ID;
        }

        switch (msg)
        {
            case MCI_SET:
                if ((flags & MCI_SET_DOOR_OPEN) != 0)
                {
                    Stop();
                    doorOpen = true;
                }

                if ((flags & MCI_SET_DOOR_CLOSED) != 0)
                {
                    doorOpen = false;
                }

                return 0;

            case MCI_STATUS:
                return Status(flags, parms!);

            case MCI_PLAY:
                if (!Ready)
                {
                    return MCIERR_HARDWARE;
                }

                notify = (flags & MCI_NOTIFY) != 0;
                if ((flags & MCI_FROM) != 0 || !player.Loaded)
                {
                    if ((flags & MCI_FROM) != 0)
                    {
                        track = MCI_TMSF_TRACK(parms!.dwFrom);
                    }

                    string? file = tracks.File(track);
                    if (file == null)
                    {
                        return MCIERR_OUTOFRANGE;
                    }

                    play++;
                    player.Play(file, loop: false);
                }
                else
                {
                    play++;
                    player.Resume();
                }

                paused = false;
                return 0;

            case MCI_STOP:
                Stop();
                return 0;

            case MCI_PAUSE:
                if (player.Loaded && !paused)
                {
                    player.Pause();
                    paused = true;
                }

                return 0;

            case MCI_CLOSE:
                Stop();
                open = false;
                return 0;
        }

        return MCIERR_UNSUPPORTED_FUNCTION;
    }

    public bool PeekNotify(out uint wParam, out uint lParam)
    {
        lParam = Device;
        while (notifications.TryDequeue(out (uint WParam, int Play) n))
        {
            if (n.Play == play)
            {
                wParam = n.WParam;
                return true;
            }
        }

        wParam = 0;
        return false;
    }

    public void Dispose() => player.Dispose();

    private void Stop()
    {
        play++;
        player.Stop();
        paused = false;
    }

    private uint Status(uint flags, MciParms parms)
    {
        switch (parms.dwItem)
        {
            case MCI_STATUS_READY:
                parms.dwReturn = Ready ? 1u : 0u;
                return 0;

            case MCI_STATUS_NUMBER_OF_TRACKS:
                if (!Ready)
                {
                    return MCIERR_HARDWARE;
                }

                parms.dwReturn = (uint)tracks.Count;
                return 0;

            case MCI_CDA_STATUS_TYPE_TRACK:
            case MCI_STATUS_LENGTH:
                if (!Ready)
                {
                    return MCIERR_HARDWARE;
                }

                if ((flags & MCI_TRACK) == 0 || parms.dwTrack < 1 || parms.dwTrack > tracks.Count)
                {
                    return MCIERR_OUTOFRANGE;
                }

                if (parms.dwItem == MCI_CDA_STATUS_TYPE_TRACK)
                {
                    parms.dwReturn = IsAudio(parms.dwTrack) ? MCI_CDA_TRACK_AUDIO : MCI_CDA_TRACK_OTHER;
                    return 0;
                }

                // minutes, seconds and frames (75 a second)
                TimeSpan length = IsAudio(parms.dwTrack) ? CdTrackPlayer.Length(tracks.File((int)parms.dwTrack)!) : TimeSpan.Zero;
                long frames = (long)(length.TotalSeconds * 75);
                parms.dwReturn = MCI_MAKE_MSF((uint)Math.Min(frames / (75 * 60), 255), (uint)(frames / 75 % 60), (uint)(frames % 75));
                return 0;
        }

        return MCIERR_UNSUPPORTED_FUNCTION;
    }
}

/// <summary>
/// The CD drive of -recordevents (design.md decision 34): the oracle's virtual disc answers cd_win.c, so the game
/// replays the same in the oracle, and the user's track files are only heard: the track the virtual drive plays, round
/// and round, as no track of the virtual disc ends.
/// </summary>
public sealed class ListeningCdDrive : ICdDrive, IDisposable
{
    private readonly VirtualCdDrive drive = new();
    private readonly CdTracks tracks;
    private readonly CdTrackPlayer player = new();
    private int heard; // the track the player has loaded
    private bool held;

    public ListeningCdDrive(CdTracks tracks)
    {
        this.tracks = tracks;
    }

    public uint mciSendCommand(uint id, uint msg, uint flags, MciParms? parms)
    {
        uint result = drive.mciSendCommand(id, msg, flags, parms);
        int[] state = drive.State(); // open, door open, mode (0 stopped, 1 playing, 2 paused), track
        int mode = state[0] == 0 ? 0 : state[2];
        if (mode == 1)
        {
            if (heard == state[3] && held && (flags & MCI_FROM) == 0)
            {
                player.Resume();
            }
            else if (heard != state[3] || (flags & MCI_FROM) != 0 || !player.Loaded)
            {
                heard = state[3];
                string? file = tracks.File(heard);
                if (file != null)
                {
                    player.Play(file, loop: true);
                }
                else
                {
                    player.Stop();
                }
            }

            held = false;
        }
        else if (mode == 2)
        {
            player.Pause();
            held = true;
        }
        else
        {
            player.Stop();
            heard = 0;
            held = false;
        }

        return result;
    }

    public bool PeekNotify(out uint wParam, out uint lParam) => drive.PeekNotify(out wParam, out lParam);

    public void Dispose() => player.Dispose();
}
