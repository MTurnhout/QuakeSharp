using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

/// <summary>host.c: host_abortserver. Host_Error and Host_EndGame throw it; _Host_Frame catches it, as its setjmp.</summary>
public sealed class HostAbortException : Exception
{
    public HostAbortException()
    {
    }

    public HostAbortException(string message)
        : base(message)
    {
    }

    public HostAbortException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>sys_win.c: Sys_Quit. The host has shut down; the program ends.</summary>
public sealed class QuitException : Exception
{
    public QuitException()
    {
    }

    public QuitException(string message)
        : base(message)
    {
    }

    public QuitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// host.c: the host. A server can always be started, even if the system started out as a client to a remote system.
// A client can NOT be started if the system started as a dedicated server.
public sealed partial class QuakeEngine
{
    /// <summary>true if into command execution</summary>
    public bool host_initialized;

    public double host_frametime;

    public double host_time;

    /// <summary>without any filtering or bounding</summary>
    public double realtime;

    /// <summary>last frame run</summary>
    public double oldrealtime;

    public int host_framecount;

    public int minimum_memory;

    /// <summary>host.c: host_client, the current client</summary>
    public ServerClient? host_client;

    /// <summary>host.c: host_basepal, gfx/palette.lmp</summary>
    public byte[] host_basepal = [];

    /// <summary>host.c: host_colormap, gfx/colormap.lmp</summary>
    public byte[] host_colormap = [];

    /// <summary>sys_win.c: isDedicated (-dedicated)</summary>
    public bool isDedicated;

    // host.c: the host's cvars
    public readonly Cvar host_framerate = new("host_framerate", "0"); // set for slow motion
    public readonly Cvar host_speeds = new("host_speeds", "0"); // set for running times
    public readonly Cvar sys_ticrate = new("sys_ticrate", "0.05");
    public readonly Cvar serverprofile = new("serverprofile", "0");
    public readonly Cvar fraglimit = new("fraglimit", "0", false, true);
    public readonly Cvar timelimit = new("timelimit", "0", false, true);
    public readonly Cvar teamplay = new("teamplay", "0", false, true);
    public readonly Cvar samelevel = new("samelevel", "0");
    public readonly Cvar noexit = new("noexit", "0", false, true);
    public readonly Cvar developer = new("developer", "0");
    public readonly Cvar skill = new("skill", "1"); // 0 - 3
    public readonly Cvar deathmatch = new("deathmatch", "0"); // 0, 1, or 2
    public readonly Cvar coop = new("coop", "0"); // 0 or 1
    public readonly Cvar pausable = new("pausable", "1");
    public readonly Cvar temp1 = new("temp1", "0");

    // host.c: Host_Error's static inerror, Host_Shutdown's static isdown
    private bool host_inerror;
    private bool host_isdown;

    // host.c: _Host_Frame's static time1, time2, time3; Host_Frame's static timetotal, timecount
    private double host_time1, host_time2, host_time3;
    private double host_timetotal;
    private int host_timecount;

    // host.c: Host_EndGame
    public HostAbortException Host_EndGame(string message, params ReadOnlySpan<PrintfArg> args)
    {
        string @string = CFormat.Sprintf(message, args);
        Con_DPrintf("Host_EndGame: %s\n", @string);

        if (sv.active)
        {
            Host_ShutdownServer(false);
        }

        if (cls.state == cactive_t.ca_dedicated)
        {
            throw Sys_Error("Host_EndGame: %s\n", @string); // dedicated servers exit
        }

        if (cls.demonum != -1)
        {
            CL_NextDemo();
        }
        else
        {
            CL_Disconnect();
        }

        return new HostAbortException(@string);
    }

    // host.c: Host_Error
    // This shuts down both the client and server
    public HostAbortException Host_Error(string error, params ReadOnlySpan<PrintfArg> args)
    {
        if (host_inerror)
        {
            throw Sys_Error("Host_Error: recursively entered");
        }

        host_inerror = true;

        SCR_EndLoadingPlaque(); // reenable screen updates

        string @string = CFormat.Sprintf(error, args);
        Con_Printf("Host_Error: %s\n", @string);

        if (sv.active)
        {
            Host_ShutdownServer(false);
        }

        if (cls.state == cactive_t.ca_dedicated)
        {
            throw Sys_Error("Host_Error: %s\n", @string); // dedicated servers exit
        }

        CL_Disconnect();
        cls.demonum = -1;

        host_inerror = false;

        return new HostAbortException(@string);
    }

