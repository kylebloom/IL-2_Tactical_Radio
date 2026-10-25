using Il2Radio.Core.Contacts;
using Il2Radio.Core.MissionLog;

namespace Il2Radio.Core.Tests;

/// <summary>Regression tests against a real Career Mode .mlg recorded by IL-2 (Battle of Stalingrad, Oct 2026).</summary>
public class RealLogFixtureTests
{
    private static readonly byte[] Log = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "career-sample.mlg"));

    [Fact]
    public void Entire_file_decodes_with_consistent_record_framing()
    {
        var events = BinaryMissionLog.DecodeAll(Log);

        Assert.IsType<MissionStarted>(events.First(e => e is MissionStarted));
        Assert.Contains(events, e => e is PlaneSpawnedEvent { IsPlayer: true });
    }

    [Fact]
    public void Single_player_log_has_no_periodic_positions()
    {
        var events = BinaryMissionLog.DecodeAll(Log);

        Assert.DoesNotContain(events, e => e is PositionEvent);
    }

    [Fact]
    public void Tracker_identifies_the_player_and_their_flight()
    {
        var tracker = new ContactTracker();
        foreach (var e in BinaryMissionLog.DecodeAll(Log))
            tracker.Apply(e);

        var player = Assert.Single(tracker.Contacts, c => c.IsPlayer);
        Assert.True(player.InPlayerFlight);
        Assert.Contains(tracker.Contacts, c => c.InPlayerFlight && !c.IsPlayer);
        Assert.All(tracker.Contacts, c => Assert.True(c.Coalition is 1 or 2));
    }
}
