using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Server;
using QuakeSharp.Core.Video;

namespace QuakeSharp.Core;

// The state and functions of modules not ported yet, which the ported ones already use. Each moves into its module's
// file when that module is ported (the milestone in brackets); until then the functions do nothing.
public sealed partial class QuakeEngine
{
    /// <summary>cl_main.c: cls (milestone 5)</summary>
    public readonly ClientStatic cls = new();

    /// <summary>sv_main.c: sv (milestone 7)</summary>
    public readonly ServerState sv = new();

    /// <summary>sv_main.c: svs (milestone 7)</summary>
    public readonly ServerStatic svs = new();

    /// <summary>vid_win.c: vid (milestone 6)</summary>
    public readonly VidDef vid = new();

    /// <summary>screen.c: scr_disabled_for_loading (milestone 6)</summary>
    public bool scr_disabled_for_loading;

    /// <summary>net_main.c: net_message (milestone 7)</summary>
    public SizeBuf net_message = new(8192);

    // r_main.c: r_origin, vpn, vright, vup, the view (milestone 6)
    public Vec3 r_origin, vpn, vright, vup;

    // view.c: V_Init (milestone 5)
    public void V_Init()
    {
    }

    // chase.c: Chase_Init (milestone 5)
    public void Chase_Init()
    {
    }

    // host_cmd.c: Host_InitCommands (milestones 5 and 7)
    public void Host_InitCommands()
    {
    }

    // menu.c: M_Init, M_Menu_Main_f, M_ToggleMenu_f, M_Keydown (milestone 6)
    public void M_Init()
    {
    }

    public void M_Menu_Main_f()
    {
    }

    public void M_ToggleMenu_f()
    {
    }

    public void M_Keydown(int key)
    {
    }

    // pr_edict.c: PR_Init (milestone 7)
    public void PR_Init()
    {
    }

    // model.c: Mod_Init (milestone 5)
    public void Mod_Init()
    {
    }

    // net_main.c: NET_Init, NET_Poll, NET_Shutdown (milestone 7)
    public void NET_Init()
    {
    }

    public void NET_Poll()
    {
    }

    public void NET_Shutdown()
    {
    }

    // sv_main.c: SV_Init, SV_BroadcastPrintf; host.c: Host_ServerFrame, Host_ShutdownServer (milestone 7)
    public void SV_Init()
    {
    }

    public void SV_BroadcastPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
    }

    public void Host_ServerFrame()
    {
    }

    public void Host_ShutdownServer(bool crash)
    {
    }

    // r_main.c: R_InitTextures, R_Init; draw.c: Draw_Init; screen.c: SCR_Init, SCR_UpdateScreen,
    // SCR_EndLoadingPlaque; vid_win.c: VID_Init, VID_Shutdown; sbar.c: Sbar_Init (milestone 6)
    public void R_InitTextures()
    {
    }

    public void R_Init()
    {
    }

    public void Draw_Init()
    {
    }

    public void SCR_Init()
    {
    }

    public void SCR_UpdateScreen()
    {
    }

    public void SCR_EndLoadingPlaque()
    {
    }

    public void VID_Init(byte[] palette)
    {
    }

    public void VID_Shutdown()
    {
    }

    public void Sbar_Init()
    {
    }

    // cl_main.c: CL_Init, CL_SendCmd, CL_ReadFromServer, CL_DecayLights, CL_NextDemo, CL_Disconnect (milestone 5)
    public void CL_Init()
    {
    }

    public void CL_SendCmd()
    {
    }

    public void CL_ReadFromServer()
    {
    }

    public void CL_DecayLights()
    {
    }

    public void CL_NextDemo()
    {
    }

    public void CL_Disconnect()
    {
    }

    // snd_dma.c: S_Update, S_LocalSound, S_Shutdown (milestone 8)
    public void S_Update(in Vec3 origin, in Vec3 forward, in Vec3 right, in Vec3 up)
    {
    }

    public void S_LocalSound(string sound)
    {
    }

    public void S_Shutdown()
    {
    }

    // cd_win.c: CDAudio_Init, CDAudio_Update, CDAudio_Shutdown (milestone 8)
    public void CDAudio_Init()
    {
    }

    public void CDAudio_Update()
    {
    }

    public void CDAudio_Shutdown()
    {
    }

    // in_win.c: IN_Init, IN_Commands, IN_Shutdown (milestone 6, with the mouse)
    public void IN_Init()
    {
    }

    public void IN_Commands()
    {
    }

    public void IN_Shutdown()
    {
    }
}
