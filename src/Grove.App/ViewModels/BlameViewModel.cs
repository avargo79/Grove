using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Grove.Core;

namespace Grove.App.ViewModels;

/// <summary>One blamed line, with the attribution shown only where it changes.</summary>
public sealed class BlameLineViewModel
{
    public required BlameLine Line { get; init; }

    /// <summary>
    /// False for a line whose commit matches the line above. Repeating the same sha down a whole
    /// block is noise; showing it once marks where authorship actually changes.
    /// </summary>
    public required bool StartsBlock { get; init; }

    public required IReadOnlyList<DiffRun> Runs { get; init; }

    public string Number => Line.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(5);
    public string Sha => StartsBlock ? Line.ShortSha : string.Empty;
    public string Author => StartsBlock ? Line.Author : string.Empty;
    public string Date => StartsBlock && Line.Date != DateTimeOffset.MinValue
        ? Line.Date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        : string.Empty;

    public string ToolTip => Line.IsUncommitted
        ? "Not committed yet"
        : $"{Line.ShortSha}  {Line.Author}  {Line.Summary}";

    public bool IsUncommitted => Line.IsUncommitted;
}

/// <summary>Per-line attribution for one file at one revision.</summary>
public sealed partial class BlameViewModel(GitRepository repository) : ViewModelBase
{
    public ObservableCollection<BlameLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Revision { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public string Title => Revision is null
        ? $"Blame — {Path}"
        : $"Blame — {Path} at {Short(Revision)}";

    public async Task LoadAsync(string path, string? revision = null, CancellationToken ct = default)
    {
        Path = path;
        Revision = revision;
        OnPropertyChanged(nameof(Title));

        Lines.Clear();

        var blame = await repository.Files.GetBlameAsync(path, revision, ct).ConfigureAwait(true);
        if (blame.Count == 0)
        {
            StatusText = "No blame information for this file.";
            return;
        }

        string? previousSha = null;
        foreach (var line in blame)
        {
            Lines.Add(new BlameLineViewModel
            {
                Line = line,
                StartsBlock = line.Sha != previousSha,
                Runs = DiffRunBuilder.BuildPlain(line.Text, path),
            });

            previousSha = line.Sha;
        }

        var commits = blame.Select(l => l.Sha).Distinct().Count();
        StatusText = $"{blame.Count:N0} lines from {commits:N0} commits";
    }

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
