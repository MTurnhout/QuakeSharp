using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Net;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Net.NetDef;

namespace QuakeSharp.Core;

// net_main.c: the network layer over its drivers (net_vcr.c's recorder, -record and -playback, is not ported:
// decision 24)
public sealed partial class QuakeEngine
{
    public QSocket? net_activeSockets;
    public QSocket? net_freeSockets;
    public int net_numsockets;

    public bool serialAvailable;
    public bool ipxAvailable;
    public bool tcpipAvailable;

    public int net_hostport;
    public int DEFAULTnet_hostport = 26000;

    public string my_ipx_address = "";
    public string my_tcpip_address = "";

    // net_main.c: GetComPortConfig, SetComPortConfig, GetModemConfig, SetModemConfig (net_ser.c's: no serial driver,
    // so never set)
    public Action<int, int, int, int, bool>? SetComPortConfig;
    public Action<int, string, string, string, string>? SetModemConfig;

    private bool listening;

    public bool slistInProgress;
    public bool slistSilent;
    public bool slistLocal = true;
    private double slistStartTime;
    private int slistLastShown;

    private readonly PollProcedure slistSendProcedure;
    private readonly PollProcedure slistPollProcedure;

    /// <summary>net_main.c: net_message</summary>
    public SizeBuf net_message = new(NET_MAXMESSAGE);

    public int net_activeconnections;

    public int messagesSent;
    public int messagesReceived;
    public int unreliableMessagesSent;
    public int unreliableMessagesReceived;

    public readonly Cvar net_messagetimeout = new("net_messagetimeout", "300");
    public readonly Cvar hostname = new("hostname", "UNNAMED");

    public bool configRestored;
    public readonly Cvar config_com_port = new("_config_com_port", "0x3f8", true);
    public readonly Cvar config_com_irq = new("_config_com_irq", "4", true);
    public readonly Cvar config_com_baud = new("_config_com_baud", "57600", true);
    public readonly Cvar config_com_modem = new("_config_com_modem", "1", true);
    public readonly Cvar config_modem_dialtype = new("_config_modem_dialtype", "T", true);
    public readonly Cvar config_modem_clear = new("_config_modem_clear", "ATZ", true);
    public readonly Cvar config_modem_init = new("_config_modem_init", "", true);
    public readonly Cvar config_modem_hangup = new("_config_modem_hangup", "AT H", true);

    /// <summary>net_win.c: net_drivers, loopback then datagram</summary>
    public NetDriver[] net_drivers = [];

    public int net_numdrivers;

    public int net_driverlevel;

    public double net_time;

    public int hostCacheCount;
    public readonly HostCache[] hostcache = [.. Enumerable.Range(0, HOSTCACHESIZE).Select(_ => new HostCache())];

    private PollProcedure? pollProcedureList;

    /// <summary>
    /// Called with every message NET_SendMessage and NET_SendUnreliableMessage pass to a driver (the socket, the
    /// message, reliable or not, the driver's result): the traces' M line (the oracle's patch calls Trace_Message there).
    /// </summary>
    public Action<QSocket, SizeBuf, bool, int>? MessageSent { get; set; }

    // net_main.c: SetNetTime
    public double SetNetTime()
    {
        net_time = Sys_FloatTime();
        return net_time;
    }

    // net_main.c: NET_NewQSocket
    // Called by drivers when a new communications endpoint is required. The sequence and buffer fields will be filled
    // in properly
    public QSocket? NET_NewQSocket()
    {
        QSocket sock;

        if (net_freeSockets == null)
        {
            return null;
        }

        if (net_activeconnections >= svs.maxclients)
        {
            return null;
        }

        // get one from free list
        sock = net_freeSockets;
        net_freeSockets = sock.next;

        // add it to active list
        sock.next = net_activeSockets;
        net_activeSockets = sock;

        sock.disconnected = false;
        sock.connecttime = net_time;
        sock.address = "UNSET ADDRESS";
        sock.driver = net_driverlevel;
        sock.socket = 0;
        sock.driverdata = null;
        sock.canSend = true;
        sock.sendNext = false;
        sock.lastMessageTime = net_time;
        sock.ackSequence = 0;
        sock.sendSequence = 0;
        sock.unreliableSendSequence = 0;
        sock.sendMessageLength = 0;
        sock.receiveSequence = 0;
        sock.unreliableReceiveSequence = 0;
        sock.receiveMessageLength = 0;

        return sock;
    }

