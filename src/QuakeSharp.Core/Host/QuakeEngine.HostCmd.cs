using System.Text;
using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using QuakeSharp.Core.UI;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// host_cmd.c: the host's commands
public sealed partial class QuakeEngine
{
    public int current_skill;

    public bool noclip_anglehack;

    // host_cmd.c: Host_Quit_f
    public void Host_Quit_f()
    {
        if (key_dest != keydest_t.key_console && cls.state != cactive_t.ca_dedicated)
        {
            M_Menu_Quit_f();
            return;
        }

        CL_Disconnect();
        Host_ShutdownServer(false);

        throw Sys_Quit();
    }

    // host_cmd.c: Host_Connect_f
    // User command to connect to server
    public void Host_Connect_f()
    {
        string name;

        cls.demonum = -1; // stop demo loop in case this fails
        if (cls.demoplayback)
        {
            CL_StopPlayback();
            CL_Disconnect();
        }

        name = Cmd_Argv(1);
        CL_EstablishConnection(name);
        Host_Reconnect_f();
    }

    // host_cmd.c: Host_Reconnect_f
    // This command causes the client to wait for the signon messages again. This is sent just before a server changes
    // levels
    public void Host_Reconnect_f()
    {
        SCR_BeginLoadingPlaque();
        cls.signon = 0; // need new connection messages
    }

    // host_cmd.c: Host_Version_f
    public void Host_Version_f()
    {
        Con_Printf("Version %4.2f\n", VERSION);
        Con_Printf("Exe: 09:30:49 Mar 21 1997\n"); // "Exe: "__TIME__" "__DATE__: Winquake.exe 1.09's
    }

    // host_cmd.c: Host_Startdemos_f
    public void Host_Startdemos_f()
    {
        int i, c;

        if (cls.state == cactive_t.ca_dedicated)
        {
            if (!sv.active)
            {
                Cbuf_AddText("map start\n");
            }

            return;
        }

        c = Cmd_Argc() - 1;
        if (c > ClientStatic.MAX_DEMOS)
        {
            Con_Printf("Max %i demos in demoloop\n", ClientStatic.MAX_DEMOS);
            c = ClientStatic.MAX_DEMOS;
        }

        Con_Printf("%i demo(s) in loop\n", c);

        for (i = 1; i < c + 1; i++)
        {
            // strncpy (cls.demos[i-1], Cmd_Argv(i), sizeof(cls.demos[0])-1): at most 15 characters (the 16th is
            // never written, so it stays 0)
            string name = Cmd_Argv(i);
            cls.demos[i - 1] = name.Length > ClientStatic.MAX_DEMONAME - 1 ? name[..(ClientStatic.MAX_DEMONAME - 1)] : name;
        }

        if (!sv.active && cls.demonum != -1 && !cls.demoplayback)
        {
            cls.demonum = 0;
            CL_NextDemo();
        }
        else
        {
            cls.demonum = -1;
        }
    }

    // host_cmd.c: Host_Demos_f
    // Return to looping demos
    public void Host_Demos_f()
    {
        if (cls.state == cactive_t.ca_dedicated)
        {
            return;
        }

        if (cls.demonum == -1)
        {
            cls.demonum = 1;
        }

        CL_Disconnect_f();
        CL_NextDemo();
    }

    // host_cmd.c: Host_Stopdemo_f
    // Return to looping demos
    public void Host_Stopdemo_f()
    {
        if (cls.state == cactive_t.ca_dedicated)
        {
            return;
        }

        if (!cls.demoplayback)
        {
            return;
        }

        CL_StopPlayback();
        CL_Disconnect();
    }

    // host_cmd.c: Host_InitCommands
    public void Host_InitCommands()
    {
        Cmd_AddCommand("status", Host_Status_f);
        Cmd_AddCommand("quit", Host_Quit_f);
        Cmd_AddCommand("god", Host_God_f);
        Cmd_AddCommand("notarget", Host_Notarget_f);
        Cmd_AddCommand("fly", Host_Fly_f);
        Cmd_AddCommand("map", Host_Map_f);
        Cmd_AddCommand("restart", Host_Restart_f);
        Cmd_AddCommand("changelevel", Host_Changelevel_f);
        Cmd_AddCommand("connect", Host_Connect_f);
        Cmd_AddCommand("reconnect", Host_Reconnect_f);
        Cmd_AddCommand("name", Host_Name_f);
        Cmd_AddCommand("noclip", Host_Noclip_f);
        Cmd_AddCommand("version", Host_Version_f);
        Cmd_AddCommand("say", Host_Say_f);
        Cmd_AddCommand("say_team", Host_Say_Team_f);
        Cmd_AddCommand("tell", Host_Tell_f);
        Cmd_AddCommand("color", Host_Color_f);
        Cmd_AddCommand("kill", Host_Kill_f);
        Cmd_AddCommand("pause", Host_Pause_f);
        Cmd_AddCommand("spawn", Host_Spawn_f);
        Cmd_AddCommand("begin", Host_Begin_f);
        Cmd_AddCommand("prespawn", Host_PreSpawn_f);
        Cmd_AddCommand("kick", Host_Kick_f);
        Cmd_AddCommand("ping", Host_Ping_f);
        Cmd_AddCommand("load", Host_Loadgame_f);
        Cmd_AddCommand("save", Host_Savegame_f);
        Cmd_AddCommand("give", Host_Give_f);

        Cmd_AddCommand("startdemos", Host_Startdemos_f);
        Cmd_AddCommand("demos", Host_Demos_f);
        Cmd_AddCommand("stopdemo", Host_Stopdemo_f);

        Cmd_AddCommand("viewmodel", Host_Viewmodel_f);
        Cmd_AddCommand("viewframe", Host_Viewframe_f);
        Cmd_AddCommand("viewnext", Host_Viewnext_f);
        Cmd_AddCommand("viewprev", Host_Viewprev_f);

        Cmd_AddCommand("mcache", Mod_Print);
    }

