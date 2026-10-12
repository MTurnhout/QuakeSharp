using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Protocol;
using static QuakeSharp.Core.QuakeDef;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// cl_main.c: client main loop
public sealed partial class QuakeEngine
{
    // client.h
    public const int MAX_DLIGHTS = 32;
    public const int MAX_BEAMS = 24;
    public const int MAX_EFRAGS = 640;
    public const int MAX_MAPSTRING = 2048;
    public const int MAX_TEMP_ENTITIES = 64; // lightning bolts, etc
    public const int MAX_STATIC_ENTITIES = 128; // torches, etc
    public const int MAX_VISEDICTS = 256;

    // we need to declare some mouse variables here, because the menu system references them even when on a unix
    // system.

    // these two are not intended to be set directly
    public readonly Cvar cl_name = new("_cl_name", "player", true);
    public readonly Cvar cl_color = new("_cl_color", "0", true);

    /// <summary>can be 0, 1, or 2</summary>
    public readonly Cvar cl_shownet = new("cl_shownet", "0");

    public readonly Cvar cl_nolerp = new("cl_nolerp", "0");

    public readonly Cvar lookspring = new("lookspring", "0", true);
    public readonly Cvar lookstrafe = new("lookstrafe", "0", true);
    public readonly Cvar sensitivity = new("sensitivity", "3", true);

    public readonly Cvar m_pitch = new("m_pitch", "0.022", true);
    public readonly Cvar m_yaw = new("m_yaw", "0.022", true);
    public readonly Cvar m_forward = new("m_forward", "1", true);
    public readonly Cvar m_side = new("m_side", "0.8", true);

    /// <summary>cl_main.c: cls</summary>
    public readonly ClientStatic cls = new();

    /// <summary>cl_main.c: cl (wiped by a new instance)</summary>
    public ClientState cl = new();

    // FIXME: put these on hunk?
    public readonly Efrag[] cl_efrags = [.. Enumerable.Range(0, MAX_EFRAGS).Select(_ => new Efrag())];
    public readonly Entity[] cl_entities = [.. Enumerable.Range(0, MAX_EDICTS).Select(_ => new Entity())];
    public readonly Entity[] cl_static_entities = [.. Enumerable.Range(0, MAX_STATIC_ENTITIES).Select(_ => new Entity())];
    public readonly LightStyle[] cl_lightstyle = [.. Enumerable.Range(0, MAX_LIGHTSTYLES).Select(_ => new LightStyle())];
    public readonly Dlight[] cl_dlights = [.. Enumerable.Range(0, MAX_DLIGHTS).Select(_ => new Dlight())];

    public int cl_numvisedicts;
    public readonly Entity?[] cl_visedicts = new Entity?[MAX_VISEDICTS];

    // cl_main.c: CL_ClearState
    public void CL_ClearState()
    {
        int i;

        if (!sv.active)
        {
            Host_ClearMemory();
        }

        // wipe the entire cl structure
        cl = new ClientState();

        SZ_Clear(cls.message);

        // clear other arrays
        foreach (Efrag e in cl_efrags)
        {
            e.leaf = null;
            e.leafnext = null;
            e.entity = null;
            e.entnext = null;
        }

        foreach (Entity e in cl_entities)
        {
            e.Clear();
        }

        foreach (Dlight d in cl_dlights)
        {
            d.Clear();
        }

        foreach (LightStyle l in cl_lightstyle)
        {
            l.length = 0;
            l.map = "";
        }

        foreach (Entity e in cl_temp_entities)
        {
            e.Clear();
        }

        foreach (Beam b in cl_beams)
        {
            b.Clear();
        }

        // allocate the efrags and chain together into a free list
        cl.free_efrags = cl_efrags[0];
        for (i = 0; i < MAX_EFRAGS - 1; i++)
        {
            cl_efrags[i].entnext = cl_efrags[i + 1];
        }

        cl_efrags[i].entnext = null;
    }