    // host.c: Host_FindMaxClients
    public void Host_FindMaxClients()
    {
        int i;

        svs.maxclients = 1;

        i = COM_CheckParm("-dedicated");
        if (i != 0)
        {
            cls.state = cactive_t.ca_dedicated;
            if (i != com_argv.Argc - 1)
            {
                svs.maxclients = Q_atoi(com_argv.Argv(i + 1));
            }
            else
            {
                svs.maxclients = 8;
            }
        }
        else
        {
            cls.state = cactive_t.ca_disconnected;
        }

        i = COM_CheckParm("-listen");
        if (i != 0)
        {
            if (cls.state == cactive_t.ca_dedicated)
            {
                throw Sys_Error("Only one of -dedicated or -listen can be specified");
            }

            if (i != com_argv.Argc - 1)
            {
                svs.maxclients = Q_atoi(com_argv.Argv(i + 1));
            }
            else
            {
                svs.maxclients = 8;
            }
        }

        if (svs.maxclients < 1)
        {
            svs.maxclients = 8;
        }
        else if (svs.maxclients > MAX_SCOREBOARD)
        {
            svs.maxclients = MAX_SCOREBOARD;
        }

        svs.maxclientslimit = svs.maxclients;
        if (svs.maxclientslimit < 4)
        {
            svs.maxclientslimit = 4;
        }

        svs.clients = [.. Enumerable.Range(0, svs.maxclientslimit).Select(_ => new ServerClient())];

        if (svs.maxclients > 1)
        {
            Cvar_SetValue("deathmatch", 1.0f);
        }
        else
        {
            Cvar_SetValue("deathmatch", 0.0f);
        }
    }

    // host.c: Host_InitLocal
    public void Host_InitLocal()
    {
        Host_InitCommands();

        Cvar_RegisterVariable(host_framerate);
        Cvar_RegisterVariable(host_speeds);

        Cvar_RegisterVariable(sys_ticrate);
        Cvar_RegisterVariable(serverprofile);

        Cvar_RegisterVariable(fraglimit);
        Cvar_RegisterVariable(timelimit);
        Cvar_RegisterVariable(teamplay);
        Cvar_RegisterVariable(samelevel);
        Cvar_RegisterVariable(noexit);
        Cvar_RegisterVariable(skill);
        Cvar_RegisterVariable(developer);
        Cvar_RegisterVariable(deathmatch);
        Cvar_RegisterVariable(coop);

        Cvar_RegisterVariable(pausable);

        Cvar_RegisterVariable(temp1);

        Host_FindMaxClients();

        host_time = 1.0; // so a think at time 0 won't get called
    }

    // host.c: Host_WriteConfiguration
    // Writes key bindings and archived cvars to config.cfg
    public void Host_WriteConfiguration()
    {
        // dedicated servers initialize the host but don't parse and set the config.cfg cvars
        if (host_initialized && !isDedicated)
        {
            var f = new StringBuilder();
            Key_WriteBindings(f);
            Cvar_WriteVariables(f);
            try
            {
                // fopen (..., "w"): text mode, so each \n is written as \r\n, as WinQuake writes it
                File.WriteAllText(fileSystem.WritePath("config.cfg"),
                    f.ToString().Replace("\n", "\r\n", StringComparison.Ordinal), Encoding.Latin1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Con_Printf("Couldn't write config.cfg.\n");
            }
        }
    }

    // host.c: SV_ClientPrintf
    // Sends text across to be displayed
    // FIXME: make this just a stuffed echo?
    public void SV_ClientPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        string @string = CFormat.Sprintf(fmt, args);

        MSG_WriteByte(host_client!.message, Protocol.svc_print);
        MSG_WriteString(host_client.message, @string);
    }

    // host.c: SV_BroadcastPrintf
    // Sends text to all active clients
    public void SV_BroadcastPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        string @string = CFormat.Sprintf(fmt, args);
        int i;

