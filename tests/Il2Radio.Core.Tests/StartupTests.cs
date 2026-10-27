namespace Il2Radio.Core.Tests;

/// <summary>The startup.cfg outputs the radio needs, and switching them on.</summary>
public class Il2OutputsTests
{
    private const string Cfg = """
        [KEY = system]
        	mission_text_log = 0
        [END]

        [KEY = track_record]
        	tacviewrecord = 1
        [END]
        """;

    [Fact]
    public void Missing_lists_only_the_wrong_values()
    {
        var missing = Il2Outputs.Missing(StartupConfig.Parse(Cfg), Il2Outputs.Required);

        Assert.Equal("mission_text_log", Assert.Single(missing).Key);
    }

    [Fact]
    public void Only_the_mission_log_and_tacview_are_required()
    {
        Assert.Equal(["mission_text_log", "tacviewrecord"], Il2Outputs.Required.Select(o => o.Key));
    }

    [Fact]
    public void Enable_backs_up_then_writes()
    {
        string root = Path.Combine(Path.GetTempPath(), "il2radio-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        try
        {
            var install = new Il2Install(root);
            File.WriteAllText(install.StartupCfg, Cfg);

            string? backup = Il2Outputs.Enable(install, Il2Outputs.Required);

            Assert.NotNull(backup);
            Assert.Equal(Cfg, File.ReadAllText(backup));
            Assert.Empty(Il2Outputs.Missing(StartupConfig.Load(install.StartupCfg), Il2Outputs.Required));
            Assert.Null(Il2Outputs.Enable(install, Il2Outputs.Required)); // nothing left to change: no second backup
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>When to remind the player to start track recording.</summary>
public class TrackRecordingWatchTests
{
    private static readonly DateTime Start = new(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void Reminds_once_a_minute_into_a_mission_without_recording()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));

        Assert.False(watch.Update(At(10), Start, recording: false));
        Assert.False(watch.Update(At(59), Start, recording: false));
        Assert.True(watch.Update(At(60), Start, recording: false));
        Assert.False(watch.Update(At(61), Start, recording: false));
        Assert.False(watch.Update(At(600), Start, recording: false));
    }

    [Fact]
    public void Never_reminds_while_recording()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));

        for (int s = 0; s <= 300; s += 10)
            Assert.False(watch.Update(At(s), Start, recording: true));
    }

    [Fact]
    public void Joining_a_mission_long_running_without_a_track_reminds_a_minute_after_first_seeing_it()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));

        Assert.False(watch.Update(At(900), Start, recording: false));
        Assert.True(watch.Update(At(960), Start, recording: false));
    }

    // 2026-10-27: restarted 21 minutes into a recorded mission, the program reminded on its first poll,
    // four seconds before it found the track IL-2 was still writing.
    [Fact]
    public void Joining_a_recorded_mission_does_not_remind_before_the_track_is_found()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));

        Assert.False(watch.Update(At(1_298), Start, recording: false));
        Assert.False(watch.Update(At(1_302), Start, recording: true));
        Assert.False(watch.Update(At(1_400), Start, recording: true));
    }

    [Fact]
    public void Stopping_the_recording_reminds_again_a_minute_later()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));
        Assert.False(watch.Update(At(5), Start, recording: true));

        Assert.False(watch.Update(At(200), Start, recording: false));
        Assert.False(watch.Update(At(259), Start, recording: false));
        Assert.True(watch.Update(At(260), Start, recording: false));
    }

    [Fact]
    public void No_reminder_without_a_running_mission_and_none_twice_after_a_pause()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));

        Assert.False(watch.Update(At(100), null, recording: false));
        Assert.False(watch.Update(At(120), Start, recording: false));
        Assert.True(watch.Update(At(180), Start, recording: false));
        Assert.False(watch.Update(At(200), null, recording: false)); // paused: the log stops
        Assert.False(watch.Update(At(400), Start, recording: false));
    }

    [Fact]
    public void A_new_mission_starts_over()
    {
        var watch = new TrackRecordingWatch(TimeSpan.FromSeconds(60));
        Assert.True(watch.Update(At(60), Start, recording: false));

        var next = At(1800);
        Assert.False(watch.Update(next.AddSeconds(30), next, recording: false));
        Assert.True(watch.Update(next.AddSeconds(60), next, recording: false));
    }

    [Fact]
    public void Zero_grace_switches_it_off()
    {
        var watch = new TrackRecordingWatch(TimeSpan.Zero);

        Assert.False(watch.Update(At(3600), Start, recording: false));
    }
}
