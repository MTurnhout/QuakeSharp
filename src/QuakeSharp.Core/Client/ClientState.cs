using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Sound;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core.Client;

/// <summary>client.h: usercmd_t</summary>
public struct UserCmd
{
    public Vec3 viewangles;

    // intended velocities
    public float forwardmove;
    public float sidemove;
    public float upmove;
}

/// <summary>client.h: lightstyle_t (map is char[MAX_STYLESTRING])</summary>
public sealed class LightStyle
{
    public int length;
    public string map = "";
}

/// <summary>client.h: scoreboard_t</summary>
public sealed class Scoreboard
{
    public string name = "";
    public float entertime;
    public int frags;

    /// <summary>two 4 bit fields</summary>
    public int colors;

    public readonly byte[] translations = new byte[QuakeEngine.VID_GRADES * 256];
}

/// <summary>client.h: cshift_t (a value, as C copies it)</summary>
public struct Cshift
{
    public Cshift(int r, int g, int b, int percent)
    {
        destcolor[0] = r;
        destcolor[1] = g;
        destcolor[2] = b;
        this.percent = percent;
    }

    public Int3 destcolor;

    /// <summary>0-256</summary>
    public int percent;
}

/// <summary>client.h: dlight_t. <see cref="Clear"/> is C's memset to 0.</summary>
public sealed class Dlight
{
    public Vec3 origin;
    public float radius;

    /// <summary>stop lighting after this time</summary>
    public float die;

    /// <summary>drop this each second</summary>
    public float decay;

    /// <summary>don't add when contributing less</summary>
    public float minlight;

    public int key;

    public void Clear()
    {
        origin = default;
        radius = 0;
        die = 0;
        decay = 0;
        minlight = 0;
        key = 0;
    }
}

/// <summary>client.h: beam_t</summary>
public sealed class Beam
{
    public int entity;
    public Model.Model? model;
    public float endtime;
    public Vec3 start, end;

    public void Clear()
    {
        entity = 0;
        model = null;
        endtime = 0;
        start = default;
        end = default;
    }
}

/// <summary>
/// client.h: client_state_t, which should hold all pieces of the client state. It is wiped completely at every server
/// signon (a new instance).
/// </summary>
public sealed class ClientState
{
    // client.h: CSHIFT_CONTENTS...
    public const int CSHIFT_CONTENTS = 0;
    public const int CSHIFT_DAMAGE = 1;
    public const int CSHIFT_BONUS = 2;
    public const int CSHIFT_POWERUP = 3;
    public const int NUM_CSHIFTS = 4;

    /// <summary>since connecting to this server; throw out the first couple, so the player doesn't accidentally do
    /// something the first frame</summary>
    public int movemessages;

    /// <summary>last command sent to the server</summary>
    public UserCmd cmd;

    // information for local display

    /// <summary>health, etc</summary>
    public readonly int[] stats = new int[MAX_CL_STATS];

    /// <summary>inventory bit flags</summary>
    public int items;

    /// <summary>cl.time of aquiring item, for blinking</summary>
    public readonly float[] item_gettime = new float[32];

    /// <summary>use anim frame if cl.time &lt; this</summary>
    public float faceanimtime;

    /// <summary>color shifts for damage, powerups</summary>
    public readonly Cshift[] cshifts = new Cshift[NUM_CSHIFTS];

    /// <summary>and content types</summary>
    public readonly Cshift[] prev_cshifts = new Cshift[NUM_CSHIFTS];

    // the client maintains its own idea of view angles, which are sent to the server each frame. The server sets
    // punchangle when the view is temporarliy offset, and an angle reset commands at the start of each level and
    // after teleporting.

    /// <summary>during demo playback viewangles is lerped between these</summary>
    public Vec3x2 mviewangles;

    public Vec3 viewangles;

    /// <summary>update by server, used for lean+bob (0 is newest)</summary>
    public Vec3x2 mvelocity;

    /// <summary>lerped between mvelocity[0] and [1]</summary>
    public Vec3 velocity;

    /// <summary>temporary offset</summary>
    public Vec3 punchangle;

    // pitch drifting vars
    public float idealpitch;
    public float pitchvel;
    public bool nodrift;
    public float driftmove;
    public double laststop;

    public float viewheight;

    /// <summary>local amount for smoothing stepups</summary>
    public float crouch;

    /// <summary>send over by server (the byte it sent)</summary>
    public int paused;

    public bool onground;
    public bool inwater;

    /// <summary>don't change view angle, full screen, etc</summary>
    public int intermission;

    /// <summary>latched at intermission start</summary>
    public int completed_time;

    /// <summary>the timestamp of last two messages</summary>
    public readonly double[] mtime = new double[2];

    /// <summary>clients view of time, should be between servertime and oldservertime to generate a lerp point for
    /// other data</summary>
    public double time;

    /// <summary>previous cl.time, time-oldtime is used to decay light values and smooth step ups</summary>
    public double oldtime;

    /// <summary>(realtime) for net trouble icon</summary>
    public float last_received_message;

    // information that is static for the entire time connected to a server
    public readonly Model.Model?[] model_precache = new Model.Model?[MAX_MODELS];
    public readonly Sfx?[] sound_precache = new Sfx?[MAX_SOUNDS];

    /// <summary>for display on solo scoreboard (char[40])</summary>
    public string levelname = "";

    /// <summary>cl_entitites[cl.viewentity] = player</summary>
    public int viewentity;

    public int maxclients;
    public int gametype;

    // refresh related state

    /// <summary>cl_entitites[0].model</summary>
    public Model.Model? worldmodel;

    public Efrag? free_efrags;

    /// <summary>held in cl_entities array</summary>
    public int num_entities;

    /// <summary>held in cl_staticentities array</summary>
    public int num_statics;

    /// <summary>the gun model</summary>
    public readonly Entity viewent = new();

    /// <summary>cd audio</summary>
    public int cdtrack, looptrack;

    /// <summary>frag scoreboard [cl.maxclients]</summary>
    public Scoreboard[]? scores;
}
