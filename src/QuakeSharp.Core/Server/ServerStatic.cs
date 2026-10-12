using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Server;

/// <summary>
/// server.h: client_t, a client slot of the server (the fields the ported code uses; the rest come with sv_*.c).
/// </summary>
public sealed class ServerClient
{
    /// <summary>false = client is free</summary>
    public bool active;

    /// <summary>false = don't send datagrams</summary>
    public bool spawned;

    /// <summary>can be added to at any time, copied and clear once per frame</summary>
    public SizeBuf message = new(QuakeDef.MAX_MSGLEN);

    public string name = "";
}

/// <summary>server.h: server_static_t (the fields the ported code uses; the rest come with sv_*.c).</summary>
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
