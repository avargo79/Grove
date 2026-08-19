namespace GitFork.Core;

/// <summary>
/// Watches a work tree and its <c>.git</c> directory and raises a single debounced event when
/// anything changes, so edits, checkouts and commits made outside the app show up without a
/// manual refresh.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private readonly FileSystemWatcher _workTree;
    private readonly FileSystemWatcher _gitDirectory;
    private readonly System.Threading.Lock _gate = new();
    private readonly TimeSpan _debounce;

    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Raised on a background thread after activity has settled. Handlers that touch UI state must
    /// marshal to their own thread.
    /// </summary>
    public event EventHandler? Changed;

    public RepositoryWatcher(string rootPath, TimeSpan? debounce = null)
    {
        // Editors save in bursts and git rewrites several files per operation, so a short quiet
        // period avoids reloading the whole history a dozen times per keystroke.
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);

        _workTree = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        var gitPath = Path.Combine(rootPath, ".git");
        _gitDirectory = new FileSystemWatcher(Directory.Exists(gitPath) ? gitPath : rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        Subscribe(_workTree, ignoreGitInternals: true);
        Subscribe(_gitDirectory, ignoreGitInternals: false);
    }

    /// <summary>Starts raising events. Watching is off until this is called.</summary>
    public void Start()
    {
        _workTree.EnableRaisingEvents = true;
        _gitDirectory.EnableRaisingEvents = true;
    }

    private void Subscribe(FileSystemWatcher watcher, bool ignoreGitInternals)
    {
        void Handle(object? _, FileSystemEventArgs e)
        {
            if (ignoreGitInternals && IsGitInternal(e.FullPath))
                return;
            Schedule();
        }

        watcher.Created += Handle;
        watcher.Deleted += Handle;
        watcher.Changed += Handle;
        watcher.Renamed += Handle;

        // A dropped buffer means changes were missed, which is itself a reason to reload.
        watcher.Error += (_, _) => Schedule();
    }

    /// <summary>
    /// The work-tree watcher must ignore <c>.git</c>: the dedicated watcher covers it, and git's
    /// own lock and temp files would otherwise fire constantly.
    /// </summary>
    private static bool IsGitInternal(string path)
    {
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/.git/", StringComparison.Ordinal)
               || normalised.EndsWith("/.git", StringComparison.Ordinal);
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            // Restart the quiet period on every event so a burst produces exactly one reload.
            _timer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        _workTree.EnableRaisingEvents = false;
        _gitDirectory.EnableRaisingEvents = false;
        _workTree.Dispose();
        _gitDirectory.Dispose();
    }
}
