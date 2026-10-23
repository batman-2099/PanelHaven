using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class SeriesShelfChecks
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
            throw new Exception("Invalid shelf arguments were accepted.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-shelves-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        var archived = Path.Combine(directory, "archived");
        var unsorted = Path.Combine(directory, "unsorted");
        foreach (var path in new[] { root, archived, unsorted }) Directory.CreateDirectory(path);
        try
        {
            var database = Path.Combine(directory, "shelves.sqlite");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void Archive(string title, string series, string? folder = null)
            {
                using var archive = ZipFile.Open(Path.Combine(folder ?? root, title + ".cbz"), ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("page.png").Open()) stream.Write(png);
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write(new XElement("ComicInfo", new XElement("Title", title), new XElement("Series", series),
                    new XElement("Publisher", series switch { "Active" => "Zulu", "Legacy" => "alpha", _ => "Alpha" })));
            }
            foreach (var series in new[] { "Active", "Stale", "Revived", "TooLate", "Complete", "Legacy", "Mixed", "UpdateOnly", "OutsideUpdate" })
            {
                Archive(series + "1", series);
                Archive(series + "2", series);
            }
            foreach (var series in new[] { "BrandNew", "Tie", "tie", "Unstarted", "", "   " }) Archive("Single" + series, series);
            Archive("Archived", "Active", archived);
            Archive("Unsorted", "Active", unsorted);
            Archive("ArchivedOnly", "ArchivedOnly", archived);
            Archive("UnsortedOnly", "UnsortedOnly", unsorted);
            var store = new LibraryStore(database, [root], archived, unsorted);
            store.ScanAsync(default).GetAwaiter().GetResult();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString());
            db.Open();
            db.CreateCollation("NATURAL_NUMBER", StringComparer.Ordinal.Compare);
            object? Sql(string sql, params (string Name, object? Value)[] parameters)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            var ids = new[] { "library", "archived", "unsorted" }.SelectMany(location => store.ListBooks("alice", null, pageSize: 100, location: location).Items)
                .ToDictionary(book => book.Title, book => book.Id);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Sql("UPDATE books SET added_at=$date", ("$date", now - 100 * 86400));
            void Age(string title, int days) => Sql("UPDATE books SET added_at=$date WHERE id=$id", ("$date", now - days * 86400), ("$id", ids[title]));
            Age("Revived2", 6);
            Age("TooLate2", 8);
            Age("Mixed2", 0);
            Age("SingleBrandNew", 2);
            Age("SingleTie", 4);
            Age("Singletie", 4);
            Age("SingleUnstarted", 0);
            Age("UpdateOnly2", 11);
            Age("OutsideUpdate2", 13);
            Sql("UPDATE books SET added_at=NULL WHERE id=$id", ("$id", ids["Mixed1"]));

            // Reopen an actual pre-timestamp progress schema, without fabricating migration dates.
            Sql("ALTER TABLE progress DROP COLUMN updated_at");
            Sql("INSERT INTO progress(user_id,book_id,page,completed) VALUES('alice',$id,0,0)", ("$id", ids["Legacy1"]));
            store = new LibraryStore(database, [root], archived, unsorted);
            Check(Sql("SELECT updated_at FROM progress WHERE book_id=$id", ("$id", ids["Legacy1"])) is DBNull,
                "Migration invented a legacy progress timestamp.");
            foreach (var series in new[] { "Active", "Stale", "Revived", "TooLate" })
            {
                store.SaveProgress("alice", ids[series + "1"], new(0, series is "Revived" or "TooLate"));
                Sql("UPDATE progress SET updated_at=$date WHERE book_id=$id", ("$date", now - (series == "Active" ? 29 : 31) * 86400), ("$id", ids[series + "1"]));
            }
            store.SetReadingStatus("alice", new([ids["Complete1"], ids["Complete2"]], true));
            Check((long)Sql("SELECT MIN(updated_at) FROM progress WHERE book_id IN ($one,$two)", ("$one", ids["Complete1"]), ("$two", ids["Complete2"]))! >= now,
                "Bulk completion failed to record real activity.");
            Sql("UPDATE progress SET updated_at=$date WHERE book_id IN ($one,$two)", ("$date", now - 31 * 86400), ("$one", ids["Complete1"]), ("$two", ids["Complete2"]));
            var deck = store.ListSeriesShelf("alice", "on-deck");
            Check(deck.Items.Select(group => group.Key).SequenceEqual(["Legacy", "Revived", "Active"]),
                "Next Issue lost case-insensitive publisher/series ordering, 30/7-day windows, legacy progress, or full-read exclusion.");
            var active = deck.Items.Single(group => group.Key == "Active");
            Check(active.Count == 2 && active.Unread == 1 && active.InProgress == 1 && active.Completed == 0 && active.CompletionPercent == 0,
                "Shelf counts included Archived/Unsorted or lost page-zero progress.");
            Check(store.ListSeriesShelf("bob", "on-deck").Total == 0, "On Deck exposed another reader's progress.");
            var bob = store.ListSeriesShelf("bob", "newly-added").Items.Single(group => group.Key == "Active");
            Check(bob.Unread == bob.Count && bob.InProgress == 0 && bob.Completed == 0, "Shelf statistics leaked another user's progress.");
            var revived = deck.Items.Single(group => group.Key == "Revived");
            Check(revived.Completed == 1 && revived.CompletionPercent == 50, "Completed issues were not counted toward series completion.");

            var recent = store.ListSeriesShelf("alice", "recently-updated", pageSize: 100);
            Check(recent.Items.Select(group => group.Key).ToHashSet().SetEquals(["Revived", "TooLate", "Mixed", "BrandNew", "Tie", "tie", "Unstarted", "UpdateOnly"]),
                "Recently Updated did not use the latest addition within twelve days.");
            var newest = store.ListSeriesShelf("alice", "newly-added", pageSize: 100);
            Check(newest.Items.Select(group => group.Key).Take(4).SequenceEqual(["Unstarted", "BrandNew", "Tie", "tie"])
                && newest.Items.Last().Key == "Mixed" && newest.Total == 13,
                "Newly Added used MAX instead of MIN, promoted a mixed unknown-date series, merged exact keys, or included unnamed/special-location series.");
            foreach (var kind in new[] { "on-deck", "recently-updated", "newly-added" })
            {
                var all = store.ListSeriesShelf("alice", kind, pageSize: 100);
                var pages = Enumerable.Range(1, (all.Total + 1) / 2).Select(page => store.ListSeriesShelf("alice", kind, page, 2)).ToArray();
                Check(pages.All(page => page.Total == all.Total && page.PageSize == 2) && pages.SelectMany(page => page.Items).SequenceEqual(all.Items)
                    && store.ListSeriesShelf("alice", kind, int.MaxValue, 2).Page == pages.Length,
                    "Shelf pagination was unstable or failed to normalize an out-of-range page.");
                Check(all.Items.All(group => group == store.ListGroups("alice", null, "series", pageSize: 100).Items.Single(existing => existing.Key == group.Key)),
                    "Shelf cards disagreed with exact series groups, cover selection, or private counts.");
            }
            store.EditMetadata(ids["Stale1"], new(new() { ["Publisher"] = "Edited" }));
            File.SetLastWriteTimeUtc(Path.Combine(root, "Stale1.cbz"), DateTime.UtcNow.AddMinutes(1));
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.ListSeriesShelf("alice", "recently-updated", pageSize: 100).Items.SequenceEqual(recent.Items)
                && (long)Sql("SELECT added_at FROM books WHERE id=$id", ("$id", ids["Stale1"]))! == now - 100 * 86400,
                "Metadata edit or rescan promoted a series or reset first-addition timestamps.");
            // Stay a minute either side of each cutoff, avoiding second-boundary races.
            Sql("UPDATE progress SET updated_at=$date WHERE book_id=$id", ("$date", now - 30 * 86400 + 60), ("$id", ids["Active1"]));
            Sql("UPDATE progress SET updated_at=$date WHERE book_id=$id", ("$date", now - 30 * 86400 - 60), ("$id", ids["Stale1"]));
            foreach (var (title, days, margin) in new[] { ("Revived2", 7, 60), ("TooLate2", 7, -60), ("UpdateOnly2", 12, 60), ("OutsideUpdate2", 12, -60) })
                Sql("UPDATE books SET added_at=$date WHERE id=$id", ("$date", now - days * 86400 + margin), ("$id", ids[title]));
            store = new LibraryStore(database, [root], archived, unsorted);
            Check(store.ListSeriesShelf("alice", "on-deck").Items.SequenceEqual(deck.Items)
                && store.ListSeriesShelf("alice", "recently-updated", pageSize: 100).Items.SequenceEqual(recent.Items)
                && Sql("SELECT updated_at FROM progress WHERE book_id=$id", ("$id", ids["Legacy1"])) is DBNull,
                "Cutoff boundaries or reopening changed eligibility or backfilled legacy activity.");
            Archive("Complete3", "Complete");
            store.ScanAsync(default).GetAwaiter().GetResult();
            var next = store.ListBooks("alice", "Complete3").Items.Single();
            Check(store.ListSeriesShelf("alice", "on-deck").Items.Any(group => group.Key == "Complete" && group.Unread == 1 && group.Completed == 2),
                "A newly imported issue did not return a previously completed series to On Deck.");
            store.SetReadingStatus("alice", new([next.Id], true));
            Check(!store.ListSeriesShelf("alice", "on-deck").Items.Any(group => group.Key == "Complete"), "Fully completed series remained on deck.");
            store.SaveProgress("alice", ids["Legacy1"], new(0, false));
            Check((long)Sql("SELECT updated_at FROM progress WHERE book_id=$id", ("$id", ids["Legacy1"]))! >= now,
                "Real activity did not replace an unknown legacy timestamp.");
            Sql("UPDATE progress SET updated_at=$date WHERE book_id=$id", ("$date", now - 31 * 86400), ("$id", ids["Legacy1"]));
            Check(!store.ListSeriesShelf("alice", "on-deck").Items.Any(group => group.Key == "Legacy"), "Legacy exemption persisted after real activity aged out.");
            store.SetReadingStatus("alice", new([ids["Active1"]], false));
            Check(!store.ListSeriesShelf("alice", "on-deck").Items.Any(group => group.Key == "Active"), "Mark unread left stale activity on deck.");
            var empty = store.ListSeriesShelf("nobody", "on-deck", int.MaxValue);
            Check(empty.Total == 0 && empty.Page == 1 && empty.PageSize == 12 && empty.Items.Count == 0, "Empty/default shelf paging failed.");
            Reject(() => store.ListSeriesShelf("alice", "unknown"));
            Reject(() => store.ListSeriesShelf("alice", "on-deck' OR 1=1 --"));
            Reject(() => store.ListSeriesShelf("alice", "on-deck", 0));
            Reject(() => store.ListSeriesShelf("alice", "on-deck", pageSize: 0));
            Reject(() => store.ListSeriesShelf("alice", "on-deck", pageSize: 101));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
