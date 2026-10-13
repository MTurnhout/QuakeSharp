using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Enhanced;

/// <summary>What a pixel of the 2D mask says wrote it last (design.md §11.6).</summary>
public static class EnhancedMask
{
    /// <summary>The 2D (draw.c) or nothing yet: the frame's own pixel is shown.</summary>
    public const byte Overlay = 0;

    /// <summary>The 3D view (R_RenderView): the enhanced view is shown.</summary>
    public const byte View = 1;

    /// <summary>Added to either: Draw_FadeScreen darkened it since (three pixels in four set to 0).</summary>
    public const byte Faded = 2;
}

/// <summary>The view r_refdef described: where it went on the frame, from where and how wide it looked.</summary>
public readonly record struct EnhancedView(
    int X, int Y, int Width, int Height, Vec3 Origin, Vec3 Angles, float FovX, float FovY, int Contents);

/// <summary>
/// An entity the frame showed. <see cref="Id"/> says where it came from (a slot of cl_entities, a static or temporary
/// entity, the view model), so interpolation finds it in the frame before; <see cref="Model"/> is the capture's number
/// for its model (0: none).
/// </summary>
public readonly record struct EnhancedEntity(
    int Id, int Model, Vec3 Origin, Vec3 Angles, int Frame, int Skin, int Colormap, int Effects, float Syncbase)
{
    /// <summary>The view model (cl.viewent).</summary>
    public const int ViewModel = -1;

    /// <summary>cl_static_entities[n] is StaticBase + n.</summary>
    public const int StaticBase = 0x10000;

    /// <summary>cl_temp_entities[n] is TempBase + n.</summary>
    public const int TempBase = 0x20000;

    /// <summary>Any other entity: not interpolated.</summary>
    public const int Other = int.MinValue;
}

/// <summary>A live particle: its slot in the pool, when it dies (a slot reused is another particle), where, its colour.</summary>
public readonly record struct EnhancedParticle(int Id, float Die, Vec3 Origin, byte Color);

/// <summary>A live dynamic light: its slot in cl_dlights and its key (a slot reused by another key is another light).</summary>
public readonly record struct EnhancedDlight(int Id, int Key, Vec3 Origin, float Radius, float Minlight);

/// <summary>
/// A snapshot of what one frame showed (design.md §11.6), taken by <see cref="EnhancedCapture"/> when the frame was
/// finished: the 8-bit frame and its 2D mask, the palette and the tables it was made with, the view, the entities, the
/// lights and the particles, and the renderer's cvars that decide what is drawn. Never written after it is made, so the
/// render thread reads it while the game goes on.
/// </summary>
public sealed class EnhancedFrame
{
    /// <summary>The frame's size (vid.width, vid.height).</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>The frame's palette indices, row after row (<see cref="Width"/> apart).</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>For each pixel, what wrote it last (<see cref="EnhancedMask"/>).</summary>
    public required byte[] Mask { get; init; }

    /// <summary>The palette the frame is shown through (256 RGB triples).</summary>
    public required byte[] Palette { get; init; }

    /// <summary>
    /// What V_UpdatePalette did to each channel of each base colour when it made the palette (the colour shifts blended
    /// in, then gammatable): 256 entries for red, then green, then blue.
    /// </summary>
    public required byte[] ChannelTables { get; init; }

    /// <summary>
    /// <see cref="ChannelTables"/> are what the palette was built from; false for a while after enhanced mode is
    /// switched on in a game (they are guessed from the shifts as they are then, until the next build).
    /// </summary>
    public bool ChannelTablesExact { get; init; }

    /// <summary>host_basepal: the palette before the colour shifts and gamma (256 RGB triples; empty before Host_Init).</summary>
    public byte[] BasePalette { get; init; } = [];

    /// <summary>vid.colormap: 64 rows of 256 palette indices, row 0 the brightest, as the software lights its pixels.</summary>
    public byte[] Colormap { get; init; } = [];

    /// <summary>The border's tile (gfx.wad's backtile, Draw_TileClear's pattern), row after row; empty before Draw_Init.</summary>
    public byte[] Backtile { get; init; } = [];

    public int BacktileWidth { get; init; }

    public int BacktileHeight { get; init; }

    /// <summary>cl.time.</summary>
    public required double Time { get; init; }

    /// <summary>The 3D view was drawn this frame.</summary>
    public required bool ViewDrawn { get; init; }

    public required EnhancedView View { get; init; }

    /// <summary>In a level (cls.signon == SIGNONS, a world model).</summary>
    public required bool InLevel { get; init; }

