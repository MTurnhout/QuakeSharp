using QuakeSharp.Core.Input;
using SDL;
using static SDL.SDL3;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The game controllers as winmm's joysticks, which in_win.c reads (design.md §8): SDL3's gamepads in the order SDL
/// lists them, in 16 slots. A gamepad's left stick is the X and Y axes (in_win.c turns and moves with them), its right
/// stick Z and R, its triggers U and V, each from 0 to 65535 centred on 32768 (the triggers from the centre up); its
/// face buttons are buttons 1-4 (JOY1-JOY4), then the shoulders, back, start and the sticks' buttons (AUX1-AUX6); the
/// direction pad is the point of view hat (AUX29-AUX32).
/// </summary>
public sealed unsafe class SdlJoysticks : IJoystickDevice, IDisposable
{
    private const uint Slots = 16;

    private static readonly SDL_GamepadButton[] ButtonOrder =
    [
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK,
    ];

    private readonly bool initialized;
    private readonly Dictionary<SDL_JoystickID, nint> opened = [];

    public SdlJoysticks()
    {
        initialized = SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_GAMEPAD);
    }

    public uint joyGetNumDevs() => initialized ? Slots : 0;

    public uint joyGetPosEx(uint id, ref JoyInfoEx info)
    {
        SDL_Gamepad* pad = Gamepad(id);
        if (pad == null)
        {
            return Win32.JOYERR_UNPLUGGED;
        }

        info.dwXpos = Stick(SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX));
        info.dwYpos = Stick(SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY));
        info.dwZpos = Stick(SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX));
        info.dwRpos = Stick(SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY));
        info.dwUpos = Stick(SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER));
        info.dwVpos = Stick(SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));

        info.dwButtons = 0;
        for (int i = 0; i < ButtonOrder.Length; i++)
        {
            if (SDL_GetGamepadButton(pad, ButtonOrder[i]))
            {
                info.dwButtons |= 1u << i;
            }
        }

        bool up = SDL_GetGamepadButton(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP);
        bool down = SDL_GetGamepadButton(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN);
        bool left = SDL_GetGamepadButton(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT);
        bool right = SDL_GetGamepadButton(pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT);
        int x = (right ? 1 : 0) - (left ? 1 : 0), y = (up ? 1 : 0) - (down ? 1 : 0);
        info.dwPOV = (x, y) switch
        {
            (0, 0) => Win32.JOY_POVCENTERED,
            (0, 1) => Win32.JOY_POVFORWARD,
            (1, 1) => 4500,
            (1, 0) => Win32.JOY_POVRIGHT,
            (1, -1) => 13500,
            (0, -1) => Win32.JOY_POVBACKWARD,
            (-1, -1) => 22500,
            (-1, 0) => Win32.JOY_POVLEFT,
            _ => 31500,
        };

        return Win32.JOYERR_NOERROR;
    }

    public uint joyGetDevCaps(uint id, out JoyCaps caps)
    {
        caps = default;
        if (Gamepad(id) == null)
        {
            return Win32.JOYERR_UNPLUGGED;
        }

        caps.wNumButtons = (uint)ButtonOrder.Length;
        caps.wCaps = Win32.JOYCAPS_HASPOV;
        return Win32.JOYERR_NOERROR;
    }

    // -32768..32767 to 0..65535
    private static uint Stick(short value) => (uint)(value + 32768);

    // the gamepad in the slot, opened the first time it is asked for
    private SDL_Gamepad* Gamepad(uint slot)
    {
        if (!initialized)
        {
            return null;
        }

        int count;
        SDL_JoystickID* ids = SDL_GetGamepads(&count);
        if (ids == null)
        {
            return null;
        }

        try
        {
            if (slot >= count)
            {
                return null;
            }

            SDL_JoystickID id = ids[slot];
            if (!opened.TryGetValue(id, out nint pad))
            {
                pad = (nint)SDL_OpenGamepad(id);
                if (pad == 0)
                {
                    return null;
                }

                opened[id] = pad;
            }

            return (SDL_Gamepad*)pad;
        }
        finally
        {
            SDL_free(ids);
        }
    }

    public void Dispose()
    {
        foreach (nint pad in opened.Values)
        {
            SDL_CloseGamepad((SDL_Gamepad*)pad);
        }

        opened.Clear();
        if (initialized)
        {
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_GAMEPAD);
        }
    }
}
