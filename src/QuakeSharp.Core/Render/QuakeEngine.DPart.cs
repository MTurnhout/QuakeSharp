using QuakeSharp.Core.Common;
using QuakeSharp.Core.Numerics;
using QuakeSharp.Core.Render;
using static QuakeSharp.Core.Common.MathLib;

namespace QuakeSharp.Core;

// d_part.c: software driver module for drawing particles
public sealed partial class QuakeEngine
{
    // d_iface.h: PARTICLE_Z_CLIP
    private const double PARTICLE_Z_CLIP = 8.0;

    // d_part.c: D_EndParticles
    public static void D_EndParticles()
    {
        // not used by software driver
    }

    // d_part.c: D_StartParticles
    public static void D_StartParticles()
    {
        // not used by software driver
    }

    // d_part.c: D_DrawParticle
    public void D_DrawParticle(Particle pparticle)
    {
        Vec3 local = default, transformed = default;
        float zi;
        int pdest;
        int pz;
        int i, izi, pix, count, u, v;
        short[] zbuffer = d_pzbuffer!;
        byte[] dest = d_viewbuffer;
        byte color;

        // transform point
        VectorSubtract(pparticle.org, r_origin, ref local);

        transformed[0] = DotProduct(local, r_pright);
        transformed[1] = DotProduct(local, r_pup);
        transformed[2] = DotProduct(local, r_ppn);

        if (transformed[2] < PARTICLE_Z_CLIP)
        {
            return;
        }

        // project the point
        // FIXME: preadjust xcenter and ycenter
        zi = (float)(1.0 / transformed[2]);
        u = X86.FtoI(xcenter + (zi * transformed[0]) + 0.5);
        v = X86.FtoI(ycenter - (zi * transformed[1]) + 0.5);

        if ((v > d_vrectbottom_particle) ||
            (u > d_vrectright_particle) ||
            (v < d_vrecty) ||
            (u < d_vrectx))
        {
            return;
        }

        pz = (int)(d_zwidth * v) + u;
        pdest = d_viewbufferoffset + d_scantable[v] + u;
        izi = X86.FtoI(zi * 0x8000);

        pix = izi >> d_pix_shift;

        if (pix < d_pix_min)
        {
            pix = d_pix_min;
        }
        else if (pix > d_pix_max)
        {
            pix = d_pix_max;
        }

        // the color is a float the C converts to the frame's byte
        color = (byte)X86.FtoI(pparticle.color);

        // (the C unwinds the cases of 1 to 4 pixels: the same pixels)
        count = pix << d_y_aspect_shift;

        for (; count != 0; count--, pz += (int)d_zwidth, pdest += screenwidth)
        {
            for (i = 0; i < pix; i++)
            {
                if (zbuffer[pz + i] <= izi)
                {
                    zbuffer[pz + i] = (short)izi;
                    dest[pdest + i] = color;
                }
            }
        }
    }
}
