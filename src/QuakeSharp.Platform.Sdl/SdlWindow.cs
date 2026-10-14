using QuakeSharp.Core;
using QuakeSharp.Core.Input;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.UI;
using SDL;
using static SDL.SDL3;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The game's window, made for Vulkan, and its events: vid_win.c's window and the messages MainWndProc takes, the keys,
/// the mouse's moves and its buttons (design.md §8, decision 43). Borderless over the whole screen, or a window. While
/// in_win.c confines the cursor to the window, the window takes the mouse: relative motion, the pointer hidden.
/// </summary>
public sealed unsafe class SdlWindow : IInputEvents, IMouseCapture, IDisposable
{
    private readonly string[] vulkanExtensions;

    // the mouse's motion not yet delivered (WM_MOUSEMOVE moves the cursor whole pixels), and the buttons held
    private float motionX, motionY;
    private int buttons;

    // the window's size in pixels, width in the high half
    private long pixelSize;

    /// <param name="fullscreen">Borderless over the whole screen at the desktop's resolution; otherwise a window of the
    /// given size (fitted to the screen).</param>
    public SdlWindow(string title, int width, int height, bool fullscreen)
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            throw new QuakeException($"SDL_Init failed: {SDL_GetError()}");
        }

        // within nine tenths of the desktop, the same shape
        SDL_Rect usable;
        if (SDL_GetDisplayUsableBounds(SDL_GetPrimaryDisplay(), &usable) && usable.w > 0 && usable.h > 0)
        {
            float fit = MathF.Min(1f, MathF.Min(0.9f * usable.w / width, 0.9f * usable.h / height));
            width = (int)(width * fit);
            height = (int)(height * fit);
        }

        Handle = SDL_CreateWindow(title, width, height,
            SDL_WindowFlags.SDL_WINDOW_VULKAN | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY |
            (fullscreen ? SDL_WindowFlags.SDL_WINDOW_FULLSCREEN : 0));
        if (Handle == null)
        {
            string error = SDL_GetError() ?? "";
            SDL_Quit();
            throw new QuakeException($"SDL_CreateWindow failed: {error}");
        }

        Minimized = (SDL_GetWindowFlags(Handle) & SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0;
        UpdatePixelSize();

        uint count;
        byte** names = SDL_Vulkan_GetInstanceExtensions(&count);
        vulkanExtensions = new string[count];
        for (int i = 0; i < count; i++)
        {
            vulkanExtensions[i] = new string((sbyte*)names[i]);
        }
    }

    /// <summary>The Vulkan instance extensions a surface on this window needs.</summary>
    public IReadOnlyList<string> RequiredVulkanInstanceExtensions => vulkanExtensions;

    public bool Minimized { get; private set; }

    /// <summary>
    /// The window got the focus (true) or lost it (false), with whether it is minimized: WM_ACTIVATE, which calls
    /// vid_win.c's AppActivate. Raised from <see cref="SendKeyEvents"/>.
    /// </summary>
    public Action<bool, bool>? Activated { get; set; }

    /// <summary>
    /// Scroll Lock was pressed (design.md decision 61: it switches the enhanced mode). The key still reaches the game,
    /// as key 0, as Winquake.exe's MapKey gives it. Raised from <see cref="SendKeyEvents"/>.
    /// </summary>
    public Action? ScrollLock { get; set; }

    /// <summary>Print Screen was pressed (the enhanced mode's screenshot). The key still reaches the game.</summary>
    public Action? PrintScreen { get; set; }

    /// <summary>Alt+Enter was pressed: true when it was handled (the enhanced mode's full screen), and the Enter key
    /// then does not reach the game.</summary>
    public Func<bool>? AltEnter { get; set; }

    // an Enter whose press Alt+Enter took: its release is taken too
    private bool enterTaken;

    /// <summary>
    /// The enhanced mode's menu pointer (design.md §11.9): while on, the system's pointer shows and moves freely over the
    /// window whatever in_win.c asked, and the mouse's moves, buttons and wheel go to <see cref="Pointer"/> (the window's
    /// pixel, and 0 a move, 1-3 a button pressed, 4 and 5 the wheel up and down) instead of the game.
    /// </summary>
    public bool PointerMode
    {
        get => pointerMode;
        set
        {
            if (value == pointerMode)
            {
                return;
            }

            pointerMode = value;
            SDL_SetWindowRelativeMouseMode(Handle, !value && clipped);
            if (value || shown)
            {
                SDL_ShowCursor();
            }
            else
            {
                SDL_HideCursor();
            }

            motionX = motionY = 0;
        }
    }

    /// <summary>The menu pointer's events (see <see cref="PointerMode"/>).</summary>
    public Action<float, float, int>? Pointer { get; set; }

    // in_win.c's wishes, kept while the pointer mode overrides them
    private bool pointerMode, clipped, shown = true;

    // a window's point in pixels
    private (float X, float Y) Pixels(float x, float y)
    {
        float density = SDL_GetWindowPixelDensity(Handle);
        return (x * density, y * density);
    }

    /// <summary>
    /// The window's size in pixels, as the last of its events said (so the enhanced renderer's thread can read it: SDL's
    /// window functions belong to the main thread).
    /// </summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            long size = Interlocked.Read(ref pixelSize);
            return ((int)(size >> 32), (int)size);
        }
    }

    public bool Fullscreen
    {
        get => (SDL_GetWindowFlags(Handle) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;
        set => SDL_SetWindowFullscreen(Handle, value);
    }

    /// <summary>The window's position and size on the desktop (not in full screen), in the desktop's units.</summary>
    public (int X, int Y, int Width, int Height) Geometry
    {
        get
        {
            int x, y, w, h;
            SDL_GetWindowPosition(Handle, &x, &y);
            SDL_GetWindowSize(Handle, &w, &h);
            return (x, y, w, h);
        }
        set
        {
            SDL_SetWindowSize(Handle, value.Width, value.Height);
            SDL_SetWindowPosition(Handle, value.X, value.Y);
            UpdatePixelSize();
        }
    }

    public string Title
    {
        set => SDL_SetWindowTitle(Handle, value);
    }

    private SDL_Window* Handle { get; }

    /// <summary>A message box for an error the game cannot go on after (works before SDL is set up).</summary>
    public static void ShowError(string title, string message) =>
        SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, title, message, null);

    /// <summary>A Vulkan surface on the window, for <paramref name="instance"/> (a VkInstance).</summary>
    public ulong CreateVulkanSurface(nint instance)
    {
        VkSurfaceKHR_T* surface;
        if (!SDL_Vulkan_CreateSurface(Handle, (VkInstance_T*)instance, null, &surface))
        {
            throw new QuakeException($"SDL_Vulkan_CreateSurface failed: {SDL_GetError()}");
        }

        return (ulong)surface;
    }

    // sys_win.c: Sys_SendKeyEvents; vid_win.c: MainWndProc
    public bool SendKeyEvents(IWindowMessages window)
    {
        bool open = true;
        SDL_Event e;
        while (SDL_PollEvent(&e))
        {
            switch (e.Type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                    open = false;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                    UpdatePixelSize();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                    Minimized = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                case SDL_EventType.SDL_EVENT_WINDOW_MAXIMIZED:
                    Minimized = false;
                    break;

                // WM_ACTIVATE
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    Activated?.Invoke(e.Type == SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED, Minimized);
                    break;

                // WM_KEYDOWN (held keys repeat, as Windows repeats them), WM_KEYUP
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    // the enhanced mode's own keys (design.md §11.9): Alt+Enter, taken from the game when it is handled
                    if ((e.key.scancode == SDL_Scancode.SDL_SCANCODE_RETURN || e.key.scancode == SDL_Scancode.SDL_SCANCODE_KP_ENTER)
                        && (enterTaken || (e.key.down && !e.key.repeat && (e.key.mod & SDL_Keymod.SDL_KMOD_ALT) != 0 && AltEnter?.Invoke() == true)))
                    {
                        enterTaken = e.key.down;
                        break;
                    }

                    if (e.key.scancode == SDL_Scancode.SDL_SCANCODE_PRINTSCREEN && e.key.down && !e.key.repeat)
                    {
                        PrintScreen?.Invoke();
                    }

                    window.KeyEvent(KeyTranslation.MapKey(e.key.scancode), e.key.down);
                    if (e.key.scancode == SDL_Scancode.SDL_SCANCODE_SCROLLLOCK && e.key.down && !e.key.repeat)
                    {
                        ScrollLock?.Invoke();
                    }

                    break;

                // WM_MOUSEMOVE: the cursor moved by whole pixels; what is left of the motion waits for more
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    if (pointerMode)
                    {
                        (float px, float py) = Pixels(e.motion.x, e.motion.y);
                        Pointer?.Invoke(px, py, 0);
                        break;
                    }

                    motionX += e.motion.xrel;
                    motionY += e.motion.yrel;
                    int dx = (int)motionX, dy = (int)motionY;
                    if (dx != 0 || dy != 0)
                    {
                        motionX -= dx;
                        motionY -= dy;
                        window.MouseMove(dx, dy);
                    }

                    break;

                // WM_LBUTTONDOWN...: the buttons held (MK_LBUTTON, MK_RBUTTON, MK_MBUTTON as 1, 2, 4)
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    int button = e.button.Button switch
                    {
                        SDLButton.SDL_BUTTON_LEFT => 1,
                        SDLButton.SDL_BUTTON_RIGHT => 2,
                        SDLButton.SDL_BUTTON_MIDDLE => 4,
                        _ => 0,
                    };
                    if (pointerMode && e.button.down)
                    {
                        (float px, float py) = Pixels(e.button.x, e.button.y);
                        Pointer?.Invoke(px, py, button == 4 ? 3 : button);
                        break;
                    }

                    // (a button let go in the pointer mode reaches the game only if the game saw it pressed)
                    if (button != 0 && (!pointerMode || (buttons & button) != 0))
                    {
                        buttons = e.button.down ? buttons | button : buttons & ~button;
                        window.MouseButtons(buttons);
                    }

                    break;

                // WM_MOUSEWHEEL: a press and a release of K_MWHEELUP or K_MWHEELDOWN
                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    if (pointerMode && e.wheel.y != 0)
                    {
                        (float px, float py) = Pixels(e.wheel.mouse_x, e.wheel.mouse_y);
                        Pointer?.Invoke(px, py, e.wheel.y > 0 ? 4 : 5);
                    }
                    else if (e.wheel.y != 0)
                    {
                        int key = e.wheel.y > 0 ? Keys.K_MWHEELUP : Keys.K_MWHEELDOWN;
                        window.KeyEvent(key, true);
                        window.KeyEvent(key, false);
                    }

                    break;
            }
        }

        return open;
    }

    /// <summary>
    /// vid_win.c set a mode: a window whose client area has the mode's size (fitted to the screen, as at its creation),
    /// or the whole screen, borderless.
    /// </summary>
    public void SetMode(int width, int height, bool fullscreen)
    {
        if (fullscreen)
        {
            SDL_SetWindowFullscreen(Handle, true);
            return;
        }

        SDL_SetWindowFullscreen(Handle, false);
        SDL_Rect usable;
        if (SDL_GetDisplayUsableBounds(SDL_GetDisplayForWindow(Handle), &usable) && usable.w > 0 && usable.h > 0)
        {
            float fit = MathF.Min(1f, MathF.Min(0.9f * usable.w / width, 0.9f * usable.h / height));
            width = (int)(width * fit);
            height = (int)(height * fit);
        }

        SDL_SetWindowSize(Handle, width, height);
        SDL_SetWindowPosition(Handle, (int)SDL_WINDOWPOS_CENTERED_MASK, (int)SDL_WINDOWPOS_CENTERED_MASK);
        UpdatePixelSize();
    }

    private void UpdatePixelSize()
    {
        int w, h;
        SDL_GetWindowSizeInPixels(Handle, &w, &h);
        Interlocked.Exchange(ref pixelSize, ((long)w << 32) | (uint)h);
    }

    // ClipCursor: the window takes the mouse (relative motion, the pointer kept in and hidden) or lets it go
    public void ClipCursor(bool clipped)
    {
        this.clipped = clipped;
        if (!pointerMode)
        {
            SDL_SetWindowRelativeMouseMode(Handle, clipped);
        }

        motionX = motionY = 0;
    }

    // ShowCursor's display counter
    public void ShowCursor(bool show)
    {
        shown = show;
        if (show || pointerMode)
        {
            SDL_ShowCursor();
        }
        else
        {
            SDL_HideCursor();
        }
    }

    public void Dispose()
    {
        SDL_DestroyWindow(Handle);
        SDL_Quit();
    }
}
