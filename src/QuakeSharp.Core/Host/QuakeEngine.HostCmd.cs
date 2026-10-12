using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.UI;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// host_cmd.c: the host's commands. The client's are ported here; those of the server and the QuakeC are stand-ins
// (QuakeEngine.Pending.cs) until milestone 7.
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
}
