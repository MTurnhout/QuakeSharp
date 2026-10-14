using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.Input;
using QuakeSharp.Core.UI;
using Xunit;

namespace QuakeSharp.Core.Tests;

// The enhanced mode's mouse and game controller (design.md decision 76), without a game
public class EnhancedInputTests
{
    private static JoyInfoEx Pad(uint buttons = 0, uint pov = Win32.JOY_POVCENTERED, uint lx = 32768, uint ly = 32768,
        uint lt = 32768, uint rt = 32768) =>
        new() { dwButtons = buttons, dwPOV = pov, dwXpos = lx, dwYpos = ly, dwUpos = lt, dwVpos = rt };

    private static List<(int Key, bool Down)> Run(EnhancedInput input, JoyInfoEx pad, bool menu, double dt = 0.014)
    {
        var events = new List<(int, bool)>();
        input.PadKeys(pad, menu, dt, (k, d) => events.Add((k, d)));
        return events;
    }

    [Fact]
    public void ADeadZoneCutsTheCentreAndTheRestReachesTheEdge()
    {
        Assert.Equal((0f, 0f), EnhancedInput.Stick(32768 + 3000, 32768, 0.15f));
        (float x, float y) = EnhancedInput.Stick(65535, 32768, 0.15f);
        Assert.InRange(x, 0.999f, 1f);
        Assert.Equal(0f, y);
        (float dx, float dy) = EnhancedInput.Stick(65535, 65535, 0.15f);
        Assert.InRange(Math.Sqrt((dx * dx) + (dy * dy)), 0.999, 1.0);
        Assert.Equal(dx, dy, 4);
        (float up, _) = EnhancedInput.Stick(0, 32768, 0.15f);
        Assert.Equal(-1f, up, 3);
    }

    [Fact]
    public void ALookCurveIsFinerNearTheCentre()
    {
        Assert.Equal(0f, EnhancedInput.Curve(0));
        Assert.Equal(1f, EnhancedInput.Curve(1));
        Assert.Equal(-1f, EnhancedInput.Curve(-1));
        Assert.True(EnhancedInput.Curve(0.5f) < 0.5f && EnhancedInput.Curve(0.5f) > 0);
    }

    [Fact]
    public void TheHatKeepsDiagonalsAsTwoDirections()
    {
        Assert.Equal(0u, EnhancedInput.HatBits(Win32.JOY_POVCENTERED));
        Assert.Equal(1u, EnhancedInput.HatBits(Win32.JOY_POVFORWARD));
        Assert.Equal(1u | 2u, EnhancedInput.HatBits(4500));
        Assert.Equal(4u | 2u, EnhancedInput.HatBits(13500));
        Assert.Equal(4u | 8u, EnhancedInput.HatBits(22500));
        Assert.Equal(8u | 1u, EnhancedInput.HatBits(31500));
        Assert.Equal(8u, EnhancedInput.HatBits(Win32.JOY_POVLEFT));
    }

    [Fact]
    public void TheTriggerIsAPullFromItsRest()
    {
        Assert.Equal(0f, EnhancedInput.Trigger(32768));
        Assert.Equal(1f, EnhancedInput.Trigger(65535));
        Assert.Equal(0f, EnhancedInput.Trigger(0)); // below the rest: not a pull
    }

    [Fact]
    public void AKeyTheMousePlayerLeftUnboundHasADefaultAndABoundOneKeepsTheirs()
    {
        var both = new EnhancedInput(true, true);
        Assert.Equal("impulse 10", both.DefaultBinding(Keys.K_MWHEELUP));
        Assert.Equal("impulse 12", both.DefaultBinding(Keys.K_MWHEELDOWN));
        Assert.Equal("+attack", both.DefaultBinding(EnhancedInput.RightTrigger));
        Assert.Equal("+jump", both.DefaultBinding(Keys.K_JOY1));
        Assert.Null(both.DefaultBinding(Keys.K_MOUSE1)); // the game's own default.cfg binds it
        Assert.Null(new EnhancedInput(false, true).DefaultBinding(Keys.K_MWHEELUP));
        Assert.Null(new EnhancedInput(true, false).DefaultBinding(Keys.K_JOY1));
    }

