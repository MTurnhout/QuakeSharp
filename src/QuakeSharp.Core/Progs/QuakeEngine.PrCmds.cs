using System.Text;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Progs;
using QuakeSharp.Core.Server;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Model.BspFile;
using static QuakeSharp.Core.Progs.PrComp;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;

namespace QuakeSharp.Core;

// pr_cmds.c: the builtins, the engine's functions QuakeC calls
public sealed partial class QuakeEngine
{
    /// <summary>pr_cmds.c: pr_string_temp (char[128]), where ftos and vtos write</summary>
    public string pr_string_temp = "";

    // pr_cmds.c: RETURN_EDICT
    private void RETURN_EDICT(Edict e) => pr_globals[OFS_RETURN] = EDICT_TO_PROG(e);

    // ===============================================================================
    // BUILT-IN FUNCTIONS
    // ===============================================================================

    // pr_cmds.c: PF_VarString (its static char out[256] is not limited here: a longer string overruns it in C)
    public string PF_VarString(int first)
    {
        var @out = new StringBuilder();
        for (int i = first; i < pr_argc; i++)
        {
            @out.Append(G_STRING(OFS_PARM0 + (i * 3)));
        }

        return @out.ToString();
    }

    // pr_cmds.c: PF_error
    // This is a TERMINAL error, which will kill off the entire server. Dumps self.
    // error(value)
    public void PF_error()
    {
        string s;
        Edict ed;

        s = PF_VarString(0);
        Con_Printf("======SERVER ERROR in %s:\n%s\n", PR_GetString(pr_xfunction!.s_name), s);
        ed = PROG_TO_EDICT(pr_global_struct.self);
        ED_Print(ed);

        throw Host_Error("Program error");
    }

    // pr_cmds.c: PF_objerror
    // Dumps out self, then an error message. The program is aborted and self is removed, but the level can continue.
    // objerror(value)
    public void PF_objerror()
    {
        string s;
        Edict ed;

        s = PF_VarString(0);
        Con_Printf("======OBJECT ERROR in %s:\n%s\n", PR_GetString(pr_xfunction!.s_name), s);
        ed = PROG_TO_EDICT(pr_global_struct.self);
        ED_Print(ed);
        ED_Free(ed);

        throw Host_Error("Program error");
    }

    // pr_cmds.c: PF_makevectors
    // Writes new values for v_forward, v_up, and v_right based on angles
    // makevectors(vector)
    public void PF_makevectors() =>
        AngleVectors(G_VECTOR(OFS_PARM0), ref pr_global_struct.v_forward, ref pr_global_struct.v_right, ref pr_global_struct.v_up);

    // pr_cmds.c: PF_setorigin
    // This is the only valid way to move an object without using the physics of the world (setting velocity and
    // waiting). Directly changing origin will not set internal links correctly, so clipping would be messed up. This
    // should be called when an object is spawned, and then only if it is teleported.
    // setorigin (entity, origin)
    public void PF_setorigin()
    {
        Edict e;

        e = G_EDICT(OFS_PARM0);
        ref Vec3 org = ref G_VECTOR(OFS_PARM1);
        VectorCopy(org, ref e.v.origin);
        SV_LinkEdict(e, false);
    }

    // pr_cmds.c: SetMinMaxSize (rotate is always set false: "FIXME: implement rotation properly again")
    public void SetMinMaxSize(Edict e, in Vec3 min, in Vec3 max, bool rotate)
    {
        Vec3 rmin = default, rmax = default;
        int i;

        for (i = 0; i < 3; i++)
        {
            if (min[i] > max[i])
            {
                throw PR_RunError("backwards mins/maxs");
            }
        }

        VectorCopy(min, ref rmin);
        VectorCopy(max, ref rmax);

        // set derived values
        VectorCopy(rmin, ref e.v.mins);
        VectorCopy(rmax, ref e.v.maxs);
        VectorSubtract(max, min, ref e.v.size);

        SV_LinkEdict(e, false);
    }

    // pr_cmds.c: PF_setsize
    // the size box is rotated by the current angle
    // setsize (entity, minvector, maxvector)
    public void PF_setsize()
    {
        Edict e;

        e = G_EDICT(OFS_PARM0);
        SetMinMaxSize(e, G_VECTOR(OFS_PARM1), G_VECTOR(OFS_PARM2), false);
    }

    // pr_cmds.c: PF_setmodel
    // setmodel(entity, model)
    public void PF_setmodel()
    {
        Edict e;
        string m;
        Model.Model? mod;
        int i;

        e = G_EDICT(OFS_PARM0);
        m = G_STRING(OFS_PARM1);

        // check to see if model was properly precached
        for (i = 0; sv.model_precache[i] != null; i++)
        {
            if (Q_strcmp(sv.model_precache[i]!, m) == 0)
            {
                break;
            }
        }

        if (sv.model_precache[i] == null)
        {
            throw PR_RunError("no precache: %s\n", m);
        }

        e.v.model = G_INT(OFS_PARM1);
        e.v.modelindex = i; // SV_ModelIndex (m);

        mod = sv.models[X86.FtoI(e.v.modelindex)]; // Mod_ForName (m, true);

        if (mod != null)
        {
            SetMinMaxSize(e, mod.mins, mod.maxs, true);
        }
        else
        {
            SetMinMaxSize(e, vec3_origin, vec3_origin, true);
        }
    }

