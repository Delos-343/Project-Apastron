using System;
using Apastron.Core;

namespace Apastron.Simulation;

/// <summary>
/// Classical (Keplerian) orbital elements derived from a state vector relative to a
/// single primary body. Valid for the two-body approximation, which is exact here
/// because the bodies are fixed and gravity from the primary dominates.
/// </summary>
public readonly struct OrbitalElements
{
    public double SemiMajorAxis   { get; init; }   // a   (m)   negative for hyperbolic
    public double Eccentricity    { get; init; }   // e   (dimensionless)
    public double Inclination     { get; init; }   // i   (rad)
    public double Periapsis       { get; init; }   // r_p (m, from centre)
    public double Apoapsis        { get; init; }   // r_a (m, from centre) NaN if unbound
    public double Period          { get; init; }   // T   (s) +inf if unbound
    public double SpecificEnergy  { get; init; }   // eps (J/kg)
    public double Altitude        { get; init; }   // |r| - R_body (m)
    public double Speed           { get; init; }   // |v| (m/s)
    public double RadialDistance  { get; init; }   // |r| (m, from centre)
    public bool   IsBound         { get; init; }   // e < 1

    /// <summary>
    /// Compute elements for a vessel at <paramref name="position"/>/<paramref name="velocity"/>
    /// relative to a primary at <paramref name="bodyPosition"/> with parameter <paramref name="mu"/>.
    /// </summary>
    public static OrbitalElements Compute(
        Vec3 position, Vec3 velocity, Vec3 bodyPosition, double mu, double bodyRadius)
    {
        Vec3 r = position - bodyPosition;
        Vec3 v = velocity;                  // bodies are fixed -> relative velocity == velocity

        double rMag = r.Length;
        double vMag = v.Length;

        // Specific orbital energy (vis-viva): eps = v^2/2 - mu/r
        double energy = 0.5 * vMag * vMag - mu / rMag;

        // Specific angular momentum h = r x v
        Vec3 h = Vec3.Cross(r, v);
        double hMag = h.Length;

        // Eccentricity vector: e = ((v^2 - mu/r) r - (r.v) v) / mu
        double rv = Vec3.Dot(r, v);
        Vec3 eVec = ((vMag * vMag - mu / rMag) * r - rv * v) / mu;
        double e = eVec.Length;

        // Semi-major axis from energy: a = -mu / (2 eps)
        double a = (Math.Abs(energy) > 1e-12) ? -mu / (2.0 * energy) : double.PositiveInfinity;

        // Inclination from the angular-momentum z-component
        double inc = (hMag > 1e-9) ? Math.Acos(Math.Clamp(h.Z / hMag, -1.0, 1.0)) : 0.0;

        bool bound = e < 1.0 && !double.IsInfinity(a);

        double rp = a * (1.0 - e);
        double ra = bound ? a * (1.0 + e) : double.NaN;
        double period = bound ? MathConstants.TwoPi * Math.Sqrt(a * a * a / mu) : double.PositiveInfinity;

        return new OrbitalElements
        {
            SemiMajorAxis  = a,
            Eccentricity   = e,
            Inclination    = inc,
            Periapsis      = rp,
            Apoapsis       = ra,
            Period         = period,
            SpecificEnergy = energy,
            Altitude       = rMag - bodyRadius,
            Speed          = vMag,
            RadialDistance = rMag,
            IsBound        = bound,
        };
    }
}