    [Fact]
    public void InAGameTheButtonsTriggersAndHatAreKeysAndStartIsEscape()
    {
        var input = new EnhancedInput(true, true);
        Assert.Equal([(Keys.K_JOY1, true)], Run(input, Pad(buttons: 1), menu: false));
        Assert.Empty(Run(input, Pad(buttons: 1), menu: false));
        Assert.Equal([(Keys.K_JOY1, false), (Keys.K_ESCAPE, true)], Run(input, Pad(buttons: 1u << 7), menu: false));
        Assert.Equal([(Keys.K_ESCAPE, false), (EnhancedInput.RightTrigger, true), (Keys.K_AUX29 + 1, true)],
            Run(input, Pad(rt: 65535, pov: Win32.JOY_POVRIGHT), menu: false));
        // a half pull holds the trigger once it is down, a light one lets it go
        Assert.Empty(Run(input, Pad(rt: 32768 + 12000, pov: Win32.JOY_POVRIGHT), menu: false));
        Assert.Equal([(EnhancedInput.RightTrigger, false), (Keys.K_AUX29 + 1, false)], Run(input, Pad(rt: 32768 + 5000), menu: false)
            .OrderBy(e => e.Key == EnhancedInput.RightTrigger ? 0 : 1).ToList());
    }

    [Fact]
    public void InAMenuTheHatAndTheLeftStickAreArrowsAAndBAreEnterAndEscape()
    {
        var input = new EnhancedInput(true, true);
        Assert.Equal([(Keys.K_UPARROW, true)], Run(input, Pad(pov: Win32.JOY_POVFORWARD), menu: true));
        Assert.Equal([(Keys.K_UPARROW, false), (Keys.K_DOWNARROW, true)], Run(input, Pad(ly: 65535), menu: true));
        Assert.Equal([(Keys.K_DOWNARROW, false), (Keys.K_ENTER, true)], Run(input, Pad(buttons: 1), menu: true));
        Assert.Equal([(Keys.K_ENTER, false), (Keys.K_ESCAPE, true)], Run(input, Pad(buttons: 2), menu: true));
    }

    [Fact]
    public void AnArrowHeldInAMenuRepeatsAfterATenthOfASecondBeyondFourTenths()
    {
        var input = new EnhancedInput(true, true);
        var held = Pad(pov: Win32.JOY_POVLEFT);
        var all = new List<(int Key, bool Down)>();
        for (int i = 0; i < 100; i++)
        {
            all.AddRange(Run(input, held, menu: true, dt: 0.01));
        }

        int presses = all.Count(e => e is { Key: Keys.K_LEFTARROW, Down: true });
        // at 0.4 s, then every 0.1 s over the rest of a second: 1 + (1.0 - 0.4) / 0.1 presses, give or take one
        Assert.InRange(presses, 6, 8);
        Assert.Equal(presses - 1, all.Count(e => e is { Key: Keys.K_LEFTARROW, Down: false }));
    }

    [Fact]
    public void ChangingFromAGameToAMenuReleasesTheGameKeysFirst()
    {
        var input = new EnhancedInput(true, true);
        Run(input, Pad(buttons: 1, rt: 65535), menu: false);
        List<(int Key, bool Down)> events = Run(input, Pad(buttons: 1, rt: 65535), menu: true);
        Assert.Contains((Keys.K_JOY1, false), events);
        Assert.Contains((EnhancedInput.RightTrigger, false), events);
        Assert.Contains((Keys.K_ENTER, true), events);
    }

    [Fact]
    public void ReleaseLetsGoOfEveryHeldKey()
    {
        var input = new EnhancedInput(true, true);
        Run(input, Pad(buttons: 1 | 2, rt: 65535), menu: false);
        var released = new List<int>();
        input.Release((k, d) => released.Add(d ? -k : k));
        Assert.Equal([Keys.K_JOY1, Keys.K_JOY1 + 1, EnhancedInput.RightTrigger], released.Order());
        Assert.Empty(Run(input, Pad(), menu: false));
    }

    [Fact]
    public void AHurtRumblesByHowMuchTheDamageFlashGrew()
    {
        Assert.Equal((0f, 0u), EnhancedInput.RumbleFor(30, 30));
        Assert.Equal((0f, 0u), EnhancedInput.RumbleFor(30, 20)); // the flash fading
        (float small, uint shortTime) = EnhancedInput.RumbleFor(0, 6);
        (float big, uint longTime) = EnhancedInput.RumbleFor(0, 90);
        Assert.Equal(0.2f, small); // the least that can be felt
        Assert.Equal(1f, big);
        Assert.True(longTime > shortTime);
    }
}