        for (i = 0; i < svs.maxclients; i++)
        {
            if (svs.clients[i].active && svs.clients[i].spawned)
            {
                MSG_WriteByte(svs.clients[i].message, Protocol.svc_print);
                MSG_WriteString(svs.clients[i].message, @string);
            }
        }
    }

    // host.c: Host_ClientCommands
    // Send text over to the client to be executed
    public void Host_ClientCommands(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
        string @string = CFormat.Sprintf(fmt, args);

        MSG_WriteByte(host_client!.message, Protocol.svc_stufftext);
        MSG_WriteString(host_client.message, @string);
    }

    // host.c: SV_DropClient
    // Called when the player is getting totally kicked off the host
    // if (crash = true), don't bother sending signofs
    public void SV_DropClient(bool crash)
    {
        int saveSelf;
        int i;
        ServerClient client;
        ServerClient host_client = this.host_client!;
        int clientnum = Array.IndexOf(svs.clients, host_client);

        if (!crash)
        {
            // send any final messages (don't check for errors)
            if (NET_CanSendMessage(host_client.netconnection))
            {
                MSG_WriteByte(host_client.message, Protocol.svc_disconnect);
                NET_SendMessage(host_client.netconnection, host_client.message);
            }

            if (host_client.edict != null && host_client.spawned)
            {
                // call the prog function for removing a client
                // this will set the body to a dead frame, among other things
                saveSelf = pr_global_struct.self;
                pr_global_struct.self = EDICT_TO_PROG(host_client.edict);
                PR_ExecuteProgram(pr_global_struct.ClientDisconnect);
                pr_global_struct.self = saveSelf;
            }

            Sys_Printf("Client %s removed\n", host_client.name);
        }

        // break the net connection
        NET_Close(host_client.netconnection);
        host_client.netconnection = null;

        // free the client (the body stays around)
        host_client.active = false;
        host_client.name = "";
        host_client.old_frags = -999999;
        net_activeconnections--;

        // send notification to all clients
        for (i = 0; i < svs.maxclients; i++)
        {
            client = svs.clients[i];
            if (!client.active)
            {
                continue;
            }

            MSG_WriteByte(client.message, Protocol.svc_updatename);
            MSG_WriteByte(client.message, clientnum);
            MSG_WriteString(client.message, "");
            MSG_WriteByte(client.message, Protocol.svc_updatefrags);
            MSG_WriteByte(client.message, clientnum);
            MSG_WriteShort(client.message, 0);
            MSG_WriteByte(client.message, Protocol.svc_updatecolors);
            MSG_WriteByte(client.message, clientnum);
            MSG_WriteByte(client.message, 0);
        }
    }

    // host.c: Host_ShutdownServer
    // This only happens at the end of a game, not between levels
    public void Host_ShutdownServer(bool crash)
    {
        int i;
        int count;
        SizeBuf buf;
        double start;

        if (!sv.active)
        {
            return;
        }

        sv.active = false;

        // stop all client sounds immediately
        if (cls.state == cactive_t.ca_connected)
        {
            CL_Disconnect();
        }

        // flush any pending messages - like the score!!!
        start = Sys_FloatTime();
        do
        {
            count = 0;
            for (i = 0; i < svs.maxclients; i++)
            {
                host_client = svs.clients[i];
                if (host_client.active && host_client.message.cursize != 0)
                {
                    if (NET_CanSendMessage(host_client.netconnection))
                    {
                        NET_SendMessage(host_client.netconnection, host_client.message);
                        SZ_Clear(host_client.message);
                    }
                    else
                    {
                        NET_GetMessage(host_client.netconnection);
                        count++;
                    }
                }
            }

            if (Sys_FloatTime() - start > 3.0)
            {
                break;
            }
        }
        while (count != 0);

        // make sure all the clients know we're disconnecting
        buf = new SizeBuf(4) { maxsize = 4 };
        MSG_WriteByte(buf, Protocol.svc_disconnect);
        count = NET_SendToAll(buf, 5);
        if (count != 0)
        {
            Con_Printf("Host_ShutdownServer: NET_SendToAll failed for %u clients\n", count);
        }

        for (i = 0; i < svs.maxclients; i++)
        {
            host_client = svs.clients[i];
            if (host_client.active)
            {
                SV_DropClient(crash);
            }
        }

        // clear structures
        sv = new ServerState();
        for (i = 0; i < svs.maxclientslimit; i++)
        {
            svs.clients[i].Clear();
        }
    }

