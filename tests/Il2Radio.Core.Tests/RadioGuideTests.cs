using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Core.Tests;

/// <summary>The Help tab's explanation of the radio, for any nation's pilot.</summary>
public class RadioGuideTests
{
    [Fact]
    public void Every_command_says_when_to_use_it()
    {
        Assert.Equal(Enum.GetValues<RadioCommand>(), RadioGuide.Calls.Select(c => c.Command).ToArray());
        Assert.All(RadioGuide.Calls, c => Assert.False(string.IsNullOrWhiteSpace(c.When)));
        Assert.All(RadioGuide.Purpose, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.All(RadioGuide.Flying, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.All(RadioGuide.OtherKeys, k =>
        {
            Assert.False(string.IsNullOrWhiteSpace(k.Title));
            Assert.False(string.IsNullOrWhiteSpace(k.When));
        });
    }

    [Fact]
    public void Every_bound_action_is_explained()
    {
        string[] documented = RadioGuide.Calls.Select(c => c.Action)
            .Concat(RadioGuide.OtherKeys.Select(k => k.Action))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();
        string[] settings = new KeySettings().All().Select(a => a.Action).OrderBy(a => a, StringComparer.Ordinal).ToArray();
        Assert.Equal(settings, documented);
    }

    [Fact]
    public void The_guide_addresses_any_air_force()
    {
        string text = string.Join("\n", RadioGuide.Purpose
            .Concat(RadioGuide.Calls.Select(c => c.When))
            .Concat(RadioGuide.OtherKeys.Select(k => k.Title + "\n" + k.When))
            .Concat(RadioGuide.Flying));

        Assert.Contains("whichever air force", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("German", text, StringComparison.Ordinal);
        Assert.Contains("Russian", text, StringComparison.Ordinal);
        Assert.Contains("English", text, StringComparison.Ordinal);
        Assert.Contains("Soviet", text, StringComparison.Ordinal);
        Assert.Contains("British", text, StringComparison.Ordinal);
        Assert.Contains("American", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Luftwaffe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you are flying for", text, StringComparison.OrdinalIgnoreCase);
    }
}
