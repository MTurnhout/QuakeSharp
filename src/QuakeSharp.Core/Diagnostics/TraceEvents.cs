using System.Globalization;
using System.Text;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// The oracle's scripted input (tools/oracle/src/trace.c: Trace_Events, -traceevents): lines of
/// "&lt;frame&gt; &lt;console text&gt;", added to the command buffer before that frame runs, or
/// "&lt;frame&gt; @key &lt;key number&gt; &lt;1 or 0&gt;", a key going down or up. Sys_SendKeyEvents delivers them.
/// </summary>
public sealed class TraceEvents : IInputEvents
{
    private readonly IEnumerator<string> lines;
    private readonly Func<int> frame;
    private bool more = true;
    private int nextFrame = -1;
    private string nextEvent = "";

    /// <param name="path">The events file.</param>
    /// <param name="frame">The frames run so far (the frame about to run is one more).</param>
    public TraceEvents(string path, Func<int> frame)
    {
        lines = File.ReadLines(path, Encoding.Latin1).GetEnumerator();
        this.frame = frame;
    }

    /// <summary>The engine whose command buffer gets the console text.</summary>
    public QuakeEngine? Engine { get; set; }

    public bool SendKeyEvents(Action<int, bool> keyEvent)
    {
        while (more)
        {
            if (nextFrame < 0 && !ReadEvent())
            {
                more = false;
                return true;
            }

            if (nextFrame > frame() + 1)
            {
                return true;
            }

            if (nextEvent.StartsWith("@key ", StringComparison.Ordinal))
            {
                string[] fields = nextEvent[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2
                    && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int key)
                    && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int down))
                {
                    keyEvent(key, down != 0);
                }
            }
            else
            {
                Engine?.Cbuf_AddText(nextEvent);
                Engine?.Cbuf_AddText("\n");
            }

            nextFrame = -1;
        }

        return true;
    }

    // fscanf (events, "%d %1023[^\n]", ...): a number, then the rest of its line (blank lines are skipped, as %d skips
    // white space)
    private bool ReadEvent()
    {
        while (lines.MoveNext())
        {
            string line = lines.Current.TrimStart();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            int space = line.IndexOfAny([' ', '\t']);
            if (space < 0 || !int.TryParse(line[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out nextFrame))
            {
                return false;
            }

            nextEvent = line[(space + 1)..].TrimStart();
            if (nextEvent.Length > 1023)
            {
                nextEvent = nextEvent[..1023];
            }

            return true;
        }

        return false;
    }
}
