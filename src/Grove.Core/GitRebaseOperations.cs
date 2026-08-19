using System.Text;

namespace Grove.Core;

/// <summary>What an interactive rebase should do with one commit.</summary>
public enum RebaseAction
{
    /// <summary>Keep the commit as it is.</summary>
    Pick,

    /// <summary>
    /// Stop so the message can be rewritten. Written to the plan as <c>edit</c> rather than
    /// <c>reword</c>: git's own reword opens an editor, and with editors suppressed that would
    /// silently keep the original message. Stopping hands the job to the commit box instead.
    /// </summary>
    Reword,

    /// <summary>Stop so the commit's content can be amended.</summary>
    Edit,

    /// <summary>Combine into the previous commit, keeping both messages.</summary>
    Squash,

    /// <summary>Combine into the previous commit, discarding this message.</summary>
    Fixup,

    /// <summary>Leave the commit out entirely.</summary>
    Drop,
}

/// <summary>One line of the rebase plan.</summary>
public sealed record RebaseTodoItem(RebaseAction Action, string Sha, string Subject)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    /// <summary>The keyword git expects in the plan file.</summary>
    public string Keyword => Action switch
    {
        // See RebaseAction.Reword for why this is not "reword".
        RebaseAction.Reword or RebaseAction.Edit => "edit",
        RebaseAction.Squash => "squash",
        RebaseAction.Fixup => "fixup",
        RebaseAction.Drop => "drop",
        _ => "pick",
    };
}

/// <summary>
/// Interactive rebase, driven without an editor.
///
/// Git expects a human to edit the plan file it writes. Rather than opening one, GIT_SEQUENCE_EDITOR
/// is pointed at a generated script whose only job is to overwrite that file with the plan the user
/// built in the UI.
/// </summary>
public sealed class GitRebaseOperations(GitCommandRunner git)
{
    /// <summary>The commits that an interactive rebase onto <paramref name="upstream"/> would replay.</summary>
    public async Task<IReadOnlyList<RebaseTodoItem>> GetTodoAsync(
        string upstream, CancellationToken ct = default)
    {
        // Oldest first, which is the order the plan file uses.
        var result = await git
            .RunAsync(ct, "log", "--reverse", "--no-merges", "--format=%H%x1f%s", $"{upstream}..HEAD")
            .ConfigureAwait(false);

        if (!result.Success)
            return [];

        var items = new List<RebaseTodoItem>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\u001F');
            if (fields.Length >= 2)
                items.Add(new RebaseTodoItem(RebaseAction.Pick, fields[0], fields[1]));
        }

        return items;
    }

    /// <summary>
    /// Runs the rebase with the given plan. A conflict, or a commit marked to stop at, leaves the
    /// repository mid-rebase for the usual continue/abort handling.
    /// </summary>
    public async Task<OperationResult> RunInteractiveAsync(
        string upstream, IReadOnlyList<RebaseTodoItem> todo, CancellationToken ct = default)
    {
        var kept = todo.Where(i => i.Action != RebaseAction.Drop).ToList();
        if (kept.Count == 0)
            return OperationResult.Fail("Every commit is marked to be dropped.");

        // Squash and fixup fold into the commit above, so the first line cannot be one.
        if (kept[0].Action is RebaseAction.Squash or RebaseAction.Fixup)
            return OperationResult.Fail("The first commit has nothing above it to be combined into.");

        var directory = Directory.CreateTempSubdirectory("grove-rebase");
        try
        {
            var todoPath = Path.Combine(directory.FullName, "todo");
            await File.WriteAllTextAsync(todoPath, BuildTodoFile(kept), ct).ConfigureAwait(false);

            // git runs the sequence editor through a shell and appends its own plan file as the
            // argument, so "cp <ours>" is the entire program: no script file, no exec bit, and
            // one code path on every platform Git itself runs on.
            var editor = $"cp \"{todoPath}\"";

            // GIT_SEQUENCE_EDITOR beats the sequence.editor config setting, and this class pins
            // it to "true" everywhere else — so it has to be replaced here, not configured around.
            var result = await git
                .RunWithEnvironmentAsync(
                    ["rebase", "--interactive", upstream],
                    new Dictionary<string, string> { ["GIT_SEQUENCE_EDITOR"] = editor },
                    ct)
                .ConfigureAwait(false);

            // Exit code alone is not enough: stopping at an "edit" is a deliberate pause and git
            // reports it as success, so the repository state has to be consulted either way.
            var state = await new GitHistoryOperations(git).GetStateAsync(ct).ConfigureAwait(false);

            if (state.HasConflicts)
            {
                var noun = state.ConflictedPaths.Count == 1 ? "file" : "files";
                return OperationResult.Conflict(
                    $"Rebase stopped with conflicts in {state.ConflictedPaths.Count} {noun}.",
                    state.ConflictedPaths);
            }

            if (state.Operation == RepositoryOperation.Rebase)
                return OperationResult.Conflict("Rebase stopped so you can amend this commit.", []);

            return result.Success
                ? OperationResult.Ok("Rebase complete.")
                : GitRefOperations.Interpret(result, "Rebase complete.");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>The plan file git would otherwise have opened in an editor.</summary>
    internal static string BuildTodoFile(IReadOnlyList<RebaseTodoItem> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
            builder.Append(item.Keyword).Append(' ').Append(item.Sha).Append(' ').Append(item.Subject).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// The GIT_SEQUENCE_EDITOR value for a given plan file. Exposed so the quoting can be asserted
    /// on directly — a path with a space in it is the case that breaks silently.
    /// </summary>
    internal static string BuildSequenceEditorCommand(string todoPath) => $"cp \"{todoPath}\"";

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing an operation over.
        }
    }
}
