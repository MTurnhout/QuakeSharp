using QuakeSharp.Core.Input;
using QuakeSharp.Core.UI;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced mode's input (design.md decision 76): the mouse taken by the window as soon as it has the focus with free
/// look, the mouse wheel changing weapons, and a game controller with analog sticks, triggers and bindings of its own.
/// It is the engine's <c>enhancedInput</c> while the mode is on and null otherwise, so the faithful mode keeps WinQuake's
/// mouse and joystick exactly (in_win.c's code is untouched; the seams only ask this). It writes no cvar and no
/// binding, so config.cfg stays the game's: what it adds is defaults for keys the player has not bound.
/// </summary>
public sealed class EnhancedInput(bool mouse, bool gamepad, float deadzone = 0.15f, float sensitivity = 1f, bool alwaysRun = false)
{
    /// <summary>Yaw and pitch rates of the right stick at full deflection, degrees a second (times the sensitivity).</summary>
    public const float YawRate = 220f, PitchRate = 140f;

    /// <summary>The triggers' keys, after the buttons' (in_win.c's numbering: a button i is K_JOY1 + i below 4, K_AUX1 + i
    /// above).</summary>
    public const int LeftTrigger = Keys.K_AUX1 + 10, RightTrigger = Keys.K_AUX1 + 11;

    private static readonly int[] HatArrows = [Keys.K_UPARROW, Keys.K_RIGHTARROW, Keys.K_DOWNARROW, Keys.K_LEFTARROW];

    private readonly HashSet<int> held = [];
    private double repeatAt = 0.4;

    /// <summary>The mouse taken without <c>_windowed_mouse</c>, and free look on (<c>+mlook</c> held).</summary>
    public bool Mouse { get; } = mouse;

    /// <summary>Running is the default and the speed key walks (cl_input.c's +speed inverted, for the keyboard's movement).</summary>
    public bool AlwaysRun { get; } = alwaysRun;

    /// <summary>A game controller drives the game; in_win.c's joystick code does not run.</summary>
    public bool Gamepad { get; } = gamepad;

    /// <summary>The share of a stick's travel around the centre that does nothing.</summary>
    public float Deadzone { get; } = Math.Clamp(deadzone, 0f, 0.9f);

    /// <summary>The right stick's turning speed as a multiple of <see cref="YawRate"/> and <see cref="PitchRate"/>.</summary>
    public float Sensitivity { get; } = Math.Clamp(sensitivity, 0.1f, 5f);

    /// <summary>The controller shakes when the player is hurt (enhanced_gamepad_rumble).</summary>
    public bool Rumble { get; init; } = true;

    /// <summary>
    /// The rumble for a hurt: the damage flash's strength (cl.cshifts[CSHIFT_DAMAGE].percent, which V_ParseDamage raises
    /// by three times the damage) gone from <paramref name="before"/> to <paramref name="after"/>, as the motors' strength
    /// 0..1 and how long, in milliseconds (0: no rumble, the flash did not grow).
    /// </summary>
    public static (float Strength, uint Milliseconds) RumbleFor(int before, int after)
    {
        if (after <= before)
        {
            return (0, 0);
        }

        float strength = Math.Clamp((after - before) / 60f, 0.2f, 1f);
        return (strength, (uint)(120 + (180 * strength)));
    }

    /// <summary>The binding a key has when the player has bound nothing to it (null: none).</summary>
    public string? DefaultBinding(int key)
    {
        if (Mouse)
        {
            if (key == Keys.K_MWHEELUP)
            {
                return "impulse 10"; // the weapon after
            }

            if (key == Keys.K_MWHEELDOWN)
            {
                return "impulse 12"; // the weapon before
            }
        }

        if (!Gamepad)
        {
            return null;
        }

        return key switch
        {
            Keys.K_JOY1 => "+jump", // A
            Keys.K_JOY1 + 1 => "+movedown", // B
            Keys.K_JOY1 + 2 => "+moveup", // X
            Keys.K_JOY1 + 3 => "centerview", // Y
            Keys.K_AUX1 + 4 => "impulse 12", // left bumper
            Keys.K_AUX1 + 5 => "impulse 10", // right bumper
            Keys.K_AUX1 + 6 => "+showscores", // back
            RightTrigger => "+attack",
            Keys.K_AUX29 + 1 => "impulse 10", // hat right
            Keys.K_AUX29 + 3 => "impulse 12", // hat left
            _ => null,
        };
    }

