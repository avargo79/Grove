using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitFork.Core;

namespace GitFork.App.ViewModels;

/// <summary>One line of a file being previewed, with its syntax colouring.</summary>
public sealed class FileLineViewModel
{
    public required int Number { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<DiffRun> Runs { get; init; }

    public string NumberDisplay =>
        Number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5);
}

/// <summary>A folder or file in the tree at some revision.</summary>
public sealed partial class TreeNodeViewModel : ViewModelBase
{
    public required string Name { get; init; }

    /// <summary>Full path from the repository root; empty for the synthetic root folders.</summary>
    public required string Path { get; init; }

    public required bool IsDirectory { get; init; }

    public long Size { get; init; }

    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string SizeDisplay => IsDirectory
        ? $"{CountFiles(this)} files"
        : FormatSize(Size);

    private static int CountFiles(TreeNodeViewModel node) =>
        node.Children.Sum(c => c.IsDirectory ? CountFiles(c) : 1);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };
}

/// <summary>
/// The repository's files as of a revision.
///
/// Git reports the tree as a flat list of paths, so the nesting here is built rather than read;
/// that is the whole job of this view model.
/// </summary>
public sealed partial class FileTreeViewModel(GitRepository repository) : ViewModelBase
{
    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    public ObservableCollection<FileLineViewModel> ContentLines { get; } = [];

    [ObservableProperty]
    public partial string Revision { get; set; } = "HEAD";

    [ObservableProperty]
    public partial TreeNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ContentMessage { get; set; }

    public string Title => $"Files at {Short(Revision)}";

    public bool HasContentMessage => ContentMessage is not null;

    /// <summary>Exposed for tests, which need to await the fire-and-forget content load.</summary>
    internal Task PendingContentLoad { get; private set; } = Task.CompletedTask;

    public async Task LoadAsync(string revision = "HEAD", CancellationToken ct = default)
    {
        Revision = revision;
        OnPropertyChanged(nameof(Title));

        Roots.Clear();
        ContentLines.Clear();

        var entries = await repository.Files.GetTreeAsync(revision, ct).ConfigureAwait(true);
        if (entries.Count == 0)
        {
            StatusText = "Nothing in this revision.";
            return;
        }

        Build(entries);

        var bytes = entries.Sum(e => e.Size);
        StatusText = $"{entries.Count:N0} files · {FormatTotal(bytes)}";
    }

    /// <summary>Turns the flat path list into folders and files.</summary>
    private void Build(IReadOnlyList<TreeEntry> entries)
    {
        var folders = new Dictionary<string, TreeNodeViewModel>(StringComparer.Ordinal);

        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            var segments = entry.Path.Split('/');
            var parentChildren = Roots;
            var prefix = string.Empty;

            // Walk the path, creating any folder that does not exist yet.
            for (var i = 0; i < segments.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? segments[i] : $"{prefix}/{segments[i]}";

                if (!folders.TryGetValue(prefix, out var folder))
                {
                    folder = new TreeNodeViewModel
                    {
                        Name = segments[i],
                        Path = prefix,
                        IsDirectory = true,
                    };

                    folders[prefix] = folder;
                    Insert(parentChildren, folder);
                }

                parentChildren = folder.Children;
            }

            Insert(parentChildren, new TreeNodeViewModel
            {
                Name = segments[^1],
                Path = entry.Path,
                IsDirectory = false,
                Size = entry.Size,
            });
        }
    }

    /// <summary>Folders before files, then alphabetically — the order a file browser uses.</summary>
    private static void Insert(ObservableCollection<TreeNodeViewModel> siblings, TreeNodeViewModel node)
    {
        var index = 0;
        while (index < siblings.Count)
        {
            var other = siblings[index];
            if (other.IsDirectory != node.IsDirectory)
            {
                if (node.IsDirectory)
                    break;
            }
            else if (string.Compare(other.Name, node.Name, StringComparison.OrdinalIgnoreCase) > 0)
            {
                break;
            }

            index++;
        }

        siblings.Insert(index, node);
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        PendingContentLoad = LoadContentAsync(value);
    }

    private async Task LoadContentAsync(TreeNodeViewModel? node)
    {
        ContentLines.Clear();
        SetMessage(null);

        if (node is null)
        {
            SetMessage("Select a file to see its contents.");
            return;
        }

        if (node.IsDirectory)
            return;

        if (GitFileOperations.IsImagePath(node.Path))
        {
            SetMessage("Image file — open the commit that changed it to see it.");
            return;
        }

        var bytes = await repository.Files.GetBlobAsync(Revision, node.Path).ConfigureAwait(true);
        if (bytes is null)
        {
            SetMessage("This file could not be read.");
            return;
        }

        // A NUL byte near the start is how git itself decides a file is binary.
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0)
        {
            SetMessage($"Binary file — {node.SizeDisplay}.");
            return;
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');

        // A trailing newline produces one empty final element that is not a real line.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        for (var i = 0; i < count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            ContentLines.Add(new FileLineViewModel
            {
                Number = i + 1,
                Text = line,
                Runs = DiffRunBuilder.BuildPlain(line, node.Path),
            });
        }
    }

    private void SetMessage(string? message)
    {
        ContentMessage = message;
        OnPropertyChanged(nameof(HasContentMessage));
    }

    private static string Short(string revision) =>
        revision.Length == 40 ? revision[..7] : revision;

    private static string FormatTotal(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };
}
