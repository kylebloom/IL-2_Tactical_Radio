namespace Il2Radio.Core.Audio;

/// <summary>
/// A file the radio may download, pinned to the exact bytes checked when it was added: size always, and a
/// hash where the host publishes one. <see cref="Sha256"/> is Hugging Face's LFS object id (the SHA-256 of the
/// file); <see cref="GitBlobSha1"/> is the git blob id of a small file (SHA-1 of "blob &lt;size&gt;\0" + content).
/// </summary>
public sealed record PinnedFile(string FileName, Uri Url, long Size, string? Sha256 = null, string? GitBlobSha1 = null)
{
    /// <summary>True when the download can be checked against a published hash, not only its size.</summary>
    public bool HasHash => Sha256 is not null || GitBlobSha1 is not null;
}

/// <summary>A Piper voice: the ONNX model and its JSON config, with the model card's facts for the consent prompt.</summary>
public sealed record PiperVoice(string Name, string Language, string Quality, int SampleRate, string Licence, PinnedFile Model, PinnedFile Config)
{
    public long Size => Model.Size + Config.Size;

    /// <summary>The speaker's name, for the player ("thorsten" from "de_DE-thorsten-medium").</summary>
    public string Speaker => Name.Split('-') is [_, var speaker, _] ? speaker : Name;
}

/// <summary>
/// What can be downloaded for the German phrase voice, pinned to versions checked on 2026-10-25 (see
/// FEASIBILITY.md, "German phrase voice (Piper)"). Nothing here is fetched unless the player agrees.
/// </summary>
public static class PiperCatalog
{
    /// <summary>
    /// The last native Piper release with a Windows build (rhasspy/piper 2023.11.14-2, MIT; the repository was
    /// archived 2025-10-06 and its successor, piper1-gpl, ships no Windows executable). GitHub publishes no
    /// checksum for this asset (API "digest": null), so it is checked by size and by its contents (piper.exe
    /// with espeak-ng-data beside it).
    /// </summary>
    public static PinnedFile Runtime { get; } = new(
        "piper_windows_amd64.zip",
        new Uri("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip"),
        22_477_236);

    public const string RuntimeDescription = "Piper speech engine (MIT licence)";

    public const string DefaultVoice = "de_DE-thorsten-medium";

    /// <summary>German voices (rhasspy/piper-voices, tag v1.0.0). Male voices only: the game's German pilots are men.</summary>
    public static IReadOnlyList<PiperVoice> Voices { get; } =
    [
        Voice("thorsten", "medium", 22_050, "free CC0 recordings",
            63_201_294, "7e64762d8e5118bb578f2eea6207e1a35a8e0c30595010b666f983fc87bb7819", 4_819, "71e11587184e86ebf875c9e449cee0f8df691496"),
        Voice("thorsten", "high", 22_050, "free CC0 recordings",
            113_895_201, "9df1c43c61149ef9b39e618e2b861fbe41e1fcea9390b2dac62e8761573ea4f1", 4_875, "2329bd2dfa0c3d7aac10f25fb8fabf754a01ffcf"),
        Voice("thorsten", "low", 16_000, "free CC0 recordings",
            63_104_526, "9ac27fad17cec5c1a791161976a64f026f16fc058b400b1fea62565b8b2cf375", 4_159, "009f4b88f7b412a98cc21d4a78c269da580abd9a"),
        Voice("karlsson", "low", 16_000, "M-AILABS recordings licence",
            63_104_526, "f9793bf7e461a472b3916cf260774bfc9812e9eb72601b147ed649d01064e8fe", 4_159, "045aec0753965f021e5bc769f55ca231fd2108e9"),
    ];

    public static PiperVoice? Find(string name) => Voices.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static PiperVoice Voice(string speaker, string quality, int rate, string licence, long modelSize, string modelSha256, long configSize, string configBlob)
    {
        string name = $"de_DE-{speaker}-{quality}";
        string folder = $"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/de/de_DE/{speaker}/{quality}/";
        return new PiperVoice(name, "de", quality, rate, licence,
            new PinnedFile($"{name}.onnx", new Uri(folder + $"{name}.onnx"), modelSize, Sha256: modelSha256),
            new PinnedFile($"{name}.onnx.json", new Uri(folder + $"{name}.onnx.json"), configSize, GitBlobSha1: configBlob));
    }
}
