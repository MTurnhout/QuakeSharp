using QuakeSharp.Core;
using QuakeSharp.Core.Platform;
using QuakeSharp.Core.UI;
using SDL;
using static SDL.SDL3;

namespace QuakeSharp.Platform.Sdl;

/// <summary>
/// The game's window, made for Vulkan, and its events: vid_win.c's window and MainWndProc's keys and mouse buttons
/// (design.md §8). Borderless over the whole screen, or a window.
/// </summary>
public sealed unsafe class SdlWindow : IInputEvents, IDisposable
{
    private readonly string[] vulkanExtensions;

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

    /// <summary>The window's size in pixels.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            int w, h;
            SDL_GetWindowSizeInPixels(Handle, &w, &h);
            return (w, h);
        }
    }

    public bool Fullscreen
    {
        get => (SDL_GetWindowFlags(Handle) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;
        set => SDL_SetWindowFullscreen(Handle, value);
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
    public bool SendKeyEvents(Action<int, bool> keyEvent)
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

                case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                    Minimized = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                case SDL_EventType.SDL_EVENT_WINDOW_MAXIMIZED:
                    Minimized = false;
                    break;

                // WM_KEYDOWN (held keys repeat, as Windows repeats them), WM_KEYUP
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    keyEvent(KeyTranslation.MapKey(e.key.scancode), e.key.down);
                    break;

                // in_win.c: IN_MouseEvent, the buttons as K_MOUSE1 + button
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    int button = e.button.Button switch
                    {
                        SDLButton.SDL_BUTTON_LEFT => 0,
                        SDLButton.SDL_BUTTON_RIGHT => 1,
                        SDLButton.SDL_BUTTON_MIDDLE => 2,
                        _ => -1,
                    };
                    if (button >= 0)
                    {
                        keyEvent(Keys.K_MOUSE1 + button, e.button.down);
                    }

                    break;

                // WM_MOUSEWHEEL: a press and a release of K_MWHEELUP or K_MWHEELDOWN
                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    if (e.wheel.y != 0)
                    {
                        int key = e.wheel.y > 0 ? Keys.K_MWHEELUP : Keys.K_MWHEELDOWN;
                        keyEvent(key, true);
                        keyEvent(key, false);
                    }

                    break;
            }
        }

        return open;
    }

    public void Dispose()
    {
        SDL_DestroyWindow(Handle);
        SDL_Quit();
    }
}
