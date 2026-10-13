using QuakeSharp.Core.Client;
using QuakeSharp.Core.Render;
using QuakeSharp.Core.UI;

namespace QuakeSharp.Core.Enhanced;

/// <summary>
/// The enhanced mode's eyes on the game (design.md §11.6): attached as the engine's <c>enhanced</c> while enhanced mode
/// is on, null otherwise. The ported code calls it at four seams, and it only reads the game or writes its own buffers:
/// draw.c's writes mark the 2D mask, R_RenderView marks the view's rectangle, V_UpdatePalette tells what it built the
/// palette from, and VID_Update, when the frame is finished, has a snapshot (<see cref="EnhancedFrame"/>) taken and
/// handed to <see cref="Captured"/>. It never reads the clock, draws rand() or calls a ported function that writes.
/// </summary>
public sealed class EnhancedCapture
{
    private readonly QuakeEngine engine;
    private readonly Dictionary<Entity, int> entityIds = [];
    private readonly Dictionary<Model.Model, int> modelIds = [];

    // the models made ready, by number (0 is none), for the level whose world is worldOf; a new level starts anew
    private EnhancedModel?[] models = [null];
    private Model.Model? worldOf;
    private int generation;
    private readonly byte[] channelTables = new byte[3 * 256];

    // the 2D mask, parallel to the screen's buffer (the same offsets), and the buffer it belongs to
    private byte[] mask = [];
    private byte[]? maskOf;

    // the particle pool the ids are slots of
    private Particle[]? particlePool;
    private Dictionary<Particle, int> particleIds = [];

    // R_RenderView drew the view since the last frame
    private bool viewDrawn;

    // the tables are what the palette was built from (not guessed at being switched on)
    private bool tablesExact;

    // the base palette and the colormap, copied when they change (a mode set remaps the colormap)
    private byte[] basePalette = [], basePaletteOf = [];
    private byte[] colormap = [], colormapOf = [];

    // the border's tile, copied once
    private QPic? backtileOf;
    private byte[] backtile = [];

    public EnhancedCapture(QuakeEngine engine)
    {
        this.engine = engine;

        for (int i = 0; i < engine.cl_entities.Length; i++)
        {
            entityIds[engine.cl_entities[i]] = i;
        }

        for (int i = 0; i < engine.cl_static_entities.Length; i++)
        {
            entityIds[engine.cl_static_entities[i]] = EnhancedEntity.StaticBase + i;
        }

        for (int i = 0; i < engine.cl_temp_entities.Length; i++)
        {
            entityIds[engine.cl_temp_entities[i]] = EnhancedEntity.TempBase + i;
        }

        entityIds[engine.cl.viewent] = EnhancedEntity.ViewModel;

        if (!engine.host_initialized)
        {
            // until V_UpdatePalette builds one, the palette is the base palette as it is (VID_Init's)
            for (int i = 0; i < 256; i++)
            {
                channelTables[i] = channelTables[256 + i] = channelTables[512 + i] = (byte)i;
            }

            tablesExact = true;
        }
        else
        {
            // switched on in a game: the palette was built before, most likely from the shifts as they are now; the
            // next build tells
            PaletteBuilt();
            tablesExact = false;
        }
    }

    /// <summary>Raised on the game thread with each finished frame's snapshot.</summary>
    public event Action<EnhancedFrame>? Captured;

    /// <summary>The enhanced mode's mixer, while it has the sound (snd_dma.c's S_Update_ and S_ClearBuffer call it).</summary>
    public EnhancedMixer? Sound { get; set; }

    /// <summary>The last snapshot taken.</summary>
    public EnhancedFrame? Latest { get; private set; }

    /// <summary>
    /// The tests' guard: a count of the engine's reads of the clock and draws of rand(), which must not change across
    /// the capture's work at the end of a frame (with the handlers of <see cref="Captured"/>) or a palette's build.
    /// </summary>
    public Func<long>? EngineCounts { get; init; }

    /// <summary>The capture's numbers for the models it has seen, 1 up (0 is none).</summary>
    public IReadOnlyDictionary<Model.Model, int> Models => modelIds;

    // draw.c wrote count pixels of the screen's buffer from offset on
    internal void Overlay(byte[] buffer, int offset, int count) =>
        MaskOf(buffer).AsSpan(offset, count).Fill(EnhancedMask.Overlay);

    // draw.c wrote one pixel
    internal void Overlay(byte[] buffer, int offset) => MaskOf(buffer)[offset] = EnhancedMask.Overlay;

