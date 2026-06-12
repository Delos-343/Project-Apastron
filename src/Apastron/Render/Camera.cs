using System;
using System.Numerics;

namespace Apastron.Render;

/// <summary>
/// An orbit ("arcball") camera that looks at the render-space origin — which the scene
/// keeps pinned to the focus body via the floating-origin transform. Controlled by
/// yaw/pitch (mouse drag) and distance (scroll). Distance is in render units.
/// </summary>
public sealed class Camera
{
    public const float DefaultYaw      = 0.6f;
    public const float DefaultPitch    = 0.35f;
    public const float DefaultDistance = 28.0f;

    public const float MinDistance = 8.0f;
    public const float MaxDistance = 8000.0f;

    private float _yaw   = DefaultYaw;
    private float _pitch = DefaultPitch;
    private float _defaultDistance = DefaultDistance;

    public float Distance { get; private set; } = DefaultDistance;

    public void Rotate(float dYaw, float dPitch)
    {
        _yaw   -= dYaw;
        _pitch -= dPitch;
        float limit = 1.55f;   // ~88.8 deg, keep clear of the poles
        if (_pitch >  limit) _pitch =  limit;
        if (_pitch < -limit) _pitch = -limit;
    }

    public void Zoom(float steps)
    {
        Distance *= MathF.Pow(0.88f, steps);
        if (Distance < MinDistance) Distance = MinDistance;
        if (Distance > MaxDistance) Distance = MaxDistance;
    }

    public void Reset()
    {
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        Distance = _defaultDistance;
    }

    /// <summary>Set the standoff distance (also used as the reset target) for the current scenario.</summary>
    public void SetDefaultDistance(float d)
    {
        if (d < MinDistance) d = MinDistance;
        if (d > MaxDistance) d = MaxDistance;
        _defaultDistance = d;
        Distance = d;
    }

    public Vector3 Eye()
    {
        float cp = MathF.Cos(_pitch);
        return new Vector3(
            Distance * cp * MathF.Sin(_yaw),
            Distance * MathF.Sin(_pitch),
            Distance * cp * MathF.Cos(_yaw));
    }

    public float[] View() => Mat4.LookAt(Eye(), Vector3.Zero, Vector3.UnitY);
}