    // net_main.c: NET_FreeQSocket
    public void NET_FreeQSocket(QSocket sock)
    {
        QSocket? s;

        // remove it from active list
        if (sock == net_activeSockets)
        {
            net_activeSockets = net_activeSockets.next;
        }
        else
        {
            for (s = net_activeSockets; s != null; s = s.next)
            {
                if (s.next == sock)
                {
                    s.next = sock.next;
                    break;
                }
            }

            if (s == null)
            {
                throw Sys_Error("NET_FreeQSocket: not active\n");
            }
        }

        // add it to free list
        sock.next = net_freeSockets;
        net_freeSockets = sock;
        sock.disconnected = true;
    }

    // net_main.c: NET_Listen_f
    private void NET_Listen_f()
    {
        if (Cmd_Argc() != 2)
        {
            Con_Printf("\"listen\" is \"%u\"\n", listening ? 1 : 0);
            return;
        }

        listening = Q_atoi(Cmd_Argv(1)) != 0;

        for (net_driverlevel = 0; net_driverlevel < net_numdrivers; net_driverlevel++)
        {
            if (!net_drivers[net_driverlevel].initialized)
            {
                continue;
            }

            net_drivers[net_driverlevel].Listen(listening);
        }
    }

    // net_main.c: MaxPlayers_f
    private void MaxPlayers_f()
    {
        int n;

        if (Cmd_Argc() != 2)
        {
            Con_Printf("\"maxplayers\" is \"%u\"\n", svs.maxclients);
            return;
        }

        if (sv.active)
        {
            Con_Printf("maxplayers can not be changed while a server is running.\n");
            return;
        }

        n = Q_atoi(Cmd_Argv(1));
        if (n < 1)
        {
            n = 1;
        }

        if (n > svs.maxclientslimit)
        {
            n = svs.maxclientslimit;
            Con_Printf("\"maxplayers\" set to \"%u\"\n", n);
        }

        if (n == 1 && listening)
        {
            Cbuf_AddText("listen 0\n");
        }

        if (n > 1 && !listening)
        {
            Cbuf_AddText("listen 1\n");
        }

        svs.maxclients = n;
        if (n == 1)
        {
            Cvar_Set("deathmatch", "0");
        }
        else
        {
            Cvar_Set("deathmatch", "1");
        }
    }

    // net_main.c: NET_Port_f
    private void NET_Port_f()
    {
        int n;

        if (Cmd_Argc() != 2)
        {
            Con_Printf("\"port\" is \"%u\"\n", net_hostport);
            return;
        }

        n = Q_atoi(Cmd_Argv(1));
        if (n < 1 || n > 65534)
        {
            Con_Printf("Bad value, must be between 1 and 65534\n");
            return;
        }

        DEFAULTnet_hostport = n;
        net_hostport = n;

        if (listening)
        {
            // force a change to the new port
            Cbuf_AddText("listen 0\n");
            Cbuf_AddText("listen 1\n");
        }
    }

    // net_main.c: PrintSlistHeader
    private void PrintSlistHeader()
    {
        Con_Printf("Server          Map             Users\n");
        Con_Printf("--------------- --------------- -----\n");
        slistLastShown = 0;
    }

    // net_main.c: PrintSlist
    private void PrintSlist()
    {
        int n;

        for (n = slistLastShown; n < hostCacheCount; n++)
        {
            if (hostcache[n].maxusers != 0)
            {
                Con_Printf("%-15.15s %-15.15s %2u/%2u\n", hostcache[n].name, hostcache[n].map, hostcache[n].users, hostcache[n].maxusers);
            }
            else
            {
                Con_Printf("%-15.15s %-15.15s\n", hostcache[n].name, hostcache[n].map);
            }
        }

        slistLastShown = n;
    }

    // net_main.c: PrintSlistTrailer
    private void PrintSlistTrailer()
    {
        if (hostCacheCount != 0)
        {
            Con_Printf("== end list ==\n\n");
        }
        else
        {
            Con_Printf("No Quake servers found.\n\n");
        }
    }

    // net_main.c: NET_Slist_f
    public void NET_Slist_f()
    {
        if (slistInProgress)
        {
            return;
        }

        if (!slistSilent)
        {
            Con_Printf("Looking for Quake servers...\n");
            PrintSlistHeader();
        }

        slistInProgress = true;
        slistStartTime = Sys_FloatTime();

        SchedulePollProcedure(slistSendProcedure, 0.0);
        SchedulePollProcedure(slistPollProcedure, 0.1);

        hostCacheCount = 0;
    }

