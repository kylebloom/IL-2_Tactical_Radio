using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

public class MissionClockTests
{
    private static readonly DateTime T0 = new(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(1942, 8, 26, 14, 12, 55);

    [Fact]
    public void ReferenceTime_is_the_clock_face_in_the_string()
    {
        Assert.True(MissionClock.TryParseReference("1942-08-26T14:12:55Z", out var time));
        Assert.Equal(Start, time);
        Assert.Equal(DateTimeKind.Unspecified, time.Kind);
        Assert.False(MissionClock.TryParseReference("not a time", out _));
        Assert.False(MissionClock.TryParseReference(null, out _));
    }

    [Fact]
    public void A_provisional_sample_does_not_move_an_authoritative_clock()
    {
        var clock = new MissionClock();
        clock.Sync(Start, T0, authoritative: true);
        clock.Sync(Start.AddHours(5), T0.AddSeconds(10), authoritative: false);

        Assert.True(clock.Authoritative);
        Assert.Equal(Start.AddSeconds(10), clock.At(T0.AddSeconds(10)));
    }

    [Fact]
    public void Recording_growth_advances_the_clock_and_a_stall_pauses_it()
    {
        var clock = new MissionClock();
        clock.Sync(Start, T0, authoritative: true);
        clock.ObserveRecording(0, T0);
        for (int second = 1; second <= 10; second++)
            clock.ObserveRecording(second * 100, T0.AddSeconds(second));
        Assert.Equal(Start.AddSeconds(10), clock.At(T0.AddSeconds(10)));

        var quiet = T0.AddSeconds(10 + MissionClock.StallAfter.TotalSeconds - 0.5);
        clock.ObserveRecording(1000, quiet);
        Assert.Equal(Start.AddSeconds(10 + MissionClock.StallAfter.TotalSeconds - 0.5), clock.At(quiet));

        var paused = T0.AddSeconds(10 + MissionClock.StallAfter.TotalSeconds + 0.5);
        clock.ObserveRecording(1000, paused);
        Assert.Equal(Start.AddSeconds(10), clock.At(paused));
        Assert.Equal(Start.AddSeconds(10), clock.At(paused.AddSeconds(20)));

        var resumed = paused.AddSeconds(4);
        clock.ObserveRecording(1400, resumed);
        // The pause runs to the last quiet sample; the interval that showed growth counts as running.
        var running = resumed - paused;
        Assert.Equal(Start.AddSeconds(10) + running, clock.At(resumed));
    }

    [Fact]
    public void A_smaller_file_is_a_new_segment_not_time_running_backwards()
    {
        var clock = new MissionClock();
        clock.Sync(Start, T0, authoritative: true);
        clock.ObserveRecording(5000, T0);
        clock.ObserveRecording(80, T0.AddSeconds(1));
        clock.ObserveRecording(180, T0.AddSeconds(2));

        Assert.Equal(Start.AddSeconds(2), clock.At(T0.AddSeconds(2)));
    }

    [Fact]
    public void Stopping_the_watch_lets_time_run_after_a_file_stall()
    {
        var clock = new MissionClock();
        clock.Sync(Start, T0, authoritative: true);
        clock.ObserveRecording(10, T0);
        var stalled = T0.AddSeconds(MissionClock.StallAfter.TotalSeconds + 1);
        clock.ObserveRecording(10, stalled);
        Assert.Equal(Start, clock.At(stalled.AddSeconds(10)));

        clock.StopWatching(stalled.AddSeconds(10));
        Assert.Equal(Start.AddSeconds(5), clock.At(stalled.AddSeconds(15)));
    }

    [Fact]
    public void Reset_drops_instants_from_the_previous_mission()
    {
        var clock = new MissionClock();
        clock.Sync(Start, T0, authoritative: true);
        var boundary = T0.AddMinutes(30);
        clock.Reset(boundary);

        Assert.False(clock.Authoritative);
        Assert.Null(clock.At(T0.AddMinutes(1)));
        Assert.Null(clock.At(boundary));

        var later = Start.AddHours(2);
        clock.Sync(later, boundary.AddSeconds(5), authoritative: true);
        Assert.Equal(later, clock.At(boundary.AddSeconds(5)));
        Assert.Null(clock.At(T0.AddMinutes(1)));
    }
}
