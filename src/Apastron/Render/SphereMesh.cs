using System;

namespace Apastron.Render;

/// <summary>Builds a unit-radius UV sphere (position + outward normal interleaved).</summary>
public static class SphereMesh
{
    public static Mesh Create(Silk.NET.OpenGL.GL gl, int stacks = 32, int slices = 48)
    {
        int vertCount = (stacks + 1) * (slices + 1);
        var verts = new float[vertCount * 6];
        int v = 0;

        for (int i = 0; i <= stacks; i++)
        {
            double phi = Math.PI * i / stacks;          // 0..pi from +Y pole
            double sinPhi = Math.Sin(phi), cosPhi = Math.Cos(phi);
            for (int j = 0; j <= slices; j++)
            {
                double theta = 2.0 * Math.PI * j / slices;
                float x = (float)(sinPhi * Math.Cos(theta));
                float y = (float)cosPhi;
                float z = (float)(sinPhi * Math.Sin(theta));
                verts[v++] = x; verts[v++] = y; verts[v++] = z;   // position (unit sphere)
                verts[v++] = x; verts[v++] = y; verts[v++] = z;   // normal == position
            }
        }

        var indices = new uint[stacks * slices * 6];
        int n = 0;
        int row = slices + 1;
        for (int i = 0; i < stacks; i++)
        {
            for (int j = 0; j < slices; j++)
            {
                uint a = (uint)(i * row + j);
                uint b = (uint)((i + 1) * row + j);
                indices[n++] = a;
                indices[n++] = b;
                indices[n++] = a + 1;
                indices[n++] = a + 1;
                indices[n++] = b;
                indices[n++] = b + 1;
            }
        }

        return new Mesh(gl, verts, indices);
    }
}
