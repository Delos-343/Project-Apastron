using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Apastron.Render;

/// <summary>
/// Procedural primitive meshes beyond the sphere — a capped cylinder, a cone, and a box — so vessels
/// can be built from recognizable rocket parts (tube fuselage, nose cone, flared nozzle, flat panels)
/// instead of stacked ellipsoids. All are unit-sized, centred at the origin, with the long axis on +Z
/// and correct outward normals (back-face culling is disabled, so winding is irrelevant; only normals
/// matter for shading). Vertex format matches <see cref="Mesh"/>: interleaved position(3) + normal(3).
/// </summary>
public static class Primitives
{
    /// <summary>Unit cylinder: radius 1, length 1 on Z (z in [-0.5, +0.5]), with end caps.</summary>
    public static Mesh Cylinder(GL gl, int slices = 28)
    {
        var v = new List<float>();
        var ix = new List<uint>();

        // side wall — radial normals
        for (int j = 0; j <= slices; j++)
        {
            double t = 2.0 * Math.PI * j / slices;
            float cx = (float)Math.Cos(t), cy = (float)Math.Sin(t);
            v.AddRange(new[] { cx, cy, -0.5f, cx, cy, 0f });
            v.AddRange(new[] { cx, cy, 0.5f, cx, cy, 0f });
        }
        for (uint j = 0; j < (uint)slices; j++)
        {
            uint b = j * 2;
            ix.Add(b); ix.Add(b + 1); ix.Add(b + 2);
            ix.Add(b + 2); ix.Add(b + 1); ix.Add(b + 3);
        }

        AddCap(v, ix, slices, -0.5f, new[] { 0f, 0f, -1f });
        AddCap(v, ix, slices, 0.5f, new[] { 0f, 0f, 1f });
        return new Mesh(gl, v.ToArray(), ix.ToArray());
    }

    /// <summary>Unit cone: base radius 1 at z=-0.5, apex at z=+0.5, with a base cap.</summary>
    public static Mesh Cone(GL gl, int slices = 28)
    {
        var v = new List<float>();
        var ix = new List<uint>();
        float inv = 1.0f / (float)Math.Sqrt(2.0);   // slant normal has equal radial/axial parts for a unit cone

        for (int j = 0; j < slices; j++)
        {
            double t0 = 2.0 * Math.PI * j / slices;
            double t1 = 2.0 * Math.PI * (j + 1) / slices;
            double tm = 0.5 * (t0 + t1);
            float c0x = (float)Math.Cos(t0), c0y = (float)Math.Sin(t0);
            float c1x = (float)Math.Cos(t1), c1y = (float)Math.Sin(t1);
            float cmx = (float)Math.Cos(tm), cmy = (float)Math.Sin(tm);
            uint s = (uint)(v.Count / 6);
            v.AddRange(new[] { c0x, c0y, -0.5f, c0x * inv, c0y * inv, inv });
            v.AddRange(new[] { c1x, c1y, -0.5f, c1x * inv, c1y * inv, inv });
            v.AddRange(new[] { 0f, 0f, 0.5f, cmx * inv, cmy * inv, inv });
            ix.Add(s); ix.Add(s + 1); ix.Add(s + 2);
        }

        AddCap(v, ix, slices, -0.5f, new[] { 0f, 0f, -1f });
        return new Mesh(gl, v.ToArray(), ix.ToArray());
    }

