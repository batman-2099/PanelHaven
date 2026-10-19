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
                CREATE TABLE list_folders (id TEXT PRIMARY KEY,user_id TEXT NOT NULL,name TEXT NOT NULL);
                INSERT INTO list_folders VALUES('legacy-folder',$user,'Old folder');
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
            Check(store.GetListFolders(owner.Id).Single() == new ListFolder("legacy-folder", "Old folder"),
                "Existing folder migration did not preserve the folder as a root.");

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

            var folder = store.UpdateListFolder(owner.Id, "legacy-folder", new("  Reading  "));
            var otherFolder = store.CreateListFolder(owner.Id, new("Later"));
            var adminFolder = store.CreateListFolder(admin.Id, new("Admin"));
            Check(folder.Name == "Reading" && store.GetListFolders(owner.Id).Count == 2
                && store.GetListFolders(admin.Id).Single() == adminFolder, "Folder names or ownership were not preserved.");
            Check(store.UpdateListFolder(owner.Id, folder.Id, new("  Renamed  ")).Name == "Renamed", "Folder rename was not trimmed.");
            foreach (var name in new[] { " ", new string('x', 201), "bad\nname" })
                Reject<ArgumentException>(() => store.CreateListFolder(owner.Id, new(name)));
            Reject<ArgumentException>(() => store.CreateListFolder(owner.Id, null!));
            Reject<ArgumentException>(() => store.CreateListFolder(owner.Id, new(null!)));
            foreach (var json in new[] { "{}", "{\"name\":3}", "{\"name\":\"x\",\"parentId\":3}", "{\"name\":\"x\",\"unexpected\":true}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<ListFolderEdit>(json, jsonOptions));
            Reject<KeyNotFoundException>(() => store.UpdateListFolder(admin.Id, folder.Id, new("Stolen")));
            Reject<KeyNotFoundException>(() => store.DeleteListFolder(admin.Id, folder.Id));
            var child = store.CreateListFolder(owner.Id, new("Child", folder.Id));
            var grandchild = store.CreateListFolder(owner.Id, new("Grandchild", child.Id));
            Check(store.GetListFolders(owner.Id).Single(item => item.Id == grandchild.Id).ParentId == child.Id,
                "Nested folder parent was not persisted.");
            Reject<ArgumentException>(() => store.UpdateListFolder(owner.Id, folder.Id, new("Bad rename", folder.Id)));
            Reject<ArgumentException>(() => store.UpdateListFolder(owner.Id, folder.Id, new("Bad rename", grandchild.Id)));
            Reject<KeyNotFoundException>(() => store.CreateListFolder(owner.Id, new("Cross-account", adminFolder.Id)));
            Reject<KeyNotFoundException>(() => store.CreateListFolder(owner.Id, new("Missing parent", "missing")));
            Reject<KeyNotFoundException>(() => store.UpdateListFolder(owner.Id, folder.Id, new("Bad rename", adminFolder.Id)));
            Check(store.GetListFolders(owner.Id).Single(item => item.Id == folder.Id) == (folder with { Name = "Renamed" }),
                "Rejected reparenting changed the folder's name or parent.");
            Check(store.UpdateListFolder(owner.Id, child.Id, new("Moved", otherFolder.Id)).ParentId == otherFolder.Id
                && store.GetListFolders(owner.Id).Single(item => item.Id == grandchild.Id).ParentId == child.Id,
                "Reparenting changed descendants.");
            Check(store.UpdateListFolder(owner.Id, child.Id, new("Root")).ParentId is null, "Folder could not move to root.");
            store.UpdateListFolder(owner.Id, child.Id, new("Child", folder.Id));
            Reject<SqliteException>(() => Execute("UPDATE list_folders SET parent_id=$child WHERE id=$folder", ("$child", grandchild.Id), ("$folder", folder.Id)));
            Reject<SqliteException>(() => Execute("UPDATE list_folders SET parent_id=id WHERE id=$folder", ("$folder", folder.Id)));
            Reject<SqliteException>(() => Execute("UPDATE list_folders SET parent_id=$parent WHERE id=$child", ("$parent", adminFolder.Id), ("$child", child.Id)));
            Reject<SqliteException>(() => Execute("UPDATE list_folders SET user_id=$admin WHERE id=$folder", ("$admin", admin.Id), ("$folder", folder.Id)));
            Reject<SqliteException>(() => Execute("UPDATE list_folders SET user_id=$admin WHERE id=$child", ("$admin", admin.Id), ("$child", grandchild.Id)));
            Reject<SqliteException>(() => Execute("INSERT INTO list_folders(id,user_id,name,parent_id) VALUES('cross-owner',$owner,'X',$parent)",
                ("$owner", owner.Id), ("$parent", adminFolder.Id)));
            Reject<SqliteException>(() => Execute("INSERT INTO list_folders(id,user_id,name,parent_id) VALUES('self-parent',$owner,'X','self-parent')", ("$owner", owner.Id)));
            var directList = store.CreateList(owner.Id, new("Direct", "manual", "all", [], folder.Id));
            var childList = store.CreateList(owner.Id, new("Child list", "manual", "all", [], child.Id));
            var deepList = store.CreateList(owner.Id, new("Deep list", "smart", "all", [new("Title", "equals", "Alpha")], grandchild.Id));
            store.AddListBooks(owner.Id, directList.Id, [ids["Alpha"]]);
            store.AddListBooks(owner.Id, childList.Id, [ids["Beta"], ids["Alpha"]]);
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
            Check(store.GetListFolders(owner.Id).Single(item => item.Id == child.Id).ParentId is null
                && store.GetListFolders(owner.Id).Single(item => item.Id == grandchild.Id).ParentId == child.Id
                && store.GetList(owner.Id, directList.Id).FolderId is null
                && store.GetList(owner.Id, directList.Id).BookIds!.SequenceEqual([ids["Alpha"]])
                && store.GetList(owner.Id, childList.Id).FolderId == child.Id
                && store.GetList(owner.Id, childList.Id).BookIds!.SequenceEqual([ids["Beta"], ids["Alpha"]])
                && store.GetList(owner.Id, deepList.Id).FolderId == grandchild.Id
                && store.ListBooks(owner.Id, null, listId: deepList.Id).Items.Single().Id == ids["Alpha"]
                && store.GetBook(owner.Id, ids["Legacy"]).Completed
                && store.GetBook(owner.Id, ids["Alpha"]).PersonalRating == 3,
                "Folder deletion lost hierarchy, list membership, smart rules, or private book state.");
            store.DeleteListFolder(owner.Id, otherFolder.Id);
            Check(store.GetList(owner.Id, "legacy-list").FolderId is null
                && store.GetList(owner.Id, "legacy-list").BookIds!.SequenceEqual(memberOrder), "Folder deletion changed list membership/order.");
            var filtered = store.ListBooks(owner.Id, "marker", 2, 1, "list", publisher: "Press", listId: "legacy-list", rating: 5);
            Check(filtered.Total == 2 && filtered.Items.Single().Id == ids["Legacy"], "Rating/list/search/publisher filters ran after pagination.");

            var smartEdit = new ListEdit("Rules", "smart", "all", [new("Writer", "contains", "ada")]);
            var smart = store.CreateList(owner.Id, smartEdit);
            void Matches(ListRule rule, params string[] expected)
            {
                var listsBefore = (long)Scalar("SELECT COUNT(*) FROM lists")!;
                var preview = store.PreviewSmartList(owner.Id, new("all", [rule]));
                Check((long)Scalar("SELECT COUNT(*) FROM lists")! == listsBefore, "Preview persisted a list.");
                store.UpdateList(owner.Id, smart.Id, smartEdit with { Rules = [rule] });
                var saved = store.ListBooks(owner.Id, null, listId: smart.Id);
                Check(preview.Total == saved.Total && preview.Items.SequenceEqual(saved.Items), "Preview and saved smart matching diverged.");
                Check(saved.Items.Select(book => book.Title).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)),
                    $"Expanded rule failed: {rule}.");
            }
            Matches(new("Writer", "contains", "ADA"), "Legacy", "Alpha", "Gamma");
            Matches(new("Penciller", "equals", "ink"), "Legacy", "Alpha", "Gamma");
            Matches(new("Genre", "notEquals", "mystery"), "Alpha", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Year", "lessThan", "2000"), "Legacy", "Epsilon");
            Matches(new("Year", "equals", "0"), "Epsilon");
            Matches(new("Year", "greaterThan", "0"), "Legacy", "Alpha");
            Matches(new("PersonalRating", "equals", "0"), "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("PersonalRating", "lessThan", "3"), "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Year", "notEquals", "0"), "Legacy", "Alpha");
            Matches(new("Year", "greaterOrEqual", "2005"), "Alpha");
            Matches(new("Year", "lessOrEqual", "1998"), "Legacy", "Epsilon");
            Matches(new("Year", "isEmpty", ""), "Beta", "Gamma", "Delta", "Zeta", "Eta");
            Matches(new("Year", "isNotEmpty", ""), "Legacy", "Alpha", "Epsilon");
            Matches(new("PersonalRating", "notEquals", "0"), "Legacy", "Alpha", "Beta");
            Matches(new("PersonalRating", "greaterOrEqual", "3"), "Legacy", "Alpha", "Beta");
            Matches(new("PersonalRating", "lessOrEqual", "3"), "Alpha", "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Writer", "notContains", "a"), "Beta", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Writer", "startsWith", "AD"), "Legacy", "Alpha", "Gamma");
            Matches(new("Genre", "endsWith", "TERY"), "Legacy", "Beta", "Gamma");
            Matches(new("Number", "isEmpty", ""), "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Number", "isNotEmpty", ""), "Legacy");
            Matches(new("Location", "endsWith", "/Alpha.cbz"), "Alpha");
            Matches(new("Folder", "equals", "Library 1"), "Legacy", "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            Matches(new("Availability", "equals", "missing"), "Legacy");
            Matches(new("Availability", "notEquals", "available"), "Legacy");
            Matches(new("Availability", "equals", "unavailable"));
            foreach (var operation in new[] { "contains", "startsWith", "endsWith", "equals" })
            foreach (var literal in new[] { "%", "_", "' OR 1=1 --" })
                Matches(new("Title", operation, literal));
            Matches(new("Title", "notContains", "%"), "Legacy", "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
            foreach (var operation in new[] { "startsWith", "endsWith", "contains" })
                Matches(new("Title", operation, ""), "Legacy", "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
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
                new("Year", "contains", "2000"), new("PersonalRating", "equals", "6"), new("PersonalRating", "isEmpty", ""),
                new("Writer", "greaterThan", "a"), new("Penciller", "equals", null!), new("Year", "isEmpty", "0"),
                new("Title", "isNotEmpty", "x"), new("Availability", "contains", "available"), new("Availability", "equals", "other"),
                new("PageCount", "isEmpty", "") })
            {
                Reject<ArgumentException>(() => store.CreateList(owner.Id, smartEdit with { Rules = [rule] }));
                Reject<ArgumentException>(() => store.PreviewSmartList(owner.Id, new("all", [rule])));
            }

            store.SaveProgress(owner.Id, ids["Alpha"], new(0, false));
            var privatePreview = new SmartListPreview("all", [new("PersonalRating", "greaterOrEqual", "3"), new("ReadStatus", "equals", "in-progress")]);
            Check(store.PreviewSmartList(owner.Id, privatePreview).Items.Single().Id == ids["Alpha"]
                && store.PreviewSmartList(admin.Id, privatePreview).Total == 0, "Preview leaked private ratings or progress.");
            Check(store.PreviewSmartList(admin.Id, new("all", [new("PersonalRating", "greaterThan", "2")])).Total == 0
                && store.PreviewSmartList(admin.Id, new("all", [new("ReadStatus", "equals", "completed")])).Total == 0
                && store.PreviewSmartList(owner.Id, new("all", [new("ReadStatus", "equals", "completed")])).Items.Single().Id == ids["Legacy"],
                "Preview rule joins were not independently scoped to the requesting account.");
            var adminBook = store.PreviewSmartList(admin.Id, new("all", [new("Title", "equals", "Legacy")])).Items.Single();
            Check(adminBook.PersonalRating == 2 && !adminBook.Started && !adminBook.Completed,
                "Preview book results exposed another account's private state.");
            var previewEdit = new SmartListPreview("any", [new("PersonalRating", "equals", "5"), new("Writer", "equals", "Ada")]);
            store.UpdateList(owner.Id, smart.Id, smartEdit with { Match = previewEdit.Match, Rules = previewEdit.Rules });
            var previewPages = Enumerable.Range(1, 2).Select(page => store.PreviewSmartList(owner.Id, previewEdit, page, 2)).ToArray();
            var savedPreview = store.ListBooks(owner.Id, null, listId: smart.Id);
            Check(previewPages.All(page => page.Total == 4 && page.Items.Count == 2)
                && previewPages.SelectMany(page => page.Items).SequenceEqual(savedPreview.Items)
                && store.PreviewSmartList(owner.Id, previewEdit, int.MaxValue, 2).Page == 2,
                "Preview pagination differed from saved matching or lost overlapping any-rule matches.");
            Check(store.PreviewSmartList(owner.Id, new("all", [new("Title", "equals", "absent")]), int.MaxValue).Page == 1,
                "Empty preview did not normalize its page.");
            foreach (var json in new[] { "{}", "{\"match\":\"all\"}", "{\"rules\":[]}", "{\"match\":\"all\",\"rules\":[],\"userId\":\"other\"}",
                "{\"match\":\"all\",\"rules\":[{\"field\":\"Title\",\"operator\":\"isEmpty\"}]}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<SmartListPreview>(json, jsonOptions));
            foreach (var edit in new SmartListPreview[] { null!, new("wrong", [new("Title", "equals", "a")]), new("all", null!), new("all", []),
                new("all", Enumerable.Repeat(new ListRule("Title", "equals", "a"), 21).ToArray()) })
                Reject<ArgumentException>(() => store.PreviewSmartList(owner.Id, edit));
            foreach (var (page, size) in new[] { (0, 1), (1, 0), (1, 101) })
                Reject<ArgumentException>(() => store.PreviewSmartList(owner.Id, previewEdit, page, size));

            // Exercise both group operators at each level against independent predicate sets.
            var ruleSets = new[]
            {
                new HashSet<string>(["Legacy", "Alpha", "Gamma"]),
                new HashSet<string>(["Legacy", "Beta", "Gamma"]),
                new HashSet<string>(["Legacy", "Alpha", "Beta"]),
                new HashSet<string>(["Legacy", "Alpha", "Epsilon"])
            };
            static bool Combine(string mode, bool left, bool right) => mode == "all" ? left && right : left || right;
            foreach (var outer in new[] { "all", "any" })
            foreach (var left in new[] { "all", "any" })
            foreach (var right in new[] { "all", "any" })
            {
                var tree = new SmartListPreview(outer,
                [
                    new(Match: left, Rules: [new("Writer", "equals", "Ada"), new("Genre", "equals", "Mystery")]),
                    new(Match: right, Rules: [new("PersonalRating", "greaterOrEqual", "3"), new("Year", "isNotEmpty", "")])
                ]);
                var expected = ids.Where(pair => Combine(outer,
                    Combine(left, ruleSets[0].Contains(pair.Key), ruleSets[1].Contains(pair.Key)),
                    Combine(right, ruleSets[2].Contains(pair.Key), ruleSets[3].Contains(pair.Key))))
                    .Select(pair => pair.Value).Order().ToArray();
                var preview = store.PreviewSmartList(owner.Id, tree);
                store.UpdateList(owner.Id, smart.Id, smartEdit with { Match = tree.Match, Rules = tree.Rules });
                var reopened = new LibraryStore(database, [root]);
                var savedTree = reopened.GetList(owner.Id, smart.Id);
                var saved = reopened.ListBooks(owner.Id, null, listId: smart.Id);
                Check(JsonSerializer.Serialize(savedTree.Rules) == JsonSerializer.Serialize(tree.Rules)
                    && preview.Total == expected.Length && saved.Total == expected.Length
                    && preview.Items.SequenceEqual(saved.Items)
                    && saved.Items.Select(book => book.Id).Order().SequenceEqual(expected)
                    && reopened.ListGroups(owner.Id, null, "series", listId: smart.Id).Items.Sum(item => item.Count) == expected.Length,
                    "Nested AND/OR truth table, persistence, counts, or preview/save parity failed.");
            }
            var nestedPrivate = new SmartListPreview("all",
                [new(Match: "any", Rules: [new("PersonalRating", "equals", "5"), new("ReadStatus", "equals", "in-progress")])]);
            Check(store.PreviewSmartList(owner.Id, nestedPrivate).Total == 3
                && store.PreviewSmartList(admin.Id, nestedPrivate).Total == 0, "Nested rules leaked private state.");
            Reject<KeyNotFoundException>(() => store.GetList(admin.Id, smart.Id));
            Reject<KeyNotFoundException>(() => store.ListBooks(admin.Id, null, listId: smart.Id));

            var extraText = new[] { "Colorist", "Inker", "Letterer", "CoverArtist", "Editor", "LanguageISO", "Format", "AgeRating" };
            var extraNumbers = new[] { "Volume", "Count", "Month", "Day" };
            const string literalMetadata = "%_\\' OR 1=1 --";
            store.EditMetadata(ids["Alpha"], new(extraText.ToDictionary(name => name, _ => (string?)literalMetadata)));
            store.EditMetadata(ids["Alpha"], new(extraNumbers.ToDictionary(name => name, _ => (string?)"2")));
            foreach (var name in extraText)
            {
                Check(LibraryStore.ListRuleFields.Single(field => field.Name == name).Type == "text", "Missing text rule schema.");
                foreach (var operation in new[] { "equals", "contains", "startsWith", "endsWith" })
                    Matches(new(name, operation, literalMetadata), "Alpha");
                Matches(new(name, "isNotEmpty", ""), "Alpha");
            }
            foreach (var name in extraNumbers)
            {
                Check(LibraryStore.ListRuleFields.Single(field => field.Name == name).Type == "integer", "Missing numeric rule schema.");
                Matches(new(name, "equals", "2"), "Alpha");
                Matches(new(name, "notEquals", "3"), "Alpha");
                Matches(new(name, "isNotEmpty", ""), "Alpha");
                store.EditMetadata(ids["Beta"], new(new() { [name] = "-1" }));
                Matches(new(name, "isEmpty", ""), "Legacy", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta");
                Reject<ArgumentException>(() => store.PreviewSmartList(owner.Id, new("all", [new(name, "equals", "-1")])));
            }
            foreach (var (name, value) in new[] { ("Month", "0"), ("Month", "13"), ("Day", "32"), ("Count", "1.5"), ("Volume", "2147483648") })
            {
                var xml = XDocument.Parse((string)Scalar("SELECT info_xml FROM books WHERE id=$id", ("$id", ids["Beta"]))!);
                xml.Root!.SetElementValue(name, value);
                Execute("UPDATE books SET info_xml=$xml WHERE id=$id", ("$xml", xml.ToString()), ("$id", ids["Beta"]));
                Matches(new(name, "isNotEmpty", ""), "Alpha");
                Reject<ArgumentException>(() => store.PreviewSmartList(owner.Id, new("all", [new(name, "equals", value)])));
            }
            var metadataTree = new SmartListPreview("all", [new(Match: "any",
                Rules: [new("Colorist", "equals", literalMetadata), new("Count", "greaterThan", "2")])]);
            store.UpdateList(owner.Id, smart.Id, smartEdit with { Rules = metadataTree.Rules });
            Check(store.ListBooks(owner.Id, null, listId: smart.Id).Items.Single().Id == ids["Alpha"], "Nested extra fields did not match.");
            store.EditMetadata(ids["Alpha"], new(new() { ["Colorist"] = "Changed", ["Count"] = "1" }));
            Check(new LibraryStore(database, [root]).ListBooks(owner.Id, null, listId: smart.Id).Total == 0
                && store.PreviewSmartList(owner.Id, metadataTree).Total == 0, "Nested rules did not reflect catalog metadata edits.");

            ListRule Nested(int groups)
            {
                var rule = new ListRule("Title", "equals", "Alpha");
                for (var level = 0; level < groups; level++) rule = new(Match: "all", Rules: [rule]);
                return rule;
            }
            Check(store.PreviewSmartList(owner.Id, new("all", [Nested(3)])).Total == 1, "Four group levels should be accepted.");
            Check(store.PreviewSmartList(owner.Id, new("all", Enumerable.Range(0, 19).Select(_ => Nested(1)).ToArray())).Total == 1,
                "Twenty total groups should be accepted.");
            Check(store.PreviewSmartList(owner.Id, new("all", Enumerable.Repeat(Nested(0), 20).ToArray())).Total == 1,
                "Twenty total leaves should be accepted.");
            var cyclicRules = new ListRule[1];
            cyclicRules[0] = new(Match: "all", Rules: cyclicRules);
            foreach (var invalid in new ListRule[][]
            {
                [Nested(4)], Enumerable.Range(0, 20).Select(_ => Nested(1)).ToArray(),
                [new(Match: "all", Rules: Enumerable.Repeat(Nested(0), 11).ToArray()),
                    new(Match: "any", Rules: Enumerable.Repeat(Nested(0), 10).ToArray())],
                [new(Match: "all", Rules: [])], [new(Match: "wrong", Rules: [Nested(0)])],
                [new(Match: "all")], [new(Rules: [Nested(0)])], [new("Title", "equals", "Alpha", "all", [Nested(0)])],
                [new()], [null!], cyclicRules
            })
            {
                Reject<ArgumentException>(() => store.PreviewSmartList(owner.Id, new("all", invalid)));
                Reject<ArgumentException>(() => store.CreateList(owner.Id, smartEdit with { Rules = invalid }));
                Reject<ArgumentException>(() => store.UpdateList(owner.Id, smart.Id, smartEdit with { Rules = invalid }));
            }
            foreach (var json in new[]
            {
                "{\"match\":\"all\",\"rules\":[],\"field\":null}",
                "{\"match\":\"all\",\"rules\":[],\"value\":\"\"}",
                "{\"field\":\"Title\",\"operator\":\"equals\",\"value\":\"Alpha\",\"rules\":null}",
                "{\"match\":\"all\",\"rules\":null}", "{\"match\":\"all\",\"rules\":[],\"extra\":1}",
                "{\"field\":\"Title\",\"field\":\"Series\",\"operator\":\"equals\",\"value\":\"Alpha\"}",
                string.Concat(Enumerable.Repeat("{\"match\":\"all\",\"rules\":[", 40))
                    + "{\"field\":\"Title\",\"operator\":\"equals\",\"value\":\"Alpha\"}" + string.Concat(Enumerable.Repeat("]}", 40))
            })
                Reject<JsonException>(() => JsonSerializer.Deserialize<ListRule>(json, jsonOptions));
            var webTree = JsonSerializer.Serialize(metadataTree, jsonOptions);
            Check(!webTree.Contains("\"field\":null") && !webTree.Contains("\"rules\":null")
                && JsonSerializer.Serialize(JsonSerializer.Deserialize<SmartListPreview>(webTree, jsonOptions), jsonOptions) == webTree,
                "Nested rule JSON did not preserve the exclusive group/leaf contract.");
            store.UpdateList(owner.Id, smart.Id, smartEdit with { Match = previewEdit.Match, Rules = previewEdit.Rules });
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
