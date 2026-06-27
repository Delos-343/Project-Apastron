using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apastron.Core;
using Apastron.Physics;
using Apastron.Vehicles;

namespace Apastron.Simulation;

// --- serializable snapshot of the simulation state ---

public sealed class BodyDto
{
    public string Name { get; set; } = "";
    public double Mu { get; set; }
    public double Radius { get; set; }
    public double[] Position { get; set; } = new double[3];
    public float[] Color { get; set; } = new float[3];
}

public sealed class VesselDto
{
    public string Name { get; set; } = "";
    public double Mass { get; set; }
    public double[] Position { get; set; } = new double[3];
    public double[] Velocity { get; set; } = new double[3];
    public double SpinRadius { get; set; }
    public double SpinRpm { get; set; }
    public bool IsStation { get; set; }
}

public sealed class PartDto
{
    public string Name { get; set; } = "";
    public PartCategory Category { get; set; }
    public double DryMass { get; set; }
    public double Length { get; set; }
    public double PropellantCapacity { get; set; }
    public double Propellant { get; set; }
    public double ThrustVac { get; set; }
    public double IspVac { get; set; }
    public double HeatOutput { get; set; }
    public double HeatRejection { get; set; }
    public double Health { get; set; } = 1.0;   // 1.0 default => pre-Chunk-D saves load intact
    public WeaponSpec? Weapon { get; set; }
    public ArmorSpec? Armor { get; set; }
}

public sealed class ShipDto
{
    public string Name { get; set; } = "Custom Vessel";
    public List<PartDto> Parts { get; set; } = new();
}

public sealed class ViewDto
{
    public int Focus { get; set; }
    public float Fov { get; set; } = 45.0f;
    public bool ShowOrbitPath { get; set; } = true;
    public bool ShowVesselMarker { get; set; } = true;
}

public sealed class ScenarioDto
{
    public double SimTime { get; set; }
    public string Integrator { get; set; } = "Velocity Verlet";
    public double RenderScale { get; set; } = 1.0e-6;
    public double CameraDistance { get; set; } = 28.0;
    public List<BodyDto> Bodies { get; set; } = new();
    public List<VesselDto> Vessels { get; set; } = new();
    public ShipDto Ship { get; set; } = new();
    public ViewDto View { get; set; } = new();
}

/// <summary>Result of a successful load: the rebuilt world/ship plus the saved view + integrator name.</summary>
public sealed class LoadedScenario
{
    public PhysicsWorld World = null!;
    public Spacecraft Ship = null!;
    public string Integrator = "Velocity Verlet";
    public ViewDto View = new();
}

