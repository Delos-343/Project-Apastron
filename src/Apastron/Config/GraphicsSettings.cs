namespace Apastron.Config;

/// <summary>Coarse quality bucket used by several individual settings.</summary>
public enum QualityTier { Low, Medium, High, Ultra }

/// <summary>Gen X Soft Club UI palette variants.</summary>
public enum UiPalette { Cool, Warm, Dusk }

/// <summary>
/// User-tunable rendering settings, consumed live by the 3D renderer: render scale and
/// MSAA drive the offscreen render target, draw distance sets the far clip plane, texture
/// quality currently drives sphere tessellation (LOD), and VSync / target FPS are applied
/// to the window. Surfaced in the Settings window, persisted, and auto-detected from the GPU.
/// </summary>
public sealed class GraphicsSettings
{
    /// <summary>Internal render-resolution multiplier (0.5 = half-res upscaled, 2.0 = super-sampled).</summary>
    public double RenderScale { get; set; } = 1.0;

    public QualityTier TextureQuality { get; set; } = QualityTier.High;
    public QualityTier ShadowQuality  { get; set; } = QualityTier.Medium;

    /// <summary>MSAA sample count: 1 (off), 2, 4, or 8.</summary>
    public int MsaaSamples { get; set; } = 4;

    /// <summary>Far render/clip distance in metres. Default 5e9 m covers cislunar space comfortably.</summary>
    public double MaxRenderDistance { get; set; } = 5.0e9;

    public bool VSync { get; set; } = true;

    /// <summary>When true, the scene is run through the soft-focus / cross-process film-grade pass.</summary>
    public bool FilmGrade { get; set; } = true;

    /// <summary>Which "Gen X Soft Club" UI palette to apply (cool slate / warm tan / dusk mauve).</summary>
    public UiPalette Palette { get; set; } = UiPalette.Cool;

    /// <summary>Frame-rate cap applied when VSync is off.</summary>
    public int TargetFps { get; set; } = 144;

    /// <summary>When true, settings are derived from the detected GPU at startup.</summary>
    public bool AutoDetect { get; set; } = true;

    /// <summary>
    /// When true, engine plumes draw through the additive glow shader; when false, the solid
    /// emissive fallback cone is used. Exposed (and auto-disabled by <see cref="DriverSafeMode"/>)
    /// because a 2023-era AMD OpenGL driver fast-fails inside the driver the first time the
    /// additive pipeline runs; see the README's troubleshooting section.
    /// </summary>
    public bool AdditivePlumes { get; set; } = true;

    /// <summary>
    /// Crash-adaptive fallback, set automatically when the previous session died mid-frame on the
    /// first burning frame (the silent 0xC0000409 driver-fault signature recorded in crash.log).
    /// While true the additive plume shader and the film-grade post pass are bypassed so the game
    /// stays playable; clear it from Settings &gt; Appearance once the GPU driver is updated.
    /// </summary>
    public bool DriverSafeMode { get; set; } = false;

    public GraphicsSettings Clone() => new()
    {
        RenderScale       = RenderScale,
        TextureQuality    = TextureQuality,
        ShadowQuality     = ShadowQuality,
        MsaaSamples       = MsaaSamples,
        MaxRenderDistance = MaxRenderDistance,
        VSync             = VSync,
        FilmGrade         = FilmGrade,
        Palette           = Palette,
        TargetFps         = TargetFps,
        AutoDetect        = AutoDetect,
        AdditivePlumes    = AdditivePlumes,
        DriverSafeMode    = DriverSafeMode,
    };
}
