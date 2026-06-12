using System;
using Silk.NET.OpenGL;

namespace Apastron.Render;

/// <summary>
/// An offscreen framebuffer backed by renderbuffers (never sampled as a texture — we only
/// ever <see cref="BlitColorTo"/> it). Optionally multisampled and/or depth-backed.
///
/// This is the backbone of two quality settings: <b>render scale</b> (render the scene at
/// a resolution independent of the window, then blit-scale to the screen) and <b>MSAA</b>
/// (a multisampled target resolved before the final scale blit). Because the default
/// framebuffer stays single-sample, the resolve→scale path is always a valid blit.
/// </summary>
public sealed class RenderTarget : IDisposable
{
    private readonly GL _gl;
    private readonly bool _withDepth;
    private uint _fbo, _color, _depth;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Samples { get; private set; }
    public uint Handle => _fbo;

    public RenderTarget(GL gl, bool withDepth)
    {
        _gl = gl;
        _withDepth = withDepth;
    }

    /// <summary>(Re)allocate storage only if the size or sample count changed.</summary>
    public void Ensure(int width, int height, int samples)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        if (samples < 1) samples = 1;
        if (_fbo != 0 && width == Width && height == Height && samples == Samples)
            return;

        Width = width; Height = height; Samples = samples;
        Recreate();
    }

    private void Recreate()
    {
        Destroy();

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _color = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
        if (Samples > 1)
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)Samples,
                InternalFormat.Rgba8, (uint)Width, (uint)Height);
        else
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                InternalFormat.Rgba8, (uint)Width, (uint)Height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _color);

        if (_withDepth)
        {
            _depth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            if (Samples > 1)
                _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)Samples,
                    InternalFormat.DepthComponent24, (uint)Width, (uint)Height);
            else
                _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                    InternalFormat.DepthComponent24, (uint)Width, (uint)Height);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
        }

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            Console.WriteLine($"[render] framebuffer incomplete ({Width}x{Height} x{Samples}): {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Bind() => _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

    /// <summary>
    /// Blit this target's colour into <paramref name="destFbo"/> (0 = the screen). When the
    /// source is multisampled and the destination is the same size, this resolves MSAA;
    /// when sizes differ (and source is single-sample) it scales.
    /// </summary>
    public void BlitColorTo(uint destFbo, int destW, int destH, bool linear)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destFbo);
        _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, destW, destH,
            (uint)ClearBufferMask.ColorBufferBit,
            linear ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void Destroy()
    {
        if (_color != 0) { _gl.DeleteRenderbuffer(_color); _color = 0; }
        if (_depth != 0) { _gl.DeleteRenderbuffer(_depth); _depth = 0; }
        if (_fbo != 0)   { _gl.DeleteFramebuffer(_fbo);   _fbo = 0; }
    }

    public void Dispose() => Destroy();
}
