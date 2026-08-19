using System.Diagnostics;

namespace GitFork.Core.Tests;

/// <summary>
/// A throwaway git repository in a temp directory. Integration tests run against real git output
/// rather than fixtures, so a change in git's porcelain format shows up as a failing test.
/// </summary>
public sealed class TestRepository : IDisposable
{
    public string Path { get; }

    private TestRepository(string path) => Path = path;

    public static TestRepository CreateEmpty()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitfork-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        var repo = new TestRepository(path);
        repo.Git("init", "--quiet", "--initial-branch=main");
        // Identity and signing must be pinned so tests never depend on the developer's global config.
        repo.Git("config", "user.name", "Test Author");
        repo.Git("config", "user.email", "test@example.com");
        repo.Git("config", "commit.gpgsign", "false");
        repo.Git("config", "gc.auto", "0");
        return repo;
    }

    /// <summary>
    /// A bare repository to act as a remote. Using a local path exercises the whole fetch/push
    /// code path without needing a server or any credentials.
    /// </summary>
    public static TestRepository CreateBareRemote()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitfork-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        var repo = new TestRepository(path);
        repo.Git("init", "--quiet", "--bare", "--initial-branch=main");
        return repo;
    }

    /// <summary>Points this repository at another one as "origin" and fetches from it.</summary>
    public void AddRemote(TestRepository remote, string name = "origin")
    {
        Git("remote", "add", name, remote.Path);
        Git("fetch", "--quiet", name);
    }

    /// <summary>Runs git in the repository and fails the test if it errors.</summary>
    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_DATE"] = "2024-01-01T12:00:00+00:00";
        psi.Environment["GIT_COMMITTER_DATE"] = "2024-01-01T12:00:00+00:00";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");

        return stdout;
    }

    public void WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void DeleteFile(string relativePath) =>
        File.Delete(System.IO.Path.Combine(Path, relativePath));

    /// <summary>Writes a file, stages everything and commits. Returns the new commit's sha.</summary>
    public string Commit(string message, string relativePath, string content)
    {
        WriteFile(relativePath, content);
        Git("add", "-A");
        Git("commit", "--quiet", "-m", message);
        return Head();
    }

    public string CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "--quiet", "-m", message);
        return Head();
    }

    public string Head() => Git("rev-parse", "HEAD").Trim();

    /// <summary>Content of a path as currently staged in the index.</summary>
    public string IndexContent(string relativePath) => Git("show", $":{relativePath}");

    /// <summary>Content of a path in the working tree.</summary>
    public string WorkingContent(string relativePath) =>
        File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    /// <summary>Content of a path as of HEAD.</summary>
    public string HeadContent(string relativePath) => Git("show", $"HEAD:{relativePath}");

    public int CommitCount() => int.Parse(Git("rev-list", "--count", "HEAD").Trim(), System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
