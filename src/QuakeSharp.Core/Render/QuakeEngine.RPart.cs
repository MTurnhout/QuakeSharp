using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// r_part.c: the particles. The client spawns them (drawing from rand()); R_DrawParticles moves and kills them as it
// draws them, with D_DrawParticle (d_part.c) drawing each.
public sealed partial class QuakeEngine
{
    /// <summary>default max # of particles at one time</summary>
    private const int MAX_PARTICLES = 2048;

    /// <summary>no fewer than this no matter what's on the command line</summary>
    private const int ABSOLUTE_MIN_PARTICLES = 512;

    private static readonly int[] ramp1 = [0x6f, 0x6d, 0x6b, 0x69, 0x67, 0x65, 0x63, 0x61];
    private static readonly int[] ramp2 = [0x6f, 0x6e, 0x6d, 0x6c, 0x6b, 0x6a, 0x68, 0x66];
    private static readonly int[] ramp3 = [0x6d, 0x6b, 6, 5, 4, 3, 0, 0];

    public Particle? active_particles, free_particles;

    public Particle[] particles = [];
    public int r_numparticles;

    public Vec3 r_pright, r_pup, r_ppn;

    /// <summary>r_part.c: avelocities[NUMVERTEXNORMALS][3], row by row</summary>
    private readonly float[] avelocities = new float[NUMVERTEXNORMALS * 3];

    private readonly float beamlength = 16;

    // r_part.c: R_RocketTrail's static tracercount
    private int tracercount;

    // r_part.c: R_InitParticles
    public void R_InitParticles()
    {
        int i;

        i = COM_CheckParm("-particles");

        if (i != 0)
        {
            r_numparticles = QString.Q_atoi(com_argv.Argv(i + 1));
            if (r_numparticles < ABSOLUTE_MIN_PARTICLES)
            {
                r_numparticles = ABSOLUTE_MIN_PARTICLES;
            }
        }
        else
        {
            r_numparticles = MAX_PARTICLES;
        }

        particles = [.. Enumerable.Range(0, r_numparticles).Select(_ => new Particle())];
    }

    // Takes a free particle and makes it active (the lines every spawning function repeats)
    private Particle NewParticle()
    {
        Particle p = free_particles!;
        free_particles = p.next;
        p.next = active_particles;
        active_particles = p;
        return p;
    }

    // r_part.c: R_EntityParticles
    public void R_EntityParticles(Entity ent)
    {
        int i;
        Particle p;
        float angle;
        float sr, sp, sy, cr, cp, cy;
        Vec3 forward = default;
        float dist;

        dist = 64;

        if (avelocities[0] == 0)
        {
            for (i = 0; i < NUMVERTEXNORMALS * 3; i++)
            {
                avelocities[i] = (float)((rand() & 255) * 0.01);
            }
        }

        for (i = 0; i < NUMVERTEXNORMALS; i++)
        {
            angle = (float)(cl.time * avelocities[(i * 3) + 0]);
            sy = (float)Libm.sin(angle);
            cy = (float)Libm.cos(angle);
            angle = (float)(cl.time * avelocities[(i * 3) + 1]);
            sp = (float)Libm.sin(angle);
            cp = (float)Libm.cos(angle);
            angle = (float)(cl.time * avelocities[(i * 3) + 2]);
            sr = (float)Libm.sin(angle);
            cr = (float)Libm.cos(angle);

            forward[0] = cp * cy;
            forward[1] = cp * sy;
            forward[2] = -sp;

            if (free_particles == null)
            {
                return;
            }

            p = NewParticle();

            p.die = (float)(cl.time + 0.01);
            p.color = 0x6f;
            p.type = ptype_t.pt_explode;

            p.org[0] = ent.origin[0] + (r_avertexnormals[(i * 3) + 0] * dist) + (forward[0] * beamlength);
            p.org[1] = ent.origin[1] + (r_avertexnormals[(i * 3) + 1] * dist) + (forward[1] * beamlength);
            p.org[2] = ent.origin[2] + (r_avertexnormals[(i * 3) + 2] * dist) + (forward[2] * beamlength);
        }

    }

    // r_part.c: R_ClearParticles
    public void R_ClearParticles()
    {
        int i;

        free_particles = particles[0];
        active_particles = null;

        for (i = 0; i < r_numparticles - 1; i++)
        {
            particles[i].next = particles[i + 1];
        }

        particles[r_numparticles - 1].next = null;
    }

