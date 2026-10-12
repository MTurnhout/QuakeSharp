using SDL;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// vid_win.c: MapKey. WinQuake reads the PC scan code of a key message (set 1, without the e0 prefix of the extended
/// keys) and looks it up in scantokey: keys are read by position, the typing keys give the US layout's characters on
/// any keyboard, and an extended key gives what its unprefixed twin does (the arrows the keypad's, right Ctrl Ctrl,
/// NumLock K_PAUSE as Pause). SDL scan codes are positions too; they are turned back into PC scan codes here.
/// </summary>
internal static partial class KeyTranslation
{
    // vid_win.c: MapKey
    public static int MapKey(SDL_Scancode scancode)
    {
        int key = PcScanCode(scancode);
        if (key > 127)
        {
            return 0;
        }

        return scantokey[key];
    }

    // The PC scan code (set 1) of a key, without the e0 prefix; 0 for keys a PC keyboard does not have
    private static int PcScanCode(SDL_Scancode scancode) => scancode switch
    {
        SDL_Scancode.SDL_SCANCODE_ESCAPE => 0x01,
        >= SDL_Scancode.SDL_SCANCODE_1 and <= SDL_Scancode.SDL_SCANCODE_9 => 0x02 + (scancode - SDL_Scancode.SDL_SCANCODE_1),
        SDL_Scancode.SDL_SCANCODE_0 => 0x0b,
        SDL_Scancode.SDL_SCANCODE_MINUS => 0x0c,
        SDL_Scancode.SDL_SCANCODE_EQUALS => 0x0d,
        SDL_Scancode.SDL_SCANCODE_BACKSPACE => 0x0e,
        SDL_Scancode.SDL_SCANCODE_TAB => 0x0f,
        SDL_Scancode.SDL_SCANCODE_Q => 0x10,
        SDL_Scancode.SDL_SCANCODE_W => 0x11,
        SDL_Scancode.SDL_SCANCODE_E => 0x12,
        SDL_Scancode.SDL_SCANCODE_R => 0x13,
        SDL_Scancode.SDL_SCANCODE_T => 0x14,
        SDL_Scancode.SDL_SCANCODE_Y => 0x15,
        SDL_Scancode.SDL_SCANCODE_U => 0x16,
        SDL_Scancode.SDL_SCANCODE_I => 0x17,
        SDL_Scancode.SDL_SCANCODE_O => 0x18,
        SDL_Scancode.SDL_SCANCODE_P => 0x19,
        SDL_Scancode.SDL_SCANCODE_LEFTBRACKET => 0x1a,
        SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET => 0x1b,
        SDL_Scancode.SDL_SCANCODE_RETURN or SDL_Scancode.SDL_SCANCODE_KP_ENTER => 0x1c,
        SDL_Scancode.SDL_SCANCODE_LCTRL or SDL_Scancode.SDL_SCANCODE_RCTRL => 0x1d,
        SDL_Scancode.SDL_SCANCODE_A => 0x1e,
        SDL_Scancode.SDL_SCANCODE_S => 0x1f,
        SDL_Scancode.SDL_SCANCODE_D => 0x20,
        SDL_Scancode.SDL_SCANCODE_F => 0x21,
        SDL_Scancode.SDL_SCANCODE_G => 0x22,
        SDL_Scancode.SDL_SCANCODE_H => 0x23,
        SDL_Scancode.SDL_SCANCODE_J => 0x24,
        SDL_Scancode.SDL_SCANCODE_K => 0x25,
        SDL_Scancode.SDL_SCANCODE_L => 0x26,
        SDL_Scancode.SDL_SCANCODE_SEMICOLON => 0x27,
        SDL_Scancode.SDL_SCANCODE_APOSTROPHE => 0x28,
        SDL_Scancode.SDL_SCANCODE_GRAVE => 0x29,
        SDL_Scancode.SDL_SCANCODE_LSHIFT => 0x2a,
        SDL_Scancode.SDL_SCANCODE_BACKSLASH or SDL_Scancode.SDL_SCANCODE_NONUSHASH => 0x2b,
        SDL_Scancode.SDL_SCANCODE_Z => 0x2c,
        SDL_Scancode.SDL_SCANCODE_X => 0x2d,
        SDL_Scancode.SDL_SCANCODE_C => 0x2e,
        SDL_Scancode.SDL_SCANCODE_V => 0x2f,
        SDL_Scancode.SDL_SCANCODE_B => 0x30,
        SDL_Scancode.SDL_SCANCODE_N => 0x31,
        SDL_Scancode.SDL_SCANCODE_M => 0x32,
        SDL_Scancode.SDL_SCANCODE_COMMA => 0x33,
        SDL_Scancode.SDL_SCANCODE_PERIOD => 0x34,
        SDL_Scancode.SDL_SCANCODE_SLASH or SDL_Scancode.SDL_SCANCODE_KP_DIVIDE => 0x35,
        SDL_Scancode.SDL_SCANCODE_RSHIFT => 0x36,
        SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY or SDL_Scancode.SDL_SCANCODE_PRINTSCREEN => 0x37,
        SDL_Scancode.SDL_SCANCODE_LALT or SDL_Scancode.SDL_SCANCODE_RALT => 0x38,
        SDL_Scancode.SDL_SCANCODE_SPACE => 0x39,
        SDL_Scancode.SDL_SCANCODE_CAPSLOCK => 0x3a,
        >= SDL_Scancode.SDL_SCANCODE_F1 and <= SDL_Scancode.SDL_SCANCODE_F10 => 0x3b + (scancode - SDL_Scancode.SDL_SCANCODE_F1),
        SDL_Scancode.SDL_SCANCODE_PAUSE or SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR => 0x45,
        SDL_Scancode.SDL_SCANCODE_SCROLLLOCK => 0x46,
        SDL_Scancode.SDL_SCANCODE_KP_7 or SDL_Scancode.SDL_SCANCODE_HOME => 0x47,
        SDL_Scancode.SDL_SCANCODE_KP_8 or SDL_Scancode.SDL_SCANCODE_UP => 0x48,
        SDL_Scancode.SDL_SCANCODE_KP_9 or SDL_Scancode.SDL_SCANCODE_PAGEUP => 0x49,
        SDL_Scancode.SDL_SCANCODE_KP_MINUS => 0x4a,
        SDL_Scancode.SDL_SCANCODE_KP_4 or SDL_Scancode.SDL_SCANCODE_LEFT => 0x4b,
        SDL_Scancode.SDL_SCANCODE_KP_5 => 0x4c,
        SDL_Scancode.SDL_SCANCODE_KP_6 or SDL_Scancode.SDL_SCANCODE_RIGHT => 0x4d,
        SDL_Scancode.SDL_SCANCODE_KP_PLUS => 0x4e,
        SDL_Scancode.SDL_SCANCODE_KP_1 or SDL_Scancode.SDL_SCANCODE_END => 0x4f,
        SDL_Scancode.SDL_SCANCODE_KP_2 or SDL_Scancode.SDL_SCANCODE_DOWN => 0x50,
        SDL_Scancode.SDL_SCANCODE_KP_3 or SDL_Scancode.SDL_SCANCODE_PAGEDOWN => 0x51,
        SDL_Scancode.SDL_SCANCODE_KP_0 or SDL_Scancode.SDL_SCANCODE_INSERT => 0x52,
        SDL_Scancode.SDL_SCANCODE_KP_PERIOD or SDL_Scancode.SDL_SCANCODE_DELETE => 0x53,
        SDL_Scancode.SDL_SCANCODE_NONUSBACKSLASH => 0x56,
        SDL_Scancode.SDL_SCANCODE_F11 => 0x57,
        SDL_Scancode.SDL_SCANCODE_F12 => 0x58,
        SDL_Scancode.SDL_SCANCODE_LGUI => 0x5b,
        SDL_Scancode.SDL_SCANCODE_RGUI => 0x5c,
        SDL_Scancode.SDL_SCANCODE_APPLICATION => 0x5d,
        _ => 0,
    };
}