    // host.c: Host_ClearMemory (the hunk is managed memory: nothing to free)
    public void Host_ClearMemory()
    {
        Con_DPrintf("Clearing memory\n");
        D_FlushCaches();
        Mod_ClearAll();

        cls.signon = 0;
        sv = new ServerState();
        cl = new ClientState();
    }

    // host.c: Host_FilterTime
    // Returns false if the time is too short to run a frame
    public bool Host_FilterTime(float time)
    {
        realtime += time;

        if (!cls.timedemo && realtime - oldrealtime < 1.0 / 72.0)
        {
            return false; // framerate is too high
        }

        host_frametime = realtime - oldrealtime;
        oldrealtime = realtime;

        if (host_framerate.value > 0)
        {
            host_frametime = host_framerate.value;
        }
        else
        {
            // don't allow really long or short frames
            if (host_frametime > 0.1)
            {
                host_frametime = 0.1;
            }

            if (host_frametime < 0.001)
            {
                host_frametime = 0.001;
            }
        }

        return true;
    }

    // host.c: Host_ServerFrame
    public void Host_ServerFrame()
    {
        // run the world state
        pr_global_struct.frametime = (float)host_frametime;

        // set the time and clear the general datagram
        SV_ClearDatagram();

        // check for new clients
        SV_CheckForNewClients();

        // read client messages
        SV_RunClients();

        // move things around and think
        // always pause in single player if in console or menus
        if (!sv.paused && (svs.maxclients > 1 || key_dest == keydest_t.key_game))
        {
            SV_Physics();
        }

        // send all messages to the clients
        SV_SendClientMessages();
    }

    // host.c: Host_GetConsoleCommands
    // Add them exactly as if they had been typed at the console (a dedicated server's console: there is none here)
    public static void Host_GetConsoleCommands()
    {
    }

    // host.c: _Host_Frame
    // Runs all active servers
    private void _Host_Frame(float time)
    {
        int pass1, pass2, pass3;

        try
        {
            // keep the random time dependent
            rand();

            // decide the simulation time
            if (!Host_FilterTime(time))
            {
                return; // don't run too fast, or packets will flood out
            }

            // get new key events
            Sys_SendKeyEvents();

            // allow mice or other external controllers to add commands
            IN_Commands();

            // process console commands
            Cbuf_Execute();

            NET_Poll();

            // if running the server locally, make intentions now
            if (sv.active)
            {
                CL_SendCmd();
            }

            // -------------------
            // server operations
            // -------------------

            // check for commands typed to the host
            Host_GetConsoleCommands();

            if (sv.active)
            {
                Host_ServerFrame();
            }

            // -------------------
            // client operations
            // -------------------

            // if running the server remotely, send intentions now after
            // the incoming messages have been read
            if (!sv.active)
            {
                CL_SendCmd();
            }

            host_time += host_frametime;

            // fetch results from server
            if (cls.state == cactive_t.ca_connected)
            {
                CL_ReadFromServer();
            }

            // update video
            if (host_speeds.value != 0)
            {
                host_time1 = Sys_FloatTime();
            }

            SCR_UpdateScreen();

            if (host_speeds.value != 0)
            {
                host_time2 = Sys_FloatTime();
            }

            // update audio
            if (cls.signon == SIGNONS)
            {
                S_Update(r_origin, vpn, vright, vup);
                CL_DecayLights();
            }
            else
            {
                S_Update(MathLib.vec3_origin, MathLib.vec3_origin, MathLib.vec3_origin, MathLib.vec3_origin);
            }

            CDAudio_Update();

            if (host_speeds.value != 0)
            {
                pass1 = Numerics.X86.FtoI((host_time1 - host_time3) * 1000);
                host_time3 = Sys_FloatTime();
                pass2 = Numerics.X86.FtoI((host_time2 - host_time1) * 1000);
                pass3 = Numerics.X86.FtoI((host_time3 - host_time2) * 1000);
                Con_Printf("%3i tot %3i server %3i gfx %3i snd\n", pass1 + pass2 + pass3, pass1, pass2, pass3);
            }

            host_framecount++;
        }
        catch (HostAbortException)
        {
            // something bad happened, or the server disconnected
        }
    }

