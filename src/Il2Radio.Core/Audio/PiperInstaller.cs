using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Il2Radio.Core.Settings;

namespace Il2Radio.Core.Audio;

/// <summary>A complete Piper install: the engine and one voice, ready to run.</summary>
public sealed record PiperInstallation(string Folder, string Executable, string Model, string Config, PiperVoice Voice);

/// <summary>Download progress for one file: what it is (in player's words), bytes so far, bytes in all.</summary>
public readonly record struct PiperProgress(string What, long Done, long Total)
{
    public int Percent => Total <= 0 ? 100 : (int)(Done * 100 / Total);

    public override string ToString() =>
        $"Downloading {What}: {Percent} % ({Done / 1_000_000.0:0.0} of {Total / 1_000_000.0:0.0} MB)";
}

/// <summary>
/// Downloads, checks and lays out Piper and a voice in the radio's own folder (never the game's):
/// <c>&lt;folder&gt;\runtime\...\piper.exe</c> (the release zip, extracted), <c>&lt;folder&gt;\voices\&lt;voice&gt;.onnx(.json)</c>.
/// Each file goes to <c>&lt;folder&gt;\downloads\*.part</c> first, is checked against its pinned size and hash,
/// and only then moved into place, so an interrupted or tampered download never looks installed.
/// The network is reached only through the <c>open</c> function the caller passes, after the player agreed.
/// </summary>
public static class PiperInstaller
{
    public static string RuntimeFolder(string folder) => Path.Combine(folder, "runtime");

    public static string VoicesFolder(string folder) => Path.Combine(folder, "voices");

    /// <summary>The install, or null when the engine or the voice is missing or incomplete.</summary>
    public static PiperInstallation? Find(string folder, PiperVoice voice) =>
        Missing(folder, voice).Count == 0 && FindExecutable(RuntimeFolder(folder)) is { } exe
            ? new PiperInstallation(folder, exe, ModelPath(folder, voice), ConfigPath(folder, voice), voice)
            : null;

