using System.IO.Compression;
using System.Text.Json;
using ComicRack.Web;
using Microsoft.Data.Sqlite;

public static class ListChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "comicrack-lists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            var titles = new[] { "Alpha", "Beta", "Gamma", "Delta" };
            for (var i = 0; i < titles.Length; i++)
            {
                using var archive = ZipFile.Open(Path.Combine(directory, titles[i] + ".cbz"), ZipArchiveMode.Create);
                for (var page = 0; page <= i; page++)
                    using (var stream = archive.CreateEntry($"{page}.png").Open()) stream.Write(png);
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write($"<ComicInfo><Title>{titles[i]}</Title><Series>{(i < 2 ? "One" : "Two")}</Series>"
                    + $"<Number>{i + 1}</Number><Publisher>{(i == 2 ? "Other" : "Press")}</Publisher><Summary>marker</Summary></ComicInfo>");
            }
            var store = new LibraryStore(Path.Combine(directory, "lists.sqlite"), [directory]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 4, "List fixtures failed to import.");
            var ids = titles.Select(title => store.ListBooks("alice", title).Items.Single().Id).ToArray();
            var manualEdit = new ListEdit("Reading", "manual", "all", []);
            var manual = store.CreateList("alice", manualEdit);
            Check(store.GetLists("alice").Single().Id == manual.Id && store.GetLists("bob").Count == 0,
                "List ownership leaked in list index.");
            Check(store.GetList("alice", manual.Id).BookIds!.Length == 0, "New manual list was not empty.");
            Reject<KeyNotFoundException>(() => store.GetList("bob", manual.Id));
            Reject<KeyNotFoundException>(() => store.UpdateList("bob", manual.Id, manualEdit));
            Reject<KeyNotFoundException>(() => store.DeleteList("bob", manual.Id));
            Reject<KeyNotFoundException>(() => store.AddListBooks("bob", manual.Id, [ids[0]]));
            Reject<KeyNotFoundException>(() => store.RemoveListBooks("bob", manual.Id, [ids[0]]));
            Reject<KeyNotFoundException>(() => store.MoveListBook("bob", manual.Id, ids[0], null));
            Reject<KeyNotFoundException>(() => store.ListBooks("bob", null, listId: manual.Id));
            Reject<KeyNotFoundException>(() => store.GetList("alice", "' OR 1=1 --"));
            Check(store.GetLists("' OR 1=1 --").Count == 0, "User ID was interpreted as SQL.");

            void Order(params string[] expected) => Check(store.GetList("alice", manual.Id).BookIds!.SequenceEqual(expected),
                "Manual membership/order differs from requested order.");
            store.AddListBooks("alice", manual.Id, [ids[0], ids[1], ids[0], ids[2]]);
            Order(ids[0], ids[1], ids[2]);
            Reject<KeyNotFoundException>(() => store.AddListBooks("alice", manual.Id, [ids[3], "missing"]));
            Order(ids[0], ids[1], ids[2]);
            Reject<KeyNotFoundException>(() => store.RemoveListBooks("alice", manual.Id, [ids[0], "missing"]));
            Order(ids[0], ids[1], ids[2]);
            store.MoveListBook("alice", manual.Id, ids[2], ids[0]);
            Order(ids[2], ids[0], ids[1]);
            store.MoveListBook("alice", manual.Id, ids[2], null);
            Order(ids[0], ids[1], ids[2]);
            store.MoveListBook("alice", manual.Id, ids[2], null);
            store.MoveListBook("alice", manual.Id, ids[0], ids[0]);
            store.MoveListBook("alice", manual.Id, ids[0], ids[2]);
            Order(ids[1], ids[0], ids[2]);
            Reject<KeyNotFoundException>(() => store.MoveListBook("alice", manual.Id, ids[0], ids[3]));
            Reject<KeyNotFoundException>(() => store.MoveListBook("alice", manual.Id, ids[3], ids[0]));
            Order(ids[1], ids[0], ids[2]);
            store.RemoveListBooks("alice", manual.Id, [ids[0], ids[0], ids[3]]);
            Order(ids[1], ids[2]);
            store.AddListBooks("alice", manual.Id, [ids[3], ids[0]]);
            Order(ids[1], ids[2], ids[3], ids[0]);
            store.MoveListBook("alice", manual.Id, ids[0], ids[1]);
            Order(ids[0], ids[1], ids[2], ids[3]);
            var pages = Enumerable.Range(1, 2).Select(page => store.ListBooks("alice", null, page, 2, "list", listId: manual.Id)).ToArray();
            Check(pages.All(page => page.Total == 4 && page.Items.Count == 2)
                && pages.SelectMany(page => page.Items).Select(book => book.Id).SequenceEqual(ids), "Manual list pagination lost order/members.");
            Check(store.ListBooks("alice", null, sort: "list", direction: "desc", listId: manual.Id).Items.Select(book => book.Id)
                .SequenceEqual(ids.Reverse()), "Descending list order failed.");
            Check(store.ListBooks("alice", null, sort: "list").Items.Select(book => book.Id)
                .SequenceEqual(store.ListBooks("alice", null).Items.Select(book => book.Id)), "List sorting without a list changed default order.");
            var intersection = store.ListBooks("alice", "marker", 2, 1, "list", publisher: "Press", listId: manual.Id);
            Check(intersection.Total == 3 && intersection.Items.Single().Id == ids[1], "List filters were applied after pagination.");
            Check(store.ListBooks("alice", "absent", int.MaxValue, 1, listId: manual.Id).Page == 1,
                "Empty list filter did not normalize page.");
            Check(store.UpdateList("alice", manual.Id, manualEdit with { Name = " Renamed " }).Name == "Renamed",
                "List rename did not persist.");
            Reject<ArgumentException>(() => store.UpdateList("alice", manual.Id,
                new ListEdit("Kind change", "smart", "all", [new("Title", "equals", "Alpha")])));

            var smartEdit = new ListEdit("Smart", "smart", "all", [new("Publisher", "equals", "press"), new("PageCount", "greaterThan", "1")]);
            var smart = store.CreateList("alice", smartEdit);
            Check(store.GetList("alice", smart.Id).BookIds is null, "Smart list materialized membership.");
            Check(store.ListBooks("alice", null, sort: "number", listId: smart.Id).Items.Select(book => book.Id)
                .SequenceEqual([ids[1], ids[3]]), "All smart rules or numeric comparison failed.");
            Check(store.ListBooks("alice", "Delta", pageSize: 1, publisher: "Press", listId: smart.Id).Items.Single().Id == ids[3],
                "Smart rules did not intersect search/publisher/pagination.");
            store.UpdateList("alice", smart.Id, smartEdit with { Match = "any" });
            Check(store.ListBooks("alice", null, listId: smart.Id).Total == 4, "Any smart rules failed.");
            Reject<ArgumentException>(() => store.AddListBooks("alice", smart.Id, [ids[0]]));
            Reject<ArgumentException>(() => store.RemoveListBooks("alice", smart.Id, [ids[0]]));
            Reject<ArgumentException>(() => store.MoveListBook("alice", smart.Id, ids[0], null));

            var readEdit = new ListEdit("Unread", "smart", "all", [new("ReadStatus", "equals", "unread")]);
            var reading = store.CreateList("alice", readEdit);
            var bobReading = store.CreateList("bob", readEdit);
            Check(store.ListBooks("alice", null, listId: reading.Id).Total == 4, "Unread rules excluded untouched books.");
            store.SaveProgress("alice", ids[0], new ReadingProgress(0, false));
            Check(store.ListBooks("alice", null, listId: reading.Id).Total == 3
                && store.ListBooks("bob", null, listId: bobReading.Id).Total == 4, "Smart progress was not live or leaked between users.");
            store.UpdateList("alice", reading.Id, readEdit with { Rules = [new("ReadStatus", "equals", "in-progress")] });
            Check(store.ListBooks("alice", null, listId: reading.Id).Items.Single().Id == ids[0], "Page zero is not in-progress.");
            store.SaveProgress("alice", ids[0], new ReadingProgress(0, true));
            Check(store.ListBooks("alice", null, listId: reading.Id).Total == 0, "Completed book remained in-progress.");
            store.UpdateList("alice", reading.Id, readEdit with { Rules = [new("ReadStatus", "equals", "completed")] });
            Check(store.ListBooks("alice", null, status: "completed", listId: reading.Id).Items.Single().Id == ids[0]
                && store.ListBooks("alice", null, status: "unread", listId: reading.Id).Total == 0, "Smart and browsing status filters failed to intersect.");
            store.UpdateList("alice", reading.Id, readEdit with { Rules = [new("ReadStatus", "notEquals", "completed")] });
            Check(store.ListBooks("alice", null, listId: reading.Id).Total == 3, "Negated reading rule failed.");

            store.UpdateList("alice", smart.Id, smartEdit with { Rules = [new("Publisher", "equals", "Changed")] });
            Check(store.ListBooks("alice", null, listId: smart.Id).Total == 0, "Metadata rule initial match was incorrect.");
            store.EditMetadata(ids[0], new MetadataEdit(new Dictionary<string, string?> { ["Publisher"] = "Changed" }, null));
            Check(store.ListBooks("alice", null, listId: smart.Id).Items.Single().Id == ids[0], "Smart metadata matching was not live.");
            foreach (var (rule, count) in new (ListRule, int)[]
            {
                (new("Title", "contains", "ALP"), 1), (new("Series", "notEquals", "One"), 2),
                (new("Number", "equals", "2"), 1), (new("Summary", "contains", "marker"), 4),
                (new("PageCount", "equals", "3"), 1), (new("PageCount", "lessThan", "3"), 2),
                (new("Title", "contains", "' OR 1=1 --"), 0), (new("Title", "contains", "%"), 0)
            })
            {
                store.UpdateList("alice", smart.Id, smartEdit with { Rules = [rule] });
                Check(store.ListBooks("alice", null, listId: smart.Id).Total == count, $"Smart rule failed: {rule}.");
            }
            foreach (var rule in new ListRule[]
            {
                new("Title); DROP TABLE books;--", "equals", "x"), new("Title", "OR 1=1", "x"),
                new("ReadStatus", "contains", "unread"), new("ReadStatus", "equals", "invalid"),
                new("PageCount", "contains", "2"), new("PageCount", "equals", "-1"),
                new("PageCount", "equals", "2147483648"), new("PageCount", "equals", "1.5"),
                new("Title", "equals", new string('x', 2001)), new("Title", "equals", null!), null!
            }) Reject<ArgumentException>(() => store.CreateList("alice", smartEdit with { Rules = [rule] }));
            Reject<ArgumentException>(() => store.CreateList("alice", manualEdit with { Name = " " }));
            Reject<ArgumentException>(() => store.CreateList("alice", manualEdit with { Name = new string('x', 201) }));
            Reject<ArgumentException>(() => store.CreateList("alice", manualEdit with { Kind = "unknown" }));
            Reject<ArgumentException>(() => store.CreateList("alice", manualEdit with { Match = "unknown" }));
            Reject<ArgumentException>(() => store.CreateList("alice", manualEdit with { Rules = smartEdit.Rules }));
            Reject<ArgumentException>(() => store.CreateList("alice", smartEdit with { Rules = [] }));
            Reject<ArgumentException>(() => store.CreateList("alice", smartEdit with { Rules = Enumerable.Repeat(new ListRule("Title", "equals", "x"), 21).ToArray() }));
            Reject<ArgumentException>(() => store.CreateList("alice", smartEdit with { Rules = null! }));
            Reject<ArgumentException>(() => store.AddListBooks("alice", manual.Id, []));
            Reject<ArgumentException>(() => store.AddListBooks("alice", manual.Id, Enumerable.Repeat(ids[0], 101).ToArray()));
            Reject<ArgumentException>(() => store.RemoveListBooks("alice", manual.Id, [new string('x', 129)]));
            Reject<ArgumentException>(() => store.GetList("alice", new string('x', 129)));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ListEdit>("{\"Name\":\"Missing fields\"}"));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ListEdit>("{\"Name\":\"x\",\"Kind\":\"manual\",\"Match\":\"all\",\"Rules\":[],\"UserId\":\"bob\"}"));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ListRule>("{\"Field\":\"Title\",\"Operator\":\"equals\"}"));
            store.DeleteList("alice", manual.Id);
            Reject<KeyNotFoundException>(() => store.GetList("alice", manual.Id));
            Reject<KeyNotFoundException>(() => store.DeleteList("alice", manual.Id));
            Check(store.GetLists("bob").Single().Id == bobReading.Id, "Deleting one user's list affected another user.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
