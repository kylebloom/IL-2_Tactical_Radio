using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Core.Tests;

/// <summary>
/// The optional German phrase voice (Piper): which parts of a reply it speaks, how it joins the game's
/// recordings, the fallbacks when it is missing or fails, the consent gate before any download, and
/// download checking. No real Piper is involved: a fake synthesizer and in-memory downloads stand in.
/// </summary>
public class PiperVoiceTests
{
    /// <summary>The keys the German game pack provides (voices/il2-ger.json): no type names, no "no contacts".</summary>
    private static readonly HashSet<string> GermanGamePack =
    [
        "callsign.8", .. Enumerable.Range(1, 20).Select(n => $"number.{n}"), "group", "pair",
        "class.fighter", "class.fighters", "class.bomber", "class.bombers",
        .. Enumerable.Range(1, 12).Select(h => $"clock.{h}"),
        .. new[] { "n", "ne", "e", "se", "s", "sw", "w", "nw" }.Select(d => $"compass.{d}"),
        "range.close", .. Enumerable.Range(1, 10).Select(k => $"range.{k}"), "range.far", "vertical.above", "vertical.below",
    ];

    private static IReadOnlyList<string>? Segments(Func<SpeechToken, string?>? words, params SpeechToken[] tokens) =>
        ClipPhraser.PhraseSegments(tokens, GermanGamePack.Contains, words)?.Select(s => s.ToString()).ToList();