    /// <summary>What still has to be downloaded: the engine if no usable piper.exe is there, and each voice file absent or of the wrong size.</summary>
    /// <param name="runtime">The engine download to list when it is missing (default <see cref="PiperCatalog.Runtime"/>).</param>
    public static IReadOnlyList<PinnedFile> Missing(string folder, PiperVoice voice, PinnedFile? runtime = null)
    {
        var missing = new List<PinnedFile>();
        if (FindExecutable(RuntimeFolder(folder)) is null)
            missing.Add(runtime ?? PiperCatalog.Runtime);
        foreach (var (file, path) in new[] { (voice.Model, ModelPath(folder, voice)), (voice.Config, ConfigPath(folder, voice)) })
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size)
                missing.Add(file);
        }

        return missing;
    }

    /// <summary>True when the voice is there but piper.exe is gone: typically an antivirus quarantined it.</summary>
    public static bool EngineVanished(string folder, PiperVoice voice) =>
        File.Exists(ModelPath(folder, voice)) && FindExecutable(RuntimeFolder(folder)) is null;

    /// <summary>
    /// The short question shown before anything is downloaded: what it is for, each file with its licence,
    /// size and source URL, the total, and where it goes. Kept to a few lines for a player, not a developer.
    /// </summary>
    public static IReadOnlyList<string> ConsentLines(string folder, PiperVoice voice, IReadOnlyList<PinnedFile> missing)
    {
        var lines = new List<string>
        {
            "Add a German voice for words the game's pilots can't say (aircraft names, \"nothing sighted\")?",
            $"It is a one-time download of {Size(missing.Sum(f => f.Size))}, and works offline afterwards:",
        };
        foreach (var file in missing)
        {
            string what = file == voice.Model ? $"German voice \"{voice.Speaker}\" ({voice.Licence})"
                : file == voice.Config ? $"voice settings for \"{voice.Speaker}\""
                : PiperCatalog.RuntimeDescription;
            lines.Add($"  - {what}, {Size(file.Size)}, from {file.Url}");
        }

        lines.Add($"Saved in {folder} (not in the IL-2 folder).");
        return lines;
    }

    private static Func<PinnedFile, bool> IsRuntime(PiperVoice voice) => f => f != voice.Model && f != voice.Config;

    /// <summary>
    /// Downloads and installs the missing files. Throws <see cref="InvalidDataException"/> when a file doesn't
    /// match its pinned size or hash (nothing of it is kept), or whatever <paramref name="open"/> throws.
    /// </summary>
    /// <param name="log">Lines worth keeping (the engine's SHA-256, which has no published value to check against).</param>
    public static async Task<PiperInstallation> InstallAsync(string folder, PiperVoice voice, IReadOnlyList<PinnedFile> missing,
        Func<Uri, CancellationToken, Task<Stream>> open, Action<PiperProgress>? progress = null, Action<string>? log = null, CancellationToken cancel = default)
    {
        string downloads = Path.Combine(folder, "downloads");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(VoicesFolder(folder));

        foreach (var file in missing)
        {
            string part = Path.Combine(downloads, file.FileName + ".part");
            string what = IsRuntime(voice)(file) ? "speech engine" : file == voice.Model ? "German voice" : "voice settings";
            try
            {
                await DownloadAsync(file, part, what, open, progress, log, cancel);

                if (IsRuntime(voice)(file))
                    InstallRuntime(part, RuntimeFolder(folder));
                else
                    File.Move(part, file == voice.Config ? ConfigPath(folder, voice) : ModelPath(folder, voice), overwrite: true);
            }
            finally
            {
                if (File.Exists(part))
                    File.Delete(part);
            }
        }

        TryDelete(downloads);
        return Find(folder, voice) ?? throw new InvalidDataException("the download finished, but the install is incomplete");
    }

    /// <summary>Streams one file to <paramref name="part"/>, refusing more bytes than pinned, then checks size and hashes.</summary>
    private static async Task DownloadAsync(PinnedFile file, string part, string what, Func<Uri, CancellationToken, Task<Stream>> open,
        Action<PiperProgress>? progress, Action<string>? log, CancellationToken cancel)
    {
        if (file.Url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"{file.FileName}: refusing a non-HTTPS download ({file.Url})");

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var gitSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        gitSha1.AppendData(Encoding.ASCII.GetBytes($"blob {file.Size.ToString(CultureInfo.InvariantCulture)}\0"));

        long total = 0;
        int lastPercent = -1;
        progress?.Invoke(new PiperProgress(what, 0, file.Size));
        await using (var source = await open(file.Url, cancel))
        await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81_920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                total += read;
                if (total > file.Size)
                    throw new InvalidDataException($"{file.FileName}: more data than the expected {file.Size:N0} bytes; download discarded");
                sha256.AppendData(buffer, 0, read);
                gitSha1.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);

                var now = new PiperProgress(what, total, file.Size);
                if (now.Percent != lastPercent)
                {
                    lastPercent = now.Percent;
                    progress?.Invoke(now);
                }
            }
        }

        if (total != file.Size)
            throw new InvalidDataException($"{file.FileName}: got {total:N0} bytes, expected {file.Size:N0}; download discarded");
        string sha256Hex = Convert.ToHexStringLower(sha256.GetHashAndReset());
        if (file.Sha256 is { } expectedSha && !sha256Hex.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{file.FileName}: SHA-256 {sha256Hex} doesn't match the published {expectedSha}; download discarded");
        if (file.GitBlobSha1 is { } expectedBlob && Convert.ToHexStringLower(gitSha1.GetHashAndReset()) is var blob && !blob.Equals(expectedBlob, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{file.FileName}: content id {blob} doesn't match the published {expectedBlob}; download discarded");
        if (!file.HasHash)
            log?.Invoke($"{file.FileName}: size checked (no published checksum); SHA-256 {sha256Hex}");
    }

    /// <summary>
    /// Extracts the release zip next to the final folder, checks it holds piper.exe with espeak-ng-data beside
    /// it, then swaps it in. .NET's extraction refuses entries that would land outside the target folder.
    /// </summary>
    private static void InstallRuntime(string zip, string runtime)
    {
        string staging = runtime + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        try
        {
            ZipFile.ExtractToDirectory(zip, staging);
        }
        catch (InvalidDataException ex)
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw new InvalidDataException($"{Path.GetFileName(zip)}: not a readable zip ({ex.Message})");
        }

        if (FindExecutable(staging) is null)
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidDataException("the Piper download has no piper.exe with espeak-ng-data beside it");
        }

        if (Directory.Exists(runtime))
            Directory.Delete(runtime, recursive: true);
        Directory.Move(staging, runtime);
    }

    /// <summary>
    /// Deletes what the installer created (runtime, voices, leftovers) and then the folder if it is empty.
    /// Nothing else in the folder is touched. Returns true when anything was removed.
    /// </summary>
    public static bool Remove(string folder)
    {
        bool removed = false;
        foreach (string sub in new[] { RuntimeFolder(folder), RuntimeFolder(folder) + ".new", VoicesFolder(folder), Path.Combine(folder, "downloads") })
        {
            if (!Directory.Exists(sub))
                continue;
            Directory.Delete(sub, recursive: true);
            removed = true;
        }

        TryDelete(folder);
        return removed;
    }

    /// <summary>piper.exe anywhere under the folder, with the espeak-ng-data folder it needs beside it (Piper looks there by default).</summary>
    public static string? FindExecutable(string runtime) =>
        Directory.Exists(runtime)
            ? Directory.EnumerateFiles(runtime, "piper.exe", SearchOption.AllDirectories)
                .FirstOrDefault(exe => Directory.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "espeak-ng-data")))
            : null;

    private static string ModelPath(string folder, PiperVoice voice) => Path.Combine(VoicesFolder(folder), voice.Model.FileName);

    private static string ConfigPath(string folder, PiperVoice voice) => Path.Combine(VoicesFolder(folder), voice.Config.FileName);

    public static string Size(long bytes) =>
        bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{Math.Max(1, bytes / 1_000):0} KB";

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The player's answer to the download question.</summary>
public enum PiperAnswer
{
    Yes,

