using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;

namespace QuakeSharp.Core.Client;

/// <summary>quakedef.h: entity_state_t</summary>
public struct EntityState
{
    public Vec3 origin;
    public Vec3 angles;
    public int modelindex;
    public int frame;
    public int colormap;
    public int skin;
    public int effects;
}

/// <summary>render.h: entity_t. <see cref="Clear"/> is C's memset to 0.</summary>
public sealed class Entity
{
    /// <summary>model changed</summary>
    public bool forcelink;

    public int update_type;

    /// <summary>to fill in defaults in updates</summary>
    public EntityState baseline;

    /// <summary>time of last update</summary>
    public double msgtime;

    /// <summary>last two updates (0 is newest)</summary>
    public Vec3x2 msg_origins;

    public Vec3 origin;

    /// <summary>last two updates (0 is newest)</summary>
    public Vec3x2 msg_angles;

    public Vec3 angles;

    /// <summary>NULL = no model</summary>
    public Model.Model? model;

    /// <summary>linked list of efrags</summary>
    public Efrag? efrag;

    public int frame;

    /// <summary>for client-side animations</summary>
    public float syncbase;

    /// <summary>vid.colormap or a scoreboard's translations (the reference is what C's pointer is)</summary>
    public byte[]? colormap;

    /// <summary>light, particals, etc</summary>
    public int effects;

    /// <summary>for Alias models</summary>
    public int skinnum;

    /// <summary>last frame this entity was found in an active leaf</summary>
    public int visframe;

    /// <summary>dynamic lighting</summary>
    public int dlightframe;

    public int dlightbits;

    // FIXME: could turn these into a union
    public int trivial_accept;

    /// <summary>for bmodels, first world node that splits bmodel, or NULL if not split</summary>
    public MNodeBase? topnode;

    // memset (ent, 0, sizeof(*ent))
    public void Clear()
    {
        forcelink = false;
        update_type = 0;
        baseline = default;
        msgtime = 0;
        msg_origins = default;
        origin = default;
        msg_angles = default;
        angles = default;
        model = null;
        efrag = null;
        frame = 0;
        syncbase = 0;
        colormap = null;
        effects = 0;
        skinnum = 0;
        visframe = 0;
        dlightframe = 0;
        dlightbits = 0;
        trivial_accept = 0;
        topnode = null;
    }
}
