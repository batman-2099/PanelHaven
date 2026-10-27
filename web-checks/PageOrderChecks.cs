using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PanelHaven.Web;

public static class PageOrderChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-page-order-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var state = Path.Combine(directory, "state");
        var database = Path.Combine(state, "panelhaven.sqlite");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "pages.cbz");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void WriteSource(int count)
            {
                using var file = File.Create(path);
                using var archive = new ZipArchive(file, ZipArchiveMode.Create);
                archive.Comment = "stable-A";
                for (var page = 0; page < count; page++)
                {
                    using var image = archive.CreateEntry($"{page}.png").Open();
                    image.Write(png);
                }
                using var xml = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                xml.Write("<ComicInfo><Title>Pages</Title><Pages><Page Image='1' Type='Story' Bookmark='Embedded'/></Pages></ComicInfo>");
            }
            object? Sql(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            WriteSource(3);
            var original = File.ReadAllBytes(path);
            var mode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);
            var store = new LibraryStore(database, [root]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Page order fixture did not import.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            var xmlBefore = (string)Sql("SELECT info_xml FROM books")!;
            store.SaveProgress("owner", id, new(1, true));
            store.SaveBookmark("owner", id, 2, new("Private"));
            store.SaveRating("owner", id, new(4));
            var list = store.CreateList("owner", new("Keep", "manual", "all", []));
            store.AddListBooks("owner", list.Id, [id]);
            void PrivateState()
            {
                var book = store.GetBook("owner", id);
                Check(book.Id == id && book.CurrentPage == 1 && book.Completed && book.PersonalRating == 4
                    && store.GetBookmarks("owner", id).Single() == new ReaderBookmark(2, "Private")
                    && store.GetList("owner", list.Id).BookIds!.Single() == id
                    && !store.GetBook("other", id).Started && store.GetBookmarks("other", id).Count == 0,
                    "Shared order changed physical progress, bookmarks, identity, or private state.");
            }
            var initial = store.GetPageOrder(id);
            Check(!initial.Custom && initial.Pages.SequenceEqual([0, 1, 2]), "Initial order was not physical archive order.");
            foreach (var pages in new int[][] { [], [0, 1], [0, 1, 1], [0, 1, 3], [-1, 1, 2], [0, 1, 2, 3], null! })
                Reject<ArgumentException>(() => store.SavePageOrder(id, new(initial.Revision, pages)));
            Reject<ArgumentException>(() => store.SavePageOrder(id, null!));
            Reject<ArgumentException>(() => store.SavePageOrder(id, new(null!, [0, 1, 2])));
            Reject<KeyNotFoundException>(() => store.GetPageOrder("missing"));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var json in new[]
            {
                "{}", "{\"expectedRevision\":\"x\"}", "{\"pages\":[0,1,2]}",
                "{\"expectedRevision\":\"x\",\"pages\":[0,1,2],\"extra\":true}",
                "{\"expectedRevision\":\"x\",\"pages\":[0,1,2.5]}",
                "{\"expectedRevision\":\"x\",\"pages\":[0,1,\"2\"]}",
                "{\"expectedRevision\":\"x\",\"pages\":[0,1,null]}"
            }) Reject<JsonException>(() => JsonSerializer.Deserialize<PageOrderEdit>(json, options));
            var saved = store.SavePageOrder(id, new(initial.Revision, [2, 0, 1]));
            Check(saved.Custom && saved.Pages.SequenceEqual([2, 0, 1]) && saved.Revision != initial.Revision,
                "Shared order was not saved with a new revision.");
            // A custom GET hashes source bytes without waiting for an unrelated SQLite writer.
            using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                writer.Open();
                using var reservation = writer.BeginTransaction(deferred: false);
                var reading = Task.Run(() => store.GetPageOrder(id));
                try
                {
                    Check(reading.Wait(TimeSpan.FromSeconds(5)) && reading.Result.Revision == saved.Revision,
                        "Page-order GET waited for the SQLite writer.");
                }
                finally
                {
                    reservation.Rollback();
                    reading.GetAwaiter().GetResult();
                }
            }
            // Separate stores have separate scan locks: the database snapshot must guard competing saves.
            var concurrentStore = new LibraryStore(database, [root]);
            using (var barrier = new Barrier(2))
            {
                var expected = saved.Revision;
                Task<BookPageOrder?> Save(LibraryStore target, int[] pages) => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { return target.SavePageOrder(id, new(expected, pages)); }
                    catch (MetadataRevisionException) { return null; }
                });
                var first = Save(store, [1, 2, 0]);
                var second = Save(concurrentStore, [2, 1, 0]);
                Task.WaitAll(first, second);
                Check((first.Result is null) != (second.Result is null), "Competing page-order saves both committed.");
                saved = first.Result ?? second.Result!;
                Check(store.GetPageOrder(id).Revision == saved.Revision, "A stale page-order save overwrote the winner.");
            }
            Reject<MetadataRevisionException>(() => store.SavePageOrder(id, new(initial.Revision, [1, 2, 0])));
            var reset = store.SavePageOrder(id, new(saved.Revision, [0, 1, 2]));
            Check(!reset.Custom && reset.Revision != initial.Revision && Sql("SELECT pages_json FROM page_orders") is DBNull,
                "Reset retained an override or resurrected an old natural-order revision.");
            Reject<MetadataRevisionException>(() => store.SavePageOrder(id, new(initial.Revision, [2, 1, 0])));
            saved = store.SavePageOrder(id, new(reset.Revision, [1, 2, 0]));
            store = new LibraryStore(database, [root]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetPageOrder(id).Revision == saved.Revision, "Restart or unchanged scan lost shared order.");
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetPageOrder(id).Revision == saved.Revision, "Byte-identical rescan discarded shared order.");
            Check(File.ReadAllBytes(path).SequenceEqual(original) && (string)Sql("SELECT info_xml FROM books")! == xmlBefore
                && (OperatingSystem.IsWindows() || File.GetUnixFileMode(path) == mode)
                && store.GetPageInfo(id).Single().Page == 1 && store.GetPage(id, 2).Bytes.SequenceEqual(png),
                "Page order mutated source bytes, permissions, ComicInfo, or physical image identity.");
            PrivateState();
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(Path.Combine(root, "Unknown Publisher", "Unknown Series"));
                var move = store.PreviewFileMove(id, new(0, "", "renamed.cbz"));
                store.ApplyFileMove(id, new(0, "", "renamed.cbz", move.Revision, true));
                Check(store.GetPageOrder(id).Revision == saved.Revision && File.Exists(Path.Combine(root, "Unknown Publisher", "Unknown Series", "renamed.cbz")),
                    "Move changed shared order or omitted the publisher/series folders.");
                var returnMove = store.PreviewFileMove(id, new(0, "", "pages.cbz"));
                store.ApplyFileMove(id, new(0, "", "pages.cbz", returnMove.Revision, true));
                path = Path.Combine(root, "Unknown Publisher", "Unknown Series", "pages.cbz");
                Check(store.GetPageOrder(id).Revision == saved.Revision && File.Exists(path) && store.GetFileOperations(id).Count == 0,
                    "Return move changed shared order, lost the source, or retained completed history.");
                PrivateState();
            }
            var stamp = File.GetLastWriteTimeUtc(path);
            var changedBytes = (byte[])original.Clone();
            changedBytes[^1] = (byte)'B';
            File.WriteAllBytes(path, changedBytes);
            File.SetLastWriteTimeUtc(path, stamp);
            Reject<InvalidOperationException>(() => store.GetPageOrder(id));
            Check((string)Sql("SELECT availability FROM books")! == "unavailable", "Hidden source replacement remained scan-skippable.");
            store.ScanAsync(default).GetAwaiter().GetResult();
            var changed = store.GetPageOrder(id);
            Check(!changed.Custom && changed.Pages.SequenceEqual([0, 1, 2]), "Same-count source replacement reused stale permutation.");
            Reject<MetadataRevisionException>(() => store.SavePageOrder(id, new(saved.Revision, saved.Pages)));
            PrivateState();
            // Default GET stays cheap; PUT must still detect a replacement whose size and timestamp match.
            stamp = File.GetLastWriteTimeUtc(path);
            File.WriteAllBytes(path, original);
            File.SetLastWriteTimeUtc(path, stamp);
            var natural = store.GetPageOrder(id);
            Reject<InvalidOperationException>(() => store.SavePageOrder(id, new(natural.Revision, [2, 0, 1])));
            store.ScanAsync(default).GetAwaiter().GetResult();
            saved = store.SavePageOrder(id, new(store.GetPageOrder(id).Revision, [1, 2, 0]));
            Reject<MetadataRevisionException>(() => store.SavePageOrder(id, new(reset.Revision, [2, 0, 1])));

            var destination = Path.Combine(directory, "relocated");
            Directory.Move(root, destination);
            path = Path.Combine(destination, Path.GetRelativePath(root, path));
            store = new LibraryStore(database, [root, destination]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var duplicate = store.ListBooks("owner", null).Items.Single(book => book.Id != id).Id;
            var duplicateOrder = store.SavePageOrder(duplicate, new(store.GetPageOrder(duplicate).Revision, [2, 1, 0]));
            var relocate = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["relocate-from"] = root, ["relocate-to"] = destination, ["apply-relocation"] = "true"
            }).Build();
            RelocateCatalog.Run(relocate, state, [destination]);
            Check((long)Sql("SELECT COUNT(*) FROM books")! == 2, "Relocation silently merged conflicting shared page orders.");
            store.SavePageOrder(duplicate, new(duplicateOrder.Revision, [0, 1, 2]));
            RelocateCatalog.Run(relocate, state, [destination]);
            store = new LibraryStore(database, [destination]);
            Check((long)Sql("SELECT COUNT(*) FROM books")! == 1 && store.GetPageOrder(id).Pages.SequenceEqual(saved.Pages),
                "Relocation merge dropped the surviving shared order or book ID.");
            PrivateState();
            // Metadata writing also changes source bytes: never guess whether the physical page identity survived.
            store.EditMetadata(id, new(new() { ["Title"] = "Written" }));
            var write = store.PreviewArchiveWrite(id);
            var beforeWrite = File.ReadAllBytes(path);
            var written = store.ApplyArchiveWrite(id, new(write.Revision, true));
            Check(written.BookId == id && !File.ReadAllBytes(path).SequenceEqual(beforeWrite)
                && !store.GetPageOrder(id).Custom, "Metadata rewrite did not change source bytes or retained a source-bound permutation.");
            Reject<MetadataRevisionException>(() => store.SavePageOrder(id, new(saved.Revision, saved.Pages)));
            saved = store.SavePageOrder(id, new(store.GetPageOrder(id).Revision, [2, 0, 1]));
            WriteSource(2);
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetPageOrder(id).Pages.SequenceEqual([0, 1]) && !store.GetPageOrder(id).Custom,
                "Shrinking source retained missing or invalid physical page IDs.");
            Reject<MetadataRevisionException>(() => store.SavePageOrder(id, new(saved.Revision, saved.Pages)));
            File.Delete(path);
            store.ScanAsync(default).GetAwaiter().GetResult();
            store.RemoveMissingBook(id);
            Check((long)Sql("SELECT COUNT(*) FROM page_orders")! == 0, "Catalog removal orphaned shared order state.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
