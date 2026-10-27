namespace Il2Radio.Core.Radio;

/// <summary>What choosing a menu entry does, besides opening a submenu.</summary>
public enum RadioMenuAction
{
    None,
    LogOlder,
    LogNewer,
}

/// <summary>One menu entry: a radio command, a chat action, or a submenu of further entries.</summary>
public sealed record RadioMenuItem(string Label, RadioCommand? Command = null, RadioMenuAction Action = RadioMenuAction.None,
    IReadOnlyList<RadioMenuItem>? Children = null)
{
    public bool IsSubmenu => Children is { Count: > 0 };

    public static RadioMenuItem Submenu(string label, params RadioMenuItem[] children) => new(label, Children: children);
}

/// <param name="Digit">The numpad digit that chooses it (1–9).</param>
public sealed record RadioMenuEntry(int Digit, string Label, bool IsSubmenu);

/// <summary>What the menu shows: the path to the current level, its entries, and how to leave it.</summary>
public sealed record RadioMenuView(IReadOnlyList<string> Path, IReadOnlyList<RadioMenuEntry> Entries, string Footer)
{
    /// <summary>"Radio" at the top, "Radio › Flight" one level down.</summary>
    public string Title => string.Join(" › ", Path);

    /// <summary>
    /// The menu as chat lines, for outputs that show it in the radio log's place (RTSS, the window
    /// overlay): the title, one line per entry under its numpad key, then the footer.
    /// </summary>
    public IReadOnlyList<ChatLine> ToChatLines()
    {
        var lines = new List<ChatLine> { new(ChatLineKind.System, "", Title, -1, TimeSpan.Zero) };
        lines.AddRange(Entries.Select(e => new ChatLine(ChatLineKind.Info, $"Num{e.Digit}", e.Label + (e.IsSubmenu ? " ›" : ""), -1, TimeSpan.Zero)));
        lines.Add(new ChatLine(ChatLineKind.System, "", Footer, -1, TimeSpan.Zero));
        return lines;
    }
}

/// <summary>The outcome of a key press in the menu.</summary>
public sealed record RadioMenuResult(bool Changed, RadioCommand? Command = null, RadioMenuAction Action = RadioMenuAction.None)
{
    public static readonly RadioMenuResult Nothing = new(false);
}

/// <summary>
/// The radio's in-game command menu, in the manner of IL-2's own: the menu key opens it, numpad digits
/// choose an entry or step into a submenu, the menu key steps back (closing it at the top), and the
/// quit key closes it. Choosing a command or action closes it. It also closes after
/// <paramref name="holdSeconds"/> without a key, like the radio log. Not thread-safe: drive it from one thread.
/// </summary>
public sealed class RadioMenu(IReadOnlyList<RadioMenuItem> root, double holdSeconds = 14)
{
    /// <summary>The radio's commands, grouped so each level fits the numpad keys the radio already uses (1–5).</summary>
    public static IReadOnlyList<RadioMenuItem> Default { get; } =
    [
        RadioMenuItem.Submenu("Flight",
            new RadioMenuItem(RadioCommands.Title(RadioCommand.FlightPosition), RadioCommand.FlightPosition),
            new RadioMenuItem(RadioCommands.Title(RadioCommand.Rejoin), RadioCommand.Rejoin)),
        new RadioMenuItem(RadioCommands.Title(RadioCommand.FriendlyPositions), RadioCommand.FriendlyPositions),
        RadioMenuItem.Submenu("Enemy",
            new RadioMenuItem(RadioCommands.Title(RadioCommand.EnemyContacts), RadioCommand.EnemyContacts),
            new RadioMenuItem(RadioCommands.Title(RadioCommand.IdentifyContact), RadioCommand.IdentifyContact)),
        RadioMenuItem.Submenu("Radio log",
            new RadioMenuItem("Older page", Action: RadioMenuAction.LogOlder),
            new RadioMenuItem("Newer page", Action: RadioMenuAction.LogNewer)),
    ];

    public const string RootTitle = "Radio";

    private readonly List<RadioMenuItem> _path = [];
    private DateTime _closesUtc;

    /// <summary>Key names for the footer, e.g. "Num0" and "Num."; null when that key is unbound.</summary>
    public string? MenuKeyLabel { get; init; } = "Num0";
    public string? CloseKeyLabel { get; init; } = "Num.";

    public bool IsOpen { get; private set; }

    /// <summary>The menu key: opens the menu, or steps back one level, closing it at the top.</summary>
    public RadioMenuResult MenuKey(DateTime nowUtc)
    {
        Expire(nowUtc);
        if (!IsOpen)
            IsOpen = true;
        else if (_path.Count > 0)
            _path.RemoveAt(_path.Count - 1);
        else
            IsOpen = false;
        _closesUtc = nowUtc.AddSeconds(holdSeconds);
        return new RadioMenuResult(true);
    }

    /// <summary>Closes the menu if it is open (the quit key while it shows).</summary>
    public RadioMenuResult Close()
    {
        if (!IsOpen)
            return RadioMenuResult.Nothing;
        IsOpen = false;
        _path.Clear();
        return new RadioMenuResult(true);
    }

    /// <summary>A numpad digit while the menu is open: steps into a submenu or chooses a command, which closes the menu.</summary>
    public RadioMenuResult Choose(int digit, DateTime nowUtc)
    {
        Expire(nowUtc);
        var items = Level;
        if (!IsOpen || digit < 1 || digit > items.Count)
            return RadioMenuResult.Nothing;
        var item = items[digit - 1];
        if (item.IsSubmenu)
        {
            _path.Add(item);
            _closesUtc = nowUtc.AddSeconds(holdSeconds);
            return new RadioMenuResult(true);
        }
        Close();
        return new RadioMenuResult(true, item.Command, item.Action);
    }

    /// <summary>Closes the menu once it has gone <c>holdSeconds</c> without a key. True when this call closed it.</summary>
    public bool Expire(DateTime nowUtc) => IsOpen && nowUtc >= _closesUtc && Close().Changed;

    /// <summary>What to show, or null while the menu is closed.</summary>
    public RadioMenuView? View
    {
        get
        {
            if (!IsOpen)
                return null;
            var path = new List<string> { RootTitle };
            path.AddRange(_path.Select(p => p.Label));
            var entries = Level.Select((item, i) => new RadioMenuEntry(i + 1, item.Label, item.IsSubmenu)).ToList();
            string back = MenuKeyLabel is { } menu ? $"{menu} {(_path.Count > 0 ? "back" : "closes")}" : "";
            string close = CloseKeyLabel is { } quit && _path.Count > 0 ? $"{quit} closes" : "";
            return new RadioMenuView(path, entries, string.Join(" · ", new[] { back, close }.Where(s => s.Length > 0)));
        }
    }

    private IReadOnlyList<RadioMenuItem> Level => _path.Count > 0 ? _path[^1].Children! : root;
}
