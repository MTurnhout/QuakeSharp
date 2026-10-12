using System.Globalization;
using System.Text;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// The executable's -recordevents (design.md decision 34): passes the platform's key events on, and writes each as a
/// "&lt;frame&gt; @key &lt;key&gt; &lt;1 or 0&gt;" line of the oracle's -traceevents format, the frame being the one that
/// takes it, so <see cref="TraceEvents"/> and the oracle deliver it in the same frame of a replay.
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

    public bool SendKeyEvents(Action<int, bool> keyEvent)
    {
        int current = frame() + 1;
        return input.SendKeyEvents((key, down) =>
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{current} @key {key} {(down ? 1 : 0)}"));
            keyEvent(key, down);
        });
    }

    public void Dispose() => writer.Dispose();
}
