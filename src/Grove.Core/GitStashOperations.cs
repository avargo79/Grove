using System.Globalization;

namespace Grove.Core;

/// <summary>One entry from the stash list.</summary>
public sealed record StashEntry(int Index, string Reference, string Message, string Branch, DateTimeOffset Date)
{
    /// <summary>
    /// The message without git's branch prefix. A bare stash reads "WIP on main: sha subject";
    /// one created with a description reads "On main: the description".
    /// </summary>
    public string DisplayMessage
    {
        get
        {
            string[] prefixes = ["WIP on ", "On "];
            foreach (var prefix in prefixes)
            {
                if (!Message.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var colon = Message.IndexOf(':');
                return colon > 0 ? Message[(colon + 1)..].Trim() : Message;
            }

            return Message;
        }
    }
}

/// <summary>Stash lifecycle, plus a diff for previewing one before applying it.</summary>
public sealed class GitStashOperations(GitCommandRunner git)
{
    private const char Sep = '\u001F';

    public async Task<IReadOnlyList<StashEntry>> GetStashesAsync(CancellationToken ct = default)
    {
        var format = string.Join(Sep, "%gd", "%gs", "%aI");
        var result = await git.RunAsync(ct, "stash", "list", $"--format={format}").ConfigureAwait(false);
        if (!result.Success)
            return [];

        var entries = new List<StashEntry>();
        var index = 0;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(Sep);
            if (f.Length < 3)
                continue;

            entries.Add(new StashEntry(
                Index: index,
                Reference: f[0],
                Message: f[1],
                Branch: ExtractBranch(f[1]),
                Date: DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTimeOffset.MinValue));
            index++;
        }

        return entries;
    }

    /// <summary>Stashes the working tree. Untracked files are only included when asked for.</summary>
    public async Task<OperationResult> PushAsync(
        string? message = null, bool includeUntracked = false, bool keepIndex = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked)
            args.Add("--include-untracked");
        if (keepIndex)
            args.Add("--keep-index");
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("--message");
            args.Add(message);
        }

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);

        // Stashing a clean tree is a no-op that git reports as success with a notice.
        if (result.Success && result.StdOut.Contains("No local changes", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail("There is nothing to stash.");

        return GitRefOperations.Interpret(result, "Stashed the working tree.");
    }

    /// <summary>Applies a stash and leaves it on the stack.</summary>
    public Task<OperationResult> ApplyAsync(string reference, CancellationToken ct = default) =>
        ApplyInternalAsync("apply", reference, $"Applied {reference}.", ct);

    /// <summary>Applies a stash and removes it from the stack.</summary>
    public Task<OperationResult> PopAsync(string reference, CancellationToken ct = default) =>
        ApplyInternalAsync("pop", reference, $"Popped {reference}.", ct);

    /// <summary>Discards a stash. Unrecoverable, so callers must confirm with the user first.</summary>
    public async Task<OperationResult> DropAsync(string reference, CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "stash", "drop", reference).ConfigureAwait(false);
        return GitRefOperations.Interpret(result, $"Dropped {reference}.");
    }

    /// <summary>Diff of a stash's contents, for previewing before applying it.</summary>
    public async Task<IReadOnlyList<FileDiff>> GetStashDiffAsync(
        string reference, CancellationToken ct = default)
    {
        var result = await git
            .RunAsync(ct, "stash", "show", "--patch", "--no-color", "--include-untracked", reference)
            .ConfigureAwait(false);

        return result.Success ? DiffParser.ParseFiles(result.StdOut) : [];
    }

    private async Task<OperationResult> ApplyInternalAsync(
        string command, string reference, string successMessage, CancellationToken ct)
    {
        var result = await git.RunAsync(ct, "stash", command, reference).ConfigureAwait(false);
        if (result.Success)
            return OperationResult.Ok(successMessage);

        // Applying against committed work that touches the same lines leaves conflict markers.
        var conflicts = await new GitHistoryOperations(git).GetConflictedPathsAsync(ct).ConfigureAwait(false);
        if (conflicts.Count > 0)
            return OperationResult.Conflict("The stash conflicts with your current changes.", conflicts);

        // Applying over *uncommitted* edits to the same file is refused before anything happens,
        // and git's own wording ("would be overwritten by merge") does not say what to do about it.
        if (result.StdErr.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
            return OperationResult.Fail(
                "Your uncommitted changes would be overwritten. Commit or stash them first.");

        return GitRefOperations.Interpret(result, successMessage);
    }

    /// <summary>Reads the branch name out of git's "WIP on main: ..." message.</summary>
    private static string ExtractBranch(string message)
    {
        string[] prefixes = ["WIP on ", "On "];
        foreach (var prefix in prefixes)
        {
            if (!message.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = message[prefix.Length..];
            var colon = rest.IndexOf(':');
            return colon > 0 ? rest[..colon] : rest;
        }

        return string.Empty;
    }
}