    // pr_cmds.c: PF_bprint
    // broadcast print to everyone on server
    // bprint(value)
    public void PF_bprint()
    {
        string s;

        s = PF_VarString(0);
        SV_BroadcastPrintf("%s", s);
    }

    // pr_cmds.c: PF_sprint
    // single print to a specific client
    // sprint(clientent, value)
    public void PF_sprint()
    {
        string s;
        ServerClient client;
        int entnum;

        entnum = G_EDICTNUM(OFS_PARM0);
        s = PF_VarString(1);

        if (entnum < 1 || entnum > svs.maxclients)
        {
            Con_Printf("tried to sprint to a non-client\n");
            return;
        }

        client = svs.clients[entnum - 1];

        MSG_WriteChar(client.message, svc_print);
        MSG_WriteString(client.message, s);
    }

    // pr_cmds.c: PF_centerprint
    // single print to a specific client
    // centerprint(clientent, value)
    public void PF_centerprint()
    {
        string s;
        ServerClient client;
        int entnum;

        entnum = G_EDICTNUM(OFS_PARM0);
        s = PF_VarString(1);

        if (entnum < 1 || entnum > svs.maxclients)
        {
            Con_Printf("tried to sprint to a non-client\n");
            return;
        }

        client = svs.clients[entnum - 1];

        MSG_WriteChar(client.message, svc_centerprint);
        MSG_WriteString(client.message, s);
    }

    // pr_cmds.c: PF_normalize
    // vector normalize(vector)
    public void PF_normalize()
    {
        Vec3 newvalue = default;
        float @new;

        ref Vec3 value1 = ref G_VECTOR(OFS_PARM0);

        @new = (value1[0] * value1[0]) + (value1[1] * value1[1]) + (value1[2] * value1[2]);
        @new = (float)Math.Sqrt(@new);

        if (@new == 0)
        {
            newvalue[0] = newvalue[1] = newvalue[2] = 0;
        }
        else
        {
            @new = 1 / @new;
            newvalue[0] = value1[0] * @new;
            newvalue[1] = value1[1] * @new;
            newvalue[2] = value1[2] * @new;
        }

        VectorCopy(newvalue, ref G_VECTOR(OFS_RETURN));
    }

    // pr_cmds.c: PF_vlen
    // scalar vlen(vector)
    public void PF_vlen()
    {
        float @new;

        ref Vec3 value1 = ref G_VECTOR(OFS_PARM0);

        @new = (value1[0] * value1[0]) + (value1[1] * value1[1]) + (value1[2] * value1[2]);
        @new = (float)Math.Sqrt(@new);

        G_FLOAT(OFS_RETURN) = @new;
    }

    // pr_cmds.c: PF_vectoyaw
    // float vectoyaw(vector)
    public void PF_vectoyaw()
    {
        float yaw;

        ref Vec3 value1 = ref G_VECTOR(OFS_PARM0);

        if (value1[1] == 0 && value1[0] == 0)
        {
            yaw = 0;
        }
        else
        {
            yaw = X86.FtoI(Libm.atan2(value1[1], value1[0]) * 180 / M_PI);
            if (yaw < 0)
            {
                yaw += 360;
            }
        }

        G_FLOAT(OFS_RETURN) = yaw;
    }

    // pr_cmds.c: PF_vectoangles
    // vector vectoangles(vector)
    public void PF_vectoangles()
    {
        float forward;
        float yaw, pitch;

        ref Vec3 value1 = ref G_VECTOR(OFS_PARM0);

        if (value1[1] == 0 && value1[0] == 0)
        {
            yaw = 0;
            if (value1[2] > 0)
            {
                pitch = 90;
            }
            else
            {
                pitch = 270;
            }
        }
        else
        {
            yaw = X86.FtoI(Libm.atan2(value1[1], value1[0]) * 180 / M_PI);
            if (yaw < 0)
            {
                yaw += 360;
            }

            forward = (float)Math.Sqrt((value1[0] * value1[0]) + (value1[1] * value1[1]));
            pitch = X86.FtoI(Libm.atan2(value1[2], forward) * 180 / M_PI);
            if (pitch < 0)
            {
                pitch += 360;
            }
        }

        G_FLOAT(OFS_RETURN + 0) = pitch;
        G_FLOAT(OFS_RETURN + 1) = yaw;
        G_FLOAT(OFS_RETURN + 2) = 0;
    }

    // pr_cmds.c: PF_random
    // Returns a number from 0<= num < 1
    // random()
    public void PF_random()
    {
        float num;

        num = (rand() & 0x7fff) / (float)0x7fff;

        G_FLOAT(OFS_RETURN) = num;
    }

    // pr_cmds.c: PF_particle
    // particle(origin, color, count)
    public void PF_particle()
    {
        float color;
        float count;

        ref Vec3 org = ref G_VECTOR(OFS_PARM0);
        ref Vec3 dir = ref G_VECTOR(OFS_PARM1);
        color = G_FLOAT(OFS_PARM2);
        count = G_FLOAT(OFS_PARM3);
        SV_StartParticle(org, dir, X86.FtoI(color), X86.FtoI(count));
    }

