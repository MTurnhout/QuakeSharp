namespace QuakeSharp.Core.Net;

/// <summary>
/// The virtual IPX driver, as the oracle's tools/oracle/src/net_oracle.c: net_wipx.c as it starts on today's Windows,
/// which has no IPX (design.md §3.4). Its control socket cannot be opened, so it prints why and stays down; the machine
/// has no name of its own (<see cref="VirtualLanDriver"/>), so hostname is left as it is. Its other functions are never
/// called: net_dgrm.c only calls a driver that started.
/// </summary>
public sealed class VirtualIpxDriver : NetLanDriver
{
    private readonly QuakeEngine engine;

    public VirtualIpxDriver(QuakeEngine engine)
        : base("Winsock IPX")
    {
        this.engine = engine;
    }

    // net_wipx.c: WIPX_Init
    public override int Init()
    {
        if (engine.COM_CheckParm("-noipx") != 0)
        {
            return -1;
        }

        engine.Con_Printf("WIPX_Init: Unable to open control socket\n");
        return -1;
    }

    public override void Shutdown()
    {
    }

    public override void Listen(bool state)
    {
    }

    public override int OpenSocket(int port) => -1;

    public override int CloseSocket(int socket) => 0;

    public override int Connect(int socket, in QSockAddr addr) => 0;

    public override int CheckNewConnections() => -1;

    public override int Read(int socket, byte[] buf, int len, ref QSockAddr addr) => 0;

    public override int Write(int socket, byte[] buf, int len, in QSockAddr addr) => -1;

    public override int Broadcast(int socket, byte[] buf, int len) => -1;

    public override string AddrToString(in QSockAddr addr) => "";

    public override int StringToAddr(string @string, ref QSockAddr addr) => -1;

    public override int GetSocketAddr(int socket, ref QSockAddr addr) => -1;

    public override int GetNameFromAddr(in QSockAddr addr, out string name)
    {
        name = "";
        return -1;
    }

    public override int GetAddrFromName(string name, ref QSockAddr addr) => -1;

    public override int AddrCompare(in QSockAddr addr1, in QSockAddr addr2) => -1;

    public override int GetSocketPort(in QSockAddr addr) => 0;

    public override int SetSocketPort(ref QSockAddr addr, int port) => 0;
}
