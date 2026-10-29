using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

public enum RadioCommand
{
    FriendlyPositions,
    FlightPosition,
    Rejoin,
    EnemyContacts,
    IdentifyContact,
    RoutePosition,
    SteerHome,
    SteerTarget,
    FixPosition,
}

public static class RadioCommands
{
    public static string Title(RadioCommand command) => command switch
    {
        RadioCommand.FriendlyPositions => "Request friendly positions",
        RadioCommand.FlightPosition => "Request flight position",
        RadioCommand.Rejoin => "Separated — request rejoin",
        RadioCommand.EnemyContacts => "Request enemy contacts",
        RadioCommand.IdentifyContact => "Identify nearest enemy",
        RadioCommand.RoutePosition => "Where am I on the route",
        RadioCommand.SteerHome => "Steer home",
        RadioCommand.SteerTarget => "Steer to target",
        RadioCommand.FixPosition => "Fix position",
        _ => command.ToString(),
    };

    /// <summary>
    /// The player keys the radio for it: a call goes out, with the mic click. A position fix is his own look at the
    /// ground against the sheet, not a transmission.
    /// </summary>
    public static bool IsTransmission(RadioCommand command) => command != RadioCommand.FixPosition;

    /// <param name="memory">What the flight has already spotted; keep one per session so contacts stay tracked between calls.</param>
    public static RadioResult Execute(RadioCommand command, ContactTracker tracker, RadioSettings? settings = null, SightingMemory? memory = null) => command switch
    {
        RadioCommand.FriendlyPositions => FriendlyPositionsCommand.Execute(tracker, settings),
        RadioCommand.FlightPosition => FlightCommands.FlightPosition(tracker, settings),
        RadioCommand.Rejoin => FlightCommands.Rejoin(tracker, settings),
        RadioCommand.EnemyContacts => EnemyCommands.EnemyContacts(tracker, settings, memory),
        RadioCommand.IdentifyContact => EnemyCommands.Identify(tracker, settings, memory),
        RadioCommand.RoutePosition => RouteCommands.RoutePosition(tracker, settings),
        RadioCommand.SteerHome => RouteCommands.SteerHome(tracker, settings),
        RadioCommand.SteerTarget => RouteCommands.SteerTarget(tracker, settings),
        RadioCommand.FixPosition => FixCommand.FixPosition(tracker, settings),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}
