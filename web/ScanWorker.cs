namespace PanelHaven.Web;

public sealed record ScanStatus(string State, int Scanned, int Added, int Updated, int Skipped,
    int ErrorCount, IReadOnlyList<string> Errors, long Revision = 0);
public sealed record LibraryWatchStatus(int WatchingRoots, int UnavailableRoots, bool Pending);

public sealed class ScanWorker(LibraryStore library, ILogger<ScanWorker> logger,
    IConfiguration configuration, IHostEnvironment environment) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly string dataPath = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(configuration["DataPath"] ?? "data", environment.ContentRootPath));
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private ScanStatus status = new("idle", 0, 0, 0, 0, 0, []);
    private CancellationTokenSource? cancellation;
    private Task? running;
    private Task? monitoring;
    private long nextScan, nextReconcile, ignoreEventsUntil;
    private bool pending;
    private bool stopping, disposed;

    public ScanStatus Status { get { lock (gate) return status; } }
    public LibraryWatchStatus WatchStatus
    {
        get { lock (gate) return new(watchers.Count, library.WatchRoots.Count - watchers.Count, pending); }
    }

    public bool TryStart(out ScanStatus snapshot)
    {
        lock (gate)
        {
            if (stopping || running is { IsCompleted: false })
            {
                snapshot = status;
                return false;
            }
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            pending = false;
            ignoreEventsUntil = 0;
            status = new("running", 0, 0, 0, 0, 0, [], status.Revision);
            snapshot = status;
            // ponytail: one process-local scan and one pending bit; per-file queues only if full scans become too costly.
            running = Task.Run(() => RunAsync(token));
            return true;
        }
    }

    public ScanStatus Cancel()
    {
        lock (gate)
        {
            pending = false;
            var now = Environment.TickCount64;
            nextReconcile = now + 60_000;
            // Drop late callbacks from the cancelled burst; periodic reconciliation still recovers their changes.
            ignoreEventsUntil = now + 2_000;
            if (status.State == "running")
            {
                status = status with { State = "cancelling" };
                cancellation!.Cancel();
            }
            return status;
        }
    }

    private void Progress(ScanResult result)
    {
        lock (gate)
            status = new(status.State, result.Scanned, result.Added, result.Updated, result.Skipped,
                result.ErrorCount, result.Errors.ToArray(), status.Revision);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var result = await library.ScanAsync(token, Progress);
            Progress(result);
            lock (gate) status = status with { State = token.IsCancellationRequested ? "cancelled" : "completed" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (gate) status = status with { State = "cancelled" };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Library scan failed");
            lock (gate)
                status = status with
                {
                    State = "failed", ErrorCount = status.ErrorCount + 1,
                    Errors = status.Errors.Take(99).Append("Scan failed. Check the server log before retrying.").ToArray()
                };
        }
        finally
        {
            lock (gate)
            {
                if (token.IsCancellationRequested) pending = false;
                status = status with { Revision = status.Revision + 1 };
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (monitoring is not null || stopping) return Task.CompletedTask;
            RefreshWatchers();
            pending = true;
            nextScan = Environment.TickCount64;
            nextReconcile = nextScan + 60_000;
            var token = lifetime.Token;
            monitoring = Task.Run(() => MonitorAsync(token));
        }
        return Task.CompletedTask;
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                lock (gate)
                {
                    if (stopping) break;
                    var now = Environment.TickCount64;
                    if (now >= nextReconcile)
                    {
                        nextReconcile = now + 60_000;
                        // Rebuild only before an idle scan: no event gap mid-scan, no endless follow-ups for slow scans.
                        if (running is not { IsCompleted: false })
                        {
                            RefreshWatchers();
                            pending = true;
                            nextScan = now;
                        }
                    }
                    if (pending && now >= nextScan) TryStart(out _);
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (gate) DisposeWatchers();
        }
    }

    private bool IsStatePath(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(dataPath, comparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(dataPath) ? dataPath : dataPath + Path.DirectorySeparatorChar, comparison);
    }

    private void Changed(object sender, FileSystemEventArgs change)
    {
        if (IsStatePath(change.FullPath)
            && (change is not RenamedEventArgs renamed || IsStatePath(renamed.OldFullPath))) return;
        lock (gate) QueueScan();
    }

    private void QueueScan()
    {
        var now = Environment.TickCount64;
        if (stopping || status.State == "cancelling" || now < ignoreEventsUntil) return;
        pending = true;
        nextScan = now + 2_000;
    }

    private void WatchError(object sender, ErrorEventArgs error)
    {
        lock (gate)
        {
            if (sender is not FileSystemWatcher watcher || !watchers.Remove(watcher)) return;
            watcher.Dispose();
            QueueScan();
        }
    }

    private void RefreshWatchers()
    {
        // ponytail: rebuild once a minute and reconcile the catalog; this bounds overflow/disconnect recovery without a durable queue.
        DisposeWatchers();
        foreach (var root in library.WatchRoots)
        {
            if (IsStatePath(root)) continue;
            FileSystemWatcher? watcher = null;
            try
            {
                LibraryStore.EnsureSafePath(root, root);
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
                };
                watcher.Created += Changed;
                watcher.Changed += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += Changed;
                watcher.Error += WatchError;
                watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
                LibraryStore.EnsureSafePath(root, root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (watcher is not null)
                {
                    watchers.Remove(watcher);
                    watcher.Dispose();
                }
            }
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? task, monitor;
        lock (gate)
        {
            if (disposed) return;
            stopping = true;
            Cancel();
            lifetime.Cancel();
            monitor = monitoring;
            task = running;
        }
        if (task is not null) await task.WaitAsync(cancellationToken);
        if (monitor is not null) await monitor.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stopping = true;
            Cancel();
            lifetime.Cancel();
            lifetime.Dispose();
            DisposeWatchers();
            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = null;
        }
    }
}