    // pr_cmds.c: PF_ambientsound
    public void PF_ambientsound()
    {
        string samp;
        float vol, attenuation;
        int i, soundnum;

        ref Vec3 pos = ref G_VECTOR(OFS_PARM0);
        samp = G_STRING(OFS_PARM1);
        vol = G_FLOAT(OFS_PARM2);
        attenuation = G_FLOAT(OFS_PARM3);

        // check to see if samp was properly precached
        for (soundnum = 0; sv.sound_precache[soundnum] != null; soundnum++)
        {
            if (Q_strcmp(sv.sound_precache[soundnum]!, samp) == 0)
            {
                break;
            }
        }

        if (sv.sound_precache[soundnum] == null)
        {
            Con_Printf("no precache: %s\n", samp);
            return;
        }

        // add an svc_spawnambient command to the level signon packet
        MSG_WriteByte(sv.signon, svc_spawnstaticsound);
        for (i = 0; i < 3; i++)
        {
            MSG_WriteCoord(sv.signon, pos[i]);
        }

        MSG_WriteByte(sv.signon, soundnum);

        MSG_WriteByte(sv.signon, X86.FtoI(vol * 255));
        MSG_WriteByte(sv.signon, X86.FtoI(attenuation * 64));
    }

    // pr_cmds.c: PF_sound
    // Each entity can have eight independant sound sources, like voice, weapon, feet, etc.
    //
    // Channel 0 is an auto-allocate channel, the others override anything allready running on that entity/channel
    // pair.
    //
    // An attenuation of 0 will play full volume everywhere in the level. Larger attenuations will drop off.
    public void PF_sound()
    {
        string sample;
        int channel;
        Edict entity;
        int volume;
        float attenuation;

        entity = G_EDICT(OFS_PARM0);
        channel = X86.FtoI(G_FLOAT(OFS_PARM1));
        sample = G_STRING(OFS_PARM2);
        volume = X86.FtoI(G_FLOAT(OFS_PARM3) * 255);
        attenuation = G_FLOAT(OFS_PARM4);

        if (volume < 0 || volume > 255)
        {
            throw Sys_Error("SV_StartSound: volume = %i", volume);
        }

        if (attenuation < 0 || attenuation > 4)
        {
            throw Sys_Error("SV_StartSound: attenuation = %f", attenuation);
        }

        if (channel < 0 || channel > 7)
        {
            throw Sys_Error("SV_StartSound: channel = %i", channel);
        }

        SV_StartSound(entity, channel, sample, volume, attenuation);
    }

    // pr_cmds.c: PF_break: writes to address -4 to drop into the debugger; the program dies
    public void PF_break()
    {
        Con_Printf("break statement\n");
        throw Sys_Error("break statement: *(int *)-4 = 0"); // dump to debugger
    }

    // pr_cmds.c: PF_traceline
    // Used for use tracing and shot targeting. Traces are blocked by bbox and exact bsp entityes, and also slide box
    // entities if the tryents flag is set.
    // traceline (vector1, vector2, tryents)
    public void PF_traceline()
    {
        Trace trace;
        int nomonsters;
        Edict ent;

        ref Vec3 v1 = ref G_VECTOR(OFS_PARM0);
        ref Vec3 v2 = ref G_VECTOR(OFS_PARM1);
        nomonsters = X86.FtoI(G_FLOAT(OFS_PARM2));
        ent = G_EDICT(OFS_PARM3);

        trace = SV_Move(v1, vec3_origin, vec3_origin, v2, nomonsters, ent);

        pr_global_struct.trace_allsolid = trace.allsolid ? 1 : 0;
        pr_global_struct.trace_startsolid = trace.startsolid ? 1 : 0;
        pr_global_struct.trace_fraction = trace.fraction;
        pr_global_struct.trace_inwater = trace.inwater ? 1 : 0;
        pr_global_struct.trace_inopen = trace.inopen ? 1 : 0;
        VectorCopy(trace.endpos, ref pr_global_struct.trace_endpos);
        VectorCopy(trace.plane.normal, ref pr_global_struct.trace_plane_normal);
        pr_global_struct.trace_plane_dist = trace.plane.dist;
        if (trace.ent != null)
        {
            pr_global_struct.trace_ent = EDICT_TO_PROG(trace.ent);
        }
        else
        {
            pr_global_struct.trace_ent = EDICT_TO_PROG(sv.edicts[0]);
        }
    }

    // pr_cmds.c: PF_checkpos
    // Returns true if the given entity can move to the given position from it's current position by walking or
    // rolling.
    // FIXME: make work...
    // scalar checkpos (entity, vector)
    public void PF_checkpos()
    {
    }

    // ============================================================================

    /// <summary>pr_cmds.c: checkpvs</summary>
    private readonly byte[] checkpvs = new byte[MAX_MAP_LEAFS / 8];

