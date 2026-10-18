using System.IO.Compression;
using System.Xml.Linq;
using ComicRack.Web;
using Microsoft.Data.Sqlite;

public static class GroupChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "comicrack-groups-" + Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(directory, "first");
        var secondRoot = Path.Combine(directory, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            var fixtures = new[]
            {
                (Title: "Alpha", Series: "One", Publisher: "Press", Folder: Path.Combine(firstRoot, "Stacks")),
                (Title: "Beta", Series: "One", Publisher: "Press", Folder: Path.Combine(firstRoot, "Stacks")),
                (Title: "Gamma", Series: "Two", Publisher: "Other", Folder: Path.Combine(firstRoot, "Other")),
                (Title: "Delta", Series: "Two", Publisher: "Press", Folder: Path.Combine(firstRoot, "Other")),
                (Title: "Empty", Series: "", Publisher: "", Folder: firstRoot),
                (Title: "Lower", Series: "one", Publisher: "Press", Folder: Path.Combine(secondRoot, "Stacks"))
            };
            foreach (var fixture in fixtures)
            {
                Directory.CreateDirectory(fixture.Folder);
                using var archive = ZipFile.Open(Path.Combine(fixture.Folder, fixture.Title + ".cbz"), ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("1.png").Open()) stream.Write(png);
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write(new XElement("ComicInfo", new XElement("Title", fixture.Title),
                    new XElement("Series", fixture.Series), new XElement("Publisher", fixture.Publisher),
                    new XElement("Summary", "marker")));
            }
            var store = new LibraryStore(Path.Combine(directory, "groups.sqlite"), [firstRoot, secondRoot]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == fixtures.Length, "Group fixtures failed to import.");
            var all = store.ListBooks("alice", null).Items;
            var ids = all.ToDictionary(book => book.Title, book => book.Id);
            var groups = store.ListGroups("alice", null, "series");
            Check(groups.Total == 4 && groups.Items.Sum(group => group.Count) == 6
                && groups.Items.Single(group => group.Key == "One").Count == 2
                && groups.Items.Single(group => group.Key == "one").Count == 1
                && groups.Items.Single(group => group.Key == "").Count == 1,
                "Series groups lost exact keys, counts, or the empty group.");
            var pages = Enumerable.Range(1, 2).Select(page => store.ListGroups("alice", null, "series", page, 2)).ToArray();
            Check(pages.All(page => page.Total == 4 && page.Items.Count == 2 && page.PageSize == 2)
                && pages.SelectMany(page => page.Items).SequenceEqual(groups.Items)
                && store.ListGroups("alice", null, "series", int.MaxValue, 2).Page == 2,
                "Group pagination or stable ordering failed.");
            foreach (var groupBy in new[] { "series", "publisher", "folder" })
            foreach (var group in store.ListGroups("alice", null, groupBy).Items)
            {
                var members = store.ListBooks("alice", null, groupBy: groupBy, groupKey: group.Key);
                var memberPages = Enumerable.Range(1, group.Count)
                    .Select(page => store.ListBooks("alice", null, page, 1, groupBy: groupBy, groupKey: group.Key)).ToArray();
                Check(members.Total == group.Count && members.Items.Any(book => book.Id == group.CoverId)
                    && memberPages.All(page => page.Total == group.Count && page.Items.Count == 1)
                    && memberPages.SelectMany(page => page.Items).SequenceEqual(members.Items),
                    "Stack drilldown disagreed with grouped count, cover, or pagination.");
            }
            var folders = store.ListGroups("alice", null, "folder");
            Check(folders.Total == 4 && folders.Items.All(group => !Path.IsPathRooted(group.Key)
                && !group.Key.Contains(directory)) && folders.Items.Count(group => group.Key.EndsWith("/Stacks")) == 2,
                "Folder groups leaked absolute paths or merged configured roots.");
            Check(store.ListGroups("alice", null, "publisher").Items.Single(group => group.Key == "").Count == 1
                && store.ListBooks("alice", null, groupBy: "series", groupKey: "").Items.Single().Id == ids["Empty"]
                && store.ListBooks("alice", null, groupBy: "series").Items.SequenceEqual(all),
                "Empty-key drilldown or flat browsing changed.");
            var empty = store.ListGroups("alice", "absent", "series", int.MaxValue, 1);
            Check(empty.Total == 0 && empty.Page == 1 && empty.Items.Count == 0, "Empty groups were not normalized.");

            var manual = store.CreateList("alice", new("Manual", "manual", "all", []));
            store.AddListBooks("alice", manual.Id, [ids["Alpha"], ids["Beta"], ids["Gamma"], ids["Delta"]]);
            var smart = store.CreateList("alice", new("Smart", "smart", "all", [new("Publisher", "equals", "Press")]));
            store.SaveProgress("alice", ids["Alpha"], new(0, true));
            store.SaveProgress("alice", ids["Delta"], new(0, false));
            foreach (var listId in new[] { manual.Id, smart.Id })
            {
                var intersection = store.ListGroups("alice", "marker", "series", 2, 1,
                    status: "unread", publisher: "Press", listId: listId);
                Check(intersection.Items.All(group => group.Count == 1)
                    && intersection.Total == (listId == manual.Id ? 1 : 2), "Group filters ran after pagination.");
                var selected = intersection.Items.Single();
                Check(store.ListBooks("alice", "marker", pageSize: 1, status: "unread", publisher: "Press",
                    listId: listId, groupBy: "series", groupKey: selected.Key).Total == selected.Count,
                    "Filtered group drilldown lost a list/search/status/publisher intersection.");
                Reject<KeyNotFoundException>(() => store.ListGroups("bob", null, "series", listId: listId));
                Reject<KeyNotFoundException>(() => store.ListBooks("bob", null, listId: listId, groupBy: "series", groupKey: "One"));
            }
            Check(store.ListGroups("alice", null, "series", status: "completed").Items.Single().CoverId == ids["Alpha"]
                && store.ListGroups("bob", null, "series", status: "completed").Total == 0,
                "Grouped progress leaked across users.");
            store.EditMetadata(ids["Delta"], new(new() { ["Series"] = "One" }));
            Check(store.ListGroups("alice", null, "series").Items.Single(group => group.Key == "One").Count == 3
                && store.ListBooks("alice", null, groupBy: "series", groupKey: "Two").Items.Single().Id == ids["Gamma"],
                "Metadata edits did not update groups and drilldown immediately.");
            const string sqlLikeKey = "' OR 1=1 --";
            store.EditMetadata(ids["Gamma"], new(new() { ["Series"] = sqlLikeKey }));
            Check(store.ListBooks("alice", null, groupBy: "series", groupKey: sqlLikeKey).Items.Single().Id == ids["Gamma"]
                && store.ListGroups("alice", null, "series").Items.Any(group => group.Key == sqlLikeKey),
                "Group keys were interpreted as SQL.");

            File.Delete(Path.Combine(firstRoot, "Stacks", "Alpha.cbz"));
            File.Delete(Path.Combine(firstRoot, "Empty.cbz"));
            Directory.Move(secondRoot, secondRoot + "-offline");
            store.ScanAsync(default).GetAwaiter().GetResult();
            groups = store.ListGroups("alice", null, "series");
            Check(groups.Items.Single(group => group.Key == "One").CoverId is { } cover
                && cover != ids["Alpha"] && groups.Items.Single(group => group.Key == "").CoverId is null
                && groups.Items.Single(group => group.Key == "one").CoverId is null,
                "Groups selected missing/unavailable covers or hid catalog members.");
            foreach (var availability in new[] { "available", "missing", "unavailable" })
            {
                var filtered = store.ListGroups("alice", null, "series", availability: availability);
                Check(filtered.Items.Sum(group => group.Count) == store.ListBooks("alice", null, availability: availability).Total,
                    "Availability groups disagreed with the flat catalog.");
                foreach (var group in filtered.Items)
                    Check(store.ListBooks("alice", null, availability: availability, groupBy: "series", groupKey: group.Key).Total == group.Count,
                        "Availability drilldown disagreed with its group.");
            }
            Check(store.ListGroups("alice", null, "series", availability: "missing").Items.Sum(group => group.Count) == 2
                && store.ListGroups("alice", null, "series", availability: "unavailable").Items.Single().Key == "one",
                "Missing and unavailable source grouping failed.");
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "none"));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series; DROP TABLE books"));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", page: 0));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", pageSize: 101));
            Reject<ArgumentException>(() => store.ListGroups("alice", null, "series", availability: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, availability: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, groupBy: "unknown"));
            Reject<ArgumentException>(() => store.ListBooks("alice", null, groupKey: ""));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
