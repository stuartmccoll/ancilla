using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// A flag action in Capture the Flag. Steal: took the enemy flag from its base. Grab: picked up the
/// enemy flag where it was dropped. Capture: scored it. Drop: let go of it alive (killed carriers
/// drop it too, but that's the kill). Return: sent their own team's dropped flag home. CarrierKill:
/// killed the enemy carrier of their team's flag. CaptureAssist: a teammate credited at a capture.
/// </summary>
public enum FlagAction { Steal, Grab, Capture, CaptureAssist, Drop, Return, CarrierKill }

public sealed record FlagEvent(int Tick, int Player, FlagAction Action);

/// <summary>
/// Works out flag actions from what the film shows, following each team's flag as home, carried or
/// dropped (FORMAT.md, "The flag slot"):
/// <list type="bullet">
/// <item>The carrier's flag slot (inventory slot 2) filling with the flag is a pickup: a steal when
/// the flag was home, a grab when it was dropped.</item>
/// <item>The slot emptying is a capture when the carrier's own objective event (summary code 5) comes
/// at the same moment, and a drop otherwise. A carrier's death drops the flag.</item>
/// <item>An objective event at the player's own kill of the carrier is a carrier kill; one by the
/// capturer's teammate at a capture is an assist; any other one by a player whose team's flag is
/// dropped (or was returned at that moment) is a return.</item>
/// <item>A dropped flag nobody touches goes home after <see cref="AutoReturnTicks"/> (inferred: the
/// counts fit best between 13 and 18 s).</item>
/// </list>
/// Records can be missing (the winning capture's, some pickups'), so an objective event from the
/// carrier with no record is taken as a capture, and an unexplained one while nobody carries the enemy
/// flag as a pickup. Checked by <c>inspect</c> against the scoreboard's per-player CTF stats.
/// </summary>
public static class FlagEvents
{
    public const int AutoReturnTicks = 900, SameMomentTicks = 3, FlagSlot = 2;

    /// <param name="teamOf">Each player's team (0 or 1).</param>
    /// <param name="flagSlot">Flag-slot changes: tick, player, and whether the flag went in (false: the slot emptied).</param>
    /// <param name="deaths">Tick and player, for every death.</param>
    /// <param name="kills">Tick, killer and victim.</param>
    /// <param name="objectives">Tick and player of each objective event (summary code 5).</param>
    public static List<FlagEvent> Infer(IReadOnlyList<int> teamOf,
        IEnumerable<(int Tick, int Player, bool In)> flagSlot, IEnumerable<(int Tick, int Player)> deaths,
        IEnumerable<(int Tick, int Killer, int Victim)> kills, IEnumerable<(int Tick, int Player)> objectives)
    {
        var obj = objectives.OrderBy(o => o.Tick).ToList();
        var used = new bool[obj.Count];
        bool TakeObjective(int player, int tick)
        {
            for (var i = 0; i < obj.Count; i++)
                if (!used[i] && obj[i].Player == player && Math.Abs(obj[i].Tick - tick) <= SameMomentTicks) { used[i] = true; return true; }
            return false;
        }

        // 0: slot change, 1: death, 2: objective event (handled last at a tick, after the changes it may belong to).
        var timeline = flagSlot.Select(f => (f.Tick, Order: 0, f.Player, f.In, Victim: -1))
            .Concat(deaths.Select(d => (d.Tick, Order: 1, d.Player, In: false, Victim: -1)))
            .Concat(obj.Select((o, i) => (o.Tick, Order: 2, o.Player, In: false, Victim: i)))
            .OrderBy(e => e.Tick).ThenBy(e => e.Order).ToList();
        var killList = kills.ToList();

        // Per flag (by the team it belongs to): its carrier, or when it was dropped (null: home).
        var carrier = new int?[2];
        var droppedAt = new int?[2];
        var returnedAt = new int?[2];
        var capturedAt = new (int Tick, int Team)?[1];
        var carrierDeaths = new List<(int Tick, int Player)>();
        var result = new List<FlagEvent>();
        foreach (var (tick, order, player, isIn, objIndex) in timeline)
        {
            for (var f = 0; f < 2; f++)
                if (droppedAt[f] is { } d && tick - d > AutoReturnTicks) droppedAt[f] = null;
            if (player < 0 || player >= teamOf.Count || teamOf[player] is not (0 or 1)) continue;
            var team = teamOf[player];
            var enemyFlag = 1 - team;
            switch (order)
            {
                case 0 when isIn:
                    if (carrier[enemyFlag] == player) break;   // the same pickup again
                    var action = droppedAt[enemyFlag] is null ? FlagAction.Steal : FlagAction.Grab;
                    TakeObjective(player, tick);
                    result.Add(new FlagEvent(tick, player, action));
                    (carrier[enemyFlag], droppedAt[enemyFlag]) = (player, null);
                    break;
                case 0:
                    if (carrier[enemyFlag] != player) break;
                    carrier[enemyFlag] = null;
                    if (TakeObjective(player, tick)) { result.Add(new FlagEvent(tick, player, FlagAction.Capture)); capturedAt[0] = (tick, team); }
                    else { result.Add(new FlagEvent(tick, player, FlagAction.Drop)); droppedAt[enemyFlag] = tick; }
                    break;
                case 1:
                    if (carrier[enemyFlag] == player) { (carrier[enemyFlag], droppedAt[enemyFlag]) = (null, tick); carrierDeaths.Add((tick, player)); }
                    break;
                default:
                    if (used[objIndex]) break;
                    var ownFlag = team;
                    var killedCarrier = killList.Any(k => k.Killer == player && Math.Abs(k.Tick - tick) <= SameMomentTicks
                        && carrierDeaths.Any(c => c.Player == k.Victim && Math.Abs(c.Tick - k.Tick) <= SameMomentTicks));
                    // Records can be missing: the carrier's objective event with no slot record is a capture
                    // (the winning capture ends play before its record), and an otherwise unexplained one
                    // while nobody carries the enemy flag is a pickup with no record.
                    FlagAction? label = killedCarrier ? FlagAction.CarrierKill
                        : capturedAt[0] is { } cap && cap.Team == team && tick - cap.Tick <= SameMomentTicks ? FlagAction.CaptureAssist
                        : carrier[enemyFlag] == player ? FlagAction.Capture
                        : droppedAt[ownFlag] is not null || (returnedAt[ownFlag] is { } r && tick - r <= SameMomentTicks) ? FlagAction.Return
                        : carrier[enemyFlag] is null ? (droppedAt[enemyFlag] is null ? FlagAction.Steal : FlagAction.Grab)
                        : null;
                    if (label is not { } a) break;
                    used[objIndex] = true;
                    result.Add(new FlagEvent(tick, player, a));
                    if (a == FlagAction.Return) { droppedAt[ownFlag] = null; returnedAt[ownFlag] ??= tick; if (tick - returnedAt[ownFlag] > SameMomentTicks) returnedAt[ownFlag] = tick; }
                    if (a == FlagAction.Capture) { carrier[enemyFlag] = null; capturedAt[0] = (tick, team); }
                    if (a is FlagAction.Steal or FlagAction.Grab) (carrier[enemyFlag], droppedAt[enemyFlag]) = (player, null);
                    break;
            }
        }
        return result;
    }
}
