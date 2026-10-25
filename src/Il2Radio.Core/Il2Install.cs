using System.Text;

namespace Il2Radio.Core;

/// <summary>Locates an IL-2 Great Battles installation and the files this project reads.</summary>
public sealed class Il2Install(string root)
{
    public string Root { get; } = root;
    public string DataDir => Path.Combine(Root, "data");
    public string StartupCfg => Path.Combine(DataDir, "startup.cfg");
    public string GeneratedMission => Path.Combine(DataDir, "Missions", "_gen.Mission");
    public string FlightLogsDir => Path.Combine(DataDir, "FlightLogs");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string TracksDir => Path.Combine(DataDir, "Tracks");

    private static readonly string[] CandidateRoots =
    [
        @"C:\Program Files (x86)\Steam\steamapps\common\IL-2 Sturmovik Battle of Stalingrad",
        @"C:\Program Files\Steam\steamapps\common\IL-2 Sturmovik Battle of Stalingrad",
        @"C:\Program Files\1C Game Studios\IL-2 Sturmovik Great Battles",
        @"C:\Program Files (x86)\1C Game Studios\IL-2 Sturmovik Great Battles",
    ];

    /// <summary>Explicit path, then the IL2_PATH environment variable, then common install locations.</summary>
    public static Il2Install? Locate(string? explicitRoot = null)
    {
        IEnumerable<string?> candidates = [explicitRoot, Environment.GetEnvironmentVariable("IL2_PATH"), .. CandidateRoots];
        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Il2Install(p!))
            .FirstOrDefault(i => File.Exists(i.StartupCfg));
    }
}

/// <summary>
/// Reads and edits IL-2's startup.cfg, which is made of sections:
/// <code>
/// [KEY = name]
///     key = value
/// [END]
/// </code>
/// </summary>
public sealed class StartupConfig
{
    private readonly List<string> _lines;

    private StartupConfig(List<string> lines) => _lines = lines;

    public static StartupConfig Load(string path) => new([.. File.ReadAllLines(path)]);

    public static StartupConfig Parse(string text) => new([.. text.Replace("\r\n", "\n").Split('\n')]);

    public string? Get(string section, string key)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
            return null;
        for (int i = start + 1; i < end; i++)
        {
            var (k, v) = SplitKeyValue(_lines[i]);
            if (k == key)
                return v;
        }

        return null;
    }

    /// <summary>Sets a value, adding the key or the whole section when missing. Returns true if anything changed.</summary>
    public bool Set(string section, string key, string value)
    {
        var (start, end) = FindSection(section);
        if (start < 0)
        {
            _lines.AddRange([$"[KEY = {section}]", $"\t{key} = {value}", "[END]", ""]);
            return true;
        }

        for (int i = start + 1; i < end; i++)
        {
            var (k, v) = SplitKeyValue(_lines[i]);
            if (k != key)
                continue;
            if (v == value)
                return false;
            _lines[i] = $"\t{key} = {value}";
            return true;
        }

        _lines.Insert(end, $"\t{key} = {value}");
        return true;
    }

    public override string ToString() => string.Join(Environment.NewLine, _lines);

    public void Save(string path) => File.WriteAllText(path, ToString(), new UTF8Encoding(false));

    private (int Start, int End) FindSection(string section)
    {
        string header = $"[KEY = {section}]";
        int start = _lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            return (-1, -1);
        int end = _lines.FindIndex(start + 1, l => l.Trim() == "[END]");
        return (start, end < 0 ? _lines.Count : end);
    }

    private static (string? Key, string? Value) SplitKeyValue(string line)
    {
        int eq = line.IndexOf('=');
        return eq < 0 ? (null, null) : (line[..eq].Trim(), line[(eq + 1)..].Trim());
    }
}