    // cl_main.c: CL_Disconnect
    // Sends a disconnect message to the server
    // This is also called on Host_Error, so it shouldn't cause any errors
    public void CL_Disconnect()
    {
        // stop sounds (especially looping!)
        S_StopAllSounds(true);

        // bring the console down and fade the colors back to normal
        // SCR_BringDownConsole ();

        // if running a local server, shut it down
        if (cls.demoplayback)
        {
            CL_StopPlayback();
        }
        else if (cls.state == cactive_t.ca_connected)
        {
            if (cls.demorecording)
            {
                CL_Stop_f();
            }

            Con_DPrintf("Sending clc_disconnect\n");
            SZ_Clear(cls.message);
            MSG_WriteByte(cls.message, clc_disconnect);
            NET_SendUnreliableMessage(cls.netcon, cls.message);
            SZ_Clear(cls.message);
            NET_Close(cls.netcon);

            cls.state = cactive_t.ca_disconnected;
            if (sv.active)
            {
                Host_ShutdownServer(false);
            }
        }

        cls.demoplayback = cls.timedemo = false;
        cls.signon = 0;
    }

    // cl_main.c: CL_Disconnect_f
    public void CL_Disconnect_f()
    {
        CL_Disconnect();
        if (sv.active)
        {
            Host_ShutdownServer(false);
        }
    }

    // cl_main.c: CL_EstablishConnection
    // Host should be either "local" or a net address to be passed on
    public void CL_EstablishConnection(string host)
    {
        if (cls.state == cactive_t.ca_dedicated)
        {
            return;
        }

        if (cls.demoplayback)
        {
            return;
        }

        CL_Disconnect();

        cls.netcon = NET_Connect(host);
        if (cls.netcon == null)
        {
            throw Host_Error("CL_Connect: connect failed\n");
        }

        Con_DPrintf("CL_EstablishConnection: connected to %s\n", host);

        cls.demonum = -1; // not in the demo loop now
        cls.state = cactive_t.ca_connected;
        cls.signon = 0; // need all the signon messages before playing
    }

    // cl_main.c: CL_SignonReply
    // An svc_signonnum has been received, perform a client side setup
    public void CL_SignonReply()
    {
        string str;

        Con_DPrintf("CL_SignonReply: %i\n", cls.signon);

        switch (cls.signon)
        {
            case 1:
                MSG_WriteByte(cls.message, clc_stringcmd);
                MSG_WriteString(cls.message, "prespawn");
                break;

            case 2:
                MSG_WriteByte(cls.message, clc_stringcmd);
                MSG_WriteString(cls.message, va("name \"%s\"\n", cl_name.@string));

                MSG_WriteByte(cls.message, clc_stringcmd);
                MSG_WriteString(cls.message, va("color %i %i\n", ((int)cl_color.value) >> 4, ((int)cl_color.value) & 15));

                MSG_WriteByte(cls.message, clc_stringcmd);
                str = CFormat.Sprintf("spawn %s", cls.spawnparms);
                MSG_WriteString(cls.message, str);
                break;

            case 3:
                MSG_WriteByte(cls.message, clc_stringcmd);
                MSG_WriteString(cls.message, "begin");
                Cache_Report(); // print remaining memory
                break;

            case 4:
                SCR_EndLoadingPlaque(); // allow normal screen updates
                break;
        }
    }

    // cl_main.c: CL_NextDemo
    // Called to play the next demo in the demo loop
    public void CL_NextDemo()
    {
        string str;

        if (cls.demonum == -1)
        {
            return; // don't play demos
        }

        SCR_BeginLoadingPlaque();

        if (cls.demonum == ClientStatic.MAX_DEMOS || QString.Ch(cls.demos[cls.demonum], 0) == 0)
        {
            cls.demonum = 0;
            if (QString.Ch(cls.demos[cls.demonum], 0) == 0)
            {
                Con_Printf("No demos listed with startdemos\n");
                cls.demonum = -1;
                return;
            }
        }

        str = CFormat.Sprintf("playdemo %s\n", cls.demos[cls.demonum]);
        Cbuf_InsertText(str);
        cls.demonum++;
    }