    /// <summary>Remembered (<c>piper.enabled</c> false): not asked again until the player turns it on in Settings.</summary>
    No,

    /// <summary>Not remembered: asked again at the next start. Closing the question counts as this, never as yes.</summary>
    Later,
}

public enum PiperSetupStatus
{
    /// <summary>Turned off (<c>piper.enabled</c> is false): nothing checked, nothing downloaded.</summary>
    Off,

    /// <summary>Installed and ready.</summary>
    Ready,

    /// <summary>Not installed: the player said no or later, or nobody could be asked.</summary>
    NotInstalled,

    /// <summary>The download or install failed, or the settings point somewhere they mustn't.</summary>
    Failed,
}

/// <param name="Remember">The answer to save as <c>piper.enabled</c> in settings.json, or null to leave the file as it is.</param>
public sealed record PiperSetupResult(PiperSetupStatus Status, PiperInstallation? Installation, string Message, bool? Remember = null);

/// <summary>
/// Start-up decision for the German phrase voice. While <c>piper.enabled</c> is unanswered (null) or true and
/// the files are missing, the player is asked once, in plain words, with what would be downloaded, from where
/// and how big: Yes / No / Later. Only yes reaches the network. Yes and no are returned in
/// <see cref="PiperSetupResult.Remember"/> for the caller to save; later is not saved, so the question comes back
/// next start. Every path without the voice leaves the radio as it was: the game's recordings, the class word
/// for types, and the full text on screen.
/// </summary>
public static class PiperSetup
{
    /// <summary>How to change one's mind, in the player's words.</summary>
    public const string HowToChange = "You can change this any time under Settings, German phrase voice.";

    /// <summary>The console's choices (Diag); the player's window has a button for each.</summary>
    public const string Choices = "[Y]es / [N]o / [L]ater";

