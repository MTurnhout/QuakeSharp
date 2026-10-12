using QuakeSharp.Core.Common;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// sv_main.c: server main program (SV_Init so far; the rest comes with milestone 7)
public sealed partial class QuakeEngine
{
    public readonly string[] localmodels = new string[MAX_MODELS];

    // sv_main.c: SV_Init
    public void SV_Init()
    {
        int i;

        Cvar_RegisterVariable(sv_maxvelocity);
        Cvar_RegisterVariable(sv_gravity);
        Cvar_RegisterVariable(sv_friction);
        Cvar_RegisterVariable(sv_edgefriction);
        Cvar_RegisterVariable(sv_stopspeed);
        Cvar_RegisterVariable(sv_maxspeed);
        Cvar_RegisterVariable(sv_accelerate);
        Cvar_RegisterVariable(sv_idealpitchscale);
        Cvar_RegisterVariable(sv_aim);
        Cvar_RegisterVariable(sv_nostep);

        for (i = 0; i < MAX_MODELS; i++)
        {
            localmodels[i] = CFormat.Sprintf("*%i", i);
        }
    }
}