    // pr_cmds.c: PF_newcheckclient
    public int PF_newcheckclient(int check)
    {
        int i;
        byte[] pvs;
        Edict ent;
        MLeaf leaf;
        Vec3 org = default;

        // cycle to the next one
        if (check < 1)
        {
            check = 1;
        }

        if (check > svs.maxclients)
        {
            check = svs.maxclients;
        }

        if (check == svs.maxclients)
        {
            i = 1;
        }
        else
        {
            i = check + 1;
        }

        for (; ; i++)
        {
            if (i == svs.maxclients + 1)
            {
                i = 1;
            }

            ent = EDICT_NUM(i);

            if (i == check)
            {
                break; // didn't find anything else
            }

            if (ent.free)
            {
                continue;
            }

            if (ent.v.health <= 0)
            {
                continue;
            }

            if ((X86.FtoI(ent.v.flags) & FL_NOTARGET) != 0)
            {
                continue;
            }

            // anything that is a client, or has a client as an enemy
            break;
        }

        // get the PVS for the entity
        VectorAdd(ent.v.origin, ent.v.view_ofs, ref org);
        leaf = Mod_PointInLeaf(org, sv.worldmodel!);
        pvs = Mod_LeafPVS(leaf, sv.worldmodel!);
        Array.Copy(pvs, checkpvs, (sv.worldmodel!.numleafs + 7) >> 3);

        return i;
    }

    private int c_invis, c_notvis;

    // pr_cmds.c: PF_checkclient
    // Returns a client (or object that has a client enemy) that would be a valid target.
    //
    // If there are more than one valid options, they are cycled each frame
    //
    // If (self.origin + self.viewofs) is not in the PVS of the current target, it is not returned at all.
    //
    // name checkclient ()
    public void PF_checkclient()
    {
        Edict ent, self;
        MLeaf leaf;
        int l;
        Vec3 view = default;

        // find a new check if on a new frame
        if (sv.time - sv.lastchecktime >= 0.1)
        {
            sv.lastcheck = PF_newcheckclient(sv.lastcheck);
            sv.lastchecktime = sv.time;
        }

        // return check if it might be visible
        ent = EDICT_NUM(sv.lastcheck);
        if (ent.free || ent.v.health <= 0)
        {
            RETURN_EDICT(sv.edicts[0]);
            return;
        }

        // if current entity can't possibly see the check entity, return 0
        self = PROG_TO_EDICT(pr_global_struct.self);
        VectorAdd(self.v.origin, self.v.view_ofs, ref view);
        leaf = Mod_PointInLeaf(view, sv.worldmodel!);
        l = leaf.index - 1;
        if (l < 0 || (checkpvs[l >> 3] & (1 << (l & 7))) == 0)
        {
            c_notvis++;
            RETURN_EDICT(sv.edicts[0]);
            return;
        }

        // might be able to see it
        c_invis++;
        RETURN_EDICT(ent);
    }

    // ============================================================================

    // pr_cmds.c: PF_stuffcmd
    // Sends text over to the client's execution buffer
    // stuffcmd (clientent, value)
    public void PF_stuffcmd()
    {
        int entnum;
        string str;
        ServerClient? old;

        entnum = G_EDICTNUM(OFS_PARM0);
        if (entnum < 1 || entnum > svs.maxclients)
        {
            throw PR_RunError("Parm 0 not a client");
        }

        str = G_STRING(OFS_PARM1);

        old = host_client;
        host_client = svs.clients[entnum - 1];
        Host_ClientCommands("%s", str);
        host_client = old;
    }

    // pr_cmds.c: PF_localcmd
    // Sends text over to the client's execution buffer
    // localcmd (string)
    public void PF_localcmd()
    {
        string str;

        str = G_STRING(OFS_PARM0);
        Cbuf_AddText(str);
    }

    // pr_cmds.c: PF_cvar
    // float cvar (string)
    public void PF_cvar()
    {
        string str;

        str = G_STRING(OFS_PARM0);

        G_FLOAT(OFS_RETURN) = Cvar_VariableValue(str);
    }

    // pr_cmds.c: PF_cvar_set
    // float cvar (string)
    public void PF_cvar_set()
    {
        string var, val;

        var = G_STRING(OFS_PARM0);
        val = G_STRING(OFS_PARM1);

        Cvar_Set(var, val);
    }

    // pr_cmds.c: PF_findradius
    // Returns a chain of entities that have origins within a spherical area
    // findradius (origin, radius)
    public void PF_findradius()
    {
        Edict ent, chain;
        float rad;
        Vec3 eorg = default;
        int i, j;

        chain = sv.edicts[0];

        ref Vec3 org = ref G_VECTOR(OFS_PARM0);
        rad = G_FLOAT(OFS_PARM1);

        ent = NEXT_EDICT(sv.edicts[0]);
        for (i = 1; i < sv.num_edicts; i++, ent = NEXT_EDICT(ent))
        {
            if (ent.free)
            {
                continue;
            }

            if (ent.v.solid == SOLID_NOT)
            {
                continue;
            }

            for (j = 0; j < 3; j++)
            {
                eorg[j] = (float)(org[j] - (ent.v.origin[j] + ((ent.v.mins[j] + ent.v.maxs[j]) * 0.5)));
            }

            if (Length(eorg) > rad)
            {
                continue;
            }

            ent.v.chain = EDICT_TO_PROG(chain);
            chain = ent;
        }

        RETURN_EDICT(chain);
    }

    // pr_cmds.c: PF_dprint
    public void PF_dprint() => Con_DPrintf("%s", PF_VarString(0));

