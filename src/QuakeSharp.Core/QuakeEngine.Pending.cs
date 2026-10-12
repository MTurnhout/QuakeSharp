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

    /// <summary>vid_win.c: vid (milestone 6)</summary>
    public readonly VidDef vid = new();

    /// <summary>screen.c: scr_disabled_for_loading (milestone 6)</summary>
    public bool scr_disabled_for_loading;

    /// <summary>net_main.c: net_message (step 4 of milestone 4)</summary>
    public SizeBuf net_message = new(8192);

    // screen.c: SCR_UpdateScreen (milestone 6)
    public void SCR_UpdateScreen()
    {
    }

    // screen.c: SCR_EndLoadingPlaque (milestone 6)
    public void SCR_EndLoadingPlaque()
    {
    }

    // snd_dma.c: S_LocalSound (milestone 8)
    public void S_LocalSound(string sound)
    {
    }

    // menu.c: M_Menu_Main_f (milestone 6)
    public void M_Menu_Main_f()
    {
    }

    // menu.c: M_ToggleMenu_f (milestone 6)
    public void M_ToggleMenu_f()
    {
    }

    // menu.c: M_Keydown (milestone 6)
    public void M_Keydown(int key)
    {
    }

    // sv_main.c: SV_BroadcastPrintf (milestone 7)
    public void SV_BroadcastPrintf(string fmt, params ReadOnlySpan<PrintfArg> args)
    {
    }
}
