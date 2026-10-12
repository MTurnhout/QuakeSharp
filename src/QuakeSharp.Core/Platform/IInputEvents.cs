namespace QuakeSharp.Core.Platform;

/// <summary>
/// The platform's events: sys_win.c's Sys_SendKeyEvents, which runs the window procedure (vid_win.c: MainWndProc) over
/// the window's messages.
/// </summary>
public interface IInputEvents
{
    /// <summary>
    /// Delivers the pending messages to <paramref name="window"/>, in order. Returns false once the window has been
    /// closed.
    /// </summary>
    bool SendKeyEvents(IWindowMessages window);
}

/// <summary>
/// The messages vid_win.c's window procedure (MainWndProc) takes from the window: the keys, the mouse's moves and its
/// buttons (design.md §8, decision 43).
/// </summary>
public interface IWindowMessages
{
    /// <summary>WM_KEYDOWN, WM_KEYUP and WM_MOUSEWHEEL's press and release: a key number of keys.h.</summary>
    void KeyEvent(int key, bool down);

    /// <summary>WM_MOUSEMOVE: the mouse moved the cursor by (<paramref name="dx"/>, <paramref name="dy"/>), before the
    /// cursor is confined.</summary>
    void MouseMove(int dx, int dy);

    /// <summary>WM_LBUTTONDOWN, WM_LBUTTONUP...: the buttons held now (1 left, 2 right, 4 middle).</summary>
    void MouseButtons(int state);
}
