using QuakeSharp.Core.Common;

namespace QuakeSharp.Core.Client;

/// <summary>client.h: cactive_t</summary>
public enum cactive_t
{
    /// <summary>a dedicated server with no ability to start a client</summary>
    ca_dedicated,

    /// <summary>full screen console with no connection</summary>
    ca_disconnected,

    /// <summary>valid netcon, talking to a server</summary>
    ca_connected,
}

/// <summary>
/// client.h: client_static_t, the client state that persists through an arbitrary number of server connections (the
/// fields the ported code uses; the rest come with cl_*.c).
/// </summary>
public sealed class ClientStatic
{
    // client.h: MAX_DEMOS, MAX_DEMONAME
    public const int MAX_DEMOS = 8;
    public const int MAX_DEMONAME = 16;

    public cactive_t state;

    // personalization data sent to server
    public string mapstring = "";

    /// <summary>to restart a level</summary>
    public string spawnparms = "";

    // demo loop control

    /// <summary>-1 = don't play demos</summary>
    public int demonum;

    /// <summary>when not playing</summary>
    public string[] demos = [.. Enumerable.Repeat("", MAX_DEMOS)];

    // demo recording info must be here, because record is started before
    // entering a map (and clearing client_state_t)
    public bool demorecording;

    public bool demoplayback;

    public bool timedemo;

    /// <summary>-1 = use normal cd track</summary>
    public int forcetrack;

    /// <summary>the demo being played or recorded (a demo in a pack is read through a stream on the pack)</summary>
    public Stream? demofile;

    /// <summary>to meter out one message a frame</summary>
    public int td_lastframe;

    /// <summary>host_framecount at start</summary>
    public int td_startframe;

    /// <summary>realtime at second frame of timedemo</summary>
    public float td_starttime;

    // connection information

    /// <summary>0 to SIGNONS</summary>
    public int signon;

    /// <summary>net.h's qsocket_t (milestone 7, with net_main.c)</summary>
    public Net.QSocket? netcon;

    /// <summary>writing buffer to send to server (cl_main.c: CL_Init allocates 1024 bytes)</summary>
    public SizeBuf message = new(1024);
}
