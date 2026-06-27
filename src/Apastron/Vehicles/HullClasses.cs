using System.Linq;

namespace Apastron.Vehicles;

/// <summary>Warship classes along a modern-naval ladder, scaled to the hard-sci hull/part model. Light hulls
/// run a Compact Fusion Drive on a thin (Whipple) belt and fight as torpedo/light-gun skirmishers; the line
/// (destroyer/cruiser) carries fusion/Epstein torches, belt armour and heavy kinetics; capitals add spinal
/// main batteries and the heaviest armour at the cost of acceleration.</summary>
public enum HullClass { Drone, Corvette, Frigate, Destroyer, Cruiser, Battlecruiser, Battleship, LanceDrone, LanceFrigate, Raider }

/// <summary>
/// Builds each <see cref="HullClass"/> from the parts catalogue and supplies its display name and role.
/// Layouts are tuned so radiators cover the drive's waste heat (sustainable throttle ~1) while hull mass and
/// fuel load set the class's acceleration: drones accelerate hardest, battleships slowest.
/// </summary>
public static class WarshipClasses
{
    public static readonly HullClass[] All =
    {
        HullClass.Drone, HullClass.Corvette, HullClass.Frigate, HullClass.Destroyer,
        HullClass.Cruiser, HullClass.Battlecruiser, HullClass.Battleship,
        HullClass.LanceDrone, HullClass.LanceFrigate,
    };

    public static string DisplayName(HullClass c) => c switch
    {
        HullClass.Drone         => "Attack Drone",
        HullClass.Corvette      => "Corvette",
        HullClass.Frigate       => "Frigate",
        HullClass.Destroyer     => "Destroyer",
        HullClass.Cruiser       => "Light Cruiser",
        HullClass.Battlecruiser => "Battlecruiser",
        HullClass.Battleship    => "Battleship",
        HullClass.LanceDrone    => "Lance Drone",
        HullClass.LanceFrigate  => "Lance Frigate",
        HullClass.Raider        => "Pirate Raider",
        _                       => "Vessel",
    };

    public static string Role(HullClass c) => c switch
    {
        HullClass.Drone         => "Expendable torpedo skirmisher - one torpedo bank + a 40mm PDC, fast and thin",
        HullClass.Corvette      => "Fast attack craft - a twin torpedo battery + PDC, light armour, high accel",
        HullClass.Frigate       => "Escort / missile-defense - torpedoes behind heavy PD + a KKV interceptor",
        HullClass.Destroyer     => "Line combatant - a spinal railgun over a torpedo battery, belt armour, pulse PD",
        HullClass.Cruiser       => "Light cruiser - spinal railgun, deep torpedo magazines, double belt, KKV + lasers",
        HullClass.Battlecruiser => "Capital striker - twin turreted heavy railguns, fast, torpedo waves, KKV screen",
        HullClass.Battleship    => "Line-of-battle - twin heavy railgun turrets, heaviest armour, torpedoes + KKVs",
        HullClass.LanceDrone    => "DEW drone - a solar lance that melts armour at medium range; fast, fragile",
        HullClass.LanceFrigate  => "Lance support frigate - a spinal solar lance over torpedoes and layered PD",
        HullClass.Raider        => "Informal raider - a light coilgun and a PDC on a skeletal hull, barely any armour",
        _                       => "",
    };

