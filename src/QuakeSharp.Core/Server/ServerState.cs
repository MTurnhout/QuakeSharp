namespace QuakeSharp.Core.Server;

/// <summary>server.h: server_state_t</summary>
public enum server_state_t
{
    ss_loading,
    ss_active,
}

/// <summary>server.h: server_t, the server state (the fields the ported code uses; the rest come with sv_*.c).</summary>
public sealed class ServerState
{
    /// <summary>false if only a net client</summary>
    public bool active;

    public bool paused;

    /// <summary>handle connections specially</summary>
    public bool loadgame;

    public double time;

    /// <summary>map name</summary>
    public string name = "";

    /// <summary>some actions are only valid during load</summary>
    public server_state_t state;
}
