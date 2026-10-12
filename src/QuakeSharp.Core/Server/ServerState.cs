using QuakeSharp.Core.Common;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core.Server;

/// <summary>server.h: server_state_t</summary>
public enum server_state_t
{
    ss_loading,
    ss_active,
}

/// <summary>server.h: server_t, the server state (its edicts come with the VM).</summary>
public sealed class ServerState
{
    /// <summary>false if only a net client</summary>
    public bool active;

    public bool paused;

    /// <summary>handle connections specially</summary>
    public bool loadgame;

    public double time;

    /// <summary>used by PF_checkclient</summary>
    public int lastcheck;

    public double lastchecktime;

    /// <summary>map name (char[64])</summary>
    public string name = "";

    /// <summary>maps/&lt;name&gt;.bsp, for model_precache[0] (char[64])</summary>
    public string modelname = "";

    public Model.Model? worldmodel;

    /// <summary>NULL terminated</summary>
    public string?[] model_precache = new string?[MAX_MODELS];

    public Model.Model?[] models = new Model.Model?[MAX_MODELS];

    /// <summary>NULL terminated</summary>
    public string?[] sound_precache = new string?[MAX_SOUNDS];

    public string?[] lightstyles = new string?[MAX_LIGHTSTYLES];

    public int num_edicts;

    public int max_edicts;

    /// <summary>some actions are only valid during load</summary>
    public server_state_t state;

    public SizeBuf datagram = new(MAX_DATAGRAM);

    /// <summary>copied to all clients at end of frame</summary>
    public SizeBuf reliable_datagram = new(MAX_DATAGRAM);

    public SizeBuf signon = new(8192);

    /// <summary>can NOT be array indexed in C, because edict_t is variable sized, but can be used to reference the world ent</summary>
    public Progs.Edict[] edicts = [];

    /// <summary>The edicts' memory: max_edicts * pr_edict_size bytes as 4-byte slots (design.md §11.1).</summary>
    public int[] edictmem = [];
}
