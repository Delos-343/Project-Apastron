namespace Apastron.Core;

/// <summary>Physical constants and standard gravitational parameters (SI units).</summary>
public static class MathConstants
{
    /// <summary>Newtonian constant of gravitation (m^3 kg^-1 s^-2).</summary>
    public const double G = 6.67430e-11;

    /// <summary>Standard gravity g0 (m/s^2), used by the rocket equation (Isp*g0) and TWR.</summary>
    public const double StandardGravity = 9.80665;

    public const double TwoPi    = 2.0 * System.Math.PI;
    public const double DegToRad = System.Math.PI / 180.0;
    public const double RadToDeg = 180.0 / System.Math.PI;

    /// <summary>Standard gravitational parameters mu = G*M (m^3/s^2).</summary>
    public static class Mu
    {
        public const double Sun     = 1.32712440018e20;
        public const double Earth   = 3.986004418e14;
        public const double Moon    = 4.9028000e12;
        public const double Mars    = 4.282837e13;
        public const double Jupiter = 1.26686534e17;
    }

    /// <summary>Mean radii (m).</summary>
    public static class Radius
    {
        public const double Sun     = 6.9634e8;
        public const double Earth   = 6.371e6;
        public const double Moon    = 1.7374e6;
        public const double Mars    = 3.3895e6;
        public const double Jupiter = 6.9911e7;
    }
}
