using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class NextIssueChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-next-issue-" + Guid.NewGuid().ToString("N"));
        var regular = Path.Combine(directory, "Archived"); // A root label is not its catalog location.
        var archived = Path.Combine(directory, "saved_%'");
        var nearby = archived + "-nearby";
        var unsorted = Path.Combine(directory, "intake");
        var offline = Path.Combine(directory, "offline");
        var database = Path.Combine(directory, "state", "library.sqlite");
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            var fixtures = new[]
            {
                (regular, "Start", "Series", "01"),
                (regular, "Same number", "Series", "1"),
                (regular, "Missing", "Series", "2"),
                (offline, "Unavailable", "Series", "3"),
                (nearby, "Next", "Series", "5"),
                (regular, "Duplicate next", "Series", "5"),
                (regular, "Ten", "Series", "10"),
                (regular, "Decimal", "Series", "10.2"),
                (regular, "Natural decimal", "Series", "10.10"),
                (regular, "Annual", "Series", "Annual 1"),
                (regular, "Annual duplicate", "Series", "annual 01"),
                (regular, "Last", "Series", "Annual 2"),
                (regular, "Different case", "series", "1.1"),
                (regular, "Different series", "Other", "1.1"),
                (regular, "Unnamed", "", "1"),
                (regular, "Unnumbered", "Series", ""),
                (archived, "Archived start", "Series", "1"),
                (archived, "Archived next", "Series", "2"),
                (unsorted, "Intake start", "Series", "1"),
                (unsorted, "Intake next", "Series", "3")
            };
            foreach (var (root, title, series, number) in fixtures)
            {
                Directory.CreateDirectory(root);
                using var comic = ZipFile.Open(Path.Combine(root, title + ".cbz"), ZipArchiveMode.Create);
                using (var page = comic.CreateEntry("page.png").Open()) page.Write(png);
                using var metadata = new StreamWriter(comic.CreateEntry("ComicInfo.xml").Open());
                metadata.Write(new XElement("ComicInfo", new XElement("Title", title),
                    new XElement("Series", series), new XElement("Number", number)));
            }
            var store = new LibraryStore(database, [regular, nearby, offline], archived, unsorted);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == fixtures.Length, "Next-issue fixtures failed to import.");
            var ids = new[] { "library", "archived", "unsorted" }
                .SelectMany(location => store.ListBooks("alice", null, location: location).Items)
                .ToDictionary(book => book.Title, book => book.Id);
            File.Delete(Path.Combine(regular, "Missing.cbz"));
            Directory.Move(offline, offline + "-removed");
            store.ScanAsync(default).GetAwaiter().GetResult();
            using (var db = new SqliteConnection($"Data Source={database}"))
            {
                db.Open();
                using var command = db.CreateCommand();
                // Prove display labels do not define actual location.
                command.CommandText = "UPDATE books SET location='Unsorted';";
                command.ExecuteNonQuery();
            }
            var next = new[] { ids["Next"], ids["Duplicate next"] }.Order(StringComparer.Ordinal).First();
            Check(store.GetNextIssue(ids["Start"]) == next && store.GetNextIssue(ids["Same number"]) == next,
                "Next issue did not skip equivalent numbers, unavailable books, gaps or other locations/series.");
            Check(store.GetNextIssue(ids["Next"]) == ids["Ten"] && store.GetNextIssue(ids["Duplicate next"]) == ids["Ten"],
                "Same-number duplicates were treated as successors or 10 sorted before 5.");
            Check(store.GetNextIssue(ids["Ten"]) == ids["Decimal"]
                && store.GetNextIssue(ids["Decimal"]) == ids["Natural decimal"]
                && store.GetNextIssue(ids["Natural decimal"]) == ids["Annual"],
                "Next issue disagreed with the existing natural issue-number ordering.");
            Check(store.GetNextIssue(ids["Annual"]) == ids["Last"]
                && store.GetNextIssue(ids["Annual duplicate"]) == ids["Last"]
                && store.GetNextIssue(ids["Last"]) is null,
                "Alphanumeric duplicates or the final issue were handled incorrectly.");
            Check(store.GetNextIssue(ids["Archived start"]) == ids["Archived next"]
                && store.GetNextIssue(ids["Archived next"]) is null
                && store.GetNextIssue(ids["Intake start"]) == ids["Intake next"]
                && store.GetNextIssue(ids["Intake next"]) is null,
                "Next issue crossed an actual Archived/Unsorted location or a path-prefix boundary.");
            Check(store.GetNextIssue(ids["Unnamed"]) is null && store.GetNextIssue(ids["Unnumbered"]) is null
                && store.GetNextIssue(ids["Different case"]) is null && store.GetNextIssue(ids["Different series"]) is null,
                "Next issue guessed a missing series/number or merged exact series keys.");
            store.SaveProgress("alice", next, new(0, true));
            store.SaveProgress("bob", ids["Start"], new(0, false));
            var alice = store.GetBook("alice", next);
            var bob = store.GetBook("bob", ids["Start"]);
            Check(store.GetNextIssue(ids["Start"]) == next
                && store.GetBook("alice", next).Completed == alice.Completed
                && store.GetBook("bob", ids["Start"]).Started == bob.Started
                && !store.GetBook("alice", ids["Start"]).Started && !store.GetBook("bob", next).Started,
                "Next issue filtered completed books or changed private reading progress.");
            store.EditMetadata(next, new(new() { ["Series"] = "Changed" }));
            Check(store.GetNextIssue(ids["Start"]) == (next == ids["Next"] ? ids["Duplicate next"] : ids["Next"]),
                "Next issue did not reflect a metadata edit immediately.");
            try { store.GetNextIssue("missing"); }
            catch (KeyNotFoundException) { return; }
            throw new Exception("Unknown current book did not produce the normal not-found result.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
