using System.Buffers.Binary;
using Il2Radio.Core.Audio;

namespace Il2Radio.Core.Tests;

public class RadioEffectTests
{
    private const int Rate = 16_000;

    private static short[] Sine(double hz, double seconds, double amplitude = 0.5) =>
        Enumerable.Range(0, (int)(Rate * seconds))
            .Select(i => (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * hz * i / Rate)))
            .ToArray();

    private static double Rms(ReadOnlySpan<short> s)
    {
        double sum = 0;
        foreach (short x in s)
            sum += (double)x * x;
        return Math.Sqrt(sum / s.Length) / 32768;
    }

    /// <summary>RMS of the voice section only (skipping squelch and filter settling), with no hiss and no drive.</summary>
    private static double PassedLevel(double hz)
    {
        var settings = new RadioEffectSettings { NoiseLevel = 0, Drive = 0.01, Volume = 1 };
        var output = RadioEffect.Apply(Sine(hz, 1.0, 0.1), Rate, settings);
        int open = (int)(Rate * settings.SquelchOpenMs / 1000);
        return Rms(output.AsSpan(open + Rate / 10, Rate / 2));
    }

    [Fact]
    public void Passes_the_voice_band_and_cuts_low_rumble_and_high_frequencies()
    {
        double voice = PassedLevel(1000);
        Assert.True(PassedLevel(60) < voice * 0.05, "60 Hz should be at least 26 dB down");
        Assert.True(PassedLevel(6000) < voice * 0.1, "6 kHz should be at least 20 dB down");
        Assert.InRange(voice, 0.05, 0.08); // 0.1-amplitude sine has RMS 0.0707
    }

    [Fact]
    public void Adds_squelch_burst_and_tail_around_the_voice()
    {
        var settings = new RadioEffectSettings();
        var output = RadioEffect.Apply(new short[Rate], Rate, settings); // one second of silence

        int open = (int)(Rate * settings.SquelchOpenMs / 1000), close = (int)(Rate * settings.SquelchCloseMs / 1000);
        Assert.Equal(open + Rate + close, output.Length);
        double burst = Rms(output.AsSpan(Rate / 100, open - Rate / 100));
        double hiss = Rms(output.AsSpan(open + Rate / 4, Rate / 2));
        Assert.True(burst > hiss * 2, $"squelch burst {burst:F4} should be louder than hiss {hiss:F4}");
        Assert.True(hiss > 0, "hiss is present under the voice");
    }

    [Fact]
    public void Distant_transmissions_are_noisier()
    {
        var settings = new RadioEffectSettings();
        double near = Rms(RadioEffect.Apply(new short[Rate], Rate, settings.ForRange(0.1)));
        double far = Rms(RadioEffect.Apply(new short[Rate], Rate, settings.ForRange(0.9)));
        Assert.True(far > near * 2);
    }

    [Fact]
    public void Never_clips_even_with_loud_input()
    {
        var output = RadioEffect.Apply(Sine(800, 0.5, 1.0), Rate, new RadioEffectSettings { Volume = 1, NoiseLevel = 0.2 });
        Assert.DoesNotContain(output, s => s == short.MinValue);
    }

    [Fact]
    public void Zero_volume_is_silent_and_output_is_deterministic_per_seed()
    {
        Assert.All(RadioEffect.Apply(Sine(1000, 0.2), Rate, new RadioEffectSettings { Volume = 0 }), s => Assert.Equal(0, s));
        var a = RadioEffect.Apply(Sine(1000, 0.2), Rate, new RadioEffectSettings(), seed: 3);
        var b = RadioEffect.Apply(Sine(1000, 0.2), Rate, new RadioEffectSettings(), seed: 3);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Wav_header_describes_16_bit_mono_pcm()
    {
        byte[] wav = RadioEffect.ToWav(new short[] { 1, -1, 300 }, Rate);

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))); // channels
        Assert.Equal(Rate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + 6, wav.Length);
        Assert.Equal(300, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(48)));
    }
}
