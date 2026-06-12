using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Apastron.Combat;
using Apastron.Physics;
using Apastron.Simulation;
using Apastron.Vehicles;

namespace Apastron.App;

/// <summary>
/// Fire-control / terminal-ballistics analysis for the weapons mounted on the active ship.
/// Resolves an engagement against the live rendezvous target (its real range and closing rate)
/// or against manual range / closing inputs, versus a chosen target armor, and reports the
/// terminal result: kinetic penetration vs perforation, laser dwell-to-burn, or missile intercept.
/// </summary>
public static class WeaponsPanel
{
    private static int _sel;
    private static bool _useLive = true;
    private static float _rangeKm = 100.0f;
    private static float _closingKms = 10.0f;
    private static float _arealDensity = 800.0f;
    private static int _matIdx = 1;             // Steel(RHA)
    private static float _absorptivity = 0.5f;
    private static float _evasionG = 3.0f;

    public static void Body(GameContext ctx)
    {
        List<Part> weapons = ctx.Ship.Parts.Where(p => p.Category == PartCategory.Weapon && p.Weapon != null).ToList();
        if (weapons.Count == 0)
        {
            Ui.TextWrapped("No weapon mounted. Add a Railgun, Mass Driver, Missile Rack or Laser Battery in the Spacecraft Builder.");
            return;
        }
        if (_sel >= weapons.Count) _sel = 0;

        Section("Weapon");
        for (int i = 0; i < weapons.Count; i++)
        {
            if (ImGui.RadioButton(weapons[i].Name, _sel == i)) _sel = i;
            if (i < weapons.Count - 1) ImGui.SameLine();
        }
        WeaponSpec w = weapons[_sel].Weapon!;

        // --- target geometry ---
        Section("Target");
        RigidBody? vessel = ctx.World.PrimaryVessel;
        RigidBody? target = ctx.World.TargetVessel;
        CelestialBody? primary = vessel != null ? ctx.World.DominantBody(vessel.Position) : null;
        bool haveLive = vessel != null && target != null && primary != null;

        if (haveLive) ImGui.Checkbox("Use rendezvous target", ref _useLive);
        else Ui.TextDisabled("No rendezvous target - using manual inputs.");

        double range, closing;
        if (_useLive && haveLive)
        {
            RelativeState rel = Rendezvous.Relative(vessel!, target!, primary!);
            range = rel.Range;
            closing = -rel.RangeRate;     // RangeRate is negative when closing
            Ui.Text($"Range    {range / 1000.0:N1} km");
            Ui.Text($"Closing  {closing / 1000.0:N2} km/s");
        }
        else
        {
            ImGui.SetNextItemWidth(220.0f);
            ImGui.SliderFloat("Range", ref _rangeKm, 1.0f, 100000.0f, "%.0f km", ImGuiSliderFlags.Logarithmic);
            ImGui.SetNextItemWidth(220.0f);
            ImGui.SliderFloat("Closing speed", ref _closingKms, -50.0f, 200.0f, "%.1f km/s");
            range = _rangeKm * 1000.0;
            closing = _closingKms * 1000.0;
        }

        // --- target armor ---
        Section("Target armor");
        ImGui.SetNextItemWidth(160.0f);
        ImGui.Combo("Material", ref _matIdx, Materials.Names, Materials.Names.Length);
        if (_matIdx < 0 || _matIdx >= Materials.Names.Length) _matIdx = 1;
        ImGui.SetNextItemWidth(220.0f);
        ImGui.SliderFloat("Areal density", ref _arealDensity, 10.0f, 5000.0f, "%.0f kg/m2", ImGuiSliderFlags.Logarithmic);
        var armor = new ArmorSpec { ArealDensity = _arealDensity, Material = Materials.Names[_matIdx] };

        // --- terminal result ---
        Section("Result");
        switch (w.Kind)
        {
            case WeaponKind.Kinetic: DrawKinetic(w, closing, armor); break;
            case WeaponKind.Laser:   DrawLaser(w, range, armor);     break;
            case WeaponKind.Missile: DrawMissile(w, range, closing); break;
        }
    }

