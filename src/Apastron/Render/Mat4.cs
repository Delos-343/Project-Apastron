using System;
using System.Numerics;

namespace Apastron.Render;

/// <summary>
/// Minimal 4x4 matrix helpers returning <b>column-major</b> float[16] arrays — the
/// native layout OpenGL expects with <c>transpose = false</c>. We build the matrices
/// with explicit OpenGL conventions (right-handed, -Z forward, clip z in [-1, 1])
/// rather than using System.Numerics.Matrix4x4, whose perspective uses a DirectX-style
/// depth range that would break GL depth testing. Matrices are combined in the shader
/// as proj * view * model (column-vector convention).
/// </summary>
public static class Mat4
{
    /// <summary>Perspective projection. <paramref name="fovY"/> in radians.</summary>
    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        float f = 1.0f / MathF.Tan(fovY * 0.5f);
        var m = new float[16];
        m[0]  = f / aspect;
        m[5]  = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1.0f;
        m[14] = (2.0f * far * near) / (near - far);
        return m;
    }

    /// <summary>Right-handed look-at view matrix.</summary>
    public static float[] LookAt(Vector3 eye, Vector3 center, Vector3 up)
    {
        Vector3 fwd = Vector3.Normalize(center - eye);
        Vector3 side = Vector3.Normalize(Vector3.Cross(fwd, up));
        Vector3 trueUp = Vector3.Cross(side, fwd);

        var m = new float[16];
        m[0] = side.X;   m[1] = trueUp.X;   m[2]  = -fwd.X;
        m[4] = side.Y;   m[5] = trueUp.Y;   m[6]  = -fwd.Y;
        m[8] = side.Z;   m[9] = trueUp.Z;   m[10] = -fwd.Z;
        m[12] = -Vector3.Dot(side, eye);
        m[13] = -Vector3.Dot(trueUp, eye);
        m[14] =  Vector3.Dot(fwd, eye);
        m[15] = 1.0f;
        return m;
    }

    /// <summary>Translation combined with a uniform scale (no rotation).</summary>
    public static float[] TranslationScale(Vector3 t, float scale)
    {
        var m = new float[16];
        m[0]  = scale;
        m[5]  = scale;
        m[10] = scale;
        m[12] = t.X;
        m[13] = t.Y;
        m[14] = t.Z;
        m[15] = 1.0f;
        return m;
    }

    /// <summary>
    /// Object-to-world matrix from an orthonormal basis (right/up/fwd), a position, and a
    /// per-axis scale. Columns hold the scaled basis vectors; the translation is the position.
    /// A local point (x,y,z) maps to x*right*sx + y*up*sy + z*fwd*sz + pos.
    /// </summary>
    public static float[] ModelAxes(Vector3 pos, Vector3 right, Vector3 up, Vector3 fwd, Vector3 scale)
    {
        var m = new float[16];
        m[0] = right.X * scale.X; m[1] = right.Y * scale.X; m[2]  = right.Z * scale.X;
        m[4] = up.X    * scale.Y; m[5] = up.Y    * scale.Y; m[6]  = up.Z    * scale.Y;
        m[8] = fwd.X   * scale.Z; m[9] = fwd.Y   * scale.Z; m[10] = fwd.Z   * scale.Z;
        m[12] = pos.X; m[13] = pos.Y; m[14] = pos.Z;
        m[15] = 1.0f;
        return m;
    }

    /// <summary>Column-major product C = A * B (applies B then A under column-vector convention).</summary>
    public static float[] Multiply(float[] a, float[] b)
    {
        var c = new float[16];
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                float s = 0.0f;
                for (int k = 0; k < 4; k++)
                    s += a[k * 4 + i] * b[j * 4 + k];
                c[j * 4 + i] = s;
            }
        return c;
    }
}
