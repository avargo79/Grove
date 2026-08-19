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

        ConfigureCommon(psi, args);

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

    /// <summary>
    /// Runs git and reports each line it writes to stderr as it arrives. Network commands report
    /// their progress there, so this is what makes fetch and push observable rather than opaque.
    /// </summary>
    public async Task<GitResult> RunWithProgressAsync(
        IEnumerable<string> args, IProgress<string>? progress, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitPath,
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        ConfigureCommon(psi, args);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new GitException($"Could not start '{gitPath}'.");

        var stdErr = new StringBuilder();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);

        var stdErrTask = Task.Run(async () =>
        {
            // git writes progress as \r-separated updates on one line; ReadLineAsync would only
            // yield when the line finally ends, so read it in chunks and split ourselves.
            var buffer = new char[512];
            var pending = new StringBuilder();

            while (true)
            {
                var read = await process.StandardError.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                stdErr.Append(buffer, 0, read);
                pending.Append(buffer, 0, read);
                EmitCompleteLines(pending, progress);
            }

            var tail = pending.ToString().Trim();
            if (tail.Length > 0)
                progress?.Report(tail);
        }, ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled fetch or push must not leave git running against the repository.
            TryKill(process);
            throw;
        }

        await stdErrTask.ConfigureAwait(false);
        return new GitResult(process.ExitCode, await stdOutTask.ConfigureAwait(false), stdErr.ToString());
    }

    private static void EmitCompleteLines(StringBuilder pending, IProgress<string>? progress)
    {
        if (progress is null)
        {
            pending.Clear();
            return;
        }

        var text = pending.ToString();
        var lastBreak = text.LastIndexOfAny(['\n', '\r']);
        if (lastBreak < 0)
            return;

        foreach (var line in text[..lastBreak].Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                progress.Report(trimmed);
        }

        pending.Clear();
        pending.Append(text[(lastBreak + 1)..]);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Runs git and returns its raw stdout bytes. Text decoding would corrupt binary blobs, so
    /// image previews read through here instead.
    /// </summary>
    public async Task<byte[]?> RunBinaryAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitPath,
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        ConfigureCommon(psi, args);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new GitException($"Could not start '{gitPath}'.");

        using var buffer = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await copyTask.ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);

        return process.ExitCode == 0 ? buffer.ToArray() : null;
    }

    /// <summary>Arguments and environment shared by every invocation.</summary>
    private static void ConfigureCommon(ProcessStartInfo psi, IEnumerable<string> args)
    {
        // Keep git non-interactive and locale-stable so output stays parseable.
        psi.ArgumentList.Add("--no-optional-locks");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=false");
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // No credentials are ever handled in-process: a helper may supply them, but git must
        // never fall back to prompting on a terminal that does not exist.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        // Without these, anything that wants a message (merge, rebase --continue, commit)
        // launches an editor and blocks forever with no visible cause.
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        psi.Environment["EDITOR"] = "true";
    }

    /// <summary>
    /// Runs git with extra environment variables layered over the defaults. Needed where a
    /// variable this class normally pins has to be replaced for one call — the interactive rebase
    /// sequence editor being the case in point, since the environment beats any config setting.
    /// </summary>
    public async Task<GitResult> RunWithEnvironmentAsync(
        IEnumerable<string> args, IReadOnlyDictionary<string, string> environment,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = gitPath,
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        ConfigureCommon(psi, args);
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new GitException($"Could not start '{gitPath}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new GitResult(process.ExitCode, await stdOutTask.ConfigureAwait(false),
            await stdErrTask.ConfigureAwait(false));
    }

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
