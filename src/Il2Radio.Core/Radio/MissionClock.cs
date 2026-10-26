using System.Globalization;

namespace Il2Radio.Core.Radio;

/// <summary>
/// The mission clock a cockpit watch shows. IL-2 writes it as the ACMI <c>ReferenceTime</c>
/// (in-game time of the first recorded frame, not the wall clock) plus <c>#t</c> seconds, and
/// <c>#t</c> does not advance while the simulation is paused.
/// </summary>
/// <remarks>
/// VERIFIED 2026-10-25 on this machine's Career sorties. The mission file <c>Time</c> matched
/// log AType 0 <c>GTime</c> (26.8.1942 14:11:57). <c>ReferenceTime + #t</c> matched
/// <c>GTime + tick/50</c> at the end of the 15:58 sortie within 3 s (13:48:55 against 13:48:52).
/// A 20:06 recording spanned 874 s of wall time and only 658 s of <c>#t</c>, so a paused sim
/// leaves the track's clock where the watch stops. <c>RecordingTime</c>'s trailing Z is local
/// wall time, not UTC, and is not used here.
/// </remarks>
public sealed class MissionClock
{
    /// <summary>
    /// How long the recording may sit at the same size before the sim is treated as paused.
    /// The session polls about once a second, and a position snapshot can stall that poll, so
    /// one quiet sample is not a pause. The clock can therefore run on for up to this long
    /// after the game is paused, then it stops.
    /// </summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly List<(DateTime From, DateTime To)> _pauses = [];

    private DateTime? _game;
    private DateTime _wall;
    private bool _authoritative;
    private bool _tracking;
    private DateTime _trackedFrom;
    private long? _size;
    private DateTime _lastWall;
    private DateTime _lastGrowth;
    private DateTime? _pauseFrom;

    /// <summary>True once an ACMI frame has placed the clock. A mission-log sample is only provisional.</summary>
    public bool Authoritative
    {
        get { lock (_gate) return _authoritative; }
    }

    /// <summary>
    /// Reads an ACMI <c>ReferenceTime</c> such as <c>1942-08-26T14:12:55Z</c>. The clock face is
    /// the time written in the string; the trailing Z is not applied as a zone.
    /// </summary>
    public static bool TryParseReference(string? text, out DateTime gameTime)
    {
        gameTime = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return false;
        gameTime = DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Drops the anchor and the pause record. The next sample belongs to a new mission.</summary>
    public void Reset(DateTime wallUtc)
    {
        lock (_gate)
        {
            _game = null;
            _authoritative = false;
            _pauses.Clear();
            _pauseFrom = null;
            _size = null;
            _tracking = true;
            _trackedFrom = wallUtc;
            _lastWall = wallUtc;
            _lastGrowth = wallUtc;
        }
    }

    /// <summary>
    /// Places the clock at <paramref name="wallUtc"/>. An authoritative sample (an ACMI frame)
    /// replaces a provisional one (the mission log). A provisional sample never moves an
    /// authoritative clock.
    /// </summary>
    public void Sync(DateTime gameTime, DateTime wallUtc, bool authoritative)
    {
        lock (_gate)
        {
            if (!authoritative && _authoritative)
                return;
            _game = DateTime.SpecifyKind(gameTime, DateTimeKind.Unspecified);
            _wall = wallUtc;
            _authoritative = authoritative;
            if (!_tracking)
            {
                _tracking = true;
                _trackedFrom = DateTime.MinValue;
                _lastWall = wallUtc;
                _lastGrowth = wallUtc;
            }
        }
    }

    /// <summary>
    /// The recording is no longer the sim's clock: IL-2 has released the file. A pause that was
    /// only "the file stopped growing" ends here, so the watch can run on without a track.
    /// </summary>
    public void StopWatching(DateTime wallUtc)
    {
        lock (_gate)
        {
            ClosePause(wallUtc);
            _size = null;
        }
    }

    /// <summary>
    /// A new ACMI segment. The file size starts over; that drop is not time running backwards.
    /// A pause already under way is closed here.
    /// </summary>
    public void NewRecording(DateTime wallUtc)
    {
        lock (_gate)
        {
            ClosePause(wallUtc);
            _size = null;
            _lastWall = wallUtc;
            _lastGrowth = wallUtc;
        }
    }

    /// <summary>
    /// The recording's size at <paramref name="wallUtc"/>. Growth means the sim wrote frames
    /// since the previous sample. A size that sits still for <see cref="StallAfter"/> is a pause,
    /// and the clock stops at the last growth.
    /// </summary>
    public void ObserveRecording(long sizeBytes, DateTime wallUtc)
    {
        lock (_gate)
        {
            if (!_tracking)
            {
                _tracking = true;
                _trackedFrom = wallUtc;
                _size = sizeBytes;
                _lastWall = wallUtc;
                _lastGrowth = wallUtc;
                return;
            }

            if (_size is null || sizeBytes < _size)
            {
                ClosePause(_lastWall);
                _size = sizeBytes;
                _lastWall = wallUtc;
                _lastGrowth = wallUtc;
                return;
            }

            if (sizeBytes > _size)
            {
                if (_pauseFrom is { } from && _lastWall > from)
                    _pauses.Add((from, _lastWall));
                _pauseFrom = null;
                _size = sizeBytes;
                _lastGrowth = wallUtc;
                _lastWall = wallUtc;
                return;
            }

            _lastWall = wallUtc;
            if (_pauseFrom is null && wallUtc - _lastGrowth >= StallAfter)
                _pauseFrom = _lastGrowth;
        }
    }

    /// <summary>
    /// Mission time at <paramref name="wallUtc"/>, or null before the first sample, or when
    /// <paramref name="wallUtc"/> is earlier than this mission (see <see cref="Reset"/>).
    /// </summary>
    public DateTime? At(DateTime wallUtc)
    {
        lock (_gate)
        {
            if (_game is null || !_tracking || wallUtc < _trackedFrom)
                return null;
            var running = wallUtc >= _wall ? Running(_wall, wallUtc) : -Running(wallUtc, _wall);
            return _game.Value + running;
        }
    }

    private void ClosePause(DateTime at)
    {
        if (_pauseFrom is { } from && at > from)
            _pauses.Add((from, at));
        _pauseFrom = null;
    }

    private TimeSpan Running(DateTime from, DateTime to)
    {
        if (to <= from)
            return TimeSpan.Zero;
        var span = to - from;
        foreach (var (pauseFrom, pauseTo) in _pauses)
            span -= Overlap(from, to, pauseFrom, pauseTo);
        if (_pauseFrom is { } open)
            span -= Overlap(from, to, open, DateTime.MaxValue);
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

    private static TimeSpan Overlap(DateTime a0, DateTime a1, DateTime b0, DateTime b1)
    {
        var start = a0 > b0 ? a0 : b0;
        var end = a1 < b1 ? a1 : b1;
        return end > start ? end - start : TimeSpan.Zero;
    }
}
