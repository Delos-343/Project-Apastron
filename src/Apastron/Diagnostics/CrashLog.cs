using System;
using System.IO;
using System.Text;

namespace Apastron.Diagnostics;

/// <summary>
/// Last-resort diagnostics. Captures unhandled and per-frame exceptions to a flat log file
/// next to the config (%APPDATA%/Apastron/crash.log on Windows) and keeps the most recent
/// message in memory so the UI can surface it on-screen. This exists so a runtime fault
/// reports *what* failed (message + stack) instead of the process vanishing silently - the
/// game can't be single-stepped in a debugger on every machine, so the log is the ground truth.
/// </summary>
public static class CrashLog
{
    private static readonly object _gate = new();

    /// <summary>The most recent error text, for an on-screen banner. Null when clean.</summary>
    public static string? LastError { get; private set; }

    /// <summary>How many times <see cref="Report"/> has fired this session.</summary>
    public static int Count { get; private set; }

    public static string Path
    {
        get
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apastron");
            return System.IO.Path.Combine(dir, "crash.log");
        }
    }

    /// <summary>Wire the process-wide handlers. Call once at startup.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report("AppDomain.UnhandledException", ex);
            else Write("AppDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "(null)", null, surface: true);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        Write("session", $"Apastron started {DateTime.Now:u}", null, surface: false);
    }

    /// <summary>Record an exception with context (e.g. the frame phase that threw). Surfaced on-screen.</summary>
    public static void Report(string context, Exception ex)
        => Write(context, ex.GetType().Name + ": " + ex.Message, ex.ToString(), surface: true);

    /// <summary>Record a non-exception diagnostic (e.g. a GL error code). Logged to disk only, not
    /// surfaced in the on-screen banner.</summary>
    public static void Note(string context, string message)
        => Write(context, message, null, surface: false);

    private static readonly System.Collections.Generic.HashSet<string> _phases = new();

    /// <summary>
    /// Write-ahead breadcrumb for native-fault hunting: logs the FIRST occurrence of a named
    /// phase to disk *before* the phase executes. A native access violation (e.g. inside the
    /// GL driver) kills the process without unwinding managed frames, so it can never be
    /// caught - but the last breadcrumb on disk names the phase that was entered and never
    /// completed. One write per unique key per session; subsequent calls are a HashSet lookup.
    /// </summary>
    public static void Phase(string key)
    {
        lock (_gate)
        {
            if (!_phases.Add(key)) return;
        }
        // File.AppendAllText opens, writes and closes - the line is on disk before returning.
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.AppendAllText(Path, $"{DateTime.Now:u}  [phase]  {key}{Environment.NewLine}");
        }
        catch { /* never crash from logging */ }
        Console.Error.WriteLine($"[phase] {key}");
    }

    /// <summary>
    /// True when the most recent session recorded in crash.log armed the burn tracer (first
    /// thrust) but never completed a traced frame and never shut down cleanly - the signature of
    /// the silent native fast-fail (exit 0xC0000409) observed on a 2023-era AMD OpenGL driver.
    /// Used at startup to engage <c>GraphicsSettings.DriverSafeMode</c>. Must be called BEFORE
    /// <see cref="Install"/>, which appends the new session header this method keys off.
    /// </summary>
    public static bool PreviousSessionDiedDuringFirstBurn()
    {
        try
        {
            if (!File.Exists(Path)) return false;
            string[] lines = File.ReadAllLines(Path);
            int start = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Contains("[session]")) { start = i; break; }
            if (start < 0) return false;

            bool armed = false, completed = false, clean = false, wuBegan = false, wuDone = false;
            for (int i = start; i < lines.Length; i++)
            {
                if (lines[i].Contains("burntrace armed"))             armed     = true;
                if (lines[i].Contains("imgui done (frame complete)")) completed = true;
                if (lines[i].Contains("clean shutdown"))              clean     = true;
                if (lines[i].Contains("shader warm-up: begin"))       wuBegan   = true;
                if (lines[i].Contains("shader warm-up: complete"))    wuDone    = true;
            }
            // Two fingerprints of the native driver fast-fail: died mid-flight on the first
            // burning frame, or died inside the load-time shader warm-up itself.
            return (armed && !completed && !clean) || (wuBegan && !wuDone && !clean);
        }
        catch { return false; }   // diagnostics must never block startup
    }

    private static void Write(string context, string headline, string? detail, bool surface)
    {
        lock (_gate)
        {
            if (surface)
            {
                Count++;
                LastError = $"[{context}] {headline}";
            }
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (dir != null) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("u")).Append("  [").Append(context).Append("]  ").AppendLine(headline);
                if (detail != null) sb.AppendLine(detail);
                sb.AppendLine(new string('-', 72));
                File.AppendAllText(Path, sb.ToString());
            }
            catch
            {
                // Logging must never itself crash the process; a failed write is silently dropped.
            }
            Console.Error.WriteLine(surface ? $"[crash] {context}: {headline}" : $"[log] {context}: {headline}");
        }
    }

    public static void Clear() => LastError = null;
}
