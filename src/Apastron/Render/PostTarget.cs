using System;
using Silk.NET.OpenGL;

namespace Apastron.Render;

/// <summary>
/// An offscreen framebuffer whose colour attachment is a <b>texture</b> (so it can be sampled in a
/// post-process shader, unlike <see cref="RenderTarget"/> which is renderbuffer-backed). The scene's
/// resolved colour is blitted here, then read back by the film-grade fullscreen pass.
/// </summary>
public sealed class PostTarget : IDisposable
{
    private readonly GL _gl;
    private uint _fbo, _tex;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Handle => _fbo;
    public uint Texture => _tex;

    public PostTarget(GL gl) => _gl = gl;

    public void Ensure(int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        if (_fbo != 0 && width == Width && height == Height) return;
        Width = width; Height = height;
        Recreate();
    }

    private unsafe void Recreate()
    {
        Destroy();
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _tex, 0);

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            Console.WriteLine($"[post] framebuffer incomplete ({Width}x{Height}): {status}");

        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void Destroy()
    {
        if (_tex != 0) { _gl.DeleteTexture(_tex); _tex = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
    }

    public void Dispose() => Destroy();
}
