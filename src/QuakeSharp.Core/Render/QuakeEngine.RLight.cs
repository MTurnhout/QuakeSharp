using QuakeSharp.Core.Client;
using QuakeSharp.Core.Common;
using QuakeSharp.Core.Model;
using QuakeSharp.Core.Numerics;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;
using static QuakeSharp.Core.Model.BspFile;

namespace QuakeSharp.Core;

// r_light.c: light styles, dynamic lights and the light at a point
public sealed partial class QuakeEngine
{
    public int r_dlightframecount;

    // r_light.c: R_AnimateLight
    public void R_AnimateLight()
    {
        int i, j, k;

        // light animations
        // 'm' is normal light, 'a' is no light, 'z' is double bright
        i = (int)(cl.time * 10);
        for (j = 0; j < QuakeDef.MAX_LIGHTSTYLES; j++)
        {
            if (cl_lightstyle[j].length == 0)
            {
                d_lightstylevalue[j] = 256;
                continue;
            }

            k = i % cl_lightstyle[j].length;
            k = Ch(cl_lightstyle[j].map, k) - 'a';
            k *= 22;
            d_lightstylevalue[j] = k;
        }
    }

    // =============================================================================
    // DYNAMIC LIGHTS
    // =============================================================================

    // r_light.c: R_MarkLights
    public void R_MarkLights(Dlight light, int bit, MNodeBase nodebase)
    {
        MPlane splitplane;
        float dist;
        MSurface surf;
        int i;

        if (nodebase.contents < 0)
        {
            return;
        }

        var node = (MNode)nodebase;
        splitplane = node.plane;
        dist = DotProduct(light.origin, splitplane.normal) - splitplane.dist;

        if (dist > light.radius)
        {
            R_MarkLights(light, bit, node.children[0]);
            return;
        }

        if (dist < -light.radius)
        {
            R_MarkLights(light, bit, node.children[1]);
            return;
        }

        // mark the polygons
        for (i = 0; i < node.numsurfaces; i++)
        {
            surf = cl.worldmodel!.surfaces[node.firstsurface + i];
            if (surf.dlightframe != r_dlightframecount)
            {
                surf.dlightbits = 0;
                surf.dlightframe = r_dlightframecount;
            }

            surf.dlightbits |= bit;
        }

        R_MarkLights(light, bit, node.children[0]);
        R_MarkLights(light, bit, node.children[1]);
    }

    // r_light.c: R_PushDlights
    public void R_PushDlights()
    {
        int i;
        Dlight l;

        r_dlightframecount = r_framecount + 1; // because the count hasn't advanced yet for this frame

        for (i = 0; i < MAX_DLIGHTS; i++)
        {
            l = cl_dlights[i];
            if (l.die < cl.time || l.radius == 0)
            {
                continue;
            }

            R_MarkLights(l, 1 << i, cl.worldmodel!.nodes[0]);
        }
    }

    // =============================================================================
    // LIGHT SAMPLING
    // =============================================================================

    // r_light.c: RecursiveLightPoint
    public int RecursiveLightPoint(MNodeBase nodebase, in Vec3 start, in Vec3 end)
    {
        int r;
        float front, back, frac;
        int side;
        MPlane plane;
        Vec3 mid = default;
        MSurface surf;
        int s, t, ds, dt;
        int i;
        MTexinfo tex;
        int lightmap;
        uint scale;
        int maps;

        if (nodebase.contents < 0)
        {
            return -1; // didn't hit anything
        }

        var node = (MNode)nodebase;

        // calculate mid point

        // FIXME: optimize for axial
        plane = node.plane;
        front = DotProduct(start, plane.normal) - plane.dist;
        back = DotProduct(end, plane.normal) - plane.dist;
        side = front < 0 ? 1 : 0;

        if ((back < 0 ? 1 : 0) == side)
        {
            return RecursiveLightPoint(node.children[side], start, end);
        }

        frac = front / (front - back);
        mid[0] = start[0] + ((end[0] - start[0]) * frac);
        mid[1] = start[1] + ((end[1] - start[1]) * frac);
        mid[2] = start[2] + ((end[2] - start[2]) * frac);

        // go down front side
        r = RecursiveLightPoint(node.children[side], start, mid);
        if (r >= 0)
        {
            return r; // hit something
        }

        if ((back < 0 ? 1 : 0) == side)
        {
            return -1; // didn't hit anuthing
        }

        // check for impact on this node
        for (i = 0; i < node.numsurfaces; i++)
        {
            surf = cl.worldmodel!.surfaces[node.firstsurface + i];
            if ((surf.flags & MSurface.SURF_DRAWTILED) != 0)
            {
                continue; // no lightmaps
            }

            tex = surf.texinfo;

            s = X86.FtoI((mid[0] * tex.vecs[0][0]) + (mid[1] * tex.vecs[0][1]) + (mid[2] * tex.vecs[0][2]) + tex.vecs[0][3]);
            t = X86.FtoI((mid[0] * tex.vecs[1][0]) + (mid[1] * tex.vecs[1][1]) + (mid[2] * tex.vecs[1][2]) + tex.vecs[1][3]);

            if (s < surf.texturemins[0] ||
                t < surf.texturemins[1])
            {
                continue;
            }

            ds = s - surf.texturemins[0];
            dt = t - surf.texturemins[1];

            if (ds > surf.extents[0] || dt > surf.extents[1])
            {
                continue;
            }

            if (surf.samples < 0)
            {
                return 0;
            }

            ds >>= 4;
            dt >>= 4;

            lightmap = surf.samples;
            r = 0;

            byte[] lightdata = cl.worldmodel.lightdata!;
            lightmap += (dt * ((surf.extents[0] >> 4) + 1)) + ds;

            for (maps = 0; maps < MAXLIGHTMAPS && surf.styles[maps] != 255; maps++)
            {
                scale = (uint)d_lightstylevalue[surf.styles[maps]];
                r = (int)(r + (lightdata[lightmap] * scale));
                lightmap += ((surf.extents[0] >> 4) + 1) *
                    ((surf.extents[1] >> 4) + 1);
            }

            r >>= 8;

            return r;
        }

        // go down back side
        return RecursiveLightPoint(node.children[side == 0 ? 1 : 0], mid, end);
    }

    // r_light.c: R_LightPoint
    public int R_LightPoint(in Vec3 p)
    {
        Vec3 end = default;
        int r;

        if (cl.worldmodel!.lightdata == null)
        {
            return 255;
        }

        end[0] = p[0];
        end[1] = p[1];
        end[2] = p[2] - 2048;

        r = RecursiveLightPoint(cl.worldmodel.nodes[0], p, end);

        if (r == -1)
        {
            r = 0;
        }

        if (r < r_refdef.ambientlight)
        {
            r = r_refdef.ambientlight;
        }

        return r;
    }
}
