using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Audio;

/// <summary>
/// One part of a reply: a recorded clip (<see cref="Key"/>) or words for a phrase synthesizer
/// (<see cref="Text"/>). A required synthesized part carries the message: if the synthesizer fails,
/// the whole reply goes to text-to-speech; an optional one is simply left out.
/// </summary>
public sealed record PhraseSegment(string? Key, string? Text, bool Required)
{
    public bool IsClip => Key is not null;

    public static PhraseSegment Clip(string key) => new(key, null, false);

    public static PhraseSegment Synth(string text, bool required) => new(null, text, required);

    public override string ToString() => Key ?? $"\"{Text}\"";
}

/// <summary>
/// How Piper is asked to say a fill-in word. thorsten-medium defaults to a noise scale of 0.667 and a
/// phoneme-width noise of 0.8, a wandering read-aloud delivery. These are flatter, so a one-word insert
/// sits closer to the game's short recorded fragments. The spectrum match itself is
/// <see cref="RadioEffectSettings.LikeGameRecordings"/>.
/// </summary>
public static class PhraseDelivery
{
    public const double NoiseScale = 0.4;

    public const double NoiseW = 0.5;
}

/// <summary>
/// A local neural voice (Piper) that speaks short phrases the recorded clips lack, in the clips' language.
/// Implementations must be safe to call from the voice thread and return null rather than throw when they
/// can't speak (process gone, timeout), so the caller can fall back.
/// </summary>
public interface IPhraseSynthesizer
{
    /// <summary>Language code of the voice ("de"), matched against a voice pack's <see cref="VoicePack.Language"/>.</summary>
    string Language { get; }

    /// <summary>For diagnostics: "Piper de_DE-thorsten-medium".</summary>
    string Name { get; }

    /// <summary>Clean speech for the text, or null when it can't be produced.</summary>
    MonoAudio? Synthesize(string text);
}

/// <summary>
/// What a phrase synthesizer says for the tokens the game's recordings can't. One word list per language;
/// only German exists, because the player's own side speaks German in the game's Luftwaffe packs.
/// </summary>
public static class SynthWords
{
    /// <summary>The word list for a language ("de", "de-DE"), or null when there is none.</summary>
    public static Func<SpeechToken, string?>? For(string? language) =>
        language is not null && language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? German : null;

    /// <summary>
    /// Plain German radio words. The game's German pilots say "Pirol eins, Jäger, drei Uhr ..." from their
    /// recordings; these fill the gaps in the same terse style. Period-correct Luftwaffe brevity code is not
    /// attempted (UNVERIFIED which terms crews used); the words are ordinary German a pilot would understand.
    /// </summary>
    public static string? German(SpeechToken t) => t.Word switch
    {
        SpeechWord.NoContacts => "nichts gesichtet",
        SpeechWord.WithUs => "du bist bei uns",
        SpeechWord.InFormation => "du bist in Formation",
        SpeechWord.Hedge => "vermutlich",
        SpeechWord.ContactSide => t.Value < 0 ? "Feind" : "eigene",
        SpeechWord.ContactType when t.Name is { } family => GermanType(family, plural: t.Value > 1),
        SpeechWord.RangeBand when t.Value > 0 && t.Value < 2 => "ein paar Kilometer",
        SpeechWord.OnMe => "bei mir",
        SpeechWord.CannotSee => "ich sehe dich nicht",
        SpeechWord.LastSeen => "zuletzt gesehen",
        SpeechWord.ContactAmount when t.Value >= 4 => "viele",
        SpeechWord.ContactAmount when t.Value >= 3 => "einige",
        _ => null,
    };

    /// <summary>German radio name for an <see cref="AircraftType.Family"/>; null for "aircraft" (no type made out).</summary>
    public static string? GermanType(string family, bool plural)
    {
        var type = AircraftTypes.ByFamily(family);
        if (type is null || type.Class == AircraftClass.Unknown)
            return null;
        if (GermanNames.TryGetValue(family, out var german))
            return plural ? german.Plural : german.Singular;
        return plural ? type.Plural : type.Family; // English names German pilots used as-is (Spitfire, Mustang)
    }

    /// <summary>
    /// German spellings where the English radio word would be read wrongly by a German voice, or where German
    /// usage differed. Numbers are spelt out, because the voice reads "109" as a quantity.
    /// </summary>
    private static readonly Dictionary<string, (string Singular, string Plural)> GermanNames = new(StringComparer.Ordinal)
    {
        ["Messerschmitt 110"] = ("Me hundertzehn", "Me hundertzehn"),
        ["Messerschmitt 410"] = ("Me vierhundertzehn", "Me vierhundertzehn"),
        ["Messerschmitt jet"] = ("Me zweihundertzweiundsechzig", "Me zweihundertzweiundsechzig"),
        ["Junkers transport"] = ("Ju zweiundfünfzig", "Ju zweiundfünfzig"),
        ["Arado jet"] = ("Arado", "Arados"),
        ["Yak"] = ("Jak", "Jaks"),
        ["Lavochkin"] = ("Lawotschkin", "Lawotschkins"),
        ["Chaika"] = ("Tschaika", "Tschaikas"),
        ["Sturmovik"] = ("Iljuschin", "Iljuschins"),
        ["Pe-2"] = ("Pe zwei", "Pe zwei"),
        ["U-2"] = ("U zwei", "U zwei"),
        ["Kittyhawk"] = ("Curtiss", "Curtiss"),
    };
}
