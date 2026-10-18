using System.IO.Compression;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;

public static class LibraryChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            void CreateArchive(string path, string xml, string pageName = "page2.gif", int extraPages = 0)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("page10.png").Open()) stream.Write(png);
                using (var stream = archive.CreateEntry(pageName).Open()) stream.Write(gif);
                for (var i = 0; i < extraPages; i++)
                    using (var stream = archive.CreateEntry($"extra{i}.png").Open()) stream.Write(png);
                using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open())) writer.Write(xml);
                using (var writer = new StreamWriter(archive.CreateEntry("ComicBook.xml").Open()))
                    writer.Write("<ComicBook><Title>Legacy</Title><CurrentPage>8</CurrentPage><CustomValues><Value key='custom'>retained</Value></CustomValues></ComicBook>");
            }
            var archivePath = Path.Combine(directory, "sample.cbz");
            CreateArchive(archivePath, "<ComicInfo><Title>Imported</Title><Unknown key='kept'>value</Unknown><Pages><Page Image='0'/></Pages></ComicInfo>");
            var store = new LibraryStore(Path.Combine(directory, "test.sqlite"), [directory]);
            var scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.Scanned == 1 && scan.Added == 1 && scan.Updated == 0 && scan.Skipped == 0 && scan.ErrorCount == 0,
                "Valid CBZ import failed.");
            var book = store.ListBooks("alice", null).Items.Single();
            Check(book.Title == "Imported" && book.CurrentPage == 0 && !book.Started && book.Metadata is null,
                "ComicInfo precedence, legacy progress isolation, or list metadata exclusion failed.");
            Check(store.GetPage(book.Id, 0).Bytes.SequenceEqual(gif), "Pages are not naturally ordered.");
            Check(store.GetPage(book.Id, 1).Bytes.SequenceEqual(png), "Page byte delivery changed the source.");
            store.SaveProgress("alice", book.Id, new ReadingProgress(1, true));
            Check(store.GetBook("alice", book.Id).CurrentPage == 1 && store.GetBook("alice", book.Id).Started
                && store.GetBook("bob", book.Id).CurrentPage == 0 && !store.GetBook("bob", book.Id).Started,
                "Per-user reading progress leaked.");
            Reject<ArgumentException>(() => store.SaveProgress("alice", book.Id, new ReadingProgress(2, false)));
            Reject<ArgumentException>(() => store.GetPage(book.Id, -1));
            store.EditMetadata(book.Id, new MetadataEdit(new()
            {
                ["Title"] = "Edited", ["Series"] = "Series", ["Number"] = "1",
                ["Publisher"] = "Publisher", ["Summary"] = "Summary"
            }));
            var revision = store.GetCoverVersion(book.Id);
            using (var locked = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                scan = store.ScanAsync(default).GetAwaiter().GetResult();
                Check(scan.Scanned == 1 && scan.Skipped == 1 && scan.Added == 0 && scan.Updated == 0 && scan.ErrorCount == 0,
                    "Unchanged source was reopened instead of skipped.");
                Check(store.GetCoverVersion(book.Id) == revision, "Cover revision required reopening the archive.");
            }
            var edited = store.GetBook("alice", book.Id);
            Check(edited.Title == "Edited" && ((string)edited.Metadata!["comicInfoXml"]!).Contains("key=\"kept\""),
                "Rescan lost web edits or unknown ComicInfo metadata.");
            Check(((string)edited.Metadata!["comicBookXml"]!).Contains("CustomValues"), "ComicBook metadata was lost.");
            Check(store.ListBooks("alice", "publisher").Total == 1, "Metadata search failed.");
            using (var original = ZipFile.OpenRead(archivePath))
            using (var reader = new StreamReader(original.GetEntry("ComicInfo.xml")!.Open()))
                Check(reader.ReadToEnd().Contains("<Title>Imported</Title>"), "Metadata edit wrote to the source archive.");
            var previousModified = File.GetLastWriteTimeUtc(archivePath);
            File.Delete(archivePath);
            CreateArchive(archivePath, "<ComicInfo><Title>Changed source</Title></ComicInfo>", extraPages: 1);
            File.SetLastWriteTimeUtc(archivePath, previousModified.AddSeconds(2));
            Reject<InvalidOperationException>(() => store.GetCoverVersion(book.Id));
            Reject<InvalidOperationException>(() => store.GetPage(book.Id, 0));
            scan = store.ScanAsync(default).GetAwaiter().GetResult();
            edited = store.GetBook("alice", book.Id);
            Check(scan.Updated == 1 && scan.Added == 0 && scan.Skipped == 0 && scan.ErrorCount == 0
                && edited.Title == "Edited" && edited.PageCount == 3 && edited.CurrentPage == 1 && edited.Completed
                && ((string)edited.Metadata!["comicInfoXml"]!).Contains("key=\"kept\""),
                "Changed import lost edits, unknown metadata, reading progress, or refreshed page count.");
            Check(store.GetCoverVersion(book.Id) != revision, "Changed import did not invalidate the thumbnail revision.");

            CreateArchive(Path.Combine(directory, "traversal.cbz"), "<ComicInfo/>", "../page.gif");
            CreateArchive(Path.Combine(directory, "dtd.cbz"), "<!DOCTYPE ComicInfo [<!ENTITY x 'bad'>]><ComicInfo>&x;</ComicInfo>");
            CreateArchive(Path.Combine(directory, "wrong-root.cbz"), "<Other/>");
            scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.Scanned == 4 && scan.Skipped == 1 && scan.ErrorCount == 3 && scan.Errors.Count == 3
                && store.ListBooks("alice", null).Total == 1, "Unsafe archives were imported.");
            Check(scan.Errors.All(e => !e.Contains(directory)), "Scan errors exposed absolute paths.");

            File.WriteAllText(archivePath, "broken archive");
            scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.Errors.Count == 4 && store.GetBook("alice", book.Id).Title == "Edited", "Failed rescan deleted metadata.");
            File.Delete(archivePath);
            Reject<FileNotFoundException>(() => store.GetPage(book.Id, 0));
            Reject<FileNotFoundException>(() => store.GetCoverVersion(book.Id));
            Check(store.GetBook("alice", book.Id).Completed, "Missing source erased reading progress.");
            var unconfigured = new LibraryStore(Path.Combine(directory, "empty.sqlite"), []);
            Check(unconfigured.ScanAsync(default).GetAwaiter().GetResult().Errors.Count == 1, "Missing roots were reported as a successful scan.");

            var queryRoot = Path.Combine(directory, "queries");
            Directory.CreateDirectory(queryRoot);
            var numbers = new[] { "10", "2", "1", "20", "3", "11" };
            var titles = new[] { "Zulu", "Alpha", "Mike", "Bravo", "Echo", "Delta" };
            var publishers = new[] { "Beta", "Alpha", "Alpha", "", "Beta", "alpha" };
            for (var i = 0; i < numbers.Length; i++)
                CreateArchive(Path.Combine(queryRoot, $"{numbers[i]}.cbz"),
                    $"<ComicInfo><Title>{titles[i]}</Title><Series>{(i < 3 ? "One" : "Two")}</Series><Number>{numbers[i]}</Number>"
                    + $"<Publisher>{publishers[i]}</Publisher><Summary>marker {i}</Summary></ComicInfo>", extraPages: i % 3);
            var queryDb = Path.Combine(directory, "queries.sqlite");
            var queries = new LibraryStore(queryDb, [queryRoot]);
            Check(queries.ScanAsync(default).GetAwaiter().GetResult().Added == 6, "Query fixture import failed.");
            var all = queries.ListBooks("alice", null, pageSize: 100).Items;
            var pages = Enumerable.Range(1, 3).Select(page => queries.ListBooks("alice", null, page, 2)).ToArray();
            Check(pages.All(page => page.Total == 6 && page.Items.Count == 2 && page.PageSize == 2)
                && pages.Select(page => page.Page).SequenceEqual([1, 2, 3])
                && pages.SelectMany(page => page.Items).Select(item => item.Id).SequenceEqual(all.Select(item => item.Id))
                && pages.SelectMany(page => page.Items).Select(item => item.Id).Distinct().Count() == 6,
                "Pagination totals, ordering, bounds, or duplicate handling failed.");
            Check(queries.ListBooks("alice", null, int.MaxValue, 2).Page == 3, "Out-of-range page was not clamped.");
            var empty = queries.ListBooks("alice", "not present", int.MaxValue, 2);
            Check(empty.Total == 0 && empty.Page == 1 && empty.Items.Count == 0, "Empty result page was not normalized.");
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, 0));
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, pageSize: 0));
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, pageSize: 101));
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, sort: "title; DROP TABLE books"));
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, direction: "sideways"));
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, status: "unknown"));
            Reject<ArgumentException>(() => queries.ListBooks("alice", new string('q', 2001)));
            Reject<ArgumentException>(() => queries.ListBooks("alice", null, publisher: new string('p', 2001)));
            foreach (var sort in new[] { "series", "title", "publisher", "pageCount" })
            foreach (var direction in new[] { "asc", "desc" })
            {
                var descending = direction == "desc";
                IOrderedEnumerable<Book> expected;
                if (sort == "pageCount")
                    expected = descending ? all.OrderByDescending(b => b.PageCount) : all.OrderBy(b => b.PageCount);
                else
                {
                    string Key(Book value) => sort switch
                    {
                        "series" => value.Series, "title" => value.Title, _ => value.Publisher
                    };
                    expected = descending ? all.OrderByDescending(Key, StringComparer.OrdinalIgnoreCase)
                        : all.OrderBy(Key, StringComparer.OrdinalIgnoreCase);
                }
                Check(queries.ListBooks("alice", null, sort: sort, direction: direction).Items.Select(b => b.Id)
                    .SequenceEqual(expected.ThenBy(b => b.Id, StringComparer.Ordinal).Select(b => b.Id)),
                    $"SQL sort or stable ID tie-break failed: {sort} {direction}.");
            }
            Check(queries.ListBooks("alice", null, sort: "number").Items.Select(b => b.Number)
                .SequenceEqual(["1", "2", "3", "10", "11", "20"]), "Issue numbers were sorted lexically.");
            Check(queries.ListBooks("alice", null, sort: "number", direction: "desc").Items.Select(b => b.Number)
                .SequenceEqual(["20", "11", "10", "3", "2", "1"]), "Descending natural number sort failed.");
            var completed = all.Single(b => b.Number == "1");
            var started = all.Single(b => b.Number == "2");
            queries.SaveProgress("alice", completed.Id, new ReadingProgress(1, true));
            queries.SaveProgress("alice", started.Id, new ReadingProgress(0, false));
            Check(queries.ListBooks("alice", null, status: "unread").Total == 4
                && queries.ListBooks("bob", null, status: "unread").Total == 6
                && queries.ListBooks("bob", null, status: "completed").Total == 0
                && queries.ListBooks("bob", null, status: "in-progress").Total == 0,
                "Reading filters leaked another user's progress.");
            var inProgress = queries.ListBooks("alice", null, status: "in-progress").Items.Single();
            Check(inProgress.Id == started.Id && inProgress.Started && inProgress.CurrentPage == 0 && !inProgress.Completed,
                "Starting page zero was treated as unread.");
            Check(queries.ListBooks("alice", " marker 2 ", status: "completed", publisher: "Alpha").Items.Single().Id == completed.Id
                && queries.ListBooks("alice", null, publisher: "Alpha").Total == 2
                && queries.ListBooks("alice", null, publisher: "alpha").Total == 1
                && queries.ListBooks("alice", null, publisher: "' OR 1=1 --").Total == 0,
                "Combined search, status, or exact publisher filtering failed.");
            var filteredPage = queries.ListBooks("alice", null, 2, 1, "number", status: "unread", publisher: "Beta");
            Check(filteredPage.Total == 2 && filteredPage.Items.Single().Number == "10", "Filtering happened after pagination.");
            Check(queries.ListPublishers().SequenceEqual(["Alpha", "alpha", "Beta"]), "Publisher choices were missing, duplicated, or empty.");

            var restrictedRoot = Path.Combine(directory, "restricted");
            Directory.CreateDirectory(restrictedRoot);
            var restricted = new LibraryStore(queryDb, [restrictedRoot]);
            Reject<UnauthorizedAccessException>(() => restricted.GetCoverVersion(completed.Id));
            Reject<UnauthorizedAccessException>(() => restricted.GetPage(completed.Id, 0));
            if (!OperatingSystem.IsWindows())
            {
                var linkedPath = Path.Combine(queryRoot, "1.cbz");
                var target = Path.Combine(directory, "outside.cbz");
                File.Move(linkedPath, target);
                try
                {
                    File.CreateSymbolicLink(linkedPath, target);
                    Reject<UnauthorizedAccessException>(() => queries.GetCoverVersion(completed.Id));
                    Reject<UnauthorizedAccessException>(() => queries.GetPage(completed.Id, 0));
                    scan = queries.ScanAsync(default).GetAwaiter().GetResult();
                    Check(scan.Scanned == 6 && scan.Skipped == 5 && scan.ErrorCount == 1,
                        "Matching size/mtime incorrectly skipped an unsafe symlink.");
                }
                finally
                {
                    File.Delete(linkedPath);
                    File.Move(target, linkedPath);
                }
            }

            var resumeRoot = Path.Combine(directory, "resume");
            var nested = Path.Combine(resumeRoot, "nested");
            Directory.CreateDirectory(nested);
            CreateArchive(Path.Combine(resumeRoot, "first.cbz"), "<ComicInfo/>");
            for (var i = 0; i < 4; i++) CreateArchive(Path.Combine(nested, $"{i}.cbz"), "<ComicInfo/>");
            var resume = new LibraryStore(Path.Combine(directory, "resume.sqlite"), [nested, resumeRoot, nested + Path.DirectorySeparatorChar]);
            var snapshots = new List<ScanResult>();
            using (var cancellation = new CancellationTokenSource())
                Reject<OperationCanceledException>(() => resume.ScanAsync(cancellation.Token, result =>
                {
                    snapshots.Add(result);
                    if (result.Added == 1) cancellation.Cancel();
                }).GetAwaiter().GetResult());
            Check(snapshots[0].Scanned == 0 && snapshots[^1].Scanned == 1 && snapshots[^1].Added == 1
                && resume.ListBooks("alice", null).Total == 1, "Cancellation lost a committed book or the final progress snapshot.");
            snapshots.Clear();
            scan = resume.ScanAsync(default, snapshots.Add).GetAwaiter().GetResult();
            Check(scan.Scanned == 5 && scan.Added == 4 && scan.Skipped == 1 && scan.Updated == 0 && scan.ErrorCount == 0
                && resume.ListBooks("alice", null).Total == 5, "Cancelled scan was not resumable or overlapping roots were scanned twice.");
            Check(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.Scanned <= pair.Second.Scanned
                && pair.First.Added <= pair.Second.Added && pair.First.Skipped <= pair.Second.Skipped)
                && snapshots[^1].Scanned == scan.Scanned && snapshots[^1].Added == scan.Added,
                "Progress was not monotonic or the terminal snapshot was stale.");
            using (var waiting = new CancellationTokenSource())
            {
                Task<ScanResult>? queued = null;
                resume.ScanAsync(default, result =>
                {
                    if (result.Scanned != 0) return;
                    queued = resume.ScanAsync(waiting.Token);
                    Check(!queued.IsCompleted, "Concurrent store scans were not serialized.");
                    waiting.Cancel();
                }).GetAwaiter().GetResult();
                Reject<OperationCanceledException>(() => queued!.GetAwaiter().GetResult());
            }
            using (var cancellation = new CancellationTokenSource())
            {
                snapshots.Clear();
                cancellation.Cancel();
                Reject<OperationCanceledException>(() => resume.ScanAsync(cancellation.Token, snapshots.Add).GetAwaiter().GetResult());
                Check(snapshots.Single().Scanned == 0, "A pre-cancelled scan did not publish its final snapshot.");
            }

            var errorsRoot = Path.Combine(directory, "errors");
            Directory.CreateDirectory(errorsRoot);
            for (var i = 0; i < 105; i++) File.WriteAllText(Path.Combine(errorsRoot, $"{i}.cbz"), "invalid archive");
            var invalid = new LibraryStore(Path.Combine(directory, "errors.sqlite"), [errorsRoot]);
            snapshots.Clear();
            scan = invalid.ScanAsync(default, snapshots.Add).GetAwaiter().GetResult();
            Check(scan.Scanned == 105 && scan.ErrorCount == 105 && scan.Errors.Count == 100
                && snapshots.First(result => result.ErrorCount == 1).Errors.Count == 1
                && snapshots[^1].ErrorCount == 105 && scan.Errors.All(error => !error.Contains(directory)),
                "Errors were unbounded, undercounted, mutated after publication, or exposed absolute paths.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
