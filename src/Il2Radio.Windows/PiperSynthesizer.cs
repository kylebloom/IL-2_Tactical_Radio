using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Il2Radio.Core.Audio;

namespace Il2Radio.Windows;

/// <summary>
/// Runs piper.exe as one long-lived child process and asks it for short phrases. Protocol (piper 2023.11.14-2,
/// VERIFIED from its src/cpp/main.cpp; not yet run on this machine): each line of UTF-8 text on stdin is
/// synthesized to a new WAV file in <c>--output_dir</c>, and that file's path is printed as one line on stdout.
/// <c>--noise_scale</c> and <c>--noise_w</c> come from <see cref="PhraseDelivery"/>: flatter than the model defaults.
/// Results are cached by text: the vocabulary is a few dozen words, so after the first call each costs nothing.
/// Failures return null (the radio then falls back to the recordings alone, or text-to-speech).
/// </summary>
internal sealed class PiperSynthesizer : IPhraseSynthesizer, IDisposable
{
    private static readonly TimeSpan FirstReplyTimeout = TimeSpan.FromSeconds(20); // loading a 60–110 MB model
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(8);

    private readonly PiperInstallation _install;
    private readonly double _lengthScale;
    private readonly string _outputDir;
    private readonly ConcurrentDictionary<string, MonoAudio> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Process? _process;
    private BlockingCollection<string>? _lines;
    private bool _answeredOnce;
    private int _failures;

    public PiperSynthesizer(PiperInstallation install, double lengthScale)
    {
        _install = install;
        _lengthScale = lengthScale;
        _outputDir = Path.Combine(Path.GetTempPath(), $"Il2Radio-piper-{Environment.ProcessId}");
    }

    public string Language => _install.Voice.Language;

    public string Name => $"Piper {_install.Voice.Name}";

    /// <summary>The last problem, for the monitor and the evidence file.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gives up for the session after this many failures in a row, so a broken install can't stall every reply.</summary>
    private const int MaxFailures = 3;

    /// <summary>Starts the process and loads the model in the background, so the first reply isn't delayed by it.</summary>
    public void WarmUp() => ThreadPool.QueueUserWorkItem(_ => Synthesize(SynthWords.German(new(Core.Radio.SpeechWord.NoContacts))!));

    public MonoAudio? Synthesize(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); // one line, no control characters
        if (text.Length == 0)
            return null;
        if (_cache.TryGetValue(text, out var cached))
            return cached;

        lock (_gate)
        {
            if (_cache.TryGetValue(text, out cached))
                return cached;
            if (_failures >= MaxFailures)
                return null;
            try
            {
                var audio = Run(text);
                _cache[text] = audio;
                _failures = 0;
                return audio;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                LastError = ex.Message;
                _failures++;
                StopProcess(); // start afresh next time
                return null;
            }
        }
    }

    private MonoAudio Run(string text)
    {
        EnsureProcess();
        _process!.StandardInput.WriteLine(text);
        _process.StandardInput.Flush();

        var timeout = _answeredOnce ? ReplyTimeout : FirstReplyTimeout;
        if (!_lines!.TryTake(out string? path, timeout))
            throw _process.HasExited
                ? new InvalidOperationException($"piper exited with code {_process.ExitCode}")
                : new TimeoutException($"piper gave no answer within {timeout.TotalSeconds:F0} s");
        _answeredOnce = true;

        path = path.Trim();
        if (!File.Exists(path))
            throw new InvalidDataException($"piper reported {path}, which doesn't exist");
        try
        {
            return ClipAudio.ParseWav(File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private void EnsureProcess()
    {
        if (_process is { HasExited: false })
            return;
        StopProcess();
        Directory.CreateDirectory(_outputDir);

        var start = new ProcessStartInfo(_install.Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(_install.Executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), // umlauts; a BOM would be read as text
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[]
                 {
                     "--model", _install.Model, "--config", _install.Config, "--output_dir", _outputDir,
                     "--length_scale", _lengthScale.ToString("0.###", CultureInfo.InvariantCulture),
                     "--noise_scale", PhraseDelivery.NoiseScale.ToString("0.###", CultureInfo.InvariantCulture),
                     "--noise_w", PhraseDelivery.NoiseW.ToString("0.###", CultureInfo.InvariantCulture),
                     "--quiet",
                 })
            start.ArgumentList.Add(arg);

        var lines = new BlockingCollection<string>();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            try
            {
                if (e.Data is { Length: > 0 } line)
                    lines.Add(line);
            }
            catch (ObjectDisposedException)
            {
                // the process is being replaced; its late output is of no use
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
                LastError = line; // with --quiet only real errors appear here
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        (_process, _lines, _answeredOnce) = (process, lines, false);
    }

    private void StopProcess()
    {
        if (_process is null)
            return;
        try
        {
            if (!_process.HasExited)
                _process.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        _lines?.Dispose();
        (_process, _lines) = (null, null);
    }

    public void Dispose()
    {
        lock (_gate)
            StopProcess();
        try
        {
            if (Directory.Exists(_outputDir))
                Directory.Delete(_outputDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
