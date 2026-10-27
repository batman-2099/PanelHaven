using System.IO.Compression;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class LibraryLocationChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid location configuration/filter was accepted.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-locations-" + Guid.NewGuid().ToString("N"));
        var regular = Path.Combine(directory, "Sorted");
        var archived = Path.Combine(directory, "Archived_%'");
        var nearby = archived + "-nearby";
        var unsorted = Path.Combine(directory, "Unsorted");
        var database = Path.Combine(directory, "state", "library.sqlite");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
        try
        {
            foreach (var (root, title, publisher) in new[] {
                (regular, "Regular", "Regular publisher"), (nearby, "Nearby", "Nearby publisher"),
                (archived, "Archived", "Archived publisher"), (unsorted, "Intake", "Intake publisher") })
            {
                Directory.CreateDirectory(root);
                using var comic = ZipFile.Open(Path.Combine(root, title + ".cbz"), ZipArchiveMode.Create);
                using (var page = comic.CreateEntry("page.png").Open()) page.Write(png);
                using var metadata = new StreamWriter(comic.CreateEntry("ComicInfo.xml").Open());
                metadata.Write($"<ComicInfo><Title>{title}</Title><Series>Shared series</Series><Publisher>{publisher}</Publisher></ComicInfo>");
            }
            var store = new LibraryStore(database, [regular, nearby], archived, unsorted);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 4, "Scanning omitted a configured area.");
            var normal = store.ListBooks("alice", null);
            Check(normal.Total == 2 && normal.Items.Select(book => book.Title).ToHashSet().SetEquals(["Regular", "Nearby"]),
                "Default browsing leaked special areas or hid a prefix-neighbor library.");
            var saved = store.ListBooks("alice", null, location: "archived").Items.Single();
            var intake = store.ListBooks("alice", null, location: "unsorted").Items.Single();
            var regularBook = normal.Items.Single(book => book.Title == "Regular");
            Check(saved.Title == "Archived" && intake.Title == "Intake", "Explicit area selection mixed locations.");
            Check(store.ListBooks("alice", "Archived").Total == 0, "Default search exposed archived comicbooks.");
            store.EditMetadata(regularBook.Id, new(new() { ["Number"] = "30" }));
            store.EditMetadata(normal.Items.Single(book => book.Title == "Nearby").Id, new(new() { ["Number"] = "32" }));
            store.EditMetadata(saved.Id, new(new() { ["Number"] = "31" }));
            store.EditMetadata(intake.Id, new(new() { ["Number"] = "40" }));
            Check(store.ListMissingIssues().Items.SequenceEqual(new[] {
                new MissingIssueReport("Shared series", "library", 31, 31) }),
                "Missing issue report merged locations or inferred leading/trailing gaps.");
            foreach (var (area, count, publishers) in new[] {
                ("library", 2, new[] { "Nearby publisher", "Regular publisher" }),
                ("archived", 1, new[] { "Archived publisher" }), ("unsorted", 1, new[] { "Intake publisher" }) })
            {
                var groups = store.ListGroups("alice", null, "series", location: area);
                Check(groups.Total == 1 && groups.Items.Single().Count == count, "Group counts ignored area scope.");
                Check(store.ListPublishers(area).SequenceEqual(publishers), "Publisher filters leaked another area.");
                var first = store.ListBooks("alice", null, pageSize: 1, location: area);
                Check(first.Total == count && first.Items.Count == 1, "Location filtering ran after pagination.");
            }
            var list = store.CreateList("alice", new("Across areas", "manual", "all", []));
            store.AddListBooks("alice", list.Id, [regularBook.Id, saved.Id, intake.Id]);
            foreach (var (area, id) in new[] { ("library", regularBook.Id), ("archived", saved.Id), ("unsorted", intake.Id) })
                Check(store.ListBooks("alice", null, listId: list.Id, location: area).Items.Single().Id == id,
                    "Reading-list filtering bypassed the selected area.");
            var smart = store.CreateList("alice", new("Shared", "smart", "all", [new("Series", "equals", "Shared series")]));
            Check(store.ListBooks("alice", null, listId: smart.Id).Total == 2
                && store.ListBooks("alice", null, listId: smart.Id, location: "archived").Total == 1,
                "Smart list membership bypassed default or explicit location filtering.");
            Check(store.PreviewSmartList("alice", new("all", [new("Series", "equals", "Shared series")])).Total == 2,
                "Default smart-list preview exposed hidden locations.");
            Check(store.GetPage(saved.Id, 0).Bytes.SequenceEqual(png) && store.GetPage(intake.Id, 0).Bytes.SequenceEqual(png),
                "Hidden areas lost direct reading access.");
            _ = new LibraryStore(database, [regular, nearby, archived, unsorted]);
            store = new LibraryStore(database, [regular, nearby], archived, unsorted);
            Check(store.GetBook("alice", saved.Id).Location.StartsWith("Archived/", StringComparison.Ordinal)
                && store.GetBook("alice", intake.Id).Location.StartsWith("Unsorted/", StringComparison.Ordinal),
                "Reconfiguring regular roots as special areas left stale move-destination labels.");
            using (var paused = new ManualResetEventSlim())
            using (var resume = new ManualResetEventSlim())
            {
                var scanning = Task.Run(() => store.ScanAsync(default, _ =>
                {
                    if (paused.IsSet) return;
                    paused.Set();
                    resume.Wait();
                }));
                Task<BookPageOrder>? reading = null;
                try
                {
                    Check(paused.Wait(TimeSpan.FromSeconds(5)), "Scan did not reach its progress callback.");
                    reading = Task.Run(() => store.GetPageOrder(saved.Id));
                    Check(reading.Wait(TimeSpan.FromSeconds(5)) && reading.Result.Pages.SequenceEqual([0]),
                        "Reading an indexed comicbook waited for the whole library scan.");
                }
                finally
                {
                    resume.Set();
                    scanning.GetAwaiter().GetResult();
                    reading?.GetAwaiter().GetResult();
                }
            }
            Reject(() => store.ListBooks("alice", null, location: "everything"));
            Reject(() => store.ListGroups("alice", null, "series", location: "everything"));
            Reject(() => store.ListPublishers("everything"));
            Reject(() => new LibraryStore(database, [regular], regular, unsorted));
            Reject(() => new LibraryStore(database, [regular], Path.Combine(regular, "nested"), unsorted));
            Reject(() => new LibraryStore(database, [regular], archived, Path.Combine(archived, "nested")));
            if (OperatingSystem.IsLinux())
            {
                var roots = store.GetFileOperationRoots();
                var archiveIndex = roots.Single(root => root.Label == "Archived").Index;
                var originalBytes = File.ReadAllBytes(Path.Combine(regular, "Regular.cbz"));
                store.SaveProgress("alice", regularBook.Id, new(0, true));
                store.SaveRating("alice", regularBook.Id, new(4));
                Directory.CreateDirectory(Path.Combine(archived, "Regular publisher", "Shared series"));
                var preview = store.PreviewFileMove(regularBook.Id, new(archiveIndex, "", "Moved.cbz"));
                Check(preview.CanApply, "Moving into Archived was blocked.");
                store.ApplyFileMove(regularBook.Id, new(archiveIndex, "", "Moved.cbz", preview.Revision, true));
                Check(store.ListBooks("alice", null).Total == 1 && store.ListBooks("alice", null, location: "archived").Total == 2,
                    "A move did not immediately change browsing scope.");
                var book = store.GetBook("alice", regularBook.Id);
                Check(book.Completed && book.PersonalRating == 4 && !store.GetBook("bob", regularBook.Id).Started
                    && store.GetList("alice", list.Id).BookIds!.Contains(regularBook.Id)
                    && File.ReadAllBytes(Path.Combine(archived, "Regular publisher", "Shared series", "Moved.cbz")).SequenceEqual(originalBytes),
                    "Archiving changed comicbook identity, contents, or private data.");
                var returnMove = store.PreviewFileMove(regularBook.Id, new(0, "", "Regular.cbz"));
                store.ApplyFileMove(regularBook.Id, new(0, "", "Regular.cbz", returnMove.Revision, true));
                Check(store.ListBooks("alice", null).Total == 2 && store.ListBooks("alice", null, location: "archived").Total == 1
                    && File.ReadAllBytes(Path.Combine(regular, "Regular publisher", "Shared series", "Regular.cbz")).SequenceEqual(originalBytes)
                    && store.GetFileOperations(regularBook.Id).Count == 0,
                    "Return move did not restore the original area or retained completed history.");
                Directory.CreateDirectory(Path.Combine(regular, "Intake publisher", "Shared series"));
                preview = store.PreviewFileMove(intake.Id, new(0, "", "Organized.cbz"));
                store.ApplyFileMove(intake.Id, new(0, "", "Organized.cbz", preview.Revision, true));
                Check(store.ListBooks("alice", null, location: "unsorted").Total == 0
                    && store.ListBooks("alice", null).Items.Any(book => book.Id == intake.Id),
                    "Organized intake comicbook remained in Unsorted or lost its identity.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
