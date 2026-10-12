using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Sound;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Protocol;
using ModelT = QuakeSharp.Core.Model.Model;

namespace QuakeSharp.Core;

// cl_tent.c: client side temporary entities
public sealed partial class QuakeEngine
{
    public int num_temp_entities;
    public readonly Entity[] cl_temp_entities = [.. Enumerable.Range(0, MAX_TEMP_ENTITIES).Select(_ => new Entity())];
    public readonly Beam[] cl_beams = [.. Enumerable.Range(0, MAX_BEAMS).Select(_ => new Beam())];

    public Sfx? cl_sfx_wizhit;
    public Sfx? cl_sfx_knighthit;
    public Sfx? cl_sfx_tink1;
    public Sfx? cl_sfx_ric1;
    public Sfx? cl_sfx_ric2;
    public Sfx? cl_sfx_ric3;
    public Sfx? cl_sfx_r_exp3;

    // cl_tent.c: CL_InitTEnts
    public void CL_InitTEnts()
    {
        cl_sfx_wizhit = S_PrecacheSound("wizard/hit.wav");
        cl_sfx_knighthit = S_PrecacheSound("hknight/hit.wav");
        cl_sfx_tink1 = S_PrecacheSound("weapons/tink1.wav");
        cl_sfx_ric1 = S_PrecacheSound("weapons/ric1.wav");
        cl_sfx_ric2 = S_PrecacheSound("weapons/ric2.wav");
        cl_sfx_ric3 = S_PrecacheSound("weapons/ric3.wav");
        cl_sfx_r_exp3 = S_PrecacheSound("weapons/r_exp3.wav");
    }

    // cl_tent.c: CL_ParseBeam
    public void CL_ParseBeam(ModelT? m)
    {
        int ent;
        Vec3 start = default, end = default;
        Beam b;
        int i;

        ent = MSG_ReadShort();

        start[0] = MSG_ReadCoord();
        start[1] = MSG_ReadCoord();
        start[2] = MSG_ReadCoord();

        end[0] = MSG_ReadCoord();
        end[1] = MSG_ReadCoord();
        end[2] = MSG_ReadCoord();

        // override any beam with the same entity
        for (i = 0; i < MAX_BEAMS; i++)
        {
            b = cl_beams[i];
            if (b.entity == ent)
            {
                b.entity = ent;
                b.model = m;
                b.endtime = (float)(cl.time + 0.2);
                VectorCopy(start, ref b.start);
                VectorCopy(end, ref b.end);
                return;
            }
        }

        // find a free beam
        for (i = 0; i < MAX_BEAMS; i++)
        {
            b = cl_beams[i];
            if (b.model == null || b.endtime < cl.time)
            {
                b.entity = ent;
                b.model = m;
                b.endtime = (float)(cl.time + 0.2);
                VectorCopy(start, ref b.start);
                VectorCopy(end, ref b.end);
                return;
            }
        }

        Con_Printf("beam list overflow!\n");
    }

