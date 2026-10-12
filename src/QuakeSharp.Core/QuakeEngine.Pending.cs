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