    // net_main.c: Slist_Send
    private void Slist_Send()
    {
        for (net_driverlevel = 0; net_driverlevel < net_numdrivers; net_driverlevel++)
        {
            if (!slistLocal && net_driverlevel == 0)
            {
                continue;
            }

            if (!net_drivers[net_driverlevel].initialized)
            {
                continue;
            }

            net_drivers[net_driverlevel].SearchForHosts(true);
        }

        if (Sys_FloatTime() - slistStartTime < 0.5)
        {
            SchedulePollProcedure(slistSendProcedure, 0.75);
        }
    }

    // net_main.c: Slist_Poll
    private void Slist_Poll()
    {
        for (net_driverlevel = 0; net_driverlevel < net_numdrivers; net_driverlevel++)
        {
            if (!slistLocal && net_driverlevel == 0)
            {
                continue;
            }

            if (!net_drivers[net_driverlevel].initialized)
            {
                continue;
            }

            net_drivers[net_driverlevel].SearchForHosts(false);
        }

        if (!slistSilent)
        {
            PrintSlist();
        }

        if (Sys_FloatTime() - slistStartTime < 1.5)
        {
            SchedulePollProcedure(slistPollProcedure, 0.1);
            return;
        }

        if (!slistSilent)
        {
            PrintSlistTrailer();
        }

        slistInProgress = false;
        slistSilent = false;
        slistLocal = true;
    }

    // net_main.c: NET_Connect
    public QSocket? NET_Connect(string? host)
    {
        QSocket? ret;
        int n;
        int numdrivers = net_numdrivers;

        SetNetTime();

        if (host != null && host.Length == 0)
        {
            host = null;
        }

        if (host != null)
        {
            if (Q_strcasecmp(host, "local") == 0)
            {
                numdrivers = 1;
                goto JustDoIt;
            }

            if (hostCacheCount != 0)
            {
                for (n = 0; n < hostCacheCount; n++)
                {
                    if (Q_strcasecmp(host, hostcache[n].name) == 0)
                    {
                        host = hostcache[n].cname;
                        break;
                    }
                }

                if (n < hostCacheCount)
                {
                    goto JustDoIt;
                }
            }
        }

        slistSilent = host != null;
        NET_Slist_f();

        while (slistInProgress)
        {
            NET_Poll();
        }

        if (host == null)
        {
            if (hostCacheCount != 1)
            {
                return null;
            }

            host = hostcache[0].cname;
            Con_Printf("Connecting to...\n%s @ %s\n\n", hostcache[0].name, host);
        }

        if (hostCacheCount != 0)
        {
            for (n = 0; n < hostCacheCount; n++)
            {
                if (Q_strcasecmp(host, hostcache[n].name) == 0)
                {
                    host = hostcache[n].cname;
                    break;
                }
            }
        }

    JustDoIt:
        for (net_driverlevel = 0; net_driverlevel < numdrivers; net_driverlevel++)
        {
            if (!net_drivers[net_driverlevel].initialized)
            {
                continue;
            }

            ret = net_drivers[net_driverlevel].Connect(host);
            if (ret != null)
            {
                return ret;
            }
        }

        if (host != null)
        {
            Con_Printf("\n");
            PrintSlistHeader();
            PrintSlist();
            PrintSlistTrailer();
        }

        return null;
    }

    // net_main.c: NET_CheckNewConnections
    public QSocket? NET_CheckNewConnections()
    {
        QSocket? ret;

        SetNetTime();

        for (net_driverlevel = 0; net_driverlevel < net_numdrivers; net_driverlevel++)
        {
            if (!net_drivers[net_driverlevel].initialized)
            {
                continue;
            }

            if (net_driverlevel != 0 && !listening)
            {
                continue;
            }

            ret = net_drivers[net_driverlevel].CheckNewConnections();
            if (ret != null)
            {
                return ret;
            }
        }

        return null;
    }

    // net_main.c: NET_Close
    public void NET_Close(QSocket? sock)
    {
        if (sock == null)
        {
            return;
        }

        if (sock.disconnected)
        {
            return;
        }

        SetNetTime();

        // call the driver_Close function
        net_drivers[sock.driver].Close(sock);

        NET_FreeQSocket(sock);
    }

    // net_main.c: NET_GetMessage
    // If there is a complete message, return it in net_message
    // returns 0 if no data is waiting
    // returns 1 if a message was received
    // returns -1 if connection is invalid
    public int NET_GetMessage(QSocket? sock)
    {
        int ret;

        if (sock == null)
        {
            return -1;
        }

        if (sock.disconnected)
        {
            Con_Printf("NET_GetMessage: disconnected socket\n");
            return -1;
        }

        SetNetTime();

        ret = net_drivers[sock.driver].QGetMessage(sock);

        // see if this connection has timed out
        if (ret == 0 && sock.driver != 0)
        {
            if (net_time - sock.lastMessageTime > net_messagetimeout.value)
            {
                NET_Close(sock);
                return -1;
            }
        }

        if (ret > 0)
        {
            if (sock.driver != 0)
            {
                sock.lastMessageTime = net_time;
                if (ret == 1)
                {
                    messagesReceived++;
                }
                else if (ret == 2)
                {
                    unreliableMessagesReceived++;
                }
            }
        }

        return ret;
    }