    /// <summary>A stick's raw position (0..65535, centred on 32768) as -1..1 on each axis: the dead zone cut out of the
    /// circle round the centre, the rest stretched back to the edge.</summary>
    public static (float X, float Y) Stick(uint rawX, uint rawY, float deadzone)
    {
        float x = (rawX - 32768f) / 32768f, y = (rawY - 32768f) / 32768f;
        float length = MathF.Sqrt((x * x) + (y * y));
        if (length <= deadzone || length == 0)
        {
            return (0, 0);
        }

        float scale = MathF.Min(1f, (length - deadzone) / (1f - deadzone)) / length;
        return (x * scale, y * scale);
    }

    /// <summary>A looking stick's response: finer near the centre, full at the edge.</summary>
    public static float Curve(float v) => v * MathF.Sqrt(MathF.Abs(v));

    /// <summary>A trigger's pull, 0..1 (the device reports its rest as 32768).</summary>
    public static float Trigger(uint raw) => Math.Clamp((raw - 32768f) / 32767f, 0f, 1f);

    /// <summary>The hat as four bits (1 up, 2 right, 4 down, 8 left), a diagonal as both.</summary>
    public static uint HatBits(uint pov)
    {
        if (pov == Win32.JOY_POVCENTERED)
        {
            return 0;
        }

        uint bits = 0;
        if (pov >= 31500 || pov <= 4500)
        {
            bits |= 1;
        }

        if (pov is >= 4500 and <= 13500)
        {
            bits |= 2;
        }

        if (pov is >= 13500 and <= 22500)
        {
            bits |= 4;
        }

        if (pov is >= 22500 and <= 31500)
        {
            bits |= 8;
        }

        return bits;
    }

    /// <summary>
    /// The key events of a pad's state, given to <paramref name="key"/>: in a game the buttons, the triggers and the hat as
    /// their keys (Start is the Escape key, always); in a menu the hat and the left stick as the arrow keys, repeating
    /// while held, A as Enter and B as Escape. Keys held across a change between the two are released first.
    /// </summary>
    public void PadKeys(in JoyInfoEx pad, bool menu, double dt, Action<int, bool> key)
    {
        var wanted = new HashSet<int>();
        uint hat = HatBits(pad.dwPOV);
        if (menu)
        {
            (float lx, float ly) = Stick(pad.dwXpos, pad.dwYpos, 0.55f);
            if (hat == 0)
            {
                hat = (ly < 0 ? 1u : 0) | (lx > 0 ? 2u : 0) | (ly > 0 ? 4u : 0) | (lx < 0 ? 8u : 0);
            }

            for (int i = 0; i < 4; i++)
            {
                if ((hat & (1u << i)) != 0)
                {
                    wanted.Add(HatArrows[i]);
                }
            }

            if ((pad.dwButtons & 1) != 0)
            {
                wanted.Add(Keys.K_ENTER);
            }

            if ((pad.dwButtons & 2) != 0)
            {
                wanted.Add(Keys.K_ESCAPE);
            }
        }
        else
        {
            for (int i = 0; i < 10; i++)
            {
                if (i != 7 && (pad.dwButtons & (1u << i)) != 0)
                {
                    wanted.Add(i < 4 ? Keys.K_JOY1 + i : Keys.K_AUX1 + i);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if ((hat & (1u << i)) != 0)
                {
                    wanted.Add(Keys.K_AUX29 + i);
                }
            }

            if (Trigger(pad.dwUpos) > (held.Contains(LeftTrigger) ? 0.3f : 0.5f))
            {
                wanted.Add(LeftTrigger);
            }

            if (Trigger(pad.dwVpos) > (held.Contains(RightTrigger) ? 0.3f : 0.5f))
            {
                wanted.Add(RightTrigger);
            }
        }

        if ((pad.dwButtons & (1u << 7)) != 0)
        {
            wanted.Add(Keys.K_ESCAPE);
        }

        // an arrow held in a menu comes again, as a keyboard's does: after 0.4 s, then every 0.1 s
        bool again = false;
        if (menu && wanted.Any(IsArrow))
        {
            repeatAt -= dt;
            if (repeatAt <= 0 && held.Any(IsArrow))
            {
                again = true;
                repeatAt = 0.1;
            }
        }
        else
        {
            repeatAt = 0.4;
        }

        foreach (int k in held.Order().ToArray())
        {
            if (!wanted.Contains(k) || (again && IsArrow(k)))
            {
                held.Remove(k);
                key(k, false);
            }
        }

        foreach (int k in wanted.Order())
        {
            if (held.Add(k))
            {
                key(k, true);
            }
        }
    }

    /// <summary>Releases every key the pad holds (the mode is switched off, or the pad went).</summary>
    public void Release(Action<int, bool> key)
    {
        foreach (int k in held.Order().ToArray())
        {
            held.Remove(k);
            key(k, false);
        }
    }

    private static bool IsArrow(int k) => k is >= Keys.K_UPARROW and <= Keys.K_RIGHTARROW;
}
