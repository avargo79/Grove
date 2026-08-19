using System.Diagnostics;
using System.Text;

namespace GitFork.Core;

/// <summary>Result of a single git invocation.</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string EnsureSuccess(string context)
    {
        if (!Success)
            throw new GitException($"{context} failed (exit {ExitCode}): {StdErr.Trim()}");
        return StdOut;
    }
}

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Thin async wrapper around the installed git binary. Everything in Core goes through here so
/// behaviour always matches the user's real git (credential helpers, hooks, config, LFS included).
/// </summary>
public sealed class GitCommandRunner(string workingDirectory, string gitPath = "git")
{
    public string WorkingDirectory { get; } = workingDirectory;

    public Task<GitResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default) =>
        RunAsync(args, standardInput: null, ct);

    /// <summary>
    /// Runs git, optionally piping <paramref name="standardInput"/> to it. Patches are fed this way
    /// rather than through a temporary file so nothing is left behind if the process is cancelled.
    /// </summary>
    public async Task<GitResult> RunAsync(
        IEnumerable<string> args, string? standardInput, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitPath,
            WorkingDirectory = WorkingDirectory,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = standardInput is not null ? new UTF8Encoding(false) : null,
        };

        // Keep git non-interactive and locale-stable so output stays parseable.
        psi.ArgumentList.Add("--no-optional-locks");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=false");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new GitException($"Could not start '{gitPath}'.");

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new GitResult(process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false));
    }

    public Task<GitResult> RunAsync(CancellationToken ct, params string[] args) => RunAsync(args, ct);

    /// <summary>Walks up from <paramref name="path"/> to find the enclosing work tree root.</summary>
    public static async Task<string?> DiscoverRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;
        if (!Directory.Exists(path))
            return null;

        var runner = new GitCommandRunner(path);
        var result = await runner.RunAsync(ct, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (!result.Success)
            return null;

        var root = result.StdOut.Trim();
        return string.IsNullOrEmpty(root) ? null : root;
    }
}