    // draw.c: Draw_FadeScreen darkened the whole screen
    internal void Fade()
    {
        byte[] m = MaskOf(engine.vid.buffer);
        for (int y = 0; y < engine.vid.height; y++)
        {
            int row = engine.vid.bufferoffset + (int)(engine.vid.rowbytes * y);
            for (int x = 0; x < engine.vid.width; x++)
            {
                m[row + x] |= EnhancedMask.Faded;
            }
        }
    }

    // r_main.c: R_RenderView drew the view into scr_vrect
    internal void ViewDrawn()
    {
        byte[] m = MaskOf(engine.vid.buffer);
        VRect r = engine.scr_vrect;
        for (int y = r.y; y < r.y + r.height; y++)
        {
            m.AsSpan(engine.vid.bufferoffset + (y * (int)engine.vid.rowbytes) + r.x, r.width).Fill(EnhancedMask.View);
        }

        viewDrawn = true;
    }

    // view.c: V_UpdatePalette built the palette from the colour shifts and gammatable as they are now
    internal void PaletteBuilt()
    {
        long counts = EngineCounts?.Invoke() ?? 0;
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < 256; i++)
            {
                int v = i;
                for (int j = 0; j < ClientState.NUM_CSHIFTS; j++)
                {
                    v += (engine.cl.cshifts[j].percent * (engine.cl.cshifts[j].destcolor[c] - v)) >> 8;
                }

                channelTables[(c * 256) + i] = engine.gammatable[v];
            }
        }

        tablesExact = true;
        Check(counts);
    }

    // vid_win.c: VID_Update: the frame is finished
    internal void Frame()
    {
        long counts = EngineCounts?.Invoke() ?? 0;
        Latest = Snapshot();
        viewDrawn = false;
        Captured?.Invoke(Latest);
        Check(counts);
    }

    private void Check(long counts)
    {
        if (EngineCounts != null && EngineCounts() != counts)
        {
            throw new InvalidOperationException("the enhanced mode read the clock or drew rand()");
        }
    }

    private byte[] MaskOf(byte[] buffer)
    {
        if (!ReferenceEquals(buffer, maskOf))
        {
            // a new buffer (a mode was set): nothing of the view on it yet
            mask = new byte[buffer.Length];
            maskOf = buffer;
        }

        return mask;
    }

    private EnhancedFrame Snapshot()
    {
        QuakeEngine e = engine;
        int width = (int)e.vid.width, height = (int)e.vid.height;
        (byte[] frame, int rowbytes) = e.VID_Frame();
        byte[] m = MaskOf(frame);
        byte[] pixels = new byte[width * height];
        byte[] packedMask = new byte[width * height];
        for (int y = 0; y < height && ((y * rowbytes) + width) <= frame.Length; y++)
        {
            frame.AsSpan(y * rowbytes, width).CopyTo(pixels.AsSpan(y * width, width));
            m.AsSpan(y * rowbytes, width).CopyTo(packedMask.AsSpan(y * width, width));
        }

        bool inLevel = e.cls.signon == QuakeEngine.SIGNONS && e.cl.worldmodel != null;
        if (!ReferenceEquals(e.cl.worldmodel, worldOf))
        {
            // a new level: its models numbered anew, the last level's let go
            worldOf = e.cl.worldmodel;
            modelIds.Clear();
            models = [null];
            generation++;
        }

        // the entities only with a view drawn in a level (the screen is also updated while a level loads, from
        // Con_Printf, with the last level's entities and no world)
        var entities = new List<EnhancedEntity>(e.cl_numvisedicts + 1);
        for (int i = 0; i < (viewDrawn && e.cl.worldmodel != null ? e.cl_numvisedicts : 0); i++)
        {
            // r_main.c: R_DrawEntitiesOnList: "don't draw the player"
            if (e.cl_visedicts[i] is { } ent && !ReferenceEquals(ent, e.cl_entities[e.cl.viewentity]))
            {
                entities.Add(Entity(ent, viewModel: false));
            }
        }

        // r_main.c: R_DrawViewModel's conditions
        if (viewDrawn && e.cl.worldmodel != null && e.r_drawviewmodel.value != 0 && !e.r_fov_greater_than_90
            && (e.cl.items & QuakeDef.IT_INVISIBILITY) == 0 && e.cl.stats[QuakeDef.STAT_HEALTH] > 0
            && e.cl.viewent.model != null)
        {
            entities.Add(Entity(e.cl.viewent, viewModel: true));
        }

        if (!ReferenceEquals(particlePool, e.particles))
        {
            particlePool = e.particles;
            particleIds = [];
            for (int i = 0; i < particlePool.Length; i++)
            {
                particleIds[particlePool[i]] = i;
            }
        }

        var particles = new List<EnhancedParticle>();
        for (Particle? p = e.active_particles; p != null; p = p.next)
        {
            particles.Add(new EnhancedParticle(particleIds.GetValueOrDefault(p, -1), p.die, p.org, (byte)(int)p.color));
        }

        var dlights = new List<EnhancedDlight>();
        for (int i = 0; i < e.cl_dlights.Length; i++)
        {
            Dlight d = e.cl_dlights[i];
            if (d.die >= e.cl.time && d.radius != 0)
            {
                dlights.Add(new EnhancedDlight(i, d.key, d.origin, d.radius, d.minlight));
            }
        }

        if (e.draw_backtile is { } tile && !ReferenceEquals(tile, backtileOf))
        {
            backtileOf = tile;
            backtile = tile.data[..(tile.width * tile.height)];
        }

        if (!ReferenceEquals(e.host_basepal, basePaletteOf))
        {
            basePaletteOf = e.host_basepal;
            basePalette = e.host_basepal.Length >= 768 ? e.host_basepal[..768] : [];
        }

        if (!ReferenceEquals(e.vid.colormap, colormapOf) || (colormap.Length > 0 && !colormap.AsSpan().SequenceEqual(e.vid.colormap.AsSpan(0, colormap.Length))))
        {
            colormapOf = e.vid.colormap;
            colormap = e.vid.colormap.Length >= 64 * 256 ? e.vid.colormap[..(64 * 256)] : [];
        }

        RefDef r = e.r_refdef;
        return new EnhancedFrame
        {
            Width = width,
            Height = height,
            Pixels = pixels,
            Mask = packedMask,
            Palette = (byte[])e.vid_curpal.Clone(),
            ChannelTables = (byte[])channelTables.Clone(),
            ChannelTablesExact = tablesExact,
            Backtile = backtile,
            BasePalette = basePalette,
            Colormap = colormap,
            BacktileWidth = backtileOf?.width ?? 0,
            BacktileHeight = backtileOf?.height ?? 0,
            Time = e.cl.time,
            ViewDrawn = viewDrawn,
            View = new EnhancedView(e.scr_vrect.x, e.scr_vrect.y, e.scr_vrect.width, e.scr_vrect.height,
                r.vieworg, r.viewangles, r.fov_x, r.fov_y, viewDrawn && e.r_viewleaf != null ? e.r_viewleaf.contents : 0),
            InLevel = inLevel,
            World = inLevel ? ModelId(e.cl.worldmodel) : 0,
            Models = models,
            ModelGeneration = generation,
            AmbientLight = r.ambientlight,
            Warp = viewDrawn && e.r_dowarp,
            PixelAspect = e.vid.aspect,
            Entities = [.. entities],
            Particles = [.. particles],
            Dlights = [.. dlights],
            LightStyles = e.d_lightstylevalue[..QuakeDef.MAX_LIGHTSTYLES],
            DrawEntities = e.r_drawentities.value != 0,
            Fullbright = e.r_fullbright.value != 0,
            WaterWarp = e.r_waterwarp.value != 0,
            ClearColor = (int)e.r_clearcolor.value & 0xff,
        };
    }

    private EnhancedEntity Entity(Entity ent, bool viewModel)
    {
        // the colour translation: a player's (cl.scores[n].translations) as n + 1, 0 for the normal colormap
        int colormap = 0;
        int top = -1, bottom = -1;
        if (ent.colormap != null && engine.cl.scores != null && !ReferenceEquals(ent.colormap, engine.vid.colormap))
        {
            colormap = Array.FindIndex(engine.cl.scores, s => ReferenceEquals(s.translations, ent.colormap)) + 1;
            if (colormap > 0)
            {
                // cl_parse.c: CL_NewTranslation's colours
                top = engine.cl.scores[colormap - 1].colors & 0xf0;
                bottom = (engine.cl.scores[colormap - 1].colors & 15) << 4;
            }
        }

        var entity = new EnhancedEntity(entityIds.GetValueOrDefault(ent, EnhancedEntity.Other), ModelId(ent.model),
            ent.origin, ent.angles, ent.frame, ent.skinnum, colormap, ent.effects, ent.syncbase)
        {
            Top = top,
            Bottom = bottom,
        };

        return ent.model?.type == Model.modtype_t.mod_alias ? AliasLighting(entity, ent, viewModel) : entity;
    }

    // r_main.c: R_DrawEntitiesOnList's and R_DrawViewModel's lighting of an alias model, then r_alias.c's
    // R_AliasSetupLighting (R_LightPoint writes nothing; the rest is copied)
    private EnhancedEntity AliasLighting(EnhancedEntity entity, Entity ent, bool viewModel)
    {
        const int LIGHT_MIN = 5;
        QuakeEngine e = engine;
        int j = e.R_LightPoint(ent.origin);
        if (viewModel && j < 24)
        {
            j = 24; // allways give some light on gun
        }

        int ambient = j;
        int shade = j;
        for (int lnum = 0; lnum < e.cl_dlights.Length; lnum++)
        {
            Dlight dl = e.cl_dlights[lnum];
            if ((viewModel && dl.radius == 0) || dl.die < e.cl.time)
            {
                continue;
            }

            Common.Vec3 dist = default;
            Common.MathLib.VectorSubtract(ent.origin, dl.origin, ref dist);
            float add = dl.radius - Common.MathLib.Length(dist);
            if (add > 0)
            {
                ambient = Numerics.X86.FtoI(ambient + add);
            }
        }

        // clamp lighting so it doesn't overbright as much
        if (ambient > 128)
        {
            ambient = 128;
        }

        if (ambient + shade > 192)
        {
            shade = 192 - ambient;
        }

        // r_alias.c: R_AliasSetupLighting
        int r_ambientlight = Math.Max(ambient, LIGHT_MIN);
        r_ambientlight = (255 - r_ambientlight) << QuakeEngine.VID_CBITS;
        if (r_ambientlight < LIGHT_MIN)
        {
            r_ambientlight = LIGHT_MIN;
        }

        float r_shadelight = Math.Max(shade, 0) * QuakeEngine.VID_GRADES;

        // the lighting vector ({-1, 0, 0}) into the model's frame of reference (R_AliasSetUpTransform's axes, the
        // pitch turned the other way)
        Common.Vec3 angles = default, forward = default, right = default, up = default;
        angles[0] = -ent.angles[0];
        angles[1] = ent.angles[1];
        angles[2] = ent.angles[2];
        Common.MathLib.AngleVectors(angles, ref forward, ref right, ref up);
        var lightvec = new Common.Vec3(-forward[0], right[0], -up[0]);

        return entity with { LightAmbient = r_ambientlight, LightShade = r_shadelight, LightVector = lightvec };
    }

    // The model's number, the model made ready the first time it is seen (the array of them replaced, not written, so
    // the snapshots already handed on keep theirs)
    private int ModelId(Model.Model? model)
    {
        if (model == null)
        {
            return 0;
        }

        if (!modelIds.TryGetValue(model, out int id))
        {
            id = models.Length;
            modelIds[model] = id;
            models = [.. models, null];
            EnhancedModel made = Make(model); // a submodel numbers the world first
            EnhancedModel?[] with = (EnhancedModel?[])models.Clone();
            with[id] = made;
            models = with;
        }

        return id;
    }

    private EnhancedModel Make(Model.Model model)
    {
        // the model's data as loaded (model.c keeps it in the cache; QuakeSharp's cache never lets it go, design.md
        // decision 22), read, never reloaded
        if (model.type == Model.modtype_t.mod_alias)
        {
            return model.cache is Model.AliasHdr header
                ? new EnhancedModel { Name = model.name, Alias = EnhancedAliasModel.Build(model, header) }
                : new EnhancedModel { Name = model.name };
        }

        if (model.type == Model.modtype_t.mod_sprite)
        {
            return model.cache is Model.MSprite sprite
                ? new EnhancedModel { Name = model.name, Sprite = EnhancedSpriteModel.Build(model, sprite) }
                : new EnhancedModel { Name = model.name };
        }

        if (model.type != Model.modtype_t.mod_brush)
        {
            return new EnhancedModel { Name = model.name };
        }

        // a submodel of the world ("*n") draws the world's surfaces of its submodel
        if (model.name.StartsWith('*') && engine.cl.worldmodel is { } world && !ReferenceEquals(model, world))
        {
            return new EnhancedModel { Name = model.name, Parent = ModelId(world), Submodel = Common.QString.Q_atoi(model.name[1..]) };
        }

        return new EnhancedModel { Name = model.name, Brush = EnhancedBrushModel.Build(model) };
    }
}
