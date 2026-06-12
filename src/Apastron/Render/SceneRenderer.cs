using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Apastron.Config;
using Apastron.Combat;
using Apastron.Core;
using Apastron.Diagnostics;
using Apastron.Physics;
using Apastron.Simulation;

namespace Apastron.Render;

/// <summary>
/// Draws the 3D scene: shaded sphere bodies, a vessel marker, and the predicted orbit.
///
/// Two techniques underpin it. (1) A <b>floating origin</b>: world state lives in double
/// precision at scales up to ~10^9 m, where 32-bit float (what the GPU consumes) would
/// lose all useful resolution. Every position is shifted so the camera focus sits at the
/// origin, then scaled to render units (set per scenario) and cast to float, so
/// rendered coordinates stay small regardless of absolute world position. (2) An
/// <b>offscreen pipeline</b> (see <see cref="RenderTarget"/>): the scene renders into an
/// optionally-multisampled buffer sized by the render-scale setting, is resolved, then
/// blit-scaled to the screen. This is where the graphics quality settings take effect.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    /// <summary>Metres-to-render-units factor, set each frame from the scenario's hint.</summary>
    private double _scale = 1.0e-6;

    private const float Near = 0.1f;

    private readonly GL _gl;
    private readonly Shader _meshShader;
    private readonly Shader _planetShader;
    private readonly Shader _lineShader;
    private readonly LineBatch _lines;
    private readonly Camera _camera = new();
    private readonly Vector3 _light = Vector3.Normalize(new Vector3(0.4f, 0.6f, 0.7f));

    private readonly RenderTarget _sceneTarget;     // depth-backed, possibly multisampled
    private readonly RenderTarget _resolveTarget;   // single-sample, colour only (MSAA resolve)
    private readonly PostTarget _postTarget;        // texture-backed, sampled by the film grade
    private Shader? _post;                           // film-grade fullscreen pass (null if it failed to compile)
    private uint _postVao;
    private bool _postOk;
    private readonly int _maxSamples;

    private Mesh _sphere;
    private Mesh _cyl = null!;
    private Mesh _cone = null!;
    private Mesh _box = null!;
    private Mesh _noseFrustum = null!;
    private QualityTier? _sphereTier;

    private Shader? _plumeShader;   // additive plasma drive plume (null if it failed to compile)

    /// <summary>One queued engine-plume glow cone (drawn after all opaque geometry).</summary>
    private struct PlumeDraw
    {
        public Vector3 Pos, Right, Up, Aft, Color;
        public float Radius, Length, Intensity;
    }
    private readonly List<PlumeDraw> _plumes = new();
    private bool _plumesEnabled = true;   // mirrors ViewSettings.ShowEnginePlume each frame
    private bool _additiveOk = true;      // additive plume shader allowed this frame (settings + driver safe mode)

    private Vector2 _lastMouse;
    private bool _dragging;
    private bool _wasDown;
    private Vector2 _downPos;
    private bool _moved;
    private bool _clickPending;
    private Vector2 _clickPos;

    // last frame's view-projection (column-major), viewport, focus and render scale, for screen-space
    // vessel picking (so a left-click without a drag selects the ship under the cursor).
    private float[]? _pickVp;
    private float _pickW = 1.0f, _pickH = 1.0f;
    private Vec3 _pickFocus;
    private double _pickScale = 1.0e-6;

    public SceneRenderer(GL gl)
    {
        _gl = gl;
        _meshShader = MakeMeshShader(gl);
        _planetShader = MakePlanetShader(gl);
        _lineShader = new Shader(gl, LineVert, LineFrag);
        _lines = new LineBatch(gl);
        _sphere = SphereMesh.Create(gl, 32, 48);
        _cyl = Primitives.Cylinder(gl);
        _cone = Primitives.Cone(gl);
        _box = Primitives.Box(gl);
        _noseFrustum = Primitives.Frustum(gl, 0.43f);   // smooth bow taper: top radius is 43% of the base

        // Additive plume shader: guarded like the film grade, so a driver-specific compile failure
        // falls back to the solid emissive plume rather than breaking the build.
        try { _plumeShader = new Shader(gl, PlumeVert, PlumeFrag); }
        catch (Exception e)
        {
            Console.WriteLine($"[plume] additive plume disabled (shader error): {e.Message}");
            _plumeShader = null;
        }

        _sceneTarget = new RenderTarget(gl, withDepth: true);
        _resolveTarget = new RenderTarget(gl, withDepth: false);
        _postTarget = new PostTarget(gl);

        // The film-grade pass is optional: if the shader fails to compile on this GPU, disable it
        // rather than taking down the whole renderer.
        try
        {
            _post = new Shader(gl, PostVert, PostFrag);
            _postVao = _gl.GenVertexArray();
            _postOk = true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[post] film-grade disabled (shader error): {e.Message}");
            _post = null;
            _postOk = false;
        }

        _gl.GetInteger((GetPName)GLEnum.MaxSamples, out int maxSamples);   // GL_MAX_SAMPLES isn't in Silk.NET's GetPName
        _maxSamples = Math.Max(maxSamples, 1);
    }

    /// <summary>
    /// Build the planet shader, falling back to a plain lit sphere if the full version (graticule,
    /// continental mottling, fresnel atmosphere) fails to compile on this GPU. The fallback uses the
    /// same uniforms the renderer sets, so the body still renders rather than the app failing to start.
    /// </summary>
    private static Shader MakePlanetShader(GL gl)
    {
        try { return new Shader(gl, PlanetVert, PlanetFrag); }
        catch (Exception e)
        {
            Console.WriteLine($"[render] planet shader failed, using flat fallback: {e.Message}");
            return new Shader(gl, BasicPlanetVert, BasicPlanetFrag);
        }
    }

    /// <summary>Build the lit mesh shader, falling back to flat diffuse if the specular/rim/emissive
    /// version fails to compile on this GPU, so vessels still render rather than the app failing to start.</summary>
    private static Shader MakeMeshShader(GL gl)
    {
        try { return new Shader(gl, MeshVert, MeshFrag); }
        catch (Exception e)
        {
            Console.WriteLine($"[render] mesh shader failed, using flat fallback: {e.Message}");
            return new Shader(gl, BasicMeshVert, BasicMeshFrag);
        }
    }

    public void Zoom(float steps) => _camera.Zoom(steps);

    /// <summary>Nudge the orbit camera (used for the slow idle spin on the title screen).</summary>
    public void OrbitCamera(float dYaw, float dPitch) => _camera.Rotate(dYaw, dPitch);

    /// <summary>Apply left-drag camera rotation when the UI is not capturing the mouse.</summary>
    public void HandleDrag(IMouse mouse, bool allowed)
    {
        Vector2 pos = mouse.Position;
        bool down = mouse.IsButtonPressed(MouseButton.Left);

        if (down && !_wasDown) { _downPos = pos; _moved = false; }     // press
        if (allowed && down && _dragging)
        {
            Vector2 d = pos - _lastMouse;
            _camera.Rotate(d.X * 0.01f, d.Y * 0.01f);
        }
        if (down && (pos - _downPos).Length() > 4.0f) _moved = true;    // movement => it's a drag, not a click
        if (!down && _wasDown && allowed && !_moved) { _clickPending = true; _clickPos = pos; }   // released cleanly

        _dragging = down && allowed;
        _wasDown = down;
        _lastMouse = pos;
    }

    /// <summary>True once for a left-click that was not a camera drag; yields the click position (px).</summary>
    public bool TryConsumeClick(out Vector2 pos)
    {
        pos = _clickPos;
        if (!_clickPending) return false;
        _clickPending = false;
        return true;
    }

    /// <summary>Project every vessel with the last frame's camera and return the index of the one nearest
    /// the cursor within a pixel threshold, or -1 if the click missed all of them.</summary>
    public int PickVessel(Vector2 mouse, PhysicsWorld world)
    {
        if (_pickVp == null) return -1;
        int best = -1; float bestD = 38.0f;   // selection tolerance in pixels
        for (int i = 0; i < world.Vessels.Count; i++)
        {
            Vec3 w = world.Vessels[i].Position;
            float rx = (float)((w.X - _pickFocus.X) * _pickScale);
            float ry = (float)((w.Y - _pickFocus.Y) * _pickScale);
            float rz = (float)((w.Z - _pickFocus.Z) * _pickScale);
            float cw = _pickVp[3] * rx + _pickVp[7] * ry + _pickVp[11] * rz + _pickVp[15];
            if (cw <= 1e-4f) continue;                                  // behind the camera
            float cx = _pickVp[0] * rx + _pickVp[4] * ry + _pickVp[8]  * rz + _pickVp[12];
            float cy = _pickVp[1] * rx + _pickVp[5] * ry + _pickVp[9]  * rz + _pickVp[13];
            float sx = (cx / cw * 0.5f + 0.5f) * _pickW;
            float sy = (1.0f - (cy / cw * 0.5f + 0.5f)) * _pickH;
            float d = (new Vector2(sx, sy) - mouse).Length();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public void Render(PhysicsWorld world, ViewSettings view, GraphicsSettings graphics,
                       Vector2D<int> framebuffer, ManeuverPreview? maneuver = null, CombatManager? combat = null)
    {
        int fbW = Math.Max(framebuffer.X, 1);
        int fbH = Math.Max(framebuffer.Y, 1);
        _plumesEnabled = view.ShowEnginePlume;
        _additiveOk = graphics.AdditivePlumes && !graphics.DriverSafeMode;
        BurnTrace.Mark("scene: begin");

        // --- quality settings -> render target size + samples ---
        double scale = Math.Clamp(graphics.RenderScale, 0.25, 2.0);
        int rw = Math.Max((int)Math.Round(fbW * scale), 1);
        int rh = Math.Max((int)Math.Round(fbH * scale), 1);
        int samples = ClampSamples(graphics.MsaaSamples);

        EnsureSphereLod(graphics.TextureQuality);
        _sceneTarget.Ensure(rw, rh, samples);

        // --- draw the scene into the offscreen target ---
        _sceneTarget.Bind();
        _gl.Viewport(0, 0, (uint)rw, (uint)rh);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        if (samples > 1) _gl.Enable(EnableCap.Multisample);
        _gl.ClearColor(0.02f, 0.02f, 0.035f, 1.0f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (view.ResetRequested) { _camera.Reset(); view.ResetRequested = false; }
        view.Distance = _camera.Distance;
        _scale = world.RenderScaleHint > 0.0 ? world.RenderScaleHint : 1.0e-6;

        RigidBody? vessel = world.PrimaryVessel;
        RigidBody? target = world.TargetVessel;
        bool inCombat = combat != null && combat.Combatants.Count > 0;
        CelestialBody? primary = world.DominantBody(vessel?.Position ?? Vec3.Zero);

        Vec3 focus;
        if (view.FocusVesselIndex >= 0 && view.FocusVesselIndex < world.Vessels.Count)
            focus = world.Vessels[view.FocusVesselIndex].Position;   // Homeworld-style: locked to a chosen ship
        else
            focus = view.Focus switch
            {
                CameraFocus.Vessel when vessel != null => vessel.Position,
                CameraFocus.Target when target != null => target.Position,
                _ => primary?.Position ?? vessel?.Position ?? Vec3.Zero,
            };

        float aspect = (float)rw / rh;
        float far = Math.Clamp((float)(graphics.MaxRenderDistance * _scale),
                               _camera.Distance * 2.0f + 10.0f, 1.0e7f);
        float[] proj = Mat4.Perspective(view.FovDegrees * (float)MathConstants.DegToRad, aspect, Near, far);
        float[] vmat = _camera.View();

        // view-projection for CPU-side frustum culling (planes live in render space)
        float[] vp = Mat4.Multiply(proj, vmat);
        Vector4[] frustum = ExtractFrustum(vp);
        _drawn = 0; _culled = 0;

        // remember this frame's camera for next-frame click picking (mouse coords are in framebuffer px)
        _pickVp = vp; _pickW = fbW; _pickH = fbH; _pickFocus = focus; _pickScale = _scale;

        // gravitating bodies (procedural surface: lighting + lat/long graticule)
        _planetShader.Use();
        _planetShader.SetMatrix("uProj", proj);
        _planetShader.SetMatrix("uView", vmat);
        _planetShader.SetVec3("uLightDir", _light.X, _light.Y, _light.Z);

        foreach (CelestialBody b in world.Bodies)
        {
            Vector3 c = ToRender(b.Position, focus);
            float radius = (float)(b.Radius * _scale);
            if (!InFrustum(frustum, c, radius)) { _culled++; continue; }
            _planetShader.SetMatrix("uModel", Mat4.TranslationScale(c, radius));
            _planetShader.SetVec3("uColor", b.Color.R, b.Color.G, b.Color.B);
            _planetShader.SetFloat("uAmbient", 0.10f);
            _sphere.Draw();
            _drawn++;
        }

        BurnTrace.Mark("scene: planets done");
        // ships + stations use the simple mesh shader
        _meshShader.Use();
        _meshShader.SetMatrix("uProj", proj);
        _meshShader.SetMatrix("uView", vmat);
        _meshShader.SetVec3("uLightDir", _light.X, _light.Y, _light.Z);
        Vector3 eye = _camera.Eye();
        _meshShader.SetVec3("uViewPos", eye.X, eye.Y, eye.Z);
        _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);

        float markerScale = MathF.Max(_camera.Distance * 0.009f, 0.03f);

        // In a live engagement, draw every combatant as a role-tinted oriented hull (player cyan,
        // enemy red) instead of the flight nav-markers, so the two warships read clearly as ships.
        if (inCombat)
        {
            foreach (Combatant cb in combat!.Combatants)
            {
                if (!cb.Alive) continue;
                Vector3 c = ToRender(cb.Body.Position, focus);
                if (!InFrustum(frustum, c, markerScale * 3.0f)) { _culled++; continue; }
                Vector3 tint = cb.IsPlayer ? new Vector3(0.45f, 0.80f, 1.00f) : new Vector3(1.00f, 0.42f, 0.38f);
                DrawHull(c, cb.Body, markerScale, tint);
                _drawn++;
            }
        }
        else
        {
            // primary vessel: a large, stylized "subject" hull rendered at a fixed world size — the
            // ship is the focus of the scene, not a tiny distance-tracked nav-marker.
            if (vessel != null && view.ShowVesselMarker)
            {
                float shipScale = 1.0f;   // fixed render size; the scale is deliberately non-physical
                Vector3 c = ToRender(vessel.Position, focus);
                // If we are at a low altitude (e.g. an old save), lift the oversized hull radially
                // outward so it floats above the limb instead of intersecting the surface.
                CelestialBody? near = world.DominantBody(vessel.Position);
                if (near != null)
                {
                    Vector3 bodyC = ToRender(near.Position, focus);
                    Vector3 radial = c - bodyC;
                    float dist = radial.Length();
                    float surfaceR = (float)(near.Radius * _scale);
                    float minClear = surfaceR + shipScale * 2.2f;
                    if (dist > 1e-5f && dist < minClear)
                        c = bodyC + radial * (minClear / dist);
                }
                if (InFrustum(frustum, c, shipScale * 3.0f)) { DrawHull(c, vessel, shipScale); _drawn++; }
                else _culled++;
            }

            // companion vessels (slot 1+): spin stations get the full habitat model and always render;
            // plain practice targets are green markers gated by the marker toggle
            for (int vi = 1; vi < world.Vessels.Count; vi++)
            {
                RigidBody t = world.Vessels[vi];
                if (!t.IsStation && !view.ShowVesselMarker) continue;
                Vector3 c = ToRender(t.Position, focus);
                if (InFrustum(frustum, c, markerScale * 3.0f))
                {
                    if (t.SpinRadius > 0.0)
                    {
                        CrashLog.Phase("first station draw");
                        DrawStation(c, t, markerScale, world.SimTime);
                    }
                    else
                    {
                        _meshShader.SetMatrix("uModel", Mat4.TranslationScale(c, markerScale * 0.92f));
                        _meshShader.SetVec3("uColor", 0.30f, 0.90f, 0.50f);
                        _meshShader.SetFloat("uAmbient", 1.0f);
                        _sphere.Draw();
                    }
                    _drawn++;
                }
                else _culled++;
            }
        }

        BurnTrace.Mark("scene: hulls+companions done");
        // predicted orbit
        if (vessel != null && primary != null && view.ShowOrbitPath)
        {
            var pts = OrbitPath.Compute(vessel.Position, vessel.Velocity, primary.Position, primary.Mu);
            if (pts.Count >= 2)
            {
                CrashLog.Phase("first orbit-path draw (vessel)");
                var data = OrbitVertices(pts, focus);
                _lineShader.Use();
                _lineShader.SetMatrix("uProj", proj);
                _lineShader.SetMatrix("uView", vmat);
                _lineShader.SetVec4("uColor", 0.25f, 0.78f, 0.95f, 1.0f);
                _gl.LineWidth(1.5f);
                _lines.Upload(data);
                _lines.DrawStrip();
            }
        }

        BurnTrace.Mark("scene: vessel path done");
        // target orbit (green)
        if (target != null && primary != null && view.ShowOrbitPath)
        {
            var pts = OrbitPath.Compute(target.Position, target.Velocity, primary.Position, primary.Mu);
            if (pts.Count >= 2)
            {
                var data = OrbitVertices(pts, focus);
                _lineShader.Use();
                _lineShader.SetMatrix("uProj", proj);
                _lineShader.SetMatrix("uView", vmat);
                _lineShader.SetVec4("uColor", 0.30f, 0.85f, 0.50f, 1.0f);
                _gl.LineWidth(1.5f);
                _lines.Upload(data);
                _lines.DrawStrip();
            }
        }

        BurnTrace.Mark("scene: target path done");
        // maneuver node + predicted post-burn orbit (yellow)
        if (maneuver != null && maneuver.Active)
        {
            if (maneuver.HasPost && maneuver.Path.Count >= 2)
            {
                var data = new float[maneuver.Path.Count * 3];
                int idx = 0;
                for (int i = 0; i < maneuver.Path.Count; i++)
                {
                    Vector3 rp = ToRender(maneuver.Path[i], focus);
                    data[idx++] = rp.X; data[idx++] = rp.Y; data[idx++] = rp.Z;
                }
                _lineShader.Use();
                _lineShader.SetMatrix("uProj", proj);
                _lineShader.SetMatrix("uView", vmat);
                _lineShader.SetVec4("uColor", 1.0f, 0.85f, 0.2f, 1.0f);
                _gl.LineWidth(1.5f);
                _lines.Upload(data);
                _lines.DrawStrip();
            }

            Vector3 nodeC = ToRender(maneuver.NodeWorld, focus);
            float nodeR = MathF.Max(_camera.Distance * 0.010f, 0.025f);
            _meshShader.Use();
            _meshShader.SetMatrix("uProj", proj);
            _meshShader.SetMatrix("uView", vmat);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(nodeC, nodeR));
            _meshShader.SetVec3("uColor", 1.0f, 0.85f, 0.2f);
            _meshShader.SetFloat("uAmbient", 1.0f);
            _sphere.Draw();
        }

        BurnTrace.Mark("scene: preview drawn");
        // --- live combat: munition streaks and laser beams ---
        if (combat != null)
        {
            _lineShader.Use();
            _lineShader.SetMatrix("uProj", proj);
            _lineShader.SetMatrix("uView", vmat);
            _gl.LineWidth(1.5f);

            foreach (Munition m in combat.Munitions)
            {
                if (!m.Alive) continue;
                double streakSec = m.Kind == MunitionKind.Missile ? 1.2 : 0.5;
                Vec3 tail = m.Position - m.Velocity * streakSec;
                Vector3 a = ToRender(tail, focus);
                Vector3 b = ToRender(m.Position, focus);
                if (m.Kind == MunitionKind.Missile) _lineShader.SetVec4("uColor", 1.0f, 0.55f, 0.15f, 1.0f);
                else _lineShader.SetVec4("uColor", 1.0f, 0.95f, 0.6f, 1.0f);
                _lines.Upload(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z });
                _lines.DrawStrip();
            }

            foreach (var beam in combat.Beams)
            {
                Vector3 a = ToRender(beam.From, focus);
                Vector3 b = ToRender(beam.To, focus);
                _lineShader.SetVec4("uColor", beam.R, beam.G, beam.B, 1.0f);
                _lines.Upload(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z });
                _lines.DrawStrip();
            }

            // point-defense intercepts: gun tracers / PD-laser beams, dimming as they age
            _gl.LineWidth(2.5f);
            foreach (PdTracer t in combat.Tracers)
            {
                float k = t.Life > 0.0 ? (float)Math.Clamp(t.Ttl / t.Life, 0.15, 1.0) : 1.0f;
                Vector3 a = ToRender(t.From, focus);
                Vector3 b = ToRender(t.To, focus);
                _lineShader.SetVec4("uColor", t.R * k, t.G * k, t.B * k, 1.0f);
                _lines.Upload(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z });
                _lines.DrawStrip();
            }
            _gl.LineWidth(1.5f);
        }

        BurnTrace.Mark("scene: streaks done");
        // additive engine plumes, drawn after all opaque geometry so they composite like light
        DrawPlumes(proj, vmat);
        LatchGlError("plumes");
        BurnTrace.Mark("scene: plumes done");

        _gl.BindVertexArray(0);

        bool post = graphics.FilmGrade && !graphics.DriverSafeMode && _postOk && _post != null;
        if (post)
        {
            // resolve/copy the scene colour into a sampleable texture, then run the film-grade
            // fullscreen pass straight to the screen (the linear-sampled texture also does the upscale)
            _postTarget.Ensure(rw, rh);
            _sceneTarget.BlitColorTo(_postTarget.Handle, rw, rh, linear: false);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(0, 0, (uint)fbW, (uint)fbH);
            _gl.Disable(EnableCap.DepthTest);
            _post!.Use();
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _postTarget.Texture);
            _post.SetVec2("uResolution", rw, rh);
            _post.SetFloat("uTime", (float)((Environment.TickCount64 % 100000L) * 0.001));
            _post.SetFloat("uAmount", 0.85f);
            _gl.BindVertexArray(_postVao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3u);
            _gl.BindVertexArray(0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }
        else if (samples > 1)
        {
            _resolveTarget.Ensure(rw, rh, 1);
            _sceneTarget.BlitColorTo(_resolveTarget.Handle, rw, rh, linear: false); // resolve, same size
            _resolveTarget.BlitColorTo(0, fbW, fbH, linear: true);                  // scale to screen
        }
        else
        {
            _sceneTarget.BlitColorTo(0, fbW, fbH, linear: true);
        }

        LatchGlError("post+blit");
        BurnTrace.Mark("scene: post+blit done");
        // hand a clean default framebuffer + full viewport to ImGui
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)fbW, (uint)fbH);
    }

    private int ClampSamples(int requested)
    {
        if (requested < 1) requested = 1;
        if (requested > _maxSamples) requested = _maxSamples;
        return requested;
    }

    private void EnsureSphereLod(QualityTier tier)
    {
        if (_sphereTier == tier) return;
        _sphereTier = tier;
        (int stacks, int slices) = tier switch
        {
            QualityTier.Low    => (16, 24),
            QualityTier.Medium => (24, 36),
            QualityTier.Ultra  => (48, 72),
            _                  => (32, 48),   // High
        };
        _sphere.Dispose();
        _sphere = SphereMesh.Create(_gl, stacks, slices);
    }

    private Vector3 ToRender(Vec3 world, Vec3 focus) => new(
        (float)((world.X - focus.X) * _scale),
        (float)((world.Y - focus.Y) * _scale),
        (float)((world.Z - focus.Z) * _scale));

    /// <summary>Set the camera standoff for the current scenario (render units).</summary>
    public void FrameFor(double cameraDistanceHint)
    {
        if (cameraDistanceHint > 0.0) _camera.SetDefaultDistance((float)cameraDistanceHint);
    }

    /// <summary>
    /// Draws one primitive through every shader program - each with the exact pipeline state it
    /// uses in-game (the plume with additive blending and depth writes off, the film grade as a
    /// fullscreen textured pass) - into a tiny offscreen target, then blocks on <c>glFinish</c>.
    ///
    /// Rationale: AMD's OpenGL driver finalises shader compilation lazily, on worker threads, at
    /// a program's first real draw. On the 2023-era driver this project was developed against
    /// (22.40.84.06), that deferred work fast-failed the process (silent exit 0xC0000409) the
    /// first time the engine-plume pipeline ran - mid-flight, on the first burning frame. Doing
    /// the first draw of every pipeline here moves any such driver work to load time, where it is
    /// synchronous, breadcrumbed (the dying program would be named in crash.log), and recoverable
    /// via the driver-safe-mode fallback. Warm-up is best-effort: managed failures are logged and
    /// never block startup.
    /// </summary>
    public void WarmUp(GraphicsSettings graphics)
    {
        try
        {
            CrashLog.Phase("shader warm-up: begin");
            bool additive = graphics.AdditivePlumes && !graphics.DriverSafeMode && _plumeShader != null;
            bool film     = graphics.FilmGrade && !graphics.DriverSafeMode && _postOk && _post != null;

            _sceneTarget.Ensure(8, 8, ClampSamples(graphics.MsaaSamples));
            _sceneTarget.Bind();
            _gl.Viewport(0, 0, 8, 8);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Less);
            _gl.Disable(EnableCap.Blend);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

            float[] proj  = Mat4.Perspective(1.0f, 1.0f, 0.1f, 100.0f);
            float[] view  = Mat4.LookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, new Vector3(0f, 1f, 0f));
            float[] model = Mat4.TranslationScale(Vector3.Zero, 1.0f);

            CrashLog.Phase("shader warm-up: planet program");
            _planetShader.Use();
            _planetShader.SetMatrix("uProj", proj);
            _planetShader.SetMatrix("uView", view);
            _planetShader.SetMatrix("uModel", model);
            _planetShader.SetVec3("uLightDir", 0f, 1f, 0f);
            _planetShader.SetVec3("uColor", 0.5f, 0.5f, 0.5f);
            _planetShader.SetFloat("uAmbient", 0.10f);
            _sphere.Draw();

            CrashLog.Phase("shader warm-up: mesh program (hot emissive)");
            _meshShader.Use();
            _meshShader.SetMatrix("uProj", proj);
            _meshShader.SetMatrix("uView", view);
            _meshShader.SetMatrix("uModel", model);
            _meshShader.SetVec3("uLightDir", 0f, 1f, 0f);
            _meshShader.SetVec3("uViewPos", 0f, 0f, 3f);
            _meshShader.SetVec3("uColor", 0.5f, 0.5f, 0.5f);
            _meshShader.SetFloat("uAmbient", 0.30f);
            _meshShader.SetVec3("uEmissive", 0.45f, 0.16f, 0.05f);   // the burning-bell value
            _cone.Draw();
            _cyl.Draw();
            _box.Draw();
            _noseFrustum.Draw();
            _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);

            CrashLog.Phase("shader warm-up: line program");
            _lineShader.Use();
            _lineShader.SetMatrix("uProj", proj);
            _lineShader.SetMatrix("uView", view);
            _lineShader.SetVec4("uColor", 1f, 1f, 1f, 1f);
            _gl.LineWidth(1.5f);
            _lines.Upload(new float[] { -0.5f, 0f, 0f, 0.5f, 0f, 0f });
            _lines.DrawStrip();

            if (additive)
            {
                CrashLog.Phase("shader warm-up: plume program (additive, depth-mask off)");
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                _gl.DepthMask(false);
                _plumeShader!.Use();
                _plumeShader.SetMatrix("uProj", proj);
                _plumeShader.SetMatrix("uView", view);
                _plumeShader.SetMatrix("uModel", model);
                _plumeShader.SetVec3("uViewPos", 0f, 0f, 3f);
                _plumeShader.SetVec3("uAxis", 0f, 0f, -1f);
                _plumeShader.SetVec3("uColor", 1.0f, 0.42f, 0.12f);
                _plumeShader.SetFloat("uIntensity", 0.85f);
                _cone.Draw();
                _gl.DepthMask(true);
                _gl.Disable(EnableCap.Blend);
            }

            if (film)
            {
                CrashLog.Phase("shader warm-up: film-grade program");
                _postTarget.Ensure(8, 8);
                _sceneTarget.BlitColorTo(_postTarget.Handle, 8, 8, linear: false);
                _resolveTarget.Ensure(8, 8, 1);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveTarget.Handle);
                _gl.Viewport(0, 0, 8, 8);
                _gl.Disable(EnableCap.DepthTest);
                _post!.Use();
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, _postTarget.Texture);
                _post.SetVec2("uResolution", 8f, 8f);
                _post.SetFloat("uTime", 0f);
                _post.SetFloat("uAmount", 0.85f);
                _gl.BindVertexArray(_postVao);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3u);
                _gl.BindVertexArray(0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.Enable(EnableCap.DepthTest);
            }

            _gl.BindVertexArray(0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Finish();   // block until the driver has fully consumed - and compiled for - all of the above
            LatchGlError("warm-up");
            CrashLog.Phase("shader warm-up: complete");
        }
        catch (Exception ex)
        {
            CrashLog.Report("shader warm-up", ex);
        }
    }

    // Drains one GL error code per call and records the first error seen for each named pass, so
    // a driver that survives but flags an error (instead of fast-failing) leaves evidence in
    // crash.log. Latched per pass: after the first hit a pass stops logging (and checking).
    private readonly HashSet<string> _glErrLatched = new();
    private void LatchGlError(string pass)
    {
        if (_glErrLatched.Contains(pass)) return;
        GLEnum err = _gl.GetError();
        if (err != GLEnum.NoError)
        {
            _glErrLatched.Add(pass);
            CrashLog.Note("GL:" + pass, "glGetError = " + err);
        }
    }

    // --- frustum culling diagnostics ---
    private int _drawn, _culled;
    public int LastDrawn => _drawn;
    public int LastCulled => _culled;

    private static Vector4[] ExtractFrustum(float[] vp)
    {
        // rows of the column-major view-projection
        Vector4 r0 = new(vp[0], vp[4], vp[8], vp[12]);
        Vector4 r1 = new(vp[1], vp[5], vp[9], vp[13]);
        Vector4 r2 = new(vp[2], vp[6], vp[10], vp[14]);
        Vector4 r3 = new(vp[3], vp[7], vp[11], vp[15]);
        return new[]
        {
            NormPlane(r3 + r0), NormPlane(r3 - r0),   // left, right
            NormPlane(r3 + r1), NormPlane(r3 - r1),   // bottom, top
            NormPlane(r3 + r2), NormPlane(r3 - r2),   // near, far
        };
    }

    private static Vector4 NormPlane(Vector4 p)
    {
        float n = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return n > 1e-9f ? p / n : p;
    }

    private static bool InFrustum(Vector4[] planes, Vector3 c, float radius)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            Vector4 p = planes[i];
            if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -radius) return false;
        }
        return true;
    }

    /// <summary>Orthonormal basis (right, up, fwd) from a world nose direction.</summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Fwd) Basis(Vec3 forward)
    {
        Vector3 fwd = new((float)forward.X, (float)forward.Y, (float)forward.Z);
        fwd = fwd.LengthSquared() > 1e-12f ? Vector3.Normalize(fwd) : new Vector3(1, 0, 0);
        Vector3 worldUp = MathF.Abs(fwd.Z) > 0.95f ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
        Vector3 right = Vector3.Normalize(Vector3.Cross(worldUp, fwd));
        Vector3 up = Vector3.Cross(fwd, right);
        return (right, up, fwd);
    }

    /// <summary>An oriented ship hull: a deliberately blocky, modular brutalist warship — blunt command
    /// section, stepped propellant tanks, a structural spine, a distinct reactor block, a clustered engine
    /// array, a large radiator cross, and pipework detail. All procedural primitives (no model files).</summary>
    /// <summary>
    /// Draw all queued engine plumes as additive, depth-tested (but never depth-written) glow cones:
    /// light, not solid geometry. Brightness peaks on the centreline and fades to nothing at the
    /// silhouette and toward the tail (see the plume shader), overlapping cones sum to a hot core,
    /// and the film-grade bloom then bleeds the result. If the plume shader failed to compile on
    /// this GPU, falls back to the old solid emissive cone via the mesh shader.
    /// </summary>
    private void DrawPlumes(float[] proj, float[] vmat)
    {
        if (_plumes.Count == 0) return;

        if (_plumeShader != null && _additiveOk)
        {
            CrashLog.Phase("first plume draw: additive shader path entering");
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);   // pure additive
            _gl.DepthMask(false);                                    // test against hulls, never write

            _plumeShader.Use();
            _plumeShader.SetMatrix("uProj", proj);
            _plumeShader.SetMatrix("uView", vmat);
            Vector3 eye = _camera.Eye();
            _plumeShader.SetVec3("uViewPos", eye.X, eye.Y, eye.Z);

            foreach (PlumeDraw p in _plumes)
            {
                Vector3 center = p.Pos + p.Aft * (p.Length * 0.5f);
                _plumeShader.SetMatrix("uModel",
                    Mat4.ModelAxes(center, p.Right, p.Up, p.Aft, new Vector3(p.Radius, p.Radius, p.Length)));
                _plumeShader.SetVec3("uAxis", p.Aft.X, p.Aft.Y, p.Aft.Z);
                _plumeShader.SetVec3("uColor", p.Color.X, p.Color.Y, p.Color.Z);
                _plumeShader.SetFloat("uIntensity", p.Intensity);
                _cone.Draw();
            }

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            CrashLog.Phase("first plume draw: additive shader path complete");
        }
        else
        {
            // fallback: the previous solid emissive cone look
            CrashLog.Phase("first plume draw: fallback emissive path");
            _meshShader.Use();
            _meshShader.SetMatrix("uProj", proj);
            _meshShader.SetMatrix("uView", vmat);
            foreach (PlumeDraw p in _plumes)
            {
                Vector3 center = p.Pos + p.Aft * (p.Length * 0.5f);
                _meshShader.SetVec3("uColor", 0.30f, 0.14f, 0.05f);
                _meshShader.SetFloat("uAmbient", 0.30f);
                _meshShader.SetVec3("uEmissive", p.Color.X * 0.8f, p.Color.Y * 0.8f, p.Color.Z * 0.8f);
                _meshShader.SetMatrix("uModel",
                    Mat4.ModelAxes(center, p.Right, p.Up, p.Aft, new Vector3(p.Radius, p.Radius, p.Length)));
                _cone.Draw();
            }
            _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);
        }

        _plumes.Clear();
    }

    // Projects an orbit sample list to render space, collapsing any non-finite vertex onto the
    // previous valid one. A hyperbolic arc can produce a near-asymptote sample at extreme/Inf
    // magnitude; feeding that to the GL line buffer is undefined, so it is sanitised here.
    private float[] OrbitVertices(List<Vec3> pts, Vec3 focus)
    {
        var data = new float[pts.Count * 3];
        int idx = 0;
        Vector3 prev = default;
        bool have = false;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 rp = ToRender(pts[i], focus);
            if (!float.IsFinite(rp.X) || !float.IsFinite(rp.Y) || !float.IsFinite(rp.Z))
                rp = have ? prev : default;
            else { prev = rp; have = true; }
            data[idx++] = rp.X; data[idx++] = rp.Y; data[idx++] = rp.Z;
        }
        return data;
    }

    private void DrawHull(Vector3 c, RigidBody v, float hs) =>
        DrawHull(c, v, hs, new Vector3(0.62f, 0.66f, 0.72f));

    private void DrawHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);

        void Part(Mesh m, float r, float g, float b, float amb, Vector3 center, Vector3 scale, Vector3 emis = default)
        {
            _meshShader.SetVec3("uColor", r, g, b);
            _meshShader.SetFloat("uAmbient", amb);
            _meshShader.SetVec3("uEmissive", emis.X, emis.Y, emis.Z);
            _meshShader.SetMatrix("uModel", Mat4.ModelAxes(center, right, up, fwd, scale));
            m.Draw();
        }
        Vector3 F(float k) => c + fwd * (k * hs);                          // a point along the nose axis
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs); // scale helper

        float tr = hull.X, tg = hull.Y, tb = hull.Z;

        // local: a weapon hardpoint — a small turret base on the hull plus a thin barrel
        void WeaponMount(Vector3 at, Vector3 outDir, float size, float barrelLen)
        {
            Part(_box, 0.30f, 0.31f, 0.34f, 0.38f, at, new Vector3(size * hs * 1.7f, size * hs * 1.7f, size * hs * 1.7f));
            Vector3 bDir = Vector3.Normalize(fwd + Vector3.Normalize(outDir) * 0.45f);
            Vector3 bRight = Vector3.Normalize(Vector3.Cross(up, bDir));
            Vector3 bUp = Vector3.Cross(bDir, bRight);
            _meshShader.SetVec3("uColor", 0.22f, 0.23f, 0.26f);
            _meshShader.SetFloat("uAmbient", 0.34f);
            _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);
            _meshShader.SetMatrix("uModel", Mat4.ModelAxes(at + bDir * (barrelLen * hs * 0.5f), bRight, bUp, bDir,
                new Vector3(size * hs * 0.34f, size * hs * 0.34f, barrelLen * hs)));
            _cyl.Draw();
        }

        // --- radiators: each wing is three slim segmented fins (not one slab), glowing with waste heat ---
        // (the orange glow is a fixed baseline; a later chunk drives its intensity from the actual heat load)
        void RadWing(Vector3 outDir, bool vertical)
        {
            Vector3 glow = new(0.55f, 0.20f, 0.06f);
            for (int n = -1; n <= 1; n++)
            {
                Vector3 ctr = F(-0.05f + n * 0.40f) + outDir * (hs * 0.68f);
                Vector3 sc = vertical ? S(0.05f, 0.80f, 0.26f) : S(0.80f, 0.05f, 0.26f);
                Part(_box, 0.20f, 0.16f, 0.15f, 0.40f, ctr, sc, glow);
            }
        }
        RadWing(right, false);
        RadWing(-right, false);
        RadWing(up, true);
        RadWing(-up, true);

        // --- drive block: a TRIANGULAR array of three gimballed fusion torches (one dorsal, two
        //     ventral) on a shared mount plate, with a heat-shield skirt, a warm central drive core,
        //     and a propellant feed conduit running aft to each torch. Plumes are queued and drawn
        //     last as additive light (see DrawPlumes), not as solid geometry ---
        Part(_cyl, tr * 0.55f, tg * 0.55f, tb * 0.58f, 0.34f, F(-1.98f), S(0.44f, 0.44f, 0.24f));   // mount plate
        Part(_cyl, 0.16f, 0.17f, 0.19f, 0.30f, F(-2.12f), S(0.34f, 0.34f, 0.05f));                   // heat-shield skirt
        Part(_cyl, 0.30f, 0.26f, 0.24f, 0.32f, F(-2.05f), S(0.10f, 0.10f, 0.18f),
             new Vector3(0.12f, 0.05f, 0.02f));                                                       // central drive core (warm)
        bool burning = v.ThrustWorld.Length > 1e-6;
        if (burning) CrashLog.Phase("first burning frame: torch bells + plume queue");
        float lf = 0.85f + 0.55f * MathF.Min((float)(v.ThrustWorld.Length / Math.Max(v.Mass, 1.0)) / 10f, 1f);   // plume length follows acceleration
        if (!float.IsFinite(lf)) lf = 1f;   // never feed a NaN/Inf scale into the plume mesh
        Vector3 bellHot = burning ? new Vector3(0.45f, 0.16f, 0.05f) : default;
        for (int k = 0; k < 3; k++)
        {
            float a = MathF.PI * (0.5f + k * 2.0f / 3.0f);            // 90, 210, 330 degrees
            Vector3 dir = right * MathF.Cos(a) + up * MathF.Sin(a);   // radial direction of this torch
            Vector3 off = dir * (hs * 0.21f);
            Vector3 gfwd = Vector3.Normalize(fwd - dir * 0.06f);      // slight outward gimbal splay
            Part(_cyl, 0.24f, 0.25f, 0.27f, 0.32f, F(-1.67f) + dir * (hs * 0.375f), S(0.030f, 0.030f, 0.34f));   // feed conduit on the aft hull
            Part(_sphere, 0.22f, 0.22f, 0.25f, 0.40f, F(-2.06f) + off, S(0.14f, 0.14f, 0.14f));                  // gimbal joint
            _meshShader.SetVec3("uColor", 0.16f, 0.17f, 0.20f);
            _meshShader.SetFloat("uAmbient", 0.34f);
            _meshShader.SetVec3("uEmissive", bellHot.X, bellHot.Y, bellHot.Z);
            _meshShader.SetMatrix("uModel", Mat4.ModelAxes(F(-2.30f) + off, right, up, gfwd, S(0.17f, 0.17f, 0.42f)));
            _cone.Draw();   // torch bell (glows faintly hot while burning)
            Part(_cyl, 0.13f, 0.14f, 0.16f, 0.30f, F(-2.50f) + off, S(0.185f, 0.185f, 0.035f));   // nozzle lip ring
            if (burning && _plumesEnabled)
            {
                Vector3 exit = F(-2.51f) + off;
                Vector3 aft = -gfwd;
                _plumes.Add(new PlumeDraw { Pos = exit, Right = right, Up = up, Aft = aft,
                    Radius = 0.155f * hs, Length = 1.60f * hs * lf,
                    Color = new Vector3(1.00f, 0.42f, 0.12f), Intensity = 0.85f });    // outer soft glow
                _plumes.Add(new PlumeDraw { Pos = exit, Right = right, Up = up, Aft = aft,
                    Radius = 0.080f * hs, Length = 1.10f * hs * lf,
                    Color = new Vector3(1.25f, 1.00f, 0.75f), Intensity = 1.5f });     // hot near-white core
            }
        }

        // --- aft hull (wide) ---
        Part(_cyl, tr * 0.82f, tg * 0.82f, tb * 0.82f, 0.30f, F(-1.58f), S(0.36f, 0.36f, 0.58f));

        // --- reactor module (distinct, faintly warm) with rib detail ---
        Part(_cyl, 0.36f, 0.32f, 0.30f, 0.34f, F(-0.92f), S(0.34f, 0.34f, 0.66f), new Vector3(0.10f, 0.04f, 0.02f));
        Part(_box, 0.28f, 0.25f, 0.24f, 0.40f, F(-0.92f) + up * (hs * 0.36f), S(0.64f, 0.06f, 0.54f));
        Part(_box, 0.28f, 0.25f, 0.24f, 0.40f, F(-0.92f) - up * (hs * 0.36f), S(0.64f, 0.06f, 0.54f));

        // --- mid propellant tank (widest cylindrical section) ---
        Part(_cyl, tr, tg, tb, 0.30f, F(-0.05f), S(0.37f, 0.37f, 1.05f));

        // --- forward hull: one smooth tapered section (frustum) running from the tank to a slim flat bow,
        //     replacing the old stack of stepped cylinders so the nose tapers as a single clean surface ---
        Part(_noseFrustum, MathF.Min(tr * 1.02f, 1f), MathF.Min(tg * 1.02f, 1f), MathF.Min(tb * 1.02f, 1f), 0.30f, F(1.49f), S(0.36f, 0.36f, 2.02f));

        // --- sensor / comms box on the forward hull ---
        Part(_box, 0.30f, 0.33f, 0.38f, 0.45f, F(1.30f) + up * (hs * 0.30f), S(0.13f, 0.15f, 0.20f));

        // --- surface detail: hull frame rings, a tank/bow seam collar, and RCS thruster quads ---
        // all of it hugs the existing sections (slightly proud bands, half-embedded blocks): detail
        // without changing the silhouette. Placements are chosen clear of the radiator fins.
        void Ring(float at, float r, float len) =>
            Part(_cyl, tr * 0.78f, tg * 0.78f, tb * 0.78f, 0.28f, F(at), S(r, r, len));
        Ring(-1.55f, 0.366f, 0.05f);    // aft-hull frame
        Ring(-0.28f, 0.376f, 0.05f);    // tank frame (between radiator fin rows)
        Ring(0.18f, 0.376f, 0.05f);     // tank frame (between radiator fin rows)
        Ring(0.48f, 0.372f, 0.06f);     // tank / bow seam collar
        Ring(1.05f, 0.308f, 0.045f);    // forward-hull band
        void RcsQuad(float at, float reach)
        {
            foreach (Vector3 o in new[] { right, -right, up, -up })
                Part(_box, 0.30f, 0.31f, 0.34f, 0.36f, F(at) + o * (hs * reach), S(0.05f, 0.05f, 0.07f));
        }
        RcsQuad(1.85f, 0.235f);    // bow attitude cluster
        RcsQuad(-1.40f, 0.372f);   // stern attitude cluster

        // --- weapon hardpoints (placed clear of the radiator fins): medium dorsal/ventral forward,
        //     small side turrets on the bow, small point-defense aft ---
        WeaponMount(F(0.88f) + up * (hs * 0.40f), up, 0.13f, 0.55f);
        WeaponMount(F(0.88f) - up * (hs * 0.40f), -up, 0.13f, 0.55f);
        WeaponMount(F(1.32f) + right * (hs * 0.30f), right, 0.085f, 0.32f);
        WeaponMount(F(1.32f) - right * (hs * 0.30f), -right, 0.085f, 0.32f);
        WeaponMount(F(-1.38f) + right * (hs * 0.34f), right, 0.085f, 0.30f);
        WeaponMount(F(-1.38f) - right * (hs * 0.34f), -right, 0.085f, 0.30f);

        // --- pipework greebles running along the spine ---
        Part(_cyl, 0.30f, 0.32f, 0.35f, 0.35f, F(-0.2f) + right * (hs * 0.30f) + up * (hs * 0.12f), S(0.035f, 0.035f, 2.10f));
        Part(_cyl, 0.30f, 0.32f, 0.35f, 0.35f, F(-0.2f) - right * (hs * 0.30f) + up * (hs * 0.12f), S(0.035f, 0.035f, 2.10f));
    }

    /// <summary>A spin habitat: a despun hub plus two counter-rotating rings (angle from sim time).</summary>
    private void DrawStation(Vector3 c, RigidBody t, float hs, double simTime)
    {
        // despun hub (docking core)
        _meshShader.SetVec3("uColor", 0.55f, 0.58f, 0.62f);
        _meshShader.SetFloat("uAmbient", 0.40f);
        _meshShader.SetMatrix("uModel", Mat4.TranslationScale(c, hs * 0.5f));
        _sphere.Draw();

        double w = t.SpinRpm * MathConstants.TwoPi / 60.0;
        float ang = (float)(w * simTime);
        const int n = 12;
        float step = (float)(MathConstants.TwoPi / n);

        // main ring (cyan), spins +ang in the world XY plane (orbit-normal axis)
        _meshShader.SetVec3("uColor", 0.45f, 0.80f, 0.95f);
        _meshShader.SetFloat("uAmbient", 0.80f);
        float rr = hs * 2.4f, node = hs * 0.32f;
        for (int k = 0; k < n; k++)
        {
            float a = ang + k * step;
            Vector3 p = c + new Vector3(MathF.Cos(a) * rr, MathF.Sin(a) * rr, 0f);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(p, node));
            _sphere.Draw();
        }

        // counter-rotating ring (green), -ang, smaller — nulls net angular momentum
        _meshShader.SetVec3("uColor", 0.45f, 0.85f, 0.55f);
        float rr2 = hs * 1.5f;
        for (int k = 0; k < n; k++)
        {
            float a = -ang + k * step;
            Vector3 p = c + new Vector3(MathF.Cos(a) * rr2, MathF.Sin(a) * rr2, 0f);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(p, node * 0.8f));
            _sphere.Draw();
        }
    }

    public void Dispose()
    {
        _sphere.Dispose();
        _cyl.Dispose();
        _cone.Dispose();
        _box.Dispose();
        _noseFrustum.Dispose();
        _plumeShader?.Dispose();
        _lines.Dispose();
        _meshShader.Dispose();
        _planetShader.Dispose();
        _lineShader.Dispose();
        _sceneTarget.Dispose();
        _resolveTarget.Dispose();
        _postTarget.Dispose();
        _post?.Dispose();
        if (_postVao != 0) _gl.DeleteVertexArray(_postVao);
    }

    // ----- shader sources -----
    private const string MeshVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uProj;
