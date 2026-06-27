namespace Apastron.Audio;

/// <summary>Game events that can be sonified. The simulation triggers these; a backend turns them into sound.</summary>
public enum GameSound
{
    SlugFire,
    MissileLaunch,
    Hit,
    Intercept,
    Victory,
    Defeat,
    UiClick,
}

/// <summary>
/// Minimal audio surface. The game calls <see cref="Play"/> at the right moments regardless of backend,
/// so swapping in a real implementation (see the README's audio note) yields sound with no other changes.
/// </summary>
public interface IAudio
{
    void Play(GameSound sound);
}