    // net_main.c: NET_SendMessage
    // Try to send a complete length+message unit over the reliable stream.
    // returns 0 if the message cannot be delivered reliably, but the connection is still considered valid
    // returns 1 if the message was sent properly
    // returns -1 if the connection died
    public int NET_SendMessage(QSocket? sock, SizeBuf data)
    {
        int r;

        if (sock == null)
        {
            return -1;
        }

        if (sock.disconnected)
        {
            Con_Printf("NET_SendMessage: disconnected socket\n");
            return -1;
        }

        SetNetTime();
        r = net_drivers[sock.driver].QSendMessage(sock, data);
        if (r == 1 && sock.driver != 0)
        {
            messagesSent++;
        }

        MessageSent?.Invoke(sock, data, true, r);
        return r;
    }

    // net_main.c: NET_SendUnreliableMessage
    public int NET_SendUnreliableMessage(QSocket? sock, SizeBuf data)
    {
        int r;

        if (sock == null)
        {
            return -1;
        }

        if (sock.disconnected)
        {
            Con_Printf("NET_SendMessage: disconnected socket\n");
            return -1;
        }

        SetNetTime();
        r = net_drivers[sock.driver].SendUnreliableMessage(sock, data);
        if (r == 1 && sock.driver != 0)
        {
            unreliableMessagesSent++;
        }

        MessageSent?.Invoke(sock, data, false, r);
        return r;
    }

    // net_main.c: NET_CanSendMessage
    // Returns true or false if the given qsocket can currently accept a message to be transmitted.
    public bool NET_CanSendMessage(QSocket? sock)
    {
        if (sock == null)
        {
            return false;
        }

        if (sock.disconnected)
        {
            return false;
        }

        SetNetTime();

        return net_drivers[sock.driver].CanSendMessage(sock);
    }

    // net_main.c: NET_SendToAll
    // This is a reliable *blocking* send to all attached clients.
    public int NET_SendToAll(SizeBuf data, int blocktime)
    {
        double start;
        int i;
        int count = 0;
        Span<bool> state1 = stackalloc bool[QuakeDef.MAX_SCOREBOARD];
        Span<bool> state2 = stackalloc bool[QuakeDef.MAX_SCOREBOARD];

        for (i = 0; i < svs.maxclients; i++)
        {
            host_client = svs.clients[i];
            if (host_client.netconnection == null)
            {
                // the C leaves this slot's states as the stack had them (design.md §3.3): done
                state1[i] = true;
                state2[i] = true;
                continue;
            }

            if (host_client.active)
            {
                if (host_client.netconnection.driver == 0)
                {
                    NET_SendMessage(host_client.netconnection, data);
                    state1[i] = true;
                    state2[i] = true;
                    continue;
                }

                count++;
                state1[i] = false;
                state2[i] = false;
            }
            else
            {
                state1[i] = true;
                state2[i] = true;
            }
        }

        start = Sys_FloatTime();
        while (count != 0)
        {
            count = 0;
            for (i = 0; i < svs.maxclients; i++)
            {
                host_client = svs.clients[i];
                if (!state1[i])
                {
                    if (NET_CanSendMessage(host_client.netconnection))
                    {
                        state1[i] = true;
                        NET_SendMessage(host_client.netconnection, data);
                    }
                    else
                    {
                        NET_GetMessage(host_client.netconnection);
                    }

                    count++;
                    continue;
                }

                if (!state2[i])
                {
                    if (NET_CanSendMessage(host_client.netconnection))
                    {
                        state2[i] = true;
                    }
                    else
                    {
                        NET_GetMessage(host_client.netconnection);
                    }

                    count++;
                    continue;
                }
            }

            if (Sys_FloatTime() - start > blocktime)
            {
                break;
            }
        }

        return count;
    }

