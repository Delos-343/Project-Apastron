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
    {
        _count = data.Length / 3;
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
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