    /// <param name="ask">Shows the lines and returns the answer. Null when nobody can be asked (no console or window): then nothing is downloaded.</param>
    public static async Task<PiperSetupResult> PrepareAsync(PiperSettings settings, string folder, string? il2Root,
        Func<IReadOnlyList<string>, PiperAnswer>? ask, Func<Uri, CancellationToken, Task<Stream>> open,
        Action<PiperProgress>? progress = null, Action<string>? log = null, CancellationToken cancel = default)
    {
        if (settings.Enabled == false)
            return new(PiperSetupStatus.Off, null, $"German phrase voice: off. {HowToChange}");
        if (PiperCatalog.Find(settings.Voice) is not { } voice)
            return new(PiperSetupStatus.Failed, null, $"Unknown German voice \"{settings.Voice}\" in settings.json (piper.voice).");

        folder = Path.GetFullPath(folder);
        if (il2Root is not null && IsInside(folder, Path.GetFullPath(il2Root)))
            return new(PiperSetupStatus.Failed, null, $"The German voice can't be stored in {folder}, because that is inside the IL-2 folder. Change piper.folder in settings.json.");

        if (PiperInstaller.Find(folder, voice) is { } ready)
            return new(PiperSetupStatus.Ready, ready, "German phrase voice: ready", Remember: settings.Enabled == true ? null : true);

        if (ask is null)
            return new(PiperSetupStatus.NotInstalled, null, "German phrase voice not installed. Start Il2Radio.exe by double-clicking it to be asked about the download.");

        var missing = PiperInstaller.Missing(folder, voice);
        var lines = PiperInstaller.ConsentLines(folder, voice, missing).ToList();
        if (PiperInstaller.EngineVanished(folder, voice))
            lines.Add("(The engine file piper.exe has disappeared; an antivirus program may have removed it.)");
        switch (ask(lines))
        {
            case PiperAnswer.No:
                return new(PiperSetupStatus.NotInstalled, null, $"No German phrase voice; you won't be asked again. {HowToChange}", Remember: false);
            case PiperAnswer.Later:
                return new(PiperSetupStatus.NotInstalled, null, "German phrase voice not downloaded. You'll be asked again next time the radio starts.");
        }

        try
        {
            var installed = await PiperInstaller.InstallAsync(folder, voice, missing, open, progress, log, cancel);
            return new(PiperSetupStatus.Ready, installed, "German phrase voice: installed", Remember: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            // Yes is remembered even though it failed: the next start offers the download again.
            log?.Invoke($"German voice download failed: {ex.Message}");
            return new(PiperSetupStatus.Failed, null, FriendlyError(ex, folder), Remember: true);
        }
    }

    /// <summary>
    /// A typed console answer (Diag): y/yes, n/no, l/later. Just Enter, or the input closing, counts as later,
    /// never as yes. Anything else is null, so the question is asked again.
    /// </summary>
    public static PiperAnswer? ParseAnswer(string? input) => input?.Trim().ToLowerInvariant() switch
    {
        null or "" or "l" or "later" => PiperAnswer.Later,
        "y" or "yes" => PiperAnswer.Yes,
        "n" or "no" => PiperAnswer.No,
        _ => null,
    };

    /// <summary>
    /// Two plain sentences for a failed download: what went wrong, and what happens next. The technical cause
    /// (HTTP status, hash) goes to the log, not here.
    /// </summary>
    public static string FriendlyError(Exception ex, string folder)
    {
        string what = ex switch
        {
            HttpRequestException or TaskCanceledException => "The German phrase voice couldn't be downloaded (no internet connection, or the website didn't answer).",
            InvalidDataException => "The German phrase voice download arrived damaged or different from what was expected, so it was thrown away.",
            UnauthorizedAccessException => $"The German phrase voice couldn't be saved in {folder} (no permission to write there).",
            _ => $"The German phrase voice couldn't be saved in {folder} ({ex.Message}).",
        };
        return $"{what} The radio works without it and will offer the download again next time it starts (or choose Save and apply in Settings to try now).";
    }

    private static bool IsInside(string path, string root)
    {
        string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
