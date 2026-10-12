using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Render;
using Channel = QuakeSharp.Core.Sound.Channel;

namespace QuakeSharp.Core.Diagnostics;

/// <summary>
/// The lines of the oracle's traces (tools/oracle/src/trace.c, the format in tools/oracle/README.md), from an engine's
/// state: every field trace.c hashes, in the same order, as FNV-1a over its bytes (floats and doubles as their bits,
/// strings without their 0, pointers as what they point at).
/// </summary>
public sealed class TraceWriter
{
    /// <summary>The trace format's version (the H line).</summary>
    public const int Version = 3;

    private const ulong FNV_OFFSET = 0xcbf29ce484222325UL;
    private const ulong FNV_PRIME = 0x100000001b3UL;

    private readonly QuakeEngine engine;
    private readonly int framesPerSecond;
    private int frame;

    public TraceWriter(QuakeEngine engine, int framesPerSecond)
    {
        this.engine = engine;
        this.framesPerSecond = framesPerSecond;
    }

    /// <summary>The trace's frames so far.</summary>
    public int Frame => frame;

    /// <summary>The lines of the frame just run (with the H line before the first).</summary>
    public IReadOnlyList<string> FrameLines()
    {
        QuakeEngine e = engine;
        var lines = new List<string>(11);
        frame++;

        if (frame == 1)
        {
            lines.Add(Invariant($"H {Version} {e.vid.width} {e.vid.height} {framesPerSecond}"));
        }

        lines.Add(Invariant($"F {frame} {e.host_framecount} {BitConverter.DoubleToInt64Bits(e.realtime):x16} {e.RandSeed:x8} {e.RandCalls}"));
        lines.Add(Invariant($"C {Client():x16}"));
        lines.Add(Invariant($"E {Entities():x16}"));
        lines.Add(Invariant($"D {Drawn():x16}"));
        ulong x = Effects(out int particles);
        lines.Add(Invariant($"X {x:x16} {particles}"));
        lines.Add(Invariant($"S {Sound():x16}"));
        lines.Add(Invariant($"T {Text():x16}"));
        lines.Add(Invariant($"R {View():x16}"));
        lines.Add(Invariant($"V {Picture():x16} {Palette():x16}"));
        return lines;
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The oracle's -tracedump: the entities field by field (floats as their bits), as trace.c writes them.</summary>
    public string Dump()
    {
        QuakeEngine e = engine;
        var sb = new StringBuilder();
        int i;

        for (i = 0; i < e.cl.num_entities; i++)
        {
            DumpEntity(sb, "entity", i, e.cl_entities[i]);
        }

        for (i = 0; i < e.cl.num_statics; i++)
        {
            DumpEntity(sb, "static", i, e.cl_static_entities[i]);
        }

        DumpEntity(sb, "viewent", 0, e.cl.viewent);
        for (i = 0; i < QuakeEngine.MAX_TEMP_ENTITIES; i++)
        {
            DumpEntity(sb, "temp", i, e.cl_temp_entities[i]);
        }

        for (i = 0; i < e.cl_numvisedicts; i++)
        {
            Entity? v = e.cl_visedicts[i];
            int n = Array.IndexOf(e.cl_entities, v);
            int t = Array.IndexOf(e.cl_temp_entities, v);
            int st = Array.IndexOf(e.cl_static_entities, v);
            sb.Append(Invariant($"visedict {i} {(n >= 0 ? n : t >= 0 ? 0x20000 + t : st >= 0 ? 0x10000 + st : -1)}\n"));
        }

        return sb.ToString();
    }

    /// <summary>The oracle's -traceshot: width, height (32-bit), the palette, the pixels row by row.</summary>
    public byte[] Shot()
    {
        Video.VidDef vid = engine.vid;
        int width = (int)vid.width, height = (int)vid.height;
        byte[] shot = new byte[8 + 768 + (width * height)];
        BinaryPrimitives.WriteInt32LittleEndian(shot, width);
        BinaryPrimitives.WriteInt32LittleEndian(shot.AsSpan(4), height);
        engine.vid_curpal.CopyTo(shot, 8);
        for (int y = 0; y < height; y++)
        {
            vid.buffer.AsSpan(vid.bufferoffset + (y * (int)vid.rowbytes), width).CopyTo(shot.AsSpan(8 + 768 + (y * width)));
        }

        return shot;
    }

    private void DumpEntity(StringBuilder sb, string what, int i, Entity e)
    {
        sb.Append(Invariant($"{what} {i} forcelink {(e.forcelink ? 1 : 0)} update_type {e.update_type}"));
        DumpVec(sb, "baseline.origin", e.baseline.origin);
        DumpVec(sb, "baseline.angles", e.baseline.angles);
        sb.Append(Invariant($" modelindex {e.baseline.modelindex} frame {e.baseline.frame} colormap {e.baseline.colormap} skin {e.baseline.skin} effects {e.baseline.effects}"));
        sb.Append(Invariant($" msgtime {BitConverter.DoubleToInt64Bits(e.msgtime):x16}"));
        DumpVec(sb, "msg_origins0", e.msg_origins[0]);
        DumpVec(sb, "msg_origins1", e.msg_origins[1]);
        DumpVec(sb, "origin", e.origin);
        DumpVec(sb, "msg_angles0", e.msg_angles[0]);
        DumpVec(sb, "msg_angles1", e.msg_angles[1]);
        DumpVec(sb, "angles", e.angles);
        int colormap = e.colormap == engine.vid.colormap ? 0
            : engine.cl.scores is { } scores && Array.FindIndex(scores, s => s.translations == e.colormap) is int n && n >= 0 && n < engine.cl.maxclients ? 1 + n
            : -1;
        sb.Append(Invariant($" model {e.model?.name ?? ""} frame {e.frame} syncbase {BitConverter.SingleToInt32Bits(e.syncbase):x8} colormap {colormap} effects {e.effects} skinnum {e.skinnum}\n"));
    }

    private static void DumpVec(StringBuilder sb, string name, in Vec3 v) =>
        sb.Append(Invariant($" {name} {BitConverter.SingleToInt32Bits(v[0]):x8} {BitConverter.SingleToInt32Bits(v[1]):x8} {BitConverter.SingleToInt32Bits(v[2]):x8}"));

    // ===============================================================================
    // HASHES
    // ===============================================================================

    private struct Hash
    {
        public ulong h;

        public Hash() => h = FNV_OFFSET;

        public void Bytes(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                h ^= b;
                h *= FNV_PRIME;
            }
        }

        public void Int(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            Bytes(b);
        }

        public void Bool(bool v) => Int(v ? 1 : 0);

        public void Float(float v) => Int(BitConverter.SingleToInt32Bits(v));

        public void Double(double v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, BitConverter.DoubleToInt64Bits(v));
            Bytes(b);
        }

