using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Server;

/// <summary>server.h: client_t, a client slot of the server (its edict comes with the VM).</summary>
public sealed class ServerClient
{
    // server.h: NUM_PING_TIMES, NUM_SPAWN_PARMS
    public const int NUM_PING_TIMES = 16;
    public const int NUM_SPAWN_PARMS = 16;

    /// <summary>false = client is free</summary>
    public bool active;

    /// <summary>false = don't send datagrams</summary>
    public bool spawned;

    /// <summary>has been told to go to another level</summary>
    public bool dropasap;

    /// <summary>can execute any host command</summary>
    public bool privileged;

    /// <summary>only valid before spawned</summary>
    public bool sendsignon;

    /// <summary>reliable messages must be sent periodically</summary>
    public double last_message;

    /// <summary>communications handle</summary>
    public Net.QSocket? netconnection;

    /// <summary>movement</summary>
    public UserCmd cmd;

    /// <summary>intended motion calced from cmd</summary>
    public Vec3 wishdir;

    /// <summary>can be added to at any time, copied and clear once per frame</summary>
    public SizeBuf message = new(QuakeDef.MAX_MSGLEN);

    /// <summary>for printing to other people (char[32])</summary>
    public string name = "";

    public int colors;

    public float[] ping_times = new float[NUM_PING_TIMES];

    /// <summary>ping_times[num_pings%NUM_PING_TIMES]</summary>
    public int num_pings;

    /// <summary>spawn parms are carried from level to level</summary>
    public float[] spawn_parms = new float[NUM_SPAWN_PARMS];

    /// <summary>client known data for deltas</summary>
    public int old_frags;
}

/// <summary>server.h: server_static_t</summary>
public sealed class ServerStatic
{
    public int maxclients;

    public int maxclientslimit;

    /// <summary>[maxclients]</summary>
    public ServerClient[] clients = [];

    /// <summary>episode completion information</summary>
    public int serverflags;

    /// <summary>cleared when at SV_SpawnServer</summary>
    public bool changelevel_issued;
}
