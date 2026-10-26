using Il2Radio.Core;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Settings;
using Il2Radio.Windows;

namespace Il2Radio.App;

/// <summary>
/// One running radio: the start-up checks, the voice, the chat output and <see cref="LiveSession"/>.
/// Save and apply stops it and starts another in this process, so administrator rights stay.
/// </summary>
internal sealed class FlightHost
{
    private CancellationTokenSource? _stop;
    private Task? _run;
    private RadioVoice? _voice;
    private Il2AudioMeter? _meter;
    private IChatSink? _chat;
    private PiperSynthesizer? _piper;
    private int _generation;

    public RadioVoice? Voice => _voice;

    public bool Running => _run is { IsCompleted: false };

    /// <summary>A voice, chat output or session is still held and must be released on the way out.</summary>
    public bool NeedsRelease => Running || _voice is not null || _chat is not null || _meter is not null || _piper is not null;

    /// <summary>The session ended on its own. Null is the quit key; otherwise it failed.</summary>
    public event Action<Exception?>? Ended;

    /// <summary>
    /// Stops any current session, runs the checks and starts again. Returns a reason when it did not
    /// start (IL-2 or the voice); the check lines are still in <see cref="StartupChecks.Results"/>.
    /// The answer to the German-voice question (yes or no; not later) goes to <paramref name="rememberPiper"/> to be saved.
    /// </summary>
    public async Task<string?> StartAsync(AppSettings settings, string home, Action<CompactStatus> status,
        Func<string, bool> confirmOutputs, Func<IReadOnlyList<string>, PiperAnswer> confirmPiper,
        Action<PiperProgress> piperProgress, Action<string> piperLog, Action<bool> rememberPiper, CancellationToken cancel = default)
    {
        await StopAsync();
        int generation = Interlocked.Increment(ref _generation);
        StartupChecks.Reset();

        var install = Il2Install.Locate(settings.Il2Path);
        if (install is null)
        {
            return settings.Il2Path is null
                ? "IL-2 wasn't found in the usual Steam folders. Set the IL-2 folder in Settings (the one with data\\startup.cfg)."
                : $"No IL-2 install at \"{settings.Il2Path}\" (looked for data\\startup.cfg).";
        }

        StartupChecks.Ok($"IL-2 found: {install.Root}");
        List<string> warnings = [];
        if (StartupChecks.Elevation() is { } elevation)
            warnings.Add(elevation);
        if (StartupChecks.Outputs(install, confirmOutputs) is { } outputs)
            warnings.Add(outputs);
        StartupChecks.ChatOutput(settings.Chat, install);

        VoiceSelection voices;
        try
        {
            voices = VoiceSelection.Create(settings.Voice, install.Root);
        }
        catch (InvalidOperationException ex)
        {
            StartupChecks.Warn($"Voice \"{settings.Voice}\": {ex.Message}");
            return $"Voice \"{settings.Voice}\": {ex.Message}";
        }

        foreach (string problem in voices.Problems)
            StartupChecks.Warn($"Voice pack skipped: {problem}");

        try
        {
            _piper = await StartupChecks.PhraseVoice(settings, voices, install, confirmPiper, piperProgress, piperLog, rememberPiper, cancel);
            if (cancel.IsCancellationRequested)
            {
                ReleaseOutputs();
                throw new OperationCanceledException(cancel);
            }

            StartupChecks.Ok($"Voice: {voices.Describe()}");
            StartupChecks.Info("Track recording: press LCtrl+R once in the cockpit; positions come from that track."
                + (settings.RecordingReminderSeconds > 0
                    ? $" You'll be reminded {settings.RecordingReminderSeconds} s into a mission if it isn't on."
                    : ""));

            var keys = KeyBindings.From(settings.Keys);
            _voice = new RadioVoice(settings.Volume, voices);
            _meter = settings.RadioWait ? new Il2AudioMeter(settings.RadioWaitRatio) : null;
            _chat = ChatSinks.Start(ChatOptions.From(settings.Chat, keys).WithGameScreen(install).WithIl2Areas(install));
            var session = new LiveSession(install, new LiveOptions
            {
                Compact = true,
                Title = "IL-2 Tactical Radio",
                Keys = keys,
                OutputDir = Path.Combine(home, "logs"),
                RecordingReminder = TimeSpan.FromSeconds(settings.RecordingReminderSeconds),
                Realism = settings.RealismMode,
                StartupResults = StartupChecks.Results.ToArray(),
                Status = status,
            }, _voice);
            session.Attach(_meter, _chat);
            foreach (string warning in warnings)
                session.AlertEachMission(warning);

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _run = Task.Run(() => session.RunAsync(token));
            _ = _run.ContinueWith(t =>
            {
                if (generation != Volatile.Read(ref _generation))
                    return;
                Ended?.Invoke(t.IsFaulted ? t.Exception?.GetBaseException() : null);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return null;
        }
        catch
        {
            ReleaseOutputs();
            throw;
        }
    }

    /// <summary>Cancels the session and waits up to 4 s so the RTSS slot is released and the log is written.</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation); // a session that ends because of this stop is not a quit
        var stop = _stop;
        var run = _run;
        _stop = null;
        _run = null;
        if (stop is not null)
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }

            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (Exception) { /* timeout, or the session already failed; release either way */ }
            }

            if (run is null || run.IsCompleted)
                stop.Dispose();
        }

        ReleaseOutputs();
    }

    private void ReleaseOutputs()
    {
        DisposeQuiet(_chat);
        DisposeQuiet(_meter);
        DisposeQuiet(_voice);
        DisposeQuiet(_piper);
        _chat = null;
        _meter = null;
        _voice = null;
        _piper = null;
    }

    /// <summary>One output failing to close must not skip the others (the RTSS slot, in particular).</summary>
    private static void DisposeQuiet(IDisposable? item)
    {
        try { item?.Dispose(); }
        catch (Exception) { }
    }
}
