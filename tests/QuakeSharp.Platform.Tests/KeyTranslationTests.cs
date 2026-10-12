using QuakeSharp.Core.UI;
using QuakeSharp.Platform.Sdl;
using SDL;

namespace QuakeSharp.Platform.Tests;

public class KeyTranslationTests
{
    [Theory]
    [InlineData(SDL_Scancode.SDL_SCANCODE_A, 'a')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_Z, 'z')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_1, '1')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_0, '0')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_GRAVE, '`')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_APOSTROPHE, '\'')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_ESCAPE, Keys.K_ESCAPE)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_RETURN, Keys.K_ENTER)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_BACKSPACE, Keys.K_BACKSPACE)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_F1, Keys.K_F1)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_F10, Keys.K_F10)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_F12, Keys.K_F12)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_LSHIFT, Keys.K_SHIFT)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_RSHIFT, Keys.K_SHIFT)]
    public void TypingKeysByPosition(SDL_Scancode scancode, int key) => Assert.Equal(key, KeyTranslation.MapKey(scancode));

    [Theory]
    // MapKey drops the e0 prefix: the grey keys are their keypad twins, the right-hand modifiers the left-hand ones
    [InlineData(SDL_Scancode.SDL_SCANCODE_UP, Keys.K_UPARROW)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_KP_8, Keys.K_UPARROW)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_DELETE, Keys.K_DEL)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_KP_PERIOD, Keys.K_DEL)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_KP_ENTER, Keys.K_ENTER)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_RCTRL, Keys.K_CTRL)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_RALT, Keys.K_ALT)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_KP_DIVIDE, '/')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_KP_MINUS, '-')]
    [InlineData(SDL_Scancode.SDL_SCANCODE_KP_5, '5')]
    // NumLock has Pause's scan code
    [InlineData(SDL_Scancode.SDL_SCANCODE_PAUSE, Keys.K_PAUSE)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR, Keys.K_PAUSE)]
    // keys scantokey has nothing for
    [InlineData(SDL_Scancode.SDL_SCANCODE_CAPSLOCK, 0)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_SCROLLLOCK, 0)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_LGUI, 0)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_NONUSBACKSLASH, 0)]
    [InlineData(SDL_Scancode.SDL_SCANCODE_F13, 0)]
    public void ExtendedKeysAsWinQuakeReadsThem(SDL_Scancode scancode, int key) => Assert.Equal(key, KeyTranslation.MapKey(scancode));
}
