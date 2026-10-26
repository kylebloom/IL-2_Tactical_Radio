using System.Reflection;

namespace Il2Radio.Core;

/// <summary>The running program's version, from Directory.Build.props or the release tag.</summary>
public static class AppVersion
{
    /// <summary>For example "0.2.0 (e9d4624)": the version, then the commit it was built from when known.</summary>
    public static string Text { get; } = Describe(
        (Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Turns "0.2.0+e9d4624…" (the SDK appends the full commit) into "0.2.0 (e9d4624)".</summary>
    public static string Describe(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "unknown";
        int plus = informational.IndexOf('+');
        if (plus < 0)
            return informational;
        string commit = informational[(plus + 1)..];
        return $"{informational[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }
}
