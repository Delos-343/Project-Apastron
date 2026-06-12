using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Apastron.Combat;

/// <summary>
/// Persists the player's best score per mission to %APPDATA%/Apastron/scores.json.
/// All file access is best-effort and wrapped in try/catch, so a missing or unwritable
/// profile directory simply yields no saved scores rather than disrupting play.
/// </summary>
public static class ScoreBoard
{
    private static Dictionary<string, int>? _cache;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apastron", "scores.json");

    private static Dictionary<string, int> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            string p = FilePath;
            _cache = File.Exists(p)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(p)) ?? new()
                : new();
        }
        catch { _cache = new(); }
        return _cache;
    }

    public static int GetBest(string mission)
    {
        var d = Load();
        return d.TryGetValue(mission, out int v) ? v : 0;
    }

    /// <summary>Record a score; returns the best (after any update) and reports whether it set a new record.</summary>
    public static int Submit(string mission, int score, out bool isNewBest)
    {
        var d = Load();
        int prev = d.TryGetValue(mission, out int v) ? v : 0;
        isNewBest = score > prev;
        if (isNewBest)
        {
            d[mission] = score;
            try
            {
                string p = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(d));
            }
            catch { /* persistence is best-effort */ }
        }
        return d.TryGetValue(mission, out int b) ? b : score;
    }

    public static string RatingFor(int score) =>
        score >= 4000 ? "S" : score >= 3000 ? "A" : score >= 2000 ? "B" : score >= 1000 ? "C" : "D";
}
