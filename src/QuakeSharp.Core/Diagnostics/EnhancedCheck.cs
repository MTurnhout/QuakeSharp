using QuakeSharp.Core.Enhanced;
using QuakeSharp.Core.Platform;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// The enhanced mode in a trace run, as EnhancedModeChangesNothing has it (design.md §11.6): switched on before the
/// first frame and off and on again along the way (off for <see cref="OffFrames"/> frames from frame 500 of every
/// thousand), every frame captured and the frames between it and the one before built at ¼, ½ and ¾, with the capture's
/// guard on: its work must leave the clock's reads and rand()'s draws as they were. Where the capture knows what the
/// palette was built from, its tables must give the palette shown.
/// </summary>
internal sealed class EnhancedCheck
{
    /// <summary>The frames enhanced mode is off for, every thousand.</summary>
    public const int OffFrames = 20;

    private static readonly float[] Fractions = [0.25f, 0.5f, 0.75f];

    private readonly QuakeEngine engine;
    private readonly VirtualSystem system;
    private EnhancedFrame? previous;
    private int frame;

    public EnhancedCheck(QuakeEngine engine, VirtualSystem system)
    {
        this.engine = engine;
        this.system = system;
    }

    /// <summary>Before frame <paramref name="number"/> (1 up): enhanced mode switched on or off.</summary>
    public void BeforeFrame(int number)
    {
        frame = number;
        bool on = number % 1000 is < 500 or >= 500 + OffFrames;
        if (on && engine.enhanced == null)
        {
            var capture = new EnhancedCapture(engine) { EngineCounts = () => (system.Reads << 32) + engine.RandCalls };
            capture.Captured += Captured;
            engine.enhanced = capture;
            previous = null;
        }
        else if (!on)
        {
            engine.enhanced = null;
        }
    }

    private void Captured(EnhancedFrame next)
    {
        if (next.ChannelTablesExact && !next.TablesMakePalette(engine.host_basepal))
        {
            throw new InvalidOperationException($"frame {frame}: the enhanced mode's palette tables do not make the palette shown");
        }

        if (previous != null)
        {
            foreach (float f in Fractions)
            {
                _ = EnhancedFrame.Lerp(previous, next, f);
            }
        }

        previous = next;
    }
}
