using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apastron.Config;

/// <summary>Simulation-side preferences (integrator choice, step size, time scale).</summary>
public sealed class SimulationSettings
{
    /// <summary>Integrator display name; resolved to an instance by SettingsWindow.MakeIntegrator.</summary>
    public string Integrator { get; set; } = "Velocity Verlet";

    /// <summary>Fixed physics step (s). Smaller = more accurate, more CPU.</summary>
    public double FixedStep { get; set; } = 1.0;

    /// <summary>Upper bound on sub-steps per frame so heavy time-warp can't stall a frame.</summary>
    public int MaxStepsPerFrame { get; set; } = 20000;

    /// <summary>Wall-clock-to-sim-time multiplier (time warp).</summary>
    public double TimeScale { get; set; } = 1.0;
}

/// <summary>
/// Root application configuration, persisted as JSON under
/// <c>%APPDATA%/Apastron/config.json</c>.
/// </summary>
public sealed class AppConfig
{
    public GraphicsSettings   Graphics   { get; set; } = new();
    public SimulationSettings Simulation { get; set; } = new();

    [JsonIgnore]
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apastron");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Load config from disk, or return defaults if absent/unreadable.</summary>
    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] load failed, using defaults: {ex.Message}");
        }
        return new AppConfig();
    }

    /// <summary>Persist config to disk, creating the directory if needed.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] save failed: {ex.Message}");
        }
    }
}
