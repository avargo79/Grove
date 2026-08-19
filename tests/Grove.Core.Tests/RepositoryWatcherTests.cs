using Grove.Core;

namespace Grove.Core.Tests;

[Trait("Category", "Integration")]
public class RepositoryWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Waits for the watcher to fire, or gives up so a failure reads as a failure.</summary>
    private static async Task<bool> WaitForSignalAsync(SemaphoreSlim signal) =>
        await signal.WaitAsync(Timeout);

    [Fact]
    public async Task EditingATrackedFileRaisesAChange()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var signal = new SemaphoreSlim(0);
        using var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Changed += (_, _) => signal.Release();
        watcher.Start();

        fixture.WriteFile("a.txt", "two\n");

        Assert.True(await WaitForSignalAsync(signal));
    }

    [Fact]
    public async Task CreatingANewFileRaisesAChange()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var signal = new SemaphoreSlim(0);
        using var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Changed += (_, _) => signal.Release();
        watcher.Start();

        fixture.WriteFile("brand-new.txt", "content\n");

        Assert.True(await WaitForSignalAsync(signal));
    }

    [Fact]
    public async Task ACommitMadeOutsideTheAppRaisesAChange()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var signal = new SemaphoreSlim(0);
        using var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Changed += (_, _) => signal.Release();
        watcher.Start();

        fixture.Commit("second", "b.txt", "two\n");

        Assert.True(await WaitForSignalAsync(signal));
    }

    [Fact]
    public async Task ABurstOfEditsCollapsesIntoASingleNotification()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var count = 0;
        var signal = new SemaphoreSlim(0);
        using var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Changed += (_, _) =>
        {
            Interlocked.Increment(ref count);
            signal.Release();
        };
        watcher.Start();

        // Twenty writes in quick succession, as an editor's autosave would produce.
        for (var i = 0; i < 20; i++)
            fixture.WriteFile("a.txt", $"content {i}\n");

        Assert.True(await WaitForSignalAsync(signal));
        await Task.Delay(Debounce * 4);

        // The debounce exists so a burst does not reload history twenty times.
        Assert.InRange(Volatile.Read(ref count), 1, 3);
    }

    [Fact]
    public async Task NoEventsArriveBeforeStartIsCalled()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var count = 0;
        using var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);

        fixture.WriteFile("a.txt", "changed before start\n");
        await Task.Delay(Debounce * 4);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task DisposingStopsFurtherNotifications()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var count = 0;
        var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);
        watcher.Start();
        watcher.Dispose();

        fixture.WriteFile("a.txt", "after dispose\n");
        await Task.Delay(Debounce * 4);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        using var fixture = TestRepository.CreateEmpty();
        fixture.Commit("first", "a.txt", "one\n");

        var watcher = new RepositoryWatcher(fixture.Path, Debounce);
        watcher.Start();
        watcher.Dispose();

        Assert.Null(Record.Exception(watcher.Dispose));
    }
}