    // pr_cmds.c: PF_ftos
    public void PF_ftos()
    {
        float v;
        v = G_FLOAT(OFS_PARM0);

        if (v == X86.FtoI(v))
        {
            pr_string_temp = CFormat.Sprintf("%d", X86.FtoI(v));
        }
        else
        {
            pr_string_temp = CFormat.Sprintf("%5.1f", v);
        }

        G_INT(OFS_RETURN) = PR_STRING_TEMP;
    }

    // pr_cmds.c: PF_fabs
    public void PF_fabs()
    {
        float v;
        v = G_FLOAT(OFS_PARM0);
        G_FLOAT(OFS_RETURN) = Math.Abs(v);
    }

    // pr_cmds.c: PF_vtos
    public void PF_vtos()
    {
        pr_string_temp = CFormat.Sprintf("'%5.1f %5.1f %5.1f'", G_VECTOR(OFS_PARM0)[0], G_VECTOR(OFS_PARM0)[1], G_VECTOR(OFS_PARM0)[2]);
        G_INT(OFS_RETURN) = PR_STRING_TEMP;
    }

    // pr_cmds.c: PF_Spawn
    public void PF_Spawn()
    {
        Edict ed;
        ed = ED_Alloc();
        RETURN_EDICT(ed);
    }

    // pr_cmds.c: PF_Remove
    public void PF_Remove()
    {
        Edict ed;

        ed = G_EDICT(OFS_PARM0);
        ED_Free(ed);
    }

    // pr_cmds.c: PF_Find
    // entity (entity start, .string field, string match) find = #5;
    public void PF_Find()
    {
        int e;
        int f;
        string s, t;
        Edict ed;

        e = G_EDICTNUM(OFS_PARM0);
        f = G_INT(OFS_PARM1);
        s = G_STRING(OFS_PARM2);

        for (e++; e < sv.num_edicts; e++)
        {
            ed = EDICT_NUM(e);
            if (ed.free)
            {
                continue;
            }

            t = E_STRING(ed, f);
            if (Q_strcmp(t, s) == 0)
            {
                RETURN_EDICT(ed);
                return;
            }
        }

        RETURN_EDICT(sv.edicts[0]);
    }

    // pr_cmds.c: PR_CheckEmptyString
    public void PR_CheckEmptyString(string s)
    {
        if (Ch(s, 0) <= ' ')
        {
            throw PR_RunError("Bad string");
        }
    }

    // pr_cmds.c: PF_precache_file
    // precache_file is only used to copy files with qcc, it does nothing
    public void PF_precache_file() => G_INT(OFS_RETURN) = G_INT(OFS_PARM0);

    // pr_cmds.c: PF_precache_sound
    public void PF_precache_sound()
    {
        string s;
        int i;

        if (sv.state != server_state_t.ss_loading)
        {
            throw PR_RunError("PF_Precache_*: Precache can only be done in spawn functions");
        }

        s = G_STRING(OFS_PARM0);
        G_INT(OFS_RETURN) = G_INT(OFS_PARM0);
        PR_CheckEmptyString(s);

        for (i = 0; i < MAX_SOUNDS; i++)
        {
            if (sv.sound_precache[i] == null)
            {
                sv.sound_precache[i] = s;
                return;
            }

            if (Q_strcmp(sv.sound_precache[i]!, s) == 0)
            {
                return;
            }
        }

        throw PR_RunError("PF_precache_sound: overflow");
    }

    // pr_cmds.c: PF_precache_model
    public void PF_precache_model()
    {
        string s;
        int i;

        if (sv.state != server_state_t.ss_loading)
        {
            throw PR_RunError("PF_Precache_*: Precache can only be done in spawn functions");
        }

        s = G_STRING(OFS_PARM0);
        G_INT(OFS_RETURN) = G_INT(OFS_PARM0);
        PR_CheckEmptyString(s);

        for (i = 0; i < MAX_MODELS; i++)
        {
            if (sv.model_precache[i] == null)
            {
                sv.model_precache[i] = s;
                sv.models[i] = Mod_ForName(s, true);
                return;
            }

            if (Q_strcmp(sv.model_precache[i]!, s) == 0)
            {
                return;
            }
        }

        throw PR_RunError("PF_precache_model: overflow");
    }

    // pr_cmds.c: PF_coredump
    public void PF_coredump() => ED_PrintEdicts();

    // pr_cmds.c: PF_traceon
    public void PF_traceon() => pr_trace = true;

    // pr_cmds.c: PF_traceoff
    public void PF_traceoff() => pr_trace = false;

    // pr_cmds.c: PF_eprint
    public void PF_eprint() => ED_PrintNum(G_EDICTNUM(OFS_PARM0));

    // pr_cmds.c: PF_walkmove
    // float(float yaw, float dist) walkmove
    public void PF_walkmove()
    {
        Edict ent;
        float yaw, dist;
        Vec3 move = default;
        dfunction_t? oldf;
        int oldself;

        ent = PROG_TO_EDICT(pr_global_struct.self);
        yaw = G_FLOAT(OFS_PARM0);
        dist = G_FLOAT(OFS_PARM1);

        if ((X86.FtoI(ent.v.flags) & (FL_ONGROUND | FL_FLY | FL_SWIM)) == 0)
        {
            G_FLOAT(OFS_RETURN) = 0;
            return;
        }

        yaw = (float)(yaw * M_PI * 2 / 360);

        move[0] = (float)(Libm.cos(yaw) * dist);
        move[1] = (float)(Libm.sin(yaw) * dist);
        move[2] = 0;

        // save program state, because SV_movestep may call other progs
        oldf = pr_xfunction;
        oldself = pr_global_struct.self;

        G_FLOAT(OFS_RETURN) = SV_movestep(ent, move, true) ? 1 : 0;

        // restore program state
        pr_xfunction = oldf;
        pr_global_struct.self = oldself;
    }

