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
using Apastron.Vehicles;

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
    // Right button: camera orbit (drag), altitude move-disk (Ctrl+drag), move order (clean click).
    private bool _rWasDown, _rMoved;
    private Vector2 _rDownPos;
    // A world-space move target ready to consume (from a clean right-click or a released altitude gizmo).
    private bool _movePending;
    private Vec3 _moveTarget;
    // Live Ctrl+right-drag altitude gizmo: a base point on the focus plane plus the dragged target above it.
    private bool _gizmoActive;
    private Vec3 _gizmoBase, _gizmoTarget;
    // Left button: band-select (drag) and unit-select (clean click).
    private bool _lDown, _lWasDown, _lMoved, _lClickPending, _bandPending;
    private Vector2 _lDownPos, _lNowPos, _lClickPos, _bandMin, _bandMax;

    // last frame's view-projection (column-major), viewport, focus and render scale, for screen-space
    // vessel picking (so a left-click without a drag selects the ship under the cursor).
    private float[]? _pickVp;
    private float _pickW = 1.0f, _pickH = 1.0f;
    private Vec3 _pickFocus;
    private double _pickScale = 1.0e-6;
    private float _pickFovY = 1.0f;   // vertical FOV (rad) of the last frame, for unprojecting clicks

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

    /// <summary>Process mouse input for the 3D view: right-drag orbits the camera, a clean right-click is a
    /// move order, left-drag is a band-select, and a clean left-click selects. No-ops while the UI wants the
    /// mouse. Results are read back through the TryConsume* methods and the Band* properties.</summary>
    public void HandleInput(IMouse mouse, bool allowed, bool altitudeMode)
    {
        Vector2 pos = mouse.Position;
        _lNowPos = pos;

        if (!allowed)
        {
            // UI owns the mouse: drop any in-progress interaction without firing actions.
            _rWasDown = false; _rMoved = false; _gizmoActive = false;
            _lWasDown = false; _lDown = false; _lMoved = false;
            _lastMouse = pos;
            return;
        }

        // ----- right button: orbit (drag) / altitude move-disk (Ctrl+drag) / move order (click) -----
        bool rDown = mouse.IsButtonPressed(MouseButton.Right);
        if (rDown && !_rWasDown)
        {
            _rDownPos = pos; _rMoved = false;
            // Ctrl held: anchor an altitude gizmo on the focus plane under the cursor.
            _gizmoActive = altitudeMode && ScreenToPlane(pos, out _gizmoBase);
            _gizmoTarget = _gizmoBase;
        }
        if (rDown && _rWasDown)
        {
            if (_gizmoActive)
            {
                // Vertical drag raises/lowers the target off the base plane; the camera does not move.
                double alt = -(pos.Y - _rDownPos.Y) * AltitudeScale();
                _gizmoTarget = new Vec3(_gizmoBase.X, _gizmoBase.Y + alt, _gizmoBase.Z);
            }
            else
            {
                Vector2 d = pos - _lastMouse;
                _camera.Rotate(d.X * 0.01f, d.Y * 0.01f);
            }
        }
        if (rDown && (pos - _rDownPos).Length() > 4.0f) _rMoved = true;
        if (!rDown && _rWasDown)
        {
            if (_gizmoActive) { _moveTarget = _gizmoTarget; _movePending = true; _gizmoActive = false; }
            else if (!_rMoved && ScreenToPlane(pos, out Vec3 planar)) { _moveTarget = planar; _movePending = true; }
        }
        _rWasDown = rDown;

        // ----- left button: band-select (drag) / select (click) -----
        bool lDown = mouse.IsButtonPressed(MouseButton.Left);
        if (lDown && !_lWasDown) { _lDownPos = pos; _lMoved = false; }
        if (lDown && (pos - _lDownPos).Length() > 4.0f) _lMoved = true;
        if (!lDown && _lWasDown)
        {
            if (_lMoved)
            {
                _bandMin = new Vector2(MathF.Min(_lDownPos.X, pos.X), MathF.Min(_lDownPos.Y, pos.Y));
                _bandMax = new Vector2(MathF.Max(_lDownPos.X, pos.X), MathF.Max(_lDownPos.Y, pos.Y));
                _bandPending = true;
            }
            else { _lClickPending = true; _lClickPos = pos; }
        }
        _lDown = lDown;
        _lWasDown = lDown;

        _lastMouse = pos;
    }

    // World units per vertical screen-pixel at the focus depth - so dragging the altitude gizmo feels like
    // dragging a point that sits at the same distance as whatever the camera is looking at.
    private float AltitudeScale()
    {
        double sc = _pickScale > 0.0 ? _pickScale : 1.0e-6;
        double camDistWorld = _camera.Distance / sc;
        return (float)(2.0 * Math.Tan(_pickFovY * 0.5) * camDistWorld / Math.Max(_pickH, 1.0));
    }

    /// <summary>A live Ctrl+right-drag altitude gizmo is in progress (for drawing the move-disk).</summary>
    public bool MoveGizmoActive => _gizmoActive;
    public Vec3 MoveGizmoBase => _gizmoBase;
    public Vec3 MoveGizmoTarget => _gizmoTarget;
    /// <summary>World radius for the gizmo's plane disk, sized to a roughly constant on-screen size.</summary>
    public double MoveGizmoDiskRadius => _camera.Distance / (_pickScale > 0.0 ? _pickScale : 1.0e-6) * 0.05;

    /// <summary>True while a band-select rectangle is being dragged (for drawing the marquee).</summary>
    public bool BandActive => _lDown && _lMoved;
    public Vector2 BandRectMin => new(MathF.Min(_lDownPos.X, _lNowPos.X), MathF.Min(_lDownPos.Y, _lNowPos.Y));
    public Vector2 BandRectMax => new(MathF.Max(_lDownPos.X, _lNowPos.X), MathF.Max(_lDownPos.Y, _lNowPos.Y));

    /// <summary>True once for a clean left-click (selection); yields the click position (px).</summary>
    public bool TryConsumeSelectClick(out Vector2 pos)
    {
        pos = _lClickPos;
        if (!_lClickPending) return false;
        _lClickPending = false; return true;
    }

    /// <summary>True once for a completed band-select; yields the rectangle (px).</summary>
    public bool TryConsumeBand(out Vector2 min, out Vector2 max)
    {
        min = _bandMin; max = _bandMax;
        if (!_bandPending) return false;
        _bandPending = false; return true;
    }

    /// <summary>True once when a move order is ready (clean right-click on the plane, or a released altitude
    /// gizmo); yields the world-space target.</summary>
    public bool TryConsumeMoveTarget(out Vec3 world)
    {
        world = _moveTarget;
        if (!_movePending) return false;
        _movePending = false; return true;
    }

    /// <summary>Project every vessel and collect the indices whose screen position falls inside the rect.</summary>
    public void PickVesselsInRect(Vector2 min, Vector2 max, PhysicsWorld world, System.Collections.Generic.List<int> into)
    {
        into.Clear();
        if (_pickVp == null) return;
        for (int i = 0; i < world.Vessels.Count; i++)
        {
            Vec3 w = world.Vessels[i].Position;
            float rx = (float)((w.X - _pickFocus.X) * _pickScale);
            float ry = (float)((w.Y - _pickFocus.Y) * _pickScale);
            float rz = (float)((w.Z - _pickFocus.Z) * _pickScale);
            float cw = _pickVp[3] * rx + _pickVp[7] * ry + _pickVp[11] * rz + _pickVp[15];
            if (cw <= 1e-4f) continue;
            float cx = _pickVp[0] * rx + _pickVp[4] * ry + _pickVp[8]  * rz + _pickVp[12];
            float cy = _pickVp[1] * rx + _pickVp[5] * ry + _pickVp[9]  * rz + _pickVp[13];
            float sx = (cx / cw * 0.5f + 0.5f) * _pickW;
            float sy = (1.0f - (cy / cw * 0.5f + 0.5f)) * _pickH;
            if (sx >= min.X && sx <= max.X && sy >= min.Y && sy <= max.Y) into.Add(i);
        }
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

    /// <summary>Project a world point to framebuffer-pixel screen coordinates with the last frame's camera.
    /// Returns false if the point is behind the camera or nothing has been rendered yet.</summary>
    public bool WorldToScreen(Vec3 world, out Vector2 screen)
    {
        screen = default;
        if (_pickVp == null) return false;
        float rx = (float)((world.X - _pickFocus.X) * _pickScale);
        float ry = (float)((world.Y - _pickFocus.Y) * _pickScale);
        float rz = (float)((world.Z - _pickFocus.Z) * _pickScale);
        float cw = _pickVp[3] * rx + _pickVp[7] * ry + _pickVp[11] * rz + _pickVp[15];
        if (cw <= 1e-4f) return false;
        float cx = _pickVp[0] * rx + _pickVp[4] * ry + _pickVp[8]  * rz + _pickVp[12];
        float cy = _pickVp[1] * rx + _pickVp[5] * ry + _pickVp[9]  * rz + _pickVp[13];
        screen = new Vector2((cx / cw * 0.5f + 0.5f) * _pickW, (1.0f - (cy / cw * 0.5f + 0.5f)) * _pickH);
        return true;
    }

    /// <summary>Unproject a screen pixel onto the horizontal reference plane through the camera focus (the
    /// Homeworld "movement plane"), returning the world point. False if the view ray is parallel to the plane
    /// or nothing has rendered yet. The plane sits at the focus's world height (world Y = focus Y).</summary>
    public bool ScreenToPlane(Vector2 screen, out Vec3 world)
    {
        world = default;
        if (_pickVp == null || _pickScale <= 0.0) return false;

        // Camera ray in render space: the camera sits at Eye() and looks at the render origin (the focus).
        Vector3 camPos = _camera.Eye();
        Vector3 fwd   = Vector3.Normalize(-camPos);
        Vector3 rAxis = Vector3.Cross(fwd, Vector3.UnitY);
        Vector3 right = rAxis.LengthSquared() > 1e-8f ? Vector3.Normalize(rAxis) : Vector3.UnitX;
        Vector3 up    = Vector3.Cross(right, fwd);

        float ndcx = (screen.X / _pickW) * 2.0f - 1.0f;
        float ndcy = 1.0f - (screen.Y / _pickH) * 2.0f;
        float tanH = MathF.Tan(_pickFovY * 0.5f);
        float aspect = _pickW / MathF.Max(_pickH, 1.0f);
        Vector3 dir = Vector3.Normalize(fwd + right * (ndcx * tanH * aspect) + up * (ndcy * tanH));

        // Intersect the render-space plane y = 0 (the world plane at the focus height).
        if (MathF.Abs(dir.Y) < 1e-6f) return false;
        float t = -camPos.Y / dir.Y;
        if (t <= 0.0f) return false;                       // plane is behind the camera
        Vector3 hit = camPos + dir * t;

        double inv = 1.0 / _pickScale;
        world = new Vec3(_pickFocus.X + hit.X * inv, _pickFocus.Y + hit.Y * inv, _pickFocus.Z + hit.Z * inv);
        return true;
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
        _pickFovY = view.FovDegrees * (float)MathConstants.DegToRad;

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

        // In a live engagement, draw every combatant as a faction-tinted oriented hull (player cyan, pirate
        // raiders dingy amber, near-peer adversaries red) instead of the flight nav-markers.
        if (inCombat)
        {
            foreach (Combatant cb in combat!.Combatants)
            {
                if (!cb.Alive) continue;
                Vector3 c = ToRender(cb.Body.Position, focus);
                if (!InFrustum(frustum, c, markerScale * 3.0f)) { _culled++; continue; }
                Vector3 tint = cb.IsPlayer
                    ? new Vector3(0.45f, 0.80f, 1.00f)                                       // player: cyan
                    : cb.Name.StartsWith("Raider", System.StringComparison.Ordinal)
                        ? new Vector3(0.86f, 0.60f, 0.28f)                                   // pirate raider: dingy amber
                        : new Vector3(1.00f, 0.42f, 0.38f);                                  // near-peer / other: red
                DrawHull(c, cb.Body, markerScale * HullSizeFactor(cb.Body.HullLength), tint, ArchetypeFor(cb.Ship));
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
                    else if (t.Controllable && !inCombat)
                    {
                        // a commanded escort outside combat reads as a class-sized ship, not a nav-dot
                        DrawHull(c, t, markerScale * HullSizeFactor(t.HullLength), new Vector3(0.45f, 0.80f, 1.00f));
                    }
                    else
                    {
                        _meshShader.SetMatrix("uModel", Mat4.TranslationScale(c, markerScale * 0.92f));
                        _meshShader.SetVec3("uColor", 0.30f, 0.90f, 0.50f);
                        _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);
                        _meshShader.SetFloat("uAmbient", 1.0f);
                        _sphere.Draw();
                    }
                    _drawn++;
                }
                else _culled++;
            }
        }

        BurnTrace.Mark("scene: hulls+companions done");

        // --- in-flight torpedoes: a small oriented physical body that "spawns in" by scaling up over its
        //     launch, with a warm nose. Its drive flare, exhaust streak and impact bloom are added below. ---
        if (combat != null)
        {
            float missileLen = MathF.Max(markerScale * 0.32f, 0.015f);
            foreach (Munition m in combat.Munitions)
            {
                if (!m.Alive || m.Kind != MunitionKind.Missile) continue;
                float fadeIn = (float)Math.Clamp(m.Age / 0.35, 0.0, 1.0);
                float grow = 0.45f + 0.55f * fadeIn;                       // grows from a seed to full size
                Vec3 vdir = m.Velocity.LengthSquared > 1.0 ? m.Velocity.Normalized() : Vec3.UnitX;
                (Vector3 right, Vector3 up, Vector3 fwd) = Basis(vdir);
                Vector3 ctr = ToRender(m.Position, focus);
                float L = missileLen * grow, r = L * 0.17f;
                _meshShader.SetVec3("uColor", 0.56f, 0.58f, 0.62f);
                _meshShader.SetFloat("uAmbient", 0.45f);
                _meshShader.SetVec3("uEmissive", 0.10f * fadeIn, 0.05f * fadeIn, 0.02f * fadeIn);
                _meshShader.SetMatrix("uModel", Mat4.ModelAxes(ctr, right, up, fwd, new Vector3(r, r, L)));
                _cyl.Draw();
                _meshShader.SetVec3("uColor", 0.32f, 0.20f, 0.12f);
                _meshShader.SetVec3("uEmissive", 0.35f * fadeIn, 0.13f * fadeIn, 0.03f * fadeIn);
                _meshShader.SetMatrix("uModel", Mat4.TranslationScale(ctr + fwd * (L * 0.55f), r * 1.25f));
                _sphere.Draw();
            }
            _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);
        }
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
        // solid physical bodies for torpedoes + KKV interceptors (materialize on launch), drawn opaque so the
        // additive drive flares and detonation blooms composite over them
        if (combat != null) DrawMunitionBodies(proj, vmat, combat, focus, markerScale);

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
                if (m.Kind == MunitionKind.Missile)
                {
                    // exhaust trail fades and lengthens in as the torpedo clears the tube
                    float fadeIn = (float)Math.Clamp(m.Age / 0.35, 0.0, 1.0);
                    Vec3 tail = m.Position - m.Velocity * (1.2 * fadeIn);
                    Vector3 a = ToRender(tail, focus);
                    Vector3 b = ToRender(m.Position, focus);
                    _lineShader.SetVec4("uColor", 1.0f * fadeIn, 0.55f * fadeIn, 0.15f * fadeIn, 1.0f);
                    _lines.Upload(a, b);
                    _lines.DrawStrip();
                }
                else
                {
                    Vec3 tail = m.Position - m.Velocity * 0.5;
                    Vector3 a = ToRender(tail, focus);
                    Vector3 b = ToRender(m.Position, focus);
                    _lineShader.SetVec4("uColor", 1.0f, 0.95f, 0.6f, 1.0f);
                    _lines.Upload(a, b);
                    _lines.DrawStrip();
                }
            }

            // anti-ship beams (solar lances): a thick, bright sustained line from emitter to target
            _gl.LineWidth(3.5f);
            foreach (var beam in combat.Beams)
            {
                Vector3 a = ToRender(beam.From, focus);
                Vector3 b = ToRender(beam.To, focus);
                _lineShader.SetVec4("uColor", beam.R, beam.G, beam.B, 1.0f);
                _lines.Upload(a, b);
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
                _lines.Upload(a, b);
                _lines.DrawStrip();
            }
            _gl.LineWidth(1.5f);
        }

        BurnTrace.Mark("scene: streaks done");
        // additive engine plumes, drawn after all opaque geometry so they composite like light
        DrawPlumes(proj, vmat);
        LatchGlError("plumes");
        if (combat != null) DrawCombatFx(proj, vmat, combat, focus, markerScale);
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

    // Additive combat FX drawn after opaque geometry: each torpedo's drive flare and the expanding blooms
    // from warhead detonations. Pure additive (One,One), depth-tested against hulls but never depth-writing,
    // so the glow composites like light over the scene and fades by intensity rather than alpha.
    private void DrawCombatFx(float[] proj, float[] vmat, CombatManager combat, Vec3 focus, float markerScale)
    {
        if (combat.Munitions.Count == 0 && combat.Explosions.Count == 0) return;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _gl.DepthMask(false);
        _meshShader.Use();
        _meshShader.SetMatrix("uProj", proj);
        _meshShader.SetMatrix("uView", vmat);
        _meshShader.SetVec3("uColor", 0f, 0f, 0f);
        _meshShader.SetFloat("uAmbient", 0f);

        // torpedo drive flares: a warm glow trailing the body, fading in on launch
        float flare = MathF.Max(markerScale * 0.20f, 0.010f);
        foreach (Munition m in combat.Munitions)
        {
            if (!m.Alive || m.Kind != MunitionKind.Missile) continue;
            float fadeIn = (float)Math.Clamp(m.Age / 0.35, 0.0, 1.0);
            Vec3 vdir = m.Velocity.LengthSquared > 1.0 ? m.Velocity.Normalized() : Vec3.UnitX;
            (Vector3 right, Vector3 up, Vector3 fwd) = Basis(vdir);
            Vector3 tail = ToRender(m.Position, focus) - fwd * (markerScale * 0.30f);
            _meshShader.SetVec3("uEmissive", 1.20f * fadeIn, 0.55f * fadeIn, 0.18f * fadeIn);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(tail, flare));
            _sphere.Draw();
        }

        // detonation blooms: a white-hot core inside an orange halo, expanding and fading over their life
        foreach (Explosion x in combat.Explosions)
        {
            float t = x.Life > 0.0 ? (float)Math.Clamp(1.0 - x.Ttl / x.Life, 0.0, 1.0) : 1.0f;
            float fade = 1.0f - t;
            float baseR = markerScale * (float)x.Size;
            Vector3 ctr = ToRender(x.Position, focus);

            float haloR = baseR * (0.5f + 3.0f * t);
            _meshShader.SetVec3("uEmissive", 1.30f * fade, 0.45f * fade, 0.12f * fade);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(ctr, haloR));
            _sphere.Draw();

            float coreR = baseR * (0.35f + 1.1f * t);
            float cf = fade * fade;
            _meshShader.SetVec3("uEmissive", 1.60f * cf, 1.35f * cf, 0.95f * cf);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(ctr, coreR));
            _sphere.Draw();
        }

        _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    // Solid physical bodies for torpedoes and KKV interceptors: a slim cylinder hull with a bright nose,
    // oriented along the velocity vector and growing in over the first third-second so it "materializes" out
    // of the launch tube rather than popping. Drawn opaque (depth-written) so the additive drive flares and
    // detonation blooms (DrawCombatFx) composite over it. Slugs stay as fast streaks (handled elsewhere).
    private void DrawMunitionBodies(float[] proj, float[] vmat, CombatManager combat, Vec3 focus, float markerScale)
    {
        if (combat.Munitions.Count == 0) return;
        _meshShader.Use();
        _meshShader.SetMatrix("uProj", proj);
        _meshShader.SetMatrix("uView", vmat);
        _meshShader.SetFloat("uAmbient", 0.5f);
        _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);

        foreach (Munition m in combat.Munitions)
        {
            if (!m.Alive || m.Kind == MunitionKind.Slug) continue;
            bool kkv = m.Kind == MunitionKind.KKV;
            float grow = (float)Math.Clamp(m.Age / 0.35, 0.06, 1.0);
            Vec3 vdir = m.Velocity.LengthSquared > 1.0 ? m.Velocity.Normalized() : Vec3.UnitX;
            (Vector3 right, Vector3 up, Vector3 fwd) = Basis(vdir);
            Vector3 ctr = ToRender(m.Position, focus);

            float len = markerScale * (kkv ? 0.26f : 0.42f) * grow;
            float rad = markerScale * (kkv ? 0.05f : 0.07f) * grow;

            if (kkv) _meshShader.SetVec3("uColor", 0.78f, 0.74f, 0.30f);   // brassy interceptor
            else     _meshShader.SetVec3("uColor", 0.56f, 0.58f, 0.63f);   // grey torpedo
            _meshShader.SetMatrix("uModel", Mat4.ModelAxes(ctr, right, up, fwd, new Vector3(rad, rad, len)));
            _cyl.Draw();

            Vector3 nose = ctr + fwd * (len * 0.5f);
            _meshShader.SetVec3("uColor", kkv ? 1.0f : 0.85f, kkv ? 0.90f : 0.80f, kkv ? 0.45f : 0.72f);
            _meshShader.SetMatrix("uModel", Mat4.TranslationScale(nose, rad * 1.35f));
            _sphere.Draw();
        }

        _meshShader.SetVec3("uEmissive", 0f, 0f, 0f);
    }

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

    // Maps a hull's physical length (m) to a render-size multiplier so classes read at proportional sizes
    // (a ~30 m drone vs a ~150 m battleship spans roughly 0.6x..3x). 0/unknown falls back to the base size.
    private static float HullSizeFactor(double hullLength) =>
        hullLength > 1.0 ? System.Math.Clamp((float)(hullLength / 50.0), 0.55f, 3.2f) : 1.0f;

    private void DrawHull(Vector3 c, RigidBody v, float hs) =>
        DrawHull(c, v, hs, new Vector3(0.62f, 0.66f, 0.72f), HullArchetype.Standard);

    private void DrawHull(Vector3 c, RigidBody v, float hs, Vector3 hull) =>
        DrawHull(c, v, hs, hull, HullArchetype.Standard);

    // Each hull class has its own model; this Standard body is the destroyer / fast-attack form (the Duat).
    private void DrawHull(Vector3 c, RigidBody v, float hs, Vector3 hull, HullArchetype arch)
    {
        switch (arch)
        {
            case HullArchetype.Drone:         DrawDroneHull(c, v, hs, hull);         return;
            case HullArchetype.LanceDrone:    DrawLanceDroneHull(c, v, hs, hull);    return;
            case HullArchetype.Raider:        DrawRaiderHull(c, v, hs, hull);        return;
            case HullArchetype.Corvette:      DrawCorvetteHull(c, v, hs, hull);      return;
            case HullArchetype.Frigate:       DrawFrigateHull(c, v, hs, hull);       return;
            case HullArchetype.Cruiser:       DrawCruiserHull(c, v, hs, hull);       return;
            case HullArchetype.Battlecruiser: DrawBattlecruiserHull(c, v, hs, hull); return;
            case HullArchetype.Battleship:    DrawBattleshipHull(c, v, hs, hull);    return;
            case HullArchetype.LanceFrigate:  DrawLanceFrigateHull(c, v, hs, hull);  return;
        }

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

    private enum HullArchetype
    { Standard, Corvette, Frigate, Cruiser, Battlecruiser, Battleship, Drone, LanceDrone, LanceFrigate, Raider }

    // map each hull class to its own distinct model (Standard == the destroyer / fast-attack body, the Duat)
    private static HullArchetype ArchetypeFor(Spacecraft ship) => ship.Class switch
    {
        HullClass.Corvette      => HullArchetype.Corvette,
        HullClass.Frigate       => HullArchetype.Frigate,
        HullClass.Cruiser       => HullArchetype.Cruiser,
        HullClass.Battlecruiser => HullArchetype.Battlecruiser,
        HullClass.Battleship    => HullArchetype.Battleship,
        HullClass.Drone         => HullArchetype.Drone,
        HullClass.LanceDrone    => HullArchetype.LanceDrone,
        HullClass.LanceFrigate  => HullArchetype.LanceFrigate,
        HullClass.Raider        => HullArchetype.Raider,
        _                       => HullArchetype.Standard,   // Destroyer + custom/Duat
    };

    // shared mesh-part helper (mirrors the local one inside DrawHull) for the archetype models below
    private void MeshPart(Mesh m, float r, float g, float b, float amb, Vector3 center,
                          Vector3 right, Vector3 up, Vector3 fwd, Vector3 scale, Vector3 emis = default)
    {
        _meshShader.SetVec3("uColor", r, g, b);
        _meshShader.SetFloat("uAmbient", amb);
        _meshShader.SetVec3("uEmissive", emis.X, emis.Y, emis.Z);
        _meshShader.SetMatrix("uModel", Mat4.ModelAxes(center, right, up, fwd, scale));
        m.Draw();
    }

    // queues a torch plume (outer glow + hot core) aft of a single engine, matching the warship plumes
    private void EnginePlume(Vector3 exit, Vector3 right, Vector3 up, Vector3 fwd, float radius, float length)
    {
        if (!_plumesEnabled) return;
        Vector3 aft = -fwd;
        _plumes.Add(new PlumeDraw { Pos = exit, Right = right, Up = up, Aft = aft,
            Radius = radius, Length = length, Color = new Vector3(1.00f, 0.42f, 0.12f), Intensity = 0.85f });
        _plumes.Add(new PlumeDraw { Pos = exit, Right = right, Up = up, Aft = aft,
            Radius = radius * 0.5f, Length = length * 0.7f, Color = new Vector3(1.25f, 1.00f, 0.75f), Intensity = 1.5f });
    }

    // A pirate raider: a scrappy, asymmetric, mostly-skeletal hull - a bare spine, an offset gun and tank, a
    // single radiator fin and one torch. Deliberately sparse so it never reads as a real warship.
    private void DrawRaiderHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z;
        bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_cyl, tr * 0.70f, tg * 0.70f, tb * 0.70f, 0.32f, F(-0.2f), right, up, fwd, S(0.10f, 0.10f, 2.2f));   // bare spine
        MeshPart(_box, tr, tg, tb, 0.42f, F(0.95f) + up * (hs * 0.12f), right, up, fwd, S(0.24f, 0.22f, 0.5f));        // offset cockpit
        MeshPart(_cyl, 0.34f, 0.30f, 0.28f, 0.34f, F(-0.35f), right, up, fwd, S(0.20f, 0.20f, 0.55f),
                 new Vector3(0.10f, 0.04f, 0.02f));                                                                    // reactor drum
        MeshPart(_cyl, tr * 0.85f, tg * 0.85f, tb * 0.85f, 0.30f, F(0.10f) - right * (hs * 0.30f), right, up, fwd,
                 S(0.15f, 0.15f, 0.75f));                                                                              // slung fuel tank (port)
        MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.4f) + up * (hs * 0.50f), right, up, fwd, S(0.04f, 0.62f, 0.5f),
                 new Vector3(0.40f, 0.14f, 0.05f));                                                                    // single radiator fin
        Vector3 gun = F(0.45f) + right * (hs * 0.28f);
        MeshPart(_box, 0.30f, 0.31f, 0.34f, 0.38f, gun, right, up, fwd, S(0.16f, 0.16f, 0.18f));                       // offset cannon base
        MeshPart(_cyl, 0.22f, 0.23f, 0.26f, 0.34f, gun + fwd * (hs * 0.45f), right, up, fwd, S(0.05f, 0.05f, 0.8f));   // barrel
        MeshPart(_cone, 0.16f, 0.17f, 0.20f, 0.34f, F(-1.25f), right, up, fwd, S(0.18f, 0.18f, 0.42f),
                 burning ? new Vector3(0.45f, 0.16f, 0.05f) : default);                                                // stern torch
        if (burning) EnginePlume(F(-1.46f), right, up, fwd, 0.13f * hs, 1.2f * hs);
    }

    // A drone: tiny and simple - a faceted body, a wedge nose, a sensor eye, canard fins, one engine.
    private void DrawDroneHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z;
        bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_box, tr, tg, tb, 0.40f, F(0.0f), right, up, fwd, S(0.34f, 0.26f, 0.95f));                            // faceted body
        MeshPart(_cone, tr, tg, tb, 0.40f, F(0.92f), right, up, fwd, S(0.26f, 0.20f, 0.5f));                           // wedge nose
        MeshPart(_sphere, 0.30f, 0.40f, 0.50f, 0.50f, F(0.55f) + up * (hs * 0.14f), right, up, fwd, S(0.09f, 0.09f, 0.09f),
                 new Vector3(0.05f, 0.10f, 0.16f));                                                                    // sensor eye
        MeshPart(_cyl, 0.28f, 0.29f, 0.32f, 0.36f, F(0.3f) - up * (hs * 0.18f), right, up, fwd, S(0.08f, 0.08f, 0.55f)); // weapon pod
        MeshPart(_box, tr * 0.80f, tg * 0.80f, tb * 0.80f, 0.36f, F(-0.2f) + right * (hs * 0.34f), right, up, fwd,
                 S(0.32f, 0.04f, 0.30f));                                                                              // canard (stbd)
        MeshPart(_box, tr * 0.80f, tg * 0.80f, tb * 0.80f, 0.36f, F(-0.2f) - right * (hs * 0.34f), right, up, fwd,
                 S(0.32f, 0.04f, 0.30f));                                                                              // canard (port)
        MeshPart(_cone, 0.16f, 0.17f, 0.20f, 0.34f, F(-0.66f), right, up, fwd, S(0.16f, 0.16f, 0.34f),
                 burning ? new Vector3(0.45f, 0.16f, 0.05f) : default);                                                // engine
        if (burning) EnginePlume(F(-0.84f), right, up, fwd, 0.10f * hs, 0.9f * hs);
    }

    // a turret: a base block plus a barrel angled from forward toward outDir
    private void Turret(Vector3 at, Vector3 outDir, float size, float barrelLen, Vector3 right, Vector3 up, Vector3 fwd)
    {
        MeshPart(_box, 0.30f, 0.31f, 0.34f, 0.38f, at, right, up, fwd, new Vector3(size * 1.7f, size * 1.7f, size * 1.7f));
        Vector3 bDir = Vector3.Normalize(fwd + Vector3.Normalize(outDir) * 0.45f);
        Vector3 bRight = Vector3.Normalize(Vector3.Cross(up, bDir));
        Vector3 bUp = Vector3.Cross(bDir, bRight);
        MeshPart(_cyl, 0.22f, 0.23f, 0.26f, 0.34f, at + bDir * (barrelLen * 0.5f), bRight, bUp, bDir,
                 new Vector3(size * 0.34f, size * 0.34f, barrelLen));
    }

    // a drive cluster of `count` torch bells in a layout (1 axial, 2 vertical, 3 triangular, 4 quad) + plumes
    private void DriveBlock(Vector3 c, float aftK, int count, float hs, Vector3 right, Vector3 up, Vector3 fwd,
                            bool burning, float spread, float bell)
    {
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 hot = burning ? new Vector3(0.45f, 0.16f, 0.05f) : default;
        MeshPart(_cyl, 0.30f, 0.30f, 0.33f, 0.34f, F(aftK + 0.14f), right, up, fwd,
                 new Vector3(spread * 1.5f * hs + 0.10f * hs, spread * 1.5f * hs + 0.10f * hs, 0.10f * hs));   // mount plate
        for (int k = 0; k < count; k++)
        {
            Vector3 off;
            if (count == 1) off = Vector3.Zero;
            else if (count == 2) off = (k == 0 ? up : -up) * (spread * hs);
            else
            {
                float a = MathF.PI * (0.5f + k * 2.0f / count) + (count == 4 ? MathF.PI / 4f : 0f);
                off = (right * MathF.Cos(a) + up * MathF.Sin(a)) * (spread * hs);
            }
            MeshPart(_sphere, 0.22f, 0.22f, 0.25f, 0.40f, F(aftK) + off, right, up, fwd,
                     new Vector3(bell * 0.5f * hs, bell * 0.5f * hs, bell * 0.5f * hs));                       // gimbal joint
            MeshPart(_cone, 0.16f, 0.17f, 0.20f, 0.32f, F(aftK - 0.20f) + off, right, up, fwd,
                     new Vector3(bell * hs, bell * hs, bell * 2.4f * hs), hot);                                // torch bell
            if (burning) EnginePlume(F(aftK - 0.40f) + off, right, up, fwd, bell * 0.9f * hs, 1.4f * hs);
        }
    }

    // CORVETTE: small, sleek interceptor - slim fuselage, swept canards, dorsal sensor blister, chin gun,
    // twin outboard engines on stub pylons.
    private void DrawCorvetteHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_noseFrustum, tr, tg, tb, 0.34f, F(0.9f), right, up, fwd, S(0.20f, 0.18f, 1.5f));             // tapered bow
        MeshPart(_cyl, tr * 0.92f, tg * 0.92f, tb * 0.92f, 0.32f, F(-0.35f), right, up, fwd, S(0.22f, 0.20f, 1.3f)); // body
        MeshPart(_box, tr * 0.8f, tg * 0.8f, tb * 0.8f, 0.36f, F(0.35f) + right * (hs * 0.34f), right, up, fwd, S(0.34f, 0.03f, 0.22f)); // canard
        MeshPart(_box, tr * 0.8f, tg * 0.8f, tb * 0.8f, 0.36f, F(0.35f) - right * (hs * 0.34f), right, up, fwd, S(0.34f, 0.03f, 0.22f));
        MeshPart(_sphere, 0.32f, 0.35f, 0.40f, 0.46f, F(0.55f) + up * (hs * 0.18f), right, up, fwd, S(0.12f, 0.10f, 0.12f),
                 new Vector3(0.04f, 0.06f, 0.10f));                                                            // dorsal blister
        Turret(F(0.2f) - up * (hs * 0.18f), -up, 0.10f * hs, 0.5f * hs, right, up, fwd);                       // chin gun
        foreach (float s in new[] { 1f, -1f })
        {
            MeshPart(_box, 0.30f, 0.31f, 0.34f, 0.36f, F(-0.7f) + right * (s * hs * 0.26f), right, up, fwd, S(0.05f, 0.05f, 0.4f)); // pylon
            MeshPart(_cone, 0.16f, 0.17f, 0.20f, 0.32f, F(-1.05f) + right * (s * hs * 0.30f), right, up, fwd, S(0.12f, 0.12f, 0.3f),
                     burning ? new Vector3(0.45f, 0.16f, 0.05f) : default);
            if (burning) EnginePlume(F(-1.22f) + right * (s * hs * 0.30f), right, up, fwd, 0.10f * hs, 1.0f * hs);
        }
    }

    // FRIGATE: boxy modular escort - a rectangular hull, external tanks slung on both flanks, a tall dorsal
    // sensor mast, two dorsal light turrets, radiator panels, twin inline engines.
    private void DrawFrigateHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_box, tr, tg, tb, 0.32f, F(0.0f), right, up, fwd, S(0.50f, 0.42f, 2.6f));                     // main hull
        MeshPart(_box, MathF.Min(tr * 1.02f, 1f), tg, tb, 0.34f, F(1.5f), right, up, fwd, S(0.38f, 0.34f, 0.6f)); // blunt prow
        foreach (float s in new[] { 1f, -1f })
            MeshPart(_cyl, tr * 0.85f, tg * 0.85f, tb * 0.85f, 0.30f, F(-0.1f) + right * (s * hs * 0.42f), right, up, fwd, S(0.16f, 0.16f, 1.6f)); // flank tank
        MeshPart(_box, 0.30f, 0.33f, 0.38f, 0.42f, F(0.4f) + up * (hs * 0.42f), right, up, fwd, S(0.06f, 0.5f, 0.10f));   // sensor mast
        MeshPart(_sphere, 0.34f, 0.38f, 0.44f, 0.46f, F(0.4f) + up * (hs * 0.66f), right, up, fwd, S(0.10f, 0.10f, 0.10f),
                 new Vector3(0.05f, 0.07f, 0.10f));                                                            // mast head
        Turret(F(0.8f) + up * (hs * 0.24f), up, 0.10f * hs, 0.5f * hs, right, up, fwd);
        Turret(F(-0.6f) + up * (hs * 0.24f), up, 0.10f * hs, 0.5f * hs, right, up, fwd);
        foreach (float s in new[] { 1f, -1f })
            MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.3f) + right * (s * hs * 0.62f), right, up, fwd, S(0.03f, 0.36f, 1.0f),
                     new Vector3(0.35f, 0.13f, 0.05f));                                                        // radiator panel
        DriveBlock(c, -1.5f, 2, hs, right, up, fwd, burning, 0.18f, 0.14f);
    }

    // CRUISER: built around a prominent dorsal spinal railgun running bow-to-amidships (muzzle past the bow),
    // with stabiliser fins, side secondary turrets, radiator fins and a triple engine cluster.
    private void DrawCruiserHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_cyl, tr, tg, tb, 0.30f, F(-0.2f), right, up, fwd, S(0.34f, 0.34f, 2.8f));                    // main hull
        MeshPart(_noseFrustum, tr, tg, tb, 0.30f, F(1.9f), right, up, fwd, S(0.34f, 0.34f, 1.6f));             // bow taper
        MeshPart(_cyl, 0.40f, 0.42f, 0.46f, 0.36f, F(0.6f) + up * (hs * 0.30f), right, up, fwd, S(0.10f, 0.10f, 3.4f)); // spinal rail
        MeshPart(_box, 0.30f, 0.31f, 0.34f, 0.38f, F(-0.9f) + up * (hs * 0.30f), right, up, fwd, S(0.22f, 0.22f, 0.5f)); // breech
        MeshPart(_cyl, 0.50f, 0.45f, 0.42f, 0.40f, F(2.3f) + up * (hs * 0.30f), right, up, fwd, S(0.12f, 0.12f, 0.3f),
                 new Vector3(0.14f, 0.10f, 0.06f));                                                            // muzzle
        MeshPart(_box, tr * 0.8f, tg * 0.8f, tb * 0.8f, 0.36f, F(-1.2f) + up * (hs * 0.5f), right, up, fwd, S(0.04f, 0.6f, 0.7f)); // dorsal fin
        MeshPart(_box, tr * 0.8f, tg * 0.8f, tb * 0.8f, 0.36f, F(-1.2f) - up * (hs * 0.5f), right, up, fwd, S(0.04f, 0.6f, 0.7f)); // ventral fin
        Turret(F(0.9f) + right * (hs * 0.34f), right, 0.11f * hs, 0.5f * hs, right, up, fwd);
        Turret(F(0.9f) - right * (hs * 0.34f), -right, 0.11f * hs, 0.5f * hs, right, up, fwd);
        foreach (float s in new[] { 1f, -1f })
            MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.5f) + right * (s * hs * 0.42f), right, up, fwd, S(0.04f, 0.5f, 1.2f),
                     new Vector3(0.35f, 0.13f, 0.05f));                                                        // radiator fin
        DriveBlock(c, -1.7f, 3, hs, right, up, fwd, burning, 0.22f, 0.16f);
    }

    // BATTLECRUISER: a long slab-sided hull with broadside sponson batteries down both flanks, a forward
    // command tower, dorsal/ventral radiator panels and a quad engine cluster.
    private void DrawBattlecruiserHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_box, tr, tg, tb, 0.30f, F(0.0f), right, up, fwd, S(0.46f, 0.40f, 3.2f));                     // slab hull
        MeshPart(_noseFrustum, tr, tg, tb, 0.30f, F(2.0f), right, up, fwd, S(0.40f, 0.36f, 1.2f));             // prow
        MeshPart(_box, tr * 0.95f, tg * 0.95f, tb * 0.95f, 0.40f, F(0.9f) + up * (hs * 0.42f), right, up, fwd, S(0.22f, 0.34f, 0.6f)); // tower
        MeshPart(_sphere, 0.34f, 0.38f, 0.44f, 0.46f, F(0.9f) + up * (hs * 0.66f), right, up, fwd, S(0.10f, 0.10f, 0.10f),
                 new Vector3(0.05f, 0.07f, 0.10f));
        for (int i = 0; i < 3; i++)
        {
            float k = 0.9f - i * 0.9f;
            Turret(F(k) + right * (hs * 0.48f), right, 0.13f * hs, 0.6f * hs, right, up, fwd);                 // broadside (stbd)
            Turret(F(k) - right * (hs * 0.48f), -right, 0.13f * hs, 0.6f * hs, right, up, fwd);                // broadside (port)
        }
        MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.8f) + up * (hs * 0.5f), right, up, fwd, S(0.5f, 0.04f, 1.4f), new Vector3(0.35f, 0.13f, 0.05f));
        MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.8f) - up * (hs * 0.5f), right, up, fwd, S(0.5f, 0.04f, 1.4f), new Vector3(0.35f, 0.13f, 0.05f));
        DriveBlock(c, -1.9f, 4, hs, right, up, fwd, burning, 0.26f, 0.16f);
    }

    // BATTLESHIP: a massive, bulky dreadnought - armoured belt blocks, a tall central citadel, four large main
    // turrets (dorsal fore+aft and ventral), heavy radiator panels and a large quad engine cluster.
    private void DrawBattleshipHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_box, tr, tg, tb, 0.30f, F(0.0f), right, up, fwd, S(0.62f, 0.52f, 3.4f));                     // bulk hull
        MeshPart(_noseFrustum, tr, tg, tb, 0.30f, F(2.1f), right, up, fwd, S(0.54f, 0.46f, 1.2f));             // prow
        foreach (float s in new[] { 1f, -1f })
            MeshPart(_box, tr * 0.78f, tg * 0.78f, tb * 0.78f, 0.34f, F(0.0f) + right * (s * hs * 0.60f), right, up, fwd, S(0.10f, 0.40f, 2.8f)); // armour belt
        MeshPart(_box, tr * 0.95f, tg * 0.95f, tb * 0.95f, 0.40f, F(0.3f) + up * (hs * 0.5f), right, up, fwd, S(0.34f, 0.5f, 0.9f)); // citadel
        MeshPart(_box, 0.30f, 0.33f, 0.38f, 0.44f, F(0.3f) + up * (hs * 0.86f), right, up, fwd, S(0.10f, 0.22f, 0.16f)); // mast
        Turret(F(1.2f) + up * (hs * 0.52f), up, 0.22f * hs, 1.0f * hs, right, up, fwd);                        // dorsal fore
        Turret(F(-1.0f) + up * (hs * 0.52f), up, 0.22f * hs, 1.0f * hs, right, up, fwd);                       // dorsal aft
        Turret(F(0.7f) - up * (hs * 0.52f), -up, 0.20f * hs, 0.9f * hs, right, up, fwd);                       // ventral fore
        Turret(F(-1.3f) - up * (hs * 0.52f), -up, 0.20f * hs, 0.9f * hs, right, up, fwd);                      // ventral aft
        foreach (float s in new[] { 0.66f, -0.66f })
            MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-1.0f) + right * (s * hs), right, up, fwd, S(0.04f, 0.7f, 1.5f), new Vector3(0.35f, 0.13f, 0.05f));
        DriveBlock(c, -2.0f, 4, hs, right, up, fwd, burning, 0.32f, 0.20f);
    }

    // LANCE DRONE: a tiny DEW craft dominated by a forward focusing lens, with cooling fins and one engine -
    // deliberately emitter-led, not a shrunk warship.
    private void DrawLanceDroneHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_cyl, tr, tg, tb, 0.40f, F(-0.1f), right, up, fwd, S(0.26f, 0.26f, 0.9f));                    // compact body
        MeshPart(_cyl, 0.42f, 0.40f, 0.44f, 0.40f, F(0.55f), right, up, fwd, S(0.30f, 0.30f, 0.34f));          // emitter housing
        MeshPart(_sphere, 0.55f, 0.42f, 0.34f, 0.46f, F(0.82f), right, up, fwd, S(0.26f, 0.26f, 0.18f),
                 new Vector3(0.22f, 0.12f, 0.05f));                                                            // glowing lens
        MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.35f) + up * (hs * 0.34f), right, up, fwd, S(0.03f, 0.4f, 0.4f), new Vector3(0.35f, 0.13f, 0.05f));
        MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.35f) - up * (hs * 0.34f), right, up, fwd, S(0.03f, 0.4f, 0.4f), new Vector3(0.35f, 0.13f, 0.05f));
        MeshPart(_cone, 0.16f, 0.17f, 0.20f, 0.34f, F(-0.62f), right, up, fwd, S(0.14f, 0.14f, 0.32f),
                 burning ? new Vector3(0.45f, 0.16f, 0.05f) : default);
        if (burning) EnginePlume(F(-0.8f), right, up, fwd, 0.09f * hs, 0.8f * hs);
    }

    // LANCE FRIGATE: a DEW platform - a slender power spine, a large flared focusing dish at the bow, heavy
    // radiator panels (lasers run hot), a reactor drum and twin engines. Reads as a beam ship, not a gunship.
    private void DrawLanceFrigateHull(Vector3 c, RigidBody v, float hs, Vector3 hull)
    {
        (Vector3 right, Vector3 up, Vector3 fwd) = Basis(v.Forward);
        Vector3 F(float k) => c + fwd * (k * hs);
        Vector3 S(float a, float b, float d) => new(a * hs, b * hs, d * hs);
        float tr = hull.X, tg = hull.Y, tb = hull.Z; bool burning = v.ThrustWorld.Length > 1e-6;

        MeshPart(_cyl, tr, tg, tb, 0.32f, F(-0.4f), right, up, fwd, S(0.28f, 0.28f, 2.4f));                    // power spine
        MeshPart(_cyl, 0.40f, 0.42f, 0.46f, 0.36f, F(1.1f), right, up, fwd, S(0.40f, 0.40f, 0.5f));            // emitter drum
        MeshPart(_cone, 0.42f, 0.40f, 0.44f, 0.38f, F(1.6f), right, up, fwd, S(0.5f, 0.5f, 0.6f));             // flared dish
        MeshPart(_sphere, 0.60f, 0.45f, 0.36f, 0.46f, F(1.85f), right, up, fwd, S(0.30f, 0.30f, 0.20f),
                 new Vector3(0.26f, 0.14f, 0.06f));                                                            // focal lens
        foreach (float s in new[] { 1f, -1f })
        {
            MeshPart(_box, 0.20f, 0.16f, 0.15f, 0.40f, F(-0.6f) + right * (s * hs * 0.5f), right, up, fwd, S(0.04f, 0.8f, 1.6f), new Vector3(0.38f, 0.14f, 0.05f)); // big panel
            MeshPart(_box, 0.05f, 0.05f, 0.06f, 0.34f, F(-0.6f) + right * (s * hs * 0.26f), right, up, fwd, S(0.04f, 0.04f, 1.6f)); // panel spar
        }
        MeshPart(_cyl, 0.36f, 0.32f, 0.30f, 0.34f, F(-0.9f), right, up, fwd, S(0.26f, 0.26f, 0.5f), new Vector3(0.10f, 0.04f, 0.02f)); // reactor
        DriveBlock(c, -1.6f, 2, hs, right, up, fwd, burning, 0.18f, 0.15f);
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