uniform mat4 uView;
uniform mat4 uModel;
out vec3 vNormal;
out vec3 vWorldPos;
void main(){
    vec4 wp = uModel * vec4(aPos, 1.0);
    vWorldPos = wp.xyz;
    vNormal = mat3(uModel) * aNormal;
    gl_Position = uProj * uView * wp;
}";

    private const string MeshFrag = @"#version 330 core
in vec3 vNormal;
in vec3 vWorldPos;
uniform vec3 uColor;
uniform vec3 uLightDir;
uniform float uAmbient;
uniform vec3 uViewPos;
uniform vec3 uEmissive;
out vec4 FragColor;
void main(){
    vec3 N = normalize(vNormal);
    vec3 L = normalize(uLightDir);
    vec3 V = normalize(uViewPos - vWorldPos);
    vec3 H = normalize(L + V);
    float diff = max(dot(N, L), 0.0);
    float light = uAmbient + (1.0 - uAmbient) * diff;
    vec3 base = uColor * light;
    // Blinn-Phong specular on the lit side, tinted toward the surface for a brushed-metal feel
    float specAmt = pow(max(dot(N, H), 0.0), 28.0) * step(0.0001, diff);
    vec3 spec = mix(vec3(1.0), uColor, 0.35) * (specAmt * 0.40);
    // cool fresnel rim to lift the silhouette against the dark
    float rim = pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.28;
    vec3 rimCol = vec3(0.45, 0.55, 0.70) * (rim * (0.3 + 0.7 * diff));
    FragColor = vec4(base + spec + rimCol + uEmissive, 1.0);
}";

    // Fallback mesh shader (used only if the lit shader above fails to compile): flat diffuse, no
    // specular/rim/emissive. Uses the same uProj/uView/uModel/uColor/uLightDir/uAmbient uniforms.
    private const string BasicMeshVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uProj;