    /// <summary>The capture's number for the world model (0: none).</summary>
    public required int World { get; init; }

    /// <summary>The models by the capture's numbers (<see cref="EnhancedEntity.Model"/>, <see cref="World"/>; 0 is none).</summary>
    public IReadOnlyList<EnhancedModel?> Models { get; init; } = [null];

    /// <summary>Changes when the numbers start anew (a new level, or another capture): what was made of the models before goes.</summary>
    public int ModelGeneration { get; init; }

    /// <summary>r_refdef.ambientlight (r_ambient's light added everywhere).</summary>
    public int AmbientLight { get; init; }

    /// <summary>vid.aspect: the frame's pixels' height over their width as shown (320x200 shown 4:3: 1.2 tall; 0.8333 as vid.aspect has it).</summary>
    public float PixelAspect { get; init; } = 1;

    public required EnhancedEntity[] Entities { get; init; }

    public required EnhancedParticle[] Particles { get; init; }

    public required EnhancedDlight[] Dlights { get; init; }

    /// <summary>d_lightstylevalue of the 64 light styles (256 is normal).</summary>
    public required int[] LightStyles { get; init; }

    /// <summary>r_drawentities, r_fullbright, r_waterwarp, r_clearcolor as the software frame used them.</summary>
    public required bool DrawEntities { get; init; }

    public required bool Fullbright { get; init; }

    public required bool WaterWarp { get; init; }

    public required int ClearColor { get; init; }

    /// <summary>Moves of more than this in a frame (on any axis) are not interpolated, as CL_RelinkEntities' teleports.</summary>
    public const float MaxMove = 100;

    /// <summary>
    /// The frame <paramref name="fraction"/> of the way from <paramref name="previous"/> to <paramref name="next"/>:
    /// the view, the entities found in both (by their slot and model), the particles found in both (by their slot and
    /// death) and the lights found in both (by their slot and key) part way, cl.time part way; what moved more than
    /// <see cref="MaxMove"/>, appeared or went, and everything else (the 2D, the palette, the light styles) as
    /// <paramref name="next"/> has it.
    /// </summary>
    public static EnhancedFrame Lerp(EnhancedFrame previous, EnhancedFrame next, float fraction)
    {
        if (fraction >= 1 || previous == next)
        {
            return next;
        }

        fraction = Math.Max(fraction, 0);

        EnhancedView view = next.View;
        if (previous.ViewDrawn && next.ViewDrawn && Near(previous.View.Origin, next.View.Origin))
        {
            view = view with
            {
                Origin = LerpVector(previous.View.Origin, next.View.Origin, fraction),
                Angles = LerpAngles(previous.View.Angles, next.View.Angles, fraction),
            };
        }

        var entities = new EnhancedEntity[next.Entities.Length];
        Dictionary<int, EnhancedEntity> before = [];
        foreach (EnhancedEntity e in previous.Entities)
        {
            if (e.Id != EnhancedEntity.Other)
            {
                before[e.Id] = e;
            }
        }

        for (int i = 0; i < entities.Length; i++)
        {
            EnhancedEntity e = next.Entities[i];
            entities[i] = e.Id != EnhancedEntity.Other && before.TryGetValue(e.Id, out EnhancedEntity p)
                && p.Model == e.Model && Near(p.Origin, e.Origin)
                ? e with { Origin = LerpVector(p.Origin, e.Origin, fraction), Angles = LerpAngles(p.Angles, e.Angles, fraction) }
                : e;
        }

        var particles = new EnhancedParticle[next.Particles.Length];
        Dictionary<int, EnhancedParticle> particlesBefore = [];
        foreach (EnhancedParticle p in previous.Particles)
        {
            particlesBefore[p.Id] = p;
        }

        for (int i = 0; i < particles.Length; i++)
        {
            EnhancedParticle p = next.Particles[i];
            particles[i] = particlesBefore.TryGetValue(p.Id, out EnhancedParticle q) && q.Die == p.Die && Near(q.Origin, p.Origin)
                ? p with { Origin = LerpVector(q.Origin, p.Origin, fraction) }
                : p;
        }

        var dlights = new EnhancedDlight[next.Dlights.Length];
        for (int i = 0; i < dlights.Length; i++)
        {
            EnhancedDlight d = next.Dlights[i];
            EnhancedDlight? q = Array.Find(previous.Dlights, l => l.Id == d.Id && l.Key == d.Key);
            dlights[i] = q is { } l && Near(l.Origin, d.Origin)
                ? d with { Origin = LerpVector(l.Origin, d.Origin, fraction), Radius = l.Radius + ((d.Radius - l.Radius) * fraction) }
                : d;
        }

        return new EnhancedFrame
        {
            Width = next.Width,
            Height = next.Height,
            Pixels = next.Pixels,
            Mask = next.Mask,
            Palette = next.Palette,
            ChannelTables = next.ChannelTables,
            ChannelTablesExact = next.ChannelTablesExact,
            Backtile = next.Backtile,
            BasePalette = next.BasePalette,
            Colormap = next.Colormap,
            BacktileWidth = next.BacktileWidth,
            BacktileHeight = next.BacktileHeight,
            Time = previous.Time + ((next.Time - previous.Time) * fraction),
            ViewDrawn = next.ViewDrawn,
            View = view,
            InLevel = next.InLevel,
            World = next.World,
            Models = next.Models,
            ModelGeneration = next.ModelGeneration,
            AmbientLight = next.AmbientLight,
            PixelAspect = next.PixelAspect,
            Entities = entities,
            Particles = particles,
            Dlights = dlights,
            LightStyles = next.LightStyles,
            DrawEntities = next.DrawEntities,
            Fullbright = next.Fullbright,
            WaterWarp = next.WaterWarp,
            ClearColor = next.ClearColor,
        };
    }

