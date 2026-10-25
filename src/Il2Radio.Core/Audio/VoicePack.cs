using System.Text.Json;
using System.Text.Json.Serialization;

namespace Il2Radio.Core.Audio;

/// <summary>
/// A set of recorded clips described by a JSON manifest (see <c>voices/README.md</c>). Clips are read
/// where they are, never copied: a pack can point into the user's own IL-2 install with <c>{il2}</c>,
/// or at WAV/OGG files next to the manifest.
/// </summary>
/// <example>
/// <code>
/// {
///   "name": "IL-2 German pilots",
///   "language": "de",
///   "countries": [201],
///   "root": "{il2}/data/Audio/radio/ger",
///   "speakers": ["actor51", "actor52"],
///   "preprocessed": true,
///   "clips": { "clock.3": "{speaker}/varDir/03clock.ogg" }
/// }
/// </code>
/// </example>
public sealed class VoicePack
{
    private readonly Dictionary<string, string> _clips;
    private readonly Dictionary<string, bool> _exists = new();

    private VoicePack(Manifest m, string manifestPath, string root)
    {
        Name = m.Name ?? Path.GetFileNameWithoutExtension(manifestPath);
        Language = m.Language ?? "";
        Countries = m.Countries ?? [];
        Speakers = m.Speakers is { Count: > 0 } s ? s : [""];
        PreProcessed = m.PreProcessed;
        ManifestPath = manifestPath;
        Root = root;
        _clips = new Dictionary<string, string>(m.Clips ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }
    public string Language { get; }

    /// <summary>IL-2 country codes this pack speaks for (101 USSR, 102 GB, 103 USA, 201 Germany); empty for any.</summary>
    public IReadOnlyList<int> Countries { get; }

    /// <summary>Speaker folders substituted for <c>{speaker}</c>; one entry ("") for a single-speaker pack.</summary>
    public IReadOnlyList<string> Speakers { get; }

    /// <summary>The clips already sound like radio (band-limited, compressed, hiss): apply a lighter effect.</summary>
    public bool PreProcessed { get; }

    public string ManifestPath { get; }
    public string Root { get; }

    /// <summary>True when the pack uses the IL-2 install, i.e. the game's own recordings.</summary>
    public bool FromGame { get; private init; }

    public IEnumerable<string> Keys => _clips.Keys;

    public bool SpeaksFor(int country) => Countries.Count == 0 || Countries.Contains(country);

    /// <summary>Stable speaker choice: formation number 1 is the first speaker, 2 the second, and so on, wrapping.</summary>
    public int SpeakerIndex(int speakerId) => (int)((uint)(speakerId - 1) % (uint)Speakers.Count);

    /// <summary>Full path of a clip for a speaker, or null if the manifest lacks it or the file is missing.</summary>
    public string? ClipPath(string key, int speakerIndex)
    {
        if (!_clips.TryGetValue(key, out var relative))
            return null;
        string path = Path.GetFullPath(Path.Combine(Root, relative.Replace("{speaker}", Speakers[speakerIndex])));
        if (!_exists.TryGetValue(path, out bool exists))
            _exists[path] = exists = File.Exists(path);
        return exists ? path : null;
    }

    /// <summary>
    /// Loads a manifest. Returns null when it refers to the IL-2 install (<c>{il2}</c>) and none is known.
    /// A relative root is relative to the manifest's folder.
    /// </summary>
    public static VoicePack? Load(string manifestPath, string? il2Root)
    {
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidDataException($"{manifestPath}: empty manifest");
        string root = m.Root ?? ".";
        bool fromGame = root.Contains("{il2}", StringComparison.OrdinalIgnoreCase);
        if (fromGame)
        {
            if (il2Root is null)
                return null;
            root = root.Replace("{il2}", il2Root, StringComparison.OrdinalIgnoreCase);
        }

        root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, root));
        return new VoicePack(m, manifestPath, root) { FromGame = fromGame };
    }

    /// <summary>
    /// Every manifest in the given folders: <c>*.json</c> directly inside, and <c>*/voice.json</c> one level down.
    /// Packs that can't be used here (no IL-2 install, unreadable JSON) are reported in <paramref name="problems"/>.
    /// </summary>
    public static List<VoicePack> Discover(IEnumerable<string> folders, string? il2Root, List<string>? problems = null)
    {
        var packs = new List<VoicePack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders.Where(Directory.Exists))
        {
            var manifests = Directory.EnumerateFiles(folder, "*.json")
                .Concat(Directory.EnumerateDirectories(folder).Select(d => Path.Combine(d, "voice.json")).Where(File.Exists));
            foreach (string manifest in manifests.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add(Path.GetFullPath(manifest)))
                    continue;
                try
                {
                    if (Load(manifest, il2Root) is { } pack)
                        packs.Add(pack);
                    else
                        problems?.Add($"{Path.GetFileName(manifest)}: needs the IL-2 install (pass --game or set IL2_PATH)");
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
                {
                    problems?.Add($"{manifest}: {ex.Message}");
                }
            }
        }

        return packs;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record Manifest
    {
        public string? Name { get; init; }
        public string? Language { get; init; }
        public List<int>? Countries { get; init; }
        public string? Root { get; init; }
        public List<string>? Speakers { get; init; }

        [JsonPropertyName("preprocessed")]
        public bool PreProcessed { get; init; }

        public Dictionary<string, string>? Clips { get; init; }
    }
}
