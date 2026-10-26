using Il2Radio.Core.Audio;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

/// <summary>Recorded-clip voices: token phrasing, voice-pack manifests, decoding, resampling and joining.</summary>
public class ClipVoiceTests
{
    /// <summary>The keys the IL-2 game packs provide (voices/il2-*.json).</summary>
    private static readonly HashSet<string> GameVocabulary =
    [
        .. Enumerable.Range(1, 20).Select(n => $"number.{n}"),
        "group", "pair",
        .. Enumerable.Range(1, 12).Select(h => $"clock.{h}"),
        .. new[] { "n", "ne", "e", "se", "s", "sw", "w", "nw" }.Select(d => $"compass.{d}"),
        "range.close", .. Enumerable.Range(1, 10).Select(k => $"range.{k}"), "range.far",
        "vertical.above", "vertical.below",
    ];

    private static IReadOnlyList<string>? Phrase(HashSet<string> vocabulary, params SpeechToken[] tokens) =>
        ClipPhraser.Phrase(tokens, vocabulary.Contains);

    [Fact]
    public void Flight_position_is_phrased_from_game_clips()
    {
        var keys = Phrase(GameVocabulary,
            new(SpeechWord.Speaker, 1), new(SpeechWord.Group, 5), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 18_000), new(SpeechWord.Vertical, 2400));