    private static readonly SpeechToken[] PairOfYaks =
    [
        new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.ContactCount, 2), new(SpeechWord.ContactClass, -(int)AircraftClass.Fighter),
        new(SpeechWord.ContactType, 2, "Yak"), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 3_700), new(SpeechWord.Vertical, 800),
    ];

    [Fact]
    public void The_type_name_is_spoken_by_the_synthesizer_in_place_of_the_class_word()
    {
        Assert.Equal(["callsign.8", "number.1", "pair", "\"Jaks\"", "clock.3", "range.4", "vertical.above"], Segments(SynthWords.German, PairOfYaks));
    }

    [Fact]
    public void Without_a_synthesizer_the_class_word_stands_in_as_before()
    {
        Assert.Equal(["callsign.8", "number.1", "pair", "class.fighters", "clock.3", "range.4", "vertical.above"], Segments(null, PairOfYaks));
    }

    [Fact]
    public void No_contacts_is_spoken_by_the_synthesizer_after_the_recorded_callsign()
    {
        SpeechToken[] tokens = [new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.NoContacts)];

        Assert.Equal(["callsign.8", "number.1", "\"nichts gesichtet\""], Segments(SynthWords.German, tokens));
        Assert.True(ClipPhraser.PhraseSegments(tokens, GermanGamePack.Contains, SynthWords.German)![^1].Required);
        Assert.Null(Segments(null, tokens)); // as before: the whole reply goes to text-to-speech
    }

    [Fact]
    public void Neighbouring_synthesized_words_become_one_phrase()
    {
        var segments = Segments(SynthWords.German,
            new(SpeechWord.Speaker, 2), new(SpeechWord.ContactCount, 1), new(SpeechWord.Hedge), new(SpeechWord.ContactType, 1, "Messerschmitt"),
            new(SpeechWord.Clock, 11), new(SpeechWord.Range, 2_300));

        Assert.Equal(["number.2", "\"vermutlich Messerschmitt\"", "clock.11", "range.2"], segments);
    }

    [Fact]
    public void A_sure_identification_says_the_side_after_the_type()
    {
        var segments = Segments(SynthWords.German,
            new(SpeechWord.Speaker, 1), new(SpeechWord.ContactCount, 4), new(SpeechWord.ContactClass, -(int)AircraftClass.Bomber),
            new(SpeechWord.ContactType, 4, "Sturmovik"), new(SpeechWord.ContactSide, -1), new(SpeechWord.Clock, 9), new(SpeechWord.Range, 1_200));

        Assert.Equal(["number.1", "group", "\"Iljuschins Feind\"", "clock.9", "range.close"], segments);
    }

    [Fact]
    public void Numbers_the_clips_say_coarsely_are_never_handed_to_the_synthesizer()
    {
        // 18 km, three-digit steer, altitude and heading stay as the game's clips can say them (or are left out).
        var segments = Segments(SynthWords.German,
            new(SpeechWord.Speaker, 1), new(SpeechWord.Steer, 110), new(SpeechWord.Range, 18_000), new(SpeechWord.Altitude, 2_700), new(SpeechWord.Heading, 80));

        Assert.Equal(["number.1", "compass.e", "range.far"], segments);
    }

    [Fact]
    public void A_course_stays_on_the_chat_and_is_not_spoken_by_the_phrase_voice()
    {
        Assert.Null(SynthWords.German(new(SpeechWord.Course, 185)));
        Assert.Null(SynthWords.German(new(SpeechWord.Heading, 180)));

        Assert.Equal(["number.1"],
            Segments(SynthWords.German, new(SpeechWord.Speaker, 1), new(SpeechWord.Course, 185)));
        Assert.Null(Segments(SynthWords.German, new SpeechToken(SpeechWord.Course, 180)));
    }

    [Fact]
    public void Contact_type_without_a_name_says_nothing()
    {
        Assert.Null(SynthWords.German(new(SpeechWord.ContactType, 2)));
        Assert.Equal(["number.1", "class.fighters", "clock.3", "range.2"],
            Segments(SynthWords.German, new(SpeechWord.Speaker, 1), new(SpeechWord.ContactClass, -1), new(SpeechWord.ContactType, 2), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 2_200)));
    }

    [Theory]
    [InlineData("Yak", 1, "Jak")]
    [InlineData("Yak", 3, "Jaks")]
    [InlineData("Messerschmitt", 2, "Messerschmitts")]
    [InlineData("Sturmovik", 1, "Iljuschin")]
    [InlineData("Spitfire", 2, "Spitfires")]
    [InlineData("aircraft", 2, null)]
    [InlineData("Nonexistent", 1, null)]
    public void Type_names_in_german(string family, int count, string? expected) =>
        Assert.Equal(expected, SynthWords.German(new SpeechToken(SpeechWord.ContactType, count, family)));

    [Fact]
    public void Every_known_type_has_a_german_name() =>
        Assert.All(new[] { "Bf 109", "Bf 110", "Me 262", "Fw 190", "Ju 87", "Ju 52", "Yak-1", "La-5", "I-16", "I-153", "Il-2", "Pe-2", "U-2", "P-40", "Spitfire" },
            model => Assert.False(string.IsNullOrWhiteSpace(SynthWords.GermanType(AircraftTypes.Classify(model).Family, plural: true))));

    [Fact]
    public void Only_german_has_synthesizer_words()
    {
        Assert.NotNull(SynthWords.For("de"));
        Assert.NotNull(SynthWords.For("de-DE"));
        Assert.Null(SynthWords.For("ru"));
        Assert.Null(SynthWords.For(""));
    }

    // ---- Rendering with a fake synthesizer ----

    private sealed class FakeSynth(string language = "de", bool fail = false) : IPhraseSynthesizer
    {
        public List<string> Spoken { get; } = [];
        public string Language => language;
        public string Name => "Fake";

        public MonoAudio? Synthesize(string text)
        {
            Spoken.Add(text);
            if (fail)
                return null;
            // 50 ms silence, 300 ms tone, 50 ms silence at 22.05 kHz, as Piper pads its output.
            var samples = new float[22_050 * 400 / 1000];
            for (int i = 1102; i < 1102 + 6615; i++)
                samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 22_050));
            return new MonoAudio(samples, 22_050);
        }
    }

    private static (ClipVoice Voice, string Root) Pack(string language, bool preProcessed = true)
    {
        string root = Directory.CreateTempSubdirectory("il2radio-piper").FullName;
        foreach (string clip in new[] { "one", "pair", "fighters", "three", "four", "above", "cs8" })
            File.WriteAllBytes(Path.Combine(root, clip + ".wav"), RadioEffect.ToWav(Enumerable.Range(0, 4800).Select(i => (short)(8000 * Math.Sin(i * 0.17))).ToArray(), 16_000));
        string manifest = Path.Combine(root, "voice.json");
        File.WriteAllText(manifest, $$"""
            { "name": "Test {{language}}", "language": "{{language}}", "preprocessed": {{(preProcessed ? "true" : "false")}},
              "clips": { "callsign.8": "cs8.wav", "number.1": "one.wav", "pair": "pair.wav", "class.fighters": "fighters.wav",
                         "clock.3": "three.wav", "range.4": "four.wav", "vertical.above": "above.wav" } }
            """);
        return (new ClipVoice(VoicePack.Load(manifest, null)!), root);
    }

    [Fact]
    public void A_german_pack_with_a_synthesizer_renders_the_type_between_recorded_clips()
    {
        var (voice, root) = Pack("de");
        try
        {
            var synth = new FakeSynth();
            var utterance = voice.Render(PairOfYaks, 1, 16_000, synth);

            Assert.NotNull(utterance);
            Assert.Equal(["callsign.8", "number.1", "pair", "\"Jaks\"", "clock.3", "range.4", "vertical.above"], utterance.Keys);
            Assert.Equal(["Jaks"], synth.Spoken);
            Assert.Equal("(Fake)", utterance.Files[3]);
            // Six 0.3 s clips and the tone trimmed of its padding (0.34 s with 20 ms kept each side), less six 25 ms crossfades: 1.99 s.
            Assert.InRange(utterance.Samples.Length / 16_000.0, 1.97, 2.01);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_synthesizer_in_another_language_is_ignored()
    {
        var (voice, root) = Pack("ru");
        try
        {
            var synth = new FakeSynth("de");
            var utterance = voice.Render(PairOfYaks, 1, 16_000, synth);

            Assert.Equal(["callsign.8", "number.1", "pair", "class.fighters", "clock.3", "range.4", "vertical.above"], utterance!.Keys);
            Assert.Empty(synth.Spoken);
            Assert.False(voice.CanUse(synth));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_failing_synthesizer_falls_back_to_the_recordings_alone()
    {
        var (voice, root) = Pack("de");
        try
        {
            var synth = new FakeSynth(fail: true);

            // Optional type: the class word comes back.
            Assert.Equal(["callsign.8", "number.1", "pair", "class.fighters", "clock.3", "range.4", "vertical.above"], voice.Render(PairOfYaks, 1, 16_000, synth)!.Keys);
            // Required "no contacts": nothing the clips can say alone, so text-to-speech takes the reply.
            Assert.Null(voice.Render([new(SpeechWord.Callsign, 8), new(SpeechWord.Speaker, 1), new(SpeechWord.NoContacts)], 1, 16_000, synth));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Trimming_removes_the_synthesizers_padding()
    {
        var audio = new FakeSynth().Synthesize("x")!;
        var trimmed = ClipAudio.TrimSilence(audio, padMs: 0);

        Assert.InRange(trimmed.Samples.Length, 6600, 6616);
        Assert.Empty(ClipAudio.TrimSilence(new MonoAudio(new float[100], 16_000)).Samples);
    }

    [Fact]
    public void Colouring_a_fragment_adds_no_squelch_or_click()
    {
        short[] silence = new short[1600];
        var plain = RadioEffect.Apply(silence, 16_000, new RadioEffectSettings());
        var coloured = RadioEffect.Apply(silence, 16_000, RadioEffectSettings.LikeGameRecordings);

        Assert.Equal(1600, coloured.Length);
        Assert.True(plain.Length > 1600);
        Assert.True(Math.Abs(coloured[^1] / 32767.0) < 0.2, "no squelch click at the end");
    }

    // ---- Settings ----

    [Fact]
    public void Piper_is_unanswered_by_default_and_its_settings_are_validated_by_name()
    {
        var defaults = new AppSettings();
        Assert.Null(defaults.Piper.Enabled);
        Assert.Equal("de_DE-thorsten-medium", defaults.Piper.Voice);
        Assert.Contains("\"enabled\": null", defaults.ToJson());
        Assert.Null(AppSettings.Parse(defaults.ToJson()).Settings.Piper.Enabled); // the default round-trips as "not answered"
        Assert.Null(AppSettings.Parse("{}").Settings.Piper.Enabled); // an older settings.json without "piper"

        var (settings, problems) = AppSettings.Parse("""{ "piper": { "enabled": true, "voice": "de_DE-karlsson-low", "lengthScale": 1.1 } }""");
        Assert.Empty(problems);
        Assert.True(settings.Piper.Enabled);
        Assert.False(AppSettings.Parse("""{ "piper": { "enabled": false } }""").Settings.Piper.Enabled);

        Assert.Contains(AppSettings.Parse("""{ "piper": { "voice": "en_US-lessac-medium" } }""").Problems, p => p.StartsWith("piper.voice"));
        Assert.Contains(AppSettings.Parse("""{ "piper": { "lengthScale": 5 } }""").Problems, p => p.StartsWith("piper.lengthScale"));
        Assert.Contains(AppSettings.Parse("""{ "piper": { "folder": " " } }""").Problems, p => p.StartsWith("piper.folder"));
        Assert.NotEmpty(AppSettings.Parse("""{ "piper": { "enable": true } }""").Problems); // misspelt name
    }

    // ---- Consent gate ----

    private static Func<Uri, CancellationToken, Task<Stream>> NoNetwork(List<Uri> opened) => (url, _) =>
    {
        opened.Add(url);
        throw new InvalidOperationException("the network must not be reached");
    };

    /// <summary>A question that records how often it was asked and gives a fixed answer.</summary>
    private sealed class Asker(PiperAnswer answer)
    {
        public int Times { get; private set; }
        public IReadOnlyList<string> Shown { get; private set; } = [];

        public PiperAnswer Ask(IReadOnlyList<string> lines)
        {
            Times++;
            Shown = lines;
            return answer;
        }
    }

    [Fact]
    public async Task No_is_remembered_and_never_asked_again()
    {
        string folder = TempFolder();
        var opened = new List<Uri>();
        var ask = new Asker(PiperAnswer.No);

        var first = await PiperSetup.PrepareAsync(new PiperSettings(), folder, null, ask.Ask, NoNetwork(opened));
        Assert.Equal(PiperSetupStatus.NotInstalled, first.Status);
        Assert.False(first.Remember); // saved as piper.enabled = false
        Assert.Contains("Settings", first.Message); // how to change his mind

        // Next start, with the saved answer: no question at all.
        var second = await PiperSetup.PrepareAsync(new PiperSettings { Enabled = first.Remember }, folder, null, ask.Ask, NoNetwork(opened));
        Assert.Equal(PiperSetupStatus.Off, second.Status);
        Assert.Null(second.Remember);
        Assert.Equal(1, ask.Times);
        Assert.Empty(opened);
        Assert.False(Directory.Exists(folder)); // nothing created either
    }

    [Fact]
    public async Task Later_is_not_remembered_so_the_question_comes_back()
    {
        string folder = TempFolder();
        var opened = new List<Uri>();
        var ask = new Asker(PiperAnswer.Later);

        var first = await PiperSetup.PrepareAsync(new PiperSettings(), folder, null, ask.Ask, NoNetwork(opened));
        var second = await PiperSetup.PrepareAsync(new PiperSettings(), folder, null, ask.Ask, NoNetwork(opened));

        Assert.Equal(PiperSetupStatus.NotInstalled, first.Status);
        Assert.Null(first.Remember);
        Assert.Contains("asked again", first.Message);
        Assert.Equal(2, ask.Times);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task The_question_is_short_and_says_what_from_where_and_how_big()
    {
        string folder = TempFolder();
        var ask = new Asker(PiperAnswer.Later);
        await PiperSetup.PrepareAsync(new PiperSettings(), folder, null, ask.Ask, NoNetwork([]));

        string text = string.Join("\n", ask.Shown);
        Assert.InRange(ask.Shown.Count, 3, 7);
        Assert.Contains("aircraft names", text);
        Assert.Contains("85.7 MB", text); // 22.5 MB engine + 63.2 MB voice + 4.8 KB settings
        Assert.Contains("github.com", text);
        Assert.Contains("huggingface.co", text);
        Assert.Contains("MIT", text);
        Assert.Contains("CC0", text);
        Assert.Contains(folder, text);
    }

    [Fact]
    public async Task Turned_on_but_missing_still_asks_before_downloading()
    {
        var ask = new Asker(PiperAnswer.Later);
        var opened = new List<Uri>();
        var result = await PiperSetup.PrepareAsync(new PiperSettings { Enabled = true }, TempFolder(), null, ask.Ask, NoNetwork(opened));

        Assert.Equal(1, ask.Times);
        Assert.Equal(PiperSetupStatus.NotInstalled, result.Status);
        Assert.Null(result.Remember); // still on; asked again next start
        Assert.Empty(opened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task Without_a_console_or_window_nobody_is_asked_and_nothing_is_downloaded(bool? enabled)
    {
        var opened = new List<Uri>();
        var result = await PiperSetup.PrepareAsync(new PiperSettings { Enabled = enabled }, TempFolder(), null, ask: null, NoNetwork(opened));

        Assert.Equal(PiperSetupStatus.NotInstalled, result.Status);
        Assert.Null(result.Remember);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task The_store_is_never_inside_the_il2_folder()
    {
        string il2 = TempFolder();
        var opened = new List<Uri>();
        var ask = new Asker(PiperAnswer.Yes);
        var result = await PiperSetup.PrepareAsync(new PiperSettings { Enabled = true }, Path.Combine(il2, "data", "piper"), il2, ask.Ask, NoNetwork(opened));

        Assert.Equal(PiperSetupStatus.Failed, result.Status);
        Assert.Equal(0, ask.Times);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task A_network_failure_after_yes_is_explained_plainly_and_offered_again()
    {
        string folder = TempFolder();
        var log = new List<string>();
        try
        {
            var result = await PiperSetup.PrepareAsync(new PiperSettings(), folder, null, new Asker(PiperAnswer.Yes).Ask,
                (_, _) => throw new HttpRequestException("No such host is known. (github.com:443)"), log: log.Add);

            Assert.Equal(PiperSetupStatus.Failed, result.Status);
            Assert.True(result.Remember); // the yes stands: the next start offers the download again
            Assert.Contains("no internet connection", result.Message);
            Assert.Contains("works without it", result.Message);
            Assert.Contains("again", result.Message);
            Assert.DoesNotContain("github.com:443", result.Message); // the technical detail goes to the log
            Assert.Contains(log, l => l.Contains("github.com:443"));
            Assert.Null(PiperInstaller.Find(folder, PiperCatalog.Find(PiperCatalog.DefaultVoice)!));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_download_is_explained_without_jargon()
    {
        string message = PiperSetup.FriendlyError(new InvalidDataException("x.onnx: SHA-256 abc doesn't match the published def"), @"C:\Radio\piper");

        Assert.Contains("damaged", message);
        Assert.DoesNotContain("SHA-256", message);
        Assert.Contains("works without it", message);
    }

    [Theory]
    [InlineData("y", PiperAnswer.Yes)]
    [InlineData(" YES ", PiperAnswer.Yes)]
    [InlineData("n", PiperAnswer.No)]
    [InlineData("No", PiperAnswer.No)]
    [InlineData("l", PiperAnswer.Later)]
    [InlineData("later", PiperAnswer.Later)]
    [InlineData("", PiperAnswer.Later)]   // just Enter
    [InlineData(null, PiperAnswer.Later)] // the console closed: never yes
    [InlineData("maybe", null)]           // asked again
    public void Typed_answers(string? input, PiperAnswer? expected) => Assert.Equal(expected, PiperSetup.ParseAnswer(input));

    [Fact]
    public void The_answer_is_saved_into_settings_json_keeping_everything_else()
    {
        string json = new AppSettings { Volume = 0.55, Chat = new ChatSettings { Output = "window" } }.ToJson();

        string saved = AppSettings.WithPiperEnabled(json, false);
        var (settings, problems) = AppSettings.Parse(saved);

        Assert.Empty(problems);
        Assert.False(settings.Piper.Enabled);
        Assert.Equal(0.55, settings.Volume);
        Assert.Equal("window", settings.Chat.Output);
        Assert.Equal("de_DE-thorsten-medium", settings.Piper.Voice);

        Assert.True(AppSettings.Parse(AppSettings.WithPiperEnabled(saved, true)).Settings.Piper.Enabled);
    }

    [Fact]
    public void Saving_the_answer_works_on_an_older_file_without_piper_and_with_comments()
    {
        string old = """
            // written by hand
            { "volume": 0.7, "chat": { "output": "rtss", }, }
            """;

        var (settings, problems) = AppSettings.Parse(AppSettings.WithPiperEnabled(old, true));

        Assert.Empty(problems);
        Assert.True(settings.Piper.Enabled);
        Assert.Equal(0.7, settings.Volume);
        Assert.True(AppSettings.Parse(AppSettings.WithPiperEnabled("", false)).Settings.Piper.Enabled is false);
    }

    [Fact]
    public async Task Remove_deletes_what_the_installer_made_and_nothing_else()
    {
        string folder = TempFolder();
        try
        {
            var f = Files();
            await PiperInstaller.InstallAsync(folder, f.Voice, PiperInstaller.Missing(folder, f.Voice, f.Runtime), Serve(f.Served));
            Assert.NotNull(PiperInstaller.Find(folder, f.Voice));

            File.WriteAllText(Path.Combine(folder, "my notes.txt"), "keep me");

            Assert.True(PiperInstaller.Remove(folder));
            Assert.Null(PiperInstaller.Find(folder, f.Voice));
            Assert.True(File.Exists(Path.Combine(folder, "my notes.txt"))); // not the installer's, so kept (and the folder with it)
            Assert.False(Directory.Exists(PiperInstaller.RuntimeFolder(folder)));

            File.Delete(Path.Combine(folder, "my notes.txt"));
            Assert.False(PiperInstaller.Remove(folder));
            Assert.False(Directory.Exists(folder)); // an empty folder goes too
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ---- Download checking and layout ----

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "il2radio-piper-" + Guid.NewGuid().ToString("N"));

    private static PinnedFile Pin(string name, byte[] bytes, bool gitBlob = false) =>
        new(name, new Uri("https://example.test/" + name), bytes.Length,
            Sha256: gitBlob ? null : Convert.ToHexStringLower(SHA256.HashData(bytes)),
            GitBlobSha1: gitBlob ? Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes])) : null);

    private static byte[] RuntimeZip(bool withEspeak = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("piper/piper.exe").Open()))
                w.Write("not really an exe");
            if (withEspeak)
                using (var w = new StreamWriter(zip.CreateEntry("piper/espeak-ng-data/phontab").Open()))
                    w.Write("x");
        }

        return ms.ToArray();
    }

    private sealed record Fixture(PiperVoice Voice, PinnedFile Runtime, Dictionary<Uri, byte[]> Served);

    private static Fixture Files(byte[]? runtimeZip = null, byte[]? servedModel = null)
    {
        byte[] model = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        byte[] config = Encoding.UTF8.GetBytes("""{ "audio": { "sample_rate": 22050 } }""");
        byte[] zip = runtimeZip ?? RuntimeZip();
        var voice = new PiperVoice("de_DE-test-medium", "de", "medium", 22_050, "CC0", Pin("de_DE-test-medium.onnx", model), Pin("de_DE-test-medium.onnx.json", config, gitBlob: true));
        var runtime = Pin("piper_windows_amd64.zip", zip) with { Sha256 = null }; // GitHub publishes no checksum: size only
        return new Fixture(voice, runtime, new() { [voice.Model.Url] = servedModel ?? model, [voice.Config.Url] = config, [runtime.Url] = zip });
    }

    private static Func<Uri, CancellationToken, Task<Stream>> Serve(Dictionary<Uri, byte[]> files) =>
        (url, _) => Task.FromResult<Stream>(new MemoryStream(files[url]));

    [Fact]
    public async Task Verified_downloads_are_installed_and_found()
    {
        string folder = TempFolder();
        try
        {
            var f = Files();
            var missing = PiperInstaller.Missing(folder, f.Voice, f.Runtime);
            Assert.Equal([f.Runtime, f.Voice.Model, f.Voice.Config], missing);

            var log = new List<string>();
            var install = await PiperInstaller.InstallAsync(folder, f.Voice, missing, Serve(f.Served), log: log.Add);

            Assert.EndsWith(Path.Combine("runtime", "piper", "piper.exe"), install.Executable);
            Assert.True(File.Exists(install.Model));
            Assert.True(File.Exists(install.Config));
            Assert.False(Directory.Exists(Path.Combine(folder, "downloads"))); // temporary files gone
            Assert.Contains(log, l => l.Contains("no published checksum")); // the engine was checked by size only, and said so
            Assert.Empty(PiperInstaller.Missing(folder, f.Voice, f.Runtime));
            Assert.NotNull(PiperInstaller.Find(folder, f.Voice));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_tampered_model_is_rejected_and_nothing_of_it_is_kept()
    {
        string folder = TempFolder();
        try
        {
            byte[] tampered = Enumerable.Range(0, 3000).Select(i => (byte)(i + 1)).ToArray(); // right size, wrong bytes
            var f = Files(servedModel: tampered);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                PiperInstaller.InstallAsync(folder, f.Voice, [f.Voice.Model], Serve(f.Served)));

            Assert.Contains("SHA-256", ex.Message);
            Assert.Empty(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_short_or_oversized_download_is_rejected()
    {
        string folder = TempFolder();
        try
        {
            var f = Files();
            f.Served[f.Voice.Config.Url] = f.Served[f.Voice.Config.Url][..10];
            Assert.Contains("expected", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                PiperInstaller.InstallAsync(folder, f.Voice, [f.Voice.Config], Serve(f.Served)))).Message);

            f.Served[f.Voice.Config.Url] = new byte[f.Voice.Config.Size + 1];
            Assert.Contains("more data", (await Assert.ThrowsAsync<InvalidDataException>(() =>
                PiperInstaller.InstallAsync(folder, f.Voice, [f.Voice.Config], Serve(f.Served)))).Message);
            Assert.False(File.Exists(Path.Combine(PiperInstaller.VoicesFolder(folder), f.Voice.Config.FileName)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_runtime_zip_without_espeak_data_is_rejected()
    {
        string folder = TempFolder();
        try
        {
            var f = Files(runtimeZip: RuntimeZip(withEspeak: false));

            await Assert.ThrowsAsync<InvalidDataException>(() => PiperInstaller.InstallAsync(folder, f.Voice, [f.Runtime], Serve(f.Served)));

            Assert.Null(PiperInstaller.FindExecutable(PiperInstaller.RuntimeFolder(folder)));
            Assert.False(Directory.Exists(PiperInstaller.RuntimeFolder(folder) + ".new"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_catalog_pins_every_file_and_the_default_voice_exists()
    {
        var voice = PiperCatalog.Find(PiperCatalog.DefaultVoice)!;
        Assert.Equal(63_201_294, voice.Model.Size);
        Assert.All(PiperCatalog.Voices, v =>
        {
            Assert.Equal("de", v.Language);
            Assert.NotNull(v.Model.Sha256);
            Assert.NotNull(v.Config.GitBlobSha1);
            Assert.Equal("https", v.Model.Url.Scheme);
            Assert.Equal(v.Name + ".onnx", v.Model.FileName);
        });
        Assert.Equal("https", PiperCatalog.Runtime.Url.Scheme);
    }

    [Fact]
    public async Task A_non_https_download_is_refused_without_opening_it()
    {
        string folder = TempFolder();
        try
        {
            var voice = PiperCatalog.Find(PiperCatalog.DefaultVoice)!;
            var http = voice.Model with { Url = new Uri("http://example.test/model.onnx") };
            bool opened = false;

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                PiperInstaller.InstallAsync(folder, voice, [http], (_, _) =>
                {
                    opened = true;
                    return Task.FromResult<Stream>(Stream.Null);
                }));

            Assert.Contains("HTTPS", ex.Message);
            Assert.False(opened);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
