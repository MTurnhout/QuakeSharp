using System.Diagnostics;
using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// The enhanced mode's renderer (design.md §11.6): draws the snapshots the game thread hands it, on a thread of its own,
/// so the game never waits for the GPU. It sees only <see cref="EnhancedFrame"/>s, never the engine, and draws them
/// through the presenter (<see cref="VulkanPresenter.PresentEnhanced"/>). With interpolation (step 6) it draws as often
/// as the display shows frames (vsync) or as <see cref="EnhancedOptions.MaxFps"/> lets, each frame part way between
/// the last two snapshots by the real time since the last one came, over the game time between them: the picture at
/// most one game frame behind; once there, it waits for the next. A failure on its thread is thrown on the game thread
/// at the next frame.
/// </summary>
public sealed class EnhancedRenderer : IDisposable
{
    // the longest game time between two snapshots that is interpolated (more is a pause, a level change, a load)
    private const double MaxInterval = 0.1;

    private readonly VulkanPresenter presenter;
    private readonly EnhancedOptions options;
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly EnhancedSmoothing smoothing;
    private EnhancedFrame? previous, latest;
    private long latestAt;
    private bool fresh;
    private Exception? failure;
    private bool stopping;

    public EnhancedRenderer(VulkanPresenter presenter, EnhancedOptions options)
    {
        this.presenter = presenter;
        this.options = options;
        smoothing = new EnhancedSmoothing { Poses = options.LerpModels, Steps = options.LerpMove };
        thread = new Thread(Run) { Name = "QuakeSharp enhanced renderer", IsBackground = true };
        thread.Start();
    }

    /// <summary>A finished frame's snapshot, to draw.</summary>
    public void Submit(EnhancedFrame frame)
    {
        lock (gate)
        {
            ThrowFailure();
            previous = latest;
            latest = frame;
            latestAt = Stopwatch.GetTimestamp();
            smoothing.Observe(frame);
            fresh = true;
            Monitor.Pulse(gate);
        }
    }

    /// <summary>
    /// A rectangle of the screen's buffer drawn over the last frame between frames (the loading disc, and what was under
    /// it put back), shown now when <paramref name="show"/>.
    /// </summary>
    public void DirectRect(ReadOnlySpan<byte> pixels, int rowBytes, int x, int y, int width, int height, bool show)
    {
        lock (gate)
        {
            ThrowFailure();
            if (latest == null)
            {
                return;
            }

            latest = latest.WithDirectRect(pixels, rowBytes, x, y, width, height);
            previous = null;
            if (show)
            {
                fresh = true;
                Monitor.Pulse(gate);
            }
        }
    }

    /// <summary>The enhanced frames drawn so far, and the time spent drawing them (for the frame rate measured).</summary>
    public long FramesDrawn { get; private set; }

    public TimeSpan DrawingTime { get; private set; }

    public void Dispose()
    {
        lock (gate)
        {
            stopping = true;
            Monitor.Pulse(gate);
        }

        thread.Join();
    }

    private void Run()
    {
        bool caughtUp = true;
        long lastDrawn = 0;
        while (true)
        {
            EnhancedFrame frame;
            lock (gate)
            {
                // nothing new and the last one reached: wait for the game
                while (!fresh && caughtUp && !stopping)
                {
                    Monitor.Wait(gate);
                }

                if (stopping)
                {
                    return;
                }

                fresh = false;
                (frame, caughtUp) = Interpolated();
                frame = smoothing.Apply(frame);
            }

            try
            {
                long started = Stopwatch.GetTimestamp();
                presenter.PresentEnhanced(frame, options);
                DrawingTime += Stopwatch.GetElapsedTime(started);
                FramesDrawn++;
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    failure = e;
                }

                return;
            }

            // without vsync, at most MaxFps a second
            if (!options.Vsync && options.MaxFps > 0)
            {
                double wait = (1.0 / options.MaxFps) - Stopwatch.GetElapsedTime(lastDrawn).TotalSeconds;
                if (wait > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(wait));
                }
            }

            lastDrawn = Stopwatch.GetTimestamp();
        }
    }

    // The frame to draw now and whether it is the last snapshot itself (nothing to draw until the next)
    private (EnhancedFrame Frame, bool CaughtUp) Interpolated()
    {
        if (!options.Interpolate || previous == null)
        {
            return (latest!, true);
        }

        double interval = latest!.Time - previous.Time;
        if (interval <= 0 || interval > MaxInterval)
        {
            return (latest, true);
        }

        double f = Stopwatch.GetElapsedTime(latestAt).TotalSeconds / interval;
        return f >= 1 ? (latest, true) : (EnhancedFrame.Lerp(previous, latest, (float)f), false);
    }

    private void ThrowFailure()
    {
        if (failure != null)
        {
            throw new InvalidOperationException("the enhanced renderer failed", failure);
        }
    }
}