    private static void DrawKinetic(WeaponSpec w, double closing, ArmorSpec armor)
    {
        Ui.TextDisabled($"{w.ProjectileMass:N1} kg / {w.ProjectileLength * 100.0:N0} cm {w.ProjectileMaterial} rod, muzzle {w.MuzzleVelocity / 1000.0:N1} km/s");
        KineticResult k = Ballistics.Kinetic(w, closing, armor);
        Ui.Text($"Impact velocity  {k.ImpactVelocity / 1000.0:N1} km/s");
        Ui.Text($"Kinetic energy   {Energy(k.KineticEnergy)}");
        Ui.Text($"Penetration      {k.Penetration * 100.0:N1} cm");
        Ui.Text($"Armor thickness  {k.ArmorThickness * 100.0:N1} cm  ({armor.Material})");
        ImGui.Spacing();
        if (k.Perforates)
            Ui.TextColored(Green, $"PERFORATES - {k.Residual * 100.0:N1} cm of residual penetration");
        else
            Ui.TextColored(Red, "DEFEATED by the armor");
        if (w.RateOfFire > 0.0)
            Ui.TextDisabled($"rate of fire {w.RateOfFire:N1} /s");
    }

    private static void DrawLaser(WeaponSpec w, double range, ArmorSpec armor)
    {
        Ui.TextDisabled($"{w.BeamPower / 1.0e6:N1} MW, {w.Aperture:N1} m aperture, {w.Wavelength * 1.0e9:N0} nm, M2 {w.BeamQuality:N1}");
        ImGui.SetNextItemWidth(220.0f);
        ImGui.SliderFloat("Absorptivity", ref _absorptivity, 0.05f, 1.0f, "%.2f");
        LaserResult l = Ballistics.Laser(w, range, _absorptivity, armor);
        Ui.Text($"Spot diameter    {SpotSize(l.SpotDiameter)}");
        Ui.Text($"Intensity        {l.Intensity / 1.0e6:N2} MW/m2");
        ImGui.Spacing();
        if (double.IsFinite(l.DwellTime))
        {
            Vector4 col = l.DwellTime < 5.0 ? Green : l.DwellTime < 60.0 ? Amber : Red;
            Ui.TextColored(col, $"Dwell to burn through: {Duration(l.DwellTime)}");
        }
        else Ui.TextColored(Red, "Beam too weak to ablate.");
        Ui.TextDisabled("Spot grows with range, so intensity falls as 1/range^2.");
    }

    private static void DrawMissile(WeaponSpec w, double range, double closing)
    {
        Ui.TextDisabled($"{w.MissileDryMass:N0} kg dry + {w.MissilePropellant:N0} kg prop, Isp {w.MissileIsp:N0} s, {w.MissileThrust / 1000.0:N0} kN");
        ImGui.SetNextItemWidth(220.0f);
        ImGui.SliderFloat("Target evasion", ref _evasionG, 0.0f, 30.0f, "%.1f g");
        double evasion = _evasionG * Ballistics.G0;
        MissileResult m = Ballistics.Missile(w, range, closing, evasion);
        Ui.Text($"Delta-v          {m.DeltaV / 1000.0:N2} km/s");
        Ui.Text($"Accel            {m.AccelInitial:N0} -> {m.AccelBurnout:N0} m/s2  ({m.AccelBurnout / Ballistics.G0:N0} g burnout)");
        Ui.Text($"Burn time        {Duration(m.BurnTime)}");
        ImGui.Spacing();
        if (!m.OutAccelerates)
            Ui.TextColored(Red, $"Out-accelerated by a {_evasionG:N1} g evader - cannot converge.");
        else if (double.IsFinite(m.InterceptTime))
            Ui.TextColored(Green, $"INTERCEPT in {Duration(m.InterceptTime)}");
        else
            Ui.TextColored(Red, "Cannot close the range (target opening too fast).");
    }

    private static readonly Vector4 Green = new(0.5f, 0.85f, 0.55f, 1.0f);
    private static readonly Vector4 Amber = new(0.95f, 0.85f, 0.4f, 1.0f);
    private static readonly Vector4 Red = new(1.0f, 0.45f, 0.35f, 1.0f);

    private static string Energy(double j)
    {
        if (j >= 1.0e9) return $"{j / 1.0e9:N1} GJ";
        if (j >= 1.0e6) return $"{j / 1.0e6:N1} MJ";
        if (j >= 1.0e3) return $"{j / 1.0e3:N1} kJ";
        return $"{j:N0} J";
    }

    private static string SpotSize(double m)
    {
        if (m >= 1.0) return $"{m:N2} m";
        return $"{m * 100.0:N1} cm";
    }

    private static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0) return "--";
        if (seconds < 1.0) return $"{seconds * 1000.0:N0} ms";
        if (seconds < 90.0) return $"{seconds:N1} s";
        long s = (long)seconds;
        long h = s / 3600; s %= 3600;
        long m = s / 60; s %= 60;
        if (h > 0) return $"{h}h {m:00}m";
        return $"{m}m {s:00}s";
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        Ui.TextDisabled(label);
        ImGui.Separator();
    }
}
