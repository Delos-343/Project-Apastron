using System;
using System.Collections.Generic;
using Apastron.Audio;

namespace Apastron.Combat;

public enum MissionObjective { Destroy, Survive }
public enum MissionOutcome { InProgress, Victory, Defeat }

/// <summary>A playable engagement: the enemy loadout/posture, the win condition, and the difficulty knobs.</summary>
public sealed class MissionDef
{
    public required string Name;
    public required string Difficulty;
    public required string Description;
    public MissionObjective Objective = MissionObjective.Destroy;

    public double SurviveSeconds = 90.0;          // Survive: time you must last
    public CombatDoctrine EnemyDoctrine = CombatDoctrine.Standoff;
    public double EnemyArmorMult = 1.0;           // scales the enemy's armour pool
    public double PlayerArmorMult = 1.0;
    public double EnemyPassiveSeconds = 0.0;      // enemy holds fire / coasts this long at the start
    public double StartRange = 150.0e3;           // initial separation (m)
}

/// <summary>The four built-in missions, easy to hard plus a survival scenario.</summary>
public static class MissionCatalog
{
    public static readonly List<MissionDef> All = new()
    {
        new MissionDef
        {
            Name = "Ambush", Difficulty = "Easy", Objective = MissionObjective.Destroy,
            Description = "An enemy cruiser drifts unaware. You have the first move \u2014 open fire before it brings its " +
                          "weapons online and finish it while it is still cold.",
            EnemyDoctrine = CombatDoctrine.Standoff, EnemyArmorMult = 0.7,
            EnemyPassiveSeconds = 18.0, StartRange = 150.0e3,
        },
        new MissionDef
        {
            Name = "Patrol Skirmish", Difficulty = "Normal", Objective = MissionObjective.Destroy,
            Description = "A fair fight against a matched cruiser holding at range. Win the laser exchange and manage " +
                          "the incoming missiles with your point-defense.",
            EnemyDoctrine = CombatDoctrine.Standoff, EnemyArmorMult = 0.85,
            EnemyPassiveSeconds = 0.0, StartRange = 150.0e3,
        },
        new MissionDef
        {
            Name = "Outgunned", Difficulty = "Hard", Objective = MissionObjective.Destroy,
            Description = "A heavily-armoured aggressor is closing fast. Use evasion to spoil its kinetics and your " +
                          "missiles and beams to wear down a tougher hull before it overwhelms you.",
            EnemyDoctrine = CombatDoctrine.Aggressive, EnemyArmorMult = 1.6,
            EnemyPassiveSeconds = 0.0, StartRange = 120.0e3,
        },
        new MissionDef
        {
            Name = "Last Stand", Difficulty = "Survive", Objective = MissionObjective.Survive,
            Description = "You cannot kill this one outright \u2014 just stay alive. Weave, screen the missiles, and " +
                          "ride out ninety seconds against a relentless attacker.",
            EnemyDoctrine = CombatDoctrine.Aggressive, EnemyArmorMult = 2.0,
            SurviveSeconds = 90.0, EnemyPassiveSeconds = 0.0, StartRange = 100.0e3,
        },
    };
}

/// <summary>Tracks a mission in flight: enemy release timer, win/lose detection, and end-of-mission scoring.</summary>
public sealed class MissionState
{
    public required MissionDef Def;
    public required Combatant Player;
    public required Combatant Enemy;

    public MissionOutcome Outcome = MissionOutcome.InProgress;
    public double Elapsed;
    public bool EnemyReleased;
    public int Score;
    public string Rating = "";
    public int Best;
    public bool IsNewBest;

    // stats captured at the moment of resolution
    public double PlayerHullPct, EnemyHullPct;

    public double TimeLeft => Math.Max(0.0, Def.SurviveSeconds - Elapsed);

    public void Update(CombatManager cm, double dt)
    {
        if (Outcome != MissionOutcome.InProgress) return;
        if (dt > 0.0) Elapsed += dt;

        // release the enemy from its passive posture once the grace period ends
        if (!EnemyReleased && Elapsed >= Def.EnemyPassiveSeconds)
        {
            Enemy.Doctrine = Def.EnemyDoctrine;
            Enemy.WeaponsHold = false;
            EnemyReleased = true;
        }

        bool playerDead = !Player.Alive;
        bool enemyDead = !Enemy.Alive;

        if (Def.Objective == MissionObjective.Destroy)
        {
            if (playerDead && enemyDead) Resolve(cm, MissionOutcome.Defeat);      // mutual = you did not survive
            else if (enemyDead) Resolve(cm, MissionOutcome.Victory);
            else if (playerDead) Resolve(cm, MissionOutcome.Defeat);
        }
        else // Survive
        {
            if (playerDead) Resolve(cm, MissionOutcome.Defeat);
            else if (Elapsed >= Def.SurviveSeconds) Resolve(cm, MissionOutcome.Victory);
        }
    }

    private void Resolve(CombatManager cm, MissionOutcome outcome)
    {
        Outcome = outcome;
        cm.Active = false;
        cm.Sound?.Invoke(outcome == MissionOutcome.Victory ? GameSound.Victory : GameSound.Defeat);
        PlayerHullPct = Player.HullMax > 0 ? 100.0 * Math.Max(0.0, Player.Hull) / Player.HullMax : 0.0;
        EnemyHullPct = Enemy.HullMax > 0 ? 100.0 * Math.Max(0.0, Enemy.Hull) / Enemy.HullMax : 0.0;

        double hullFrac = PlayerHullPct / 100.0;
        if (outcome == MissionOutcome.Victory)
        {
            if (Def.Objective == MissionObjective.Survive)
                Score = (int)Math.Round(1500.0 + 1500.0 * hullFrac + 10.0 * cm.Intercepts);
            else
            {
                double timeBonus = Math.Max(0.0, 120.0 - Elapsed);
                Score = (int)Math.Round(1000.0 + 2000.0 * hullFrac + 8.0 * timeBonus + 25.0 * cm.Intercepts);
            }
        }
        else
        {
            // partial credit for damage dealt / time survived
            if (Def.Objective == MissionObjective.Survive)
                Score = (int)Math.Round(1000.0 * Math.Min(1.0, Elapsed / Math.Max(Def.SurviveSeconds, 1.0)));
            else
                Score = (int)Math.Round(300.0 * (1.0 - EnemyHullPct / 100.0));
        }
        Rating = ScoreBoard.RatingFor(Score);
        if (outcome == MissionOutcome.Victory)
        {
            Best = ScoreBoard.Submit(Def.Name, Score, out bool nb);
            IsNewBest = nb;
        }
        else
        {
            Best = ScoreBoard.GetBest(Def.Name);
            IsNewBest = false;
        }
    }
}
