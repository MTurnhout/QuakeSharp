namespace QuakeSharp.Core.Sound;

/// <summary>
/// mmsystem.h: what cd_win.c uses of MCI, the Windows multimedia interface that played the CD (design.md §7.2): the
/// commands, their flags and items, the error codes and the notifications, with the SDK's values.
/// </summary>
public static class Mci
{
    // commands
    public const uint MCI_OPEN = 0x0803;
    public const uint MCI_CLOSE = 0x0804;
    public const uint MCI_PLAY = 0x0806;
    public const uint MCI_STOP = 0x0808;
    public const uint MCI_PAUSE = 0x0809;
    public const uint MCI_SET = 0x080D;
    public const uint MCI_STATUS = 0x0814;

    // flags
    public const uint MCI_NOTIFY = 0x00000001;
    public const uint MCI_WAIT = 0x00000002;
    public const uint MCI_FROM = 0x00000004;
    public const uint MCI_TO = 0x00000008;
    public const uint MCI_TRACK = 0x00000010;
    public const uint MCI_OPEN_SHAREABLE = 0x00000100;
    public const uint MCI_OPEN_TYPE = 0x00002000;
    public const uint MCI_SET_DOOR_OPEN = 0x00000100;
    public const uint MCI_SET_DOOR_CLOSED = 0x00000200;
    public const uint MCI_SET_TIME_FORMAT = 0x00000400;
    public const uint MCI_STATUS_ITEM = 0x00000100;

    // status items and their values
    public const uint MCI_STATUS_LENGTH = 0x00000001;
    public const uint MCI_STATUS_NUMBER_OF_TRACKS = 0x00000003;
    public const uint MCI_STATUS_READY = 0x00000007;
    public const uint MCI_CDA_STATUS_TYPE_TRACK = 0x00004001;
    public const uint MCI_CD_OFFSET = 1088;
    public const uint MCI_CDA_TRACK_AUDIO = MCI_CD_OFFSET + 0;
    public const uint MCI_CDA_TRACK_OTHER = MCI_CD_OFFSET + 1;
    public const uint MCI_FORMAT_TMSF = 10;

    // error codes (0 is success)
    public const uint MCIERR_BASE = 256;
    public const uint MCIERR_INVALID_DEVICE_ID = MCIERR_BASE + 1;
    public const uint MCIERR_HARDWARE = MCIERR_BASE + 6;
    public const uint MCIERR_CANNOT_LOAD_DRIVER = MCIERR_BASE + 10;
    public const uint MCIERR_UNSUPPORTED_FUNCTION = MCIERR_BASE + 18;
    public const uint MCIERR_OUTOFRANGE = MCIERR_BASE + 26;

    // MM_MCINOTIFY's wParam
    public const uint MCI_NOTIFY_SUCCESSFUL = 0x0001;
    public const uint MCI_NOTIFY_SUPERSEDED = 0x0002;
    public const uint MCI_NOTIFY_ABORTED = 0x0004;
    public const uint MCI_NOTIFY_FAILURE = 0x0008;

    public static uint MCI_MAKE_MSF(uint m, uint s, uint f) => (byte)m | ((uint)(byte)s << 8) | ((uint)(byte)f << 16);

    public static uint MCI_MAKE_TMSF(uint t, uint m, uint s, uint f) =>
        (byte)t | ((uint)(byte)m << 8) | ((uint)(byte)s << 16) | ((uint)(byte)f << 24);

    public static byte MCI_TMSF_TRACK(uint tmsf) => (byte)tmsf;
}

/// <summary>
/// The parameter block of an MCI command: the fields cd_win.c uses of MCI_OPEN_PARMS, MCI_SET_PARMS,
/// MCI_STATUS_PARMS, MCI_PLAY_PARMS and MCI_GENERIC_PARMS, under their names.
/// </summary>
public sealed class MciParms
{
    public uint dwCallback;

    // MCI_OPEN_PARMS
    public uint wDeviceID;
    public string? lpstrDeviceType;

    // MCI_SET_PARMS
    public uint dwTimeFormat;

    // MCI_STATUS_PARMS
    public uint dwReturn;
    public uint dwItem;
    public uint dwTrack;

    // MCI_PLAY_PARMS
    public uint dwFrom;
    public uint dwTo;
}

/// <summary>
/// A CD drive as cd_win.c drives it: mciSendCommand, and the MM_MCINOTIFY messages the drive posts to the window
/// (design.md §7.2, decision 37).
/// </summary>
public interface ICdDrive
{
    // mciSendCommand: 0, or MCI's error code
    uint mciSendCommand(uint id, uint msg, uint flags, MciParms? parms);

    // the next MM_MCINOTIFY message the drive has posted (wParam: MCI_NOTIFY_*, lParam: the device), if any
    bool PeekNotify(out uint wParam, out uint lParam);
}
