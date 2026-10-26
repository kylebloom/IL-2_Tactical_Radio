using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Radio;

namespace Il2Radio.Windows;

/// <summary>
/// One transmission: who speaks (selects the voice), how far away they are (selects the hiss), and
/// the speaker's IL-2 country (selects recorded clips of that nation). <see cref="Tokens"/> lets a clip
/// voice say it; without them, or when the clips can't say everything, <see cref="Text"/> is spoken by TTS.
/// </summary>
internal sealed record Transmission(string Text, int SpeakerId, double RangeFraction, IReadOnlyList<SpeechToken>? Tokens = null, int Country = -1);

/// <summary>
/// Speaks radio replies with recorded clips (<see cref="VoiceSelection"/>) or Windows text-to-speech,
/// runs them through <see cref="RadioEffect"/>, and plays them one at a time on a background thread.
/// A new request drops transmissions not yet started, so pressing a key twice never stacks up chatter.
/// </summary>
internal sealed class RadioVoice : IDisposable
{
    private const int SampleRate = 16_000;
    private const int GapBetweenCallsMs = 400;

    // Per-speaker character: pitch and rate offsets so wingmen are distinguishable with one installed voice.
    private static readonly (string Pitch, int Rate)[] Characters =
    [
        ("-8%", 0), ("+6%", 1), ("-15%", -1), ("+12%", 0), ("0%", 2), ("-4%", -1), ("+3%", 1), ("-11%", 0),
    ];

    private readonly ConcurrentQueue<(int Generation, Transmission Item)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private RadioEffectSettings _effect; // replaced when the volume changes, read afresh for each transmission
    private readonly VoiceSelection _voices;
    private readonly ManualResetEventSlim _ready = new();
    private int _generation;

    public RadioVoice(double volume, VoiceSelection? voices = null)
    {
        _effect = new RadioEffectSettings { Volume = Math.Clamp(volume, 0, 1) };
        _voices = voices ?? VoiceSelection.TtsOnly;
        _worker = new Thread(Run) { IsBackground = true, Name = "radio-voice" };
        _worker.Start();
    }

    /// <summary>Which voice speaks each nation's transmissions.</summary>
    public VoiceSelection Voices => _voices;

    /// <summary>
    /// Radio volume, 0 to 1. The next transmission and the next key click use it; a click already
    /// built at the old volume is rebuilt.
    /// </summary>
    public double Volume
    {
        get => _effect.Volume;
        set => _effect = _effect with { Volume = Math.Clamp(value, 0, 1) };
    }

