using Il2Radio.Core.Contacts;

namespace Il2Radio.Core.Radio;

public enum RadioCommand
{
    FriendlyPositions,
    FlightPosition,
    Rejoin,
}

public static class RadioCommands
{
    public static string Title(RadioCommand command) => command switch
    {
        RadioCommand.FriendlyPositions => "Request friendly positions",
        RadioCommand.FlightPosition => "Request flight position",
        RadioCommand.Rejoin => "Separated — request rejoin",
        _ => command.ToString(),
    };

    public static RadioResult Execute(RadioCommand command, ContactTracker tracker, RadioSettings? settings = null) => command switch
    {
        RadioCommand.FriendlyPositions => FriendlyPositionsCommand.Execute(tracker, settings),
        RadioCommand.FlightPosition => FlightCommands.FlightPosition(tracker, settings),
        RadioCommand.Rejoin => FlightCommands.Rejoin(tracker, settings),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}
