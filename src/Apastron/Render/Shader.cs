using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Apastron.Render;

/// <summary>A compiled+linked GL program with cached uniform locations.</summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _program;
    private readonly Dictionary<string, int> _uniforms = new();

    public Shader(GL gl, string vertexSrc, string fragmentSrc)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSrc);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSrc);

        _program = _gl.CreateProgram();
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        _gl.LinkProgram(_program);

        _gl.GetProgram(_program, GLEnum.LinkStatus, out int linked);
        if (linked == 0)
            throw new Exception($"Program link failed: {_gl.GetProgramInfoLog(_program)}");

        _gl.DetachShader(_program, vs);
        _gl.DetachShader(_program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    public void Use() => _gl.UseProgram(_program);

    private int Loc(string name)
    {
        if (_uniforms.TryGetValue(name, out int cached)) return cached;
        int loc = _gl.GetUniformLocation(_program, name);
        _uniforms[name] = loc;
        return loc;
    }

    public unsafe void SetMatrix(string name, float[] m)
    {
        fixed (float* p = m)
            _gl.UniformMatrix4(Loc(name), 1, false, p);
    }

    public void SetVec3(string name, float x, float y, float z) => _gl.Uniform3(Loc(name), x, y, z);
    public void SetVec4(string name, float x, float y, float z, float w) => _gl.Uniform4(Loc(name), x, y, z, w);
    public void SetVec2(string name, float x, float y) => _gl.Uniform2(Loc(name), x, y);
    public void SetFloat(string name, float v) => _gl.Uniform1(Loc(name), v);

    private uint Compile(ShaderType type, string src)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, src);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Dispose() => _gl.DeleteProgram(_program);
}
