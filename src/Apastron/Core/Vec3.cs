using System;
using System.Globalization;

namespace Apastron.Core;

/// <summary>
/// Double-precision 3D vector. We deliberately do NOT use System.Numerics.Vector3
/// (32-bit float) for world-space simulation: near 10^7 m a float resolves to ~1 m,
/// and worse further out, which destroys orbital accuracy. ALL simulation state is
/// stored in this type; conversion to float happens only at the rendering boundary
/// (camera-relative / floating origin), added in a later chunk.
/// </summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero  = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a)         => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public Vec3 Normalized()
    {
        double len = Length;
        return len > 1e-300 ? this / len : Zero;
    }

    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

    public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
    public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:G6}, {1:G6}, {2:G6})", X, Y, Z);
}
