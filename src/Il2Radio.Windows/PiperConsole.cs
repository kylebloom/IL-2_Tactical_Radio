using Il2Radio.Core.Audio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Windows;

/// <summary>
/// The German phrase voice's start-up: asks with <see cref="PiperInstaller.ConsentLines"/> and
/// downloads only on an explicit yes (<see cref="PiperSetup"/> decides when to ask), then starts Piper.
/// Diag asks on the console. The player's window passes its own question and progress.
/// </summary>
internal static class PiperConsole
{
    /// <summary>The default store: a "piper" folder beside the program (never the game's folder).</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "piper");

    /// <summary>Console question, progress and log. A redirected console is not asked, so nothing is downloaded.</summary>
    public static Task<(PiperSetupResult Result, PiperSynthesizer? Synth)> StartAsync(PiperSettings settings, string? il2Root, CancellationToken cancel = default) =>
        StartAsync(settings, il2Root, Console.IsInputRedirected ? null : Ask, ConsoleProgress, ConsoleLog, cancel);

    /// <summary>The caller's question, progress and log. A null question means nobody can be asked, so nothing is downloaded.</summary>
    public static async Task<(PiperSetupResult Result, PiperSynthesizer? Synth)> StartAsync(PiperSettings settings, string? il2Root,
        Func<IReadOnlyList<string>, PiperAnswer>? ask, Action<PiperProgress>? progress, Action<string>? log, CancellationToken cancel = default)
    {
        string folder = settings.Folder ?? DefaultFolder;
        var result = await PiperSetup.PrepareAsync(settings, folder, il2Root, ask, OpenAsync, progress, log, cancel);
        if (result.Installation is not { } install)
            return (result, null);

        var synth = new PiperSynthesizer(install, settings.LengthScale);
        _ = synth.WarmUp(); // loads the model in the background; the caller may await its own WarmUp for the outcome
        return (result, synth);
    }

    /// <summary>Yes / No / Later on the console; Enter alone or a closed console is later, never yes.</summary>
    private static PiperAnswer Ask(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
            Console.WriteLine($"       {line}");
        while (true)
        {
            Console.Write($"       {PiperSetup.Choices}: ");
            if (PiperSetup.ParseAnswer(Console.ReadLine()) is { } answer)
                return answer;
            Console.WriteLine("       Please type Y, N or L and press Enter.");
        }
    }

    private static void ConsoleProgress(PiperProgress progress)
    {
        Console.Write($"\r       {progress}   ");
        if (progress.Done >= progress.Total)
            Console.WriteLine();
    }

    private static void ConsoleLog(string line) => Console.WriteLine($"       {line}");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Il2Radio/1.0");
        return client;
    }

    /// <summary>HTTPS GET, streamed (the files are 20–110 MB). Redirects to the hosts' CDNs are followed; HTTPS to HTTP is refused.</summary>
    private static async Task<Stream> OpenAsync(Uri url, CancellationToken cancel)
    {
        for (int hop = 0; ; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"refusing a non-HTTPS download: {url}");
            if (hop == 5)
                throw new InvalidDataException($"too many redirects fetching {url}");

            var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                Uri? next = response.Headers.Location;
                response.Dispose();
                if (next is null)
                    throw new InvalidDataException($"redirect from {url} had no destination");
                url = next.IsAbsoluteUri ? next : new Uri(url, next);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"download returned HTTP {code} for {url}");
            }

            return await response.Content.ReadAsStreamAsync(cancel);
        }
    }
}