    // net_main.c: NET_Init
    public void NET_Init()
    {
        int i;
        int controlSocket;
        QSocket s;

        i = COM_CheckParm("-port");
        if (i == 0)
        {
            i = COM_CheckParm("-udpport");
        }

        if (i == 0)
        {
            i = COM_CheckParm("-ipxport");
        }

        if (i != 0)
        {
            if (i < com_argv.Argc - 1)
            {
                DEFAULTnet_hostport = Q_atoi(com_argv.Argv(i + 1));
            }
            else
            {
                throw Sys_Error("NET_Init: you must specify a number after -port");
            }
        }

        net_hostport = DEFAULTnet_hostport;

        if (COM_CheckParm("-listen") != 0 || cls.state == cactive_t.ca_dedicated)
        {
            listening = true;
        }

        net_numsockets = svs.maxclientslimit;
        if (cls.state != cactive_t.ca_dedicated)
        {
            net_numsockets++;
        }

        SetNetTime();

        for (i = 0; i < net_numsockets; i++)
        {
            s = new QSocket
            {
                next = net_freeSockets,
            };
            net_freeSockets = s;
            s.disconnected = true;
        }

        // allocate space for network message buffer
        net_message = new SizeBuf(NET_MAXMESSAGE);

        Cvar_RegisterVariable(net_messagetimeout);
        Cvar_RegisterVariable(hostname);
        Cvar_RegisterVariable(config_com_port);
        Cvar_RegisterVariable(config_com_irq);
        Cvar_RegisterVariable(config_com_baud);
        Cvar_RegisterVariable(config_com_modem);
        Cvar_RegisterVariable(config_modem_dialtype);
        Cvar_RegisterVariable(config_modem_clear);
        Cvar_RegisterVariable(config_modem_init);
        Cvar_RegisterVariable(config_modem_hangup);

        Cmd_AddCommand("slist", NET_Slist_f);
        Cmd_AddCommand("listen", NET_Listen_f);
        Cmd_AddCommand("maxplayers", MaxPlayers_f);
        Cmd_AddCommand("port", NET_Port_f);

        // initialize all the drivers
        for (net_driverlevel = 0; net_driverlevel < net_numdrivers; net_driverlevel++)
        {
            controlSocket = net_drivers[net_driverlevel].Init();
            if (controlSocket == -1)
            {
                continue;
            }

            net_drivers[net_driverlevel].initialized = true;
            net_drivers[net_driverlevel].controlSock = controlSocket;
            if (listening)
            {
                net_drivers[net_driverlevel].Listen(true);
            }
        }

        if (my_ipx_address.Length != 0)
        {
            Con_DPrintf("IPX address %s\n", my_ipx_address);
        }

        if (my_tcpip_address.Length != 0)
        {
            Con_DPrintf("TCP/IP address %s\n", my_tcpip_address);
        }
    }

    // net_main.c: NET_Shutdown
    public void NET_Shutdown()
    {
        QSocket? sock;

        SetNetTime();

        for (sock = net_activeSockets; sock != null; sock = sock.next)
        {
            NET_Close(sock);
        }

        // shutdown the drivers
        for (net_driverlevel = 0; net_driverlevel < net_numdrivers; net_driverlevel++)
        {
            if (net_drivers[net_driverlevel].initialized)
            {
                net_drivers[net_driverlevel].Shutdown();
                net_drivers[net_driverlevel].initialized = false;
            }
        }
    }

    // net_main.c: NET_Poll
    public void NET_Poll()
    {
        PollProcedure? pp;
        bool useModem;

        if (!configRestored)
        {
            if (serialAvailable)
            {
                useModem = config_com_modem.value == 1.0;
                SetComPortConfig?.Invoke(0, (int)config_com_port.value, (int)config_com_irq.value, (int)config_com_baud.value, useModem);
                SetModemConfig?.Invoke(0, config_modem_dialtype.@string, config_modem_clear.@string, config_modem_init.@string, config_modem_hangup.@string);
            }

            configRestored = true;
        }

        SetNetTime();

        for (pp = pollProcedureList; pp != null; pp = pp.next)
        {
            if (pp.nextTime > net_time)
            {
                break;
            }

            pollProcedureList = pp.next;
            pp.procedure();
        }
    }

    // net_main.c: SchedulePollProcedure
    public void SchedulePollProcedure(PollProcedure proc, double timeOffset)
    {
        PollProcedure? pp, prev;

        proc.nextTime = Sys_FloatTime() + timeOffset;
        for (pp = pollProcedureList, prev = null; pp != null; pp = pp.next)
        {
            if (pp.nextTime >= proc.nextTime)
            {
                break;
            }

            prev = pp;
        }

        if (prev == null)
        {
            proc.next = pollProcedureList;
            pollProcedureList = proc;
            return;
        }

        proc.next = pp;
        prev.next = proc;
    }
}
