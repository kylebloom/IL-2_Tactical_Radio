using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Tests;

public class RadioMenuTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MenuKeyOpensAtTheTopLevel()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        Assert.Null(menu.View);

        Assert.True(menu.MenuKey(T0).Changed);

        var view = menu.View!;
        Assert.Equal("Radio", view.Title);
        Assert.Equal(["Flight", "Request friendly positions", "Enemy", "Radio log"], view.Entries.Select(e => e.Label));
        Assert.Equal([1, 2, 3, 4], view.Entries.Select(e => e.Digit));
        Assert.True(view.Entries[0].IsSubmenu);
        Assert.False(view.Entries[1].IsSubmenu);
        Assert.Equal("Num0 closes", view.Footer);
    }

    [Fact]
    public void DigitStepsIntoASubmenuAndMenuKeyStepsBack()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);

        var result = menu.Choose(1, T0.AddSeconds(1));

        Assert.True(result.Changed);
        Assert.Null(result.Command);
        Assert.Equal("Radio › Flight", menu.View!.Title);
        Assert.Equal(["Request flight position", "Separated — request rejoin", "Where am I on the route", "Steer home", "Steer to target"],
            menu.View.Entries.Select(e => e.Label));
        Assert.Equal([1, 2, 3, 4, 5], menu.View.Entries.Select(e => e.Digit)); // still within the radio's numpad keys
        Assert.Equal("Num0 back · Num. closes", menu.View.Footer);

        menu.MenuKey(T0.AddSeconds(2));
        Assert.Equal("Radio", menu.View!.Title);

        menu.MenuKey(T0.AddSeconds(3));
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void ChoosingACommandReturnsItAndClosesTheMenu()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);
        menu.Choose(3, T0);

        var result = menu.Choose(2, T0);

        Assert.Equal(RadioCommand.IdentifyContact, result.Command);
        Assert.False(menu.IsOpen);
        Assert.Null(menu.View);
    }

    [Fact]
    public void ChoosingALogActionReturnsIt()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);
        menu.Choose(4, T0);

        Assert.Equal(RadioMenuAction.LogNewer, menu.Choose(2, T0).Action);
    }

    [Fact]
    public void DigitsWithoutAnEntryOrWhileClosedDoNothing()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        Assert.False(menu.Choose(1, T0).Changed);

        menu.MenuKey(T0);
        Assert.False(menu.Choose(7, T0).Changed);
        Assert.Equal("Radio", menu.View!.Title);
    }

    [Fact]
    public void ClosesAfterTheHoldWithoutAKeyAndEachKeyRestartsIt()
    {
        var menu = new RadioMenu(RadioMenu.Default, holdSeconds: 10);
        menu.MenuKey(T0);
        menu.Choose(1, T0.AddSeconds(8)); // into Flight; the hold restarts

        Assert.False(menu.Expire(T0.AddSeconds(17)));
        Assert.True(menu.Expire(T0.AddSeconds(18)));
        Assert.False(menu.IsOpen);

        // A digit after it expired does not choose from the stale level.
        Assert.False(menu.Choose(1, T0.AddSeconds(19)).Changed);
    }

    [Fact]
    public void ReopeningStartsAtTheTop()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);
        menu.Choose(1, T0);
        menu.Close();

        menu.MenuKey(T0);

        Assert.Equal("Radio", menu.View!.Title);
    }

    [Fact]
    public void ChatLinesCarryTheTitleEntriesAndFooter()
    {
        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);

        var lines = menu.View!.ToChatLines();

        Assert.Equal("Radio", lines[0].Text);
        Assert.Equal(("Num1", "Flight ›"), (lines[1].Speaker, lines[1].Text));
        Assert.Equal(("Num2", "Request friendly positions"), (lines[2].Speaker, lines[2].Text));
        Assert.Equal("Num0 closes", lines[^1].Text);
    }

    [Fact]
    public void FooterFollowsTheBoundKeys()
    {
        var menu = new RadioMenu(RadioMenu.Default) { MenuKeyLabel = "F8", CloseKeyLabel = null };
        menu.MenuKey(T0);
        menu.Choose(1, T0);

        Assert.Equal("F8 back", menu.View!.Footer);
    }

    [Fact]
    public void TimelineShowsTheMenuInPlaceOfTheLiveLinesUntilClosed()
    {
        var timeline = new ChatTimeline(holdSeconds: 14);
        timeline.Post([new ChatLine(ChatLineKind.Reply, "Heinz", "Number one", 2, TimeSpan.Zero)], T0);
        var menu = new RadioMenu(RadioMenu.Default);
        menu.MenuKey(T0);

        timeline.ShowMenu(menu.View!.ToChatLines(), T0.AddSeconds(1));
        Assert.Equal("Radio", timeline.Snapshot(T0.AddSeconds(1)).First().Line.Text);

        timeline.CloseMenu();
        Assert.Equal("Number one", Assert.Single(timeline.Snapshot(T0.AddSeconds(1))).Line.Text);
    }
}
