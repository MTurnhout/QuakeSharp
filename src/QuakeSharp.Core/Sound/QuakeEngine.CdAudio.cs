using QuakeSharp.Core.Client;
using QuakeSharp.Core.Sound;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Sound.Mci;

namespace QuakeSharp.Core;

// cd_win.c: the CD's music, played by the drive through MCI: an ICdDrive answers the commands (design.md §7.2,
// decision 37). Without a drive, MCI_OPEN fails as on a machine with none.
public sealed partial class QuakeEngine
{
    // cd_win.c's statics (initialized, enabled, playing and remap named cd_*: other modules have those names)
    public bool cdValid;
    public bool cd_playing;
    public bool wasPlaying;
    public bool cd_initialized;
    public bool cd_enabled;
    public bool playLooping;
    public float cdvolume;
    public readonly byte[] cd_remap = new byte[100];
    public byte cdrom;
    public byte playTrack;
    public byte maxTrack;

    // cd_win.c: wDeviceID
    public uint wDeviceID;

    // mmsystem.h: mciSendCommand, to the drive (without one, opening it fails as winmm's does with no CD driver)
    private uint mciSendCommand(uint id, uint msg, uint flags, MciParms? parms) =>
        cddrive?.mciSendCommand(id, msg, flags, parms) ?? MCIERR_CANNOT_LOAD_DRIVER;

    // cd_win.c: CDAudio_Eject
    private void CDAudio_Eject()
    {
        uint dwReturn;

        if ((dwReturn = mciSendCommand(wDeviceID, MCI_SET, MCI_SET_DOOR_OPEN, null)) != 0)
        {
            Con_DPrintf("MCI_SET_DOOR_OPEN failed (%i)\n", dwReturn);
        }
    }

    // cd_win.c: CDAudio_CloseDoor
    private void CDAudio_CloseDoor()
    {
        uint dwReturn;

        if ((dwReturn = mciSendCommand(wDeviceID, MCI_SET, MCI_SET_DOOR_CLOSED, null)) != 0)
        {
            Con_DPrintf("MCI_SET_DOOR_CLOSED failed (%i)\n", dwReturn);
        }
    }

    // cd_win.c: CDAudio_GetAudioDiskInfo
    private int CDAudio_GetAudioDiskInfo()
    {
        uint dwReturn;
        var mciStatusParms = new MciParms();

        cdValid = false;

        mciStatusParms.dwItem = MCI_STATUS_READY;
        dwReturn = mciSendCommand(wDeviceID, MCI_STATUS, MCI_STATUS_ITEM | MCI_WAIT, mciStatusParms);
        if (dwReturn != 0)
        {
            Con_DPrintf("CDAudio: drive ready test - get status failed\n");
            return -1;
        }

        if (mciStatusParms.dwReturn == 0)
        {
            Con_DPrintf("CDAudio: drive not ready\n");
            return -1;
        }

        mciStatusParms.dwItem = MCI_STATUS_NUMBER_OF_TRACKS;
        dwReturn = mciSendCommand(wDeviceID, MCI_STATUS, MCI_STATUS_ITEM | MCI_WAIT, mciStatusParms);
        if (dwReturn != 0)
        {
            Con_DPrintf("CDAudio: get tracks - status failed\n");
            return -1;
        }

        if (mciStatusParms.dwReturn < 1)
        {
            Con_DPrintf("CDAudio: no music tracks\n");
            return -1;
        }

        cdValid = true;
        maxTrack = (byte)mciStatusParms.dwReturn;

        return 0;
    }

    // cd_win.c: CDAudio_Play
    public void CDAudio_Play(byte track, bool looping)
    {
        uint dwReturn;
        var mciPlayParms = new MciParms();
        var mciStatusParms = new MciParms();

        if (!cd_enabled)
        {
            return;
        }

        if (!cdValid)
        {
            CDAudio_GetAudioDiskInfo();
            if (!cdValid)
            {
                return;
            }
        }

        track = cd_remap[track];

        if (track < 1 || track > maxTrack)
        {
            Con_DPrintf("CDAudio: Bad track number %u.\n", (int)track);
            return;
        }

        // don't try to play a non-audio track
        mciStatusParms.dwItem = MCI_CDA_STATUS_TYPE_TRACK;
        mciStatusParms.dwTrack = track;
        dwReturn = mciSendCommand(wDeviceID, MCI_STATUS, MCI_STATUS_ITEM | MCI_TRACK | MCI_WAIT, mciStatusParms);
        if (dwReturn != 0)
        {
            Con_DPrintf("MCI_STATUS failed (%i)\n", dwReturn);
            return;
        }

        if (mciStatusParms.dwReturn != MCI_CDA_TRACK_AUDIO)
        {
            Con_Printf("CDAudio: track %i is not audio\n", (int)track);
            return;
        }

        // get the length of the track to be played
        mciStatusParms.dwItem = MCI_STATUS_LENGTH;
        mciStatusParms.dwTrack = track;
        dwReturn = mciSendCommand(wDeviceID, MCI_STATUS, MCI_STATUS_ITEM | MCI_TRACK | MCI_WAIT, mciStatusParms);
        if (dwReturn != 0)
        {
            Con_DPrintf("MCI_STATUS failed (%i)\n", dwReturn);
            return;
        }

        if (cd_playing)
        {
            if (playTrack == track)
            {
                return;
            }

            CDAudio_Stop();
        }

        mciPlayParms.dwFrom = MCI_MAKE_TMSF(track, 0, 0, 0);
        mciPlayParms.dwTo = (mciStatusParms.dwReturn << 8) | track;
        mciPlayParms.dwCallback = 0; // mainwindow
        dwReturn = mciSendCommand(wDeviceID, MCI_PLAY, MCI_NOTIFY | MCI_FROM | MCI_TO, mciPlayParms);
        if (dwReturn != 0)
        {
            Con_DPrintf("CDAudio: MCI_PLAY failed (%i)\n", dwReturn);
            return;
        }

        playLooping = looping;
        playTrack = track;
        cd_playing = true;

        if (cdvolume == 0.0)
        {
            CDAudio_Pause();
        }
    }

