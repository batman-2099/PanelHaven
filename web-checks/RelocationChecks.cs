using System.IO.Compression;
using System.Security.Cryptography;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

public static class RelocationChecks
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
            throw new Exception("Unsafe relocation arguments were accepted.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-relocation-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(directory, "old");
        var to = Path.Combine(directory, "new");
        var sibling = from + "-sibling";
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        Directory.CreateDirectory(sibling);
        var database = Path.Combine(directory, "panelhaven.sqlite");
        static string Key(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void Archive(string path, string title)
            {
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                for (var i = 0; i < 3; i++)
                {
                    using var stream = archive.CreateEntry($"{i}.png").Open();
                    stream.Write(png);
                }
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write($"<ComicInfo><Title>{title}</Title><Writer>Original</Writer><Year>2001</Year></ComicInfo>");
            }
            object? Sql(string sql, params (string Name, object? Value)[] values)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString());
                db.Open();
                db.CreateCollation("NATURAL_NUMBER", StringComparer.Ordinal.Compare);
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            string Id(string root, string name) => (string)Sql("SELECT id FROM books WHERE path=$path", ("$path", Key(Path.Combine(root, name + ".cbz"))))!;
            string Run(bool apply, string? source = null, string? destination = null, string[]? roots = null)
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["relocate-from"] = source ?? from, ["relocate-to"] = destination ?? to,
                    ["apply-relocation"] = apply ? "true" : null
                }).Build();
                var output = new StringWriter();
                var previous = Console.Out;
                try
                {
                    Console.SetOut(output);
                    Check(RelocateCatalog.Run(configuration, directory, roots ?? [to]), "Relocation CLI was not handled.");
                }
                finally { Console.SetOut(previous); }
                return output.ToString();
            }
            string Snapshot()
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                db.Open();
                var snapshot = new System.Text.StringBuilder();
                foreach (var table in new[] { "books", "progress", "ratings", "bookmarks", "lists", "list_books" })
                {
                    using var command = db.CreateCommand();
                    command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new object[reader.FieldCount];
                        reader.GetValues(row);
                        snapshot.AppendLine(System.Text.Json.JsonSerializer.Serialize(row));
                    }
                }
                return snapshot.ToString();
            }
            var names = new[] { "good", "progress", "rating", "bookmark", "metadata", "list", "mismatch", "unhashed", "direct", "unsafe", "traversal" };
            Archive(Path.Combine(from, "good.cbz"), "Original");
            foreach (var name in names.Skip(1)) File.Copy(Path.Combine(from, "good.cbz"), Path.Combine(from, name + ".cbz"));
            File.Copy(Path.Combine(from, "good.cbz"), Path.Combine(sibling, "good.cbz"));
            var oldStore = new LibraryStore(database, [from, sibling]);
            Check(oldStore.ScanAsync(default).GetAwaiter().GetResult().Added == 12, "Relocation fixture import failed.");
            var old = names.ToDictionary(name => name, name => Id(from, name));
            var siblingId = Id(sibling, "good");
            var sourceHash = (string)Sql("SELECT content_hash FROM books WHERE id=$id", ("$id", old["good"]))!;
            foreach (var name in names.Where(name => name != "direct")) File.Copy(Path.Combine(from, name + ".cbz"), Path.Combine(to, name + ".cbz"));
            File.Delete(Path.Combine(from, "direct.cbz"));
            oldStore.ScanAsync(default).GetAwaiter().GetResult();
            Check((string?)Sql("SELECT content_hash FROM books WHERE id=$id AND availability='missing'", ("$id", old["direct"])) == sourceHash,
                "Missing sources lost their historical hash.");
            var store = new LibraryStore(database, [to]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 10, "Destination fixture import failed.");
            Check((string?)Sql("SELECT content_hash FROM books WHERE id=$id AND availability='unavailable'", ("$id", old["good"])) == sourceHash,
                "Unavailable sources lost their historical hash.");
            Check(store.ListMaintenance("duplicates").Total == 10, "Unavailable historical hashes leaked into duplicate results.");
            var fresh = names.Where(name => name != "direct").ToDictionary(name => name, name => Id(to, name));
            File.Copy(Path.Combine(from, "good.cbz"), Path.Combine(to, "direct.cbz"));
            store.EditMetadata(old["good"], new(new() { ["Title"] = "Kept edit", ["Writer"] = "Kept writer", ["Year"] = "2020" }, new() { ["Private"] = "Retain" }));
            store.SaveProgress("alice", old["good"], new(1, false));
            store.SaveProgress("bob", fresh["good"], new(2, true));
            store.SaveProgress("alice", fresh["good"], new(1, false));
            Sql("UPDATE progress SET updated_at=CASE WHEN book_id=$old THEN 100 ELSE 200 END WHERE user_id='alice'", ("$old", old["good"]));
            Sql("UPDATE progress SET updated_at=NULL WHERE user_id='bob'");
            foreach (var (user, oldTime, newTime) in new (string, long?, long?)[]
                { ("unknown", null, null), ("older", 300, 200), ("old-null", null, 200), ("new-null", 300, null) })
                Sql("""
                    INSERT INTO progress(user_id,book_id,page,completed,updated_at) VALUES($user,$old,0,0,$oldTime),($user,$new,0,0,$newTime)
                    """, ("$user", user), ("$old", old["good"]), ("$new", fresh["good"]), ("$oldTime", oldTime), ("$newTime", newTime));
            store.SaveRating("alice", old["good"], new(5));
            store.SaveRating("bob", fresh["good"], new(3));
            store.SaveBookmark("alice", old["good"], 0, new("Old bookmark"));
            store.SaveBookmark("bob", fresh["good"], 2, new("New bookmark"));
            var oldList = store.CreateList("alice", new("Old order", "manual", "all", []));
            store.AddListBooks("alice", oldList.Id, [old["good"], old["direct"]]);
            var newList = store.CreateList("bob", new("New order", "manual", "all", []));
            store.AddListBooks("bob", newList.Id, [fresh["rating"], fresh["good"], fresh["bookmark"]]);
            store.SaveProgress("alice", old["progress"], new(0, false));
            store.SaveProgress("alice", fresh["progress"], new(1, false));
            store.SaveRating("alice", old["rating"], new(5));
            store.SaveRating("alice", fresh["rating"], new(2));
            store.SaveBookmark("alice", old["bookmark"], 0, new("Old"));
            store.SaveBookmark("alice", fresh["bookmark"], 0, new("Different"));
            store.EditMetadata(old["metadata"], new(new() { ["Title"] = "Old edit" }));
            store.EditMetadata(fresh["metadata"], new(new() { ["Title"] = "Different edit" }));
            var conflictList = store.CreateList("alice", new("Conflicting order", "manual", "all", []));
            store.AddListBooks("alice", conflictList.Id, [old["list"], fresh["list"]]);
            Sql("UPDATE books SET content_hash=NULL WHERE id=$id", ("$id", old["unhashed"]));
            Sql("UPDATE books SET path=$path WHERE id=$id", ("$id", old["traversal"]),
                ("$path", Key(from + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "old" + Path.DirectorySeparatorChar + "traversal.cbz")));
            File.Delete(Path.Combine(to, "mismatch.cbz"));
            Archive(Path.Combine(to, "mismatch.cbz"), "Changed after scan");
            File.Delete(Path.Combine(to, "unsafe.cbz"));
            if (OperatingSystem.IsLinux()) File.CreateSymbolicLink(Path.Combine(to, "unsafe.cbz"), Path.Combine(sibling, "good.cbz"));
            var before = Snapshot();
            var preview = Run(false);
            Check(preview.Contains("mapped=2 skipped=4 conflicts=5 applied=0 merged=0") && before == Snapshot(), "Preview counts or read-only guarantee failed: " + preview);
            Check(preview.Contains("Historical SHA256 differs") && preview.Contains("Noncanonical source") && preview.Contains("No historical hash"), "Preview omitted actionable refusal reasons.");
            Reject(() => Run(false, source: "relative"));
            Reject(() => Run(false, destination: Path.Combine(to, "subdirectory")));
            Reject(() => Run(false, destination: Path.Combine(from, "nested"), roots: [Path.Combine(from, "nested")]));
            var applied = Run(true);
            Check(applied.Contains("mapped=2 skipped=4 conflicts=5 applied=2 merged=1"), "Applied counts differ: " + applied);
            Check(Id(to, "good") == old["good"] && Id(to, "direct") == old["direct"] && Id(sibling, "good") == siblingId,
                "Relocation lost old IDs, failed direct-file mapping, or crossed a root boundary.");
            var book = store.GetBook("alice", old["good"]);
            Check(book.Title == "Kept edit" && book.CurrentPage == 1 && book.PersonalRating == 5 && book.Availability == "available"
                && book.Location == "Library 1/good.cbz" && store.GetMetadata(old["good"]).Custom["Private"] == "Retain"
                && (string?)Sql("SELECT writer FROM books WHERE id=$id", ("$id", old["good"])) == "Kept writer"
                && (long?)Sql("SELECT year FROM books WHERE id=$id", ("$id", old["good"])) == 2020,
                "Relocation lost edited metadata, private state, availability, or scalar projections.");
            Check(store.GetBook("bob", old["good"]).Completed && store.GetBook("bob", old["good"]).PersonalRating == 3
                && store.GetBookmarks("alice", old["good"]).Single().Name == "Old bookmark"
                && store.GetBookmarks("bob", old["good"]).Single().Name == "New bookmark"
                && !store.GetBook("outsider", old["good"]).Started && store.GetBookmarks("outsider", old["good"]).Count == 0,
                "Nonconflicting private data was lost or crossed account boundaries.");
            foreach (var (user, expected) in new (string, long?)[]
                { ("alice", 200), ("bob", null), ("unknown", null), ("older", 300), ("old-null", 200), ("new-null", 300) })
                Check(Equals(Sql("SELECT updated_at FROM progress WHERE user_id=$user AND book_id=$id", ("$user", user), ("$id", old["good"])),
                    (object?)expected ?? DBNull.Value), "Relocation lost or fabricated a progress timestamp for " + user);
            Check(store.GetList("alice", oldList.Id).BookIds!.SequenceEqual(new[] { old["good"], old["direct"] })
                && store.GetList("bob", newList.Id).BookIds!.SequenceEqual(new[] { fresh["rating"], old["good"], fresh["bookmark"] })
                && store.GetList("alice", conflictList.Id).BookIds!.SequenceEqual(new[] { old["list"], fresh["list"] }),
                "List membership/order was lost or a conflicting list was merged.");
            foreach (var name in new[] { "progress", "rating", "bookmark", "metadata", "list", "mismatch", "unhashed" })
                Check(Id(from, name) == old[name] && Id(to, name) == fresh[name], "Refused pair was changed: " + name);
            Check(store.GetBook("alice", old["progress"]).CurrentPage == 0 && store.GetBook("alice", fresh["progress"]).CurrentPage == 1
                && store.GetBook("alice", old["rating"]).PersonalRating == 5 && store.GetBook("alice", fresh["rating"]).PersonalRating == 2
                && store.GetBookmarks("alice", old["bookmark"]).Single().Name == "Old"
                && store.GetBookmarks("alice", fresh["bookmark"]).Single().Name == "Different"
                && store.GetBook("alice", old["metadata"]).Title == "Old edit" && store.GetBook("alice", fresh["metadata"]).Title == "Different edit",
                "Conflicting private values were not preserved on both rows.");
            var after = Snapshot();
            Check(Run(true).Contains("mapped=0 skipped=4 conflicts=5 applied=0 merged=0") && Snapshot() == after, "Relocation rerun was not idempotent.");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(from, "good.cbz")))) == sourceHash
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(to, "good.cbz")))) == sourceHash, "Relocation changed source bytes.");
            Sql("DELETE FROM ratings WHERE book_id=$id", ("$id", fresh["rating"]));
            var beforeRollback = Snapshot();
            Sql($"CREATE TRIGGER relocation_abort BEFORE UPDATE OF path ON books WHEN OLD.id='{old["rating"]}' BEGIN SELECT RAISE(ABORT,'forced relocation failure'); END");
            var failed = false;
            try { Run(true); }
            catch (SqliteException) { failed = true; }
            Check(failed && Snapshot() == beforeRollback, "Failure after merging/deleting the destination did not roll the pair back atomically.");
            Sql("DROP TRIGGER relocation_abort");
            store.SaveRating("alice", fresh["rating"], new(2));
            if (OperatingSystem.IsLinux())
            {
                var destinationOrder = store.SavePageOrder(fresh["unhashed"],
                    new(store.GetPageOrder(fresh["unhashed"]).Revision, [1, 0, 2]));
                Directory.Move(from, from + "-retained");
                Directory.CreateSymbolicLink(from, sibling);
                Check(Run(false).Contains("No historical hash or verified old-root symlink"), "Wrong-target symlink established correspondence.");
                Directory.Delete(from);
                Directory.CreateSymbolicLink(from, to);
                var aliasPreview = Run(false);
                Check(aliasPreview.Contains("mapped=1 skipped=3 conflicts=5") && aliasPreview.Contains("historical byte match unavailable"),
                    "Real root symlink correspondence failed or falsely claimed a historical byte match: " + aliasPreview);
                Check(Run(true).Contains("applied=1 merged=1") && Id(to, "unhashed") == old["unhashed"], "Symlink-root relocation lost the historical ID.");
                var relocatedOrder = store.GetPageOrder(old["unhashed"]);
                Check(relocatedOrder.BookId == old["unhashed"] && relocatedOrder.Custom && relocatedOrder.Pages.SequenceEqual(destinationOrder.Pages),
                    "Symlink-root relocation cleared the destination custom page order while establishing the historical hash.");
                Check(Run(true).Contains("mapped=0"), "Symlink-root relocation was not idempotent.");
            }
            // Offline maintenance must not require optional tables introduced after this catalog.
            Sql("DROP TABLE page_metadata_edits");
            Sql("ALTER TABLE progress DROP COLUMN updated_at");
            Sql("DELETE FROM ratings WHERE book_id=$id", ("$id", fresh["rating"]));
            var oldSchema = Snapshot();
            Check(Run(false).Contains("mapped=1 ") && Snapshot() == oldSchema,
                "Older-schema relocation preview changed the catalog or refused the merge.");
            Check(Run(true).Contains("applied=1 merged=1") && Id(to, "rating") == old["rating"]
                && store.GetBook("alice", old["rating"]).PersonalRating == 5
                && Sql("SELECT 1 FROM sqlite_master WHERE type='table' AND name='page_metadata_edits'") is null,
                "Older-schema apply failed, lost private state, or initialized the optional table.");
            Check(!RelocateCatalog.Run(new ConfigurationBuilder().Build(), directory, [to]), "Normal startup was treated as relocation.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
