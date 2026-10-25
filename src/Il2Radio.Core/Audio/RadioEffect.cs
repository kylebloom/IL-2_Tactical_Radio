namespace Il2Radio.Core.Audio;

public sealed record RadioEffectSettings
{
    /// <summary>Lower edge of the voice band; WWII HF/VHF sets passed roughly 300–3000 Hz.</summary>
    public double LowCutHz { get; init; } = 300;

    public double HighCutHz { get; init; } = 3000;

    /// <summary>Soft-clipping drive; 1 is clean, higher gives the compressed, slightly distorted radio sound.</summary>
    public double Drive { get; init; } = 3.0;

    /// <summary>Background hiss level (0–1 of full scale) under the voice.</summary>
    public double NoiseLevel { get; init; } = 0.03;

    /// <summary>Noise burst before the voice (carrier opening the squelch).</summary>
    public double SquelchOpenMs { get; init; } = 120;

    /// <summary>Noise tail after the voice, ending with the squelch click.</summary>
    public double SquelchCloseMs { get; init; } = 180;

    /// <summary>Output volume 0–1, independent of the game's volume.</summary>
    public double Volume { get; init; } = 0.8;

    /// <summary>Settings for a transmission from <paramref name="rangeFraction"/> of maximum radio range: further is noisier.</summary>
    public RadioEffectSettings ForRange(double rangeFraction) =>
        this with { NoiseLevel = NoiseLevel + 0.12 * Math.Clamp(rangeFraction, 0, 1) };
}

/// <summary>
/// Turns clean speech into a radio transmission: squelch burst, band-limited and overdriven voice,
/// in-band hiss, squelch tail and click. Pure DSP on 16-bit mono PCM, so it can be tested without audio hardware.
/// </summary>
public static class RadioEffect
{
    public static short[] Apply(ReadOnlySpan<short> voice, int sampleRate, RadioEffectSettings settings, int seed = 0)
    {
        int open = (int)(sampleRate * settings.SquelchOpenMs / 1000);
        int close = (int)(sampleRate * settings.SquelchCloseMs / 1000);
        int total = open + voice.Length + close;

        var voiceBand = BandPass(sampleRate, settings);
        var noiseBand = BandPass(sampleRate, settings);
        var random = new Random(seed);
        double clipNorm = Math.Tanh(settings.Drive);
        var output = new short[total];

        for (int i = 0; i < total; i++)
        {
            int v = i - open;
            double x = v >= 0 && v < voice.Length ? voice[v] / 32768.0 : 0;
            double speech = Math.Tanh(settings.Drive * Filter(voiceBand, x)) / clipNorm;

            // The squelch burst and tail are louder than the hiss under the voice, as when a carrier opens/closes.
            bool squelch = v < 0 || v >= voice.Length;
            double noiseLevel = settings.NoiseLevel * (squelch ? 2.5 : 1.0);
            double noise = Filter(noiseBand, random.NextDouble() * 2 - 1) * noiseLevel * 4;

            double mix = speech * 0.75 + noise;
            if (i >= total - sampleRate / 500) // ~2 ms click as the squelch closes
                mix += 0.3 * (((i & 1) == 0) ? 1 : -1);

            // Soft limiter before the volume control: |tanh| < 1, so the output can never clip.
            output[i] = (short)(Math.Tanh(mix) * settings.Volume * 32767);
        }

        return output;
    }

    /// <summary>Wraps 16-bit mono PCM in a RIFF/WAVE container.</summary>
    public static byte[] ToWav(ReadOnlySpan<short> pcm, int sampleRate)
    {
        int dataBytes = pcm.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                 // fmt chunk size
        w.Write((short)1);           // PCM
        w.Write((short)1);           // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);     // byte rate
        w.Write((short)2);           // block align
        w.Write((short)16);          // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (short s in pcm)
            w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Two high-pass and two low-pass biquads: 24 dB/octave skirts on each side of the voice band.</summary>
    private static Biquad[] BandPass(int sampleRate, RadioEffectSettings s) =>
    [
        Biquad.HighPass(sampleRate, s.LowCutHz), Biquad.HighPass(sampleRate, s.LowCutHz),
        Biquad.LowPass(sampleRate, s.HighCutHz), Biquad.LowPass(sampleRate, s.HighCutHz),
    ];

    private static double Filter(Biquad[] chain, double x)
    {
        foreach (var stage in chain)
            x = stage.Process(x);
        return x;
    }

    /// <summary>RBJ audio-EQ-cookbook biquad, Q = 0.707 (Butterworth).</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            (_b0, _b1, _b2, _a1, _a2) = (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        public static Biquad LowPass(int sampleRate, double cutoff)
        {
            var (cos, alpha) = Coefficients(sampleRate, cutoff);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad HighPass(int sampleRate, double cutoff)
        {
            var (cos, alpha) = Coefficients(sampleRate, cutoff);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        private static (double Cos, double Alpha) Coefficients(int sampleRate, double cutoff)
        {
            double w0 = 2 * Math.PI * Math.Min(cutoff, sampleRate * 0.45) / sampleRate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * 0.7071));
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}
