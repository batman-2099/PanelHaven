using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class ComicExportChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        static byte[] Bytes(ZipArchiveEntry entry)
        {
            using var source = entry.Open(); using var output = new MemoryStream(); source.CopyTo(output); return output.ToArray();
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-comic-export-check-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        Directory.CreateDirectory(root);
        try
        {
            var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
            var database = Path.Combine(directory, "library.sqlite");
            var store = new LibraryStore(database, [root]);
            var gif = File.ReadAllBytes(Path.Combine(fixtures, "source", "page2.gif"));
            var png = File.ReadAllBytes(Path.Combine(fixtures, "source", "page10.png"));
            var names = new[] { "rar4.cbr", "rar5.cbr", "solid.cb7", "lzma2.cb7", "plain.cbt",
                "directories-rar4.cbr", "directories-rar5.cbr", "directories.cb7", "directories.cbt", "nonsolid.cb7" };
            foreach (var name in names) File.Copy(Path.Combine(fixtures, name), Path.Combine(root, name));
            foreach (var (fixture, alias) in new[] { ("rar5.cbr", "alias.rar"), ("solid.cb7", "alias.7z"), ("plain.cbt", "alias.tar") })
                File.Copy(Path.Combine(fixtures, fixture), Path.Combine(root, alias));
            const string xml = """
                <ComicInfo xmlns="urn:comic" xmlns:x="urn:extension" x:keep="yes"><Title>Export title</Title><PageCount>88</PageCount><PreferredFrontCover>1</PreferredFrontCover><LastPageRead>1</LastPageRead><x:Extra flag="yes">shared</x:Extra><Pages><Page Image="0" Type="FrontCover" Bookmark="Embedded zero" x:keep="page"/><Page Image="1" Type="Story" Bookmark="Embedded one"/></Pages></ComicInfo>
                """;
            var privateFields = new[] { "CurrentPage", "LastPageRead", "ReadPercentage", "ReadTime", "ReadCount",
                "OpenedTime", "OpenedCount", "LastOpenedTime", "LastReadTime" };
            foreach (var filename in new[] { "source.cbz", "source.zip", "legacy-only.cbz" })
            using (var zip = ZipFile.Open(Path.Combine(root, filename), ZipArchiveMode.Create))
            {
                using (var page = zip.CreateEntry("page10.png").Open()) page.Write(png);
                using (var page = zip.CreateEntry("page2.gif").Open()) page.Write(gif);
                if (filename != "legacy-only.cbz")
                    using (var metadata = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open())) metadata.Write(xml);
                using (var metadata = new StreamWriter(zip.CreateEntry("ComicBook.xml").Open()))
                    metadata.Write("<ComicBook xmlns:x='urn:extension'><Title>Legacy shared</Title><Unknown>keep</Unknown><x:ReadTime>shared extension</x:ReadTime>"
                        + string.Concat(privateFields.Select(field => $"<{field}>1</{field}>")) + "</ComicBook>");
                using var unrelated = new StreamWriter(zip.CreateEntry("notes.txt").Open()); unrelated.Write("not shared metadata");
            }
            var scan = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(scan.ErrorCount == 0 && scan.Added == names.Length + 6, "Export fixtures failed import.");
            var books = store.ListBooks("reader", null).Items;
            var natural = store.ExportComics([books[0].Id]);
            using (natural.Stream)
            using (var zip = new ZipArchive(natural.Stream, ZipArchiveMode.Read))
                Check(Bytes(zip.GetEntry("00001.gif")!).SequenceEqual(gif), "Default natural export order changed.");
            var originalFiles = Directory.GetFiles(root).ToDictionary(path => path,
                path => (Bytes: File.ReadAllBytes(path), Ticks: File.GetLastWriteTimeUtc(path), Mode: OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(path)));
            var ids = new List<string>();
            foreach (var book in books)
            {
                ids.Add(book.Id);
                var order = store.GetPageOrder(book.Id);
                var savedOrder = store.SavePageOrder(book.Id, new(order.Revision, [1, 0]));
                store.SaveProgress("reader", book.Id, new(1, true));
                store.SaveBookmark("reader", book.Id, 1, new("Private bookmark"));
                var catalogXml = (string)store.GetBook("reader", book.Id).Metadata!["comicInfoXml"]!;
                string temporary;
                var exported = store.ExportComics([book.Id]);
                using (exported.Stream)
                {
                    temporary = exported.Stream.Name;
                    Check(exported.ContentType == "application/vnd.comicbook+zip" && exported.FileName.EndsWith(".cbz"), "Wrong single export headers.");
                    if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(temporary) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Export stage is not private.");
                    using var zip = new ZipArchive(exported.Stream, ZipArchiveMode.Read, true);
                    Check(zip.Entries.Count == 3 && zip.GetEntry("ComicBook.xml") is null && zip.GetEntry("notes.txt") is null, "Private or unrelated source entries escaped.");
                    Check(Bytes(zip.GetEntry("00001.png")!).SequenceEqual(png) && Bytes(zip.GetEntry("00002.gif")!).SequenceEqual(gif), "Export order or exact image payload changed.");
                    var metadata = XDocument.Parse(Encoding.UTF8.GetString(Bytes(zip.GetEntry("ComicInfo.xml")!)));
                    var ns = metadata.Root!.Name.Namespace;
                    Check(metadata.Root.Element(ns + "PageCount")?.Value == "2"
                        && privateFields.All(field => metadata.Root.Element(ns + field) is null), "Incorrect exported count or private reading state.");
                    if (book.Title == "Legacy shared")
                        Check(metadata.Root.Element(ns + "Title")?.Value == "Legacy shared" && metadata.Root.Element(ns + "Unknown")?.Value == "keep"
                            && metadata.Root.Element(XName.Get("ReadTime", "urn:extension"))?.Value == "shared extension",
                            "Legacy fallback lost shared metadata or extension namespace fields.");
                    if (book.Title == "Export title")
                    {
                        Check(metadata.Root.Element(ns + "PreferredFrontCover")?.Value == "0", "Preferred front cover was not remapped.");
                        Check(metadata.Root.Attribute(XName.Get("keep", "urn:extension"))?.Value == "yes"
                            && metadata.Root.Element(XName.Get("Extra", "urn:extension"))?.Value == "shared", "Unknown namespaced metadata was lost.");
                        var page = metadata.Root.Element(ns + "Pages")!.Elements(ns + "Page").Single(p => p.Attribute("Image")?.Value == "1");
                        Check(page.Attribute("Bookmark")?.Value == "Embedded zero" && page.Attribute("Type")?.Value == "FrontCover"
                            && page.Attribute(XName.Get("keep", "urn:extension"))?.Value == "page", "Physical page metadata was not preserved/remapped.");
                    }
                }
                Check(!File.Exists(temporary), "Successful export leaked its stage.");
                Check(store.GetBook("reader", book.Id).Completed && store.GetPageOrder(book.Id).Revision == savedOrder.Revision
                    && store.GetPageOrder(book.Id).Pages.SequenceEqual(new[] { 1, 0 })
                    && (string)store.GetBook("reader", book.Id).Metadata!["comicInfoXml"]! == catalogXml,
                    "Export mutated private progress, catalog metadata, or shared order.");
            }
            var editedId = books.Single(book => book.Title == "Legacy shared").Id;
            store.EditMetadata(editedId, new(new() { ["Title"] = "Edited / title" },
                new() { ["Edition,=Note"] = "Value,= shared", ["ReadTime"] = "Shared custom field" }));
            var catalogBookXml = (string)store.GetBook("reader", editedId).Metadata!["comicBookXml"]!;
            var roundtripRoot = Path.Combine(directory, "roundtrip");
            Directory.CreateDirectory(roundtripRoot);
            var edited = store.ExportComics([editedId]);
            using (edited.Stream)
            {
                using (var copy = File.Create(Path.Combine(roundtripRoot, "custom.cbz"))) edited.Stream.CopyTo(copy);
                edited.Stream.Position = 0;
                using var zip = new ZipArchive(edited.Stream, ZipArchiveMode.Read);
                Check(edited.FileName == "Edited _ title.cbz", "Export filename was not safely derived from the catalog title.");
                Check(Encoding.UTF8.GetString(Bytes(zip.GetEntry("ComicInfo.xml")!)).Contains("Edited / title"), "Export ignored shared catalog edits.");
                var legacy = XDocument.Parse(Encoding.UTF8.GetString(Bytes(zip.GetEntry("ComicBook.xml")!))).Root!;
                Check(legacy.Name == "ComicBook" && !legacy.HasAttributes && legacy.Elements().Count() == 1
                    && legacy.Elements().Single().Name == "CustomValuesStore" && legacy.Descendants().Count() == 1,
                    "Custom-value export copied private or unrelated ComicBook fields.");
            }
            Check((string)store.GetBook("reader", editedId).Metadata!["comicBookXml"]! == catalogBookXml,
                "Custom-value export mutated catalog legacy metadata.");
            var roundtrip = new LibraryStore(Path.Combine(directory, "roundtrip.sqlite"), [roundtripRoot]);
            Check(roundtrip.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Custom-value CBZ could not be imported.");
            var restored = roundtrip.GetMetadata(roundtrip.ListBooks("reader", null).Items.Single().Id);
            Check(restored.Fields["Title"] == "Edited / title" && restored.Custom.Count == 2
                && restored.Custom["Edition,=Note"] == "Value,= shared" && restored.Custom["ReadTime"] == "Shared custom field",
                "Export lost or changed shared custom values on import.");
            var bundleExport = store.ExportComics(ids.ToArray());
            using (bundleExport.Stream)
            using (var bundle = new ZipArchive(bundleExport.Stream, ZipArchiveMode.Read))
            {
                Check(bundleExport.ContentType == "application/zip" && bundle.Entries.Count == ids.Count
                    && bundle.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Count, "Bundle names or book count are wrong.");
                foreach (var entry in bundle.Entries)
                {
                    Check(!entry.FullName.Contains('/') && !entry.FullName.Contains('\\') && entry.FullName.EndsWith(".cbz"), "Unsafe bundle name.");
                    using var source = entry.Open(); using var nested = new ZipArchive(source, ZipArchiveMode.Read);
                    var hasCustom = entry.FullName.EndsWith("Edited _ title.cbz", StringComparison.Ordinal);
                    Check(Bytes(nested.GetEntry("00001.png")!).SequenceEqual(png) && nested.Entries.Count == (hasCustom ? 4 : 3)
                        && (nested.GetEntry("ComicBook.xml") is not null) == hasCustom, "Nested CBZ is not readable or lost shared custom values.");
                }
            }
            foreach (var (path, original) in originalFiles)
                Check(File.ReadAllBytes(path).SequenceEqual(original.Bytes) && File.GetLastWriteTimeUtc(path) == original.Ticks
                    && (OperatingSystem.IsWindows() || File.GetUnixFileMode(path) == original.Mode), "Export modified a source archive or its permissions.");
            Reject<ArgumentException>(() => store.ExportComics([]));
            Reject<ArgumentException>(() => store.ExportComics([ids[0], ids[0]]));
            Reject<ArgumentException>(() => store.ExportComics(Enumerable.Range(0, 101).Select(index => index.ToString()).ToArray()));
            Reject<KeyNotFoundException>(() => store.ExportComics([ids[0], "missing"]));
            using var db = new SqliteConnection($"Data Source={database}"); db.Open();
            void Update(string sql, string value)
            {
                using var command = db.CreateCommand(); command.CommandText = sql;
                command.Parameters.AddWithValue("$id", ids[0]); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
            }
            foreach (var invalid in new[] { "[0,0]", "[0]", "[0,2]", "null", "[0,\"1\"]" })
            {
                Update("UPDATE page_orders SET pages_json=$value WHERE book_id=$id", invalid);
                try { store.ExportComics([ids[0]]); throw new Exception("Invalid saved order was exported."); }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { }
            }
            Update("UPDATE page_orders SET pages_json=$value WHERE book_id=$id", "[1,0]");
            Update("UPDATE page_orders SET source_hash=$value WHERE book_id=$id", "stale");
            Reject<InvalidOperationException>(() => store.ExportComics([ids[0]]));
            Update("UPDATE page_orders SET source_hash=(SELECT content_hash FROM books WHERE id=$id) WHERE book_id=$id AND $value=''", "");
            foreach (var invalid in new[] {
                "<ComicInfo><Pages><Page Image='0'/><Page Image='0'/></Pages></ComicInfo>",
                "<ComicInfo><Pages><Page/></Pages></ComicInfo>",
                "<ComicInfo><Pages/><Pages/></ComicInfo>",
                "<ComicInfo><PreferredFrontCover>2</PreferredFrontCover></ComicInfo>",
                "<ComicInfo><PageCount/><PageCount/></ComicInfo>" })
            {
                Update("UPDATE books SET info_xml=$value WHERE id=$id", invalid);
                Reject<ArgumentException>(() => store.ExportComics([ids[0]]));
            }
            Update("UPDATE books SET info_xml=$value WHERE id=$id", "<!DOCTYPE ComicInfo [<!ENTITY private 'unsafe'>]><ComicInfo>&private;</ComicInfo>");
            Reject<System.Xml.XmlException>(() => store.ExportComics([ids[0]]));
            Update("UPDATE books SET info_xml=$value WHERE id=$id", "<ComicInfo/>");
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT path FROM books WHERE id=$id"; command.Parameters.AddWithValue("$id", ids[0]);
                var path = (string)command.ExecuteScalar()!;
                var original = File.ReadAllBytes(path); var ticks = File.GetLastWriteTimeUtc(path);
                var changed = original.ToArray(); changed[30] ^= 1; File.WriteAllBytes(path, changed); File.SetLastWriteTimeUtc(path, ticks);
                Reject<InvalidOperationException>(() => store.ExportComics([ids[0]]));
                using (var available = db.CreateCommand())
                {
                    available.CommandText = "SELECT availability FROM books WHERE id=$id"; available.Parameters.AddWithValue("$id", ids[0]);
                    Check((string)available.ExecuteScalar()! == "available", "Failed export mutated catalog availability.");
                }
                File.WriteAllBytes(path, original); File.SetLastWriteTimeUtc(path, ticks);
                File.Move(path, path + ".saved");
                Reject<FileNotFoundException>(() => store.ExportComics([ids[0]]));
                if (!OperatingSystem.IsWindows())
                {
                    File.CreateSymbolicLink(path, path + ".saved");
                    Reject<UnauthorizedAccessException>(() => store.ExportComics([ids[0]]));
                    File.Delete(path);
                }
                File.Move(path + ".saved", path);
                Update("UPDATE books SET content_hash=$value WHERE id=$id", "stale");
                Reject<InvalidOperationException>(() => store.ExportComics([ids[0]]));
            }
            using (var cancelled = new CancellationTokenSource())
            {
                // Exercise cancellation after a private stage exists, without timing-dependent threads or a 1 GiB fixture.
                var create = typeof(LibraryStore).GetMethod("NewComicExport", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var stage = (FileStream)create.Invoke(store, [cancelled.Token])!;
                var temporary = stage.Name;
                using (stage)
                {
                    stage.Position = 1024L * 1024 * 1024;
                    Reject<ComicExportLimitException>(() => stage.WriteByte(1));
                    Reject<ComicExportLimitException>(() => stage.Write(new byte[1], 0, 1));
                    Reject<ComicExportLimitException>(() => stage.Write(new byte[1].AsSpan()));
                    Check(stage.Length == 0, "Export limit was enforced after writing excess bytes.");
                    stage.Position = 0; cancelled.Cancel();
                    Reject<OperationCanceledException>(() => stage.WriteByte(1));
                }
                Check(!File.Exists(temporary), "Cancelled stage survived disposal.");
                Reject<OperationCanceledException>(() => store.ExportComics([ids[0]], cancelled.Token));
            }
            Check(!Directory.EnumerateFiles(directory, ".panelhaven-comic-export-*.tmp").Any(), "Failed or cancelled export leaked a stage.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
