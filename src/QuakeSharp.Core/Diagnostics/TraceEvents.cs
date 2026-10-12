using System.Globalization;
using System.Text;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// The oracle's scripted input (tools/oracle/src/trace.c: Trace_Events, -traceevents): lines of
/// "&lt;frame&gt; &lt;console text&gt;", added to the command buffer before that frame runs;
/// "&lt;frame&gt; @key &lt;key number&gt; &lt;1 or 0&gt;", a key going down or up; "&lt;frame&gt; @mouse &lt;dx&gt; &lt;dy&gt;",
/// the mouse moved; "&lt;frame&gt; @buttons &lt;state&gt;", the mouse buttons held (1 left, 2 right, 4 middle).
/// Sys_SendKeyEvents delivers them to the window procedure.
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

    public bool SendKeyEvents(IWindowMessages window)
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
                    window.KeyEvent(key, down != 0);
                }
            }
            else if (nextEvent.StartsWith("@mouse ", StringComparison.Ordinal))
            {
                string[] fields = nextEvent[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2
                    && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dx)
                    && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dy))
                {
                    window.MouseMove(dx, dy);
                }
            }
            else if (nextEvent.StartsWith("@buttons ", StringComparison.Ordinal))
            {
                string[] fields = nextEvent[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 1
                    && int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int state))
                {
                    window.MouseButtons(state);
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
