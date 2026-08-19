using Avalonia;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitGui.Core;

namespace GitGui.App.ViewModels;

/// <summary>A collapsible sidebar section such as "Branches" or "Remotes".</summary>
public sealed partial class SidebarSectionViewModel(string title) : ViewModelBase
{
    public string Title { get; } = title;

    public ObservableCollection<SidebarItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public bool HasItems => Items.Count > 0;
}

/// <summary>A ref in the sidebar. <see cref="TargetSha"/> lets the list scroll to it on click.</summary>
public sealed class SidebarItemViewModel(GitRef gitRef, string displayName, int indentLevel = 0)
{
    public GitRef Ref { get; } = gitRef;
    public string DisplayName { get; } = displayName;
    public string TargetSha => Ref.TargetSha;
    public bool IsHead => Ref.IsHead;
    public RefKind Kind => Ref.Kind;
    public Thickness IndentMargin { get; } = new(12 + (indentLevel * 14), 0, 0, 0);

    /// <summary>Ahead/behind indicator, e.g. "2↑ 1↓". Empty when in sync or untracked.</summary>
    public string TrackingDisplay => (Ref.Ahead, Ref.Behind) switch
    {
        (0, 0) => string.Empty,
        (> 0, 0) => $"{Ref.Ahead}↑",
        (0, > 0) => $"{Ref.Behind}↓",
        var (a, b) => $"{a}↑ {b}↓",
    };

    public bool HasTracking => TrackingDisplay.Length > 0;

    // Menu items bind to these so each ref kind offers only the actions that apply to it.
    public bool IsLocalBranch => Kind == RefKind.LocalBranch;
    public bool IsRemoteBranch => Kind == RefKind.RemoteBranch;
    public bool IsTag => Kind == RefKind.Tag;
    public bool IsStash => Kind == RefKind.Stash;

    /// <summary>A remote's own header row, which is a grouping label rather than a ref.</summary>
    public bool IsRemoteGroup => Kind == RefKind.Other;

    public bool IsCheckoutable => IsLocalBranch || IsRemoteBranch || IsTag;

    /// <summary>Merging or rebasing onto the branch you are already on is meaningless.</summary>
    public bool CanBeMergedIn => (IsLocalBranch || IsRemoteBranch) && !IsHead;
}
