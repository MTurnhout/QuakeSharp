namespace QuakeSharp.Core.Platform;

/// <summary>
/// The platform's events: sys_win.c's Sys_SendKeyEvents, which runs the window procedure that posts keys and mouse
/// buttons to Key_Event (vid_win.c: MainWndProc).
/// </summary>
public interface IInputEvents
{
    /// <summary>
    /// Delivers the pending key events to <paramref name="keyEvent"/> (a key number of keys.h and whether it went down),
    /// in order. Returns false once the window has been closed.
    /// </summary>
    bool SendKeyEvents(Action<int, bool> keyEvent);
}