uniform mat4 uView;
uniform mat4 uModel;
out vec3 vNormal;
void main(){
    vNormal = mat3(uModel) * aNormal;
    gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
}";

    private const string BasicMeshFrag = @"#version 330 core
in vec3 vNormal;
uniform vec3 uColor;
uniform vec3 uLightDir;
uniform float uAmbient;
out vec4 FragColor;
void main(){
    vec3 N = normalize(vNormal);
    float diff = max(dot(N, normalize(uLightDir)), 0.0);
    float light = uAmbient + (1.0 - uAmbient) * diff;
    FragColor = vec4(uColor * light, 1.0);
}";

    private const string LineVert = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uProj;
uniform mat4 uView;
void main(){
    gl_Position = uProj * uView * vec4(aPos, 1.0);
}";
    private const string LineFrag = @"#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main(){
    FragColor = uColor;
}";

    private const string PlanetVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uProj;
uniform mat4 uView;
uniform mat4 uModel;
out vec3 vObjN;
out vec3 vWorldN;
out vec3 vViewPos;
out vec3 vViewN;
void main(){
    vObjN = normalize(aNormal);
    vWorldN = mat3(uModel) * aNormal;
    mat4 mv = uView * uModel;
    vec4 vp = mv * vec4(aPos, 1.0);
    vViewPos = vp.xyz;
    vViewN = mat3(mv) * aNormal;
    gl_Position = uProj * vp;
}";

    private const string PlanetFrag = @"#version 330 core
