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

    /// <summary>net_main.c: net_message (milestone 7)</summary>
    public SizeBuf net_message = new(8192);

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

    // menu.c: the network menus, which need net_main.c's drivers, addresses and host cache (milestone 7). Without a
    // network driver they cannot be reached.
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

    // net_main.c: serialAvailable, ipxAvailable, tcpipAvailable (no network driver yet, as the oracle's net_none.c)
    public bool serialAvailable, ipxAvailable, tcpipAvailable;

    // net_main.c: net_messagetimeout, hostname (NET_Init registers them; the setup menu reads hostname)
    public readonly Cvar net_messagetimeout = new("net_messagetimeout", "300");
    public readonly Cvar hostname = new("hostname", "UNNAMED");

    // pr_edict.c: PR_Init (milestone 7)
    public void PR_Init()
    {
    }

    // net_main.c: NET_Init, NET_Poll, NET_Shutdown, NET_Connect, NET_GetMessage, NET_SendMessage,
    // NET_SendUnreliableMessage, NET_CanSendMessage, NET_Close (milestone 7)
    public void NET_Init()
    {
        Cvar_RegisterVariable(net_messagetimeout);
        Cvar_RegisterVariable(hostname);
    }

    public void NET_Poll()
    {
    }

    public void NET_Shutdown()
    {
    }

    public object? NET_Connect(string host) => null;

    public int NET_GetMessage(object? sock) => -1;

    public int NET_SendMessage(object? sock, SizeBuf data) => 1;

    public int NET_SendUnreliableMessage(object? sock, SizeBuf data) => 1;

    public bool NET_CanSendMessage(object? sock) => true;

    public void NET_Close(object? sock)
    {
    }

    // sv_main.c: SV_BroadcastPrintf; host.c: Host_ServerFrame, Host_ShutdownServer (milestone 7)
    public void SV_BroadcastPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
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

    // The renderer (milestone 6). r_*.c and d_*.c: what R_RenderView, R_SetupFrame and R_ViewChanged call, the
    // efrags, the sky; and the state the ported code reads.
    public Vec3 modelorg, base_modelorg;
    public bool r_cache_thrash;
    public int c_faceclip;
    public int r_amodels_drawn;
    public short[]? d_pzbuffer;

    public void R_InitTurb()
    {
    }

    public void R_InitSky(Texture mt)
    {
    }

    public void R_MarkLeaves()
    {
    }

    public void R_EdgeDrawing()
    {
    }

    public void R_DrawEntitiesOnList()
    {
    }

    public void R_DrawViewModel()
    {
    }

    public void R_TimeGraph()
    {
    }

    public void R_PrintAliasStats()
    {
    }

    public void R_PrintTimes()
    {
    }

    public void R_PrintDSpeeds()
    {
    }

    // R_SetupFrame's r_numsurfs and r_numedges reports, which count the renderer's surface and edge lists
    public void R_PrintSurfaceCounts()
    {
    }

    public void R_PrintEdgeCounts()
    {
    }

    public void R_TransformFrustum()
    {
    }

    public void R_SetSkyFrame()
    {
    }

    public void R_SetUpFrustumIndexes()
    {
    }

    public void R_AddEfrags(Entity ent)
    {
    }

    public void R_RemoveEfrags(Entity ent)
    {
    }

    public void D_Init()
    {
    }

    public void D_ViewChanged()
    {
    }

    public void D_SetupFrame()
    {
    }

    public void D_WarpScreen()
    {
    }

    public void D_FlushCaches()
    {
    }

    public void D_StartParticles()
    {
    }

    public void D_DrawParticle(Particle pparticle)
    {
    }

    public void D_EndParticles()
    {
    }

    public void D_EnableBackBufferAccess()
    {
    }

    public void D_DisableBackBufferAccess()
    {
    }

    // d_surf.c: D_SurfaceCacheForRes; D_InitCaches (its message is ported; the cache comes with the renderer)
    public int D_SurfaceCacheForRes(int width, int height)
    {
        int size, pix;

        if (COM_CheckParm("-surfcachesize") != 0)
        {
            size = QString.Q_atoi(com_argv.Argv(COM_CheckParm("-surfcachesize") + 1)) * 1024;
            return size;
        }

        size = 600 * 1024; // SURFCACHE_SIZE_AT_320X200

        pix = width * height;
        if (pix > 64000)
        {
            size += (pix - 64000) * 3;
        }

        return size;
    }

    public void D_InitCaches(int size)
    {
        if (!msg_suppress_1)
        {
            Con_Printf("%ik surface cache\n", size / 1024);
        }
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
