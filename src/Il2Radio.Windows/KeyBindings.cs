using System.Runtime.InteropServices;
using Il2Radio.Core.Input;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;

namespace Il2Radio.Windows;

public enum RadioKey
{
    FriendlyPositions, // Numpad 1
    FlightPosition,    // Numpad 2
    Rejoin,            // Numpad 3
    EnemyContacts,     // Numpad 4
    IdentifyContact,   // Numpad 5
    Menu,              // Numpad 0: show or hide the command list in the chat output
    Older,             // Numpad 7: page back through the radio log
    Newer,             // Numpad 9: page forward; past the newest page returns to the live lines
    Quit,              // Numpad . (Del)
}

/// <summary>
/// Global key bindings that work while IL-2 has focus. Polls the key state instead of using a
/// hook or RegisterHotKey, so keys are observed but never consumed: IL-2 still receives them.
/// The default numpad keys need Num Lock on (with it off the numpad sends arrow/Home/End keys instead).
/// </summary>
public sealed class KeyBindings
{
    private readonly (int VirtualKey, RadioKey Key)[] _bindings;
    private readonly bool[] _wasDown;

    public KeyBindings(IEnumerable<(int VirtualKey, RadioKey Key)> bindings)
    {
        _bindings = bindings.ToArray();
        _wasDown = new bool[_bindings.Length];
        NumLock = _bindings.Any(b => VirtualKeys.NeedsNumLock(b.VirtualKey));
        Menu = BuildMenu();
    }

    /// <summary>The numpad layout: Num1–Num5 commands, Num0 menu, Num7/Num9 log, Num. quit.</summary>
    public static KeyBindings Default => From(new KeySettings());

    /// <summary>Bindings from the settings file. Unbound (empty) actions are left out; call only on validated settings.</summary>
    public static KeyBindings From(KeySettings keys) =>
        new(new (string? Name, RadioKey Key)[]
            {
                (keys.FriendlyPositions, RadioKey.FriendlyPositions), (keys.FlightPosition, RadioKey.FlightPosition),
                (keys.Rejoin, RadioKey.Rejoin), (keys.EnemyContacts, RadioKey.EnemyContacts),
                (keys.IdentifyContact, RadioKey.IdentifyContact), (keys.Menu, RadioKey.Menu),
                (keys.Older, RadioKey.Older), (keys.Newer, RadioKey.Newer), (keys.Quit, RadioKey.Quit),
            }
            .Where(b => VirtualKeys.Parse(b.Name) is not null)
            .Select(b => (VirtualKeys.Parse(b.Name)!.Value, b.Key)));

    /// <summary>Some binding is a numpad digit or the decimal key, which need Num Lock on.</summary>
    public bool NumLock { get; }

    /// <summary>The key's label ("Num1"), or null when the action is unbound.</summary>
    public string? Label(RadioKey key) =>
        _bindings.FirstOrDefault(b => b.Key == key) is { VirtualKey: > 0 } b ? VirtualKeys.Label(b.VirtualKey) : null;

    /// <summary>One line for the console, e.g. "Num1 friendly positions, Num2 flight position, … (Num Lock on)".</summary>
    public string Legend
    {
        get
        {
            var parts = new List<string>();
            void Add(RadioKey key, string what)
            {
                if (Label(key) is { } label)
                    parts.Add($"{label} {what}");
            }

            Add(RadioKey.FriendlyPositions, "friendly positions");
            Add(RadioKey.FlightPosition, "flight position");
            Add(RadioKey.Rejoin, "rejoin");
            Add(RadioKey.EnemyContacts, "contacts");
            Add(RadioKey.IdentifyContact, "identify");
            Add(RadioKey.Menu, "menu");
            if (Label(RadioKey.Older) is { } older && Label(RadioKey.Newer) is { } newer)
                parts.Add($"{older}/{newer} log older/newer");
            else
            {
                Add(RadioKey.Older, "log older");
                Add(RadioKey.Newer, "log newer");
            }
            Add(RadioKey.Quit, "quit");
            return parts.Count == 0 ? "no radio keys set" : string.Join(", ", parts) + (NumLock ? " (Num Lock on)" : "");
        }
    }

    /// <summary>Shown under a page of the radio log.</summary>
    public string HistoryHint =>
        (Label(RadioKey.Older) is { } older ? $" · {older} older" : "") + (Label(RadioKey.Newer) is { } newer ? $" · {newer} newer" : "");

    /// <summary>The command list the menu key shows in the chat output, one line per bound key.</summary>
    public IReadOnlyList<ChatLine> Menu { get; }

    private List<ChatLine> BuildMenu()
    {
        string closes = Label(RadioKey.Menu) is { } menu ? $" · {menu} closes" : "";
        var lines = new List<ChatLine> { new(ChatLineKind.System, "", $"Radio commands{(NumLock ? " (Num Lock on)" : "")}{closes}", -1, TimeSpan.Zero) };
        foreach (var (key, action) in new[]
                 {
                     (RadioKey.FriendlyPositions, RadioCommands.Title(RadioCommand.FriendlyPositions)),
                     (RadioKey.FlightPosition, RadioCommands.Title(RadioCommand.FlightPosition)),
                     (RadioKey.Rejoin, RadioCommands.Title(RadioCommand.Rejoin)),
                     (RadioKey.EnemyContacts, RadioCommands.Title(RadioCommand.EnemyContacts)),
                     (RadioKey.IdentifyContact, RadioCommands.Title(RadioCommand.IdentifyContact)),
                     (RadioKey.Older, "Radio log, older"),
                     (RadioKey.Newer, "Radio log, newer"),
                     (RadioKey.Quit, "Quit the radio"),
                 })
        {
            if (Label(key) is { } label)
                lines.Add(new ChatLine(ChatLineKind.Info, label, action, -1, TimeSpan.Zero));
        }

        return lines;
    }

    /// <summary>Returns keys that went down since the previous call.</summary>
    public IEnumerable<RadioKey> Pressed()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        for (int i = 0; i < _bindings.Length; i++)
        {
            bool down = (GetAsyncKeyState(_bindings[i].VirtualKey) & 0x8000) != 0;
            if (down && !_wasDown[i])
                yield return _bindings[i].Key;
            _wasDown[i] = down;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
