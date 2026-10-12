namespace QuakeSharp.Core.Input;

/// <summary>
/// winuser.h and mmsystem.h: what in_win.c uses of Windows' mouse and joystick interfaces, with the SDK's values.
/// </summary>
public static class Win32
{
    // SystemParametersInfo
    public const uint SPI_GETMOUSE = 0x0003;
    public const uint SPI_SETMOUSE = 0x0004;

    // winmm's results
    public const uint JOYERR_NOERROR = 0;
    public const uint JOYERR_BASE = 160;
    public const uint JOYERR_UNPLUGGED = JOYERR_BASE + 7;

    // JOYINFOEX.dwFlags
    public const uint JOY_RETURNX = 0x00000001;
    public const uint JOY_RETURNY = 0x00000002;
    public const uint JOY_RETURNZ = 0x00000004;
    public const uint JOY_RETURNR = 0x00000008;
    public const uint JOY_RETURNU = 0x00000010;
    public const uint JOY_RETURNV = 0x00000020;
    public const uint JOY_RETURNPOV = 0x00000040;
    public const uint JOY_RETURNBUTTONS = 0x00000080;
    public const uint JOY_RETURNCENTERED = 0x00000400;

    // JOYINFOEX.dwPOV
    public const uint JOY_POVCENTERED = 0xFFFF;
    public const uint JOY_POVFORWARD = 0;
    public const uint JOY_POVRIGHT = 9000;
    public const uint JOY_POVBACKWARD = 18000;
    public const uint JOY_POVLEFT = 27000;

    // JOYCAPS.wCaps
    public const uint JOYCAPS_HASPOV = 0x0010;
}

/// <summary>windef.h: RECT, a rectangle whose right and bottom edges are outside it.</summary>
public readonly record struct Rect(int left, int top, int right, int bottom);

/// <summary>mmsystem.h: JOYINFOEX, a joystick's position: the six axes (0 to 65535, centred on 32768), the buttons,
/// the point of view hat.</summary>
public struct JoyInfoEx
{
    public uint dwSize;
    public uint dwFlags;
    public uint dwXpos;
    public uint dwYpos;
    public uint dwZpos;
    public uint dwRpos;
    public uint dwUpos;
    public uint dwVpos;
    public uint dwButtons;
    public uint dwButtonNumber;
    public uint dwPOV;
    public uint dwReserved1;
    public uint dwReserved2;
}

/// <summary>mmsystem.h: JOYCAPS, the fields in_win.c reads.</summary>
public struct JoyCaps
{
    public uint wNumButtons;
    public uint wCaps;
}

/// <summary>
/// The platform's part of the mouse while in_win.c has it (design.md decision 43): ClipCursor confining the cursor to
/// the window is the window taking the mouse (the platform gives relative motion and keeps the pointer in), and
/// ShowCursor's display counter shows or hides the pointer.
/// </summary>
public interface IMouseCapture
{
    /// <summary>The cursor confined to the window (true) or free (false).</summary>
    void ClipCursor(bool clipped);

    /// <summary>The pointer shown (true: the display counter is 0 or more) or hidden.</summary>
    void ShowCursor(bool show);
}

/// <summary>
/// winmm's joystick interface as in_win.c calls it: the platform's game controllers (design.md §8).
/// </summary>
public interface IJoystickDevice
{
    uint joyGetNumDevs();

    uint joyGetPosEx(uint id, ref JoyInfoEx info);

    uint joyGetDevCaps(uint id, out JoyCaps caps);
}