    // cd_win.c: CDAudio_Stop
    public void CDAudio_Stop()
    {
        uint dwReturn;

        if (!cd_enabled)
        {
            return;
        }

        if (!cd_playing)
        {
            return;
        }

        if ((dwReturn = mciSendCommand(wDeviceID, MCI_STOP, 0, null)) != 0)
        {
            Con_DPrintf("MCI_STOP failed (%i)", dwReturn);
        }

        wasPlaying = false;
        cd_playing = false;
    }

    // cd_win.c: CDAudio_Pause
    public void CDAudio_Pause()
    {
        uint dwReturn;
        var mciGenericParms = new MciParms();

        if (!cd_enabled)
        {
            return;
        }

        if (!cd_playing)
        {
            return;
        }

        mciGenericParms.dwCallback = 0; // mainwindow
        if ((dwReturn = mciSendCommand(wDeviceID, MCI_PAUSE, 0, mciGenericParms)) != 0)
        {
            Con_DPrintf("MCI_PAUSE failed (%i)", dwReturn);
        }

        wasPlaying = cd_playing;
        cd_playing = false;
    }

    // cd_win.c: CDAudio_Resume
    public void CDAudio_Resume()
    {
        uint dwReturn;
        var mciPlayParms = new MciParms();

        if (!cd_enabled)
        {
            return;
        }

        if (!cdValid)
        {
            return;
        }

        if (!wasPlaying)
        {
            return;
        }

        mciPlayParms.dwFrom = MCI_MAKE_TMSF(playTrack, 0, 0, 0);
        mciPlayParms.dwTo = MCI_MAKE_TMSF((uint)(playTrack + 1), 0, 0, 0);
        mciPlayParms.dwCallback = 0; // mainwindow
        dwReturn = mciSendCommand(wDeviceID, MCI_PLAY, MCI_TO | MCI_NOTIFY, mciPlayParms);
        if (dwReturn != 0)
        {
            Con_DPrintf("CDAudio: MCI_PLAY failed (%i)\n", dwReturn);
            return;
        }

        cd_playing = true;
    }

    // cd_win.c: CD_f
    private void CD_f()
    {
        string command;
        int ret;
        int n;

        if (Cmd_Argc() < 2)
        {
            return;
        }

        command = Cmd_Argv(1);

        if (Q_strcasecmp(command, "on") == 0)
        {
            cd_enabled = true;
            return;
        }

        if (Q_strcasecmp(command, "off") == 0)
        {
            if (cd_playing)
            {
                CDAudio_Stop();
            }

            cd_enabled = false;
            return;
        }

        if (Q_strcasecmp(command, "reset") == 0)
        {
            cd_enabled = true;
            if (cd_playing)
            {
                CDAudio_Stop();
            }

            for (n = 0; n < 100; n++)
            {
                cd_remap[n] = (byte)n;
            }

            CDAudio_GetAudioDiskInfo();
            return;
        }

        if (Q_strcasecmp(command, "remap") == 0)
        {
            ret = Cmd_Argc() - 2;
            if (ret <= 0)
            {
                for (n = 1; n < 100; n++)
                {
                    if (cd_remap[n] != n)
                    {
                        Con_Printf("  %u -> %u\n", n, (int)cd_remap[n]);
                    }
                }

                return;
            }

            for (n = 1; n <= ret; n++)
            {
                cd_remap[n] = (byte)Q_atoi(Cmd_Argv(n + 1));
            }

            return;
        }

        if (Q_strcasecmp(command, "close") == 0)
        {
            CDAudio_CloseDoor();
            return;
        }

        if (!cdValid)
        {
            CDAudio_GetAudioDiskInfo();
            if (!cdValid)
            {
                Con_Printf("No CD in player.\n");
                return;
            }
        }

        if (Q_strcasecmp(command, "play") == 0)
        {
            CDAudio_Play((byte)Q_atoi(Cmd_Argv(2)), false);
            return;
        }

        if (Q_strcasecmp(command, "loop") == 0)
        {
            CDAudio_Play((byte)Q_atoi(Cmd_Argv(2)), true);
            return;
        }

        if (Q_strcasecmp(command, "stop") == 0)
        {
            CDAudio_Stop();
            return;
        }

        if (Q_strcasecmp(command, "pause") == 0)
        {
            CDAudio_Pause();
            return;
        }

        if (Q_strcasecmp(command, "resume") == 0)
        {
            CDAudio_Resume();
            return;
        }

        if (Q_strcasecmp(command, "eject") == 0)
        {
            if (cd_playing)
            {
                CDAudio_Stop();
            }

            CDAudio_Eject();
            cdValid = false;
            return;
        }

        if (Q_strcasecmp(command, "info") == 0)
        {
            Con_Printf("%u tracks\n", (int)maxTrack);
            if (cd_playing)
            {
                Con_Printf("Currently %s track %u\n", playLooping ? "looping" : "playing", (int)playTrack);
            }
            else if (wasPlaying)
            {
                Con_Printf("Paused %s track %u\n", playLooping ? "looping" : "playing", (int)playTrack);
            }

            Con_Printf("Volume is %f\n", cdvolume);
            return;
        }
    }

