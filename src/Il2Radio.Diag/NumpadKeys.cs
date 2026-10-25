using System.Runtime.InteropServices;
using Il2Radio.Core.Radio;

namespace Il2Radio.Diag;

public enum RadioKey
{
    FriendlyPositions, // Numpad 1
    FlightPosition,    // Numpad 2
    Rejoin,            // Numpad 3
    Menu,              // Numpad 0: show or hide the command list in the chat output
    Older,             // Numpad 7: page back through the radio log
    Newer,             // Numpad 9: page forward; past the newest page returns to the live lines
    Quit,              // Numpad . (Del)
}

/// <summary>
/// Global numpad bindings that work while IL-2 has focus. Polls the key state instead of using a
/// hook or RegisterHotKey, so keys are observed but never consumed: IL-2 still receives them.
/// Num Lock must be on (with it off the numpad sends arrow/Home/End keys instead).
/// </summary>
internal static class NumpadKeys
{
    private const int VK_NUMPAD1 = 0x61;
    private const int VK_NUMPAD2 = 0x62;
    private const int VK_NUMPAD3 = 0x63;
    private const int VK_NUMPAD0 = 0x60;
    private const int VK_NUMPAD7 = 0x67;
    private const int VK_NUMPAD9 = 0x69;
    private const int VK_DECIMAL = 0x6E;

    private static readonly (int VirtualKey, RadioKey Key)[] Bindings =
    [
        (VK_NUMPAD1, RadioKey.FriendlyPositions),
        (VK_NUMPAD2, RadioKey.FlightPosition),
        (VK_NUMPAD3, RadioKey.Rejoin),
        (VK_NUMPAD0, RadioKey.Menu),
        (VK_NUMPAD7, RadioKey.Older),
        (VK_NUMPAD9, RadioKey.Newer),
        (VK_DECIMAL, RadioKey.Quit),
    ];

    public const string Legend = "Num1 friendly positions, Num2 flight position, Num3 rejoin, Num0 menu, Num7/Num9 log older/newer, Num. quit (Num Lock on)";

    /// <summary>Shown under a page of the radio log.</summary>
    public const string HistoryHint = " · Num7 older · Num9 newer";

    /// <summary>The command list Numpad 0 shows in the chat output, one line per key.</summary>
    public static IReadOnlyList<ChatLine> Menu { get; } =
    [
        new(ChatLineKind.System, "", "Radio commands (Num Lock on) · Num0 closes", -1, TimeSpan.Zero),
        Item("Num1", RadioCommands.Title(RadioCommand.FriendlyPositions)),
        Item("Num2", RadioCommands.Title(RadioCommand.FlightPosition)),
        Item("Num3", RadioCommands.Title(RadioCommand.Rejoin)),
        Item("Num7", "Radio log, older"),
        Item("Num9", "Radio log, newer"),
        Item("Num.", "Quit the radio"),
    ];

    private static ChatLine Item(string key, string action) => new(ChatLineKind.Info, key, action, -1, TimeSpan.Zero);

    private static readonly bool[] WasDown = new bool[Bindings.Length];

    /// <summary>Returns keys that went down since the previous call.</summary>
    public static IEnumerable<RadioKey> Pressed()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        for (int i = 0; i < Bindings.Length; i++)
        {
            bool down = (GetAsyncKeyState(Bindings[i].VirtualKey) & 0x8000) != 0;
            if (down && !WasDown[i])
                yield return Bindings[i].Key;
            WasDown[i] = down;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
