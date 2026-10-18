using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;

public static class OrganizationChecks
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
            catch (T exception) when (exception is not SqliteException sqlite || sqlite.SqliteErrorCode == 19) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-organization-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(directory, "organization.sqlite");
            var accounts = new Accounts(database);
            var admin = accounts.Create("admin", "organization-admin", "admin");
            var owner = accounts.Create("reader", "organization-reader", "reader");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString());
            db.Open();
            void Execute(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            object? Scalar(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return command.ExecuteScalar();
            }
            const string legacyXml = "<ComicInfo marker='keep'><Title>Legacy</Title><Series>One</Series><Writer>Ada</Writer><Penciller>Ink</Penciller><Genre>Mystery</Genre><Year>1998</Year><Rating>4</Rating><Unknown>keep</Unknown></ComicInfo>";
            Execute("""
                CREATE TABLE books (
                    id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,title TEXT NOT NULL,series TEXT NOT NULL,
                    number TEXT NOT NULL,publisher TEXT NOT NULL,summary TEXT NOT NULL,page_count INTEGER NOT NULL,
                    info_xml TEXT NOT NULL,book_xml TEXT,edited INTEGER NOT NULL DEFAULT 0,file_size INTEGER NOT NULL,modified_ticks INTEGER NOT NULL);
                CREATE TABLE progress (user_id TEXT NOT NULL,book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                    page INTEGER NOT NULL,completed INTEGER NOT NULL,PRIMARY KEY(user_id,book_id));
                CREATE TABLE lists (id TEXT PRIMARY KEY,user_id TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,
                    match TEXT NOT NULL,rules_json TEXT NOT NULL);
                CREATE TABLE list_books (list_id TEXT NOT NULL REFERENCES lists(id) ON DELETE CASCADE,
                    book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,position INTEGER NOT NULL,PRIMARY KEY(list_id,book_id));
                INSERT INTO books VALUES('legacy-book',$path,'Legacy','One','0','Press','marker',2,$xml,NULL,1,0,0);
                INSERT INTO progress VALUES($user,'legacy-book',0,1);
                INSERT INTO lists VALUES('legacy-list',$user,'Existing manual','manual','all','[]');
                INSERT INTO lists VALUES('legacy-smart',$user,'Existing smart','smart','all',$rules);
                INSERT INTO list_books VALUES('legacy-list','legacy-book',7);
                """, ("$path", OperatingSystem.IsWindows() ? Path.Combine(root, "legacy.cbz").ToUpperInvariant() : Path.Combine(root, "legacy.cbz")),
                ("$xml", legacyXml), ("$user", owner.Id),
                ("$rules", JsonSerializer.Serialize(new ListRule[] { new("Title", "contains", "Legacy") })));
            var store = new LibraryStore(database, [root]);
            accounts = new Accounts(database);
            Check(store.GetList(owner.Id, "legacy-list").FolderId is null
                && store.GetList(owner.Id, "legacy-list").BookIds!.SequenceEqual(["legacy-book"])
                && (long)Scalar("SELECT position FROM list_books WHERE list_id='legacy-list'")! == 7
                && store.ListBooks(owner.Id, null, listId: "legacy-smart").Items.Single().Id == "legacy-book"
                && store.GetBook(owner.Id, "legacy-book").Completed
                && (string)Scalar("SELECT info_xml FROM books WHERE id='legacy-book'")! == legacyXml,
                "Organization migration lost existing lists, positions, progress, rules, or original XML.");

            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void Archive(string title, string? year, string writer, string penciller, string genre, bool legacy = false)
            {
                var path = Path.Combine(root, title + ".cbz");
                if (File.Exists(path)) File.Delete(path);
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                for (var page = 0; page < 2; page++)
                    using (var image = archive.CreateEntry(page + ".png").Open()) image.Write(png);
                using var xml = new StreamWriter(archive.CreateEntry(legacy ? "ComicBook.xml" : "ComicInfo.xml").Open());
                xml.Write(new XElement(legacy ? "ComicBook" : "ComicInfo", new XElement("Title", title),
                    new XElement("Series", title is "Alpha" or "Beta" ? "One" : "Two"),
                    new XElement("Publisher", "Press"), new XElement("Summary", "marker"), new XElement("Writer", writer),
                    new XElement("Penciller", penciller), new XElement("Genre", genre),
                    year is null ? null : new XElement("Year", year), new XElement("Rating", "4")));
            }
            Archive("Alpha", "2005", "Ada", "Ink", "Hero");
            Archive("Beta", "invalid", "Bob", "Other", "Mystery");
            Archive("Gamma", null, "Ada", "Ink", "Mystery", legacy: true);
            Archive("Delta", "-1", "Don", "Other", "Drama");
            Archive("Epsilon", "0", "Zero", "Other", "Drama");
            Archive("Zeta", "10000", "Zed", "Other", "Drama");
            Archive("Eta", "2001.5", "Eve", "Other", "Drama");
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 7, "Organization archives did not import.");
            var ids = store.ListBooks(owner.Id, null).Items.ToDictionary(book => book.Title, book => book.Id);
            var beforeRating = (string)store.GetBook(owner.Id, ids["Alpha"]).Metadata!["comicInfoXml"]!;
            var archiveBytes = File.ReadAllBytes(Path.Combine(root, "Alpha.cbz"));
            store.SaveRating(owner.Id, ids["Legacy"], new(5));
            store.SaveRating(owner.Id, ids["Alpha"], new(3));
            store.SaveRating(owner.Id, ids["Beta"], new(5));
            store.SaveRating(admin.Id, ids["Legacy"], new(2));
            Check(store.GetBook(owner.Id, ids["Legacy"]).PersonalRating == 5
                && store.GetBook(admin.Id, ids["Legacy"]).PersonalRating == 2
                && store.GetBook(admin.Id, ids["Alpha"]).PersonalRating == 0
                && (string)store.GetBook(owner.Id, ids["Alpha"]).Metadata!["comicInfoXml"]! == beforeRating
                && File.ReadAllBytes(Path.Combine(root, "Alpha.cbz")).SequenceEqual(archiveBytes),
                "Personal ratings leaked, changed shared Rating/XML, or wrote to an archive.");
            store.SaveRating(owner.Id, ids["Alpha"], new(0));
            Check(store.GetBook(owner.Id, ids["Alpha"]).PersonalRating == 0
                && (long)Scalar("SELECT COUNT(*) FROM ratings WHERE user_id=$user AND book_id=$book", ("$user", owner.Id), ("$book", ids["Alpha"]))! == 0,
                "Clearing a rating retained private state.");
            store.SaveRating(owner.Id, ids["Alpha"], new(0));
            store.SaveRating(owner.Id, ids["Alpha"], new(3));
            foreach (var value in new[] { -1, 6 }) Reject<ArgumentException>(() => store.SaveRating(owner.Id, ids["Alpha"], new(value)));
            Reject<ArgumentException>(() => store.SaveRating(owner.Id, ids["Alpha"], null!));
            foreach (var value in new[] { 0, 5 }) Reject<KeyNotFoundException>(() => store.SaveRating(owner.Id, "missing", new(value)));
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var json in new[] { "{}", "{\"rating\":null}", "{\"rating\":1.5}", "{\"rating\":\"3\"}", "{\"rating\":true}", "{\"rating\":3,\"userId\":\"other\"}", "{\"rating\":2147483648}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<RatingEdit>(json, jsonOptions));
            Reject<SqliteException>(() => Execute("UPDATE ratings SET rating=1.5 WHERE user_id=$user", ("$user", owner.Id)));
            foreach (var direction in new[] { "asc", "desc" })
            {
                var ordered = store.ListBooks(owner.Id, null, sort: "personalRating", direction: direction).Items;
                var expected = direction == "asc" ? ordered.OrderBy(book => book.PersonalRating) : ordered.OrderByDescending(book => book.PersonalRating);
                Check(ordered.SequenceEqual(expected.ThenBy(book => book.Id, StringComparer.Ordinal)), "Rating sort lost numeric or stable tie order.");
                var pages = Enumerable.Range(1, 4).Select(page => store.ListBooks(owner.Id, null, page, 2, "personalRating", direction)).ToArray();
                Check(pages.All(page => page.Total == 8) && pages.SelectMany(page => page.Items).SequenceEqual(ordered), "Rating pagination lost or duplicated books.");
            }
            Check(store.ListBooks(owner.Id, null, rating: 0).Total == 5 && store.ListBooks(admin.Id, null, rating: 5).Total == 0,
                "Exact unrated/rating filters leaked across owners.");
            Reject<ArgumentException>(() => store.ListBooks(owner.Id, null, rating: 6));
            Reject<ArgumentException>(() => store.ListGroups(owner.Id, null, "series", rating: -1));

            var folder = store.CreateListFolder(owner.Id, new("  Reading  "));
            var otherFolder = store.CreateListFolder(owner.Id, new("Later"));
            var adminFolder = store.CreateListFolder(admin.Id, new("Admin"));
            Check(folder.Name == "Reading" && store.GetListFolders(owner.Id).Count == 2
                && store.GetListFolders(admin.Id).Single() == adminFolder, "Folder names or ownership were not preserved.");
            Check(store.UpdateListFolder(owner.Id, folder.Id, new("  Renamed  ")).Name == "Renamed", "Folder rename was not trimmed.");
            foreach (var name in new[] { " ", new string('x', 201), "bad\nname" })
                Reject<ArgumentException>(() => store.CreateListFolder(owner.Id, new(name)));
            Reject<ArgumentException>(() => store.CreateListFolder(owner.Id, null!));
            Reject<ArgumentException>(() => store.CreateListFolder(owner.Id, new(null!)));
            foreach (var json in new[] { "{}", "{\"name\":3}", "{\"name\":\"x\",\"parentId\":\"y\"}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<ListFolderEdit>(json, jsonOptions));
            Reject<KeyNotFoundException>(() => store.UpdateListFolder(admin.Id, folder.Id, new("Stolen")));
            Reject<KeyNotFoundException>(() => store.DeleteListFolder(admin.Id, folder.Id));
            var manualEdit = new ListEdit("Existing manual", "manual", "all", [], folder.Id);
            store.UpdateList(owner.Id, "legacy-list", manualEdit);
            store.AddListBooks(owner.Id, "legacy-list", [ids["Beta"], ids["Alpha"], ids["Gamma"]]);
            store.MoveListBook(owner.Id, "legacy-list", ids["Beta"], ids["Legacy"]);
            var memberOrder = store.GetList(owner.Id, "legacy-list").BookIds!;
            Reject<KeyNotFoundException>(() => store.CreateList(owner.Id, manualEdit with { FolderId = adminFolder.Id }));
            Reject<KeyNotFoundException>(() => store.UpdateList(owner.Id, "legacy-list", manualEdit with { FolderId = adminFolder.Id }));
            Reject<KeyNotFoundException>(() => store.CreateList(owner.Id, manualEdit with { FolderId = "missing" }));
            Check(store.GetList(owner.Id, "legacy-list").FolderId == folder.Id, "Rejected reassignment modified a list.");
            Reject<SqliteException>(() => Execute("UPDATE lists SET folder_id=$folder WHERE id='legacy-list'", ("$folder", adminFolder.Id)));
            Reject<SqliteException>(() => Execute("UPDATE list_folders SET user_id=$user WHERE id=$folder", ("$user", admin.Id), ("$folder", folder.Id)));
            store.UpdateList(owner.Id, "legacy-list", manualEdit with { FolderId = otherFolder.Id });
            store.DeleteListFolder(owner.Id, folder.Id);
            Check(store.GetList(owner.Id, "legacy-list").FolderId == otherFolder.Id, "Deleting a previous folder unfiled a reassigned list.");
            store.DeleteListFolder(owner.Id, otherFolder.Id);
            Check(store.GetList(owner.Id, "legacy-list").FolderId is null
                && store.GetList(owner.Id, "legacy-list").BookIds!.SequenceEqual(memberOrder), "Folder deletion changed list membership/order.");
            var filtered = store.ListBooks(owner.Id, "marker", 2, 1, "list", publisher: "Press", listId: "legacy-list", rating: 5);
            Check(filtered.Total == 2 && filtered.Items.Single().Id == ids["Legacy"], "Rating/list/search/publisher filters ran after pagination.");

            var smartEdit = new ListEdit("Rules", "smart", "all", [new("Writer", "contains", "ada")]);
            var smart = store.CreateList(owner.Id, smartEdit);
            void Matches(ListRule rule, params string[] expected)
            {
                store.UpdateList(owner.Id, smart.Id, smartEdit with { Rules = [rule] });
                var actual = store.ListBooks(owner.Id, null, listId: smart.Id).Items.Select(book => book.Title).Order(StringComparer.Ordinal);
                Check(actual.SequenceEqual(expected.Order(StringComparer.Ordinal)), $"Expanded rule failed: {rule}.");
            }
            Matches(new("Writer", "contains", "ADA"), "Legacy", "Alpha", "Gamma");
            Matches(new("Penciller", "equals", "ink"), "Legacy", "Alpha", "Gamma");
            Matches(new("Genre", "notEquals", "mystery"), "Alpha", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Year", "lessThan", "2000"), "Legacy", "Epsilon");
            Matches(new("Year", "equals", "0"), "Epsilon");
            Matches(new("Year", "greaterThan", "0"), "Legacy", "Alpha");
            Matches(new("PersonalRating", "equals", "0"), "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("PersonalRating", "lessThan", "3"), "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            var allRules = smartEdit with { Rules = [new("Writer", "equals", "Ada"), new("Year", "greaterThan", "2000"), new("PersonalRating", "greaterThan", "2")] };
            store.UpdateList(owner.Id, smart.Id, allRules);
            Check(store.ListBooks(owner.Id, null, listId: smart.Id).Items.Single().Id == ids["Alpha"], "All expanded rules did not intersect.");
            store.SaveRating(owner.Id, ids["Alpha"], new(1));
            Check(store.ListBooks(owner.Id, null, listId: smart.Id).Total == 0, "Smart rules did not reflect the current private rating.");
            store.SaveRating(owner.Id, ids["Alpha"], new(3));
            var adminSmart = store.CreateList(admin.Id, allRules);
            Check(store.ListBooks(admin.Id, null, listId: adminSmart.Id).Total == 0, "Smart rating rules leaked another owner's rating.");
            store.UpdateList(owner.Id, smart.Id, smartEdit with { Match = "any", Rules = [new("Writer", "equals", "Ada"), new("Genre", "equals", "Mystery")] });
            Check(store.ListBooks(owner.Id, null, listId: smart.Id).Total == 4, "Any expanded rules lost overlapping matches or counted duplicates.");
            foreach (var rule in new ListRule[] { new("Year", "equals", "-1"), new("Year", "equals", "10000"), new("Year", "equals", "1.5"),
                new("Year", "contains", "2000"), new("PersonalRating", "equals", "6"), new("PersonalRating", "notEquals", "2"),
                new("Writer", "greaterThan", "a"), new("Penciller", "equals", null!) })
                Reject<ArgumentException>(() => store.CreateList(owner.Id, smartEdit with { Rules = [rule] }));

            store.SaveProgress(owner.Id, ids["Alpha"], new(0, false));
            var group = store.ListGroups(owner.Id, null, "series").Items.Single(item => item.Key == "One");
            Check(group is { Count: 3, Unread: 1, InProgress: 1, Completed: 1, CompletionPercent: 33 }, "Series statistics did not partition books or floor completed-book percentage.");
            Check(store.ListGroups(admin.Id, null, "series").Items.All(item => item.Unread == item.Count && item.Completed == 0 && item.InProgress == 0),
                "Series statistics leaked private progress.");
            foreach (var listId in new[] { "legacy-list", smart.Id })
            foreach (var status in new[] { "all", "unread", "in-progress", "completed" })
            foreach (var availability in new[] { "all", "available", "missing" })
            {
                var groups = store.ListGroups(owner.Id, "marker", "series", pageSize: 1, status: status, publisher: "Press", listId: listId, availability: availability, rating: 5);
                for (var page = 1; page <= groups.Total; page++)
                foreach (var item in store.ListGroups(owner.Id, "marker", "series", page, 1, status, "Press", listId, availability, 5).Items)
                {
                    var members = store.ListBooks(owner.Id, "marker", status: status, publisher: "Press", listId: listId, availability: availability, groupBy: "series", groupKey: item.Key, rating: 5);
                    Check(item.Count == members.Total && item.Unread == members.Items.Count(book => !book.Started)
                        && item.InProgress == members.Items.Count(book => book.Started && !book.Completed)
                        && item.Completed == members.Items.Count(book => book.Completed)
                        && item.Unread + item.InProgress + item.Completed == item.Count
                        && item.CompletionPercent == item.Completed * 100 / item.Count, "Filtered group statistics diverged from drilldown members.");
                }
            }
            Archive("Gamma", "2024", "Rescanned", "New artist", "New genre", legacy: true);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Updated == 1, "Changed metadata was not rescanned.");
            Matches(new("Writer", "equals", "Rescanned"), "Gamma");
            Matches(new("Penciller", "equals", "New artist"), "Gamma");
            Matches(new("Genre", "equals", "New genre"), "Gamma");
            Matches(new("Year", "equals", "2024"), "Gamma");
            store.EditMetadata(ids["Alpha"], new(new() { ["Writer"] = "Edited", ["Penciller"] = "Edited ink", ["Genre"] = "Edited genre", ["Year"] = "2030" }));
            Matches(new("Writer", "equals", "Edited"), "Alpha");
            Matches(new("Penciller", "equals", "Edited ink"), "Alpha");
            Matches(new("Genre", "equals", "Edited genre"), "Alpha");
            Matches(new("Year", "equals", "2030"), "Alpha");
            store.EditMetadataBulk(new([ids["Beta"], ids["Gamma"]], new() { ["Writer"] = "Bulk", ["Penciller"] = "Bulk ink", ["Genre"] = "Bulk genre", ["Year"] = "2031" }));
            Matches(new("Writer", "equals", "Bulk"), "Beta", "Gamma");
            Matches(new("Penciller", "equals", "Bulk ink"), "Beta", "Gamma");
            Matches(new("Genre", "equals", "Bulk genre"), "Beta", "Gamma");
            Matches(new("Year", "equals", "2031"), "Beta", "Gamma");
            Reject<KeyNotFoundException>(() => store.EditMetadataBulk(new([ids["Beta"], "missing"], new() { ["Writer"] = "Partial" })));
            Matches(new("Writer", "equals", "Bulk"), "Beta", "Gamma");
            store.EditMetadata(ids["Beta"], new(new() { ["Year"] = null, ["Writer"] = null, ["Penciller"] = null, ["Genre"] = null }));
            store.EditMetadata(ids["Gamma"], new(new() { ["Year"] = "-1" }));
            Matches(new("Year", "equals", "2031"));
            Matches(new("Writer", "equals", "Bulk"), "Gamma");
            Matches(new("Penciller", "equals", "Bulk ink"), "Gamma");
            Matches(new("Genre", "equals", "Bulk genre"), "Gamma");
            Archive("Alpha", "1980", "Source change", "Source ink", "Source genre");
            store.ScanAsync(default).GetAwaiter().GetResult();
            store = new LibraryStore(database, [root]);
            accounts = new Accounts(database);
            Matches(new("Year", "equals", "2030"), "Alpha");
            Matches(new("Writer", "equals", "Edited"), "Alpha");
            Matches(new("Penciller", "equals", "Edited ink"), "Alpha");
            Matches(new("Genre", "equals", "Edited genre"), "Alpha");
            Check(store.GetBook(owner.Id, ids["Alpha"]).PersonalRating == 3
                && store.GetList(owner.Id, "legacy-list").BookIds!.SequenceEqual(memberOrder), "Restart/rescan lost ratings or membership order.");

            var retainedFolder = store.CreateListFolder(owner.Id, new("Private"));
            store.UpdateList(owner.Id, "legacy-list", manualEdit with { FolderId = retainedFolder.Id });
            var adminList = store.CreateList(admin.Id, new("Admin list", "manual", "all", [], adminFolder.Id));
            store.AddListBooks(admin.Id, adminList.Id, [ids["Legacy"]]);
            var sharedXml = (string)store.GetBook(owner.Id, ids["Alpha"]).Metadata!["comicInfoXml"]!;
            accounts.DeleteUser(admin.Id, admin.SessionVersion, owner.Id);
            foreach (var table in new[] { "ratings", "list_folders", "lists", "progress" })
                Check((long)Scalar($"SELECT COUNT(*) FROM {table} WHERE user_id=$user", ("$user", owner.Id))! == 0, "Account deletion left organization data in " + table);
            Check((long)Scalar("SELECT COUNT(*) FROM list_books WHERE list_id='legacy-list'")! == 0
                && store.GetListFolders(admin.Id).Single() == adminFolder
                && store.GetList(admin.Id, adminList.Id).BookIds!.SequenceEqual([ids["Legacy"]])
                && store.GetBook(admin.Id, ids["Legacy"]).PersonalRating == 2
                && (string)store.GetBook(admin.Id, ids["Alpha"]).Metadata!["comicInfoXml"]! == sharedXml, "Account deletion damaged another account or shared metadata.");
            Reject<SqliteException>(() => store.SaveRating(owner.Id, ids["Alpha"], new(5)));
            Reject<SqliteException>(() => store.CreateListFolder(owner.Id, new("Stale")));
            Reject<KeyNotFoundException>(() => store.UpdateListFolder(owner.Id, retainedFolder.Id, new("Stale")));
            Reject<KeyNotFoundException>(() => store.UpdateList(owner.Id, "legacy-list", manualEdit));
            foreach (var table in new[] { "ratings", "list_folders" })
                Reject<SqliteException>(() => Execute($"UPDATE {table} SET user_id=$deleted WHERE user_id=$admin", ("$deleted", owner.Id), ("$admin", admin.Id)));
            store.SaveRating(owner.Id, ids["Alpha"], new(0));
            Check((long)Scalar("SELECT COUNT(*) FROM ratings WHERE user_id=$user", ("$user", owner.Id))! == 0, "Stale clear recreated private data.");
            Reject<InvalidOperationException>(() => accounts.DeleteUser(admin.Id, admin.SessionVersion, admin.Id));
            store.RemoveMissingBook(ids["Legacy"]);
            Check((long)Scalar("SELECT COUNT(*) FROM ratings WHERE book_id=$book", ("$book", ids["Legacy"]))! == 0
                && store.GetList(admin.Id, adminList.Id).BookIds!.Length == 0, "Catalog removal failed to cascade ratings and membership.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
