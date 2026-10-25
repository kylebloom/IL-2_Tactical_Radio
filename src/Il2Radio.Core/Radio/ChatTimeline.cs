namespace Il2Radio.Core.Radio;

/// <summary>A chat line currently on screen; <see cref="Alpha"/> falls from 1 to 0 over the fade after the hold.</summary>
public sealed record VisibleChatLine(ChatLine Line, DateTime ShownUtc, float Alpha);

/// <summary>
/// Decides which radio-log lines are on screen: lines appear at their <see cref="ChatLine.Delay"/>
/// (or, when synced to the voice, when their transmission starts), stay for a hold time, fade and
/// expire. Every line shown is also kept in a history that can be paged through, and the screen can
/// show a command menu instead of the live lines. Shared by every chat output (window overlay,
/// RTSS OSD). Thread-safe.
/// </summary>
public sealed class ChatTimeline(double holdSeconds = 14, double fadeSeconds = 2, int maxLines = 8, int historySize = 200)
{
    /// <summary>A reply waiting for its voice still appears this long after its estimated time, in case the voice never plays it.</summary>
    public static readonly TimeSpan VoiceTimeout = TimeSpan.FromSeconds(10);

    private enum View { Live, Menu, History }

    private readonly object _gate = new();
    private readonly List<(ChatLine Line, DateTime DueUtc, bool AwaitVoice)> _pending = [];
    private readonly List<(ChatLine Line, DateTime ShownUtc)> _shown = [];
    private readonly List<(ChatLine Line, DateTime ShownUtc)> _history = [];
    private View _view;
    private IReadOnlyList<ChatLine> _menu = [];
    private int _historyOffset; // lines skipped back from the newest
    private DateTime _viewUntilUtc;

    public double HoldSeconds => holdSeconds;

    /// <summary>Appended to the history footer, e.g. which keys page it.</summary>
    public string HistoryHint { get; init; } = "";

    /// <summary>History lines per page: the line limit, less one for the footer.</summary>
    public int PageSize => Math.Max(1, maxLines - 1);

    /// <summary>
    /// Queues a new exchange and returns the screen to the live lines. Like the voice, a new request
    /// drops lines not yet shown. With <paramref name="syncToVoice"/>, reply lines wait for
    /// <see cref="Spoken"/> instead of their estimate.
    /// </summary>
    public void Post(IReadOnlyList<ChatLine> lines, DateTime nowUtc, bool syncToVoice = false)
    {
        lock (_gate)
        {
            _view = View.Live;
            _pending.Clear();
            foreach (var line in lines)
            {
                bool waits = syncToVoice && line.Kind == ChatLineKind.Reply;
                _pending.Add((line, nowUtc + line.Delay + (waits ? VoiceTimeout : TimeSpan.Zero), waits));
            }
        }
    }

    /// <summary>The voice started a transmission: show the first waiting reply with this text now.</summary>
    public void Spoken(string text, DateTime nowUtc)
    {
        lock (_gate)
        {
            int i = _pending.FindIndex(p => p.AwaitVoice && p.Line.Text == text);
            if (i >= 0)
                _pending[i] = _pending[i] with { DueUtc = nowUtc, AwaitVoice = false };
        }
    }

    /// <summary>Shows <paramref name="menu"/> for the hold time instead of the live lines, or closes it if it is open.</summary>
    public void ToggleMenu(IReadOnlyList<ChatLine> menu, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_view == View.Menu && nowUtc < _viewUntilUtc)
            {
                _view = View.Live;
                return;
            }
            _menu = menu;
            _view = View.Menu;
            _viewUntilUtc = nowUtc.AddSeconds(holdSeconds);
        }
    }

    /// <summary>
    /// Pages through the history: positive is older, negative newer. The first step back shows the
    /// newest page; stepping forward past it returns to the live lines. The view closes by itself
    /// after the hold time without a key.
    /// </summary>
    public void Scroll(int pages, DateTime nowUtc)
    {
        lock (_gate)
        {
            Advance(nowUtc);
            bool browsing = _view == View.History && nowUtc < _viewUntilUtc;
            int offset = browsing ? _historyOffset + pages * PageSize : pages > 0 ? 0 : -1;
            if (offset < 0)
            {
                _view = View.Live;
                return;
            }
            _historyOffset = Math.Min(offset, Math.Max(0, _history.Count - PageSize));
            _view = View.History;
            _viewUntilUtc = nowUtc.AddSeconds(holdSeconds);
        }
    }

    /// <summary>Moves due lines on screen, drops expired ones and returns what is visible, oldest first.</summary>
    public IReadOnlyList<VisibleChatLine> Snapshot(DateTime nowUtc)
    {
        lock (_gate)
        {
            Advance(nowUtc);
            if (_view != View.Live && nowUtc >= _viewUntilUtc)
                _view = View.Live;

            float viewAlpha = (float)Math.Clamp((_viewUntilUtc - nowUtc).TotalSeconds / fadeSeconds, 0, 1);
            return _view switch
            {
                View.Menu => _menu.Select(l => new VisibleChatLine(l, nowUtc, viewAlpha)).ToList(),
                View.History => HistoryPage(nowUtc, viewAlpha),
                _ => _shown.Select(s =>
                {
                    double age = (nowUtc - s.ShownUtc).TotalSeconds;
                    float alpha = age <= holdSeconds ? 1f : (float)Math.Clamp(1 - (age - holdSeconds) / fadeSeconds, 0, 1);
                    return new VisibleChatLine(s.Line, s.ShownUtc, alpha);
                }).ToList(),
            };
        }
    }

    private void Advance(DateTime nowUtc)
    {
        // Keep the radio order: a line never overtakes an earlier one still waiting.
        while (_pending.Count > 0 && _pending[0].DueUtc <= nowUtc)
        {
            var shown = (_pending[0].Line, _pending[0].DueUtc);
            _shown.Add(shown);
            _history.Add(shown);
            _pending.RemoveAt(0);
        }
        if (_history.Count > historySize)
            _history.RemoveRange(0, _history.Count - historySize);

        _shown.RemoveAll(s => (nowUtc - s.ShownUtc).TotalSeconds >= holdSeconds + fadeSeconds);
        while (_shown.Count > maxLines)
            _shown.RemoveAt(0);
    }

    /// <summary>One page of past lines, each stamped with its local time, and a footer saying where in the log it is.</summary>
    private List<VisibleChatLine> HistoryPage(DateTime nowUtc, float alpha)
    {
        int end = _history.Count - _historyOffset, start = Math.Max(0, end - PageSize);
        var page = _history.Skip(start).Take(end - start).Select(h =>
        {
            string time = h.ShownUtc.ToLocalTime().ToString("HH:mm:ss");
            var line = h.Line.Speaker.Length > 0 ? h.Line with { Speaker = $"{time} {h.Line.Speaker}" } : h.Line with { Text = $"{time} {h.Line.Text}" };
            return new VisibleChatLine(line, h.ShownUtc, alpha);
        }).ToList();

        string where = _history.Count == 0 ? "Radio log is empty" : $"Radio log {start + 1}-{end} of {_history.Count}";
        page.Add(new VisibleChatLine(new ChatLine(ChatLineKind.System, "", where + HistoryHint, -1, TimeSpan.Zero), nowUtc, alpha));
        return page;
    }
}
