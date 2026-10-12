using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Render;
using QuakeSharp.Core.Server;

namespace QuakeSharp.Core;

// The state and functions of modules not ported yet, which the ported ones already use. Each moves into its module's
// file when that module is ported (the milestone in brackets); until then the functions do nothing.
public sealed partial class QuakeEngine
{
    /// <summary>sv_main.c: sv (milestone 7)</summary>
    public ServerState sv = new();

    /// <summary>sv_main.c: svs (milestone 7)</summary>
    public readonly ServerStatic svs = new();

    // sv_phys.c, sv_user.c, pr_cmds.c: the server's cvars, which SV_Init registers (milestone 7)
    public readonly Cvar sv_friction = new("sv_friction", "4", false, true);
    public readonly Cvar sv_stopspeed = new("sv_stopspeed", "100");
    public readonly Cvar sv_gravity = new("sv_gravity", "800", false, true);
    public readonly Cvar sv_maxvelocity = new("sv_maxvelocity", "2000");
    public readonly Cvar sv_nostep = new("sv_nostep", "0");
    public readonly Cvar sv_edgefriction = new("edgefriction", "2");
    public readonly Cvar sv_idealpitchscale = new("sv_idealpitchscale", "0.8");
    public readonly Cvar sv_maxspeed = new("sv_maxspeed", "320", false, true);
    public readonly Cvar sv_accelerate = new("sv_accelerate", "10");
    public readonly Cvar sv_aim = new("sv_aim", "0.93");

    // menu.c: the network menus (milestone 7, design.md §11.1 step 8). The multiplayer menu reaches them now that
    // TCP/IP is available; until they are ported they do nothing.
    public void M_Menu_Net_f()
    {
    }

    public void M_Net_Draw()
    {
    }

    public void M_Net_Key(int key)
    {
    }

    public void M_SerialConfig_Draw()
    {
    }

    public void M_SerialConfig_Key(int key)
    {
    }

    public void M_ModemConfig_Draw()
    {
    }

    public void M_ModemConfig_Key(int key)
    {
    }

    public void M_LanConfig_Draw()
    {
    }

    public void M_LanConfig_Key(int key)
    {
    }

    public void M_GameOptions_Draw()
    {
    }

    public void M_GameOptions_Key(int key)
    {
    }

    public void M_Search_Draw()
    {
    }

    public void M_Search_Key(int key)
    {
    }

    public void M_ServerList_Draw()
    {
    }

    public void M_ServerList_Key(int key)
    {
    }

    // pr_edict.c: pr_global_struct->deathmatch and a client's edict->v.frags, which net_dgrm.c reads (the VM, milestone
    // 7)
    public static float PR_GlobalDeathmatch() => 0;

    public static float ClientFrags(ServerClient client) => 0;

    // pr_edict.c: PR_Init (milestone 7)
    public void PR_Init()
    {
    }

    // sv_main.c: SV_BroadcastPrintf; host.c: SV_ClientPrintf, Host_ServerFrame, Host_ShutdownServer (milestone 7)
    public void SV_BroadcastPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
    }

    public void SV_ClientPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
    }

    public void Host_ServerFrame()
    {
    }

    public void Host_ShutdownServer(bool crash)
    {
    }

    // world.c: SV_RecursiveHullCheck (milestone 7)
    public bool SV_RecursiveHullCheck(in Hull hull, int num, float p1f, float p2f, in Vec3 p1, in Vec3 p2, Trace trace) => true;

    // host_cmd.c: the server's and the QuakeC's commands (milestone 7)
    public void Host_Status_f()
    {
    }

    public void Host_God_f()
    {
    }

    public void Host_Notarget_f()
    {
    }

    public void Host_Fly_f()
    {
    }

    public void Host_Map_f()
    {
    }

    public void Host_Restart_f()
    {
    }

    public void Host_Changelevel_f()
    {
    }

    public void Host_Name_f()
    {
    }

    public void Host_Noclip_f()
    {
    }

    public void Host_Say_f()
    {
    }

    public void Host_Say_Team_f()
    {
    }

    public void Host_Tell_f()
    {
    }

    public void Host_Color_f()
    {
    }

    public void Host_Kill_f()
    {
    }

    public void Host_Pause_f()
    {
    }

    public void Host_Spawn_f()
    {
    }

    public void Host_Begin_f()
    {
    }

    public void Host_PreSpawn_f()
    {
    }

    public void Host_Kick_f()
    {
    }

    public void Host_Ping_f()
    {
    }

    public void Host_Loadgame_f()
    {
    }

    public void Host_Savegame_f()
    {
    }

    public void Host_Give_f()
    {
    }

    public void Host_Viewmodel_f()
    {
    }

    public void Host_Viewframe_f()
    {
    }

    public void Host_Viewnext_f()
    {
    }

    public void Host_Viewprev_f()
    {
    }

    // zone.c: Cache_Report, Hunk_Check (managed memory: nothing to report or check)
    public void Cache_Report()
    {
    }

    public void Hunk_Check()
    {
    }

    // cd_win.c: CDAudio_Init, CDAudio_Update, CDAudio_Shutdown, CDAudio_Play, CDAudio_Pause, CDAudio_Resume
    // (milestone 8)
    public void CDAudio_Init()
    {
    }

    public void CDAudio_Update()
    {
    }

    public void CDAudio_Shutdown()
    {
    }

    public void CDAudio_Play(byte track, bool looping)
    {
    }

    public void CDAudio_Pause()
    {
    }

    public void CDAudio_Resume()
    {
    }

    // in_win.c: IN_Init, IN_Commands, IN_Shutdown, IN_Move, IN_Accumulate, IN_ClearStates (milestone 6, with the
    // mouse)
    public void IN_Init()
    {
    }

    public void IN_Commands()
    {
    }

    public void IN_Shutdown()
    {
    }

    public void IN_Move(ref UserCmd cmd)
    {
    }

    public void IN_Accumulate()
    {
    }

    public void IN_ClearStates()
    {
    }
}
