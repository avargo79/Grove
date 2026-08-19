using Grove.Core;

namespace Grove.Core.Tests;

public class CommitFilterAndSettingsTests
{
    // -------------------------------------------------------------- filter

    [Fact]
    public void AnEmptyFilterProducesNoArguments()
    {
        Assert.True(CommitFilter.Empty.IsEmpty);
        Assert.Empty(CommitFilter.Empty.ToArguments());
        Assert.Equal(string.Empty, CommitFilter.Empty.Describe());
    }

    [Fact]
    public void WhitespaceOnlyCriteriaStillCountAsEmpty()
    {
        var filter = new CommitFilter { Text = "   ", Author = "" };

        Assert.True(filter.IsEmpty);
        Assert.Empty(filter.ToArguments());
    }

    [Fact]
    public void MessageSearchIsALiteralCaseInsensitiveGrep()
    {
        var args = new CommitFilter { Text = "fix(auth)" }.ToArguments().ToList();

        // Fixed-strings matters: a search box is not a regex box, and "fix(auth)" would not compile.
        Assert.Contains("--fixed-strings", args);
        Assert.Contains("--regexp-ignore-case", args);
        Assert.Contains("--grep=fix(auth)", args);
    }

    [Fact]
    public void AuthorSearchIsCaseInsensitive()
    {
        var args = new CommitFilter { Author = "ada" }.ToArguments().ToList();

        Assert.Contains("--author=ada", args);
        Assert.Contains("--regexp-ignore-case", args);
    }

    [Fact]
    public void CombiningMessageAndAuthorRequiresBothToMatch()
    {
        var args = new CommitFilter { Text = "fix", Author = "ada" }.ToArguments().ToList();

        // Git ORs greps by default, which is never what a search box means.
        Assert.Contains("--all-match", args);
    }

    [Fact]
    public void ASingleCriterionDoesNotNeedAllMatch()
    {
        Assert.DoesNotContain("--all-match", new CommitFilter { Text = "fix" }.ToArguments());
    }

    [Fact]
    public void DateBoundsArePassedInAnUnambiguousFormat()
    {
        var since = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var args = new CommitFilter { Since = since }.ToArguments().ToList();

        Assert.Contains(args, a => a.StartsWith("--since=2024-03-01T00:00:00", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePathIsNotAnArgumentBecauseItGoesAfterTheSeparator()
    {
        var filter = new CommitFilter { Path = "src/app.cs" };

        Assert.False(filter.IsEmpty);
        Assert.DoesNotContain(filter.ToArguments(), a => a.Contains("app.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDescriptionNamesEveryActiveCriterion()
    {
        var description = new CommitFilter
        {
            Text = "login",
            Author = "ada",
            Path = "src/",
        }.Describe();

        Assert.Contains("message contains \"login\"", description, StringComparison.Ordinal);
        Assert.Contains("author matches \"ada\"", description, StringComparison.Ordinal);
        Assert.Contains("touches src/", description, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ settings

    private static string TempSettingsPath() => Path.Combine(
        Path.GetTempPath(), "grove-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void MissingSettingsFallBackToDefaults()
    {
        var store = new SettingsStore(TempSettingsPath());

        Assert.Equal(AppTheme.Dark, store.Load().Theme);
        Assert.Equal(3, store.Load().DiffContextLines);
    }

    [Fact]
    public void SettingsSurviveARoundTrip()
    {
        var path = TempSettingsPath();
        var store = new SettingsStore(path);

        var settings = AppSettings.Default with
        {
            Theme = AppTheme.Light,
            DiffContextLines = 8,
            DiffWhitespace = WhitespaceMode.IgnoreAll,
            CommitPageSize = 250,
        };

        Assert.True(store.Save(settings));
        var loaded = new SettingsStore(path).Load();

        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(8, loaded.DiffContextLines);
        Assert.Equal(WhitespaceMode.IgnoreAll, loaded.DiffWhitespace);
        Assert.Equal(250, loaded.CommitPageSize);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void ACorruptSettingsFileFallsBackToDefaultsRatherThanThrowing()
    {
        var path = TempSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        // Settings going bad must never stop the app from starting.
        Assert.Equal(AppSettings.Default, new SettingsStore(path).Load());

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void EnumsAreStoredByNameSoTheFileStaysReadable()
    {
        var path = TempSettingsPath();
        new SettingsStore(path).Save(AppSettings.Default with { Theme = AppTheme.Light });

        Assert.Contains("\"Light\"", File.ReadAllText(path), StringComparison.Ordinal);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void OpeningARepositoryPutsItAtTheFrontOfTheRecentList()
    {
        var settings = AppSettings.Default
            .WithRecentRepository("/one")
            .WithRecentRepository("/two");

        Assert.Equal(["/two", "/one"], settings.RecentRepositories);
    }

    [Fact]
    public void ReopeningARepositoryMovesItUpRatherThanDuplicatingIt()
    {
        var settings = AppSettings.Default
            .WithRecentRepository("/one")
            .WithRecentRepository("/two")
            .WithRecentRepository("/one");

        Assert.Equal(["/one", "/two"], settings.RecentRepositories);
    }

    [Fact]
    public void TheRecentListIsCapped()
    {
        var settings = AppSettings.Default;
        for (var i = 0; i < AppSettings.MaxRecentRepositories + 5; i++)
            settings = settings.WithRecentRepository($"/repo{i}");

        Assert.Equal(AppSettings.MaxRecentRepositories, settings.RecentRepositories.Count);
        Assert.Equal($"/repo{AppSettings.MaxRecentRepositories + 4}", settings.RecentRepositories[0]);
    }

    [Fact]
    public void ARepositoryCanBeRemovedFromTheRecentList()
    {
        var settings = AppSettings.Default
            .WithRecentRepository("/one")
            .WithRecentRepository("/two")
            .WithoutRecentRepository("/one");

        Assert.Equal(["/two"], settings.RecentRepositories);
    }

    [Fact]
    public void BlankPathsAreIgnored()
    {
        Assert.Empty(AppSettings.Default.WithRecentRepository("  ").RecentRepositories);
    }
}
