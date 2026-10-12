using static QuakeSharp.Core.Sound.Mci;

namespace QuakeSharp.Core.Sound;

/// <summary>
/// The oracle's CD drive (tools/oracle/src/cd_oracle.c): it answers cd_win.c's MCI commands holding Quake's disc,
/// track 1 data and tracks 2-11 audio. No notification ever comes, so no track ends (design.md §7.2, decision 37).
/// The traces and -recordevents use it; the trace's A line reads its <see cref="State"/>.
/// </summary>
public sealed class VirtualCdDrive : ICdDrive
{
    private const uint ORACLE_CD_DEVICE = 1;
    private const uint ORACLE_CD_TRACKS = 11;

    private const int CD_STOPPED = 0;
    private const int CD_PLAYING = 1;
    private const int CD_PAUSED = 2;

    private bool cd_open; // MCI_OPEN done, MCI_CLOSE not
    private bool cd_door_open;
    private int cd_mode; // CD_STOPPED, CD_PLAYING, CD_PAUSED
    private int cd_track; // the track playing, paused or stopped in (0 before any)

    // every track's length; nothing plays to its end
    private static readonly uint ORACLE_CD_LENGTH = MCI_MAKE_MSF(4, 0, 0);

    /// <summary>The drive's state as the trace has it (cd_oracle.c: Oracle_CDDrive): open, door open, mode, track.</summary>
    public int[] State() => [cd_open ? 1 : 0, cd_door_open ? 1 : 0, cd_mode, cd_track];

    /// <summary>The track it plays, or 0 when it is not playing (for a player that lets it be heard).</summary>
    public int PlayingTrack => cd_open && cd_mode == CD_PLAYING ? cd_track : 0;

    // cd_oracle.c: Status
    private uint Status(uint flags, MciParms parms)
    {
        switch (parms.dwItem)
        {
            case MCI_STATUS_READY:
                parms.dwReturn = cd_door_open ? 0u : 1u;
                return 0;

            case MCI_STATUS_NUMBER_OF_TRACKS:
                if (cd_door_open)
                {
                    return MCIERR_HARDWARE;
                }

                parms.dwReturn = ORACLE_CD_TRACKS;
                return 0;

            case MCI_CDA_STATUS_TYPE_TRACK:
            case MCI_STATUS_LENGTH:
                if (cd_door_open)
                {
                    return MCIERR_HARDWARE;
                }

                if ((flags & MCI_TRACK) == 0 || parms.dwTrack < 1 || parms.dwTrack > ORACLE_CD_TRACKS)
                {
                    return MCIERR_OUTOFRANGE;
                }

                if (parms.dwItem == MCI_STATUS_LENGTH)
                {
                    parms.dwReturn = ORACLE_CD_LENGTH;
                }
                else
                {
                    parms.dwReturn = parms.dwTrack == 1 ? MCI_CDA_TRACK_OTHER : MCI_CDA_TRACK_AUDIO;
                }

                return 0;
        }

        return MCIERR_UNSUPPORTED_FUNCTION;
    }

    // cd_oracle.c: mciSendCommandA
    public uint mciSendCommand(uint id, uint msg, uint flags, MciParms? parms)
    {
        if (msg == MCI_OPEN)
        {
            parms!.wDeviceID = ORACLE_CD_DEVICE;
            cd_open = true;
            return 0;
        }

        if (id != ORACLE_CD_DEVICE || !cd_open)
        {
            return MCIERR_INVALID_DEVICE_ID;
        }

        switch (msg)
        {
            case MCI_SET:
                if ((flags & MCI_SET_DOOR_OPEN) != 0)
                {
                    cd_door_open = true;
                    cd_mode = CD_STOPPED;
                }

                if ((flags & MCI_SET_DOOR_CLOSED) != 0)
                {
                    cd_door_open = false;
                }

                return 0;

            case MCI_STATUS:
                return Status(flags, parms!);

            case MCI_PLAY:
                if (cd_door_open)
                {
                    return MCIERR_HARDWARE;
                }

                if ((flags & MCI_FROM) != 0)
                {
                    cd_track = MCI_TMSF_TRACK(parms!.dwFrom);
                }

                cd_mode = CD_PLAYING;
                return 0;

            case MCI_STOP:
                cd_mode = CD_STOPPED;
                return 0;

            case MCI_PAUSE:
                if (cd_mode == CD_PLAYING)
                {
                    cd_mode = CD_PAUSED;
                }

                return 0;

            case MCI_CLOSE:
                cd_open = false;
                cd_mode = CD_STOPPED;
                return 0;
        }

        return MCIERR_UNSUPPORTED_FUNCTION;
    }

    // the oracle has no window procedure: nothing is ever posted
    public bool PeekNotify(out uint wParam, out uint lParam)
    {
        wParam = 0;
        lParam = 0;
        return false;
    }
}