    // cl_tent.c: CL_ParseTEnt
    public void CL_ParseTEnt()
    {
        int type;
        Vec3 pos = default;
        Dlight dl;
        int rnd;
        int colorStart, colorLength;

        type = MSG_ReadByte();
        switch (type)
        {
            case TE_WIZSPIKE: // spike hitting wall
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_RunParticleEffect(pos, vec3_origin, 20, 30);
                S_StartSound(-1, 0, cl_sfx_wizhit, pos, 1, 1);
                break;

            case TE_KNIGHTSPIKE: // spike hitting wall
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_RunParticleEffect(pos, vec3_origin, 226, 20);
                S_StartSound(-1, 0, cl_sfx_knighthit, pos, 1, 1);
                break;

            case TE_SPIKE: // spike hitting wall
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_RunParticleEffect(pos, vec3_origin, 0, 10);
                if (rand() % 5 != 0)
                {
                    S_StartSound(-1, 0, cl_sfx_tink1, pos, 1, 1);
                }
                else
                {
                    rnd = rand() & 3;
                    if (rnd == 1)
                    {
                        S_StartSound(-1, 0, cl_sfx_ric1, pos, 1, 1);
                    }
                    else if (rnd == 2)
                    {
                        S_StartSound(-1, 0, cl_sfx_ric2, pos, 1, 1);
                    }
                    else
                    {
                        S_StartSound(-1, 0, cl_sfx_ric3, pos, 1, 1);
                    }
                }

                break;

            case TE_SUPERSPIKE: // super spike hitting wall
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_RunParticleEffect(pos, vec3_origin, 0, 20);

                if (rand() % 5 != 0)
                {
                    S_StartSound(-1, 0, cl_sfx_tink1, pos, 1, 1);
                }
                else
                {
                    rnd = rand() & 3;
                    if (rnd == 1)
                    {
                        S_StartSound(-1, 0, cl_sfx_ric1, pos, 1, 1);
                    }
                    else if (rnd == 2)
                    {
                        S_StartSound(-1, 0, cl_sfx_ric2, pos, 1, 1);
                    }
                    else
                    {
                        S_StartSound(-1, 0, cl_sfx_ric3, pos, 1, 1);
                    }
                }

                break;

            case TE_GUNSHOT: // bullet hitting wall
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_RunParticleEffect(pos, vec3_origin, 0, 20);
                break;

            case TE_EXPLOSION: // rocket explosion
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_ParticleExplosion(pos);
                dl = CL_AllocDlight(0);
                VectorCopy(pos, ref dl.origin);
                dl.radius = 350;
                dl.die = (float)(cl.time + 0.5);
                dl.decay = 300;
                S_StartSound(-1, 0, cl_sfx_r_exp3, pos, 1, 1);
                break;

            case TE_TAREXPLOSION: // tarbaby explosion
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_BlobExplosion(pos);

                S_StartSound(-1, 0, cl_sfx_r_exp3, pos, 1, 1);
                break;

            case TE_LIGHTNING1: // lightning bolts
                CL_ParseBeam(Mod_ForName("progs/bolt.mdl", true));
                break;

            case TE_LIGHTNING2: // lightning bolts
                CL_ParseBeam(Mod_ForName("progs/bolt2.mdl", true));
                break;

            case TE_LIGHTNING3: // lightning bolts
                CL_ParseBeam(Mod_ForName("progs/bolt3.mdl", true));
                break;

            // PGM 01/21/97
            case TE_BEAM: // grappling hook beam
                CL_ParseBeam(Mod_ForName("progs/beam.mdl", true));
                break;

            // PGM 01/21/97
            case TE_LAVASPLASH:
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_LavaSplash(pos);
                break;

            case TE_TELEPORT:
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                R_TeleportSplash(pos);
                break;

            case TE_EXPLOSION2: // color mapped explosion
                pos[0] = MSG_ReadCoord();
                pos[1] = MSG_ReadCoord();
                pos[2] = MSG_ReadCoord();
                colorStart = MSG_ReadByte();
                colorLength = MSG_ReadByte();
                R_ParticleExplosion2(pos, colorStart, colorLength);
                dl = CL_AllocDlight(0);
                VectorCopy(pos, ref dl.origin);
                dl.radius = 350;
                dl.die = (float)(cl.time + 0.5);
                dl.decay = 300;
                S_StartSound(-1, 0, cl_sfx_r_exp3, pos, 1, 1);
                break;

            default:
                throw Sys_Error("CL_ParseTEnt: bad type");
        }
    }

    // cl_tent.c: CL_NewTempEntity
    public Entity? CL_NewTempEntity()
    {
        Entity ent;

        if (cl_numvisedicts == MAX_VISEDICTS)
        {
            return null;
        }

        if (num_temp_entities == MAX_TEMP_ENTITIES)
        {
            return null;
        }

        ent = cl_temp_entities[num_temp_entities];
        ent.Clear();
        num_temp_entities++;
        cl_visedicts[cl_numvisedicts] = ent;
        cl_numvisedicts++;

        ent.colormap = vid.colormap;
        return ent;
    }

    // cl_tent.c: CL_UpdateTEnts
    public void CL_UpdateTEnts()
    {
        int i;
        Beam b;
        Vec3 dist = default, org = default;
        float d;
        Entity? ent;
        float yaw, pitch;
        float forward;

        num_temp_entities = 0;

        // update lightning (b moves on with i, but the loop that adds the lightning's entities reuses i: after a
        // beam that made some, i starts again from 4, and b goes on past the beams' array; C reads whatever follows
        // it, QuakeSharp takes those beams as unused: design.md §2's undefined behaviour)
        int bi;
        for (i = 0, bi = 0; i < MAX_BEAMS; i++, bi++)
        {
            if (bi >= MAX_BEAMS)
            {
                continue;
            }

            b = cl_beams[bi];
            if (b.model == null || b.endtime < cl.time)
            {
                continue;
            }

            // if coming from the player, update the start position
            if (b.entity == cl.viewentity)
            {
                VectorCopy(cl_entities[cl.viewentity].origin, ref b.start);
            }

            // calculate pitch and yaw
            VectorSubtract(b.end, b.start, ref dist);

            if (dist[1] == 0 && dist[0] == 0)
            {
                yaw = 0;
                if (dist[2] > 0)
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
                yaw = (int)(Libm.atan2(dist[1], dist[0]) * 180 / M_PI);
                if (yaw < 0)
                {
                    yaw += 360;
                }

                forward = (float)Math.Sqrt((dist[0] * dist[0]) + (dist[1] * dist[1]));
                pitch = (int)(Libm.atan2(dist[2], forward) * 180 / M_PI);
                if (pitch < 0)
                {
                    pitch += 360;
                }
            }

            // add new entities for the lightning
            VectorCopy(b.start, ref org);
            d = VectorNormalize(ref dist);
            while (d > 0)
            {
                ent = CL_NewTempEntity();
                if (ent == null)
                {
                    return;
                }

                VectorCopy(org, ref ent.origin);
                ent.model = b.model;
                ent.angles[0] = pitch;
                ent.angles[1] = yaw;
                ent.angles[2] = rand() % 360;

                for (i = 0; i < 3; i++)
                {
                    org[i] += dist[i] * 30;
                }

                d -= 30;
            }
        }
    }
}
