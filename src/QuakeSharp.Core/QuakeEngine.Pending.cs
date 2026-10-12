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
