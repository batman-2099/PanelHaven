using System.Diagnostics;
using System.IO.Compression;
using ComicRack.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

public static class WatchChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static async Task Until(Func<bool> condition, string message, int seconds = 15)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition())
            {
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(seconds), message);
                await Task.Delay(50);
            }
        }
        static void Archive(string path, string title)
        {
            File.Delete(path);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var stream = archive.CreateEntry("page.png").Open())
                stream.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
            using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
            writer.Write($"<ComicInfo><Title>{title}</Title></ComicInfo>");
        }

        var directory = Path.Combine(Path.GetTempPath(), "comicrack-watch-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        var data = Path.Combine(root, "state");
        var recovery = Path.Combine(directory, "disconnected");
        Directory.CreateDirectory(data);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = directory, DisableDefaults = true
        });
        builder.Configuration["DataPath"] = data;
        ScanWorker? worker = null;
        try
        {
            var path = Path.Combine(root, "original.cbz");
            Archive(path, "Startup");
            var store = new LibraryStore(Path.Combine(data, "watch.sqlite"), [root, recovery]);
            worker = new ScanWorker(store, NullLogger<ScanWorker>.Instance, builder.Configuration, builder.Environment);
            await worker.StartAsync(default);
            await Until(() => worker.Status.Revision == 1, "Startup reconciliation did not complete.");
            Check(store.ListBooks("watch", null).Items.Single().Title == "Startup", "Startup did not import offline changes.");
            Check(worker.WatchStatus is { WatchingRoots: 1, UnavailableRoots: 1, Pending: false }, "Watch status did not report the disconnected root.");

            File.WriteAllText(Path.Combine(data, "ignored.txt"), "state writes must not trigger scans");
            await Task.Delay(3_000);
            Check(worker.Status.Revision == 1 && !worker.WatchStatus.Pending, "State writes caused a scan loop.");

            var added = Path.Combine(root, "added.cbz");
            Archive(added, "Added");
            for (var i = 0; i < 5; i++)
            {
                Archive(path, "Changed" + i);
                await Task.Delay(50);
            }
            await Until(() => worker.Status.Revision == 2, "Debounced burst did not reconcile.");
            await Task.Delay(3_000);
            Check(worker.Status.Revision == 2 && store.ListBooks("watch", null).Total == 2
                && store.ListBooks("watch", "Changed4").Total == 1, "A burst was not coalesced or modifications were missed.");
            var id = store.ListBooks("watch", "Added").Items.Single().Id;
            File.Delete(added);
            await Until(() => store.GetBook("watch", id).Availability == "missing", "Automatic delete detection failed.");
            Archive(added, "Returned");
            await Until(() => store.GetBook("watch", id).Availability == "available"
                && store.GetBook("watch", id).Title == "Returned", "Automatic reappearance detection failed.");
            await Until(() => !worker.WatchStatus.Pending && worker.Status.State == "completed"
                && worker.Status.Revision == 4, "Reappearance scan did not settle.");

            // Hold the real store scan lock, not a fake scanner, so event/cancellation races are reproducible.
            async Task WithBusyStore(Func<Task> action)
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var blocked = false;
                var holding = Task.Run(() => store.ScanAsync(default, result =>
                {
                    if (blocked || result.Scanned != 0) return;
                    blocked = true;
                    entered.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(30)), "Watch check failed to release the real scan lock.");
                }));
                try
                {
                    await Until(() => entered.IsSet, "Could not acquire the real scan lock.");
                    await action();
                }
                finally
                {
                    release.Set();
                    await holding;
                }
            }

            var revision = worker.Status.Revision;
            await WithBusyStore(async () =>
            {
                Check(worker.TryStart(out _), "Manual scan did not start.");
                Archive(Path.Combine(root, "during-scan.cbz"), "During scan");
                await Until(() => worker.WatchStatus.Pending, "An event during a scan was lost.");
                await Task.Delay(2_250);
                Check(worker.Status.State == "running" && !worker.TryStart(out _), "Scans were not single-flight.");
            });
            await Until(() => worker.Status.Revision == revision + 2 && !worker.WatchStatus.Pending,
                "An event during a scan did not cause exactly one follow-up.");
            Check(store.ListBooks("watch", "During scan").Total == 1, "The follow-up lost a source addition.");

            revision = worker.Status.Revision;
            await WithBusyStore(async () =>
            {
                Check(worker.TryStart(out _), "The cancellable scan did not start.");
                Archive(Path.Combine(root, "cancelled.cbz"), "Cancelled");
                await Until(() => worker.WatchStatus.Pending, "Cancellation fixture did not queue a change.");
                worker.Cancel();
                await Until(() => worker.Status.State == "cancelled" && worker.Status.Revision == revision + 1,
                    "Cancellation did not finish while waiting for the scan lock.");
            });
            await Task.Delay(3_000);
            Check(worker.Status.State == "cancelled" && worker.Status.Revision == revision + 1
                && !worker.WatchStatus.Pending, "Cancellation immediately restarted a queued scan.");

            await worker.StopAsync(default);
            worker.Dispose();
            Archive(path, "Offline replacement");
            worker = new ScanWorker(store, NullLogger<ScanWorker>.Instance, builder.Configuration, builder.Environment);
            await worker.StartAsync(default);
            await Until(() => worker.Status.Revision == 1 && store.ListBooks("watch", "Offline replacement").Total == 1,
                "Restart did not recover an offline modification.");
            Directory.CreateDirectory(recovery);
            Archive(Path.Combine(recovery, "recovered.cbz"), "Recovered root");
            // Exercise the real one-minute reconciliation interval; do not add production timing knobs just for this check.
            await Until(() => worker.WatchStatus is { WatchingRoots: 2, UnavailableRoots: 0 }
                && store.ListBooks("watch", "Recovered root").Total == 1, "A disconnected root did not recover automatically.", 75);
            await worker.StopAsync(default);
            Check(worker.WatchStatus.WatchingRoots == 0 && !worker.WatchStatus.Pending
                && !worker.TryStart(out _), "Shutdown did not dispose watches or prevent new scans.");
        }
        finally
        {
            if (worker is not null)
            {
                await worker.StopAsync(default);
                worker.Dispose();
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