    private const int SAVEGAME_VERSION = 5;

    // host_cmd.c: Host_Status_f
    public void Host_Status_f()
    {
        ServerClient client;
        int seconds;
        int minutes;
        int hours = 0;
        int j;
        Action<string, PrintfArg[]> print;

        if (cmd_source == cmd_source_t.src_command)
        {
            if (!sv.active)
            {
                Cmd_ForwardToServer();
                return;
            }

            print = (fmt, args) => Con_Printf(fmt, args);
        }
        else
        {
            print = (fmt, args) => SV_ClientPrintf(fmt, args);
        }

        print("host:    %s\n", [Cvar_VariableString("hostname")]);
        print("version: %4.2f\n", [VERSION]);
        if (tcpipAvailable)
        {
            print("tcp/ip:  %s\n", [my_tcpip_address]);
        }

        if (ipxAvailable)
        {
            print("ipx:     %s\n", [my_ipx_address]);
        }

        print("map:     %s\n", [sv.name]);
        print("players: %i active (%i max)\n\n", [net_activeconnections, svs.maxclients]);
        for (j = 0; j < svs.maxclients; j++)
        {
            client = svs.clients[j];
            if (!client.active)
            {
                continue;
            }

            seconds = X86.FtoI(net_time - client.netconnection!.connecttime);
            minutes = seconds / 60;
            if (minutes != 0)
            {
                seconds -= minutes * 60;
                hours = minutes / 60;
                if (hours != 0)
                {
                    minutes -= hours * 60;
                }
            }
            else
            {
                hours = 0;
            }

            print("#%-2u %-16.16s  %3i  %2i:%02i:%02i\n", [j + 1, client.name, X86.FtoI(client.edict!.v.frags), hours, minutes, seconds]);
            print("   %s\n", [client.netconnection.address]);
        }
    }

    // host_cmd.c: Host_God_f
    // Sets client to godmode
    public void Host_God_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
        {
            return;
        }