/// <summary>
/// Saves and restores a complete scenario (bodies, vessels incl. the rendezvous target, the
/// spacecraft design, the camera view, the integrator and the clock) as JSON. The live engine
/// types carry GL handles and computed members, so a flat DTO layer is serialized instead.
/// </summary>
public static class ScenarioIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool Save(string path, PhysicsWorld world, Spacecraft ship,
                            int focus, float fov, bool showOrbit, bool showMarker)
    {
        try
        {
            var dto = new ScenarioDto
            {
                SimTime = world.SimTime,
                Integrator = world.Integrator.Name,
                RenderScale = world.RenderScaleHint,
                CameraDistance = world.CameraDistanceHint,
                Ship = ToDto(ship),
                View = new ViewDto { Focus = focus, Fov = fov, ShowOrbitPath = showOrbit, ShowVesselMarker = showMarker },
            };

            foreach (CelestialBody b in world.Bodies)
                dto.Bodies.Add(new BodyDto
                {
                    Name = b.Name, Mu = b.Mu, Radius = b.Radius,
                    Position = Arr(b.Position),
                    Color = new[] { b.Color.R, b.Color.G, b.Color.B },
                });

            foreach (RigidBody v in world.Vessels)
                dto.Vessels.Add(new VesselDto
                {
                    Name = v.Name, Mass = v.Mass, IsStation = v.IsStation,
                    Position = Arr(v.Position), Velocity = Arr(v.Velocity),
                    SpinRadius = v.SpinRadius, SpinRpm = v.SpinRpm,
                });

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scenario] save failed: {ex.Message}");
            return false;
        }
    }

    public static bool TryLoad(string path, out LoadedScenario loaded)
    {
        loaded = null!;
        try
        {
            if (!File.Exists(path)) return false;
            ScenarioDto? dto = JsonSerializer.Deserialize<ScenarioDto>(File.ReadAllText(path), Options);
            if (dto == null || dto.Bodies.Count == 0) return false;

            var world = new PhysicsWorld();
            foreach (BodyDto b in dto.Bodies)
                world.Bodies.Add(new CelestialBody
                {
                    Name = b.Name, Mu = b.Mu, Radius = b.Radius,
                    Position = Vec(b.Position),
                    Color = (Col(b.Color, 0), Col(b.Color, 1), Col(b.Color, 2)),
                });

            foreach (VesselDto v in dto.Vessels)
                world.Vessels.Add(new RigidBody
                {
                    Name = v.Name, Mass = v.Mass,
                    Position = Vec(v.Position), Velocity = Vec(v.Velocity),
                    SpinRadius = v.SpinRadius, SpinRpm = v.SpinRpm,
                    IsStation = v.IsStation,
                });

            world.SetSimTime(dto.SimTime);
            world.RenderScaleHint = dto.RenderScale > 0.0 ? dto.RenderScale : 1.0e-6;
            world.CameraDistanceHint = dto.CameraDistance > 0.0 ? dto.CameraDistance : 28.0;

            Spacecraft ship = FromDto(dto.Ship);
            if (world.PrimaryVessel != null && ship.TotalMass > 0.0)
                world.PrimaryVessel.Mass = ship.TotalMass;

            loaded = new LoadedScenario
            {
                World = world,
                Ship = ship,
                Integrator = dto.Integrator,
                View = dto.View ?? new ViewDto(),
            };
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[scenario] load failed: {ex.Message}");
            return false;
        }
    }

    private static ShipDto ToDto(Spacecraft s)
    {
        var d = new ShipDto { Name = s.Name };
        foreach (Part p in s.Parts)
            d.Parts.Add(new PartDto
            {
                Name = p.Name, Category = p.Category, DryMass = p.DryMass, Length = p.Length,
                PropellantCapacity = p.PropellantCapacity, Propellant = p.Propellant,
                ThrustVac = p.ThrustVac, IspVac = p.IspVac, HeatOutput = p.HeatOutput, HeatRejection = p.HeatRejection,
                Health = p.Health,
                Weapon = p.Weapon?.Clone(), Armor = p.Armor?.Clone(),
            });
        return d;
    }

    private static Spacecraft FromDto(ShipDto d)
    {
        var s = new Spacecraft { Name = d.Name };
        foreach (PartDto p in d.Parts)
            s.Add(new Part
            {
                Name = p.Name, Category = p.Category, DryMass = p.DryMass, Length = p.Length,
                PropellantCapacity = p.PropellantCapacity, Propellant = p.Propellant,
                ThrustVac = p.ThrustVac, IspVac = p.IspVac, HeatOutput = p.HeatOutput, HeatRejection = p.HeatRejection,
                Health = p.Health,
                Weapon = p.Weapon?.Clone(), Armor = p.Armor?.Clone(),
            });
        return s;
    }

    private static double[] Arr(Vec3 v) => new[] { v.X, v.Y, v.Z };
    private static Vec3 Vec(double[] a) => a != null && a.Length >= 3 ? new Vec3(a[0], a[1], a[2]) : Vec3.Zero;
    private static float Col(float[] a, int i) => a != null && a.Length > i ? a[i] : 0.5f;
}
