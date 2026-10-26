using Il2Radio.Core.Input;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Core.Tests;

/// <summary>The radio app's settings file: defaults, partial files, and every mistake reported by name.</summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_round_trip_and_are_valid()
    {
        var defaults = new AppSettings();
        var (parsed, problems) = AppSettings.Parse(defaults.ToJson());

        Assert.Empty(problems);
        Assert.Equal(defaults.ToJson(), parsed.ToJson());
        Assert.Equal("rtss", parsed.Chat.Output);
        Assert.Equal("Num1", parsed.Keys.FriendlyPositions);
        Assert.Equal("pilot", parsed.Realism);
        Assert.Equal(RadioRealism.Pilot, parsed.RealismMode);
    }

    [Fact]
    public void Written_defaults_name_every_setting_including_unset_ones()
    {
        string json = new AppSettings().ToJson();

        Assert.Contains("\"il2Path\": null", json);
        Assert.Contains("\"rtssZoom\": null", json);
        Assert.Contains("\"recordingReminderSeconds\": 60", json);
        Assert.Contains("\"realism\": \"pilot\"", json);
    }

    [Fact]
    public void A_partial_file_keeps_the_other_defaults()
    {
        var (settings, problems) = AppSettings.Parse("""
            // comments and trailing commas are fine
            {
              "volume": 0.5,
              "chat": { "output": "overlay", },
              "keys": { "quit": "F12" },
            }
            """);

        Assert.Empty(problems);
        Assert.Equal(0.5, settings.Volume);
        Assert.Equal("overlay", settings.Chat.Output);
        Assert.Equal("left-middle", settings.Chat.RtssPosition);
        Assert.Equal("F12", settings.Keys.Quit);
        Assert.Equal("Num1", settings.Keys.FriendlyPositions);
        Assert.True(settings.RadioWait);
        Assert.Equal("pilot", settings.Realism);
    }

    [Fact]
    public void A_misspelt_setting_is_reported_not_ignored()
    {
        var (_, problems) = AppSettings.Parse("""{ "volum": 0.5 }""");

        Assert.Contains("volum", Assert.Single(problems));
    }

    [Fact]
    public void Broken_json_is_reported()
    {
        var (_, problems) = AppSettings.Parse("""{ "volume": }""");

        Assert.Single(problems);
    }

    [Theory]
    [InlineData("""{ "volume": 1.5 }""", "volume")]
    [InlineData("""{ "radioWaitRatio": 1 }""", "radioWaitRatio")]
    [InlineData("""{ "recordingReminderSeconds": -1 }""", "recordingReminderSeconds")]
    [InlineData("""{ "voice": " " }""", "voice")]
    [InlineData("""{ "realism": "radar" }""", "realism")]
    [InlineData("""{ "chat": { "output": "screen" } }""", "chat.output")]
    [InlineData("""{ "chat": { "rtssPosition": "centre" } }""", "chat.rtssPosition")]
    [InlineData("""{ "chat": { "rtssColumns": 10 } }""", "chat.rtssColumns")]
    [InlineData("""{ "chat": { "rtssZoom": 0 } }""", "chat.rtssZoom")]
    [InlineData("""{ "chat": { "rtssCell": [9] } }""", "chat.rtssCell")]
    [InlineData("""{ "chat": { "windowArea": [0.5, 0.5, 0.6, 0.2] } }""", "chat.windowArea")]
    [InlineData("""{ "chat": null }""", "chat")]
    [InlineData("""{ "keys": { "menu": "Numpad zero" } }""", "keys.menu")]
    [InlineData("""{ "keys": { "quit": "Num1" } }""", "keys.quit")]
    public void Bad_values_are_reported_by_setting_name(string json, string setting)
    {
        var (_, problems) = AppSettings.Parse(json);

        Assert.StartsWith(setting, Assert.Single(problems));
    }

    [Fact]
    public void Duplicate_keys_name_both_actions()
    {
        var (_, problems) = AppSettings.Parse("""{ "keys": { "quit": "num1" } }""");

        Assert.Equal("keys.quit \"num1\": already used for keys.friendlyPositions", Assert.Single(problems));
    }

    [Fact]
    public void An_empty_key_leaves_the_action_unbound()
    {
        var (_, problems) = AppSettings.Parse("""{ "keys": { "quit": "", "older": null } }""");

        Assert.Empty(problems);
    }

    [Fact]
    public void Rtss_position_and_margin_make_one_placement()
    {
        var chat = new ChatSettings { RtssPosition = "bottom-right", RtssMargin = 48 };

        Assert.Equal(new RtssPlacement(RtssAnchor.BottomRight, 48), chat.Placement());
        Assert.Equal(new RtssPlacement(RtssAnchor.Pixel, 48, 40, 300), (chat with { RtssPosition = "40,300" }).Placement());
    }

    [Fact]
    public void A_missing_file_is_created_with_the_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "il2radio-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var (_, created, problems) = AppSettings.LoadOrCreate(path);
            var (_, createdAgain, problemsAgain) = AppSettings.LoadOrCreate(path);

            Assert.True(created);
            Assert.False(createdAgain);
            Assert.Empty(problems);
            Assert.Empty(problemsAgain);
            Assert.Contains("\"volume\": 0.8", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Key names in the settings file and their Windows virtual-key codes.</summary>
public class VirtualKeysTests
{
    [Theory]
    [InlineData("Num1", 0x61)]
    [InlineData("num0", 0x60)]
    [InlineData("NumPad9", 0x69)]
    [InlineData("Num.", 0x6E)]
    [InlineData("Decimal", 0x6E)]
    [InlineData("Num+", 0x6B)]
    [InlineData("F9", 0x78)]
    [InlineData("F24", 0x87)]
    [InlineData("k", 0x4B)]
    [InlineData("7", 0x37)]
    [InlineData(" PageUp ", 0x21)]
    public void Names_parse(string name, int code) => Assert.Equal(code, VirtualKeys.Parse(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Numpad one")]
    [InlineData("F25")]
    public void Unknown_names_do_not(string? name) => Assert.Null(VirtualKeys.Parse(name));

    [Fact]
    public void Each_key_has_one_label_whatever_name_it_was_given()
    {
        Assert.Equal("Num.", VirtualKeys.Label(VirtualKeys.Parse("Decimal")!.Value));
        Assert.Equal("Num3", VirtualKeys.Label(VirtualKeys.Parse("numpad3")!.Value));
        Assert.Equal("Delete", VirtualKeys.Label(VirtualKeys.Parse("del")!.Value));
    }

    [Fact]
    public void Only_numpad_digits_and_decimal_need_num_lock()
    {
        Assert.True(VirtualKeys.NeedsNumLock(VirtualKeys.Parse("Num5")!.Value));
        Assert.True(VirtualKeys.NeedsNumLock(VirtualKeys.Parse("Num.")!.Value));
        Assert.False(VirtualKeys.NeedsNumLock(VirtualKeys.Parse("Num+")!.Value));
        Assert.False(VirtualKeys.NeedsNumLock(VirtualKeys.Parse("F9")!.Value));
    }
}