    // r_part.c: R_ReadPointFile_f
    public void R_ReadPointFile_f()
    {
        Vec3 org = default;
        int c;
        Particle p;
        string name;

        name = CFormat.Sprintf("maps/%s.pts", sv.name);

        COM_FOpenFile(name, out Stream? f);
        if (f == null)
        {
            Con_Printf("couldn't open %s\n", name);
            return;
        }

        Con_Printf("Reading %s...\n", name);
        c = 0;
        using (var reader = new StreamReader(f, System.Text.Encoding.Latin1))
        {
            while (true)
            {
                // fscanf (f,"%f %f %f\n", ...)
                string? line = reader.ReadLine();
                string[] fields = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (fields.Length < 3
                    || !float.TryParse(fields[0], System.Globalization.CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(fields[1], System.Globalization.CultureInfo.InvariantCulture, out float y)
                    || !float.TryParse(fields[2], System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    break;
                }

                org[0] = x;
                org[1] = y;
                org[2] = z;
                c++;

                if (free_particles == null)
                {
                    Con_Printf("Not enough free particles\n");
                    break;
                }

                p = NewParticle();

                p.die = 99999;
                p.color = -c & 15;
                p.type = ptype_t.pt_static;
                VectorCopy(vec3_origin, ref p.vel);
                VectorCopy(org, ref p.org);
            }
        }

        Con_Printf("%i points read\n", c);
    }

    // r_part.c: R_ParseParticleEffect
    // Parse an effect out of the server message
    public void R_ParseParticleEffect()
    {
        Vec3 org = default, dir = default;
        int i, count, msgcount, color;

        for (i = 0; i < 3; i++)
        {
            org[i] = MSG_ReadCoord();
        }

        for (i = 0; i < 3; i++)
        {
            dir[i] = (float)(MSG_ReadChar() * (1.0 / 16));
        }

        msgcount = MSG_ReadByte();
        color = MSG_ReadByte();

        if (msgcount == 255)
        {
            count = 1024;
        }
        else
        {
            count = msgcount;
        }

        R_RunParticleEffect(org, dir, color, count);
    }

    // r_part.c: R_ParticleExplosion
    public void R_ParticleExplosion(in Vec3 org)
    {
        int i, j;
        Particle p;

        for (i = 0; i < 1024; i++)
        {
            if (free_particles == null)
            {
                return;
            }

            p = NewParticle();

            p.die = (float)(cl.time + 5);
            p.color = ramp1[0];
            p.ramp = rand() & 3;
            if ((i & 1) != 0)
            {
                p.type = ptype_t.pt_explode;
                for (j = 0; j < 3; j++)
                {
                    p.org[j] = org[j] + ((rand() % 32) - 16);
                    p.vel[j] = (rand() % 512) - 256;
                }
            }
            else
            {
                p.type = ptype_t.pt_explode2;
                for (j = 0; j < 3; j++)
                {
                    p.org[j] = org[j] + ((rand() % 32) - 16);
                    p.vel[j] = (rand() % 512) - 256;
                }
            }
        }
    }

    // r_part.c: R_ParticleExplosion2
    public void R_ParticleExplosion2(in Vec3 org, int colorStart, int colorLength)
    {
        int i, j;
        Particle p;
        int colorMod = 0;

        for (i = 0; i < 512; i++)
        {
            if (free_particles == null)
            {
                return;
            }

            p = NewParticle();

            p.die = (float)(cl.time + 0.3);
            p.color = colorStart + (colorMod % colorLength);
            colorMod++;

            p.type = ptype_t.pt_blob;
            for (j = 0; j < 3; j++)
            {
                p.org[j] = org[j] + ((rand() % 32) - 16);
                p.vel[j] = (rand() % 512) - 256;
            }
        }
    }

    // r_part.c: R_BlobExplosion
    public void R_BlobExplosion(in Vec3 org)
    {
        int i, j;
        Particle p;

        for (i = 0; i < 1024; i++)
        {
            if (free_particles == null)
            {
                return;
            }

            p = NewParticle();

            p.die = (float)(cl.time + 1 + ((rand() & 8) * 0.05));

            if ((i & 1) != 0)
            {
                p.type = ptype_t.pt_blob;
                p.color = 66 + (rand() % 6);
                for (j = 0; j < 3; j++)
                {
                    p.org[j] = org[j] + ((rand() % 32) - 16);
                    p.vel[j] = (rand() % 512) - 256;
                }
            }
            else
            {
                p.type = ptype_t.pt_blob2;
                p.color = 150 + (rand() % 6);
                for (j = 0; j < 3; j++)
                {
                    p.org[j] = org[j] + ((rand() % 32) - 16);
                    p.vel[j] = (rand() % 512) - 256;
                }
            }
        }
    }

    // r_part.c: R_RunParticleEffect
    public void R_RunParticleEffect(in Vec3 org, in Vec3 dir, int color, int count)
    {
        int i, j;
        Particle p;

        for (i = 0; i < count; i++)
        {
            if (free_particles == null)
            {
                return;
            }

            p = NewParticle();

            if (count == 1024)
            {
                // rocket explosion
                p.die = (float)(cl.time + 5);
                p.color = ramp1[0];
                p.ramp = rand() & 3;
                if ((i & 1) != 0)
                {
                    p.type = ptype_t.pt_explode;
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = org[j] + ((rand() % 32) - 16);
                        p.vel[j] = (rand() % 512) - 256;
                    }
                }
                else
                {
                    p.type = ptype_t.pt_explode2;
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = org[j] + ((rand() % 32) - 16);
                        p.vel[j] = (rand() % 512) - 256;
                    }
                }
            }
            else
            {
                p.die = (float)(cl.time + (0.1 * (rand() % 5)));
                p.color = (color & ~7) + (rand() & 7);
                p.type = ptype_t.pt_slowgrav;
                for (j = 0; j < 3; j++)
                {
                    p.org[j] = org[j] + ((rand() & 15) - 8);
                    p.vel[j] = dir[j] * 15; // + (rand()%300)-150;
                }
            }
        }
    }