    // pr_cmds.c: PF_droptofloor
    // void() droptofloor
    public void PF_droptofloor()
    {
        Edict ent;
        Vec3 end = default;
        Trace trace;

        ent = PROG_TO_EDICT(pr_global_struct.self);

        VectorCopy(ent.v.origin, ref end);
        end[2] -= 256;

        trace = SV_Move(ent.v.origin, ent.v.mins, ent.v.maxs, end, 0, ent);

        if (trace.fraction == 1 || trace.allsolid)
        {
            G_FLOAT(OFS_RETURN) = 0;
        }
        else
        {
            VectorCopy(trace.endpos, ref ent.v.origin);
            SV_LinkEdict(ent, false);
            ent.v.flags = X86.FtoI(ent.v.flags) | FL_ONGROUND;
            ent.v.groundentity = EDICT_TO_PROG(trace.ent!);
            G_FLOAT(OFS_RETURN) = 1;
        }
    }

    // pr_cmds.c: PF_lightstyle
    // void(float style, string value) lightstyle
    public void PF_lightstyle()
    {
        int style;
        string val;
        ServerClient client;
        int j;

        style = X86.FtoI(G_FLOAT(OFS_PARM0));
        val = G_STRING(OFS_PARM1);

        // change the string in sv
        sv.lightstyles[style] = val;

        // send message to all clients on this server
        if (sv.state != server_state_t.ss_active)
        {
            return;
        }

        for (j = 0; j < svs.maxclients; j++)
        {
            client = svs.clients[j];
            if (client.active || client.spawned)
            {
                MSG_WriteChar(client.message, svc_lightstyle);
                MSG_WriteChar(client.message, style);
                MSG_WriteString(client.message, val);
            }
        }
    }

    // pr_cmds.c: PF_rint
    public void PF_rint()
    {
        float f;
        f = G_FLOAT(OFS_PARM0);
        if (f > 0)
        {
            G_FLOAT(OFS_RETURN) = X86.FtoI(f + 0.5);
        }
        else
        {
            G_FLOAT(OFS_RETURN) = X86.FtoI(f - 0.5);
        }
    }

    // pr_cmds.c: PF_floor
    public void PF_floor() => G_FLOAT(OFS_RETURN) = (float)Math.Floor(G_FLOAT(OFS_PARM0));

    // pr_cmds.c: PF_ceil
    public void PF_ceil() => G_FLOAT(OFS_RETURN) = (float)Math.Ceiling(G_FLOAT(OFS_PARM0));

    // pr_cmds.c: PF_checkbottom
    public void PF_checkbottom()
    {
        Edict ent;

        ent = G_EDICT(OFS_PARM0);

        G_FLOAT(OFS_RETURN) = SV_CheckBottom(ent) ? 1 : 0;
    }

    // pr_cmds.c: PF_pointcontents
    public void PF_pointcontents()
    {
        ref Vec3 v = ref G_VECTOR(OFS_PARM0);

        G_FLOAT(OFS_RETURN) = SV_PointContents(v);
    }

    // pr_cmds.c: PF_nextent
    // entity nextent(entity)
    public void PF_nextent()
    {
        int i;
        Edict ent;

        i = G_EDICTNUM(OFS_PARM0);
        while (true)
        {
            i++;
            if (i == sv.num_edicts)
            {
                RETURN_EDICT(sv.edicts[0]);
                return;
            }

            ent = EDICT_NUM(i);
            if (!ent.free)
            {
                RETURN_EDICT(ent);
                return;
            }
        }
    }

