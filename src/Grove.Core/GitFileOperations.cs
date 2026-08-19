using System.Globalization;

namespace Grove.Core;

/// <summary>One line of a file, attributed to the commit that last changed it.</summary>
public sealed record BlameLine(
    int LineNumber,
    string Sha,
    string Author,
    DateTimeOffset Date,
    string Summary,
    string Text)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    /// <summary>True for content that is not committed yet, which git reports as an all-zero sha.</summary>
    public bool IsUncommitted => Sha.All(c => c == '0');
}

/// <summary>An entry in the tree at some revision.</summary>
public sealed record TreeEntry(string Path, string Mode, string ObjectId, bool IsDirectory, long Size)
{
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
}

/// <summary>Reading files as git sees them: blame, per-path history, trees and raw blobs.</summary>
public sealed class GitFileOperations(GitCommandRunner git)
{
    private const char Sep = '\u001F';

    /// <summary>Image formats worth showing as a picture rather than as a failed text diff.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
    };

    public static bool IsImagePath(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    // -------------------------------------------------------------- blame

    /// <summary>
    /// Per-line attribution for a file at a revision. Uses the porcelain format, which repeats a
    /// commit's details only the first time it appears, so headers are cached as they arrive.
    /// </summary>
    public async Task<IReadOnlyList<BlameLine>> GetBlameAsync(
        string path, string? revision = null, CancellationToken ct = default)
    {
        var args = new List<string> { "blame", "--porcelain" };
        if (!string.IsNullOrEmpty(revision))
            args.Add(revision);
        args.Add("--");
        args.Add(path);

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        if (!result.Success)
            return [];

        return ParseBlamePorcelain(result.StdOut);
    }

    internal static IReadOnlyList<BlameLine> ParseBlamePorcelain(string output)
    {
        var lines = new List<BlameLine>();

        // Commit details appear once and apply to every later mention of that sha.
        var authors = new Dictionary<string, string>(StringComparer.Ordinal);
        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);

        string? sha = null;
        var lineNumber = 0;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith('\t'))
            {
                // The content line closes the current entry.
                if (sha is not null)
                {
                    lines.Add(new BlameLine(
                        LineNumber: lineNumber,
                        Sha: sha,
                        Author: authors.GetValueOrDefault(sha, string.Empty),
                        Date: dates.GetValueOrDefault(sha, DateTimeOffset.MinValue),
                        Summary: summaries.GetValueOrDefault(sha, string.Empty),
                        Text: line[1..]));
                }

                sha = null;
                continue;
            }

            if (line.Length == 0)
                continue;

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];

            // A header line starts a new entry: "<sha> <origLine> <finalLine> [<count>]".
            if (key.Length == 40 && key.All(Uri.IsHexDigit))
            {
                sha = key;
                var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var final))
                    lineNumber = final;
                continue;
            }

            if (sha is null)
                continue;

            switch (key)
            {
                case "author":
                    authors[sha] = value;
                    break;
                case "author-time" when long.TryParse(value, CultureInfo.InvariantCulture, out var seconds):
                    dates[sha] = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    break;
                case "summary":
                    summaries[sha] = value;
                    break;
                default:
                    break;
            }
        }

        return lines;
    }

    // ------------------------------------------------------- file history

    /// <summary>
    /// Commits that touched one path, following it through renames. Returns the same
    /// <see cref="Commit"/> shape as the main history so the graph and detail pane can reuse it.
    /// </summary>
    public async Task<IReadOnlyList<Commit>> GetFileHistoryAsync(
        string path, int maxCount = 200, bool followRenames = true, CancellationToken ct = default)
    {
        var format = string.Join(Sep, "%H", "%P", "%an", "%ae", "%aI", "%cn", "%cI", "%D", "%s");

        var args = new List<string>
        {
            "log", $"--pretty=format:{format}", $"--max-count={maxCount}",
        };

        // --follow only works for a single path, which is exactly this case.
        if (followRenames)
            args.Add("--follow");

        args.Add("--");
        args.Add(path);

        var result = await git.RunAsync(args, ct).ConfigureAwait(false);
        if (!result.Success)
            return [];

        var commits = new List<Commit>();
        foreach (var line in result.StdOut.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            var f = line.Split(Sep);
            if (f.Length < 9)
                continue;

            commits.Add(new Commit(
                Sha: f[0],
                ParentShas: f[1].Length == 0 ? [] : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthorName: f[2],
                AuthorEmail: f[3],
                AuthorDate: ParseDate(f[4]),
                CommitterName: f[5],
                CommitDate: ParseDate(f[6]),
                Subject: f[8],
                RefNames: GitRepository.ParseDecoration(f[7])));
        }

        return commits;
    }

    // --------------------------------------------------------------- tree

    /// <summary>Every file in the tree at a revision, with its blob size.</summary>
    public async Task<IReadOnlyList<TreeEntry>> GetTreeAsync(
        string revision = "HEAD", CancellationToken ct = default)
    {
        var result = await git
            .RunAsync(ct, "ls-tree", "-r", "--long", "-z", revision)
            .ConfigureAwait(false);

        if (!result.Success)
            return [];

        var entries = new List<TreeEntry>();
        foreach (var record in result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <type> <object> <size>\t<path>"
            var tab = record.IndexOf('\t');
            if (tab < 0)
                continue;

            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4)
                continue;

            entries.Add(new TreeEntry(
                Path: record[(tab + 1)..],
                Mode: fields[0],
                ObjectId: fields[2],
                IsDirectory: fields[1] == "tree",
                Size: long.TryParse(fields[3], CultureInfo.InvariantCulture, out var size) ? size : 0));
        }

        return entries;
    }

    // -------------------------------------------------------------- blobs

    /// <summary>Raw bytes of a file at a revision, for image previews.</summary>
    public async Task<byte[]?> GetBlobAsync(string revision, string path, CancellationToken ct = default)
    {
        var result = await git.RunBinaryAsync(["show", $"{revision}:{path}"], ct).ConfigureAwait(false);
        return result;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : DateTimeOffset.MinValue;
}