    /// <summary>The Windows TTS voice used when no clip voice can speak a transmission (null: none installed).</summary>
    public string? VoiceName { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>What spoke each transmission ("voice: clip keys" or "TTS voice: text"), for diagnostics.</summary>
    public ConcurrentQueue<string> History { get; } = new();

    /// <summary>Raised on the voice thread just before a transmission starts playing, with its duration.</summary>
    public event Action<Transmission, TimeSpan>? Speaking;

    /// <summary>
    /// When set, each transmission waits while this returns true (IL-2's own radio is talking), for
    /// at most <see cref="MaxHold"/>, so we don't talk over it. The key click never waits.
    /// </summary>
    public Func<bool>? ChannelBusy { get; set; }

    /// <summary>Longest a reply is held for IL-2's chatter before it goes out anyway.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(8);

    /// <summary>How many transmissions were held for IL-2's radio, and for how long in total (for the evidence file).</summary>
    public (int Count, TimeSpan Total) Held => (Volatile.Read(ref _heldCount), TimeSpan.FromTicks(Interlocked.Read(ref _heldTicks)));

    private int _heldCount;
    private long _heldTicks;

    /// <summary>True when nothing is playing or waiting to be played.</summary>
    public bool Idle => _queue.IsEmpty && Volatile.Read(ref _playing) == 0;

    private int _playing;

    /// <summary>Blocks until the worker has looked for a TTS voice (so <see cref="VoiceName"/> is meaningful).</summary>
    public void WaitUntilReady(TimeSpan timeout) => _ready.Wait(timeout);

    /// <summary>
    /// Press-to-talk cue: a mic key click and a short carrier burst, played at once at the radio
    /// volume. Like keying a real transmitter it cuts off whatever is playing and drops replies not
    /// yet started; the answers to the new call follow through <see cref="Say"/>.
    /// </summary>
    public void PlayKeyClick()
    {
        Interlocked.Increment(ref _generation);
        var effect = _effect;
        if (_keyClick is null || _keyClickVolume != effect.Volume)
        {
            _keyClick = RadioEffect.ToWav(KeyClick(SampleRate, effect), SampleRate);
            _keyClickVolume = effect.Volume;
        }
        PlaySound(_keyClick, IntPtr.Zero, SND_MEMORY | SND_NODEFAULT | SND_ASYNC);
    }

    private byte[]? _keyClick;
    private double _keyClickVolume = double.NaN;

    /// <summary>~3 ms decaying mechanical click, then ~200 ms of carrier hiss ending in the squelch click.</summary>
    internal static short[] KeyClick(int sampleRate, RadioEffectSettings effect)
    {
        var carrier = RadioEffect.Apply(new short[sampleRate * 40 / 1000], sampleRate, effect with { SquelchOpenMs = 70, SquelchCloseMs = 90 }, seed: 7);
        int click = sampleRate * 3 / 1000;
        var samples = new short[click + carrier.Length];
        for (int i = 0; i < click; i++)
            samples[i] = (short)(Math.Exp(-4.0 * i / click) * ((i & 1) == 0 ? 0.6 : -0.6) * effect.Volume * 32767);
        carrier.CopyTo(samples, click);
        return samples;
    }

    /// <summary>Replaces anything still waiting to be spoken with these transmissions.</summary>
    public void Say(IEnumerable<Transmission> transmissions)
    {
        int generation = Interlocked.Increment(ref _generation);
        foreach (var t in transmissions)
        {
            _queue.Enqueue((generation, t));
            _signal.Release();
        }
    }

    private void Run()
    {
        using var synth = CreateSynthesizer();
        VoiceName = synth?.Voice.Name;
        _ready.Set();

        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_queue.TryDequeue(out var entry) || entry.Generation != Volatile.Read(ref _generation))
                continue; // superseded by a newer request

            Volatile.Write(ref _playing, 1);
            try
            {
                var effect = _effect;
                var (wav, description) = Render(synth, entry.Item, effect, _voices);
                if (entry.Generation != Volatile.Read(ref _generation))
                    continue; // superseded (e.g. by a key click) while rendering
                WaitForClearChannel(entry.Generation);
                if (entry.Generation != Volatile.Read(ref _generation))
                    continue; // superseded while waiting
                History.Enqueue(description);
                Speaking?.Invoke(entry.Item, TimeSpan.FromSeconds((wav.Length - 44) / 2.0 / SampleRate));
                PlaySync(wav);
                Thread.Sleep(GapBetweenCallsMs);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                Volatile.Write(ref _playing, 0);
            }
        }
    }

    /// <summary>Holds while IL-2 is transmitting, up to <see cref="MaxHold"/>, unless a new request supersedes this one.</summary>
    private void WaitForClearChannel(int generation)
    {
        if (ChannelBusy is not { } busy || !busy())
            return;
        var start = DateTime.UtcNow;
        while (busy() && DateTime.UtcNow - start < MaxHold && generation == Volatile.Read(ref _generation) && !_stop.IsCancellationRequested)
            Thread.Sleep(50);
        Interlocked.Increment(ref _heldCount);
        Interlocked.Add(ref _heldTicks, (DateTime.UtcNow - start).Ticks);
    }

    /// <summary>The installed TTS voice to fall back on (male, English preferred), or null if there is none.</summary>
    private static SpeechSynthesizer? CreateSynthesizer()
    {
        var synth = new SpeechSynthesizer();
        var voice = synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .OrderByDescending(v => v.VoiceInfo.Gender == VoiceGender.Male)
            .ThenByDescending(v => v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
            .FirstOrDefault();
        if (voice is null)
        {
            synth.Dispose();
            return null;
        }

        synth.SelectVoice(voice.VoiceInfo.Name);
        return synth;
    }

    /// <summary>
    /// Speaks one transmission and applies the radio effect; returns a WAV file image and what spoke it.
    /// Recorded clips are tried first. Clips flagged as pre-processed already carry their own band-limiting,
    /// compression and hiss, so they get the squelch, a gentle drive and only the range-dependent hiss.
    /// </summary>
    private static (byte[] Wav, string Description) Render(SpeechSynthesizer? synth, Transmission t, RadioEffectSettings effect, VoiceSelection voices)
    {
        short[] speech;
        string description;
        if (t.Tokens is { } tokens && voices.For(t.Country) is { } clips && clips.Render(tokens, t.SpeakerId, SampleRate, voices.Synth) is { } utterance)
        {
            speech = utterance.Samples;
            if (clips.Pack.PreProcessed)
                effect = effect with { Drive = 1.0, NoiseLevel = 0.01 };
            description = $"{clips.Pack.Name} #{clips.Pack.SpeakerIndex(t.SpeakerId) + 1}: {string.Join(" ", utterance.Keys)}";
        }
        else
        {
            if (synth is null)
                throw new InvalidOperationException("no Windows text-to-speech voice installed, and no voice pack could say this");
            var (pitch, rate) = Characters[Math.Abs(t.SpeakerId) % Characters.Length];
            speech = Synthesize(synth, t.Text, pitch, rate);
            description = $"TTS {synth.Voice.Name}: {t.Text}";
        }

        short[] radio = RadioEffect.Apply(speech, SampleRate, effect.ForRange(t.RangeFraction), seed: t.SpeakerId);
        return (RadioEffect.ToWav(radio, SampleRate), description);
    }

    /// <summary>Renders transmissions to a WAV file instead of playing them (for checking the sound offline).</summary>
    public static IReadOnlyList<string> RenderToFile(IReadOnlyList<Transmission> transmissions, string path, double volume, VoiceSelection? voices = null)
    {
        using var synth = CreateSynthesizer();
        var effect = new RadioEffectSettings { Volume = Math.Clamp(volume, 0, 1) };
        var pcm = new List<byte>();
        var descriptions = new List<string>();
        foreach (var t in transmissions)
        {
            var (wav, description) = Render(synth, t, effect, voices ?? VoiceSelection.TtsOnly);
            descriptions.Add(description);
            pcm.AddRange(wav.Skip(44)); // strip each header, concatenate samples
        }

        var samples = new short[pcm.Count / 2];
        Buffer.BlockCopy(pcm.ToArray(), 0, samples, 0, samples.Length * 2);
        File.WriteAllBytes(path, RadioEffect.ToWav(samples, SampleRate));
        return descriptions;
    }

    /// <summary>Plays a WAV image now, on the calling thread.</summary>
    public static void PlayNow(byte[] wav) => PlaySync(wav);

    private static short[] Synthesize(SpeechSynthesizer synth, string text, string pitch, int rate)
    {
        using var pcm = new MemoryStream();
        synth.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Rate = rate;
        var prompt = new PromptBuilder();
        prompt.AppendSsmlMarkup($"<prosody pitch=\"{pitch}\">{SecurityElement.Escape(text)}</prosody>");
        synth.Speak(prompt);
        synth.SetOutputToNull();

        var bytes = pcm.ToArray();
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    private static void PlaySync(byte[] wav)
    {
        if (!PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_NODEFAULT | SND_SYNC))
            throw new InvalidOperationException("PlaySound failed (no audio output device?)");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.Join(TimeSpan.FromSeconds(2));
        PlaySound(null, IntPtr.Zero, 0); // stop anything still playing
        _signal.Dispose();
        _stop.Dispose();
        _ready.Dispose();
    }

    private const uint SND_SYNC = 0x0000, SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? sound, IntPtr module, uint flags);
}
