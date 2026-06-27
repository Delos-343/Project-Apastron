using System;
using Silk.NET.OpenGL;

namespace Apastron.Render;

/// <summary>
/// A dynamic, position-only vertex buffer for drawing line strips (orbit paths). The
/// data is re-uploaded each frame because the orbit changes whenever the vessel thrusts.
/// </summary>
public sealed class LineBatch : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo;
    private int _count;
    private readonly float[] _seg = new float[6];   // reused for single-segment uploads (no per-call alloc)

    public unsafe LineBatch(GL gl)
    {
        _gl = gl;
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        _gl.BindVertexArray(0);
    }

    /// <summary>Upload an interleaved x,y,z float array (3 floats per vertex).</summary>
    public unsafe void Upload(float[] data)
    {        _count = data.Length / 3;
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
    }

    /// <summary>Upload a single line segment (two endpoints) without allocating - the streak/beam/tracer hot
    /// path calls this per entity per frame. Safe because Upload copies into the VBO immediately.</summary>
    public void Upload(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        _seg[0] = a.X; _seg[1] = a.Y; _seg[2] = a.Z;
        _seg[3] = b.X; _seg[4] = b.Y; _seg[5] = b.Z;
        Upload(_seg);
    }

    public void DrawStrip()
    {
        if (_count < 2) return;
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.LineStrip, 0, (uint)_count);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
