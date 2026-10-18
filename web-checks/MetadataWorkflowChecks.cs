using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;

public static class MetadataWorkflowChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-metadata-workflows-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        Directory.CreateDirectory(root);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void Archive(string title, string values)
            {
                using var archive = ZipFile.Open(Path.Combine(root, title + ".cbz"), ZipArchiveMode.Create);
                using (var image = archive.CreateEntry("1.png").Open()) image.Write(png);
                using (var xml = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
                    xml.Write("<ComicInfo marker='keep'><Title>" + title + "</Title>" + values
                        + "<Unknown flag='kept'><Nested>unknown</Nested></Unknown><Pages><Page Image='0' Bookmark='kept'/></Pages></ComicInfo>");
                using (var xml = new StreamWriter(archive.CreateEntry("ComicBook.xml").Open()))
                    xml.Write("<ComicBook marker='legacy'><Other>preserved</Other><CustomValuesStore>Original=same,Blank=</CustomValuesStore></ComicBook>");
            }
            Archive("Alpha", "<Series> </Series><Number>1A</Number><Year>2023</Year><Month>2</Month><Day>29</Day><Publisher>ACME</Publisher><Writer>Ada  Lovelace</Writer><Notes></Notes>");
            Archive("Beta", "<Series>Series</Series><Number></Number><Year>-1</Year><Month>2</Month><Day>29</Day><Publisher> acme </Publisher><Writer>ada lovelace</Writer>");
            Archive("Gamma", "<Series>Series</Series><Number>1/2</Number><Year>2024</Year><Month>2</Month><Day>29</Day><Publisher>Acne</Publisher><Writer>Ada Lovelace Jr</Writer><Penciller>ADA LOVELACE</Penciller><Inker>Pat, Sam</Inker>");
            Archive("Delta", "<Number>0</Number><Year>bad</Year><Month>13</Month><Day>0</Day><Inker>Sam,Pat</Inker>");
            Archive("Epsilon", "<Series>Series</Series><Number>2.5</Number><Year>10000</Year><Month>4</Month><Day>31</Day>");
            Archive("Zeta", "<Series>Series</Series><Number>text</Number><Year> </Year><Day>31</Day>");
            Archive("Eta", "<Series>Series</Series><Number>3</Number><Year>0</Year><Month>-2</Month><Day>32</Day>");
            var originalFiles = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);
            var database = Path.Combine(directory, "metadata.sqlite");
            var accounts = new Accounts(database);
            var alice = accounts.Create("alice", "metadata-workflow-alice", "admin");
            var bob = accounts.Create("bob", "metadata-workflow-bob", "reader");
            var store = new LibraryStore(database, [root]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 7, "Workflow archives failed to import.");
            var ids = store.ListBooks(alice.Id, null).Items.ToDictionary(book => book.Title, book => book.Id);
            string Snapshot()
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT id,title,series,number,publisher,summary,info_xml,book_xml,writer,penciller,genre,year,edited FROM books ORDER BY id";
                using var reader = command.ExecuteReader();
                var rows = new List<object[]>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(values);
                }
                return JsonSerializer.Serialize(rows);
            }
            var original = Snapshot();
            var missing = store.GetMetadataReport("missing");
            Check(missing.Total == 3 && missing.Items.Select(book => book.Title).SequenceEqual(["Alpha", "Beta", "Delta"])
                && missing.Items[0].Issues.Single().Field == "Series" && missing.Items[1].Issues.Single().Field == "Number"
                && missing.Items[2].Issues.Single() is { Field: "Series", Value: null },
                "Missing report must flag only blank/absent Series and textual Number.");
            var dates = store.GetMetadataReport("dates");
            Check(dates.Total == 4 && dates.Items.Single(book => book.Title == "Alpha").Issues.Single().Code == "date-impossible"
                && dates.Items.Single(book => book.Title == "Delta").Issues.Count == 3
                && dates.Items.Single(book => book.Title == "Epsilon").Issues.Count == 2
                && dates.Items.Single(book => book.Title == "Eta").Issues.Count == 3,
                "Date report lost malformed, bounds, leap-year, unknown-year, partial-date or calendar semantics.");
            var names = store.GetMetadataReport("names");
            Check(names.Total == 2 && names.Items.All(book => book.Issues.Count == 2
                && book.Issues.All(issue => issue.Code == "name-variation" && issue.Field is "Publisher" or "Writer")),
                "Name report must use per-field case/whitespace variants, not fuzzy or cross-field matching.");
            var all = store.GetMetadataReport();
            Check(all.Total == 5 && store.GetMetadataReport("all", 2, 2).Items.Select(book => book.Id)
                .SequenceEqual(all.Items.Skip(2).Take(2).Select(book => book.Id))
                && store.GetMetadataReport("all", int.MaxValue, 100).Items.Count == 0,
                "Metadata report pagination must count affected books with stable ordering.");
            Reject<ArgumentException>(() => store.GetMetadataReport("unknown"));
            Reject<ArgumentException>(() => store.GetMetadataReport(page: 0));
            Reject<ArgumentException>(() => store.GetMetadataReport(pageSize: 101));
            Check(Snapshot() == original, "Report changed catalog metadata or edit markers.");

            var alpha = ids["Alpha"];
            var beta = ids["Beta"];
            var gamma = ids["Gamma"];
            var patch = new BulkMetadataEdit([beta, alpha, alpha], new()
            {
                ["Publisher"] = "ACME", ["Series"] = "Series", ["Notes"] = "", ["Summary"] = null
            }, new() { ["original"] = "same", ["blank"] = null, ["New"] = "" });
            var preview = store.PreviewMetadata(patch);
            var alphaChanges = preview.Books.Single(book => book.Id == alpha).Changes;
            var betaChanges = preview.Books.Single(book => book.Id == beta).Changes;
            Check(preview.Books.Count == 2 && alphaChanges.Count == 3 && betaChanges.Count == 4
                && alphaChanges.Any(change => change is { Field: "blank", Custom: true, Before: "", After: null })
                && alphaChanges.Any(change => change is { Field: "New", Custom: true, Before: null, After: "" })
                && betaChanges.Any(change => change is { Field: "Notes", Custom: false, Before: null, After: "" })
                && preview.Books.All(book => book.Changes.All(change => change.Field is not ("original" or "Summary"))),
                "Preview must show only actual scalar/custom deltas and distinguish empty from absent/removal.");
            Check(store.PreviewMetadata(patch with { BookIds = [alpha, beta] }).Revision == preview.Revision
                && Snapshot() == original, "Preview changed metadata or depended on selection ordering/duplicates.");
            Reject<MetadataRevisionException>(() => store.EditMetadataBulk(patch, requirePreview: true));
            Reject<MetadataRevisionException>(() => store.EditMetadataBulk(patch with { ExpectedRevision = "wrong" }, requirePreview: true));
            Reject<MetadataRevisionException>(() => store.EditMetadataBulk(patch with
            {
                Fields = new() { ["Publisher"] = "different" }, ExpectedRevision = preview.Revision
            }, requirePreview: true));
            Reject<MetadataRevisionException>(() => store.EditMetadataBulk(patch with
            {
                BookIds = [alpha], ExpectedRevision = preview.Revision
            }, requirePreview: true));
            Reject<KeyNotFoundException>(() => store.PreviewMetadata(patch with { BookIds = [alpha, "missing"] }));
            Reject<KeyNotFoundException>(() => store.EditMetadataBulk(patch with
            {
                BookIds = [alpha, "missing"], ExpectedRevision = preview.Revision
            }, requirePreview: true));
            Reject<ArgumentException>(() => store.PreviewMetadata(patch with { Custom = new() { ["Key"] = "a", ["key"] = "b" } }));
            Check(Snapshot() == original, "Rejected previews/apply partially mutated metadata.");
            store.SaveProgress(alice.Id, alpha, new(0, true));
            store.SaveRating(alice.Id, alpha, new(5));
            Check(store.PreviewMetadata(patch).Revision == preview.Revision, "Private progress/rating invalidated shared metadata preview.");
            store.EditMetadataBulk(patch with { ExpectedRevision = preview.Revision }, requirePreview: true);
            foreach (var book in preview.Books)
            {
                var metadata = store.GetMetadata(book.Id);
                foreach (var change in book.Changes)
                {
                    var values = change.Custom ? metadata.Custom : metadata.Fields;
                    Check(change.After is null ? !values.ContainsKey(change.Field) : values.GetValueOrDefault(change.Field) == change.After,
                        "Applied field did not match its exact preview.");
                }
                Check(metadata.Custom.Keys.Any(key => key == "Original") && !metadata.Custom.Keys.Any(key => key == "original"),
                    "A case-insensitive custom update silently renamed a stored key.");
            }
            Reject<MetadataRevisionException>(() => store.EditMetadataBulk(patch with { ExpectedRevision = preview.Revision }, requirePreview: true));
            var unchanged = new BulkMetadataEdit([gamma], new() { ["Series"] = "Series", ["Notes"] = null }, new() { ["original"] = "same" });
            var noOp = store.PreviewMetadata(unchanged);
            var beforeNoOp = Snapshot();
            Check(noOp.Books.Single().Changes.Count == 0, "Unchanged book was omitted or reported as changed.");
            store.EditMetadataBulk(unchanged with { ExpectedRevision = noOp.Revision }, requirePreview: true);
            Check(Snapshot() == beforeNoOp, "No-op apply marked imported metadata edited or rewrote XML.");
            store.EditMetadata(alpha, new(new() { ["Year"] = "2024" }));
            var dayPatch = new BulkMetadataEdit([alpha], new() { ["Day"] = "29" });
            var datePreview = store.PreviewMetadata(dayPatch);
            store.EditMetadata(alpha, new(new() { ["Year"] = "2023", ["Day"] = "28" }));
            Reject<MetadataRevisionException>(() => store.EditMetadataBulk(dayPatch with { ExpectedRevision = datePreview.Revision }, requirePreview: true));
            var beforeInvalid = Snapshot();
            Reject<ArgumentException>(() => store.PreviewMetadata(new([gamma, alpha], new() { ["Day"] = "29" })));
            Reject<KeyNotFoundException>(() => store.EditMetadataBulk(new([gamma, "missing"], new() { ["Title"] = "wrong" })));
            Check(Snapshot() == beforeInvalid, "Merged-date or unknown-selection failure partially changed a book.");

            var race = new BulkMetadataEdit([gamma], new() { ["Publisher"] = "Concurrent" });
            var racePreview = store.PreviewMetadata(race);
            var applied = 0;
            var stale = 0;
            Task.WaitAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try
                {
                    store.EditMetadataBulk(race with { ExpectedRevision = racePreview.Revision }, requirePreview: true);
                    Interlocked.Increment(ref applied);
                }
                catch (MetadataRevisionException) { Interlocked.Increment(ref stale); }
            })).ToArray());
            Check(applied == 1 && stale == 1, "Concurrent bulk applies bypassed the revision check.");

            store.EditMetadata(alpha, new(new()
            {
                ["Title"] = " \t=SUM(1,2)", ["Summary"] = "line one, \"quoted\"\nline two", ["Writer"] = "\t+formula",
                ["Publisher"] = "\r\n@formula", ["Number"] = "-formula", ["Notes"] = "ordinary text"
            }, new() { ["original"] = "updated", ["Comma,Equals="] = "quote\" and\nnewline" }));
            var beforeExport = Snapshot();
            var exports = store.GetMetadataExport([beta, alpha, alpha]);
            Check(exports.Count == 2 && exports.Select(book => book.Id).SequenceEqual(exports.Select(book => book.Id).Order(StringComparer.Ordinal)),
                "Export did not bound/deduplicate/order selected records deterministically.");
            var exported = exports.Single(book => book.Id == alpha);
            var detail = store.GetBook(alice.Id, alpha);
            Check(exported.ComicInfoXml == (string)detail.Metadata!["comicInfoXml"]!
                && exported.ComicBookXml == (string)detail.Metadata["comicBookXml"]!
                && XDocument.Parse(exported.ComicInfoXml).Root!.Element("Unknown")!.Attribute("flag")!.Value == "kept"
                && exported.Custom["Original"] == "updated" && exported.Custom.Keys.Any(key => key == "Original"),
                "Export lost unknown raw XML, custom values, or original custom key spelling.");
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var json = JsonSerializer.Serialize(exports, jsonOptions);
            Check(!json.Contains(alice.Id) && !json.Contains(bob.Id) && !json.Contains(directory)
                && !json.Contains("personalRating") && !json.Contains("currentPage"), "Export leaked private account/progress/rating/path data.");
            using var csvStream = new MemoryStream();
            LibraryStore.WriteMetadataCsv(csvStream, exports).GetAwaiter().GetResult();
            var csv = Encoding.UTF8.GetString(csvStream.ToArray());
            var csvRows = ParseCsv(csv);
            var headers = csvRows[0];
            Check(csvRows.Count == 3 && csvRows.All(row => row.Count == headers.Count),
                "CSV records do not preserve the selected books or column boundaries.");
            var csvBook = csvRows.Single(row => row[0] == alpha);
            string Cell(string name) => csvBook[headers.IndexOf(name)];
            Check(Cell("Title") == "' \t=SUM(1,2)" && Cell("Writer") == "'\t+formula"
                && Cell("Publisher") == "'" + exported.Fields["Publisher"] && Cell("Number") == "'-formula"
                && Cell("Summary") == "line one, \"quoted\"\nline two" && Cell("Notes") == "ordinary text"
                && JsonSerializer.Deserialize<Dictionary<string, string>>(Cell("CustomValues"))!["Comma,Equals="] == "quote\" and\nnewline",
                "CSV escaping, custom JSON, or whitespace/control-prefixed formula protection failed.");
            foreach (var invalid in new string[][] { [], [""], Enumerable.Repeat(alpha, 101).ToArray() })
                Reject<ArgumentException>(() => store.GetMetadataExport(invalid));
            Reject<KeyNotFoundException>(() => store.GetMetadataExport([alpha, "missing"]));
            Reject<JsonException>(() => JsonSerializer.Deserialize<MetadataExportSelection>("{\"bookIds\":[\"a\"],\"path\":\"secret\"}", jsonOptions));
            Reject<JsonException>(() => JsonSerializer.Deserialize<MetadataExportSelection>("{}", jsonOptions));
            Reject<JsonException>(() => JsonSerializer.Deserialize<BulkMetadataEdit>("{\"bookIds\":[\"a\"],\"fieldz\":{}}", jsonOptions));
            Reject<JsonException>(() => JsonSerializer.Deserialize<BulkMetadataEdit>("{\"bookIds\":[\"a\"],\"fields\":{\"Year\":2024}}", jsonOptions));
            Check(Snapshot() == beforeExport && store.GetBook(alice.Id, alpha).PersonalRating == 5
                && store.GetBook(bob.Id, alpha).PersonalRating == 0 && !store.GetBook(bob.Id, alpha).Started,
                "Export changed catalog/private data or leaked per-user state.");
            Check(originalFiles.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)), "Metadata workflow wrote to a source archive.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && (c == ',' || c == '\r'))
            {
                row.Add(cell.ToString());
                cell.Clear();
                if (c == '\r') { rows.Add(row); row = []; i++; }
            }
            else cell.Append(c);
        }
        if (quoted || cell.Length != 0 || row.Count != 0) throw new Exception("Malformed CSV output.");
        return rows;
    }
}