    // pr_cmds.c: PF_aim
    // Pick a vector for the player to shoot along
    // vector aim(entity, missilespeed)
    public void PF_aim()
    {
        Edict ent, check;
        Edict? bestent;
        Vec3 start = default, dir = default, end = default, bestdir = default;
        int i, j;
        Trace tr;
        float dist, bestdist;

        ent = G_EDICT(OFS_PARM0);
        _ = G_FLOAT(OFS_PARM1); // speed

        VectorCopy(ent.v.origin, ref start);
        start[2] += 20;

        // try sending a trace straight
        VectorCopy(pr_global_struct.v_forward, ref dir);
        VectorMA(start, 2048, dir, ref end);
        tr = SV_Move(start, vec3_origin, vec3_origin, end, 0, ent);
        if (tr.ent != null && tr.ent.v.takedamage == DAMAGE_AIM
            && (teamplay.value == 0 || ent.v.team <= 0 || ent.v.team != tr.ent.v.team))
        {
            VectorCopy(pr_global_struct.v_forward, ref G_VECTOR(OFS_RETURN));
            return;
        }

        // try all possible entities
        VectorCopy(dir, ref bestdir);
        bestdist = sv_aim.value;
        bestent = null;

        check = NEXT_EDICT(sv.edicts[0]);
        for (i = 1; i < sv.num_edicts; i++, check = NEXT_EDICT(check))
        {
            if (check.v.takedamage != DAMAGE_AIM)
            {
                continue;
            }

            if (check == ent)
            {
                continue;
            }

            if (teamplay.value != 0 && ent.v.team > 0 && ent.v.team == check.v.team)
            {
                continue; // don't aim at teammate
            }

            for (j = 0; j < 3; j++)
            {
                end[j] = (float)(check.v.origin[j] + (0.5 * (check.v.mins[j] + check.v.maxs[j])));
            }

            VectorSubtract(end, start, ref dir);
            VectorNormalize(ref dir);
            dist = DotProduct(dir, pr_global_struct.v_forward);
            if (dist < bestdist)
            {
                continue; // to far to turn
            }

            tr = SV_Move(start, vec3_origin, vec3_origin, end, 0, ent);
            if (tr.ent == check)
            {
                // can shoot at this one
                bestdist = dist;
                bestent = check;
            }
        }

        if (bestent != null)
        {
            VectorSubtract(bestent.v.origin, ent.v.origin, ref dir);
            dist = DotProduct(dir, pr_global_struct.v_forward);
            VectorScale(pr_global_struct.v_forward, dist, ref end);
            end[2] = dir[2];
            VectorNormalize(ref end);
            VectorCopy(end, ref G_VECTOR(OFS_RETURN));
        }
        else
        {
            VectorCopy(bestdir, ref G_VECTOR(OFS_RETURN));
        }
    }

    // pr_cmds.c: PF_changeyaw
    // This was a major timewaster in progs, so it was converted to C
    public void PF_changeyaw()
    {
        Edict ent;
        float ideal, current, move, speed;

        ent = PROG_TO_EDICT(pr_global_struct.self);
        current = anglemod(ent.v.angles[1]);
        ideal = ent.v.ideal_yaw;
        speed = ent.v.yaw_speed;

        if (current == ideal)
        {
            return;
        }

        move = ideal - current;
        if (ideal > current)
        {
            if (move >= 180)
            {
                move = move - 360;
            }
        }
        else
        {
            if (move <= -180)
            {
                move = move + 360;
            }
        }

        if (move > 0)
        {
            if (move > speed)
            {
                move = speed;
            }
        }
        else
        {
            if (move < -speed)
            {
                move = -speed;
            }
        }

        ent.v.angles[1] = anglemod(current + move);
    }

    // ===============================================================================
    // MESSAGE WRITING
    // ===============================================================================

    private const int MSG_BROADCAST = 0; // unreliable to all
    private const int MSG_ONE = 1; // reliable to one (msg_entity)
    private const int MSG_ALL = 2; // reliable to all
    private const int MSG_INIT = 3; // write to the init string

    // pr_cmds.c: WriteDest
    public SizeBuf WriteDest()
    {
        int entnum;
        int dest;
        Edict ent;

        dest = X86.FtoI(G_FLOAT(OFS_PARM0));
        switch (dest)
        {
            case MSG_BROADCAST:
                return sv.datagram;

            case MSG_ONE:
                ent = PROG_TO_EDICT(pr_global_struct.msg_entity);
                entnum = NUM_FOR_EDICT(ent);
                if (entnum < 1 || entnum > svs.maxclients)
                {
                    throw PR_RunError("WriteDest: not a client");
                }

                return svs.clients[entnum - 1].message;

            case MSG_ALL:
                return sv.reliable_datagram;

            case MSG_INIT:
                return sv.signon;

            default:
                throw PR_RunError("WriteDest: bad destination");
        }
    }

    // pr_cmds.c: PF_WriteByte .. PF_WriteEntity (the C evaluates the value, the last argument, before WriteDest)
    public void PF_WriteByte()
    {
        int c = X86.FtoI(G_FLOAT(OFS_PARM1));
        MSG_WriteByte(WriteDest(), c);
    }

    public void PF_WriteChar()
    {
        int c = X86.FtoI(G_FLOAT(OFS_PARM1));
        MSG_WriteChar(WriteDest(), c);
    }

    public void PF_WriteShort()
    {
        int c = X86.FtoI(G_FLOAT(OFS_PARM1));
        MSG_WriteShort(WriteDest(), c);
    }

    public void PF_WriteLong()
    {
        int c = X86.FtoI(G_FLOAT(OFS_PARM1));
        MSG_WriteLong(WriteDest(), c);
    }

    public void PF_WriteAngle()
    {
        float f = G_FLOAT(OFS_PARM1);
        MSG_WriteAngle(WriteDest(), f);
    }

    public void PF_WriteCoord()
    {
        float f = G_FLOAT(OFS_PARM1);
        MSG_WriteCoord(WriteDest(), f);
    }

    public void PF_WriteString()
    {
        string s = G_STRING(OFS_PARM1);
        MSG_WriteString(WriteDest(), s);
    }

    public void PF_WriteEntity()
    {
        int c = G_EDICTNUM(OFS_PARM1);
        MSG_WriteShort(WriteDest(), c);
    }

    // =============================================================================

