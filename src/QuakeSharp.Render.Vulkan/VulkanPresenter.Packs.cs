using System.Collections.Concurrent;
using QuakeSharp.Core.Enhanced;
using Vortice.Vulkan;
using static QuakeSharp.Render.Vulkan.VulkanContext;

namespace QuakeSharp.Render.Vulkan;

/// <summary>
/// Texture packs in the enhanced view (design.md §11.7, milestone 13): a world texture's or an alias skin's replacement
/// found, read and decoded on a loader thread (never the game thread, never through the game's files), uploaded by the
/// render thread a few at a time, and drawn in its place in the true-colour look while enhanced_textures is on. Until
/// then, and without a pack, the game's own texture is drawn.
/// </summary>
public sealed unsafe partial class VulkanPresenter
{
    private volatile EnhancedTexturePack? texturePack;

    /// <summary>The texture pack the enhanced view draws from (null: none); models made ready after it is set ask it
    /// for their textures.</summary>
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

    // alias.slang's AliasParams.flags: the skin's colours are a pack's, the player's colours already in them
    private const int AliasReplaced = 2;

    private sealed partial class WorldRenderer
    {
        // At most this much of replacements uploaded in one frame (each upload waits for the GPU)
        private const long ReplacementBudget = 64L << 20;

        // The skins with a pack's shirt and pants, kept for the colours players change to (the loader's alone)
        private readonly ConcurrentDictionary<(string Model, int Skin, int Frame), EnhancedSkinLayers> playerSkins = new();
        private TextureLoader? loader;
        private bool useReplacements;

        /// <summary>A texture on the GPU a pack's colours can replace once they are uploaded.</summary>
        private interface IReplaceable
        {
            /// <summary>Gone (its level's models dropped): the loader skips it, its replacement is not uploaded.</summary>
            bool Disposed { get; }

            void Replace(Image image);
        }

        // A brush model's textures asked of the pack, if there is one
        private void RequestReplacements(EnhancedBrushModel model, GpuTexture[] textures)
        {
            if (presenter.TexturePack is not { } pack)
            {
                return;
            }

            loader ??= new TextureLoader();
            string map = Path.GetFileNameWithoutExtension(model.Name);
            for (int i = 0; i < textures.Length; i++)
            {
                EnhancedTexture texture = model.Textures[i];
                loader.Request(textures[i], () => EnhancedReplacements.World(pack, map, texture));
            }
        }

        // An alias skin as an entity shows it (its colour translation's rows, -1 none) asked of the pack
        private void RequestSkin(IReplaceable target, EnhancedAliasModel model, (int Skin, int Frame) name, byte[] gameSkin,
            byte[] basePalette, int top, int bottom)
        {
            if (presenter.TexturePack is not { } pack)
            {
                return;
            }

            loader ??= new TextureLoader();
            loader.Request(target, () =>
            {
                (string, int, int) key = (model.Name, name.Skin, name.Frame);
                EnhancedSkinLayers? layers = playerSkins.TryGetValue(key, out EnhancedSkinLayers? kept)
                    ? kept
                    : EnhancedReplacements.SkinLayers(pack, model.Name, name.Skin, name.Frame);
                if (layers is { Shirt: not null, Pants: not null })
                {
                    playerSkins[key] = layers;
                }

                return layers == null
                    ? null
                    : EnhancedReplacements.Skin(layers, gameSkin, model.SkinWidth, model.SkinHeight, basePalette, top, bottom);
            });
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

            var ready = new List<(IReplaceable Target, List<(int Width, int Height, byte[] Rgba)> Mips)>();
            long bytes = 0;
            while (bytes < ReplacementBudget && loader.TryTake(out IReplaceable? target, out List<(int Width, int Height, byte[] Rgba)>? mips))
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

            var uploads = new List<(IReplaceable Target, Image Image, HostBuffer Staging, int[] Offsets, List<(int Width, int Height, byte[] Rgba)> Mips)>();
            foreach ((IReplaceable target, List<(int Width, int Height, byte[] Rgba)> mips) in ready)
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

            foreach ((IReplaceable target, Image image, HostBuffer staging, _, _) in uploads)
            {
                staging.Dispose();
                target.Replace(image);
            }

            return true;
        }