    /// <summary>
    /// This frame with a rectangle of the screen's buffer drawn over it as 2D (D_BeginDirectRect's loading disc, and
    /// D_EndDirectRect putting back what was under it): <paramref name="width"/> by <paramref name="height"/> pixels at
    /// (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="pixels"/>, rows <paramref name="rowBytes"/> apart.
    /// </summary>
    public EnhancedFrame WithDirectRect(ReadOnlySpan<byte> pixels, int rowBytes, int x, int y, int width, int height)
    {
        byte[] frame = (byte[])Pixels.Clone();
        byte[] mask = (byte[])Mask.Clone();
        if (x >= 0 && y >= 0 && x + width <= Width && y + height <= Height)
        {
            for (int row = y; row < y + height; row++)
            {
                pixels.Slice((row * rowBytes) + x, width).CopyTo(frame.AsSpan((row * Width) + x, width));
                mask.AsSpan((row * Width) + x, width).Clear();
            }
        }

        return new EnhancedFrame
        {
            Width = Width,
            Height = Height,
            Pixels = frame,
            Mask = mask,
            Palette = Palette,
            ChannelTables = ChannelTables,
            ChannelTablesExact = ChannelTablesExact,
            Backtile = Backtile,
            BasePalette = BasePalette,
            Colormap = Colormap,
            BacktileWidth = BacktileWidth,
            BacktileHeight = BacktileHeight,
            Time = Time,
            ViewDrawn = ViewDrawn,
            View = View,
            InLevel = InLevel,
            World = World,
            Models = Models,
            ModelGeneration = ModelGeneration,
            AmbientLight = AmbientLight,
            PixelAspect = PixelAspect,
            Entities = Entities,
            Particles = Particles,
            Dlights = Dlights,
            LightStyles = LightStyles,
            DrawEntities = DrawEntities,
            Fullbright = Fullbright,
            WaterWarp = WaterWarp,
            ClearColor = ClearColor,
        };
    }

    /// <summary>Whether <see cref="ChannelTables"/> applied to <paramref name="basePalette"/> give <see cref="Palette"/>.</summary>
    public bool TablesMakePalette(ReadOnlySpan<byte> basePalette)
    {
        for (int i = 0; i < 768; i++)
        {
            if (ChannelTables[((i % 3) * 256) + basePalette[i]] != Palette[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool Near(in Vec3 a, in Vec3 b) =>
        Math.Abs(a[0] - b[0]) <= MaxMove && Math.Abs(a[1] - b[1]) <= MaxMove && Math.Abs(a[2] - b[2]) <= MaxMove;

    private static Vec3 LerpVector(in Vec3 a, in Vec3 b, float f) =>
        new(a[0] + ((b[0] - a[0]) * f), a[1] + ((b[1] - a[1]) * f), a[2] + ((b[2] - a[2]) * f));

    // each angle the shorter way round
    private static Vec3 LerpAngles(in Vec3 a, in Vec3 b, float f)
    {
        var result = default(Vec3);
        for (int i = 0; i < 3; i++)
        {
            float d = b[i] - a[i];
            d -= 360 * MathF.Round(d / 360);
            result[i] = a[i] + (d * f);
        }

        return result;
    }
}
