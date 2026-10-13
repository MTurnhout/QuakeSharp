using System.Collections.Concurrent;
using QuakeSharp.Core.Enhanced;
using Vortice.Vulkan;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// Texture packs in the enhanced view (design.md §11.7, milestone 13): a world texture's replacement found, read and
/// decoded on a loader thread (never the game thread, never through the game's files), uploaded by the render thread
/// a few at a time, and drawn in its place in the true-colour look while enhanced_textures is on. Until then, and
/// without a pack, the game's own texture is drawn.
/// </summary>
public sealed unsafe partial class VulkanPresenter
{
    private volatile EnhancedTexturePack? texturePack;

    /// <summary>The texture pack the enhanced view draws from (null: none); brush models made ready after it is set
    /// ask it for their textures.</summary>
    public EnhancedTexturePack? TexturePack
    {
        get => texturePack;
        set => texturePack = value;
    }

    /// <summary>For the tools: every replacement asked for so far read and uploaded, so the next frame drawn shows
    /// them (the view's frames otherwise take them as they come).</summary>
    public void LoadTexturePack()
    {
        lock (presenting)
        {
            enhanced?.World.LoadAllReplacements();
        }
    }

    // world.slang's DrawParams.flags: the texture's colours are a pack's (the sky reads them then)
    private const int DrawReplaced = 4;

    private sealed partial class WorldRenderer
    {
        // At most this much of replacements uploaded in one frame (each upload waits for the GPU)
        private const long ReplacementBudget = 64L << 20;

        private TextureLoader? loader;
        private bool useReplacements;

        // A brush model's textures asked of the pack, if there is one
        private void RequestReplacements(EnhancedBrushModel model, GpuTexture[] textures)
        {
            if (presenter.TexturePack is not { } pack)
            {
                return;
            }

            loader ??= new TextureLoader(pack);
            string map = Path.GetFileNameWithoutExtension(model.Name);
            for (int i = 0; i < textures.Length; i++)
            {
                loader.Request(textures[i], map, model.Textures[i]);
            }
        }

        public void LoadAllReplacements()
        {
            loader?.WaitIdle();
            while (UploadReplacements())
            {
            }
        }

        // The replacements the loader has finished, uploaded in one submission up to the budget; whether any were
        private bool UploadReplacements()
        {
            if (loader == null)
            {
                return false;
            }

            var ready = new List<(GpuTexture Target, List<(int Width, int Height, byte[] Rgba)> Mips)>();
            long bytes = 0;
            while (bytes < ReplacementBudget && loader.TryTake(out GpuTexture? target, out List<(int Width, int Height, byte[] Rgba)>? mips))
            {
                if (!target!.Disposed)
                {
                    ready.Add((target, mips!));
                    bytes += mips!.Sum(m => (long)m.Rgba.Length);
                }
            }

            if (ready.Count == 0)
            {
                return false;
            }

            var uploads = new List<(GpuTexture Target, Image Image, HostBuffer Staging, int[] Offsets, List<(int Width, int Height, byte[] Rgba)> Mips)>();
            foreach ((GpuTexture target, List<(int Width, int Height, byte[] Rgba)> mips) in ready)
            {
                var image = new Image(context, (uint)mips[0].Width, (uint)mips[0].Height, VkFormat.R8G8B8A8Unorm,
                    VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled, mipLevels: (uint)mips.Count);
                var staging = new HostBuffer(context, (ulong)mips.Sum(m => m.Rgba.Length), VkBufferUsageFlags.TransferSrc);
                var offsets = new int[mips.Count];
                int at = 0;
                for (int m = 0; m < mips.Count; m++)
                {
                    offsets[m] = at;
                    mips[m].Rgba.AsSpan().CopyTo(new Span<byte>(staging.Mapped + at, mips[m].Rgba.Length));
                    at += mips[m].Rgba.Length;
                }

                staging.Flush();
                uploads.Add((target, image, staging, offsets, mips));
            }

            Submit(cmd =>
            {
                foreach ((_, Image image, HostBuffer staging, int[] offsets, List<(int Width, int Height, byte[] Rgba)> mips) in uploads)
                {
                    GpuTexture.Copy(this, cmd, staging, image, mips.Count, m => (offsets[m], mips[m].Width, mips[m].Height));
                }
            });

            foreach ((GpuTexture target, Image image, HostBuffer staging, _, _) in uploads)
            {
                staging.Dispose();
                target.Replace(image);
            }

            return true;
        }

        /// <summary>
        /// Replacements read and decoded on a thread of their own (EnhancedReplacements), in the order asked, each
        /// skipped if its texture is gone by then; the render thread takes the finished ones.
        /// </summary>
        private sealed class TextureLoader : IDisposable
        {
            private readonly EnhancedTexturePack pack;
            private readonly BlockingCollection<(GpuTexture Target, string Map, EnhancedTexture Texture)> requests = [];
            private readonly ConcurrentQueue<(GpuTexture Target, List<(int Width, int Height, byte[] Rgba)> Mips)> results = new();
            private readonly Thread thread;
            private int pending;

            public TextureLoader(EnhancedTexturePack pack)
            {
                this.pack = pack;
                thread = new Thread(Run) { IsBackground = true, Name = "Texture pack loader", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }

            public void Request(GpuTexture target, string map, EnhancedTexture texture)
            {
                Interlocked.Increment(ref pending);
                requests.Add((target, map, texture));
            }

            /// <summary>Until every request so far is done.</summary>
            public void WaitIdle()
            {
                while (Volatile.Read(ref pending) > 0)
                {
                    Thread.Sleep(1);
                }
            }

            public bool TryTake(out GpuTexture? target, out List<(int Width, int Height, byte[] Rgba)>? mips)
            {
                bool taken = results.TryDequeue(out var result);
                (target, mips) = (result.Target, result.Mips);
                return taken;
            }

            public void Dispose()
            {
                requests.CompleteAdding();
                thread.Join();
                requests.Dispose();
            }

            private void Run()
            {
                foreach ((GpuTexture target, string map, EnhancedTexture texture) in requests.GetConsumingEnumerable())
                {
                    if (!target.Disposed && EnhancedReplacements.World(pack, map, texture) is { } mips)
                    {
                        results.Enqueue((target, mips));
                    }

                    Interlocked.Decrement(ref pending);
                }
            }
        }
    }
}
