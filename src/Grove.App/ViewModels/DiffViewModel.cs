using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Grove.Core;

namespace Grove.App.ViewModels;

/// <summary>How the diff is laid out.</summary>
public enum DiffViewMode { Unified, SideBySide }

/// <summary>One line of the unified view, with its syntax and word-level runs already built.</summary>
public sealed class UnifiedRowViewModel
{
    private const int GutterWidth = 5;

    public required DiffLineKind Kind { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<DiffRun> Runs { get; init; }
    public int? OldLineNumber { get; init; }
    public int? NewLineNumber { get; init; }

    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunkHeader => Kind == DiffLineKind.HunkHeader;
    public bool IsHeader => Kind is DiffLineKind.Header or DiffLineKind.NoNewline;

    public string OldNumber => Format(OldLineNumber);
    public string NewNumber => Format(NewLineNumber);

    public string Marker => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };

    internal static string Format(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(GutterWidth)
        ?? new string(' ', GutterWidth);
}

/// <summary>One side of a side-by-side row.</summary>
public sealed class SideCellViewModel
{
    public required SideKind Kind { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<DiffRun> Runs { get; init; }
    public int? LineNumber { get; init; }

    public bool IsEmpty => Kind == SideKind.Empty;
    public bool IsAdded => Kind == SideKind.Added;
    public bool IsRemoved => Kind == SideKind.Removed;

    public string Number => UnifiedRowViewModel.Format(LineNumber);

    public static SideCellViewModel Empty { get; } =
        new() { Kind = SideKind.Empty, Text = string.Empty, Runs = [] };
}

/// <summary>One row of the side-by-side view.</summary>
public sealed class SideBySideRowViewModel
{
    public required SideCellViewModel Left { get; init; }
    public required SideCellViewModel Right { get; init; }
    public bool IsHunkHeader { get; init; }
    public string HunkHeader { get; init; } = string.Empty;
}

/// <summary>
/// The diff pane: renders one file's diff either unified or side by side, with word-level and
/// syntax colouring, and owns the presentation options that change what git is asked for.
/// </summary>
public sealed partial class DiffViewModel : ViewModelBase, IDisposable
{
    /// <summary>Raised when an option changes in a way that needs the diff re-read from git.</summary>
    public event EventHandler? OptionsChanged;

    public ObservableCollection<UnifiedRowViewModel> UnifiedRows { get; } = [];
    public ObservableCollection<SideBySideRowViewModel> SideBySideRows { get; } = [];

    [ObservableProperty]
    public partial DiffViewMode Mode { get; set; } = DiffViewMode.Unified;

    [ObservableProperty]
    public partial int ContextLines { get; set; } = 3;

    [ObservableProperty]
    public partial WhitespaceMode Whitespace { get; set; } = WhitespaceMode.Show;

    /// <summary>
    /// The same setting as <see cref="Whitespace"/>, as the index a ComboBox binds to. The enum
    /// order is the dropdown order, and the setter clamps because a bound index can arrive as -1
    /// while the list is being rebuilt.
    /// </summary>
    public int WhitespaceIndex
    {
        get => (int)Whitespace;
        set => Whitespace = (WhitespaceMode)Math.Clamp(value, 0, 3);
    }

    [ObservableProperty]
    public partial bool ShowSyntaxHighlighting { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWordHighlighting { get; set; } = true;

    /// <summary>Set when the file is an image, so the pane shows pictures instead of text.</summary>
    [ObservableProperty]
    public partial ImageDiffViewModel? Image { get; set; }

    [ObservableProperty]
    public partial string? EmptyMessage { get; set; }

    public bool IsUnified => Mode == DiffViewMode.Unified;
    public bool IsSideBySide => Mode == DiffViewMode.SideBySide;
    public bool HasImage => Image is not null;
    public bool HasEmptyMessage => EmptyMessage is not null;

    public DiffOptions Options => new()
    {
        ContextLines = ContextLines,
        Whitespace = Whitespace,
    };

    /// <summary>Replaces the pane's content with a file's diff.</summary>
    public void Load(FileDiff? diff, string path)
    {
        UnifiedRows.Clear();
        SideBySideRows.Clear();

        // Decoded bitmaps are native memory; clicking through a run of image commits would
        // accumulate them otherwise.
        Image?.Dispose();
        Image = null;
        EmptyMessage = null;
        OnPropertyChanged(nameof(HasImage));

        if (diff is null)
        {
            SetEmpty("The diff could not be read.");
            return;
        }

        if (diff.IsBinary)
        {
            SetEmpty("Binary file — no text diff available.");
            return;
        }

        if (diff.Hunks.Count == 0)
        {
            SetEmpty(Whitespace == WhitespaceMode.Show
                ? "No changes in this file."
                : "No changes once whitespace is ignored.");
            return;
        }

        BuildUnified(diff, path);
        BuildSideBySide(diff, path);
    }

    /// <summary>Shows an image before and after, in place of a text diff.</summary>
    public void LoadImage(ImageDiffViewModel image)
    {
        UnifiedRows.Clear();
        SideBySideRows.Clear();
        EmptyMessage = null;

        Image?.Dispose();
        Image = image;
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasEmptyMessage));
    }

