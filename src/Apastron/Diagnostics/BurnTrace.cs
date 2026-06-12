namespace Apastron.Diagnostics;

/// <summary>
/// Fine-grained, short-lived execution trace, armed by the first thrust application. For the
/// next few update/render cycles every instrumented stage drops a write-ahead breadcrumb (via
/// <see cref="CrashLog.Phase"/>), so a native fault that kills the process mid-frame leaves the
/// exact dying stage as the last line of crash.log. The 0xC0000409 fail-fast seen on this
/// machine cannot be caught from managed code, so bracketing it this tightly is the only way
/// to attribute it. After the armed frames elapse, every site costs one integer comparison.
/// </summary>
public static class BurnTrace
{
    private static bool _armed;
    private static int _framesLeft;

    /// <summary>Arms the tracer once per session (subsequent calls are no-ops).</summary>
    public static void Arm(int frames = 4)
    {
        if (_armed) return;
        _armed = true;
        _framesLeft = frames;
        CrashLog.Phase("burntrace armed (first thrust)");
    }

    /// <summary>Drop an ordered breadcrumb if the tracer is live. [n] counts down per frame.</summary>
    public static void Mark(string site)
    {
        if (_framesLeft > 0) CrashLog.Phase($"bt[{_framesLeft}] {site}");
    }

    /// <summary>Call once at the end of each render frame to consume one traced frame.</summary>
    public static void EndFrame()
    {
        if (_framesLeft > 0) _framesLeft--;
    }
}
