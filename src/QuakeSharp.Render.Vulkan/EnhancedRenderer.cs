using QuakeSharp.Core.Enhanced;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// The enhanced mode's renderer (design.md §11.6): draws the snapshots the game thread hands it, on a thread of its own,
/// so the game never waits for the GPU. It sees only <see cref="EnhancedFrame"/>s, never the engine, and draws them
/// through the presenter (<see cref="VulkanPresenter.PresentEnhanced"/>); a failure on its thread is thrown on the game
/// thread at the next frame.
/// </summary>
public sealed class EnhancedRenderer : IDisposable
{
    private readonly VulkanPresenter presenter;
    private readonly EnhancedOptions options;
    private readonly Thread thread;
    private readonly object gate = new();
    private EnhancedFrame? pending;
    private EnhancedFrame? last;
    private Exception? failure;
    private bool stopping;

    public EnhancedRenderer(VulkanPresenter presenter, EnhancedOptions options)
    {
        this.presenter = presenter;
        this.options = options;
        thread = new Thread(Run) { Name = "QuakeSharp enhanced renderer", IsBackground = true };
        thread.Start();
    }

    /// <summary>A finished frame's snapshot, to draw (the newest wins over one not drawn yet).</summary>
    public void Submit(EnhancedFrame frame)
    {
        lock (gate)
        {
            ThrowFailure();
            pending = last = frame;
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
            if (last == null)
            {
                return;
            }

            last = last.WithDirectRect(pixels, rowBytes, x, y, width, height);
            if (show)
            {
                pending = last;
                Monitor.Pulse(gate);
            }
        }
    }

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
        while (true)
        {
            EnhancedFrame frame;
            lock (gate)
            {
                while (pending == null && !stopping)
                {
                    Monitor.Wait(gate);
                }

                if (stopping)
                {
                    return;
                }

                frame = pending!;
                pending = null;
            }

            try
            {
                presenter.PresentEnhanced(frame, options);
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    failure = e;
                }

                return;
            }
        }
    }

    private void ThrowFailure()
    {
        if (failure != null)
        {
            throw new InvalidOperationException("the enhanced renderer failed", failure);
        }
    }
}
