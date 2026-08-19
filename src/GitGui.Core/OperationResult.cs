namespace GitGui.Core;

/// <summary>How a repository operation ended.</summary>
public enum OperationOutcome
{
    Succeeded,

    /// <summary>
    /// Stopped on conflicts. Not a failure: the repository is now mid-merge or mid-rebase and the
    /// user is expected to resolve and continue, or abort.
    /// </summary>
    Conflicted,

    /// <summary>Refused or failed outright; the repository is unchanged.</summary>
    Failed,

    /// <summary>The user backed out at a confirmation or prompt. Nothing was attempted.</summary>
    Cancelled,
}

/// <summary>
/// Result of an operation that can legitimately stop part-way. Conflicts are modelled here rather
/// than thrown, because a conflicted merge is a normal thing for a user to be in the middle of.
/// </summary>
public sealed record OperationResult(
    OperationOutcome Outcome,
    string Message,
    IReadOnlyList<string> ConflictedPaths)
{
    public bool Succeeded => Outcome == OperationOutcome.Succeeded;
    public bool Conflicted => Outcome == OperationOutcome.Conflicted;

    public static OperationResult Ok(string message = "") => new(OperationOutcome.Succeeded, message, []);

    public static OperationResult Fail(string message) => new(OperationOutcome.Failed, message, []);

    public static OperationResult Conflict(string message, IReadOnlyList<string> paths) =>
        new(OperationOutcome.Conflicted, message, paths);

    /// <summary>The user declined. Distinct from failure: nothing went wrong and nothing happened.</summary>
    public static OperationResult Cancelled { get; } = new(OperationOutcome.Cancelled, string.Empty, []);
}

/// <summary>A multi-step operation the repository is part-way through.</summary>
public enum RepositoryOperation { None, Merge, Rebase, CherryPick, Revert, Bisect }

/// <summary>
/// Whether the repository is mid-operation and what still conflicts, so the UI can offer
/// "continue" or "abort" instead of pretending everything is normal.
/// </summary>
public sealed record RepositoryState(
    RepositoryOperation Operation,
    IReadOnlyList<string> ConflictedPaths)
{
    public bool IsInProgress => Operation != RepositoryOperation.None;
    public bool HasConflicts => ConflictedPaths.Count > 0;

    /// <summary>Human-readable label for the banner, e.g. "Merge in progress".</summary>
    public string Description => Operation switch
    {
        RepositoryOperation.Merge => "Merge in progress",
        RepositoryOperation.Rebase => "Rebase in progress",
        RepositoryOperation.CherryPick => "Cherry-pick in progress",
        RepositoryOperation.Revert => "Revert in progress",
        RepositoryOperation.Bisect => "Bisect in progress",
        _ => string.Empty,
    };

    public static RepositoryState Clean { get; } = new(RepositoryOperation.None, []);
}
