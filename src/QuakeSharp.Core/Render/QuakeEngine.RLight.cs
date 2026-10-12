using QuakeSharp.Core.Client;
using QuakeSharp.Core.Model;
using static QuakeSharp.Core.Common.MathLib;
using static QuakeSharp.Core.Common.QString;

namespace QuakeSharp.Core;

// r_light.c: light styles and dynamic lights (the light at a point, R_LightPoint, comes with the renderer)
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
}