    // r_part.c: R_LavaSplash
    public void R_LavaSplash(in Vec3 org)
    {
        int i, j, k;
        Particle p;
        float vel;
        Vec3 dir = default;

        for (i = -16; i < 16; i++)
        {
            for (j = -16; j < 16; j++)
            {
                for (k = 0; k < 1; k++)
                {
                    if (free_particles == null)
                    {
                        return;
                    }

                    p = NewParticle();

                    p.die = (float)(cl.time + 2 + ((rand() & 31) * 0.02));
                    p.color = 224 + (rand() & 7);
                    p.type = ptype_t.pt_slowgrav;

                    dir[0] = (j * 8) + (rand() & 7);
                    dir[1] = (i * 8) + (rand() & 7);
                    dir[2] = 256;

                    p.org[0] = org[0] + dir[0];
                    p.org[1] = org[1] + dir[1];
                    p.org[2] = org[2] + (rand() & 63);

                    VectorNormalize(ref dir);
                    vel = 50 + (rand() & 63);
                    VectorScale(dir, vel, ref p.vel);
                }
            }
        }
    }

    // r_part.c: R_TeleportSplash
    public void R_TeleportSplash(in Vec3 org)
    {
        int i, j, k;
        Particle p;
        float vel;
        Vec3 dir = default;

        for (i = -16; i < 16; i += 4)
        {
            for (j = -16; j < 16; j += 4)
            {
                for (k = -24; k < 32; k += 4)
                {
                    if (free_particles == null)
                    {
                        return;
                    }

                    p = NewParticle();

                    p.die = (float)(cl.time + 0.2 + ((rand() & 7) * 0.02));
                    p.color = 7 + (rand() & 7);
                    p.type = ptype_t.pt_slowgrav;

                    dir[0] = j * 8;
                    dir[1] = i * 8;
                    dir[2] = k * 8;

                    p.org[0] = org[0] + i + (rand() & 3);
                    p.org[1] = org[1] + j + (rand() & 3);
                    p.org[2] = org[2] + k + (rand() & 3);

                    VectorNormalize(ref dir);
                    vel = 50 + (rand() & 63);
                    VectorScale(dir, vel, ref p.vel);
                }
            }
        }
    }

    // r_part.c: R_RocketTrail
    public void R_RocketTrail(ref Vec3 start, in Vec3 end, int type)
    {
        Vec3 vec = default;
        float len;
        int j;
        Particle p;
        int dec;

        VectorSubtract(end, start, ref vec);
        len = VectorNormalize(ref vec);
        if (type < 128)
        {
            dec = 3;
        }
        else
        {
            dec = 1;
            type -= 128;
        }

        while (len > 0)
        {
            len -= dec;

            if (free_particles == null)
            {
                return;
            }

            p = NewParticle();

            VectorCopy(vec3_origin, ref p.vel);
            p.die = (float)(cl.time + 2);

            switch (type)
            {
                case 0: // rocket trail
                    p.ramp = rand() & 3;
                    p.color = ramp3[(int)p.ramp];
                    p.type = ptype_t.pt_fire;
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = start[j] + ((rand() % 6) - 3);
                    }

                    break;

                case 1: // smoke smoke
                    p.ramp = (rand() & 3) + 2;
                    p.color = ramp3[(int)p.ramp];
                    p.type = ptype_t.pt_fire;
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = start[j] + ((rand() % 6) - 3);
                    }

                    break;

                case 2: // blood
                    p.type = ptype_t.pt_grav;
                    p.color = 67 + (rand() & 3);
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = start[j] + ((rand() % 6) - 3);
                    }

