using System.Collections.Concurrent;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Audio;

/// <summary>
/// Speech built from recorded clips (and, where the clips have no words, synthesized phrases), before the
/// radio effect. <see cref="Keys"/> lists clip keys and synthesized text ("\"Jaks\"") in spoken order.
/// </summary>
public sealed record ClipUtterance(short[] Samples, int SampleRate, IReadOnlyList<string> Keys, IReadOnlyList<string> Files);

/// <summary>Speaks <see cref="SpeechToken"/>s by concatenating a <see cref="VoicePack"/>'s clips.</summary>
public sealed class ClipVoice(VoicePack pack)
{
    private readonly ConcurrentDictionary<string, MonoAudio> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public VoicePack Pack { get; } = pack;

    /// <summary>True when the synthesizer speaks this pack's language, so it may fill the pack's gaps.</summary>
    public bool CanUse(IPhraseSynthesizer? synth) =>
        synth is not null && Pack.Language.Length >= 2 && SynthWords.For(Pack.Language) is not null
        && synth.Language.StartsWith(Pack.Language[..2], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The utterance, or null when neither the pack nor the synthesizer can say every required part of the
    /// reply (the caller then uses text-to-speech). A synthesizer in another language than the pack is ignored.
    /// </summary>
    public ClipUtterance? Render(IReadOnlyList<SpeechToken> tokens, int speakerId, int sampleRate, IPhraseSynthesizer? synth = null)
    {
        int speaker = Pack.SpeakerIndex(speakerId);
        var words = CanUse(synth) ? SynthWords.For(Pack.Language) : null;
        if (ClipPhraser.PhraseSegments(tokens, key => Pack.ClipPath(key, speaker) is not null, words) is not { } segments)
            return null;

        var keys = new List<string>();
        var files = new List<string>();
        var audio = new List<MonoAudio>();
        foreach (var segment in segments)
        {
            if (segment.IsClip)
            {
                string file = Pack.ClipPath(segment.Key!, speaker)!;
                keys.Add(segment.Key!);
                files.Add(file);
                audio.Add(_decoded.GetOrAdd(file, ClipAudio.Load));
                continue;
            }

            if (Synthesize(synth!, segment.Text!, sampleRate) is not { } spoken)
            {
                // The synthesizer failed: say what the recordings alone can (the class word comes back in place
                // of the type), or nothing when they can't carry the message, so the caller uses text-to-speech.
                return Render(tokens, speakerId, sampleRate, synth: null);
            }

            keys.Add(segment.ToString());
            files.Add($"({synth!.Name})");
            audio.Add(spoken);
        }

        return audio.Count == 0 ? null : new ClipUtterance(ClipAudio.Join(audio, sampleRate), sampleRate, keys, files);
    }

    /// <summary>
    /// Clean synthesized speech, trimmed, and — when the pack's clips are already radio-processed —
    /// resampled to the join rate and coloured to match them. The colouring was tuned at that rate;
    /// doing it at Piper's own rate would change how loud the hiss is.
    /// </summary>
    private MonoAudio? Synthesize(IPhraseSynthesizer synth, string text, int sampleRate)
    {
        MonoAudio? clean;
        try
        {
            clean = synth.Synthesize(text);
        }
        catch (Exception)
        {
            return null; // implementations should not throw; a failure is a gap, not a crash of the voice thread
        }

        if (clean is null || ClipAudio.TrimSilence(clean) is not { Samples.Length: > 0 } trimmed)
            return null;
        if (!Pack.PreProcessed)
            return trimmed;

        var atRate = ClipAudio.Resample(trimmed, sampleRate);
        var pcm = new short[atRate.Samples.Length];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Round(Math.Clamp(atRate.Samples[i], -1f, 1f) * 32767);
        return ClipAudio.FromPcm(RadioEffect.Apply(pcm, sampleRate, RadioEffectSettings.LikeGameRecordings, seed: text.Length), sampleRate);
    }
}