    public static Spacecraft Build(HullClass c, string name)
    {
        var s = new Spacecraft { Name = name, Class = c };
        Part T(string n) => PartCatalog.Templates.First(t => t.Name == n).Clone();
        void Add(string n, int count = 1) { for (int i = 0; i < count; i++) s.Add(T(n)); }

        switch (c)
        {
            case HullClass.Drone:
                Add("Command Pod"); Add("Fission Reactor"); Add("Compact Fusion Drive");
                Add("Fuel Tank (large)"); Add("High-Temp Radiator"); Add("Whipple Shield");
                Add("Fusion Torpedo Launcher"); Add("40mm PDC");
                break;

            case HullClass.Corvette:
                Add("Command Pod"); Add("Fission Reactor"); Add("Compact Fusion Drive");
                Add("Fuel Tank (large)"); Add("High-Temp Radiator"); Add("Whipple Shield");
                Add("Fusion Torpedo Launcher", 2); Add("40mm PDC");
                break;

            case HullClass.Frigate:
                Add("Command Pod"); Add("Tokamak Reactor"); Add("Fusion Torch");
                Add("Reaction Tank (compact)"); Add("Droplet Radiator", 2); Add("Whipple Shield");
                Add("Fusion Torpedo Launcher", 2); Add("Pulse Laser Cluster"); Add("40mm PDC", 2);
                Add("KKV Interceptor");
                break;

            case HullClass.Destroyer:
                Add("Command Pod"); Add("Tokamak Reactor"); Add("Fusion Torch");
                Add("Reaction Tank (compact)"); Add("Droplet Radiator", 2); Add("Steel Belt");
                Add("Spinal Railgun"); Add("Fusion Torpedo Launcher", 2); Add("Pulse Laser Cluster"); Add("40mm PDC");
                break;

            case HullClass.Cruiser:
                Add("Command Pod"); Add("Tokamak Reactor"); Add("Epstein-Type Torch");
                Add("Reaction Mass Tank"); Add("Droplet Radiator", 3); Add("Steel Belt", 2);
                Add("Spinal Railgun"); Add("Fusion Torpedo Launcher", 3); Add("Pulse Laser Cluster"); Add("40mm PDC", 2);
                Add("KKV Interceptor");
                break;

            case HullClass.Battlecruiser:
                Add("Command Pod"); Add("Tokamak Reactor"); Add("Epstein-Type Torch");
                Add("Reaction Tank (compact)", 2); Add("Droplet Radiator", 3); Add("Steel Belt");
                Add("Heavy Railgun Turret", 2); Add("Fusion Torpedo Launcher", 3);
                Add("Pulse Laser Cluster", 2); Add("40mm PDC", 2); Add("KKV Interceptor");
                break;

            case HullClass.Battleship:
                Add("Command Pod"); Add("Habitat Module"); Add("Tokamak Reactor"); Add("Fusion Torch", 2);
                Add("Reaction Mass Tank", 2); Add("Droplet Radiator", 4); Add("Tungsten Plate"); Add("Steel Belt");
                Add("Heavy Railgun Turret", 2); Add("Fusion Torpedo Launcher", 4);
                Add("Pulse Laser Cluster", 2); Add("40mm PDC", 3); Add("KKV Interceptor", 2);
                break;

            case HullClass.LanceDrone:
                Add("Command Pod"); Add("Fission Reactor"); Add("Compact Fusion Drive");
                Add("Fuel Tank (large)"); Add("High-Temp Radiator"); Add("Whipple Shield");
                Add("Solar Lance"); Add("40mm PDC");
                break;

            case HullClass.LanceFrigate:
                Add("Command Pod"); Add("Tokamak Reactor"); Add("Fusion Torch");
                Add("Reaction Tank (compact)"); Add("Droplet Radiator", 2); Add("Whipple Shield");
                Add("Solar Lance"); Add("Fusion Torpedo Launcher", 2); Add("Pulse Laser Cluster"); Add("40mm PDC");
                break;

            case HullClass.Raider:
                // informal pirate hull: skeletal, cheap, weakly armed. A light coilgun and a single PDC on a
                // thin Whipple belt - dangerous only in numbers.
                Add("Command Pod"); Add("Fission Reactor"); Add("Compact Fusion Drive");
                Add("Fuel Tank (small)"); Add("High-Temp Radiator"); Add("Whipple Shield");
                Add("Light Coilgun"); Add("40mm PDC");
                break;
        }
        return s;
    }
}