        Assert.Equal(["number.1", "group", "clock.3", "range.far", "vertical.above"], keys);
    }

    [Fact]
    public void Two_aircraft_are_a_pair_when_the_pack_has_it_otherwise_a_group()
    {
        Assert.Equal(["pair", "clock.6", "range.4"], Phrase(GameVocabulary, new(SpeechWord.Group, 2), new(SpeechWord.Clock, 6), new(SpeechWord.Range, 4_000)));
        Assert.Equal(["group", "clock.6", "range.4"], Phrase([.. GameVocabulary.Except(["pair"])], new(SpeechWord.Group, 2), new(SpeechWord.Clock, 6), new(SpeechWord.Range, 4_000)));
    }

    [Theory]
    [InlineData(800, "range.close")]
    [InlineData(1_600, "range.2")]
    [InlineData(9_700, "range.10")]
    [InlineData(10_600, "range.far")] // rounds to 11 km; the game's clips stop at 10
    [InlineData(35_000, "range.far")]
    public void Range_uses_the_most_precise_clip(double metres, string expected)
    {
        Assert.Equal([expected], Phrase(GameVocabulary, new SpeechToken(SpeechWord.Range, metres)));
    }

    [Fact]
    public void A_pack_with_exact_kilometres_is_preferred_over_far()
    {
        Assert.Equal(["range.18"], Phrase([.. GameVocabulary, "range.18"], new SpeechToken(SpeechWord.Range, 18_200)));
    }

    [Theory]
    [InlineData(110, "compass.e")]
    [InlineData(200, "compass.s")]
    [InlineData(315, "compass.nw")]
    [InlineData(350, "compass.n")]
    [InlineData(-45, "compass.nw")]
    public void Bearing_is_spoken_as_an_eight_point_compass_direction(double degrees, string expected)
    {
        Assert.Equal(expected, ClipPhraser.CompassKey(degrees));
    }

    [Fact]
    public void Decorations_without_clips_are_skipped()
    {
        // Game packs have no altitude, heading, separated, steer or "level" clips.
        var keys = Phrase(GameVocabulary,
            new(SpeechWord.Speaker, 3), new(SpeechWord.Separated, 0), new(SpeechWord.Steer, 110), new(SpeechWord.Range, 4_000),
            new(SpeechWord.Vertical, 100), new(SpeechWord.Altitude, 2_700), new(SpeechWord.Heading, 80));

        Assert.Equal(["number.3", "compass.e", "range.4", "compass.e"], keys); // a heading is the compass clip alone
    }

    [Fact]
    public void Decorations_are_spoken_when_a_pack_has_them()
    {
        HashSet<string> full = [.. GameVocabulary, "separated", "steer", "vertical.level", "altitude", "altitude.2700", "heading"];
        var keys = Phrase(full,
            new(SpeechWord.Speaker, 3), new(SpeechWord.Separated, 0), new(SpeechWord.Steer, 110), new(SpeechWord.Range, 4_000),
            new(SpeechWord.Vertical, 100), new(SpeechWord.Altitude, 2_740), new(SpeechWord.Heading, 80));

        Assert.Equal(["number.3", "separated", "steer", "compass.e", "range.4", "vertical.level", "altitude", "altitude.2700", "heading", "compass.e"], keys);
    }

    [Fact]
    public void A_missing_clip_drops_only_that_part()
    {
        Assert.Equal(["number.1", "range.5"], Phrase([.. GameVocabulary.Except(["clock.3"])], new(SpeechWord.Speaker, 1), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 5_000)));
        Assert.Equal(["number.1"], Phrase(GameVocabulary, new(SpeechWord.Speaker, 1), new(SpeechWord.WithUs))); // the game has no "with us"
        Assert.Null(Phrase([], new SpeechToken(SpeechWord.Speaker, 1))); // nothing to play: chat only
    }

    [Theory]
    [InlineData(SpeechWord.WithUs)]
    [InlineData(SpeechWord.InFormation)]
    [InlineData(SpeechWord.NoContacts)]
    public void A_whole_message_without_a_recording_is_the_acknowledgement(SpeechWord word)
    {
        Assert.Equal(["number.1", "ack"], Phrase([.. GameVocabulary, "ack"], new(SpeechWord.Speaker, 1), new(word)));
    }

    [Fact]
    public void An_uncounted_strength_is_a_group_and_an_enemy_down_is_target_destroyed()
    {
        Assert.Equal(["group", "class.fighters"], Phrase([.. GameVocabulary, "class.fighters"], new(SpeechWord.ContactAmount, 4), new(SpeechWord.ContactClass, -1)));
        Assert.Equal(["number.3", "destroyed"], Phrase([.. GameVocabulary, "destroyed"], new(SpeechWord.Speaker, 3), new(SpeechWord.Destroyed)));
    }

    [Fact]
    public void With_us_and_in_formation_fall_back_on_each_other()
    {
        Assert.Equal(["number.1", "information"], Phrase([.. GameVocabulary, "information"], new(SpeechWord.Speaker, 1), new(SpeechWord.WithUs)));
        Assert.Equal(["withus"], Phrase(["withus"], new SpeechToken(SpeechWord.InFormation)));
    }

    [Fact]
    public void Wav_round_trips_through_the_parser()
    {
        short[] pcm = [0, 16384, -16384, 32767];
        var audio = ClipAudio.ParseWav(RadioEffect.ToWav(pcm, 22_050));

        Assert.Equal(22_050, audio.SampleRate);
        Assert.Equal([0f, 0.5f, -0.5f, 32767 / 32768f], audio.Samples);
    }

    [Fact]
    public void Resampling_keeps_voice_band_tones_and_removes_what_would_alias()
    {
        static MonoAudio Tone(double hz) =>
            new(Enumerable.Range(0, 44_100).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / 44_100))).ToArray(), 44_100);
        static double Rms(MonoAudio a) => Math.Sqrt(a.Samples.Skip(1000).Take(10_000).Average(s => (double)s * s));

        var voice = ClipAudio.Resample(Tone(1000), 16_000);
        var hiss = ClipAudio.Resample(Tone(12_000), 16_000); // would fold back to 4 kHz without filtering

        Assert.Equal(16_000, voice.SampleRate);
        Assert.InRange(voice.Samples.Length, 15_990, 16_000);
        Assert.InRange(Rms(voice), 0.34, 0.37); // 0.5 / √2
        Assert.True(Rms(hiss) < 0.01, $"aliased tone RMS {Rms(hiss):F3}");
    }

    [Fact]
    public void Join_levels_clips_and_crossfades_the_seams()
    {
        var quiet = new MonoAudio(Enumerable.Repeat(0.05f, 1600).ToArray(), 16_000);
        var loud = new MonoAudio(Enumerable.Repeat(0.6f, 1600).ToArray(), 16_000);

        short[] joined = ClipAudio.Join([quiet, loud], 16_000, targetRms: 0.2, crossfadeMs: 25);

        Assert.Equal(1600 + 1600 - 400, joined.Length);
        Assert.InRange(joined[100] / 32767.0, 0.199, 0.201);
        Assert.InRange(joined[^100] / 32767.0, 0.199, 0.201);
    }

    [Fact]
    public void Voice_pack_resolves_clips_per_speaker_and_renders_an_utterance()
    {
        string root = Directory.CreateTempSubdirectory("il2radio-pack").FullName;
        string dir = Path.Combine(root, "mypack");
        try
        {
            foreach (string speaker in new[] { "a", "b" })
            {
                Directory.CreateDirectory(Path.Combine(dir, speaker));
                foreach (string clip in new[] { "one", "three", "far" })
                    File.WriteAllBytes(Path.Combine(dir, speaker, clip + ".wav"), RadioEffect.ToWav(Sine(0.3), 16_000));
            }

            File.Delete(Path.Combine(dir, "b", "far.wav"));
            string manifest = Path.Combine(dir, "voice.json");
            File.WriteAllText(manifest, """
                // comments are allowed
                {
                  "name": "Test pack",
                  "countries": [201],
                  "speakers": ["a", "b"],
                  "clips": {
                    "number.1": "{speaker}/one.wav",
                    "clock.3": "{speaker}/three.wav",
                    "range.far": "{speaker}/far.wav",
                  }
                }
                """);

            var pack = VoicePack.Load(manifest, il2Root: null)!;

            Assert.True(pack.SpeaksFor(201));
            Assert.False(pack.SpeaksFor(101));
            Assert.False(pack.FromGame);
            Assert.Equal([0, 1, 0, 1], new[] { 1, 2, 3, 4 }.Select(pack.SpeakerIndex));
            Assert.EndsWith(Path.Combine("a", "far.wav"), pack.ClipPath("range.far", 0));
            Assert.Null(pack.ClipPath("range.far", 1)); // file missing for speaker b
            Assert.Null(pack.ClipPath("clock.4", 0));   // not in the manifest

            SpeechToken[] tokens = [new(SpeechWord.Speaker, 1), new(SpeechWord.Clock, 3), new(SpeechWord.Range, 20_000)];
            var utterance = new ClipVoice(pack).Render(tokens, speakerId: 1, sampleRate: 16_000);
            Assert.NotNull(utterance);
            Assert.Equal(["number.1", "clock.3", "range.far"], utterance.Keys);
            Assert.Equal(3 * 4800 - 2 * 400, utterance.Samples.Length);
            Assert.DoesNotContain("range.far", new ClipVoice(pack).Render(tokens, speakerId: 2, sampleRate: 16_000)?.Keys ?? []); // speaker b can't say "far"

            Assert.Equal("Test pack", Assert.Single(VoicePack.Discover([root, root], null)).Name); // finds mypack/voice.json once
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Game_pack_needs_the_il2_install_and_reads_clips_from_it()
    {
        string dir = Directory.CreateTempSubdirectory("il2radio-game").FullName;
        try
        {
            string manifest = Path.Combine(dir, "il2-test.json");
            File.WriteAllText(manifest, """{ "root": "{il2}/data/Audio/radio/ger", "speakers": ["actor51"], "preprocessed": true, "clips": { "clock.3": "{speaker}/varDir/03clock.ogg" } }""");

            Assert.Null(VoicePack.Load(manifest, il2Root: null));
            var problems = new List<string>();
            Assert.Empty(VoicePack.Discover([dir], null, problems));
            Assert.Contains("needs the IL-2 install", Assert.Single(problems));

            var pack = VoicePack.Load(manifest, il2Root: @"C:\Games\IL-2")!;
            Assert.True(pack.FromGame);
            Assert.True(pack.PreProcessed);
            Assert.Equal(Path.GetFullPath(@"C:\Games\IL-2\data\Audio\radio\ger"), pack.Root);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static short[] Sine(double seconds) =>
        Enumerable.Range(0, (int)(16_000 * seconds)).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16_000))).ToArray();
}
