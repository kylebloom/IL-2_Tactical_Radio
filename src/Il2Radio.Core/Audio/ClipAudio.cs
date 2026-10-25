using System.Buffers.Binary;
using NVorbis;

namespace Il2Radio.Core.Audio;

/// <summary>Decoded mono audio, samples in −1..1.</summary>
public sealed record MonoAudio(float[] Samples, int SampleRate)
{
    public double Seconds => Samples.Length / (double)SampleRate;
}

/// <summary>Clip decoding (WAV, Ogg Vorbis), resampling and joining, for voices built from recordings.</summary>
public static class ClipAudio
{
    /// <summary>Decodes a .wav (16-bit PCM or 32-bit float) or .ogg (Vorbis) file, mixing channels down to mono.</summary>
    public static MonoAudio Load(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? LoadOgg(path)
            : ParseWav(File.ReadAllBytes(path));

    private static MonoAudio LoadOgg(string path)
    {
        using var reader = new VorbisReader(path);
        int channels = reader.Channels;
        var interleaved = new List<float>((int)Math.Max(0, reader.TotalSamples) * channels);
        var buffer = new float[4096 * channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            interleaved.AddRange(buffer.AsSpan(0, read));
        return new MonoAudio(Downmix(interleaved.ToArray(), channels), reader.SampleRate);
    }

    /// <summary>Parses a RIFF/WAVE image: PCM 8/16/24/32-bit or IEEE float 32-bit, any channel count.</summary>
    public static MonoAudio ParseWav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("not a RIFF/WAVE file");

        int format = 0, channels = 0, rate = 0, bits = 0;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(pos + 4, 4));
            var body = wav.Slice(pos + 8, Math.Min(size, wav.Length - pos - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && body.Length >= 26) // WAVE_FORMAT_EXTENSIBLE: real format is the sub-format GUID's first word
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                    throw new InvalidDataException("WAV data before fmt chunk");
                return new MonoAudio(Downmix(DecodeSamples(body, format, bits), channels), rate);
            }

            pos += 8 + size + (size & 1); // chunks are word aligned
        }

        throw new InvalidDataException("WAV has no data chunk");
    }

    private static float[] DecodeSamples(ReadOnlySpan<byte> data, int format, int bits)
    {
        int width = bits / 8;
        var samples = new float[data.Length / width];
        for (int i = 0; i < samples.Length; i++)
        {
            var s = data.Slice(i * width, width);
            samples[i] = (format, bits) switch
            {
                (1, 8) => (s[0] - 128) / 128f,
                (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (1, 24) => ((s[2] << 24 | s[1] << 16 | s[0] << 8) >> 8) / 8388608f,
                (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                _ => throw new InvalidDataException($"unsupported WAV format {format}, {bits}-bit"),
            };
        }

        return samples;
    }

    private static float[] Downmix(float[] interleaved, int channels)
    {
        if (channels == 1)
            return interleaved;
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }

        return mono;
    }

    /// <summary>
    /// Windowed-sinc (Blackman, 32 zero crossings) resampling. When downsampling, the kernel's cutoff
    /// sits at 0.45 × the new rate, so content that would alias is removed rather than folded back.
    /// </summary>
    public static MonoAudio Resample(MonoAudio audio, int targetRate)
    {
        if (audio.SampleRate == targetRate)
            return audio;

        const int HalfTaps = 16;
        double ratio = (double)targetRate / audio.SampleRate;
        double cutoff = Math.Min(1.0, ratio) * 0.9; // fraction of the source Nyquist
        double halfWidth = HalfTaps / cutoff;       // kernel half-width in source samples
        var src = audio.Samples;
        var output = new float[(int)(src.Length * ratio)];

        for (int i = 0; i < output.Length; i++)
        {
            double centre = i / ratio;
            int first = (int)Math.Ceiling(centre - halfWidth), last = (int)Math.Floor(centre + halfWidth);
            double sum = 0, weight = 0;
            for (int k = Math.Max(0, first); k <= Math.Min(src.Length - 1, last); k++)
            {
                double x = k - centre;
                double sinc = x == 0 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / halfWidth) + 0.08 * Math.Cos(2 * Math.PI * x / halfWidth);
                sum += src[k] * sinc * w;
                weight += sinc * w;
            }

            output[i] = (float)(weight != 0 ? sum / weight : 0);
        }

        return new MonoAudio(output, targetRate);
    }

    /// <summary>
    /// Joins clips into one utterance: each is scaled to the same RMS level, then neighbours overlap
    /// by a short crossfade so the joins don't click. A positive <paramref name="gapMs"/> inserts
    /// silence instead of overlapping.
    /// </summary>
    public static short[] Join(IReadOnlyList<MonoAudio> clips, int sampleRate, double targetRms = 0.18, double crossfadeMs = 25, double gapMs = 0)
    {
        var parts = clips.Select(c => Normalise(Resample(c, sampleRate).Samples, targetRms)).ToList();
        int fade = (int)(sampleRate * crossfadeMs / 1000), gap = (int)(sampleRate * gapMs / 1000);
        var output = new List<float>();
        foreach (var part in parts)
        {
            int overlap = gap > 0 ? 0 : Math.Min(fade, Math.Min(output.Count, part.Length) / 2);
            int start = output.Count - overlap;
            for (int i = 0; i < overlap; i++)
            {
                float t = (i + 0.5f) / overlap;
                output[start + i] = output[start + i] * (1 - t) + part[i] * t;
            }

            output.AddRange(part.AsSpan(overlap));
            if (gap > 0 && part != parts[^1])
                output.AddRange(new float[gap]);
        }

        var pcm = new short[output.Count];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Round(Math.Clamp(output[i], -1f, 1f) * 32767);
        return pcm;
    }

    private static float[] Normalise(float[] samples, double targetRms)
    {
        if (samples.Length == 0)
            return samples;
        double sum = 0;
        foreach (float s in samples)
            sum += s * s;
        double rms = Math.Sqrt(sum / samples.Length);
        if (rms < 1e-6)
            return samples;
        float gain = (float)(targetRms / rms);
        return samples.Select(s => s * gain).ToArray();
    }
}
