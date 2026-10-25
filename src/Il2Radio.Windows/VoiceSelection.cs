using Il2Radio.Core.Audio;

namespace Il2Radio.Windows;

/// <summary>
/// Which voice speaks a transmission. <c>auto</c>: recorded clips for the speaker's nation when a pack
/// covers it (user packs before the game's own), otherwise Windows TTS. <c>tts</c>: always TTS.
/// Anything else names one pack (manifest path, folder, or part of its name) used for every nation.
/// </summary>
internal sealed class VoiceSelection
{
    private readonly List<ClipVoice> _voices;
    private readonly ClipVoice? _forced;

    private VoiceSelection(string mode, List<ClipVoice> voices, ClipVoice? forced, List<string> problems)
    {
        Mode = mode;
        _voices = voices;
        _forced = forced;
        Problems = problems.Distinct().ToList(); // a folder copy and the built-in manifest fail the same way
    }

    public string Mode { get; }
    public IReadOnlyList<ClipVoice> Voices => _voices;
    public IReadOnlyList<string> Problems { get; }

    public static VoiceSelection TtsOnly { get; } = new("tts", [], null, []);

    /// <summary>Folders searched for manifests: ./voices, then voices next to the executable.</summary>
    public static IReadOnlyList<string> Folders =>
        new[] { Path.Combine(Environment.CurrentDirectory, "voices"), Path.Combine(AppContext.BaseDirectory, "voices") }
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static VoiceSelection Create(string? mode, string? il2Root)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim();
        if (mode.Equals("tts", StringComparison.OrdinalIgnoreCase))
            return TtsOnly;

        var problems = new List<string>();
        var packs = new List<VoicePack>();
        foreach (var pack in VoicePack.Discover(Folders, il2Root, problems).Concat(BuiltIn(il2Root, problems)))
        {
            if (packs.All(p => !p.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase)))
                packs.Add(pack); // the same pack in ./voices, next to the exe and built in: first wins, so an edited copy overrides
        }

        // Your own recordings take precedence over the game's for the same nation.
        var voices = packs.OrderBy(p => p.FromGame).Select(p => new ClipVoice(p)).ToList();
        if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return new VoiceSelection("auto", voices, null, problems);

        ClipVoice? forced = null;
        string manifest = Directory.Exists(mode) ? Path.Combine(mode, "voice.json") : mode;
        if (File.Exists(manifest))
        {
            var pack = VoicePack.Load(manifest, il2Root) ?? throw new InvalidOperationException($"{manifest} needs the IL-2 install (pass --game or set IL2_PATH)");
            forced = new ClipVoice(pack);
        }
        else
        {
            forced = voices.FirstOrDefault(v => v.Pack.Name.Contains(mode, StringComparison.OrdinalIgnoreCase)
                                                || Path.GetFileNameWithoutExtension(v.Pack.ManifestPath).Contains(mode, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"no voice pack matches '{mode}' (searched {string.Join(", ", Folders)})");
        }

        return new VoiceSelection(forced.Pack.Name, [forced], forced, problems);
    }

    /// <summary>The repository's voices/*.json manifests, embedded in this assembly. They point into the IL-2 install, so need it.</summary>
    private static IEnumerable<VoicePack> BuiltIn(string? il2Root, List<string> problems)
    {
        var assembly = typeof(VoiceSelection).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("voices/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            string label = Path.Combine(AppContext.BaseDirectory, "(built in) " + Path.GetFileName(name));
            VoicePack? pack = null;
            try
            {
                pack = VoicePack.Parse(reader.ReadToEnd(), label, il2Root);
                if (pack is null)
                    problems.Add($"{Path.GetFileName(name)}: needs the IL-2 install (pass --game or set IL2_PATH)");
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                problems.Add($"{name}: {ex.Message}");
            }

            if (pack is not null)
                yield return pack;
        }
    }

    /// <summary>The clip voice for a speaker of this IL-2 country, or null for text-to-speech.</summary>
    public ClipVoice? For(int country) =>
        _forced ?? (country > 0 ? _voices.FirstOrDefault(v => v.Pack.Countries.Count > 0 && v.Pack.SpeaksFor(country))
                                  ?? _voices.FirstOrDefault(v => v.Pack.Countries.Count == 0)
                               : null);

    public string Describe() => Mode switch
    {
        "tts" => "Windows text-to-speech only",
        "auto" when _voices.Count == 0 => "Windows text-to-speech (no voice packs found)",
        "auto" => $"by nation: {string.Join("; ", _voices.Select(v => $"{v.Pack.Name} [{(v.Pack.Countries.Count == 0 ? "any" : string.Join(",", v.Pack.Countries))}]"))}; TTS fallback",
        _ => $"{Mode} for every speaker; TTS fallback",
    };
}
