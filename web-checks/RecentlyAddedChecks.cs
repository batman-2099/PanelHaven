using System.IO.Compression;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class RecentlyAddedChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-recent-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "comics");
        Directory.CreateDirectory(root);
        try
        {
            var state = Directory.CreateDirectory(Path.Combine(directory, "state")).FullName;
            var database = Path.Combine(state, "recent.sqlite");
            var titles = new[] { "Legacy", "Fresh", "BeforeBoundary", "AtBoundary", "Old", "Future" };
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            foreach (var title in titles)
            {
                using var archive = ZipFile.Open(Path.Combine(root, title + ".cbz"), ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("page.png").Open()) stream.Write(png);
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write($"<ComicInfo><Title>{title}</Title></ComicInfo>");
            }
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            object? Sql(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return command.ExecuteScalar();
            }
            var legacyPath = Path.Combine(root, "Legacy.cbz");
            Sql("""
                CREATE TABLE books (
                    id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,title TEXT NOT NULL,
                    series TEXT NOT NULL,number TEXT NOT NULL,publisher TEXT NOT NULL,summary TEXT NOT NULL,
                    page_count INTEGER NOT NULL,info_xml TEXT NOT NULL,book_xml TEXT,edited INTEGER NOT NULL DEFAULT 0,
                    file_size INTEGER NOT NULL,modified_ticks INTEGER NOT NULL);
                INSERT INTO books VALUES('legacy',$path,'Legacy','','','','',1,
                    '<ComicInfo><Title>Legacy</Title></ComicInfo>',NULL,0,$size,$ticks);
                """, ("$path", OperatingSystem.IsWindows() ? legacyPath.ToUpperInvariant() : legacyPath),
                ("$size", new FileInfo(legacyPath).Length), ("$ticks", File.GetLastWriteTimeUtc(legacyPath).Ticks));
            var store = new LibraryStore(database, [root]);
            Check(Sql("SELECT added_at FROM books WHERE id='legacy'") is DBNull, "Migration invented a legacy addition date.");
            var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 5, "Recently added fixtures failed to import.");
            var ids = titles.ToDictionary(title => title, title => store.ListBooks("alice", title).Items.Single().Id);
            Check((long)Sql("SELECT COUNT(*) FROM books WHERE id <> 'legacy' AND added_at BETWEEN $start AND unixepoch()", ("$start", started))! == 5,
                "First imports did not receive a UTC addition timestamp.");
            Check(Sql("SELECT added_at FROM books WHERE id='legacy'") is DBNull, "Rescan assigned an addition date to a legacy book.");

            var recent = store.GetLists("alice").Single();
            Check(recent.Name == "Recently added", "Default reading list has the wrong name.");
            Check(store.GetLists("alice").Single().Id == recent.Id, "Repeated list enumeration duplicated the default.");
            var bob = store.GetLists("bob").Single();
            Check(bob.Id != recent.Id, "Accounts share a default list identity.");
            Check(store.ListBooks("alice", null, listId: recent.Id).Total == 5, "Fresh imports or unknown legacy dates matched incorrectly.");
            var concurrent = new string[8];
            Parallel.For(0, concurrent.Length, index => concurrent[index] = store.GetLists("concurrent").Single().Id);
            Check(concurrent.Distinct().Count() == 1, "Concurrent enumeration duplicated the default list.");

            // Keep the included side a minute below the boundary; no sleeping or wall-clock race at the next second.
            Sql("UPDATE books SET added_at=unixepoch()-30*86400+60 WHERE id=$id", ("$id", ids["BeforeBoundary"]));
            Sql("UPDATE books SET added_at=unixepoch()-30*86400 WHERE id=$id", ("$id", ids["AtBoundary"]));
            Sql("UPDATE books SET added_at=unixepoch()-31*86400 WHERE id=$id", ("$id", ids["Old"]));
            Sql("UPDATE books SET added_at=unixepoch()+86400 WHERE id=$id", ("$id", ids["Future"]));
            Check(store.ListBooks("alice", null, listId: recent.Id).Items.Select(book => book.Id).ToHashSet()
                .SetEquals(new[] { ids["Fresh"], ids["BeforeBoundary"], ids["Future"] }), "Thirty-day cutoff included old/unknown books or excluded recent books.");
            var zero = store.CreateList("alice", new("Zero days", "smart", "all", [new("DaysSinceAdded", "equals", "0")]));
            Check(store.ListBooks("alice", null, listId: zero.Id).Items.Select(book => book.Id).ToHashSet()
                .SetEquals(new[] { ids["Fresh"], ids["Future"] }), "Addition age did not clamp future timestamps to zero.");
            store.UpdateList("alice", zero.Id, new("Unknown dates", "smart", "all", [new("DaysSinceAdded", "isEmpty", "")]));
            Check(store.ListBooks("alice", null, listId: zero.Id).Items.Single().Id == "legacy", "Unknown addition dates were not empty.");
            store.UpdateList("alice", zero.Id, new("Known dates", "smart", "all", [new("DaysSinceAdded", "isNotEmpty", "")]));
            Check(store.ListBooks("alice", null, listId: zero.Id).Total == 5, "Known addition dates were empty.");
            store.DeleteList("alice", zero.Id);
            var age = Sql("SELECT added_at FROM books WHERE id=$id", ("$id", ids["Old"]));
            store.EditMetadata(ids["Old"], new(new() { ["Publisher"] = "Edited" }, null));
            File.SetLastWriteTimeUtc(Path.Combine(root, "Old.cbz"), DateTime.UtcNow.AddMinutes(1));
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(Equals(age, Sql("SELECT added_at FROM books WHERE id=$id", ("$id", ids["Old"]))), "Metadata edit or changed-file rescan reset the first addition time.");
            Check(Sql("SELECT added_at FROM books WHERE id='legacy'") is DBNull, "Repeated scans replaced an unknown addition date.");
            Directory.CreateDirectory(Path.Combine(root, "Edited", "Unknown Series"));
            var move = store.PreviewFileMove(ids["Old"], new(0, "", "Moved.cbz"));
            store.ApplyFileMove(ids["Old"], new(0, "", "Moved.cbz", move.Revision, true));
            Check(Equals(age, Sql("SELECT added_at FROM books WHERE id=$id", ("$id", ids["Old"])))
                && File.Exists(Path.Combine(root, "Edited", "Unknown Series", "Moved.cbz")), "Moving a book reset its addition date or omitted its publisher/series folders.");

            store.UpdateList("alice", recent.Id, new("My recent books", "smart", "all", [new("DaysSinceAdded", "lessThan", "7")]));
            store.DeleteList("bob", bob.Id);
            var existing = store.CreateList("carol", new("Recently added", "manual", "all", []));
            store.AddListBooks("carol", existing.Id, [ids["Old"]]);
            Check(store.GetLists("carol").Single().Id == existing.Id && store.GetList("carol", existing.Id).BookIds!.SequenceEqual([ids["Old"]]),
                "Seeding duplicated or overwrote an existing same-name list.");
            store.DeleteList("carol", existing.Id);
            for (var restart = 0; restart < 2; restart++)
            {
                store = new LibraryStore(database, [root]);
                var renamed = store.GetLists("alice").Single();
                Check(renamed.Id == recent.Id && renamed.Name == "My recent books" && renamed.Rules.Single().Value == "7",
                    "Restart or enumeration overwrote a customized default.");
                Check(store.GetLists("bob").Count == 0 && store.GetLists("bob").Count == 0 && store.GetLists("carol").Count == 0,
                    "Restart or enumeration recreated a deleted default or same-name list.");
            }
            store.DeleteList("alice", recent.Id);
            Check(new LibraryStore(database, [root]).GetLists("alice").Count == 0, "Deleting a renamed default recreated it.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
