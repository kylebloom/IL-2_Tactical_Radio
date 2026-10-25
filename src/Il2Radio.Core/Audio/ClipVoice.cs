using System.Collections.Concurrent;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Audio;

/// <summary>Speech built from recorded clips, before the radio effect.</summary>
public sealed record ClipUtterance(short[] Samples, int SampleRate, IReadOnlyList<string> Keys, IReadOnlyList<string> Files);

/// <summary>Speaks <see cref="SpeechToken"/>s by concatenating a <see cref="VoicePack"/>'s clips.</summary>
public sealed class ClipVoice(VoicePack pack)
{
    private readonly ConcurrentDictionary<string, MonoAudio> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public VoicePack Pack { get; } = pack;

    /// <summary>The utterance, or null when the pack can't say every required part of the reply.</summary>
    public ClipUtterance? Render(IReadOnlyList<SpeechToken> tokens, int speakerId, int sampleRate)
    {
        int speaker = Pack.SpeakerIndex(speakerId);
        if (ClipPhraser.Phrase(tokens, key => Pack.ClipPath(key, speaker) is not null) is not { } keys)
            return null;

        var files = keys.Select(k => Pack.ClipPath(k, speaker)!).ToList();
        var clips = files.Select(f => _decoded.GetOrAdd(f, ClipAudio.Load)).ToList();
        return new ClipUtterance(ClipAudio.Join(clips, sampleRate), sampleRate, keys, files);
    }
}