    // cl_main.c: CL_PrintEntities_f
    public void CL_PrintEntities_f()
    {
        Entity ent;
        int i;

        for (i = 0; i < cl.num_entities; i++)
        {
            ent = cl_entities[i];
            Con_Printf("%3i:", i);
            if (ent.model == null)
            {
                Con_Printf("EMPTY\n");
                continue;
            }

            Con_Printf("%s:%2i  (%5.1f,%5.1f,%5.1f) [%5.1f %5.1f %5.1f]\n",
                ent.model.name, ent.frame, ent.origin[0], ent.origin[1], ent.origin[2], ent.angles[0], ent.angles[1], ent.angles[2]);
        }
    }

    // cl_main.c: SetPal
    // Debugging tool, just flashes the screen (compiled out in the C: #if 0)
    public static void SetPal(int i) => _ = i;

    // cl_main.c: CL_AllocDlight
    public Dlight CL_AllocDlight(int key)
    {
        int i;
        Dlight dl;

        // first look for an exact key match
        if (key != 0)
        {
            for (i = 0; i < MAX_DLIGHTS; i++)
            {
                dl = cl_dlights[i];
                if (dl.key == key)
                {
                    dl.Clear();
                    dl.key = key;
                    return dl;
                }
            }
        }

        // then look for anything else
        for (i = 0; i < MAX_DLIGHTS; i++)
        {
            dl = cl_dlights[i];
            if (dl.die < cl.time)
            {
                dl.Clear();
                dl.key = key;
                return dl;
            }
        }

        dl = cl_dlights[0];
        dl.Clear();
        dl.key = key;
        return dl;
    }

    // cl_main.c: CL_DecayLights
    public void CL_DecayLights()
    {
        int i;
        Dlight dl;
        float time;

        time = (float)(cl.time - cl.oldtime);

        for (i = 0; i < MAX_DLIGHTS; i++)
        {
            dl = cl_dlights[i];
            if (dl.die < cl.time || dl.radius == 0)
            {
                continue;
            }

            dl.radius -= time * dl.decay;
            if (dl.radius < 0)
            {
                dl.radius = 0;
            }
        }
    }

    // cl_main.c: CL_LerpPoint
    // Determines the fraction between the last two messages that the objects should be put at.
    public float CL_LerpPoint()
    {
        float f, frac;

        f = (float)(cl.mtime[0] - cl.mtime[1]);

        if (f == 0 || cl_nolerp.value != 0 || cls.timedemo || sv.active)
        {
            cl.time = cl.mtime[0];
            return 1;
        }

        if (f > 0.1)
        {
            // dropped packet, or start of demo
            cl.mtime[1] = cl.mtime[0] - 0.1;
            f = (float)0.1;
        }

        frac = (float)((cl.time - cl.mtime[1]) / f);

        if (frac < 0)
        {
            if (frac < -0.01)
            {
                SetPal(1);
                cl.time = cl.mtime[1];
            }

            frac = 0;
        }
        else if (frac > 1)
        {
            if (frac > 1.01)
            {
                SetPal(2);
                cl.time = cl.mtime[0];
            }

            frac = 1;
        }
        else
        {
            SetPal(0);
        }

        return frac;
    }

