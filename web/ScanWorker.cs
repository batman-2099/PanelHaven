using System.Text.Json;

namespace PanelHaven.Web;

public sealed record ScanStatus(string State, int Scanned, int Added, int Updated, int Skipped,
    int ErrorCount, IReadOnlyList<string> Errors, long Revision = 0, bool LogAvailable = false, string? LogError = null);
public sealed record LibraryWatchStatus(int WatchingRoots, int UnavailableRoots, bool Pending);

public sealed class ScanWorker(LibraryStore library, ILogger<ScanWorker> logger,
    IConfiguration configuration, IHostEnvironment environment) : IHostedService, IDisposable
{
    private const long LogSegmentBytes = 8 * 1024 * 1024;
    private const int LogTextCharacters = 16_384;
    private const string LogFailure = "Detailed scan logging is unavailable or incomplete. Check the server log.";
    private static readonly JsonSerializerOptions LogJson = new(JsonSerializerDefaults.Web);
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
    private FileStream? log;
    private bool logInspected;
    private string? scanId;
    private string LogPath => Path.Combine(dataPath, "scan-log.jsonl");
    private string PreviousLogPath => Path.Combine(dataPath, "scan-log.previous.jsonl");

    public ScanStatus Status
    {
        get { lock (gate) { InspectLog(); return status; } }
    }
    public LibraryWatchStatus WatchStatus
    {
        get { lock (gate) return new(watchers.Count, library.WatchRoots.Count - watchers.Count, pending); }
    }

    public bool TryStart(out ScanStatus snapshot)
    {
        lock (gate)
        {
            InspectLog();
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
            status = new("running", 0, 0, 0, 0, 0, [], status.Revision, status.LogAvailable);
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
                result.ErrorCount, result.Errors.ToArray(), status.Revision, status.LogAvailable, status.LogError);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            BeginLog();
            var result = await library.ScanAsync(token, Progress, entry => AppendLog("file", entry));
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
            {
                status = status with
                {
                    State = "failed", ErrorCount = status.ErrorCount + 1,
                    Errors = status.Errors.Take(99).Append("Scan failed. Check the server log before retrying.").ToArray()
                };
                AppendLog("fatal", new(DateTimeOffset.UtcNow, "error", null, null, "scan",
                    exception.GetType().FullName, exception.Message, exception.ToString()));
            }
        }
        finally
        {
            lock (gate)
            {
                if (token.IsCancellationRequested) pending = false;
                AppendLog("end");
                CloseLog();
                status = status with { Revision = status.Revision + 1 };
            }
        }
    }

    private void InspectLog()
    {
        if (logInspected) return;
        logInspected = true;
        try
        {
            var current = LogLength(LogPath);
            var previous = LogLength(PreviousLogPath);
            status = status with { LogAvailable = current > 0 || previous > 0 };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportLogFailure(exception);
        }
    }

    private long LogLength(string path)
    {
        DataRelocation.SafePath(path);
        if (!Path.Exists(path)) return 0;
        DataRelocation.RequireRegular(path);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("Scan diagnostics require private files.");
        var length = new FileInfo(path).Length;
        if (length > LogSegmentBytes) throw new IOException("Scan diagnostic segment exceeds its size limit.");
        return length;
    }

    private void BeginLog()
    {
        lock (gate)
        {
            if (disposed) return;
            scanId = Guid.NewGuid().ToString("N");
            try
            {
                LibraryStore.EnsureSafePath(dataPath, dataPath);
                LogLength(PreviousLogPath);
                log = OpenLog();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReportLogFailure(exception);
                CloseLog();
            }
            AppendLog("start");
        }
    }

    private FileStream OpenLog()
    {
        LogLength(LogPath);
        var options = new FileStreamOptions
        {
            Mode = Path.Exists(LogPath) ? FileMode.Open : FileMode.CreateNew,
            Access = FileAccess.ReadWrite, Share = FileShare.Read
        };
        if (!OperatingSystem.IsWindows() && options.Mode == FileMode.CreateNew)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        log = new FileStream(LogPath, options);
        DataRelocation.RequireRegular(LogPath);
        // A crash or failed write can leave one incomplete final record; never append JSON to that fragment.
        log.SetLength(CompleteLogLength(log));
        log.Position = log.Length;
        return log;
    }

    private static long CompleteLogLength(FileStream input)
    {
        var end = input.Length;
        if (end > LogSegmentBytes) throw new IOException("Scan diagnostic segment exceeds its size limit.");
        Span<byte> buffer = stackalloc byte[4096];
        while (end > 0)
        {
            var count = (int)Math.Min(buffer.Length, end);
            input.Position = end - count;
            input.ReadExactly(buffer[..count]);
            var newline = buffer[..count].LastIndexOf((byte)'\n');
            if (newline >= 0) return end - count + newline + 1;
            end -= count;
        }
        return 0;
    }

    private static string? BoundLogText(string? text) => text?.Length > LogTextCharacters
        ? text[..LogTextCharacters] + " [truncated]" : text;

    private void AppendLog(string kind, ScanLogEntry? entry = null)
    {
        lock (gate)
        {
            if (log is null) return;
            // Bound every text field (including escaping) below one segment; do not serialize comic metadata or contents.
            if (entry is not null && (entry.Outcome.Length > LogTextCharacters || entry.Stage.Length > LogTextCharacters
                || entry.Root?.Length > LogTextCharacters || entry.Path?.Length > LogTextCharacters
                || entry.ExceptionType?.Length > LogTextCharacters || entry.Message?.Length > LogTextCharacters
                || entry.Detail?.Length > LogTextCharacters))
                entry = entry with
                {
                    Outcome = BoundLogText(entry.Outcome)!, Stage = BoundLogText(entry.Stage)!,
                    Root = BoundLogText(entry.Root), Path = BoundLogText(entry.Path),
                    ExceptionType = BoundLogText(entry.ExceptionType), Message = BoundLogText(entry.Message),
                    Detail = BoundLogText(entry.Detail)
                };
            var record = JsonSerializer.SerializeToUtf8Bytes(new
            {
                timestamp = DateTimeOffset.UtcNow, scanId, @event = kind, entry,
                state = status.State, scanned = status.Scanned, added = status.Added,
                updated = status.Updated, skipped = status.Skipped, errorCount = status.ErrorCount
            }, LogJson);
            try
            {
                if (log.Length + record.Length + 1 > LogSegmentBytes)
                {
                    log.Dispose();
                    log = null;
                    LogLength(LogPath);
                    LogLength(PreviousLogPath);
                    File.Move(LogPath, PreviousLogPath, overwrite: true);
                    log = OpenLog();
                }
                log.Write(record);
                log.WriteByte((byte)'\n');
                log.Flush();
                status = status with { LogAvailable = true };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReportLogFailure(exception);
                CloseLog();
            }
        }
    }

    private void ReportLogFailure(Exception exception)
    {
        logger.LogError(exception, "Scan diagnostic log is unavailable or incomplete");
        status = status with { LogError = LogFailure };
    }

    private void CloseLog()
    {
        var closing = log;
        log = null;
        try { closing?.Dispose(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportLogFailure(exception);
        }
    }

    public FileStream? ExportLog(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            FileStream? output = null;
            try
            {
                LibraryStore.EnsureSafePath(dataPath, dataPath);
                Span<byte> buffer = stackalloc byte[16 * 1024];
                // ponytail: copy at most two fixed-size segments under the scan gate; downloads never own the live writer.
                foreach (var path in new[] { PreviousLogPath, LogPath })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (LogLength(path) == 0) continue;
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    DataRelocation.RequireRegular(path);
                    var remaining = CompleteLogLength(input);
                    if (remaining == 0) continue;
                    if (output is null)
                    {
                        var options = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                            Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan
                        };
                        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                        output = new FileStream(Path.Combine(dataPath, ".panelhaven-scan-log-" + Guid.NewGuid().ToString("N") + ".tmp"), options);
                    }
                    input.Position = 0;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = input.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
                        if (count == 0) throw new EndOfStreamException("Scan diagnostic segment changed during export.");
                        output.Write(buffer[..count]);
                        remaining -= count;
                    }
                }
                if (output is not null) output.Position = 0;
                status = status with { LogAvailable = output is not null };
                return output;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ReportLogFailure(exception);
                output?.Dispose();
                throw;
            }
            catch
            {
                output?.Dispose();
                throw;
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
            CloseLog();
        }
    }
}
