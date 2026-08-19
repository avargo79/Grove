using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitFork.Core;

/// <summary>Which palette the app uses.</summary>
public enum AppTheme { Dark, Light }

/// <summary>
/// Everything the app remembers between sessions. A record so a change is an explicit new value
/// rather than a mutation somewhere in the UI.
/// </summary>
public sealed record AppSettings
{
    public const int MaxRecentRepositories = 10;

    public static AppSettings Default { get; } = new();

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Context lines the diff pane opens with.</summary>
    public int DiffContextLines { get; init; } = 3;

    public WhitespaceMode DiffWhitespace { get; init; } = WhitespaceMode.Show;

    public bool ShowSyntaxHighlighting { get; init; } = true;

    public bool ShowWordHighlighting { get; init; } = true;

    /// <summary>Commits loaded per page.</summary>
    public int CommitPageSize { get; init; } = 500;

    /// <summary>Most recently opened first.</summary>
    public IReadOnlyList<string> RecentRepositories { get; init; } = [];

    /// <summary>Adds a path to the front of the recent list, without duplicating it.</summary>
    public AppSettings WithRecentRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return this;

        var updated = new List<string> { path };
        updated.AddRange(RecentRepositories.Where(
            p => !string.Equals(p, path, StringComparison.Ordinal)));

        return this with
        {
            RecentRepositories = [.. updated.Take(MaxRecentRepositories)],
        };
    }

    public AppSettings WithoutRecentRepository(string path) => this with
    {
        RecentRepositories = [.. RecentRepositories.Where(
            p => !string.Equals(p, path, StringComparison.Ordinal))],
    };
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under the user's application data.
///
/// Every failure path returns defaults rather than throwing: settings going missing or being
/// corrupted must never stop the app from starting.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public string Path => _path;

    /// <summary>Where settings live when no explicit path is given.</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitFork",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return AppSettings.Default;

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options)
                   ?? AppSettings.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file is not worth refusing to start over.
            return AppSettings.Default;
        }
    }

    /// <summary>Saves, returning false if it could not be written.</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
