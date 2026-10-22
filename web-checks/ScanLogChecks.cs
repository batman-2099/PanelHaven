using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PanelHaven.Web;

public static class ScanLogChecks
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    public static async Task RunAsync()
    {
        const long segmentBytes = 8 * 1024 * 1024;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static async Task Until(Func<bool> condition, string message)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(20), message);
                await Task.Delay(20);
            }
        }
        static IEnumerable<JsonElement> Records(Stream stream)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                yield return document.RootElement.Clone();
            }
        }
        static byte[] Hash(Stream stream)
        {
            stream.Position = 0;
            return SHA256.HashData(stream);
        }
        static void Archive(string path)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var page = zip.CreateEntry("page.png").Open();
            page.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
        }
        // Seed a boundary using actual previously exported records, not a configurable production limit or fake scanner.
        static void FillSegment(string path, string record)
        {
            var bytes = Encoding.UTF8.GetBytes(record + "\n");
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            while (segmentBytes - file.Length > bytes.Length * 2) file.Write(bytes);
            file.Write(bytes.AsSpan(0, bytes.Length - 1));
            while (file.Length < segmentBytes - 1) file.WriteByte((byte)' ');
            file.WriteByte((byte)'\n');
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-scan-log-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        var data = Path.Combine(directory, "data");
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(data);
        else Directory.CreateDirectory(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = directory, DisableDefaults = true });
        builder.Configuration["DataPath"] = data;
        var database = Path.Combine(data, "checks.sqlite");
        var current = Path.Combine(data, "scan-log.jsonl");
        var previous = Path.Combine(data, "scan-log.previous.jsonl");
        var store = new LibraryStore(database, [root]);
        ScanWorker NewWorker() => new(store, NullLogger<ScanWorker>.Instance, builder.Configuration, builder.Environment);
        ScanWorker worker = NewWorker();
        async Task Scan()
        {
            var revision = worker.Status.Revision;
            Check(worker.TryStart(out _), "Diagnostic check could not start a scan.");
            await Until(() => worker.Status.Revision == revision + 1, "Diagnostic scan did not finish.");
        }
        async Task Restart()
        {
            await worker.StopAsync(default);
            worker.Dispose();
            worker = NewWorker();
        }
        void Sql(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={database}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        try
        {
            Check(!worker.Status.LogAvailable && worker.ExportLog() is null, "An idle fresh worker invented historical diagnostics.");
            var valid = Path.Combine(root, "valid.cbz");
            Archive(valid);
            for (var index = 0; index < 125; index++)
                File.WriteAllText(Path.Combine(root, $"invalid-{index:D3}.cbz"), "invalid comicbook");
            await Scan();
            Check(worker.Status is { State: "completed", Scanned: 126, Added: 1, ErrorCount: 125, LogAvailable: true, LogError: null }
                && worker.Status.Errors.Count == 100, "Diagnostics changed scan counters or the 100-error summary cap.");
            Check(worker.Status.Errors.All(error => !error.Contains(root)), "Ordinary scan status exposed a full path.");
            JsonElement[] first;
            string snapshotPath;
            using (var snapshot = worker.ExportLog()!)
            {
                snapshotPath = snapshot.Name;
                Check(snapshot.CanSeek && snapshot.Length <= segmentBytes * 2, "Diagnostic export is not a bounded seekable snapshot.");
                if (!OperatingSystem.IsWindows())
                    Check(File.GetUnixFileMode(snapshot.Name) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Diagnostic snapshot is not private.");
                first = Records(snapshot).ToArray();
            }
            Check(!File.Exists(snapshotPath), "Closing a diagnostic download left its private temporary file behind.");
            var errors = first.Where(record => record.GetProperty("event").GetString() == "file"
                && record.GetProperty("entry").GetProperty("outcome").GetString() == "error").ToArray();
            Check(errors.Length == 125 && errors.All(record =>
                record.GetProperty("entry").GetProperty("path").GetString()!.StartsWith(root)
                && record.GetProperty("entry").GetProperty("exceptionType").GetString() is not null
                && record.GetProperty("entry").GetProperty("stage").GetString() is not null
                && record.GetProperty("entry").GetProperty("detail").GetString() is not null), "Export lost detailed errors beyond the summary cap.");
            var firstId = first[0].GetProperty("scanId").GetString();
            Check(Guid.TryParseExact(firstId, "N", out _) && first.All(record => record.GetProperty("scanId").GetString() == firstId)
                && first[0].GetProperty("event").GetString() == "start"
                && first[^1].GetProperty("event").GetString() == "end"
                && first[^1].GetProperty("state").GetString() == "completed"
                && first[^1].GetProperty("errorCount").GetInt32() == 125, "Scan log identity or final counts are incorrect.");
            Check(first.All(record => record.GetProperty("timestamp").GetDateTimeOffset().Offset == TimeSpan.Zero)
                && first.Select(record => record.GetProperty("timestamp").GetDateTimeOffset()).SequenceEqual(
                    first.Select(record => record.GetProperty("timestamp").GetDateTimeOffset()).Order()), "Diagnostic timestamps are not chronological UTC.");
            if (!OperatingSystem.IsWindows())
                Check(File.GetUnixFileMode(current) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Persistent diagnostics are not private.");

            foreach (var path in Directory.EnumerateFiles(root, "invalid-*.cbz")) File.Delete(path);
            using (var oldSnapshot = worker.ExportLog()!)
            {
                var hash = Hash(oldSnapshot);
                await Scan();
                Check(worker.Status is { State: "completed", Scanned: 1, Skipped: 1, ErrorCount: 0, LogError: null }, "A successful scan did not finish cleanly.");
                Check(Hash(oldSnapshot).SequenceEqual(hash), "Appending a scan changed an already opened download.");
            }
            using (var snapshot = worker.ExportLog()!)
            {
                var records = Records(snapshot).ToArray();
                Check(records.Count(record => record.GetProperty("event").GetString() == "start") == 2
                    && records[^1].GetProperty("scanId").GetString() != firstId
                    && records.Any(record => record.GetProperty("scanId").GetString() == firstId), "A successful scan erased preceding failure history.");
                var hash = Hash(snapshot);
                await Restart();
                Check(worker.Status.LogAvailable, "Restart failed to discover retained diagnostics.");
                using var restarted = worker.ExportLog()!;
                Check(Hash(restarted).SequenceEqual(hash), "Restart changed retained diagnostics.");
            }

            // A real SQLite failure escapes archive error handling and must produce fatal + failed end, not cancellation.
            Sql("CREATE TRIGGER fail_scan BEFORE INSERT ON books BEGIN SELECT RAISE(FAIL, '" + new string('x', 20_000) + "'); END;");
            File.Delete(valid);
            Archive(valid);
            File.SetLastWriteTimeUtc(valid, DateTime.UtcNow.AddSeconds(5));
            await Scan();
            Check(worker.Status is { State: "failed", ErrorCount: 1, LogError: null }, "Fatal scan failure was misclassified.");
            using (var snapshot = worker.ExportLog()!)
            {
                var records = Records(snapshot).ToArray();
                var fatal = records.Single(record => record.GetProperty("event").GetString() == "fatal");
                var entry = fatal.GetProperty("entry");
                Check(entry.GetProperty("exceptionType").GetString() == typeof(SqliteException).FullName
                    && entry.GetProperty("message").GetString()!.EndsWith(" [truncated]")
                    && entry.GetProperty("message").GetString()!.Length < 17_000
                    && entry.GetProperty("detail").GetString()!.Length < 17_000
                    && records[^1].GetProperty("state").GetString() == "failed", "Fatal detail or single-record truncation policy is missing.");
            }
            Sql("DROP TRIGGER fail_scan;");

            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var blocked = false;
                var holding = Task.Run(() => store.ScanAsync(default, result =>
                {
                    if (blocked || result.Scanned != 0) return;
                    blocked = true;
                    entered.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(30)), "Diagnostic check did not release the real scan lock.");
                }));
                try
                {
                    await Until(() => entered.IsSet, "Could not acquire the real scan lock for cancellation.");
                    var revision = worker.Status.Revision;
                    Check(worker.TryStart(out _), "Cancellation diagnostic scan did not start.");
                    await Until(() =>
                    {
                        using var snapshot = worker.ExportLog()!;
                        return Records(snapshot).Last().GetProperty("event").GetString() == "start";
                    }, "A running scan did not flush its start event.");
                    using var runningSnapshot = worker.ExportLog()!;
                    var hash = Hash(runningSnapshot);
                    worker.Cancel();
                    await Until(() => worker.Status.Revision == revision + 1, "Cancelled diagnostic scan did not finish.");
                    Check(worker.Status is { State: "cancelled", ErrorCount: 0, LogError: null }
                        && Hash(runningSnapshot).SequenceEqual(hash), "Cancellation changed an open snapshot or became a failure.");
                    using var snapshot = worker.ExportLog()!;
                    var records = Records(snapshot).ToArray();
                    var id = records[^1].GetProperty("scanId").GetString();
                    Check(records[^1].GetProperty("state").GetString() == "cancelled"
                        && records.Where(record => record.GetProperty("scanId").GetString() == id).Select(record => record.GetProperty("event").GetString())
                            .SequenceEqual(new[] { "start", "end" }), "Cancellation wrote a fatal event or omitted its final state.");
                }
                finally { release.Set(); await holding; }
            }

            await Restart();
            File.Move(current, previous);
            await Restart();
            Check(worker.Status.LogAvailable, "Restart missed a retained previous segment without a current segment.");
            await Scan();
            Check(File.Exists(previous) && worker.Status.LogError is null, "A new successful scan discarded retained history.");
            string seed;
            using (var snapshot = worker.ExportLog()!) seed = Records(snapshot).Last().GetRawText();
            await Restart();
            FillSegment(current, seed);
            using (var frozen = worker.ExportLog()!)
            {
                var frozenHash = Hash(frozen);
                await Scan();
                Check(new FileInfo(previous).Length == segmentBytes && new FileInfo(current).Length < segmentBytes,
                    "Diagnostics did not rotate at the 8 MiB boundary.");
                Check(Hash(frozen).SequenceEqual(frozenHash), "Rotation changed an in-flight snapshot.");
                using var snapshot = worker.ExportLog()!;
                Check(snapshot.Length == new FileInfo(previous).Length + new FileInfo(current).Length
                    && snapshot.Length <= segmentBytes * 2, "Export exceeded the two-segment retention bound.");
                seed = Records(snapshot).Last().GetRawText();
            }
            await Restart();
            FillSegment(current, seed);
            var latestSeed = JsonDocument.Parse(seed);
            using (latestSeed)
            {
                var retainedId = latestSeed.RootElement.GetProperty("scanId").GetString();
                await Scan();
                using var snapshot = worker.ExportLog()!;
                Check(Records(snapshot).First().GetProperty("scanId").GetString() == retainedId,
                    "A second rotation did not replace the oldest segment with recent scan history.");
            }
            Check(Directory.EnumerateFiles(data, "scan-log*.jsonl").Count() == 2, "Rotation created extra retained segments.");

            // Recover a process-interrupted trailing record, without discarding preceding complete diagnostics.
            await Restart();
            var beforeFragment = new FileInfo(current).Length;
            File.AppendAllText(current, "{\"partial\":");
            using (var snapshot = worker.ExportLog()!)
                Check(snapshot.Length == new FileInfo(previous).Length + beforeFragment, "Snapshot exposed an incomplete JSONL record.");
            await Scan();
            using (var snapshot = worker.ExportLog()!)
                Check(Records(snapshot).Last().GetProperty("event").GetString() == "end", "Restart appended onto an incomplete record.");

            await Restart();
            File.Delete(current);
            Directory.CreateDirectory(current);
            await Scan();
            Check(worker.Status is { State: "completed", Scanned: 1, Skipped: 1, ErrorCount: 0, LogError: not null }
                && worker.Status.LogError?.Contains(data) == false, "Logging I/O failure corrupted scan outcomes or exposed a private path.");
            try { using var ignored = worker.ExportLog(); throw new Exception("Export accepted a directory as a log segment."); }
            catch (IOException) { }
            Check(!Directory.EnumerateFiles(data, ".panelhaven-scan-log-*.tmp").Any(), "A failed snapshot leaked its partially copied temporary file.");
            Directory.Delete(current);
            await Scan();
            Check(worker.Status is { State: "completed", LogAvailable: true, LogError: null }, "A subsequent scan did not recover logging after an I/O failure.");

            if (OperatingSystem.IsLinux())
            {
                await Restart();
                File.Delete(current);
                var sentinel = Path.Combine(data, "sentinel");
                File.WriteAllText(sentinel, "private sentinel");
                File.CreateSymbolicLink(current, sentinel);
                await Scan();
                Check(worker.Status is { State: "completed", ErrorCount: 0, LogError: not null }
                    && File.ReadAllText(sentinel) == "private sentinel", "Logging followed a symlink or damaged its target.");
                File.Delete(current);
                Check(MakeFifo(current, 384) == 0, "Could not create a FIFO log fixture.");
                await Scan();
                Check(worker.Status is { State: "completed", ErrorCount: 0, LogError: not null }, "A FIFO log blocked scanning or was accepted.");
                File.Delete(current);
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { using var ignored = worker.ExportLog(cancellation.Token); throw new Exception("A cancelled snapshot was accepted."); }
                catch (OperationCanceledException) { }
                Check(!Directory.EnumerateFiles(data, ".panelhaven-scan-log-*.tmp").Any(), "Cancellation left a diagnostic snapshot behind.");
            }
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
