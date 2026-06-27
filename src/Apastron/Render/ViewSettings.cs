namespace Apastron.Render;

/// <summary>What the camera keeps centred via the floating-origin transform.</summary>
public enum CameraFocus { Body, Vessel, Target }

/// <summary>Mutable render/camera preferences, driven by the Camera UI panel.</summary>
public sealed class ViewSettings
{
    public CameraFocus Focus = CameraFocus.Body;
    public float FovDegrees = 45.0f;

    /// <summary>When &gt;= 0 and a valid index into world.Vessels, the camera locks onto that specific
    /// vessel (Homeworld-style selection of any ship), overriding <see cref="Focus"/>. -1 = follow the
    /// Focus mode. Set by clicking a ship, cycling with Tab, or the focus list in the combat panel.</summary>
    public int FocusVesselIndex = -1;
    public bool ShowOrbitPath = true;
    public bool ShowVesselMarker = true;
    public bool ShowEnginePlume = true;

    /// <summary>Current camera distance in render units, written by the renderer for display.</summary>
    public double Distance;

    /// <summary>Set by the UI to ask the renderer to reset the camera next frame.</summary>
    public bool ResetRequested;
}
