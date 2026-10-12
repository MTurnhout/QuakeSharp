using System.Globalization;
using System.Text;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// The executable's -recordevents (design.md decisions 34, 43): passes the platform's messages on to the window
/// procedure, and writes each as a line of the oracle's -traceevents format ("&lt;frame&gt; @key &lt;key&gt; &lt;1 or
/// 0&gt;", "&lt;frame&gt; @mouse &lt;dx&gt; &lt;dy&gt;", "&lt;frame&gt; @buttons &lt;state&gt;"), the frame being the one
/// that takes it, so <see cref="TraceEvents"/> and the oracle deliver the same messages in the same frame of a replay.
/// </summary>
public sealed class EventRecorder : IInputEvents, IDisposable
{
    private readonly IInputEvents input;
    private readonly Func<int> frame;
    private readonly StreamWriter writer;

    /// <param name="input">The platform's events.</param>
    /// <param name="path">The events file to write.</param>
    /// <param name="frame">The frames run so far (the frame taking the events is one more).</param>
    public EventRecorder(IInputEvents input, string path, Func<int> frame)
    {
        this.input = input;
        this.frame = frame;
        writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
    }

    public bool SendKeyEvents(IWindowMessages window) => input.SendKeyEvents(new Recording(this, window, frame() + 1));

    public void Dispose() => writer.Dispose();

    // the window procedure's messages, written as they pass
    private sealed class Recording(EventRecorder recorder, IWindowMessages window, int current) : IWindowMessages
    {
        public void KeyEvent(int key, bool down)
        {
            Write($"{current} @key {key} {(down ? 1 : 0)}");
            window.KeyEvent(key, down);
        }

        public void MouseMove(int dx, int dy)
        {
            Write($"{current} @mouse {dx} {dy}");
            window.MouseMove(dx, dy);
        }

        public void MouseButtons(int state)
        {
            Write($"{current} @buttons {state}");
            window.MouseButtons(state);
        }

        private void Write(FormattableString line) => recorder.writer.WriteLine(line.ToString(CultureInfo.InvariantCulture));
    }
}
