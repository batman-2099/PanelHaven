using System.IO.Compression;
using System.Security.Cryptography;
using ComicRack.Web;
using Microsoft.Data.Sqlite;

public static class MaintenanceChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "comicrack-maintenance-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        Directory.CreateDirectory(Path.Combine(root, "Series"));
        try
        {
            var database = Path.Combine(directory, "maintenance.sqlite");
            var original = Path.Combine(root, "Series", "original.cbz");
            var copy = Path.Combine(root, "copy.cbz");
            var different = Path.Combine(root, "different.cbz");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void Archive(string path, byte marker)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                for (var page = 0; page < 2; page++)
                {
                    using var stream = archive.CreateEntry($"{page}.png").Open();
                    stream.Write(png);
                    stream.WriteByte(marker);
                }
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write("<ComicInfo><Title>Same title</Title><Series>Series</Series><Publisher>Publisher</Publisher></ComicInfo>");
            }
            Archive(original, 1);
            File.Copy(original, copy);
            Archive(different, 2);
            var sourceBytes = File.ReadAllBytes(original);
            var differentBytes = File.ReadAllBytes(different);
            var hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            const string id = "legacy-book";
            const string legacyXml = "<ComicInfo><Title>Legacy title</Title><Unknown attribute=\"keep\">Preserved</Unknown></ComicInfo>";
            // Seed the pre-maintenance schema, including edited metadata and existing progress.
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE books (
                        id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,title TEXT NOT NULL,
                        series TEXT NOT NULL,number TEXT NOT NULL,publisher TEXT NOT NULL,summary TEXT NOT NULL,
                        page_count INTEGER NOT NULL,info_xml TEXT NOT NULL,book_xml TEXT,edited INTEGER NOT NULL DEFAULT 0,
                        file_size INTEGER NOT NULL,modified_ticks INTEGER NOT NULL);
                    CREATE TABLE progress (
                        user_id TEXT NOT NULL,book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                        page INTEGER NOT NULL,completed INTEGER NOT NULL,PRIMARY KEY(user_id,book_id));
                    INSERT INTO books VALUES($id,$path,'Legacy title','','','','',2,$xml,NULL,1,$size,$ticks);
                    INSERT INTO progress VALUES('alice',$id,1,1);
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$path", OperatingSystem.IsWindows() ? original.ToUpperInvariant() : original);
                command.Parameters.AddWithValue("$xml", legacyXml);
                command.Parameters.AddWithValue("$size", sourceBytes.Length);
                command.Parameters.AddWithValue("$ticks", File.GetLastWriteTimeUtc(original).Ticks);
                command.ExecuteNonQuery();
            }
            object? Scalar(string sql, string bookId = id)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", bookId);
                return command.ExecuteScalar();
            }
            var store = new LibraryStore(database, [root]);
            var scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.Added == 2 && scan.Updated == 1 && scan.ErrorCount == 0, "Legacy maintenance migration/backfill failed.");
            var book = store.GetBook("alice", id);
            Check(book.Title == "Legacy title" && book.CurrentPage == 1 && book.Completed
                && book.Metadata!["comicInfoXml"]!.ToString()!.Contains("attribute=\"keep\"")
                && book.Location == "Library 1/Series/original.cbz" && book.Availability == "available",
                "Migration changed old metadata/history or exposed an unsafe location.");
            Check((string?)Scalar("SELECT content_hash FROM books WHERE id=$id") == hash, "Duplicate hash is not SHA256 of actual source bytes.");
            store.EditMetadata(id, new(new() { ["Title"] = "Edited in web" }));
            store.SaveBookmark("alice", id, 1, new("Remember"));
            store.SaveProgress("bob", id, new(0, false));
            var list = store.CreateList("alice", new("Keep", "manual", "all", []));
            store.AddListBooks("alice", list.Id, [id]);
            var editedXml = store.GetBook("alice", id).Metadata!["comicInfoXml"]!.ToString();
            void Preserved()
            {
                var current = store.GetBook("alice", id);
                Check(current.Title == "Edited in web" && current.CurrentPage == 1 && current.Completed
                    && current.Metadata!["comicInfoXml"]!.ToString() == editedXml
                    && store.GetBook("bob", id).Started
                    && store.GetBookmarks("alice", id).Single() == new ReaderBookmark(1, "Remember")
                    && store.GetList("alice", list.Id).BookIds!.SequenceEqual(new[] { id }),
                    "Maintenance changed metadata, progress, bookmarks, or list membership.");
            }
            var duplicates = store.ListMaintenance("duplicates");
            Check(duplicates.Total == 2 && duplicates.Items.All(b => b.DuplicateKey == hash && b.Availability == "available")
                && duplicates.Items.Select(b => b.Location).SequenceEqual(duplicates.Items.Select(b => b.Location).Order(StringComparer.Ordinal))
                && duplicates.Items.All(b => !b.Location.Contains(directory)), "Exact duplicates or safe stable locations failed.");
            var first = store.ListMaintenance("duplicates", 1, 1);
            var second = store.ListMaintenance("duplicates", 2, 1);
            Check(first.Total == 2 && second.Total == 2 && first.Items[0].Id != second.Items[0].Id
                && first.Items[0].DuplicateKey == second.Items[0].DuplicateKey
                && store.ListMaintenance("duplicates", int.MaxValue, 1).Page == 2, "Duplicate pagination lost group continuity.");
            Reject<ArgumentException>(() => store.ListMaintenance("all"));
            Reject<ArgumentException>(() => store.ListMaintenance("missing", 0));
            Reject<ArgumentException>(() => store.ListMaintenance("missing", pageSize: 101));
            Reject<InvalidOperationException>(() => store.RemoveMissingBook(id));
            Reject<KeyNotFoundException>(() => store.RemoveMissingBook("unknown"));
            using (var held = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(store.ScanAsync(default).GetAwaiter().GetResult().Skipped == 3, "Unchanged hashed sources were reopened.");
            Check(File.ReadAllBytes(original).SequenceEqual(sourceBytes), "Maintenance or metadata editing wrote to the source archive.");

            // A changed invalid source must retain its catalog/history but stop counting as a duplicate.
            var copyId = duplicates.Items.Single(b => b.Id != id).Id;
            File.WriteAllText(copy, "corrupt replacement");
            scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.ErrorCount == 1 && store.GetBook("alice", copyId).Availability == "unavailable"
                && store.ListMaintenance("duplicates").Total == 0,
                "Changed corrupt sources counted as current duplicates or disappeared from the catalog.");
            Reject<InvalidOperationException>(() => store.RemoveMissingBook(copyId));
            File.WriteAllBytes(copy, sourceBytes);
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.ListMaintenance("duplicates").Total == 2, "Restored corrupt archive was not revalidated.");
            File.WriteAllBytes(different, sourceBytes);
            File.SetLastWriteTimeUtc(different, DateTime.UtcNow.AddSeconds(5));
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.ListMaintenance("duplicates").Total == 3, "Valid changed sources were not rehashed.");
            File.WriteAllBytes(different, differentBytes);
            File.SetLastWriteTimeUtc(different, DateTime.UtcNow.AddSeconds(10));
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.ListMaintenance("duplicates").Total == 2, "Changed different contents retained their old duplicate hash.");

            if (!OperatingSystem.IsWindows())
            {
                var saved = Path.Combine(directory, "saved.bin");
                File.WriteAllBytes(saved, sourceBytes);
                File.Delete(original);
                File.CreateSymbolicLink(original, saved);
                store.ScanAsync(default).GetAwaiter().GetResult();
                Check(store.GetBook("alice", id).Availability == "unavailable" && store.ListMaintenance("duplicates").Total == 0,
                    "Unsafe sources were treated as missing or retained a duplicate hash.");
                Reject<InvalidOperationException>(() => store.RemoveMissingBook(id));
                File.Delete(original);
                File.WriteAllBytes(original, sourceBytes);
                store.ScanAsync(default).GetAwaiter().GetResult();
                var mode = File.GetUnixFileMode(original);
                try
                {
                    File.SetUnixFileMode(original, UnixFileMode.None);
                    store.ScanAsync(default).GetAwaiter().GetResult();
                    Check(store.GetBook("alice", id).Availability == "unavailable", "Unreadable archive was treated as available or missing.");
                }
                finally { File.SetUnixFileMode(original, mode); }
                store.ScanAsync(default).GetAwaiter().GetResult();
            }

            File.Delete(original);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Reject<OperationCanceledException>(() => store.ScanAsync(cancel.Token).GetAwaiter().GetResult());
            }
            using (var cancel = new CancellationTokenSource())
                Reject<OperationCanceledException>(() => store.ScanAsync(cancel.Token, progress =>
                {
                    if (progress.Scanned == 0) Reject<InvalidOperationException>(() => store.RemoveMissingBook(id));
                    if (progress.Scanned > 0) cancel.Cancel();
                }).GetAwaiter().GetResult());
            Check(store.ListBooks("alice", null).Total == 3, "Cancellation destructively reconciled the catalog.");
            Preserved();
            var bad = Path.Combine(root, "bad.cbz");
            File.WriteAllText(bad, "broken new archive");
            scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.ErrorCount == 1 && store.ListMaintenance("missing").Items.Single().Id == id
                && store.ListBooks("alice", null).Total == 3 && store.ListMaintenance("duplicates").Total == 0,
                "Partial discovery errors hid confirmed missing sources or deleted catalog rows.");
            File.Delete(bad);
            Preserved();
            File.WriteAllBytes(original, sourceBytes);
            Reject<InvalidOperationException>(() => store.RemoveMissingBook(id));
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetBook("alice", id).Availability == "available" && store.ListMaintenance("duplicates").Total == 2,
                "Restoration did not retain the same identity and recompute its hash.");
            Preserved();

            File.Delete(original);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var offline = Path.Combine(directory, "offline");
            Directory.Move(root, offline);
            Reject<InvalidOperationException>(() => store.RemoveMissingBook(id));
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(root, offline);
                Reject<InvalidOperationException>(() => store.RemoveMissingBook(id));
                Directory.Delete(root);
            }
            scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.ErrorCount > 0 && store.ListMaintenance("missing").Total == 0
                && store.ListMaintenance("unavailable").Total == 3 && store.ListMaintenance("duplicates").Total == 0,
                "Disconnected roots were treated as missing or retained current duplicate hashes.");
            Preserved();
            Directory.Move(offline, root);
            store = new LibraryStore(database, [root]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.ListMaintenance("missing").Items.Single().Id == id, "Restart did not reconcile a reconnected root.");
            Preserved();
            store.RemoveMissingBook(id);
            Reject<KeyNotFoundException>(() => store.GetBook("alice", id));
            Check(store.GetList("alice", list.Id).BookIds!.Length == 0
                && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM progress WHERE book_id=$id")) == 0
                && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM bookmarks WHERE book_id=$id")) == 0
                && Convert.ToInt64(Scalar("SELECT COUNT(*) FROM list_books WHERE book_id=$id")) == 0
                && File.ReadAllBytes(copy).SequenceEqual(sourceBytes) && File.ReadAllBytes(different).SequenceEqual(differentBytes),
                "Explicit removal did not cascade dependent state or changed source files.");
            File.WriteAllBytes(original, sourceBytes);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var replacement = store.ListBooks("alice", null).Items.Single(b => b.Location.EndsWith("/original.cbz", StringComparison.Ordinal));
            Check(replacement.Id != id && !replacement.Started && store.GetBookmarks("alice", replacement.Id).Count == 0,
                "Explicitly removed identity/history returned on a fresh import.");
            var moved = Path.Combine(root, "moved.cbz");
            File.Move(original, moved);
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetBook("alice", replacement.Id).Availability == "missing"
                && store.ListBooks("alice", null).Items.Single(b => b.Location.EndsWith("/moved.cbz", StringComparison.Ordinal)).Id != replacement.Id
                && File.ReadAllBytes(moved).SequenceEqual(sourceBytes), "Moving a source silently transferred its identity or wrote its contents.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
