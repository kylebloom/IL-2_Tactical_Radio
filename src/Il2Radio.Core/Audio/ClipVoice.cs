using System.Collections.Concurrent;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Audio;

/// <summary>Speech built from recorded clips, before the radio effect. <see cref="Keys"/> lists the clip keys in spoken order.</summary>
public sealed record ClipUtterance(short[] Samples, int SampleRate, IReadOnlyList<string> Keys, IReadOnlyList<string> Files);

/// <summary>
/// Speaks <see cref="SpeechToken"/>s by concatenating a <see cref="VoicePack"/>'s clips. One speaker's
/// recordings only: no synthesized word is ever mixed into a transmission.
/// </summary>
public sealed class ClipVoice(VoicePack pack)
{
    private readonly ConcurrentDictionary<string, MonoAudio> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public VoicePack Pack { get; } = pack;

    /// <summary>The utterance, or null when the pack can say none of the reply (the chat then carries it alone).</summary>
    public ClipUtterance? Render(IReadOnlyList<SpeechToken> tokens, int speakerId, int sampleRate)
    {
        int speaker = Pack.SpeakerIndex(speakerId);
        if (ClipPhraser.Phrase(tokens, key => Pack.ClipPath(key, speaker) is not null) is not { } keys)
            return null;

        var files = keys.Select(k => Pack.ClipPath(k, speaker)!).ToList();
        var audio = files.Select(f => _decoded.GetOrAdd(f, ClipAudio.Load)).ToList();
        return new ClipUtterance(ClipAudio.Join(audio, sampleRate), sampleRate, keys, files);
    }
}