    /// <summary>
    /// Unit frustum (truncated cone): bottom radius 1 at z=-0.5, top radius <paramref name="rTop"/> at z=+0.5,
    /// with both end caps. Gives a single smooth taper between two radii (rTop=1 -&gt; cylinder, rTop=0 -&gt; cone),
    /// so a tapering hull is one continuous surface instead of a stack of stepped cylinders.
    /// </summary>
    public static Mesh Frustum(GL gl, float rTop, int slices = 28)
    {
        var v = new List<float>();
        var ix = new List<uint>();

        // side slant normal (for bottom radius 1): radial component nr, axial component nz
        float dr = 1.0f - rTop;
        float ln = (float)Math.Sqrt(1.0 + dr * dr);
        float nr = 1.0f / ln, nz = dr / ln;

        for (int j = 0; j <= slices; j++)
        {
            double t = 2.0 * Math.PI * j / slices;
            float cx = (float)Math.Cos(t), cy = (float)Math.Sin(t);
            v.AddRange(new[] { cx, cy, -0.5f, cx * nr, cy * nr, nz });               // bottom rim (r = 1)
            v.AddRange(new[] { cx * rTop, cy * rTop, 0.5f, cx * nr, cy * nr, nz });  // top rim (r = rTop)
        }
        for (uint j = 0; j < (uint)slices; j++)
        {
            uint b = j * 2;
            ix.Add(b); ix.Add(b + 1); ix.Add(b + 2);
            ix.Add(b + 2); ix.Add(b + 1); ix.Add(b + 3);
        }

        AddCap(v, ix, slices, -0.5f, new[] { 0f, 0f, -1f }, 1.0f);
        AddCap(v, ix, slices, 0.5f, new[] { 0f, 0f, 1f }, rTop);
        return new Mesh(gl, v.ToArray(), ix.ToArray());
    }
    public static Mesh Box(GL gl)
    {
        float[] v =
        {
            // +X
             0.5f,-0.5f,-0.5f, 1,0,0,   0.5f, 0.5f,-0.5f, 1,0,0,   0.5f, 0.5f, 0.5f, 1,0,0,   0.5f,-0.5f, 0.5f, 1,0,0,
            // -X
            -0.5f,-0.5f, 0.5f,-1,0,0,  -0.5f, 0.5f, 0.5f,-1,0,0,  -0.5f, 0.5f,-0.5f,-1,0,0,  -0.5f,-0.5f,-0.5f,-1,0,0,
            // +Y
            -0.5f, 0.5f,-0.5f,0,1,0,   -0.5f, 0.5f, 0.5f,0,1,0,    0.5f, 0.5f, 0.5f,0,1,0,    0.5f, 0.5f,-0.5f,0,1,0,
            // -Y
            -0.5f,-0.5f, 0.5f,0,-1,0,  -0.5f,-0.5f,-0.5f,0,-1,0,   0.5f,-0.5f,-0.5f,0,-1,0,   0.5f,-0.5f, 0.5f,0,-1,0,
            // +Z
            -0.5f,-0.5f, 0.5f,0,0,1,    0.5f,-0.5f, 0.5f,0,0,1,    0.5f, 0.5f, 0.5f,0,0,1,   -0.5f, 0.5f, 0.5f,0,0,1,
            // -Z
             0.5f,-0.5f,-0.5f,0,0,-1,  -0.5f,-0.5f,-0.5f,0,0,-1,  -0.5f, 0.5f,-0.5f,0,0,-1,   0.5f, 0.5f,-0.5f,0,0,-1,
        };
        var ix = new uint[36];
        int n = 0;
        for (uint q = 0; q < 6; q++)
        {
            uint b = q * 4;
            ix[n++] = b; ix[n++] = b + 1; ix[n++] = b + 2;
            ix[n++] = b; ix[n++] = b + 2; ix[n++] = b + 3;
        }
        return new Mesh(gl, v, ix);
    }

    private static void AddCap(List<float> v, List<uint> ix, int slices, float z, float[] normal, float radius = 1.0f)
    {
        uint center = (uint)(v.Count / 6);
        v.AddRange(new[] { 0f, 0f, z, normal[0], normal[1], normal[2] });
        uint ringStart = (uint)(v.Count / 6);
        for (int j = 0; j <= slices; j++)
        {
            double t = 2.0 * Math.PI * j / slices;
            v.AddRange(new[] { (float)Math.Cos(t) * radius, (float)Math.Sin(t) * radius, z, normal[0], normal[1], normal[2] });
        }
        for (uint j = 0; j < (uint)slices; j++)
        {
            ix.Add(center);
            if (z > 0f) { ix.Add(ringStart + j); ix.Add(ringStart + j + 1); }
            else        { ix.Add(ringStart + j + 1); ix.Add(ringStart + j); }
        }
    }
}
