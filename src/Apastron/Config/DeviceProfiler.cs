namespace Apastron.Config;

/// <summary>A coarse description of the host GPU, derived from OpenGL strings.</summary>
public sealed class DeviceProfile
{
    public string Vendor      { get; init; } = "Unknown";
    public string Renderer    { get; init; } = "Unknown";
    public string GlVersion   { get; init; } = "Unknown";
    public QualityTier RecommendedTier { get; init; } = QualityTier.Medium;

    /// <summary>
    /// Build a profile from the GL_VENDOR / GL_RENDERER / GL_VERSION strings using a
    /// keyword heuristic. This is intentionally conservative; precise VRAM querying via
    /// vendor extensions (NVX_gpu_memory_info, ATI_meminfo) is a later refinement.
    /// </summary>
    public static DeviceProfile FromStrings(string vendor, string renderer, string glVersion)
    {
        string r = renderer.ToLowerInvariant();
        string v = vendor.ToLowerInvariant();

        QualityTier tier = QualityTier.Medium;

        // Software / fallback rasterisers -> lowest tier.
        if (r.Contains("llvmpipe") || r.Contains("softpipe") || r.Contains("software") ||
            r.Contains("swiftshader") || r.Contains("microsoft basic"))
        {
            tier = QualityTier.Low;
        }
        // Discrete high-end keywords -> top tier.
        else if (r.Contains("rtx") || r.Contains("radeon rx") || r.Contains("arc") ||
                 r.Contains("quadro") || r.Contains("geforce gtx 16") || r.Contains("geforce gtx 10"))
        {
            tier = QualityTier.Ultra;
        }
        // Integrated graphics -> medium/low.
        else if (r.Contains("intel") || v.Contains("intel") || r.Contains("uhd") ||
                 r.Contains("iris") || r.Contains("vega") && r.Contains("graphics"))
        {
            tier = QualityTier.Medium;
        }
        else if (r.Contains("nvidia") || v.Contains("nvidia") || r.Contains("amd") || v.Contains("amd"))
        {
            tier = QualityTier.High;
        }

        return new DeviceProfile
        {
            Vendor = vendor,
            Renderer = renderer,
            GlVersion = glVersion,
            RecommendedTier = tier,
        };
    }

    /// <summary>Apply this profile's recommended tier to a <see cref="GraphicsSettings"/>.</summary>
    public void ApplyTo(GraphicsSettings g)
    {
        g.TextureQuality = RecommendedTier;
        g.ShadowQuality  = RecommendedTier == QualityTier.Ultra ? QualityTier.High : RecommendedTier;

        g.MsaaSamples = RecommendedTier switch
        {
            QualityTier.Low    => 1,
            QualityTier.Medium => 2,
            QualityTier.High   => 4,
            QualityTier.Ultra  => 8,
            _                  => 2,
        };

        g.RenderScale = RecommendedTier == QualityTier.Low ? 0.85 : 1.0;
    }
}
