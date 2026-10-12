namespace QuakeSharp.Core.Input;

/// <summary>
/// The Windows in_win.c talks to (design.md decision 43), as the oracle's tools/oracle/src/in_oracle.c has it: a screen
/// of 1920x1080 with a cursor that the window's mouse messages move, confined to the screen or to the rectangle
/// ClipCursor gives, as Windows confines it (the right and bottom edges excluded); ShowCursor's display counter; the
/// system's mouse parameters, Windows' defaults; and winmm's joysticks: the platform's (<see cref="IJoystickDevice"/>)
/// or none (16 slots, each unplugged, as the oracle's). A platform that takes the mouse while the cursor is clipped
/// gets told through <see cref="IMouseCapture"/>.
/// </summary>
public sealed class VirtualInput
{
    public const int ScreenWidth = 1920;
    public const int ScreenHeight = 1080;
    private const uint Joysticks = 16;

    // RegisterWindowMessage's number for "MSWHEEL_ROLLMSG"
    private const uint WheelMessage = 0xc0f0;

    private readonly IMouseCapture? capture;
    private readonly IJoystickDevice? joysticks;
    private readonly int[] mouseParms = [6, 10, 1];
    private Rect? clip;

    public VirtualInput(IMouseCapture? capture = null, IJoystickDevice? joysticks = null)
    {
        this.capture = capture;
        this.joysticks = joysticks;
    }

    public int CursorX { get; private set; } = ScreenWidth / 2;

    public int CursorY { get; private set; } = ScreenHeight / 2;

    /// <summary>The mouse buttons held, as the window's messages give them: 1 left, 2 right, 4 middle.</summary>
    public int Buttons { get; set; }

    public int CursorCount { get; private set; }

    public bool Captured { get; private set; }

    /// <summary>The trace's I line: the cursor, the clip rectangle (or zeros), the buttons, the display counter, the
    /// capture, the mouse parameters, as the oracle's Oracle_InputDevice gives them.</summary>
    public IEnumerable<int> State()
    {
        yield return CursorX;
        yield return CursorY;
        yield return clip != null ? 1 : 0;
        yield return clip?.left ?? 0;
        yield return clip?.top ?? 0;
        yield return clip?.right ?? 0;
        yield return clip?.bottom ?? 0;
        yield return Buttons;
        yield return CursorCount;
        yield return Captured ? 1 : 0;
        yield return mouseParms[0];
        yield return mouseParms[1];
        yield return mouseParms[2];
    }

    /// <summary>The mouse moved the cursor by (dx, dy), before it is confined.</summary>
    public void Move(int dx, int dy)
    {
        CursorX += dx;
        CursorY += dy;
        Confine();
    }

    private void Confine()
    {
        int left = 0, top = 0, right = ScreenWidth, bottom = ScreenHeight;

        if (clip is Rect r)
        {
            left = Math.Max(left, r.left);
            top = Math.Max(top, r.top);
            right = Math.Min(right, r.right);
            bottom = Math.Min(bottom, r.bottom);
        }

        if (CursorX >= right)
        {
            CursorX = right - 1;
        }

        if (CursorX < left)
        {
            CursorX = left;
        }

        if (CursorY >= bottom)
        {
            CursorY = bottom - 1;
        }

        if (CursorY < top)
        {
            CursorY = top;
        }
    }

    // winuser.h's functions in_win.c calls

    public void GetCursorPos(out int x, out int y)
    {
        x = CursorX;
        y = CursorY;
    }

    public bool SetCursorPos(int x, int y)
    {
        CursorX = x;
        CursorY = y;
        Confine();
        return true;
    }

    public bool ClipCursor(Rect? rect)
    {
        clip = rect;
        Confine();
        capture?.ClipCursor(rect != null);
        return true;
    }

    public int ShowCursor(bool show)
    {
        CursorCount += show ? 1 : -1;
        capture?.ShowCursor(CursorCount >= 0);
        return CursorCount;
    }

    public void SetCapture() => Captured = true;

    public bool ReleaseCapture()
    {
        Captured = false;
        return true;
    }

    // SPI_GETMOUSE and SPI_SETMOUSE: the thresholds and the acceleration
    public bool SystemParametersInfo(uint action, int[] parms)
    {
        switch (action)
        {
            case Win32.SPI_GETMOUSE:
                mouseParms.CopyTo(parms, 0);
                return true;
            case Win32.SPI_SETMOUSE:
                parms.AsSpan(0, mouseParms.Length).CopyTo(mouseParms);
                return true;
        }

        return false;
    }

    public static uint RegisterWindowMessage(string name)
    {
        _ = name;
        return WheelMessage;
    }

    // mmsystem.h's joystick functions

    public uint joyGetNumDevs() => joysticks?.joyGetNumDevs() ?? Joysticks;

    public uint joyGetPosEx(uint id, ref JoyInfoEx info) =>
        joysticks?.joyGetPosEx(id, ref info) ?? Win32.JOYERR_UNPLUGGED;

    public uint joyGetDevCaps(uint id, out JoyCaps caps)
    {
        if (joysticks != null)
        {
            return joysticks.joyGetDevCaps(id, out caps);
        }

        caps = default;
        return Win32.JOYERR_UNPLUGGED;
    }
}
