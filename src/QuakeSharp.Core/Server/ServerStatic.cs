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
    public SizeBuf message = new(QuakeDef.MAX_MSGLEN) { maxsize = 0 };

    /// <summary>EDICT_NUM(clientnum+1)</summary>
    public Progs.Edict? edict;

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

    /// <summary>memset (client, 0, sizeof(*client)): every field 0 (the message has no buffer until it is given one)</summary>
    public void Clear()
    {
        active = spawned = dropasap = privileged = sendsignon = false;
        last_message = 0;
        netconnection = null;
        cmd = default;
        wishdir = default;
        message = new SizeBuf(QuakeDef.MAX_MSGLEN) { maxsize = 0 };
        edict = null;
        name = "";
        colors = 0;
        Array.Clear(ping_times);
        num_pings = 0;
        Array.Clear(spawn_parms);
        old_frags = 0;
    }
}

/// <summary>server.h: server_static_t</summary>
public sealed class ServerStatic
{
    public int maxclients;

    public int maxclientslimit;

    /// <summary>[maxclients]</summary>
    public ServerClient[] clients = [];

    // what the C's pointer past the last slot points at (never active)
    private readonly ServerClient pastClients = new();

    /// <summary>&amp;svs.clients[i]: the C's loops over the clients (for (i=0, host_client = svs.clients ;
    /// i&lt;svs.maxclients ; i++, host_client++)) leave host_client at the slot after the last, which a console command
    /// may compare with (Host_Kick_f); past every slot, a slot that is never active.</summary>
    public ServerClient Client(int i) => i < clients.Length ? clients[i] : pastClients;

    /// <summary>episode completion information</summary>
    public int serverflags;

    /// <summary>cleared when at SV_SpawnServer</summary>
    public bool changelevel_issued;
}
