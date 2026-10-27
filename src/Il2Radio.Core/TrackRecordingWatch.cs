namespace Il2Radio.Core;

/// <summary>
/// Decides when to remind the player to start track recording (LCtrl+R). Live positions only exist
/// while a Tacview track records, and IL-2 doesn't start one by itself. One reminder per stretch of
/// a mission without recording: when the mission has run <c>grace</c> without one, or that long
/// after recording stopped. A mission first seen after its grace was already over gets a grace of its
/// own from then: a session started mid-mission has not looked for the track yet. In the 2026-10-27 flight
/// the program was restarted 21 minutes into a recorded mission and reminded on its first poll, four
/// seconds before it found the track IL-2 was still writing.
/// </summary>
public sealed class TrackRecordingWatch(TimeSpan grace)
{
    private DateTime? _mission;
    private DateTime? _notRecordingSince;
    private bool _reminded;

    /// <summary>
    /// Call on every poll. <paramref name="missionStartUtc"/> is the running mission's start, or null
    /// while none is running (menus, paused, ended); <paramref name="recording"/> says whether a track
    /// for this mission is being written. Returns true exactly when a reminder is due.
    /// </summary>
    public bool Update(DateTime nowUtc, DateTime? missionStartUtc, bool recording)
    {
        if (grace <= TimeSpan.Zero)
            return false;

        if (missionStartUtc is { } start && start != _mission)
        {
            _mission = start;
            _notRecordingSince = nowUtc - start <= grace ? start : nowUtc;
            _reminded = false;
        }

        if (recording)
        {
            _notRecordingSince = null;
            _reminded = false;
            return false;
        }

        if (missionStartUtc is null || _mission is null)
            return false;

        _notRecordingSince ??= nowUtc; // recording just stopped
        if (_reminded || nowUtc - _notRecordingSince < grace)
            return false;
        _reminded = true;
        return true;
    }
}