    // cd_win.c: CDAudio_MessageHandler: an MM_MCINOTIFY message the window procedure got (lParam: the device);
    // 0 when handled
    public int CDAudio_MessageHandler(uint wParam, uint lParam)
    {
        if (lParam != wDeviceID)
        {
            return 1;
        }

        switch (wParam)
        {
            case MCI_NOTIFY_SUCCESSFUL:
                if (cd_playing)
                {
                    cd_playing = false;
                    if (playLooping)
                    {
                        CDAudio_Play(playTrack, true);
                    }
                }

                break;

            case MCI_NOTIFY_ABORTED:
            case MCI_NOTIFY_SUPERSEDED:
                break;

            case MCI_NOTIFY_FAILURE:
                Con_DPrintf("MCI_NOTIFY_FAILURE\n");
                CDAudio_Stop();
                cdValid = false;
                break;

            default:
                Con_DPrintf("Unexpected MM_MCINOTIFY type (%i)\n", wParam);
                return 1;
        }

        return 0;
    }

    // cd_win.c: CDAudio_Update
    public void CDAudio_Update()
    {
        if (!cd_enabled)
        {
            return;
        }

        if (bgmvolume.value != cdvolume)
        {
            if (cdvolume != 0)
            {
                Cvar_SetValue("bgmvolume", 0.0f);
                cdvolume = bgmvolume.value;
                CDAudio_Pause();
            }
            else
            {
                Cvar_SetValue("bgmvolume", 1.0f);
                cdvolume = bgmvolume.value;
                CDAudio_Resume();
            }
        }
    }

    // cd_win.c: CDAudio_Init
    public int CDAudio_Init()
    {
        uint dwReturn;
        var mciOpenParms = new MciParms();
        var mciSetParms = new MciParms();
        int n;

        if (cls.state == cactive_t.ca_dedicated)
        {
            return -1;
        }

        if (COM_CheckParm("-nocdaudio") != 0)
        {
            return -1;
        }

        mciOpenParms.lpstrDeviceType = "cdaudio";
        if ((dwReturn = mciSendCommand(0, MCI_OPEN, MCI_OPEN_TYPE | MCI_OPEN_SHAREABLE, mciOpenParms)) != 0)
        {
            Con_Printf("CDAudio_Init: MCI_OPEN failed (%i)\n", dwReturn);
            return -1;
        }

        wDeviceID = mciOpenParms.wDeviceID;

        // Set the time format to track/minute/second/frame (TMSF).
        mciSetParms.dwTimeFormat = MCI_FORMAT_TMSF;
        if ((dwReturn = mciSendCommand(wDeviceID, MCI_SET, MCI_SET_TIME_FORMAT, mciSetParms)) != 0)
        {
            Con_Printf("MCI_SET_TIME_FORMAT failed (%i)\n", dwReturn);
            mciSendCommand(wDeviceID, MCI_CLOSE, 0, null);
            return -1;
        }

        for (n = 0; n < 100; n++)
        {
            cd_remap[n] = (byte)n;
        }

        cd_initialized = true;
        cd_enabled = true;

        if (CDAudio_GetAudioDiskInfo() != 0)
        {
            Con_Printf("CDAudio_Init: No CD in player.\n");
            cdValid = false;
        }

        Cmd_AddCommand("cd", CD_f);

        Con_Printf("CD Audio Initialized\n");

        return 0;
    }

    // cd_win.c: CDAudio_Shutdown
    public void CDAudio_Shutdown()
    {
        if (!cd_initialized)
        {
            return;
        }

        CDAudio_Stop();
        if (mciSendCommand(wDeviceID, MCI_CLOSE, MCI_WAIT, null) != 0)
        {
            Con_DPrintf("CDAudio_Shutdown: MCI_CLOSE failed\n");
        }
    }
}