        sv_player!.v.flags = X86.FtoI(sv_player.v.flags) ^ FL_GODMODE;
        if ((X86.FtoI(sv_player.v.flags) & FL_GODMODE) == 0)
        {
            SV_ClientPrintf("godmode OFF\n");
        }
        else
        {
            SV_ClientPrintf("godmode ON\n");
        }
    }

    // host_cmd.c: Host_Notarget_f
    public void Host_Notarget_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
        {
            return;
        }

        sv_player!.v.flags = X86.FtoI(sv_player.v.flags) ^ FL_NOTARGET;
        if ((X86.FtoI(sv_player.v.flags) & FL_NOTARGET) == 0)
        {
            SV_ClientPrintf("notarget OFF\n");
        }
        else
        {
            SV_ClientPrintf("notarget ON\n");
        }
    }

    // host_cmd.c: Host_Noclip_f
    public void Host_Noclip_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
        {
            return;
        }

        if (sv_player!.v.movetype != MOVETYPE_NOCLIP)
        {
            noclip_anglehack = true;
            sv_player.v.movetype = MOVETYPE_NOCLIP;
            SV_ClientPrintf("noclip ON\n");
        }
        else
        {
            noclip_anglehack = false;
            sv_player.v.movetype = MOVETYPE_WALK;
            SV_ClientPrintf("noclip OFF\n");
        }
    }

    // host_cmd.c: Host_Fly_f
    // Sets client to flymode
    public void Host_Fly_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
        {
            return;
        }

        if (sv_player!.v.movetype != MOVETYPE_FLY)
        {
            sv_player.v.movetype = MOVETYPE_FLY;
            SV_ClientPrintf("flymode ON\n");
        }
        else
        {
            sv_player.v.movetype = MOVETYPE_WALK;
            SV_ClientPrintf("flymode OFF\n");
        }
    }

    // host_cmd.c: Host_Ping_f
    public void Host_Ping_f()
    {
        int i, j;
        float total;
        ServerClient client;

        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        SV_ClientPrintf("Client ping times:\n");
        for (i = 0; i < svs.maxclients; i++)
        {
            client = svs.clients[i];
            if (!client.active)
            {
                continue;
            }

            total = 0;
            for (j = 0; j < NUM_PING_TIMES; j++)
            {
                total += client.ping_times[j];
            }

            total /= NUM_PING_TIMES;
            SV_ClientPrintf("%4i %s\n", X86.FtoI(total * 1000), client.name);
        }
    }

    // ===============================================================================
    // SERVER TRANSITIONS
    // ===============================================================================

    // host_cmd.c: Host_Map_f
    // handle a
    // map <servername>
    // command from the console. Active clients are kicked off.
    public void Host_Map_f()
    {
        int i;
        string name;

        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        cls.demonum = -1; // stop demo loop in case this fails

        CL_Disconnect();
        Host_ShutdownServer(false);

        key_dest = keydest_t.key_game; // remove console or menu
        SCR_BeginLoadingPlaque();

        var mapstring = new StringBuilder();
        for (i = 0; i < Cmd_Argc(); i++)
        {
            mapstring.Append(Cmd_Argv(i));
            mapstring.Append(' ');
        }

        mapstring.Append('\n');
        cls.mapstring = mapstring.ToString();

        svs.serverflags = 0; // haven't completed an episode yet
        name = Cmd_Argv(1);
        SV_SpawnServer(name);
        if (!sv.active)
        {
            return;
        }

        if (cls.state != cactive_t.ca_dedicated)
        {
            var spawnparms = new StringBuilder();
            for (i = 2; i < Cmd_Argc(); i++)
            {
                spawnparms.Append(Cmd_Argv(i));
                spawnparms.Append(' ');
            }

            cls.spawnparms = spawnparms.ToString();

            Cmd_ExecuteString("connect local", cmd_source_t.src_command);
        }
    }

    // host_cmd.c: Host_Changelevel_f
    // Goes to a new map, taking all clients along
    public void Host_Changelevel_f()
    {
        string level;

        if (Cmd_Argc() != 2)
        {
            Con_Printf("changelevel <levelname> : continue game on a new level\n");
            return;
        }

        if (!sv.active || cls.demoplayback)
        {
            Con_Printf("Only the server may changelevel\n");
            return;
        }

        SV_SaveSpawnparms();
        level = Cmd_Argv(1);
        SV_SpawnServer(level);
    }

    // host_cmd.c: Host_Restart_f
    // Restarts the current server for a dead player
    public void Host_Restart_f()
    {
        string mapname;

        if (cls.demoplayback || !sv.active)
        {
            return;
        }

        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        mapname = sv.name; // must copy out, because it gets cleared in sv_spawnserver
        SV_SpawnServer(mapname);
    }

    // ===============================================================================
    // LOAD / SAVE GAME
    // ===============================================================================

    // host_cmd.c: Host_SavegameComment
    // Writes a SAVEGAME_COMMENT_LENGTH character comment describing the current
    public string Host_SavegameComment()
    {
        int i;
        char[] text = new char[SAVEGAME_COMMENT_LENGTH];
        string kills;

        for (i = 0; i < SAVEGAME_COMMENT_LENGTH; i++)
        {
            text[i] = ' ';
        }

        string levelname = cl.levelname[..Q_strlen(cl.levelname)];
        levelname.CopyTo(0, text, 0, Math.Min(levelname.Length, SAVEGAME_COMMENT_LENGTH));
        kills = CFormat.Sprintf("kills:%3i/%3i", cl.stats[STAT_MONSTERS], cl.stats[STAT_TOTALMONSTERS]);
        kills.CopyTo(0, text, 22, Math.Min(kills.Length, SAVEGAME_COMMENT_LENGTH - 22));

        // convert space to _ to make stdio happy
        for (i = 0; i < SAVEGAME_COMMENT_LENGTH; i++)
        {
            if (text[i] == ' ')
            {
                text[i] = '_';
            }
        }

        return new string(text);
    }

    // host_cmd.c: Host_Savegame_f
    public void Host_Savegame_f()
    {
        string name;
        int i;
        string comment;

        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        if (!sv.active)
        {
            Con_Printf("Not playing a local game.\n");
            return;
        }

        if (cl.intermission != 0)
        {
            Con_Printf("Can't save in intermission.\n");
            return;
        }

        if (svs.maxclients != 1)
        {
            Con_Printf("Can't save multiplayer games.\n");
            return;
        }

        if (Cmd_Argc() != 2)
        {
            Con_Printf("save <savename> : save a game\n");
            return;
        }

        if (Cmd_Argv(1).Contains("..", StringComparison.Ordinal))
        {
            Con_Printf("Relative pathnames are not allowed.\n");
            return;
        }

        for (i = 0; i < svs.maxclients; i++)
        {
            if (svs.clients[i].active && svs.clients[i].edict!.v.health <= 0)
            {
                Con_Printf("Can't savegame with a dead player\n");
                return;
            }
        }

        name = CFormat.Sprintf("%s/%s", com_gamedir, Cmd_Argv(1));
        name = Com.DefaultExtension(name, ".sav");

        Con_Printf("Saving game to %s...\n", name);

        var f = new StringBuilder();
        f.Append(CFormat.Sprintf("%i\n", SAVEGAME_VERSION));
        comment = Host_SavegameComment();
        f.Append(CFormat.Sprintf("%s\n", comment));
        for (i = 0; i < NUM_SPAWN_PARMS; i++)
        {
            f.Append(CFormat.Sprintf("%f\n", svs.clients[0].spawn_parms[i]));
        }

        f.Append(CFormat.Sprintf("%d\n", current_skill));
        f.Append(CFormat.Sprintf("%s\n", sv.name));
        f.Append(CFormat.Sprintf("%f\n", sv.time));

        // write the light styles
        for (i = 0; i < MAX_LIGHTSTYLES; i++)
        {
            if (sv.lightstyles[i] != null)
            {
                f.Append(CFormat.Sprintf("%s\n", sv.lightstyles[i]));
            }
            else
            {
                f.Append("m\n");
            }
        }

        ED_WriteGlobals(f);
        for (i = 0; i < sv.num_edicts; i++)
        {
            ED_Write(f, EDICT_NUM(i));
        }

        try
        {
            // fopen (name, "w"): text mode, so each \n is written as \r\n
            File.WriteAllBytes(fileSystem.WritePath(name[(com_gamedir.Length + 1)..]),
                Encoding.Latin1.GetBytes(f.ToString().Replace("\n", "\r\n", StringComparison.Ordinal)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Con_Printf("ERROR: couldn't open.\n");
            return;
        }

        Con_Printf("done.\n");
    }

    // host_cmd.c: Host_Loadgame_f
    public void Host_Loadgame_f()
    {
        string name;
        string mapname;
        float time, tfloat;
        var str = new StringBuilder();
        int i, r;
        Edict ent;
        int entnum;
        int version;
        float[] spawn_parms = new float[NUM_SPAWN_PARMS];

        if (cmd_source != cmd_source_t.src_command)
        {
            return;
        }

        if (Cmd_Argc() != 2)
        {
            Con_Printf("load <savename> : load a game\n");
            return;
        }

        cls.demonum = -1; // stop demo loop in case this fails

        name = CFormat.Sprintf("%s/%s", com_gamedir, Cmd_Argv(1));
        name = Com.DefaultExtension(name, ".sav");

        // we can't call SCR_BeginLoadingPlaque, because too much stack space has been used. The menu calls it before
        // stuffing loadgame command
        Con_Printf("Loading game from %s...\n", name);
        byte[]? data = fileSystem.ReadWritten(name[(com_gamedir.Length + 1)..]);
        if (data == null)
        {
            Con_Printf("ERROR: couldn't open.\n");
            return;
        }

        var f = new CStdioReader(data);

        // a value the file lacks keeps what the variable held: for the C's uninitialised locals, 0 or empty (design.md
        // §3.3); line is the C's str while it is read with fscanf
        version = 0;
        string line = "";
        mapname = "";
        time = tfloat = 0;
        f.Scanf(ref version);
        if (version != SAVEGAME_VERSION)
        {
            Con_Printf("Savegame is version %i, not %i\n", version, SAVEGAME_VERSION);
            return;
        }

        f.Scanf(ref line);
        for (i = 0; i < NUM_SPAWN_PARMS; i++)
        {
            f.Scanf(ref spawn_parms[i]);
        }

        // this silliness is so we can load 1.06 save files, which have float skill values
        f.Scanf(ref tfloat);
        current_skill = X86.FtoI(tfloat + 0.1);
        Cvar_SetValue("skill", current_skill);

        f.Scanf(ref mapname);
        f.Scanf(ref time);

        CL_Disconnect_f();

        SV_SpawnServer(mapname);
        if (!sv.active)
        {
            Con_Printf("Couldn't load map\n");
            return;
        }

        sv.paused = true; // pause until all clients connect
        sv.loadgame = true;

        // load the light styles
        for (i = 0; i < MAX_LIGHTSTYLES; i++)
        {
            f.Scanf(ref line);
            sv.lightstyles[i] = line;
        }

        // load the edicts out of the savegame file
        entnum = -1; // -1 is the globals
        while (!f.Feof)
        {
            str.Clear();
            for (i = 0; i < 32768 - 1; i++)
            {
                r = f.Getc();
                if (r == -1 || r == 0)
                {
                    break;
                }

                str.Append((char)r);
                if (r == '}')
                {
                    i++;
                    break;
                }
            }

            if (i == 32768 - 1)
            {
                throw Sys_Error("Loadgame buffer overflow");
            }

            string text = str.ToString();
            int start = COM_Parse(text, 0);
            if (Ch(com_token, 0) == 0)
            {
                break; // end of file
            }

            if (Q_strcmp(com_token, "{") != 0)
            {
                throw Sys_Error("First token isn't a brace");
            }

            if (entnum == -1)
            {
                // parse the global vars
                ED_ParseGlobals(text, start);
            }
            else
            {
                // parse an edict
                ent = EDICT_NUM(entnum);
                Array.Clear(sv.edictmem, ent.fields, progs!.entityfields);
                ent.free = false;
                ED_ParseEdict(text, start, ent);

                // link it into the bsp tree
                if (!ent.free)
                {
                    SV_LinkEdict(ent, false);
                }
            }

            entnum++;
        }

        sv.num_edicts = entnum;
        sv.time = time;

        for (i = 0; i < NUM_SPAWN_PARMS; i++)
        {
            svs.clients[0].spawn_parms[i] = spawn_parms[i];
        }

        if (cls.state != cactive_t.ca_dedicated)
        {
            CL_EstablishConnection("local");
            Host_Reconnect_f();
        }
    }

    // ============================================================================

    // host_cmd.c: Host_Name_f
    public void Host_Name_f()
    {
        string newName;

        if (Cmd_Argc() == 1)
        {
            Con_Printf("\"name\" is \"%s\"\n", cl_name.@string);
            return;
        }

        if (Cmd_Argc() == 2)
        {
            newName = Cmd_Argv(1);
        }
        else
        {
            newName = Cmd_Args() ?? "";
        }

        // newName[15] = 0
        newName = newName[..Math.Min(Q_strlen(newName), 15)];

        if (cmd_source == cmd_source_t.src_command)
        {
            if (Q_strcmp(cl_name.@string, newName) == 0)
            {
                return;
            }

            Cvar_Set("_cl_name", newName);
            if (cls.state == cactive_t.ca_connected)
            {
                Cmd_ForwardToServer();
            }

            return;
        }

        if (Ch(host_client!.name, 0) != 0 && Q_strcmp(host_client.name, "unconnected") != 0)
        {
            if (Q_strcmp(host_client.name, newName) != 0)
            {
                Con_Printf("%s renamed to %s\n", host_client.name, newName);
            }
        }

        int clientnum = Array.IndexOf(svs.clients, host_client);
        host_client.name = newName;
        host_client.edict!.v.netname = PR_ClientNameString(clientnum);

        // send notification to all clients
        MSG_WriteByte(sv.reliable_datagram, svc_updatename);
        MSG_WriteByte(sv.reliable_datagram, clientnum);
        MSG_WriteString(sv.reliable_datagram, host_client.name);
    }

    // host_cmd.c: Host_Say
    public void Host_Say(bool teamonly)
    {
        ServerClient client;
        ServerClient save;
        int j;
        string p;
        string text;
        bool fromServer = false;

        if (cmd_source == cmd_source_t.src_command)
        {
            if (cls.state == cactive_t.ca_dedicated)
            {
                fromServer = true;
                teamonly = false;
            }
            else
            {
                Cmd_ForwardToServer();
                return;
            }
        }

        if (Cmd_Argc() < 2)
        {
            return;
        }

        save = host_client!;

        p = Cmd_Args() ?? "";

        // remove quotes if present
        if (Ch(p, 0) == '"')
        {
            p = p[1..];
            p = p[..(Q_strlen(p) - 1)];
        }

        // turn on color set 1
        if (!fromServer)
        {
            text = CFormat.Sprintf("%c%s: ", 1, save.name);
        }
        else
        {
            text = CFormat.Sprintf("%c<%s> ", 1, hostname.@string);
        }

        j = 64 - 2 - Q_strlen(text); // -2 for /n and null terminator
        if (Q_strlen(p) > j)
        {
            p = p[..j];
        }

        text += p;
        text += "\n";

        for (j = 0; j < svs.maxclients; j++)
        {
            client = svs.clients[j];
            if (!client.active || !client.spawned)
            {
                continue;
            }

            if (teamplay.value != 0 && teamonly && client.edict!.v.team != save.edict!.v.team)
            {
                continue;
            }

            host_client = client;
            SV_ClientPrintf("%s", text);
        }

        host_client = save;

        Sys_Printf("%s", text[1..]);
    }

    // host_cmd.c: Host_Say_f
    public void Host_Say_f() => Host_Say(false);

    // host_cmd.c: Host_Say_Team_f
    public void Host_Say_Team_f() => Host_Say(true);

    // host_cmd.c: Host_Tell_f
    public void Host_Tell_f()
    {
        ServerClient client;
        ServerClient save;
        int j;
        string p;
        string text;

        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (Cmd_Argc() < 3)
        {
            return;
        }

        text = host_client!.name;
        text += ": ";

        p = Cmd_Args() ?? "";

        // remove quotes if present
        if (Ch(p, 0) == '"')
        {
            p = p[1..];
            p = p[..(Q_strlen(p) - 1)];
        }

        // check length & truncate if necessary
        j = 64 - 2 - Q_strlen(text); // -2 for /n and null terminator
        if (Q_strlen(p) > j)
        {
            p = p[..j];
        }

        text += p;
        text += "\n";

        save = host_client;
        for (j = 0; j < svs.maxclients; j++)
        {
            client = svs.clients[j];
            if (!client.active || !client.spawned)
            {
                continue;
            }

            if (Q_strcasecmp(client.name, Cmd_Argv(1)) != 0)
            {
                continue;
            }

            host_client = client;
            SV_ClientPrintf("%s", text);
            break;
        }

        host_client = save;
    }

    // host_cmd.c: Host_Color_f
    public void Host_Color_f()
    {
        int top, bottom;
        int playercolor;

        if (Cmd_Argc() == 1)
        {
            Con_Printf("\"color\" is \"%i %i\"\n", X86.FtoI(cl_color.value) >> 4, X86.FtoI(cl_color.value) & 0x0f);
            Con_Printf("color <0-13> [0-13]\n");
            return;
        }

        if (Cmd_Argc() == 2)
        {
            top = bottom = atoi(Cmd_Argv(1));
        }
        else
        {
            top = atoi(Cmd_Argv(1));
            bottom = atoi(Cmd_Argv(2));
        }

        top &= 15;
        if (top > 13)
        {
            top = 13;
        }

        bottom &= 15;
        if (bottom > 13)
        {
            bottom = 13;
        }

        playercolor = (top * 16) + bottom;

        if (cmd_source == cmd_source_t.src_command)
        {
            Cvar_SetValue("_cl_color", playercolor);
            if (cls.state == cactive_t.ca_connected)
            {
                Cmd_ForwardToServer();
            }

            return;
        }

        host_client!.colors = playercolor;
        host_client.edict!.v.team = bottom + 1;

        // send notification to all clients
        MSG_WriteByte(sv.reliable_datagram, svc_updatecolors);
        MSG_WriteByte(sv.reliable_datagram, Array.IndexOf(svs.clients, host_client));
        MSG_WriteByte(sv.reliable_datagram, host_client.colors);
    }

    // host_cmd.c: Host_Kill_f
    public void Host_Kill_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (sv_player!.v.health <= 0)
        {
            SV_ClientPrintf("Can't suicide -- allready dead!\n");
            return;
        }

        pr_global_struct.time = (float)sv.time;
        pr_global_struct.self = EDICT_TO_PROG(sv_player);
        PR_ExecuteProgram(pr_global_struct.ClientKill);
    }

    // host_cmd.c: Host_Pause_f
    public void Host_Pause_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (pausable.value == 0)
        {
            SV_ClientPrintf("Pause not allowed.\n");
        }
        else
        {
            sv.paused = !sv.paused;

            if (sv.paused)
            {
                SV_BroadcastPrintf("%s paused the game\n", PR_GetString(sv_player!.v.netname));
            }
            else
            {
                SV_BroadcastPrintf("%s unpaused the game\n", PR_GetString(sv_player!.v.netname));
            }

            // send notification to all clients
            MSG_WriteByte(sv.reliable_datagram, svc_setpause);
            MSG_WriteByte(sv.reliable_datagram, sv.paused ? 1 : 0);
        }
    }

    // ===========================================================================

    // host_cmd.c: Host_PreSpawn_f
    public void Host_PreSpawn_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Con_Printf("prespawn is not valid from the console\n");
            return;
        }

        if (host_client!.spawned)
        {
            Con_Printf("prespawn not valid -- allready spawned\n");
            return;
        }

        SZ_Write(host_client.message, sv.signon.data.AsSpan(0, sv.signon.cursize));
        MSG_WriteByte(host_client.message, svc_signonnum);
        MSG_WriteByte(host_client.message, 2);
        host_client.sendsignon = true;
    }

    // host_cmd.c: Host_Spawn_f
    public void Host_Spawn_f()
    {
        int i;
        ServerClient client;
        Edict ent;

        if (cmd_source == cmd_source_t.src_command)
        {
            Con_Printf("spawn is not valid from the console\n");
            return;
        }

        if (host_client!.spawned)
        {
            Con_Printf("Spawn not valid -- allready spawned\n");
            return;
        }

        int clientnum = Array.IndexOf(svs.clients, host_client);

        // run the entrance script
        if (sv.loadgame)
        {
            // loaded games are fully inited allready
            // if this is the last client to be connected, unpause
            sv.paused = false;
        }
        else
        {
            // set up the edict
            ent = host_client.edict!;

            Array.Clear(sv.edictmem, ent.fields, progs!.entityfields);
            ent.v.colormap = NUM_FOR_EDICT(ent);
            ent.v.team = (host_client.colors & 15) + 1;
            ent.v.netname = PR_ClientNameString(clientnum);

            // copy spawn parms out of the client_t
            for (i = 0; i < NUM_SPAWN_PARMS; i++)
            {
                Parm(i) = host_client.spawn_parms[i];
            }

            // call the spawn function
            pr_global_struct.time = (float)sv.time;
            pr_global_struct.self = EDICT_TO_PROG(sv_player!);
            PR_ExecuteProgram(pr_global_struct.ClientConnect);

            if (Sys_FloatTime() - host_client.netconnection!.connecttime <= sv.time)
            {
                Sys_Printf("%s entered the game\n", host_client.name);
            }

            PR_ExecuteProgram(pr_global_struct.PutClientInServer);
        }

        // send all current names, colors, and frag counts
        SZ_Clear(host_client.message);

        // send time of update
        MSG_WriteByte(host_client.message, svc_time);
        MSG_WriteFloat(host_client.message, (float)sv.time);

        for (i = 0; i < svs.maxclients; i++)
        {
            client = svs.clients[i];
            MSG_WriteByte(host_client.message, svc_updatename);
            MSG_WriteByte(host_client.message, i);
            MSG_WriteString(host_client.message, client.name);
            MSG_WriteByte(host_client.message, svc_updatefrags);
            MSG_WriteByte(host_client.message, i);
            MSG_WriteShort(host_client.message, client.old_frags);
            MSG_WriteByte(host_client.message, svc_updatecolors);
            MSG_WriteByte(host_client.message, i);
            MSG_WriteByte(host_client.message, client.colors);
        }

        // send all current light styles
        for (i = 0; i < MAX_LIGHTSTYLES; i++)
        {
            MSG_WriteByte(host_client.message, svc_lightstyle);
            MSG_WriteByte(host_client.message, (sbyte)i);
            MSG_WriteString(host_client.message, sv.lightstyles[i]);
        }

        // send some stats
        MSG_WriteByte(host_client.message, svc_updatestat);
        MSG_WriteByte(host_client.message, STAT_TOTALSECRETS);
        MSG_WriteLong(host_client.message, X86.FtoI(pr_global_struct.total_secrets));

        MSG_WriteByte(host_client.message, svc_updatestat);
        MSG_WriteByte(host_client.message, STAT_TOTALMONSTERS);
        MSG_WriteLong(host_client.message, X86.FtoI(pr_global_struct.total_monsters));

        MSG_WriteByte(host_client.message, svc_updatestat);
        MSG_WriteByte(host_client.message, STAT_SECRETS);
        MSG_WriteLong(host_client.message, X86.FtoI(pr_global_struct.found_secrets));

        MSG_WriteByte(host_client.message, svc_updatestat);
        MSG_WriteByte(host_client.message, STAT_MONSTERS);
        MSG_WriteLong(host_client.message, X86.FtoI(pr_global_struct.killed_monsters));

        // send a fixangle
        // Never send a roll angle, because savegames can catch the server in a state where it is expecting the client
        // to correct the angle and it won't happen if the game was just loaded, so you wind up with a permanent head
        // tilt
        ent = EDICT_NUM(1 + clientnum);
        MSG_WriteByte(host_client.message, svc_setangle);
        for (i = 0; i < 2; i++)
        {
            MSG_WriteAngle(host_client.message, ent.v.angles[i]);
        }

        MSG_WriteAngle(host_client.message, 0);

        SV_WriteClientdataToMessage(sv_player!, host_client.message);

        MSG_WriteByte(host_client.message, svc_signonnum);
        MSG_WriteByte(host_client.message, 3);
        host_client.sendsignon = true;
    }

    // host_cmd.c: Host_Begin_f
    public void Host_Begin_f()
    {
        if (cmd_source == cmd_source_t.src_command)
        {
            Con_Printf("begin is not valid from the console\n");
            return;
        }

        host_client!.spawned = true;
    }

    // ===========================================================================

    // host_cmd.c: Host_Kick_f
    // Kicks a user off of the server
    public void Host_Kick_f()
    {
        string who;
        string? message = null;
        ServerClient save;
        int i;
        bool byNumber = false;

        if (cmd_source == cmd_source_t.src_command)
        {
            if (!sv.active)
            {
                Cmd_ForwardToServer();
                return;
            }
        }
        else if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
        {
            return;
        }

        save = host_client!;

        if (Cmd_Argc() > 2 && Q_strcmp(Cmd_Argv(1), "#") == 0)
        {
            i = X86.FtoI(Q_atof(Cmd_Argv(2)) - 1);
            if (i < 0 || i >= svs.maxclients)
            {
                return;
            }

            if (!svs.clients[i].active)
            {
                return;
            }

            host_client = svs.clients[i];
            byNumber = true;
        }
        else
        {
            for (i = 0, host_client = svs.Client(0); i < svs.maxclients; i++, host_client = svs.Client(i))
            {
                if (!host_client.active)
                {
                    continue;
                }

                if (Q_strcasecmp(host_client.name, Cmd_Argv(1)) == 0)
                {
                    break;
                }
            }
        }

        if (i < svs.maxclients)
        {
            if (cmd_source == cmd_source_t.src_command)
            {
                if (cls.state == cactive_t.ca_dedicated)
                {
                    who = "Console";
                }
                else
                {
                    who = cl_name.@string;
                }
            }
            else
            {
                who = save.name;
            }

            // can't kick yourself!
            if (host_client == save)
            {
                return;
            }

            if (Cmd_Argc() > 2)
            {
                string args = Cmd_Args() ?? "";
                int pos = COM_Parse(args, 0);
                if (byNumber)
                {
                    pos++; // skip the #
                    while (Ch(args, pos) == ' ')
                    {
                        pos++; // skip white space
                    }

                    pos += Q_strlen(Cmd_Argv(2)); // skip the number
                }

                while (Ch(args, pos) != 0 && Ch(args, pos) == ' ')
                {
                    pos++;
                }

                message = pos < args.Length ? args[pos..] : "";
            }

            if (message != null)
            {
                SV_ClientPrintf("Kicked by %s: %s\n", who, message);
            }
            else
            {
                SV_ClientPrintf("Kicked by %s\n", who);
            }

            SV_DropClient(false);
        }

        host_client = save;
    }

    // ===============================================================================
    // DEBUGGING TOOLS
    // ===============================================================================

    // the field of sv_player named so, as a float, if the progs have it (GetEdictFieldValue)
    private void SetPlayerField(string field, float value, out bool found)
    {
        int val = GetEdictFieldValue(sv_player!, field);
        found = val >= 0;
        if (found)
        {
            sv.edictmem[val] = BitConverter.SingleToInt32Bits(value);
        }
    }

    // host_cmd.c: Host_Give_f
    public void Host_Give_f()
    {
        string t;
        int v;
        bool found;

        if (cmd_source == cmd_source_t.src_command)
        {
            Cmd_ForwardToServer();
            return;
        }

        if (pr_global_struct.deathmatch != 0 && !host_client!.privileged)
        {
            return;
        }

        t = Cmd_Argv(1);
        v = atoi(Cmd_Argv(2));

        switch (Ch(t, 0))
        {
            case '0':
            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
            case '8':
            case '9':
                // MED 01/04/97 added hipnotic give stuff
                if (com_argv.hipnotic)
                {
                    if (Ch(t, 0) == '6')
                    {
                        if (Ch(t, 1) == 'a')
                        {
                            sv_player!.v.items = X86.FtoI(sv_player.v.items) | HIT_PROXIMITY_GUN;
                        }
                        else
                        {
                            sv_player!.v.items = X86.FtoI(sv_player.v.items) | IT_GRENADE_LAUNCHER;
                        }
                    }
                    else if (Ch(t, 0) == '9')
                    {
                        sv_player!.v.items = X86.FtoI(sv_player.v.items) | HIT_LASER_CANNON;
                    }
                    else if (Ch(t, 0) == '0')
                    {
                        sv_player!.v.items = X86.FtoI(sv_player.v.items) | HIT_MJOLNIR;
                    }
                    else if (Ch(t, 0) >= '2')
                    {
                        sv_player!.v.items = X86.FtoI(sv_player.v.items) | (IT_SHOTGUN << (Ch(t, 0) - '2'));
                    }
                }
                else
                {
                    if (Ch(t, 0) >= '2')
                    {
                        sv_player!.v.items = X86.FtoI(sv_player.v.items) | (IT_SHOTGUN << (Ch(t, 0) - '2'));
                    }
                }

                break;

            case 's':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_shells1", v, out _);
                }

                sv_player!.v.ammo_shells = v;
                break;

            case 'n':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_nails1", v, out found);
                    if (found)
                    {
                        if (sv_player!.v.weapon <= IT_LIGHTNING)
                        {
                            sv_player.v.ammo_nails = v;
                        }
                    }
                }
                else
                {
                    sv_player!.v.ammo_nails = v;
                }

                break;

            case 'l':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_lava_nails", v, out found);
                    if (found)
                    {
                        if (sv_player!.v.weapon > IT_LIGHTNING)
                        {
                            sv_player.v.ammo_nails = v;
                        }
                    }
                }

                break;

            case 'r':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_rockets1", v, out found);
                    if (found)
                    {
                        if (sv_player!.v.weapon <= IT_LIGHTNING)
                        {
                            sv_player.v.ammo_rockets = v;
                        }
                    }
                }
                else
                {
                    sv_player!.v.ammo_rockets = v;
                }

                break;

            case 'm':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_multi_rockets", v, out found);
                    if (found)
                    {
                        if (sv_player!.v.weapon > IT_LIGHTNING)
                        {
                            sv_player.v.ammo_rockets = v;
                        }
                    }
                }

                break;

            case 'h':
                sv_player!.v.health = v;
                break;

            case 'c':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_cells1", v, out found);
                    if (found)
                    {
                        if (sv_player!.v.weapon <= IT_LIGHTNING)
                        {
                            sv_player.v.ammo_cells = v;
                        }
                    }
                }
                else
                {
                    sv_player!.v.ammo_cells = v;
                }

                break;

            case 'p':
                if (com_argv.rogue)
                {
                    SetPlayerField("ammo_plasma", v, out found);
                    if (found)
                    {
                        if (sv_player!.v.weapon > IT_LIGHTNING)
                        {
                            sv_player.v.ammo_cells = v;
                        }
                    }
                }

                break;
        }
    }

    // host_cmd.c: FindViewthing
    public Edict? FindViewthing()
    {
        int i;
        Edict e;

        for (i = 0; i < sv.num_edicts; i++)
        {
            e = EDICT_NUM(i);
            if (Q_strcmp(PR_GetString(e.v.classname), "viewthing") == 0)
            {
                return e;
            }
        }

        Con_Printf("No viewthing on map\n");
        return null;
    }

    // host_cmd.c: Host_Viewmodel_f
    public void Host_Viewmodel_f()
    {
        Edict? e;
        Model.Model? m;

        e = FindViewthing();
        if (e == null)
        {
            return;
        }

        m = Mod_ForName(Cmd_Argv(1), false);
        if (m == null)
        {
            Con_Printf("Can't load %s\n", Cmd_Argv(1));
            return;
        }

        e.v.frame = 0;
        cl.model_precache[X86.FtoI(e.v.modelindex)] = m;
    }

    // host_cmd.c: Host_Viewframe_f
    public void Host_Viewframe_f()
    {
        Edict? e;
        int f;
        Model.Model m;

        e = FindViewthing();
        if (e == null)
        {
            return;
        }

        m = cl.model_precache[X86.FtoI(e.v.modelindex)]!;

        f = atoi(Cmd_Argv(1));
        if (f >= m.numframes)
        {
            f = m.numframes - 1;
        }

        e.v.frame = f;
    }

    // host_cmd.c: PrintFrameName
    public void PrintFrameName(Model.Model m, int frame)
    {
        if (Mod_Extradata(m) is not AliasHdr hdr)
        {
            return;
        }

        Con_Printf("frame %i: %s\n", frame, hdr.frames[frame].name);
    }

    // host_cmd.c: Host_Viewnext_f
    public void Host_Viewnext_f()
    {
        Edict? e;
        Model.Model m;

        e = FindViewthing();
        if (e == null)
        {
            return;
        }

        m = cl.model_precache[X86.FtoI(e.v.modelindex)]!;

        e.v.frame = e.v.frame + 1;
        if (e.v.frame >= m.numframes)
        {
            e.v.frame = m.numframes - 1;
        }

        PrintFrameName(m, X86.FtoI(e.v.frame));
    }

    // host_cmd.c: Host_Viewprev_f
    public void Host_Viewprev_f()
    {
        Edict? e;
        Model.Model m;

        e = FindViewthing();
        if (e == null)
        {
            return;
        }

        m = cl.model_precache[X86.FtoI(e.v.modelindex)]!;

        e.v.frame = e.v.frame - 1;
        if (e.v.frame < 0)
        {
            e.v.frame = 0;
        }

        PrintFrameName(m, X86.FtoI(e.v.frame));
    }
}