    // host.c: Host_Frame
    public void Host_Frame(float time)
    {
        double time1, time2;
        int i, c, m;

        if (serverprofile.value == 0)
        {
            _Host_Frame(time);
            return;
        }

        time1 = Sys_FloatTime();
        _Host_Frame(time);
        time2 = Sys_FloatTime();

        host_timetotal += time2 - time1;
        host_timecount++;

        if (host_timecount < 1000)
        {
            return;
        }

        m = Numerics.X86.FtoI(host_timetotal * 1000 / host_timecount);
        host_timecount = 0;
        host_timetotal = 0;
        c = 0;
        for (i = 0; i < svs.maxclients; i++)
        {
            if (svs.clients[i].active)
            {
                c++;
            }
        }

        Con_Printf("serverprofile: %2i clients %2i msec\n", c, m);
    }

    // host.c: Host_Init (net_vcr.c's recorder, Host_InitVCR, is not ported: a debugging aid of id's network code)
    public void Host_Init()
    {
        if (com_argv.standard_quake)
        {
            minimum_memory = MINIMUM_MEMORY;
        }
        else
        {
            minimum_memory = MINIMUM_MEMORY + 0x100000; // MINIMUM_MEMORY_LEVELPAK
        }

        if (COM_CheckParm("-minmemory") != 0)
        {
            host_parms.memsize = minimum_memory;
        }

        if (host_parms.memsize < minimum_memory)
        {
            throw Sys_Error("Only %4.1f megs of memory available, can't execute game", host_parms.memsize / (float)0x100000);
        }

        isDedicated = COM_CheckParm("-dedicated") != 0;

        Memory_Init();
        Cbuf_Init();
        Cmd_Init();
        V_Init();
        Chase_Init();
        COM_Init(host_parms.basedir);
        Host_InitLocal();
        W_LoadWadFile("gfx.wad");
        Key_Init();
        Con_Init();
        M_Init();
        PR_Init();
        Mod_Init();
        NET_Init();
        SV_Init();

        Con_Printf("Exe: 09:30:49 Mar 21 1997\n"); // "Exe: "__TIME__" "__DATE__": Winquake.exe 1.09's
        Con_Printf("%4.1f megabyte heap\n", host_parms.memsize / (1024 * 1024.0));

        R_InitTextures(); // needed even for dedicated servers

        if (cls.state != cactive_t.ca_dedicated)
        {
            host_basepal = COM_LoadHunkFile("gfx/palette.lmp") ?? throw Sys_Error("Couldn't load gfx/palette.lmp");
            host_colormap = COM_LoadHunkFile("gfx/colormap.lmp") ?? throw Sys_Error("Couldn't load gfx/colormap.lmp");

            VID_Init(host_basepal);

            Draw_Init();
            SCR_Init();
            R_Init();

            // on Win32, sound initialization has to come before video initialization (vid_win.c's VID_Init calls
            // S_Init)
            CDAudio_Init();
            Sbar_Init();
            CL_Init();
            IN_Init();
        }

        Cbuf_InsertText("exec quake.rc\n");

        host_initialized = true;

        Sys_Printf("========Quake Initialized=========\n");
    }

    // host.c: Host_Shutdown
    // FIXME: this is a callback from Sys_Quit and Sys_Error. It would be better to run quit through here before the
    // final handoff to the sys code.
    public void Host_Shutdown()
    {
        if (host_isdown)
        {
            Sys_Printf("recursive shutdown\n");
            return;
        }

        host_isdown = true;

        // keep Con_Printf from trying to update the screen
        scr_disabled_for_loading = true;

        Host_WriteConfiguration();

        CDAudio_Shutdown();
        NET_Shutdown();
        S_Shutdown();
        IN_Shutdown();

        if (cls.state != cactive_t.ca_dedicated)
        {
            VID_Shutdown();
        }
    }

    // sys_win.c: Sys_Quit: the host shuts down and the program ends; exit closes the files the C left open (a demo
    // being recorded or played)
    public QuitException Sys_Quit()
    {
        Host_Shutdown();
        cls.demofile?.Dispose();
        cls.demofile = null;
        return new QuitException();
    }
}
