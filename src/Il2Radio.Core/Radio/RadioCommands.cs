using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

public enum RadioCommand
{
    FriendlyPositions,
    FlightPosition,
    Rejoin,
    EnemyContacts,
    IdentifyContact,
}

public static class RadioCommands
{
    public static string Title(RadioCommand command) => command switch
    {
        RadioCommand.FriendlyPositions => "Request friendly positions",
        RadioCommand.FlightPosition => "Request flight position",
        RadioCommand.Rejoin => "Separated — request rejoin",
        RadioCommand.EnemyContacts => "Request enemy contacts",
        RadioCommand.IdentifyContact => "Identify nearest contact",
        _ => command.ToString(),
    };

    /// <param name="memory">What the flight has already spotted; keep one per session so contacts stay tracked between calls.</param>
    public static RadioResult Execute(RadioCommand command, ContactTracker tracker, RadioSettings? settings = null, SightingMemory? memory = null) => command switch
    {
        RadioCommand.FriendlyPositions => FriendlyPositionsCommand.Execute(tracker, settings),
        RadioCommand.FlightPosition => FlightCommands.FlightPosition(tracker, settings),
        RadioCommand.Rejoin => FlightCommands.Rejoin(tracker, settings),
        RadioCommand.EnemyContacts => EnemyCommands.EnemyContacts(tracker, settings, memory),
        RadioCommand.IdentifyContact => EnemyCommands.Identify(tracker, settings, memory),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}