        /// <summary>
        /// A pack's skin for one way an alias model's skin is shown (its colour translation): drawn once uploaded, with
        /// the game's indices beside it (alias.slang's set 2), from a pool of its own.
        /// </summary>
        private sealed class GpuSkinReplacement : IReplaceable, IDisposable
        {
            private readonly WorldRenderer owner;
            private readonly VkImageView indicesView;
            private readonly VkDescriptorPool pool;
            private Image? image;
            private volatile bool disposed;

            public GpuSkinReplacement(WorldRenderer owner, VkImageView indicesView)
            {
                this.owner = owner;
                this.indicesView = indicesView;
                VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[2];
                sizes[0] = new() { type = VkDescriptorType.SampledImage, descriptorCount = 1 };
                sizes[1] = new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = 1 };
                VkDescriptorPoolCreateInfo poolInfo = new() { maxSets = 1, poolSizeCount = 2, pPoolSizes = sizes };
                Check(owner.context.Api.vkCreateDescriptorPool(&poolInfo, null, out pool), "vkCreateDescriptorPool");
            }

            /// <summary>The set with the pack's colours (null until they are uploaded).</summary>
            public VkDescriptorSet Set { get; private set; }

            public bool Disposed => disposed;

            public void Replace(Image uploaded)
            {
                image = uploaded;
                Set = owner.Allocate(pool, owner.textureSetLayout);
                VkDescriptorImageInfo indicesInfo = new() { imageView = indicesView, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                VkDescriptorImageInfo colorsInfo = new() { sampler = owner.colorSampler, imageView = uploaded.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[2];
                writes[0] = new() { dstSet = Set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &indicesInfo };
                writes[1] = new() { dstSet = Set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.CombinedImageSampler, pImageInfo = &colorsInfo };
                owner.context.Api.vkUpdateDescriptorSets(2, writes, 0, null);
            }

            public void Dispose()
            {
                disposed = true;
                image?.Dispose();
                owner.context.Api.vkDestroyDescriptorPool(pool);
            }
        }

        /// <summary>
        /// Replacements made on a thread of their own (EnhancedReplacements: read, decoded, made ready), in the order
        /// asked, each skipped if its texture is gone by then; the render thread takes the finished ones.
        /// </summary>
        private sealed class TextureLoader : IDisposable
        {
            private readonly BlockingCollection<(IReplaceable Target, Func<List<(int Width, int Height, byte[] Rgba)>?> Make)> requests = [];
            private readonly ConcurrentQueue<(IReplaceable Target, List<(int Width, int Height, byte[] Rgba)> Mips)> results = new();
            private readonly Thread thread;
            private int pending;

            public TextureLoader()
            {
                thread = new Thread(Run) { IsBackground = true, Name = "Texture pack loader", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }

            public void Request(IReplaceable target, Func<List<(int Width, int Height, byte[] Rgba)>?> make)
            {
                Interlocked.Increment(ref pending);
                requests.Add((target, make));
            }

            /// <summary>Until every request so far is done.</summary>
            public void WaitIdle()
            {
                while (Volatile.Read(ref pending) > 0)
                {
                    Thread.Sleep(1);
                }
            }

            public bool TryTake(out IReplaceable? target, out List<(int Width, int Height, byte[] Rgba)>? mips)
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
                foreach ((IReplaceable target, Func<List<(int Width, int Height, byte[] Rgba)>?> make) in requests.GetConsumingEnumerable())
                {
                    if (!target.Disposed && make() is { } mips)
                    {
                        results.Enqueue((target, mips));
                    }

                    Interlocked.Decrement(ref pending);
                }
            }
        }
    }
}
