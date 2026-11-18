namespace Ancilla.Theater.Tests;

public class FlagEventsTests
{
    // Players 0 and 1 on team 0, 2 and 3 on team 1.
    private static readonly int[] Teams = [0, 0, 1, 1];

    private static List<FlagEvent> Infer((int, int, bool)[] slot, (int, int)[]? deaths = null, (int, int, int)[]? kills = null, (int, int)[]? objectives = null) =>
        FlagEvents.Infer(Teams, slot, deaths ?? [], kills ?? [], objectives ?? []);

    [Fact]
    public void A_steal_then_a_capture_with_an_assist()
    {
        var events = Infer([(100, 0, true), (1_000, 0, false)], objectives: [(100, 0), (1_000, 0), (1_001, 1)]);
        Assert.Equal([new(100, 0, FlagAction.Steal), new(1_000, 0, FlagAction.Capture), new(1_001, 1, FlagAction.CaptureAssist)], events);
    }

    [Fact]
    public void A_killed_carrier_drops_the_flag_for_a_grab_or_a_return()
    {
        // Player 0 steals; player 2 kills them (a carrier kill); player 1 grabs it where it fell.
        var events = Infer([(100, 0, true), (400, 1, true)], deaths: [(300, 0)], kills: [(300, 2, 0)], objectives: [(100, 0), (300, 2), (400, 1)]);
        Assert.Equal([FlagAction.Steal, FlagAction.CarrierKill, FlagAction.Grab], events.Select(e => e.Action));

        // Same, but player 3 returns team 1's flag instead.
        events = Infer([(100, 0, true)], deaths: [(300, 0)], kills: [(300, 2, 0)], objectives: [(100, 0), (300, 2), (500, 3)]);
        Assert.Equal(FlagAction.Return, events[^1].Action);
    }

    [Fact]
    public void A_drop_alive_has_no_objective_event_and_the_flag_goes_home_by_itself()
    {
        // Dropped at 1,000; nobody touches it for longer than the auto-return; the next pickup is a steal.
        var events = Infer([(100, 0, true), (1_000, 0, false), (1_000 + FlagEvents.AutoReturnTicks + 10, 1, true)], objectives: [(100, 0)]);
        Assert.Equal([FlagAction.Steal, FlagAction.Drop, FlagAction.Steal], events.Select(e => e.Action));
    }

    [Fact]
    public void Missing_records_fall_back_to_the_objective_events()
    {
        // The winning capture has no slot record: the carrier's objective event is the capture.
        var events = Infer([(100, 0, true)], objectives: [(100, 0), (900, 0)]);
        Assert.Equal([FlagAction.Steal, FlagAction.Capture], events.Select(e => e.Action));

        // A pickup with no record: an unexplained objective event while nobody carries the flag.
        events = Infer([], objectives: [(200, 2)]);
        Assert.Equal([new FlagEvent(200, 2, FlagAction.Steal)], events);
    }
}
