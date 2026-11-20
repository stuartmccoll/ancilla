namespace Ancilla.Core;

/// <summary>Where progress snapshots are kept: the JSON file (<see cref="SnapshotStore"/>) or the local archive.</summary>
public interface ISnapshotStore
{
    Task<IReadOnlyList<ProgressSnapshot>> LoadAsync(CancellationToken ct = default);

    /// <summary>Appends the snapshot if anything changed, and returns the full history.</summary>
    Task<IReadOnlyList<ProgressSnapshot>> AppendAsync(ProgressSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Whether <paramref name="snapshot"/> differs from <paramref name="last"/> enough to be worth keeping.</summary>
    static bool IsNew(ProgressSnapshot? last, ProgressSnapshot snapshot) =>
        last is null || last.MatchesCompleted != snapshot.MatchesCompleted || last.CareerXpEarned != snapshot.CareerXpEarned
        || last.ActivePass != snapshot.ActivePass || last.ActivePassXpEarned != snapshot.ActivePassXpEarned;
}