in vec3 vObjN;
in vec3 vWorldN;
in vec3 vViewPos;
in vec3 vViewN;
uniform vec3 uColor;
uniform vec3 uLightDir;
uniform float uAmbient;
out vec4 FragColor;
const float PI = 3.14159265;
float gridLine(float c, float spacing, float w){
    float d = abs(fract(c / spacing + 0.5) - 0.5) * spacing;
    return smoothstep(w, 0.0, d);
}
float hash3(vec3 p){
    p = fract(p * 0.3183099 + 0.1);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
void main(){
    vec3 N = normalize(vWorldN);
    vec3 n = normalize(vObjN);
    float diff = max(dot(N, normalize(uLightDir)), 0.0);
    float light = uAmbient + (1.0 - uAmbient) * diff;
    float lat = asin(clamp(n.y, -1.0, 1.0));
    float lon = atan(n.z, n.x);
    // subtle continental mottling so the body is not a flat ball
    float mott = hash3(floor(n * 9.0)) * 0.55 + hash3(floor(n * 23.0)) * 0.30;
    vec3 base = uColor * (0.82 + 0.24 * mott);
    base *= 0.95 + 0.05 * sin(lat * 7.0);
    // softer graticule
    float gr = clamp(gridLine(lat, PI / 6.0, 0.014) + gridLine(lon, PI / 6.0, 0.014), 0.0, 1.0);
    vec3 surface = mix(base, mix(base, vec3(1.0), 0.16), gr);
    // atmosphere rim: view-space fresnel, brighter on the day side
    vec3 vd = normalize(-vViewPos);
    float fres = 1.0 - max(dot(normalize(vViewN), vd), 0.0);
    float rim = pow(clamp(fres, 0.0, 1.0), 3.0);
    vec3 atmo = vec3(0.35, 0.55, 0.95) * rim * (0.25 + 0.75 * diff);
    FragColor = vec4(surface * light + atmo * 1.1, 1.0);
}";

    // Fallback planet shader (used only if PlanetFrag fails to compile): a plain lambert sphere.
    // Uses the same uniforms the renderer sets (uProj/uView/uModel/uColor/uLightDir/uAmbient).
    private const string BasicPlanetVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uProj;
uniform mat4 uView;
uniform mat4 uModel;
out vec3 vN;
void main(){
    vN = mat3(uModel) * aNormal;
    gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
}";

    private const string BasicPlanetFrag = @"#version 330 core
in vec3 vN;
uniform vec3 uColor;
uniform vec3 uLightDir;
uniform float uAmbient;
out vec4 FragColor;
void main(){
    float d = max(dot(normalize(vN), normalize(uLightDir)), 0.0);
    FragColor = vec4(uColor * (uAmbient + (1.0 - uAmbient) * d), 1.0);
}";

    // Additive plasma drive plume. The look comes from two falloffs: a radial "facing" term
    // (brightest where the view ray passes through the plume's centreline, zero at the silhouette,
    // computed from the cross-axis direction so it is immune to the cone's non-uniform scale) and
    // an axial fade from the bell exit to the tail. Output is plain RGB energy for additive blending.
    private const string PlumeVert = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;
out vec3 vWorld;
out vec3 vRadial;
out float vZ;
void main(){
    vec4 w = uModel * vec4(aPos, 1.0);
    vWorld = w.xyz;
    vec2 xy = aPos.xy;
    float l = max(length(xy), 1.0e-5);
    vRadial = mat3(uModel) * vec3(xy / l, 0.0);
    vZ = aPos.z;
    gl_Position = uProj * uView * w;
}";

    private const string PlumeFrag = @"#version 330 core
in vec3 vWorld;
in vec3 vRadial;
in float vZ;
out vec4 frag;
uniform vec3 uViewPos;
uniform vec3 uAxis;
uniform vec3 uColor;
uniform float uIntensity;
void main(){
    vec3 V = normalize(uViewPos - vWorld);
    float rl = length(vRadial);
    float radial = rl > 1.0e-5 ? abs(dot(vRadial / rl, V)) : 0.0;
    float along = abs(dot(uAxis, V));
    float facing = max(radial, along * 0.85);   // side view: radial term; end-on: bright disc via the axis term
    float soft = pow(facing, 1.4);
    float axial = clamp(0.5 - vZ, 0.0, 1.0);
    axial = axial * axial * (3.0 - 2.0 * axial);
    frag = vec4(uColor * (soft * axial * uIntensity), 1.0);
}";

    // Fullscreen triangle generated from gl_VertexID — no vertex buffer needed.
    private const string PostVert = @"#version 330 core
out vec2 vUv;
void main(){
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

    // 'Gen X Soft Club' film grade: gentle soft-focus, cross-process desaturate + cool-shadow/warm-
    // highlight tint, lifted blacks, animated film grain, and a soft vignette. uAmount scales the whole effect.
    private const string PostFrag = @"#version 330 core
in vec2 vUv;
out vec4 frag;
uniform sampler2D uScene;
uniform vec2 uResolution;
uniform float uTime;
uniform float uAmount;
float hash(vec2 p){
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
void main(){
    vec2 texel = 1.0 / uResolution;
    vec3 scene = texture(uScene, vUv).rgb;
    // 5-tap soft focus
    vec3 blur = scene * 0.5;
    blur += texture(uScene, vUv + vec2(texel.x, 0.0)).rgb * 0.125;
    blur += texture(uScene, vUv - vec2(texel.x, 0.0)).rgb * 0.125;
    blur += texture(uScene, vUv + vec2(0.0, texel.y)).rgb * 0.125;
    blur += texture(uScene, vUv - vec2(0.0, texel.y)).rgb * 0.125;
    vec3 c = mix(scene, blur, 0.45 * uAmount);
    // cross-process grade
    float l = dot(c, vec3(0.299, 0.587, 0.114));
    vec3 desat = mix(c, vec3(l), 0.35 * uAmount);
    vec3 cool = vec3(0.86, 0.95, 1.03);
    vec3 warm = vec3(1.05, 1.00, 0.93);
    vec3 graded = desat * mix(cool, warm, smoothstep(0.0, 1.0, l));
    graded = mix(desat, graded, uAmount);
    graded = graded * (1.0 - 0.06 * uAmount) + 0.045 * uAmount; // lift blacks, soften contrast
    // bloom: bright-pass a wide tap kernel and add it back, so the hot parts (radiators, engines and
    // specular highlights) bleed light the way the reference's glowing radiators do
    vec3 glow = vec3(0.0);
    for (int i = -2; i <= 2; i++)
        for (int j = -2; j <= 2; j++) {
            vec3 s = texture(uScene, vUv + vec2(float(i), float(j)) * texel * 3.5).rgb;
            float b = max(max(s.r, s.g), s.b);
            glow += s * smoothstep(0.45, 0.95, b);
        }
    graded += (glow / 25.0) * (1.1 * uAmount);
    // film grain
    float g = hash(vUv * uResolution + fract(uTime) * vec2(91.7, 47.3));
    graded += (g - 0.5) * 0.05 * uAmount;
    // starfield - sparse twinkling points, only where the scene is near-black (deep space)
    float darkness = 1.0 - smoothstep(0.015, 0.08, l);
    if (darkness > 0.02) {
        vec2 gp = vUv * vec2(260.0, 146.0);
        vec2 cell = floor(gp);
        float h = hash(cell);
        float present = step(0.975, h);
        vec2 f = fract(gp) - 0.5;
        float point = smoothstep(0.42, 0.0, length(f)) * present;
        float tw = 0.7 + 0.3 * sin(uTime * 0.251327 + h * 40.0);  // 2*pi/25: 25 s pulse; uTime wraps at 100 s = exactly 4 periods, so no visible phase jump
        graded += vec3(point * darkness * tw * 0.8 * uAmount);
    }
    // vignette
    vec2 d = vUv - 0.5;
    float vig = 1.0 - dot(d, d) * (0.95 * uAmount);
    graded *= clamp(vig, 0.0, 1.0);
    frag = vec4(clamp(graded, 0.0, 1.0), 1.0);
}";
}