    // cl_main.c: CL_RelinkEntities
    public void CL_RelinkEntities()
    {
        Entity ent;
        int i, j;
        float frac, f, d;
        Vec3 delta = default;
        float bobjrotate;
        Vec3 oldorg = default;
        Dlight dl;

        // determine partial update time
        frac = CL_LerpPoint();

        cl_numvisedicts = 0;

        // interpolate player info
        for (i = 0; i < 3; i++)
        {
            cl.velocity[i] = cl.mvelocity[1][i] + (frac * (cl.mvelocity[0][i] - cl.mvelocity[1][i]));
        }

        if (cls.demoplayback)
        {
            // interpolate the angles
            for (j = 0; j < 3; j++)
            {
                d = cl.mviewangles[0][j] - cl.mviewangles[1][j];
                if (d > 180)
                {
                    d -= 360;
                }
                else if (d < -180)
                {
                    d += 360;
                }

                cl.viewangles[j] = cl.mviewangles[1][j] + (frac * d);
            }
        }

        bobjrotate = anglemod((float)(100 * cl.time));

        // start on the entity after the world
        for (i = 1; i < cl.num_entities; i++)
        {
            ent = cl_entities[i];
            if (ent.model == null)
            {
                // empty slot
                if (ent.forcelink)
                {
                    R_RemoveEfrags(ent); // just became empty
                }

                continue;
            }

            // if the object wasn't included in the last packet, remove it
            if (ent.msgtime != cl.mtime[0])
            {
                ent.model = null;
                continue;
            }

            VectorCopy(ent.origin, ref oldorg);

            if (ent.forcelink)
            {
                // the entity was not updated in the last message so move to the final spot
                VectorCopy(ent.msg_origins[0], ref ent.origin);
                VectorCopy(ent.msg_angles[0], ref ent.angles);
            }
            else
            {
                // if the delta is large, assume a teleport and don't lerp
                f = frac;
                for (j = 0; j < 3; j++)
                {
                    delta[j] = ent.msg_origins[0][j] - ent.msg_origins[1][j];
                    if (delta[j] > 100 || delta[j] < -100)
                    {
                        f = 1; // assume a teleportation, not a motion
                    }
                }

                // interpolate the origin and angles
                for (j = 0; j < 3; j++)
                {
                    ent.origin[j] = ent.msg_origins[1][j] + (f * delta[j]);

                    d = ent.msg_angles[0][j] - ent.msg_angles[1][j];
                    if (d > 180)
                    {
                        d -= 360;
                    }
                    else if (d < -180)
                    {
                        d += 360;
                    }

                    ent.angles[j] = ent.msg_angles[1][j] + (f * d);
                }
            }

            // rotate binary objects locally
            if ((ent.model.flags & ModelT.EF_ROTATE) != 0)
            {
                ent.angles[1] = bobjrotate;
            }

            if ((ent.effects & EF_BRIGHTFIELD) != 0)
            {
                R_EntityParticles(ent);
            }

            if ((ent.effects & EF_MUZZLEFLASH) != 0)
            {
                Vec3 fv = default, rv = default, uv = default;

                dl = CL_AllocDlight(i);
                VectorCopy(ent.origin, ref dl.origin);
                dl.origin[2] += 16;
                AngleVectors(ent.angles, ref fv, ref rv, ref uv);

                VectorMA(dl.origin, 18, fv, ref dl.origin);
                dl.radius = 200 + (rand() & 31);
                dl.minlight = 32;
                dl.die = (float)(cl.time + 0.1);
            }

            if ((ent.effects & EF_BRIGHTLIGHT) != 0)
            {
                dl = CL_AllocDlight(i);
                VectorCopy(ent.origin, ref dl.origin);
                dl.origin[2] += 16;
                dl.radius = 400 + (rand() & 31);
                dl.die = (float)(cl.time + 0.001);
            }

            if ((ent.effects & EF_DIMLIGHT) != 0)
            {
                dl = CL_AllocDlight(i);
                VectorCopy(ent.origin, ref dl.origin);
                dl.radius = 200 + (rand() & 31);
                dl.die = (float)(cl.time + 0.001);
            }

            if ((ent.model.flags & ModelT.EF_GIB) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 2);
            }
            else if ((ent.model.flags & ModelT.EF_ZOMGIB) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 4);
            }
            else if ((ent.model.flags & ModelT.EF_TRACER) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 3);
            }
            else if ((ent.model.flags & ModelT.EF_TRACER2) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 5);
            }
            else if ((ent.model.flags & ModelT.EF_ROCKET) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 0);
                dl = CL_AllocDlight(i);
                VectorCopy(ent.origin, ref dl.origin);
                dl.radius = 200;
                dl.die = (float)(cl.time + 0.01);
            }
            else if ((ent.model.flags & ModelT.EF_GRENADE) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 1);
            }
            else if ((ent.model.flags & ModelT.EF_TRACER3) != 0)
            {
                R_RocketTrail(ref oldorg, ent.origin, 6);
            }

            ent.forcelink = false;

            if (i == cl.viewentity && chase_active.value == 0)
            {
                continue;
            }

            if (cl_numvisedicts < MAX_VISEDICTS)
            {
                cl_visedicts[cl_numvisedicts] = ent;
                cl_numvisedicts++;
            }
        }
    }

    // cl_main.c: CL_ReadFromServer
    // Read all incoming data from the server
    public int CL_ReadFromServer()
    {
        int ret;

        cl.oldtime = cl.time;
        cl.time += host_frametime;

        do
        {
            ret = CL_GetMessage();
            if (ret == -1)
            {
                throw Host_Error("CL_ReadFromServer: lost server connection");
            }

            if (ret == 0)
            {
                break;
            }

            cl.last_received_message = (float)realtime;
            CL_ParseServerMessage();
        }
        while (ret != 0 && cls.state == cactive_t.ca_connected);

        if (cl_shownet.value != 0)
        {
            Con_Printf("\n");
        }

        CL_RelinkEntities();
        CL_UpdateTEnts();

        // bring the links up to date
        return 0;
    }

    // cl_main.c: CL_SendCmd
    public void CL_SendCmd()
    {
        UserCmd cmd = default;

        if (cls.state != cactive_t.ca_connected)
        {
            return;
        }

        if (cls.signon == SIGNONS)
        {
            // get basic movement from keyboard
            CL_BaseMove(ref cmd);

            // allow mice or other external controllers to add to the move
            IN_Move(ref cmd);

            // send the unreliable message
            CL_SendMove(cmd);
        }

        if (cls.demoplayback)
        {
            SZ_Clear(cls.message);
            return;
        }

        // send the reliable message
        if (cls.message.cursize == 0)
        {
            return; // no message at all
        }

        if (!NET_CanSendMessage(cls.netcon))
        {
            Con_DPrintf("CL_WriteToServer: can't send\n");
            return;
        }

        if (NET_SendMessage(cls.netcon, cls.message) == -1)
        {
            throw Host_Error("CL_WriteToServer: lost server connection");
        }

        SZ_Clear(cls.message);
    }

    // cl_main.c: CL_Init
    public void CL_Init()
    {
        cls.message = new SizeBuf(1024);

        CL_InitInput();
        CL_InitTEnts();

        // register our commands
        Cvar_RegisterVariable(cl_name);
        Cvar_RegisterVariable(cl_color);
        Cvar_RegisterVariable(cl_upspeed);
        Cvar_RegisterVariable(cl_forwardspeed);
        Cvar_RegisterVariable(cl_backspeed);
        Cvar_RegisterVariable(cl_sidespeed);
        Cvar_RegisterVariable(cl_movespeedkey);
        Cvar_RegisterVariable(cl_yawspeed);
        Cvar_RegisterVariable(cl_pitchspeed);
        Cvar_RegisterVariable(cl_anglespeedkey);
        Cvar_RegisterVariable(cl_shownet);
        Cvar_RegisterVariable(cl_nolerp);
        Cvar_RegisterVariable(lookspring);
        Cvar_RegisterVariable(lookstrafe);
        Cvar_RegisterVariable(sensitivity);

        Cvar_RegisterVariable(m_pitch);
        Cvar_RegisterVariable(m_yaw);
        Cvar_RegisterVariable(m_forward);
        Cvar_RegisterVariable(m_side);

        Cmd_AddCommand("entities", CL_PrintEntities_f);
        Cmd_AddCommand("disconnect", CL_Disconnect_f);
        Cmd_AddCommand("record", CL_Record_f);
        Cmd_AddCommand("stop", CL_Stop_f);
        Cmd_AddCommand("playdemo", CL_PlayDemo_f);
        Cmd_AddCommand("timedemo", CL_TimeDemo_f);
    }
}