    // pr_cmds.c: PF_makestatic
    public void PF_makestatic()
    {
        Edict ent;
        int i;

        ent = G_EDICT(OFS_PARM0);

        MSG_WriteByte(sv.signon, svc_spawnstatic);

        MSG_WriteByte(sv.signon, SV_ModelIndex(PR_GetString(ent.v.model)));

        MSG_WriteByte(sv.signon, X86.FtoI(ent.v.frame));
        MSG_WriteByte(sv.signon, X86.FtoI(ent.v.colormap));
        MSG_WriteByte(sv.signon, X86.FtoI(ent.v.skin));
        for (i = 0; i < 3; i++)
        {
            MSG_WriteCoord(sv.signon, ent.v.origin[i]);
            MSG_WriteAngle(sv.signon, ent.v.angles[i]);
        }

        // throw the entity away now
        ED_Free(ent);
    }

    // =============================================================================

    // pr_cmds.c: PF_setspawnparms
    public void PF_setspawnparms()
    {
        Edict ent;
        int i;
        ServerClient client;

        ent = G_EDICT(OFS_PARM0);
        i = NUM_FOR_EDICT(ent);
        if (i < 1 || i > svs.maxclients)
        {
            throw PR_RunError("Entity is not a client");
        }

        // copy spawn parms out of the client_t
        client = svs.clients[i - 1];

        for (i = 0; i < NUM_SPAWN_PARMS; i++)
        {
            Parm(i) = client.spawn_parms[i];
        }
    }

    // (&pr_global_struct->parm1)[i]
    public ref float Parm(int i) => ref System.Runtime.CompilerServices.Unsafe.Add(ref pr_global_struct.parm1, i);

    // pr_cmds.c: PF_changelevel
    public void PF_changelevel()
    {
        string s;

        // make sure we don't issue two changelevels
        if (svs.changelevel_issued)
        {
            return;
        }

        svs.changelevel_issued = true;

        s = G_STRING(OFS_PARM0);
        Cbuf_AddText(va("changelevel %s\n", s));
    }

    // pr_cmds.c: PF_Fixme
    public void PF_Fixme() => throw PR_RunError("unimplemented bulitin");

    // pr_cmds.c: pr_builtin, the builtins by number
    private Action[] PR_Builtins() =>
    [
        PF_Fixme,
        PF_makevectors, // void(entity e)	makevectors 		= #1;
        PF_setorigin, // void(entity e, vector o) setorigin	= #2;
        PF_setmodel, // void(entity e, string m) setmodel	= #3;
        PF_setsize, // void(entity e, vector min, vector max) setsize = #4;
        PF_Fixme, // void(entity e, vector min, vector max) setabssize = #5;
        PF_break, // void() break						= #6;
        PF_random, // float() random						= #7;
        PF_sound, // void(entity e, float chan, string samp) sound = #8;
        PF_normalize, // vector(vector v) normalize			= #9;
        PF_error, // void(string e) error				= #10;
        PF_objerror, // void(string e) objerror				= #11;
        PF_vlen, // float(vector v) vlen				= #12;
        PF_vectoyaw, // float(vector v) vectoyaw		= #13;
        PF_Spawn, // entity() spawn						= #14;
        PF_Remove, // void(entity e) remove				= #15;
        PF_traceline, // float(vector v1, vector v2, float tryents) traceline = #16;
        PF_checkclient, // entity() clientlist					= #17;
        PF_Find, // entity(entity start, .string fld, string match) find = #18;
        PF_precache_sound, // void(string s) precache_sound		= #19;
        PF_precache_model, // void(string s) precache_model		= #20;
        PF_stuffcmd, // void(entity client, string s)stuffcmd = #21;
        PF_findradius, // entity(vector org, float rad) findradius = #22;
        PF_bprint, // void(string s) bprint				= #23;
        PF_sprint, // void(entity client, string s) sprint = #24;
        PF_dprint, // void(string s) dprint				= #25;
        PF_ftos, // void(string s) ftos				= #26;
        PF_vtos, // void(string s) vtos				= #27;
        PF_coredump,
        PF_traceon,
        PF_traceoff,
        PF_eprint, // void(entity e) debug print an entire entity
        PF_walkmove, // float(float yaw, float dist) walkmove
        PF_Fixme, // float(float yaw, float dist) walkmove
        PF_droptofloor,
        PF_lightstyle,
        PF_rint,
        PF_floor,
        PF_ceil,
        PF_Fixme,
        PF_checkbottom,
        PF_pointcontents,
        PF_Fixme,
        PF_fabs,
        PF_aim,
        PF_cvar,
        PF_localcmd,
        PF_nextent,
        PF_particle,
        PF_changeyaw,
        PF_Fixme,
        PF_vectoangles,

        PF_WriteByte,
        PF_WriteChar,
        PF_WriteShort,
        PF_WriteLong,
        PF_WriteCoord,
        PF_WriteAngle,
        PF_WriteString,
        PF_WriteEntity,

        PF_Fixme,
        PF_Fixme,
        PF_Fixme,
        PF_Fixme,
        PF_Fixme,
        PF_Fixme,
        PF_Fixme,

        SV_MoveToGoal,
        PF_precache_file,
        PF_makestatic,

        PF_changelevel,
        PF_Fixme,

        PF_cvar_set,
        PF_centerprint,

        PF_ambientsound,

        PF_precache_model,
        PF_precache_sound, // precache_sound2 is different only for qcc
        PF_precache_file,

        PF_setspawnparms,
    ];
}
