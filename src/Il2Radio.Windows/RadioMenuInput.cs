using Il2Radio.Core.Radio;

namespace Il2Radio.Windows;

/// <summary>
/// Drives the in-game <see cref="RadioMenu"/> from the radio keys and shows it on the chat output.
/// While the menu is open, numpad digits choose its entries instead of firing their usual commands,
/// and the quit key closes it instead of quitting. Keys are still only observed, never consumed, so
/// IL-2 sees them as before. Call from one thread (the session's key loop).
/// </summary>
internal sealed class RadioMenuInput(IChatSink chat, KeyBindings keys)
{
    private readonly RadioMenu _menu = new(RadioMenu.Default, ChatSinks.HoldSeconds)
    {
        MenuKeyLabel = keys.Label(RadioKey.Menu),
        CloseKeyLabel = keys.Label(RadioKey.Quit),
    };

    public bool IsOpen => _menu.IsOpen;

    /// <summary>
    /// Handles one tick of key presses. <paramref name="pressed"/> are this tick's radio keys and
    /// <paramref name="digits"/> its numpad digits. Returns the keys the menu leaves to their usual
    /// meaning, and what the menu chose, if anything.
    /// </summary>
    public (List<RadioKey> Remaining, RadioMenuResult Chosen) Handle(IEnumerable<RadioKey> pressed, IEnumerable<int> digits, DateTime nowUtc)
    {
        if (_menu.Expire(nowUtc))
            chat.ShowMenu(null);

        bool wasOpen = _menu.IsOpen; // a digit that closes the menu must not also fire its command key
        var remaining = new List<RadioKey>();
        var chosen = RadioMenuResult.Nothing;
        foreach (var key in pressed)
        {
            if (key == RadioKey.Menu)
                Show(_menu.MenuKey(nowUtc));
            else if (key == RadioKey.Quit && _menu.IsOpen)
                Show(_menu.Close());
            else if (!(wasOpen && keys.IsNumpadDigit(key)))
                remaining.Add(key);
        }
        if (wasOpen)
        {
            foreach (int digit in digits)
            {
                var result = _menu.Choose(digit, nowUtc);
                Show(result);
                if (result.Command is not null || result.Action != RadioMenuAction.None)
                    chosen = result;
            }
        }
        return (remaining, chosen);
    }

    private void Show(RadioMenuResult result)
    {
        if (result.Changed)
            chat.ShowMenu(_menu.View);
    }
}
