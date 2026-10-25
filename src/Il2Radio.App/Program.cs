using System.Runtime.InteropServices;
using Il2Radio.App;
using Il2Radio.Core;
using Il2Radio.Core.Settings;
using Il2Radio.Windows;

// IL-2 Tactical Radio: double-click Il2Radio.exe (it asks for administrator rights), then fly.
// Everything lives beside the program. An elevated start from Explorer runs in System32, so the
// working directory is never used.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "IL-2 Tactical Radio";
string home = AppContext.BaseDirectory;
string settingsPath = Path.Combine(home, "settings.json");

using var single = new Mutex(initiallyOwned: true, @"Local\Il2Radio.App", out bool first);
if (!first)
    return Exit(1, "The radio is already running in another window.");

Console.WriteLine("IL-2 Tactical Radio\n");
AppSettings settings;
try
{
    (settings, bool created, var problems) = AppSettings.LoadOrCreate(settingsPath);
    if (problems.Count > 0)
        return Exit(1, [$"There is a problem in {settingsPath}:", .. problems.Select(p => "  " + p), "",
            "Fix it, or delete the file to get the defaults back, then start the radio again."]);
    Console.WriteLine(created
        ? $"Created {settingsPath} with the default settings.\nEdit it to change the volume, voice, radio text and keys, then restart the radio.\n"
        : $"Settings: {settingsPath}\n");
}
catch (IOException ex)
{
    return Exit(1, $"Can't read or create {settingsPath}: {ex.Message}");
}

var install = Il2Install.Locate(settings.Il2Path);
if (install is null)
    return Exit(1, settings.Il2Path is null
        ? $"IL-2 wasn't found in the usual Steam folders. Set \"il2Path\" in {settingsPath} to your IL-2 folder (the one with data\\startup.cfg)."
        : $"No IL-2 install at \"{settings.Il2Path}\" (looked for data\\startup.cfg). Fix \"il2Path\" in {settingsPath}.");

Console.WriteLine("Checks:");
StartupChecks.Ok($"IL-2 found: {install.Root}");
List<string> warnings = [];
if (StartupChecks.Elevation() is { } elevation)
    warnings.Add(elevation);
if (StartupChecks.Outputs(install) is { } outputs)
    warnings.Add(outputs);
StartupChecks.ChatOutput(settings.Chat, install);

VoiceSelection voices;
try
{
    voices = VoiceSelection.Create(settings.Voice, install.Root);
}
catch (InvalidOperationException ex)
{
    return Exit(1, $"Voice \"{settings.Voice}\" in {settingsPath}: {ex.Message}");
}
foreach (string problem in voices.Problems)
    StartupChecks.Warn($"Voice pack skipped: {problem}");
StartupChecks.Ok($"Voice: {voices.Describe()}");
StartupChecks.Info($"Track recording: press LCtrl+R once in the cockpit; positions come from that track.{(settings.RecordingReminderSeconds > 0 ? $" You'll be reminded {settings.RecordingReminderSeconds} s into a mission if it isn't on." : "")}");
Console.WriteLine();

// Closing the console window: stop cleanly (release the RTSS slot, write the session log) before Windows ends the process.
using var stop = new CancellationTokenSource();
using var finished = new ManualResetEventSlim();
Action<PosixSignalContext> onClose = _ =>
{
    stop.Cancel();
    finished.Wait(TimeSpan.FromSeconds(4));
};
using var onHangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, onClose);
using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onClose);

try
{
    var keys = KeyBindings.From(settings.Keys);
    using var voice = new RadioVoice(settings.Volume, voices);
    using var meter = settings.RadioWait ? new Il2AudioMeter(settings.RadioWaitRatio) : null;
    using var chat = ChatSinks.Start(ChatOptions.From(settings.Chat, keys).WithGameScreen(install).WithIl2Areas(install));
    var session = new LiveSession(install, new LiveOptions
    {
        Compact = true,
        Title = "IL-2 Tactical Radio",
        Keys = keys,
        OutputDir = Path.Combine(home, "logs"),
        RecordingReminder = TimeSpan.FromSeconds(settings.RecordingReminderSeconds),
        StartupResults = StartupChecks.Results,
    }, voice);
    session.Attach(meter, chat);
    foreach (string warning in warnings)
        session.AlertEachMission(warning);
    return await session.RunAsync(stop.Token);
}
catch (Exception ex) when (!stop.IsCancellationRequested)
{
    return Exit(1, "The radio stopped because of an error:", ex.ToString());
}
finally
{
    finished.Set();
}

// Prints the message and, since a double-clicked console closes with the program, waits for a key.
static int Exit(int code, params string[] lines)
{
    foreach (string line in lines)
        Console.WriteLine(line);
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("\nPress any key to close.");
        Console.ReadKey(intercept: true);
    }
    return code;
}