    private void SetEmpty(string message)
    {
        EmptyMessage = message;
        OnPropertyChanged(nameof(HasEmptyMessage));
    }

    private void BuildUnified(FileDiff diff, string path)
    {
        foreach (var hunk in diff.Hunks)
        {
            UnifiedRows.Add(new UnifiedRowViewModel
            {
                Kind = DiffLineKind.HunkHeader,
                Text = hunk.Header,
                Runs = [new DiffRun(hunk.Header, TokenKind.Plain, false)],
            });

            // Word-level segments only exist for lines that were replaced, not merely inserted.
            var pairs = ShowWordHighlighting ? WordDiff.PairReplacedLines(hunk) : null;
            var reverse = pairs?.ToDictionary(p => p.Value, p => p.Key);

            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                var line = hunk.Lines[i];
                IReadOnlyList<DiffRun> runs;

                if (pairs is not null && pairs.TryGetValue(i, out var partner))
                {
                    var (old, _) = WordDiff.Compare(line.Text, hunk.Lines[partner].Text);
                    runs = DiffRunBuilder.Build(line.Text, old, path);
                }
                else if (reverse is not null && reverse.TryGetValue(i, out var source))
                {
                    var (_, updated) = WordDiff.Compare(hunk.Lines[source].Text, line.Text);
                    runs = DiffRunBuilder.Build(line.Text, updated, path);
                }
                else
                {
                    runs = DiffRunBuilder.BuildPlain(line.Text, path);
                }

                UnifiedRows.Add(new UnifiedRowViewModel
                {
                    Kind = line.Kind,
                    Text = line.Text,
                    Runs = runs,
                    OldLineNumber = line.OldLineNumber,
                    NewLineNumber = line.NewLineNumber,
                });
            }
        }
    }

    private void BuildSideBySide(FileDiff diff, string path)
    {
        foreach (var row in SideBySideDiff.Build(diff, ShowWordHighlighting))
        {
            SideBySideRows.Add(new SideBySideRowViewModel
            {
                Left = ToCell(row.Left, path),
                Right = ToCell(row.Right, path),
                IsHunkHeader = row.IsHunkHeader,
                HunkHeader = row.HunkHeader,
            });
        }
    }

    private static SideCellViewModel ToCell(SideBySideCell cell, string path) => cell.IsEmpty
        ? SideCellViewModel.Empty
        : new SideCellViewModel
        {
            Kind = cell.Kind,
            Text = cell.Text,
            LineNumber = cell.LineNumber,
            Runs = DiffRunBuilder.Build(cell.Text, cell.Segments, path),
        };

    public void Dispose()
    {
        Image?.Dispose();
        Image = null;
    }

    // ------------------------------------------------------ option changes

    partial void OnModeChanged(DiffViewMode value)
    {
        OnPropertyChanged(nameof(IsUnified));
        OnPropertyChanged(nameof(IsSideBySide));
    }

    // These three change what git is asked for, so the owner has to re-read the diff.
    partial void OnContextLinesChanged(int value) => OptionsChanged?.Invoke(this, EventArgs.Empty);

    partial void OnWhitespaceChanged(WhitespaceMode value)
    {
        OnPropertyChanged(nameof(WhitespaceIndex));
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowWordHighlightingChanged(bool value) => OptionsChanged?.Invoke(this, EventArgs.Empty);
}
