namespace GitGui.Core;

/// <summary>The three kinds of branch git-flow defines.</summary>
public enum FlowBranchKind { Feature, Release, Hotfix }

/// <summary>
/// Branch prefixes and the two long-lived branches. Read from git config where the git-flow
/// extension has set them, so an existing repository keeps its own conventions.
/// </summary>
public sealed record GitFlowConfig(
    string Develop = "develop",
    string Main = "main",
    string FeaturePrefix = "feature/",
    string ReleasePrefix = "release/",
    string HotfixPrefix = "hotfix/")
{
    public string PrefixFor(FlowBranchKind kind) => kind switch
    {
        FlowBranchKind.Release => ReleasePrefix,
        FlowBranchKind.Hotfix => HotfixPrefix,
        _ => FeaturePrefix,
    };

    /// <summary>Where a branch of this kind starts from.</summary>
    public string BaseFor(FlowBranchKind kind) => kind == FlowBranchKind.Hotfix ? Main : Develop;
}

/// <summary>
/// Git-flow as ordinary branch and merge operations, so no extension needs to be installed. The
/// prefixes come from the repository's own config where the extension has already set them.
/// </summary>
public sealed class GitFlowOperations(GitCommandRunner git, GitRefOperations refs, GitHistoryOperations history)
{
    /// <summary>Reads the repository's git-flow config, falling back to the usual defaults.</summary>
    public async Task<GitFlowConfig> GetConfigAsync(CancellationToken ct = default)
    {
        var result = await git.RunAsync(ct, "config", "--get-regexp", "^gitflow\\.").ConfigureAwait(false);
        if (!result.Success)
            return new GitFlowConfig(Main: await GuessMainAsync(ct).ConfigureAwait(false));

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ');
            if (space > 0)
                values[line[..space]] = line[(space + 1)..].Trim();
        }

        return new GitFlowConfig(
            Develop: values.GetValueOrDefault("gitflow.branch.develop", "develop"),
            Main: values.GetValueOrDefault("gitflow.branch.master", await GuessMainAsync(ct).ConfigureAwait(false)),
            FeaturePrefix: values.GetValueOrDefault("gitflow.prefix.feature", "feature/"),
            ReleasePrefix: values.GetValueOrDefault("gitflow.prefix.release", "release/"),
            HotfixPrefix: values.GetValueOrDefault("gitflow.prefix.hotfix", "hotfix/"));
    }

    /// <summary>Starts a branch of the given kind from its proper base.</summary>
    public async Task<OperationResult> StartAsync(
        FlowBranchKind kind, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult.Fail("A name is required.");

        var config = await GetConfigAsync(ct).ConfigureAwait(false);
        var start = config.BaseFor(kind);

        // Starting from a base that does not exist would silently branch from HEAD instead.
        var exists = await git.RunAsync(ct, "rev-parse", "--verify", "--quiet", start).ConfigureAwait(false);
        if (!exists.Success)
            return OperationResult.Fail($"'{start}' does not exist, so there is nothing to start from.");

        return await refs
            .CreateBranchAsync(config.PrefixFor(kind) + name.Trim(), start, checkout: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes a branch: merges it back into wherever it belongs, then deletes it. A release or
    /// hotfix goes into both the main branch and develop, which is the whole point of the model.
    /// </summary>
    public async Task<OperationResult> FinishAsync(
        FlowBranchKind kind, string name, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct).ConfigureAwait(false);
        var branch = name.StartsWith(config.PrefixFor(kind), StringComparison.Ordinal)
            ? name
            : config.PrefixFor(kind) + name.Trim();

        var targets = kind == FlowBranchKind.Feature
            ? new[] { config.Develop }
            : [config.Main, config.Develop];

        foreach (var target in targets)
        {
            var exists = await git.RunAsync(ct, "rev-parse", "--verify", "--quiet", target).ConfigureAwait(false);
            if (!exists.Success)
                continue;

            var checkout = await refs.CheckoutBranchAsync(target, ct).ConfigureAwait(false);
            if (!checkout.Succeeded)
                return checkout;

            var merge = await history.MergeAsync(branch, noFastForward: true, ct).ConfigureAwait(false);

            // Stop on conflicts rather than carrying on to the next target in a broken state.
            if (!merge.Succeeded)
                return merge;
        }

        var delete = await refs.DeleteBranchAsync(branch, force: false, ct).ConfigureAwait(false);
        return delete.Succeeded
            ? OperationResult.Ok($"Finished '{branch}'.")
            : OperationResult.Ok($"Merged '{branch}', but it could not be deleted: {delete.Message}");
    }

    /// <summary>Existing branches of one kind, without their prefix.</summary>
    public async Task<IReadOnlyList<string>> GetBranchesAsync(
        FlowBranchKind kind, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct).ConfigureAwait(false);
        var prefix = config.PrefixFor(kind);

        var result = await git
            .RunAsync(ct, "for-each-ref", "--format=%(refname:short)", $"refs/heads/{prefix}*")
            .ConfigureAwait(false);

        if (!result.Success)
            return [];

        return
        [
            .. result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Trim()[prefix.Length..]),
        ];
    }

    /// <summary>Whichever of "main" or "master" this repository actually uses.</summary>
    private async Task<string> GuessMainAsync(CancellationToken ct)
    {
        var main = await git.RunAsync(ct, "rev-parse", "--verify", "--quiet", "refs/heads/main").ConfigureAwait(false);
        return main.Success ? "main" : "master";
    }
}
