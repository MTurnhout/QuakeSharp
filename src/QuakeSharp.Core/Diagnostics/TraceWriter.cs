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
    public const int Version = 8;

    private const ulong FNV_OFFSET = 0xcbf29ce484222325UL;
    private const ulong FNV_PRIME = 0x100000001b3UL;

    private readonly QuakeEngine engine;
    private readonly int framesPerSecond;
    private int frame;

    // the M line: the messages sent during the frame running, and those of the frame just traced (for Dump)
    private Hash messageHash = new();
    private List<(int Sender, bool Reliable, int Result, byte[] Data)> messages = [];
    private List<(int Sender, bool Reliable, int Result, byte[] Data)> lastMessages = [];

    // the O line: what a dedicated server's console was given during the frame running, and during the frame just
    // traced (for Dump)
    private Hash consoleHash = new();
    private List<byte> console = [];
    private List<byte> lastConsole = [];

    // paintedtime when the frame running began, and when the frame just traced began (Dump writes what it painted)
    private int framePaintedtime;
    private int lastFramePaintedtime;

    public TraceWriter(QuakeEngine engine, int framesPerSecond)
    {
        this.engine = engine;
        this.framesPerSecond = framesPerSecond;
        engine.MessageSent = OnMessageSent;
        engine.ConsoleWritten = OnConsoleWritten;
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
        lines.Add(Invariant($"G {Server():x16}"));
        lines.Add(Invariant($"Q {Globals():x16}"));
        ulong n = Edicts(out int edicts);
        lines.Add(Invariant($"N {n:x16} {edicts}"));
        lines.Add(Invariant($"W {World():x16}"));
        lines.Add(Invariant($"M {messageHash.h:x16} {messages.Count}"));
        messageHash = new Hash();
        (lastMessages, messages) = (messages, lastMessages);
        messages.Clear();
        lines.Add(Invariant($"P {Paint():x16}"));
        lines.Add(Invariant($"A {CD():x16}"));
        lines.Add(Invariant($"I {Input():x16}"));
        ulong u = Datagrams(out int datagrams);
        lines.Add(Invariant($"U {u:x16} {datagrams}"));
        lines.Add(Invariant($"O {consoleHash.h:x16} {console.Count}"));
        consoleHash = new Hash();
        (lastConsole, console) = (console, lastConsole);
        console.Clear();
        lastFramePaintedtime = framePaintedtime;
        framePaintedtime = e.paintedtime;
        return lines;
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    // U: the datagrams the virtual machine's Winsock sent and received during the frame (winsock_oracle.c's
    // Oracle_Datagrams)
    private ulong Datagrams(out int count)
    {
        if (engine.WinsockLibrary is Net.VirtualWinsock winsock)
        {
            return winsock.Datagrams(out count);
        }

        count = 0;
        return FNV_OFFSET;
    }

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

        DumpServer(sb);
        DumpSound(sb);
        DumpInput(sb);
        if (engine.WinsockLibrary is Net.VirtualWinsock winsock)
        {
            sb.Append(winsock.LastDatagramLog);
        }

        sb.Append(Invariant($"console {lastConsole.Count} {Convert.ToHexStringLower([.. lastConsole])}\n"));
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
        (byte[] frame, int rowbytes) = engine.VID_Frame();
        for (int y = 0; y < height; y++)
        {
            frame.AsSpan(y * rowbytes, width).CopyTo(shot.AsSpan(8 + 768 + (y * width)));
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
        int colormap = IsVidColormap(e.colormap) ? 0
            : engine.cl.scores is { } scores && Array.FindIndex(scores, s => s.translations == e.colormap) is int n && n >= 0 && n < engine.cl.maxclients ? 1 + n
            : -1;
        sb.Append(Invariant($" model {e.model?.name ?? ""} frame {e.frame} syncbase {BitConverter.SingleToInt32Bits(e.syncbase):x8} colormap {colormap} effects {e.effects} skinnum {e.skinnum}\n"));
    }

    // the sound channels, the samples painted during the frame (in the device's buffer, from paintedtime at the frame's
    // start to its end) and the CD's state
    private void DumpSound(StringBuilder sb)
    {
        string[] cd_names = ["initialized", "enabled", "cdValid", "playing", "wasPlaying", "playLooping", "cdvolume", "playTrack", "maxTrack"];
        QuakeEngine e = engine;

        sb.Append(Invariant($"sound paintedtime {e.paintedtime} soundtime {e.soundtime} total_channels {e.total_channels}\n"));
        for (int i = 0; i < e.total_channels; i++)
        {
            Channel ch = e.channels[i];
            sb.Append(Invariant($"channel {i} sfx "));
            DumpString(sb, ch.sfx?.name ?? "");
            sb.Append(Invariant($" leftvol {ch.leftvol} rightvol {ch.rightvol} end {ch.end} pos {ch.pos} looping {ch.looping} entnum {ch.entnum} entchannel {ch.entchannel}"));
            DumpVec(sb, "origin", ch.origin);
            sb.Append(Invariant($" dist_mult {BitConverter.SingleToInt32Bits(ch.dist_mult):x8} master_vol {ch.master_vol}\n"));
        }

        if (e.sound_started != 0 && e.shm != null)
        {
            Core.Sound.Dma shm = e.shm;
            int frames = shm.samples / shm.channels;
            int size = shm.channels * (shm.samplebits / 8);
            sb.Append(Invariant($"painted {lastFramePaintedtime} {e.paintedtime} "));
            for (int t = lastFramePaintedtime; t < e.paintedtime && t - lastFramePaintedtime < frames; t++)
            {
                sb.Append(Convert.ToHexStringLower(shm.buffer, (t & (frames - 1)) * size, size));
            }

            sb.Append('\n');
        }

        int[] state = CDState();
        sb.Append("cd");
        for (int i = 0; i < 9; i++)
        {
            sb.Append(Invariant($" {cd_names[i]} {(i == 6 ? state[i].ToString("x8", CultureInfo.InvariantCulture) : state[i].ToString(CultureInfo.InvariantCulture))}"));
        }

        sb.Append(" remap");
        for (int i = 9; i < 109; i++)
        {
            sb.Append(Invariant($" {state[i]}"));
        }

        sb.Append(Invariant($" drive open {state[109]} door {state[110]} mode {state[111]} track {state[112]}\n"));
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

    // colormap == vid.colormap: a dedicated server's vid.colormap is NULL, as an entity's that was never set (the
    // empty array here)
    private bool IsVidColormap(byte[]? colormap) =>
        colormap == engine.vid.colormap || (colormap == null && engine.vid.colormap.Length == 0);

    // a colormap: 0 the normal one, 1 + n player n's translation, -1 anything else
    private void H_Colormap(ref Hash h, byte[]? colormap)
    {
        ClientState cl = engine.cl;

        if (IsVidColormap(colormap))
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

    // P: the sound device's buffer, all of it: what the device plays (the samples painted, S_ClearBuffer's clears, what
    // stays from the laps before). Without a started sound system, nothing
    private ulong Paint()
    {
        var h = new Hash();
        Core.Sound.Dma? shm = engine.shm;

        if (engine.sound_started != 0 && shm != null)
        {
            h.Bytes(shm.buffer.AsSpan(0, shm.samples * (shm.samplebits / 8)));
        }

        return h.h;
    }

    // cd_win.c's state (trace.c: Oracle_CDState: initialized, enabled, cdValid, playing, wasPlaying, playLooping,
    // cdvolume's bits, playTrack, maxTrack, remap), then the virtual drive's (cd_oracle.c: Oracle_CDDrive)
    private int[] CDState()
    {
        QuakeEngine e = engine;
        var state = new List<int>(113)
        {
            e.cd_initialized ? 1 : 0,
            e.cd_enabled ? 1 : 0,
            e.cdValid ? 1 : 0,
            e.cd_playing ? 1 : 0,
            e.wasPlaying ? 1 : 0,
            e.playLooping ? 1 : 0,
            BitConverter.SingleToInt32Bits(e.cdvolume),
            e.playTrack,
            e.maxTrack,
        };
        foreach (byte r in e.cd_remap)
        {
            state.Add(r);
        }

        // a drive other than the virtual one (the executable's) has no state the oracle could match: as if none
        state.AddRange((e.cddrive as Core.Sound.VirtualCdDrive)?.State() ?? [0, 0, 0, 0]);
        return [.. state];
    }

    // A: the CD
    private ulong CD()
    {
        var h = new Hash();

        foreach (int v in CDState())
        {
            h.Int(v);
        }

        return h.h;
    }

    // I: the mouse and the joystick: in_win.c's state, vid_win.c's windowed_mouse, the virtual Windows' (VirtualInput)
    private int[] InputState() =>
        [.. engine.InState(), engine.windowed_mouse, .. engine.input.State()];

    private ulong Input()
    {
        var h = new Hash();

        foreach (int v in InputState())
        {
            h.Int(v);
        }

        return h.h;
    }

    // the I line's fields by name
    private void DumpInput(StringBuilder sb)
    {
        string[] names =
        [
            "mouseactive", "mouseinitialized", "mouseactivatetoggle", "mouseshowtoggle", "mouseparmsvalid", "restore_spi",
            "dinput", "dinput_acquired", "mstate_di", "mouse_buttons", "mouse_oldbuttonstate", "current_pos.x",
            "current_pos.y", "mx_accum", "my_accum", "mouse_x", "mouse_y", "old_mouse_x", "old_mouse_y", "joy_avail",
            "joy_advancedinit", "joy_haspov", "joy_id", "joy_flags", "joy_numbuttons", "joy_oldbuttonstate",
            "joy_oldpovstate", "dwAxisMap0", "dwAxisMap1", "dwAxisMap2", "dwAxisMap3", "dwAxisMap4", "dwAxisMap5",
            "dwControlMap0", "dwControlMap1", "dwControlMap2", "dwControlMap3", "dwControlMap4", "dwControlMap5",
            "windowed_mouse", "cursor.x", "cursor.y", "clipping", "clip.left", "clip.top", "clip.right", "clip.bottom",
            "buttons", "cursor_count", "captured", "mouse_parms0", "mouse_parms1", "mouse_parms2",
        ];
        int[] state = InputState();

        sb.Append("input");
        for (int i = 0; i < state.Length && i < names.Length; i++)
        {
            sb.Append(Invariant($" {names[i]} {state[i]}"));
        }

        sb.Append('\n');
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

        h.Int(engine.vid_modenum);
        h.Int((int)engine.modestate);
        h.Int((int)vid.width);
        h.Int((int)vid.height);
        h.Int((int)vid.rowbytes);

        // the DIB or the full-screen memory DC (vid.buffer and vid.rowbytes wait for a lock in a full-screen mode)
        (byte[] frame, int rowbytes) = engine.VID_Frame();
        for (int y = 0; y < (int)vid.height; y++)
        {
            h.Bytes(frame.AsSpan(y * rowbytes, (int)vid.width));
        }

        return h.h;
    }

    // V: the palette last passed to VID_SetPalette; 0 for a dedicated server, which never sets one (no VID_Init)
    private ulong Palette()
    {
        if (engine.isDedicated)
        {
            return 0;
        }

        var h = new Hash();
        h.Bytes(engine.vid_curpal);
        return h.h;
    }

    // ===============================================================================
    // THE SERVER'S LINES
    //
    // Without an active server, G hashes the flags and svs, and Q, N and W nothing: what sv points into belongs to a
    // game that is over.
    // ===============================================================================

    private static void H_StringPtr(ref Hash h, string? s)
    {
        h.Bool(s != null);
        if (s != null)
        {
            h.String(s);
        }
    }

    private static void H_Sizebuf(ref Hash h, SizeBuf b)
    {
        h.Bool(b.overflowed);
        h.Int(b.cursize);
        h.Bytes(b.data.AsSpan(0, b.cursize));
    }

    // an edict's number (from its place, so a stale reference gives the C's number too), -1 without a server
    private int EdictNumber(Progs.Edict? e) => e == null || !engine.sv.active ? -1 : e.num;

    // G: the server (sv, svs and the client slots up to svs.maxclients)
    private ulong Server()
    {
        var h = new Hash();
        Server.ServerState sv = engine.sv;
        Server.ServerStatic svs = engine.svs;
        int i, j;

        h.Bool(sv.active);
        h.Bool(sv.paused);
        h.Bool(sv.loadgame);
        if (sv.active)
        {
            h.Double(sv.time);
            h.Int(sv.lastcheck);
            h.Double(sv.lastchecktime);
            h.String(sv.name);
            h.String(sv.modelname);
            h.Model(sv.worldmodel);
            for (i = 0; i < QuakeDef.MAX_MODELS; i++)
            {
                H_StringPtr(ref h, sv.model_precache[i]);
                h.Model(sv.models[i]);
            }

            for (i = 0; i < QuakeDef.MAX_SOUNDS; i++)
            {
                H_StringPtr(ref h, sv.sound_precache[i]);
            }

            for (i = 0; i < QuakeDef.MAX_LIGHTSTYLES; i++)
            {
                H_StringPtr(ref h, sv.lightstyles[i]);
            }

            h.Int(sv.num_edicts);
            h.Int(sv.max_edicts);
            h.Int((int)sv.state);
            H_Sizebuf(ref h, sv.datagram);
            H_Sizebuf(ref h, sv.reliable_datagram);
            H_Sizebuf(ref h, sv.signon);
        }

        h.Int(svs.maxclients);
        h.Int(svs.maxclientslimit);
        h.Int(svs.serverflags);
        h.Bool(svs.changelevel_issued);
        for (i = 0; i < svs.maxclients; i++)
        {
            Server.ServerClient c = svs.clients[i];
            h.Bool(c.active);
            h.Bool(c.spawned);
            h.Bool(c.dropasap);
            h.Bool(c.privileged);
            h.Bool(c.sendsignon);
            h.Double(c.last_message);
            h.Bool(c.netconnection != null);
            h.Vec(c.cmd.viewangles);
            h.Float(c.cmd.forwardmove);
            h.Float(c.cmd.sidemove);
            h.Float(c.cmd.upmove);
            h.Vec(c.wishdir);
            H_Sizebuf(ref h, c.message);
            h.Int(EdictNumber(c.edict));
            h.String(c.name);
            h.Int(c.colors);
            for (j = 0; j < Core.Server.ServerClient.NUM_PING_TIMES; j++)
            {
                h.Float(c.ping_times[j]);
            }

            h.Int(c.num_pings);
            for (j = 0; j < Core.Server.ServerClient.NUM_SPAWN_PARMS; j++)
            {
                h.Float(c.spawn_parms[j]);
            }

            h.Int(c.old_frags);
        }

        return h.h;
    }

    // what each slot of the globals or of an edict's fields holds, from the progs' definitions: 0 no definition
    // covers it (the compiler's temporaries, the return value, the parameters), 1 a number, an entity, a field or a
    // function, 2 a string (hashed by content: its offset depends on the oracle's addresses)
    private const byte SLOT_NONE = 0;
    private const byte SLOT_VALUE = 1;
    private const byte SLOT_STRING = 2;

    private static byte[] SlotKinds(Progs.ddef_t[] defs, int numdefs, int numslots)
    {
        byte[] kinds = new byte[numslots];
        for (int i = 0; i < numdefs; i++)
        {
            var type = (Progs.etype_t)(defs[i].type & ~Progs.PrComp.DEF_SAVEGLOBAL);
            int size = type == Progs.etype_t.ev_vector ? 3 : 1;
            for (int j = 0; j < size; j++)
            {
                if (defs[i].ofs + j >= numslots)
                {
                    break;
                }

                if (type == Progs.etype_t.ev_string)
                {
                    kinds[defs[i].ofs + j] = SLOT_STRING;
                }
                else if (kinds[defs[i].ofs + j] == SLOT_NONE)
                {
                    kinds[defs[i].ofs + j] = SLOT_VALUE;
                }
            }
        }

        return kinds;
    }

    private void H_Slots(ref Hash h, byte[] kinds, int[] mem, int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (kinds[i] == SLOT_STRING)
            {
                h.String(engine.PR_GetString(mem[start + i]));
            }
            else if (kinds[i] == SLOT_VALUE)
            {
                h.Int(mem[start + i]);
            }
        }
    }

    // Q: the QuakeC globals
    private ulong Globals()
    {
        var h = new Hash();
        Progs.dprograms_t? progs = engine.progs;

        if (!engine.sv.active || progs == null)
        {
            return h.h;
        }

        byte[] kinds = SlotKinds(engine.pr_globaldefs, progs.numglobaldefs, progs.numglobals);
        h.Int(progs.numglobals);
        H_Slots(ref h, kinds, engine.pr_globals, 0, progs.numglobals);
        return h.h;
    }

    // N: the edicts up to sv.num_edicts, their header and their fields
    private ulong Edicts(out int count)
    {
        var h = new Hash();
        Progs.dprograms_t? progs = engine.progs;

        count = 0;
        if (!engine.sv.active || progs == null)
        {
            return h.h;
        }

        byte[] kinds = SlotKinds(engine.pr_fielddefs, progs.numfielddefs, progs.entityfields);
        for (int i = 0; i < engine.sv.num_edicts; i++)
        {
            Progs.Edict e = engine.EDICT_NUM(i);
            h.Bool(e.free);
            h.Int(e.num_leafs);
            for (int j = 0; j < e.num_leafs; j++)
            {
                h.Int(e.leafnums[j]);
            }

            H_EntityState(ref h, e.baseline);
            h.Float(e.freetime);
            H_Slots(ref h, kinds, engine.sv.edictmem, e.fields, progs.entityfields);
        }

        count = engine.sv.num_edicts;
        return h.h;
    }

    // W: the area nodes' trigger and solid lists, as edict numbers in their order (-1 after each list)
    private ulong World()
    {
        var h = new Hash();

        if (!engine.sv.active)
        {
            return h.h;
        }

        var links = new List<int>();
        for (int i = 0; i < engine.sv_numareanodes; i++)
        {
            Server.AreaNode node = engine.sv_areanodes[i];
            foreach (Progs.Link list in new[] { node.trigger_edicts, node.solid_edicts })
            {
                for (Progs.Link? l = list.next; l != list && l != null; l = l.next)
                {
                    links.Add(engine.NUM_FOR_EDICT(l.owner!));
                }

                links.Add(-1);
            }
        }

        h.Int(links.Count);
        foreach (int n in links)
        {
            h.Int(n);
        }

        return h.h;
    }

    // who sent a message: -1 the client, n the server to client n, -2 another socket
    private int MessageSender(Net.QSocket sock)
    {
        if (sock == engine.cls.netcon)
        {
            return -1;
        }

        for (int i = 0; i < engine.svs.maxclients; i++)
        {
            if (engine.svs.clients[i].netconnection == sock)
            {
                return i;
            }
        }

        return -2;
    }

    // net_main.c (the oracle's Trace_Message): NET_SendMessage and NET_SendUnreliableMessage, after the driver took
    // the message
    private void OnMessageSent(Net.QSocket sock, SizeBuf data, bool reliable, int result)
    {
        int sender = MessageSender(sock);
        messageHash.Int(sender);
        messageHash.Bool(reliable);
        messageHash.Int(result);
        messageHash.Int(data.cursize);
        messageHash.Bytes(data.data.AsSpan(0, data.cursize));
        messages.Add((sender, reliable, result, data.data.AsSpan(0, data.cursize).ToArray()));
    }

    // the oracle's Trace_Console: what WriteFile gives a dedicated server's console
    private void OnConsoleWritten(byte[] text)
    {
        consoleHash.Bytes(text);
        console.AddRange(text);
    }

    // a string in a dump: quoted, with \\, \" and the characters outside 32-126 as \xHH
    private static void DumpString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '\0')
            {
                break;
            }

            if (c is '\\' or '"')
            {
                sb.Append('\\').Append(c);
            }
            else if (c < 32 || c > 126)
            {
                sb.Append(Invariant($"\\x{(int)c:x2}"));
            }
            else
            {
                sb.Append(c);
            }
        }

        sb.Append('"');
    }

    // the slots of a definition: strings by content, the rest as their bits in hexadecimal
    private void DumpDef(StringBuilder sb, string what, in Progs.ddef_t def, int[] mem, int start, int numslots)
    {
        var type = (Progs.etype_t)(def.type & ~Progs.PrComp.DEF_SAVEGLOBAL);
        int size = type == Progs.etype_t.ev_vector ? 3 : 1;
        sb.Append(what).Append(' ').Append(engine.PR_GetString(def.s_name));
        for (int j = 0; j < size && def.ofs + j < numslots; j++)
        {
            sb.Append(' ');
            if (type == Progs.etype_t.ev_string)
            {
                DumpString(sb, engine.PR_GetString(mem[start + def.ofs + j]));
            }
            else
            {
                sb.Append(Invariant($"{mem[start + def.ofs + j]:x8}"));
            }
        }

        sb.Append('\n');
    }

    // -tracedump: the server's state, the globals and the edicts by name, the frame's messages
    private void DumpServer(StringBuilder sb)
    {
        Server.ServerState sv = engine.sv;

        sb.Append(Invariant($"server active {(sv.active ? 1 : 0)} paused {(sv.paused ? 1 : 0)}"));
        if (sv.active)
        {
            sb.Append(Invariant($" time {BitConverter.DoubleToInt64Bits(sv.time):x16} name {sv.name} num_edicts {sv.num_edicts}"));
        }

        sb.Append('\n');
        if (sv.active && engine.progs is { } progs)
        {
            for (int i = 0; i < progs.numglobaldefs; i++)
            {
                DumpDef(sb, "global", engine.pr_globaldefs[i], engine.pr_globals, 0, progs.numglobals);
            }

            for (int i = 0; i < sv.num_edicts; i++)
            {
                Progs.Edict e = engine.EDICT_NUM(i);
                sb.Append(Invariant($"edict {i} free {(e.free ? 1 : 0)} freetime {BitConverter.SingleToInt32Bits(e.freetime):x8} leafs"));
                for (int j = 0; j < e.num_leafs; j++)
                {
                    sb.Append(Invariant($" {e.leafnums[j]}"));
                }

                DumpVec(sb, "baseline.origin", e.baseline.origin);
                DumpVec(sb, "baseline.angles", e.baseline.angles);
                sb.Append(Invariant($" modelindex {e.baseline.modelindex} frame {e.baseline.frame} colormap {e.baseline.colormap} skin {e.baseline.skin} effects {e.baseline.effects}\n"));
                string what = Invariant($"edict {i}");
                for (int j = 0; j < progs.numfielddefs; j++)
                {
                    DumpDef(sb, what, engine.pr_fielddefs[j], engine.sv.edictmem, e.fields, progs.entityfields);
                }
            }
        }

        foreach ((int sender, bool reliable, int result, byte[] data) in lastMessages)
        {
            sb.Append(Invariant($"message {sender} {(reliable ? 1 : 0)} {result} {data.Length} "));
            sb.Append(Convert.ToHexStringLower(data));
            sb.Append('\n');
        }
    }
}
