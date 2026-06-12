namespace Apastron.Audio;

/// <summary>
/// The default audio backend: a no-op. Chosen so the project builds and runs with zero audio dependencies
/// and zero risk. To add sound, implement <see cref="IAudio"/> over an audio library (e.g. Silk.NET.OpenAL)
/// and assign it in Program; every fire / hit / intercept / outcome hook is already in place.
/// </summary>
public sealed class SilentAudio : IAudio
{
    public void Play(GameSound sound) { }
}