                    break;

                case 3:
                case 5: // tracer
                    p.die = (float)(cl.time + 0.5);
                    p.type = ptype_t.pt_static;
                    if (type == 3)
                    {
                        p.color = 52 + ((tracercount & 4) << 1);
                    }
                    else
                    {
                        p.color = 230 + ((tracercount & 4) << 1);
                    }

                    tracercount++;

                    VectorCopy(start, ref p.org);
                    if ((tracercount & 1) != 0)
                    {
                        p.vel[0] = 30 * vec[1];
                        p.vel[1] = 30 * -vec[0];
                    }
                    else
                    {
                        p.vel[0] = 30 * -vec[1];
                        p.vel[1] = 30 * vec[0];
                    }

                    break;

                case 4: // slight blood
                    p.type = ptype_t.pt_grav;
                    p.color = 67 + (rand() & 3);
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = start[j] + ((rand() % 6) - 3);
                    }

                    len -= 3;
                    break;

                case 6: // voor trail
                    p.color = (9 * 16) + 8 + (rand() & 3);
                    p.type = ptype_t.pt_static;
                    p.die = (float)(cl.time + 0.3);
                    for (j = 0; j < 3; j++)
                    {
                        p.org[j] = start[j] + ((rand() & 15) - 8);
                    }

                    break;
            }

            VectorAdd(start, vec, ref start);
        }
    }

    // r_part.c: R_DrawParticles
    public void R_DrawParticles()
    {
        Particle? p, kill;
        float grav;
        int i;
        float time2, time3;
        float time1;
        float dvel;
        float frametime;

        D_StartParticles();

        VectorScale(vright, xscaleshrink, ref r_pright);
        VectorScale(vup, yscaleshrink, ref r_pup);
        VectorCopy(vpn, ref r_ppn);

        frametime = (float)(cl.time - cl.oldtime);
        time3 = frametime * 15;
        time2 = frametime * 10; // 15;
        time1 = frametime * 5;
        grav = (float)(frametime * sv_gravity.value * 0.05);
        dvel = 4 * frametime;

        while (true)
        {
            kill = active_particles;
            if (kill != null && kill.die < cl.time)
            {
                active_particles = kill.next;
                kill.next = free_particles;
                free_particles = kill;
                continue;
            }

            break;
        }

        for (p = active_particles; p != null; p = p.next)
        {
            while (true)
            {
                kill = p.next;
                if (kill != null && kill.die < cl.time)
                {
                    p.next = kill.next;
                    kill.next = free_particles;
                    free_particles = kill;
                    continue;
                }

                break;
            }

            D_DrawParticle(p);

            p.org[0] += p.vel[0] * frametime;
            p.org[1] += p.vel[1] * frametime;
            p.org[2] += p.vel[2] * frametime;

            switch (p.type)
            {
                case ptype_t.pt_static:
                    break;

                case ptype_t.pt_fire:
                    p.ramp += time1;
                    if (p.ramp >= 6)
                    {
                        p.die = -1;
                    }
                    else
                    {
                        p.color = ramp3[(int)p.ramp];
                    }

                    p.vel[2] += grav;
                    break;

                case ptype_t.pt_explode:
                    p.ramp += time2;
                    if (p.ramp >= 8)
                    {
                        p.die = -1;
                    }
                    else
                    {
                        p.color = ramp1[(int)p.ramp];
                    }

                    for (i = 0; i < 3; i++)
                    {
                        p.vel[i] += p.vel[i] * dvel;
                    }

                    p.vel[2] -= grav;
                    break;

                case ptype_t.pt_explode2:
                    p.ramp += time3;
                    if (p.ramp >= 8)
                    {
                        p.die = -1;
                    }
                    else
                    {
                        p.color = ramp2[(int)p.ramp];
                    }

                    for (i = 0; i < 3; i++)
                    {
                        p.vel[i] -= p.vel[i] * frametime;
                    }

                    p.vel[2] -= grav;
                    break;

                case ptype_t.pt_blob:
                    for (i = 0; i < 3; i++)
                    {
                        p.vel[i] += p.vel[i] * dvel;
                    }

                    p.vel[2] -= grav;
                    break;

                case ptype_t.pt_blob2:
                    for (i = 0; i < 2; i++)
                    {
                        p.vel[i] -= p.vel[i] * dvel;
                    }

                    p.vel[2] -= grav;
                    break;

                case ptype_t.pt_grav:
                case ptype_t.pt_slowgrav:
                    p.vel[2] -= grav;
                    break;
            }
        }

        D_EndParticles();
    }
}
