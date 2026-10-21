using System.IO.Compression;
using System.Text.Json;
using PanelHaven.Web;
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-lists-" + Guid.NewGuid().ToString("N"));
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
            Check(store.GetLists("alice").Any(list => list.Id == manual.Id) && store.GetLists("bob").All(list => list.Id != manual.Id),
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
            Check(store.GetLists("' OR 1=1 --").All(list => list.Id != manual.Id), "User ID was interpreted as SQL.");

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
                (new("PageCount", "notEquals", "3"), 3), (new("PageCount", "greaterOrEqual", "3"), 2),
                (new("PageCount", "lessOrEqual", "3"), 3),
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
                new("DaysSinceAdded", "equals", "-1"), new("DaysSinceAdded", "equals", "1.5"),
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
            Check(store.GetLists("bob").Any(list => list.Id == bobReading.Id), "Deleting one user's list affected another user.");

            var advancedRoot = Path.Combine(directory, "advanced");
            var libraryRoot = Path.Combine(advancedRoot, "library");
            var archivedRoot = Path.Combine(advancedRoot, "archived");
            var unsortedRoot = Path.Combine(advancedRoot, "unsorted");
            foreach (var root in new[] { libraryRoot, archivedRoot, unsortedRoot }) Directory.CreateDirectory(root);
            var fixtures = new (string Root, string Title, string Series, string Number, string Publisher)[]
            {
                (libraryRoot, "Zero", "Gap", "0", "A"), (libraryRoot, "Two", "Gap", "2", "A"),
                (libraryRoot, "Variant", "Gap", "02", "B"), (libraryRoot, "Annual", "Gap", "Annual", "B"),
                (libraryRoot, "First", "Full", "1", "A"), (libraryRoot, "Second", "Full", "2", "B"),
                (archivedRoot, "Archived first", "Gap", "1", "A"), (archivedRoot, "Archived third", "Gap", "3", "A"),
                (unsortedRoot, "Intake", "Gap", "1", "A")
            };
            foreach (var fixture in fixtures)
            {
                using var archive = ZipFile.Open(Path.Combine(fixture.Root, fixture.Title + ".cbz"), ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("1.png").Open()) stream.Write(png);
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write($"<ComicInfo><Title>{fixture.Title}</Title><Series>{fixture.Series}</Series>"
                    + $"<Number>{fixture.Number}</Number><Publisher>{fixture.Publisher}</Publisher></ComicInfo>");
            }
            var database = Path.Combine(advancedRoot, "advanced.sqlite");
            var advanced = new LibraryStore(database, [libraryRoot], archivedRoot, unsortedRoot);
            Check(advanced.ScanAsync(default).GetAwaiter().GetResult().Added == fixtures.Length, "Advanced fixtures failed to import.");
            var byTitle = advanced.ListBooks("alice", null).Items.ToDictionary(book => book.Title, book => book.Id);
            void Sql(string sql, params (string Name, object Value)[] parameters)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                db.Open();
                db.CreateCollation("NATURAL_NUMBER", StringComparer.Ordinal.Compare);
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                command.ExecuteNonQuery();
            }
            SmartListPreview Rules(params ListRule[] rules) => new("all", rules);
            int Matches(string field, string operation, string value, string user = "alice", string location = "library") =>
                advanced.ListBooks(user, null, location: location, advancedFilter: Rules(new ListRule(field, operation, value))).Total;
            advanced.EditMetadata(byTitle["Zero"], new(new()
            {
                ["Year"] = "2024", ["Month"] = "2", ["Day"] = "29", ["Writer"] = "Ada", ["Translator"] = "Lin",
                ["Characters"] = "Hero", ["Teams"] = "Team", ["Tags"] = "Favorite", ["Genre"] = "Adventure",
                ["Volume"] = "2", ["Format"] = "Annual", ["CommunityRating"] = "4.5"
            }));
            advanced.EditMetadata(byTitle["Two"], new(new() { ["Year"] = "2024" }));
            Sql("UPDATE books SET info_xml='<ComicInfo><Year>2023</Year><Month>2</Month><Day>29</Day></ComicInfo>' WHERE id=$id",
                ("$id", byTitle["Annual"]));
            Sql("UPDATE books SET added_at=NULL; UPDATE books SET added_at=1709164800,file_size=5368709120 WHERE id=$id",
                ("$id", byTitle["Zero"]));
            foreach (var (field, operation, value, expected) in new (string, string, string, int)[]
            {
                ("Number", "greaterOrEqual", "2", 4), ("Number", "lessOrEqual", "2", 5),
                ("Number", "greaterThan", "9", 1),
                ("PublicationDate", "equals", "2024-02-29", 1), ("PublicationDate", "isEmpty", "", 5),
                ("PublicationDate", "greaterThan", "2024-02-28", 1), ("PublicationDate", "lessThan", "2024-03-01", 1),
                ("PublicationDate", "notEquals", "2024-02-29", 0), ("Year", "equals", "2024", 2),
                ("AddedDate", "equals", "2024-02-29", 1), ("AddedDate", "isEmpty", "", 5),
                ("DaysSinceAdded", "isNotEmpty", "", 1), ("FileSizeMB", "equals", "5120", 1),
                ("FileType", "equals", "CBZ", 6),
                ("Writer", "equals", "Ada", 1), ("Translator", "contains", "lin", 1),
                ("Characters", "contains", "Hero", 1), ("Teams", "equals", "Team", 1),
                ("Tags", "isNotEmpty", "", 1), ("Genre", "equals", "Adventure", 1),
                ("Volume", "greaterOrEqual", "2", 1), ("Volume", "isEmpty", "", 5),
                ("Format", "equals", "Annual", 1), ("CommunityRating", "greaterThan", "4.4", 1),
                ("CommunityRating", "isEmpty", "", 5), ("HasMissingIssues", "equals", "yes", 4)
            }) Check(Matches(field, operation, value) == expected, $"Advanced rule failed: {field}/{operation}/{value}.");
            Check(Matches("HasMissingIssues", "equals", "yes", location: "archived") == 2
                && Matches("HasMissingIssues", "equals", "no", location: "unsorted") == 1, "Series gaps mixed catalog areas.");
            foreach (var special in new[] { "1.5", "-1", "2147483648", "Annual", "02" })
            {
                Sql("UPDATE books SET number=$number WHERE id=$id", ("$number", special), ("$id", byTitle["Annual"]));
                Check(Matches("HasMissingIssues", "equals", "yes") == 4, "Special or duplicate issue incorrectly filled a gap.");
            }
            Sql("UPDATE books SET number='1',availability='unavailable' WHERE id=$id", ("$id", byTitle["Annual"]));
            Check(Matches("HasMissingIssues", "equals", "yes") == 0, "Unavailable catalog issue did not fill the gap.");
            Sql("UPDATE books SET number='Annual' WHERE id=$id", ("$id", byTitle["Annual"]));
            advanced.SaveProgress("alice", byTitle["Zero"], new(0, true));
            advanced.SaveProgress("alice", byTitle["Variant"], new(0, false));
            foreach (var title in new[] { "First", "Second" }) advanced.SaveProgress("alice", byTitle[title], new(0, true));
            foreach (var title in new[] { "Zero", "Two", "Variant", "Annual" }) advanced.SaveProgress("bob", byTitle[title], new(0, true));
            Sql("UPDATE progress SET updated_at=NULL WHERE user_id='alice' AND book_id=$id", ("$id", byTitle["Variant"]));
            Check(Matches("DaysSinceRead", "equals", "0") == 3 && Matches("DaysSinceRead", "isEmpty", "") == 3,
                "Last-read age invented unknown timestamps.");
            Check(Matches("DaysSinceRead", "equals", "0", "bob") == 4, "Last-read age mixed accounts.");
            Check(Matches("SeriesReadStatus", "equals", "completed") == 2
                && Matches("SeriesReadStatus", "equals", "in-progress") == 4
                && Matches("SeriesReadStatus", "equals", "completed", "bob") == 4
                && Matches("SeriesReadStatus", "equals", "unread", location: "archived") == 2,
                "Series status mixed users, locations, or partial completion.");
            Check(advanced.ListBooks("alice", null, status: "completed", publisher: "A",
                advancedFilter: Rules(new ListRule("SeriesReadStatus", "equals", "in-progress"))).Items.Single().Id == byTitle["Zero"],
                "Series completion used the outer filtered subset.");
            var own = advanced.CreateList("alice", new("Own", "manual", "all", []));
            var foreign = advanced.CreateList("bob", new("Private", "manual", "all", []));
            advanced.AddListBooks("alice", own.Id, [byTitle["Zero"], byTitle["First"]]);
            advanced.AddListBooks("bob", foreign.Id, [byTitle["Two"]]);
            var saved = advanced.CreateList("alice", new("Saved", "smart", "all", [new("Publisher", "notEquals", "Absent")]));
            Check(Matches("ManualList", "equals", own.Id) == 2 && Matches("ManualList", "notEquals", own.Id) == 4
                && Matches("ManualList", "isEmpty", "") == 4 && Matches("ManualList", "isNotEmpty", "") == 2,
                "Manual membership/empty rules failed.");
            foreach (var privateId in new[] { foreign.Id, saved.Id, "missing" })
                Check(Matches("ManualList", "equals", privateId) == 0 && Matches("ManualList", "notEquals", privateId) == 6,
                    "Manual membership exposed foreign state or recursed into a smart list.");
            var nestedFilter = new SmartListPreview("all",
                [new("Number", "greaterOrEqual", "2"), new(Match: "any", Rules: [new("Publisher", "equals", "A"), new("Publisher", "equals", "B")])]);
            var composed = advanced.ListBooks("alice", null, page: 2, pageSize: 1, sort: "number", listId: saved.Id,
                sectionBy: "publisher", groupBy: "series", groupKey: "Gap", advancedFilter: nestedFilter);
            Check(composed.Total == 3 && composed.Items.Count == 1 && composed.Sections.Single().Key == "B",
                "Saved plus unsaved rules collided or failed before section/group pagination.");
            var stacks = advanced.ListGroups("alice", null, "series", page: 2, pageSize: 1, listId: saved.Id,
                sectionBy: "publisher", advancedFilter: nestedFilter);
            Check(stacks.Total == 3 && stacks.Items.Single().Count >= 1 && stacks.Sections.Single().Key == "B",
                "Advanced filtering did not compose with stack/section pagination.");
            Check(advanced.ListGroups("alice", null, "series", sectionBy: "publisher", sectionKey: "A",
                advancedFilter: nestedFilter).Items.Single().Count == 1, "Advanced section filtering aggregated excluded books.");
            foreach (var rule in new ListRule[]
            {
                new("PublicationDate", "equals", "2023-02-29"), new("AddedDate", "equals", "2024"),
                new("DaysSinceRead", "equals", "-1"), new("FileSizeMB", "equals", "NaN"),
                new("FileSizeMB", "greaterThan", "Infinity"), new("FileSizeMB", "equals", "-1"),
                new("HasMissingIssues", "equals", "true"), new("ManualList", "equals", ""),
                new("Number", "greaterThan", " "), new("CommunityRating", "equals", "6")
            }) Reject<ArgumentException>(() => advanced.ListBooks("alice", null, advancedFilter: Rules(rule)));
            var filterJson = JsonSerializer.Serialize(nestedFilter, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(LibraryStore.ParseCatalogFilter(null) is null
                && advanced.ListBooks("alice", null, advancedFilter: LibraryStore.ParseCatalogFilter(filterJson)).Total == 4,
                "Catalog filter parsing changed matching.");
            foreach (var invalid in new[]
            {
                "", "null", "[]", "{", "{\"match\":\"all\",\"rules\":[]}", "{\"match\":\"all\",\"rules\":null}",
                "{\"match\":\"all\",\"match\":\"any\",\"rules\":[{\"field\":\"Title\",\"operator\":\"equals\",\"value\":\"x\"}]}",
                "{\"match\":\"all\",\"rules\":[{\"field\":\"Title\",\"operator\":\"equals\",\"value\":\"x\",\"extra\":1}]}",
                "{\"match\":\"all\",\"rules\":[{\"field\":\"Title\",\"operator\":\"equals\",\"value\":\"x\"}],\"extra\":1}",
                JsonSerializer.Serialize(Rules(new ListRule("Title", "equals", new string('界', 1000)))),
                JsonSerializer.Serialize(Rules(Enumerable.Repeat(new ListRule("Title", "equals", "x"), 21).ToArray()))
            }) Reject<ArgumentException>(() => LibraryStore.ParseCatalogFilter(invalid));
            ListRule tooDeep = new("Title", "equals", "Zero");
            for (var level = 0; level < 4; level++) tooDeep = new(Match: "all", Rules: [tooDeep]);
            Reject<ArgumentException>(() => LibraryStore.ParseCatalogFilter(JsonSerializer.Serialize(Rules(tooDeep))));
            Reject<ArgumentException>(() => LibraryStore.ParseCatalogFilter(
                "{\"match\":\"all\",\"rules\":[{\"field\":\"Unknown\",\"operator\":\"equals\",\"value\":\"x\"}]}"));
            advanced.SaveRating("alice", byTitle["Zero"], new(5));
            Check(Matches("PersonalRating", "equals", "5") == 1 && Matches("PersonalRating", "equals", "5", "bob") == 0,
                "Unsaved rating filters leaked between accounts.");
            var sharedRules = Rules(new("SeriesReadStatus", "equals", "in-progress"), new("HasMissingIssues", "equals", "yes"),
                new("ManualList", "equals", own.Id), new("PublicationDate", "equals", "2024-02-29"), new("FileSizeMB", "greaterThan", "2048"));
            advanced.UpdateList("alice", saved.Id, new("Saved", "smart", sharedRules.Match, sharedRules.Rules));
            var unsavedBooks = advanced.ListBooks("alice", null, advancedFilter: sharedRules).Items;
            Check(unsavedBooks.Single().Id == byTitle["Zero"]
                && advanced.ListBooks("alice", null, listId: saved.Id, advancedFilter: sharedRules).Items.SequenceEqual(unsavedBooks)
                && advanced.PreviewSmartList("alice", sharedRules).Items.SequenceEqual(unsavedBooks),
                "Saved/preview/unsaved advanced semantics diverged or repeated series parameters collided.");
            Sql("UPDATE books SET series='' WHERE id=$id", ("$id", byTitle["First"]));
            Check(advanced.ListBooks("alice", "First", advancedFilter: Rules(new ListRule("HasMissingIssues", "equals", "no"))).Total == 1
                && advanced.ListBooks("alice", "First", advancedFilter: Rules(new ListRule("SeriesReadStatus", "equals", "completed"))).Total == 0,
                "Unnamed series invented completion or missing issues.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