        public void Vec(in Vec3 v)
        {
            Float(v[0]);
            Float(v[1]);
            Float(v[2]);
        }

        // the string up to its 0, then 4 zero bytes, so "a" "b" and "ab" differ
        public void String(string s)
        {
            int end = s.IndexOf('\0', StringComparison.Ordinal);
            Bytes(Encoding.Latin1.GetBytes(end < 0 ? s : s[..end]));
            Int(0);
        }

        public void Model(Model.Model? m) => String(m?.name ?? "");
    }

    // a colormap: 0 the normal one, 1 + n player n's translation, -1 anything else
    private void H_Colormap(ref Hash h, byte[]? colormap)
    {
        ClientState cl = engine.cl;

        if (colormap == engine.vid.colormap)
        {
            h.Int(0);
            return;
        }

        for (int i = 0; i < cl.maxclients && cl.scores != null; i++)
        {
            if (colormap == cl.scores[i].translations)
            {
                h.Int(1 + i);
                return;
            }
        }

        h.Int(-1);
    }

    private static void H_EntityState(ref Hash h, in EntityState s)
    {
        h.Vec(s.origin);
        h.Vec(s.angles);
        h.Int(s.modelindex);
        h.Int(s.frame);
        h.Int(s.colormap);
        h.Int(s.skin);
        h.Int(s.effects);
    }

    private void H_Entity(ref Hash h, Entity e)
    {
        h.Bool(e.forcelink);
        h.Int(e.update_type);
        H_EntityState(ref h, e.baseline);
        h.Double(e.msgtime);
        h.Vec(e.msg_origins[0]);
        h.Vec(e.msg_origins[1]);
        h.Vec(e.origin);
        h.Vec(e.msg_angles[0]);
        h.Vec(e.msg_angles[1]);
        h.Vec(e.angles);
        h.Model(e.model);
        h.Int(e.frame);
        h.Float(e.syncbase);
        H_Colormap(ref h, e.colormap);
        h.Int(e.effects);
        h.Int(e.skinnum);
    }

    // what the renderer writes into an entity (the D line), apart from what the client keeps in it
    private void H_EntityDrawn(ref Hash h, Entity e)
    {
        h.Int(e.visframe);
        h.Int(e.dlightframe);
        h.Int(e.dlightbits);
        h.Int(e.trivial_accept);
        // R_SplitEntityOnNode and R_SplitEntityOnNode2 can stop at a leaf: its index in the leafs, 0x10000 on
        h.Int(e.topnode == null || engine.cl.worldmodel == null ? -1
            : e.topnode is MLeaf leaf ? 0x10000 + leaf.index
            : ((MNode)e.topnode).index);
    }

    // an entity pointer: which array it is in and where
    private void H_EntityRef(ref Hash h, Entity? e)
    {
        int i = Array.IndexOf(engine.cl_entities, e);
        if (e != null && i >= 0)
        {
            h.Int(i);
        }
        else if (e != null && (i = Array.IndexOf(engine.cl_static_entities, e)) >= 0)
        {
            h.Int(0x10000 + i);
        }
        else if (e != null && (i = Array.IndexOf(engine.cl_temp_entities, e)) >= 0)
        {
            h.Int(0x20000 + i);
        }
        else if (e != null && e == engine.cl.viewent)
        {
            h.Int(0x30000);
        }
        else
        {
            h.Int(-1);
        }
    }

    private static void H_Cshift(ref Hash h, in Cshift c)
    {
        h.Int(c.destcolor[0]);
        h.Int(c.destcolor[1]);
        h.Int(c.destcolor[2]);
        h.Int(c.percent);
    }

    // ===============================================================================
    // THE LINES
    // ===============================================================================

    // C: the client state (cls, cl)
    private ulong Client()
    {
        var h = new Hash();
        ClientStatic cls = engine.cls;
        ClientState cl = engine.cl;
        int i;

        h.Int((int)cls.state);
        h.Int(cls.signon);
        h.Bool(cls.demoplayback);
        h.Int(cls.demonum);

        h.Int(cl.movemessages);
        h.Vec(cl.cmd.viewangles);
        h.Float(cl.cmd.forwardmove);
        h.Float(cl.cmd.sidemove);
        h.Float(cl.cmd.upmove);
        for (i = 0; i < QuakeDef.MAX_CL_STATS; i++)
        {
            h.Int(cl.stats[i]);
        }

        h.Int(cl.items);
        for (i = 0; i < 32; i++)
        {
            h.Float(cl.item_gettime[i]);
        }

        h.Float(cl.faceanimtime);
        for (i = 0; i < ClientState.NUM_CSHIFTS; i++)
        {
            H_Cshift(ref h, cl.cshifts[i]);
        }

        for (i = 0; i < ClientState.NUM_CSHIFTS; i++)
        {
            H_Cshift(ref h, cl.prev_cshifts[i]);
        }

        h.Vec(cl.mviewangles[0]);
        h.Vec(cl.mviewangles[1]);
        h.Vec(cl.viewangles);
        h.Vec(cl.mvelocity[0]);
        h.Vec(cl.mvelocity[1]);
        h.Vec(cl.velocity);
        h.Vec(cl.punchangle);
        h.Float(cl.idealpitch);
        h.Float(cl.pitchvel);
        h.Bool(cl.nodrift);
        h.Float(cl.driftmove);
        h.Double(cl.laststop);
        h.Float(cl.viewheight);
        h.Float(cl.crouch);
        h.Int(cl.paused);
        h.Bool(cl.onground);
        h.Bool(cl.inwater);
        h.Int(cl.intermission);
        h.Int(cl.completed_time);
        h.Double(cl.mtime[0]);
        h.Double(cl.mtime[1]);
        h.Double(cl.time);
        h.Double(cl.oldtime);
        h.Float(cl.last_received_message);
        h.String(cl.levelname);
        h.Int(cl.viewentity);
        h.Int(cl.maxclients);
        h.Int(cl.gametype);
        h.Model(cl.worldmodel);
        h.Int(cl.num_entities);
        h.Int(cl.num_statics);
        h.Int(cl.cdtrack);
        h.Int(cl.looptrack);
        for (i = 0; i < cl.maxclients && cl.scores != null; i++)
        {
            h.String(cl.scores[i].name);
            h.Float(cl.scores[i].entertime);
            h.Int(cl.scores[i].frags);
            h.Int(cl.scores[i].colors);
        }

        return h.h;
    }

    // E: the entities (cl_entities up to cl.num_entities, the statics, the view model, the visible list)
    private ulong Entities()
    {
        var h = new Hash();
        ClientState cl = engine.cl;
        int i;

        for (i = 0; i < cl.num_entities; i++)
        {
            H_Entity(ref h, engine.cl_entities[i]);
        }

        for (i = 0; i < cl.num_statics; i++)
        {
            H_Entity(ref h, engine.cl_static_entities[i]);
        }

        H_Entity(ref h, cl.viewent);

        // the visible entities the client listed (the renderer adds the static ones as it finds them: the D line)
        int n = 0;
        for (i = 0; i < engine.cl_numvisedicts; i++)
        {
            if (!IsStatic(engine.cl_visedicts[i]))
            {
                n++;
            }
        }

        h.Int(n);
        for (i = 0; i < engine.cl_numvisedicts; i++)
        {
            if (!IsStatic(engine.cl_visedicts[i]))
            {
                H_EntityRef(ref h, engine.cl_visedicts[i]);
            }
        }

        return h.h;
    }

    private bool IsStatic(Entity? e) => e != null && Array.IndexOf(engine.cl_static_entities, e) >= 0;

    // D: what the renderer writes into the entities (cl_entities, the statics, the view model, the temp entities) and
    // the visible list it completes
    private ulong Drawn()
    {
        var h = new Hash();
        ClientState cl = engine.cl;
        int i;

        for (i = 0; i < cl.num_entities; i++)
        {
            H_EntityDrawn(ref h, engine.cl_entities[i]);
        }

        for (i = 0; i < cl.num_statics; i++)
        {
            H_EntityDrawn(ref h, engine.cl_static_entities[i]);
        }

        H_EntityDrawn(ref h, cl.viewent);
        for (i = 0; i < QuakeEngine.MAX_TEMP_ENTITIES; i++)
        {
            H_EntityDrawn(ref h, engine.cl_temp_entities[i]);
        }

        // the visible list, with the static entities R_StoreEfrags added
        h.Int(engine.cl_numvisedicts);
        for (i = 0; i < engine.cl_numvisedicts; i++)
        {
            H_EntityRef(ref h, engine.cl_visedicts[i]);
        }

        return h.h;
    }

    // X: the effects (temp entities, beams, dynamic lights, light styles, particles)
    private ulong Effects(out int particles)
    {
        var h = new Hash();
        int i;

        for (i = 0; i < QuakeEngine.MAX_TEMP_ENTITIES; i++)
        {
            H_Entity(ref h, engine.cl_temp_entities[i]);
        }

        for (i = 0; i < QuakeEngine.MAX_BEAMS; i++)
        {
            Beam b = engine.cl_beams[i];
            h.Int(b.entity);
            h.Model(b.model);
            h.Float(b.endtime);
            h.Vec(b.start);
            h.Vec(b.end);
        }

        for (i = 0; i < QuakeEngine.MAX_DLIGHTS; i++)
        {
            Dlight d = engine.cl_dlights[i];
            h.Vec(d.origin);
            h.Float(d.radius);
            h.Float(d.die);
            h.Float(d.decay);
            h.Float(d.minlight);
            h.Int(d.key);
        }

        for (i = 0; i < QuakeDef.MAX_LIGHTSTYLES; i++)
        {
            h.Int(engine.cl_lightstyle[i].length);
            h.String(engine.cl_lightstyle[i].map);
        }

        for (i = 0; i < 256; i++)
        {
            h.Int(engine.d_lightstylevalue[i]);
        }

        particles = 0;
        for (Particle? p = engine.active_particles; p != null; p = p.next)
        {
            h.Vec(p.org);
            h.Float(p.color);
            h.Vec(p.vel);
            h.Float(p.ramp);
            h.Float(p.die);
            h.Int((int)p.type);
            particles++;
        }

        return h.h;
    }

    // S: the sound channels (snd_dma.c), which decide S_StartSound's rand() draws, and the mixer's times
    private ulong Sound()
    {
        var h = new Hash();

        h.Int(engine.paintedtime);
        h.Int(engine.soundtime);
        h.Int(engine.total_channels);
        for (int i = 0; i < engine.total_channels; i++)
        {
            Channel ch = engine.channels[i];
            h.String(ch.sfx?.name ?? "");
            h.Int(ch.leftvol);
            h.Int(ch.rightvol);
            h.Int(ch.end);
            h.Int(ch.pos);
            h.Int(ch.looping);
            h.Int(ch.entnum);
            h.Int(ch.entchannel);
            h.Vec(ch.origin);
            h.Float(ch.dist_mult);
            h.Int(ch.master_vol);
        }

        return h.h;
    }

    // T: the console's text (console.c)
    private ulong Text()
    {
        var h = new Hash();

        h.Int(engine.con_linewidth);
        h.Int(engine.con_totallines);
        h.Int(engine.con_current);
        h.Int(engine.con_x);
        for (int i = 0; i < engine.con_times.Length; i++)
        {
            h.Float(engine.con_times[i]);
        }

        if (engine.con_text.Length != 0)
        {
            h.Bytes(engine.con_text);
        }

        return h.h;
    }

    // R: the view (r_refdef)
    private ulong View()
    {
        var h = new Hash();
        RefDef r = engine.r_refdef;

        h.Int(r.vrect.x);
        h.Int(r.vrect.y);
        h.Int(r.vrect.width);
        h.Int(r.vrect.height);
        h.Vec(r.vieworg);
        h.Vec(r.viewangles);
        h.Float(r.fov_x);
        h.Float(r.fov_y);
        return h.h;
    }

    // V: the frame (vid.buffer, row by row)
    private ulong Picture()
    {
        var h = new Hash();
        Video.VidDef vid = engine.vid;

        for (int y = 0; y < (int)vid.height; y++)
        {
            h.Bytes(vid.buffer.AsSpan(vid.bufferoffset + (y * (int)vid.rowbytes), (int)vid.width));
        }

        return h.h;
    }

    // V: the palette last passed to VID_SetPalette
    private ulong Palette()
    {
        var h = new Hash();
        h.Bytes(engine.vid_curpal);
        return h.h;
    }
}
