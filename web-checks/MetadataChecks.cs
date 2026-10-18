using System.IO.Compression;
using System.Xml.Linq;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;

public static class MetadataChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            void CreateArchive(string name)
            {
                using var archive = ZipFile.Open(Path.Combine(directory, name + ".cbz"), ZipArchiveMode.Create);
                using (var stream = archive.CreateEntry("1.png").Open()) stream.Write(png);
                using (var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open()))
                    writer.Write("<ComicInfo marker='root'><Title source='kept'>" + name + "</Title><Series>Original</Series>"
                        + "<Unknown marker='kept'><Nested>value</Nested></Unknown><Pages><Page Image='0' Bookmark='kept'/></Pages></ComicInfo>");
                using (var writer = new StreamWriter(archive.CreateEntry("ComicBook.xml").Open()))
                    writer.Write("<ComicBook marker='book'><CurrentPage>9</CurrentPage><Other a='b'>keep</Other>"
                        + "<CustomValuesStore>,Original=retained,Escaped=a&amp;#44;b&amp;#61;c</CustomValuesStore></ComicBook>");
            }
            CreateArchive("First");
            CreateArchive("Second");
            var store = new LibraryStore(Path.Combine(directory, "metadata.sqlite"), [directory]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 2, "Metadata fixtures failed to import.");
            var books = store.ListBooks("metadata-check", null, sort: "title").Items;
            var first = books[0].Id;
            var second = books[1].Id;
            Check(store.GetMetadata(first).Custom["Escaped"] == "a,b=c", "Upstream custom encoding was not decoded.");
            var fields = new Dictionary<string, string?>
            {
                ["Title"] = "Edited", ["Publisher"] = "Changed", ["Number"] = "2.5", ["Summary"] = "Summary",
                ["Year"] = "2024", ["Month"] = "2", ["Day"] = "29", ["Count"] = "-1", ["Volume"] = "3",
                ["CommunityRating"] = "4.5", ["Manga"] = "YesAndRightToLeft", ["BlackAndWhite"] = "Unknown",
                ["PageCount"] = "99", ["PreferredFrontCover"] = "0", ["Tags"] = "tag, other", ["Genre"] = "Adventure"
            };
            foreach (var creator in new[] { "Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor", "Translator" })
                fields[creator] = creator + " & <name>";
            store.EditMetadata(first, new(fields, new() { ["original"] = "updated", ["New,=Name"] = "value,=<&>", ["Blank"] = "" }));
            var metadata = store.GetMetadata(first);
            Check(fields.All(pair => metadata.Fields[pair.Key] == pair.Value) && metadata.Fields["Series"] == "Original",
                "Full scalar edits or sparse omission failed.");
            Check(metadata.Custom["Original"] == "updated" && metadata.Custom["New,=Name"] == "value,=<&>"
                && metadata.Custom["Blank"] == "" && metadata.Custom["Escaped"] == "a,b=c", "Custom patch lost values.");
            var book = store.GetBook("metadata-check", first);
            Check(book.Title == "Edited" && book.Series == "Original" && book.Number == "2.5" && book.Publisher == "Changed"
                && book.Summary == "Summary" && book.PageCount == 1 && !book.Started, "Indexed columns or reader ownership changed.");
            var info = XDocument.Parse((string)book.Metadata!["comicInfoXml"]!);
            var legacy = XDocument.Parse((string)book.Metadata["comicBookXml"]!);
            Check(info.Root!.Attribute("marker")?.Value == "root" && info.Root.Element("Title")!.Attribute("source")?.Value == "kept"
                && info.Root.Element("Unknown")!.Element("Nested")!.Value == "value"
                && info.Root.Element("Pages")!.Element("Page")!.Attribute("Bookmark")?.Value == "kept"
                && legacy.Root!.Element("CurrentPage")!.Value == "9" && legacy.Root.Element("Other")!.Attribute("a")?.Value == "b",
                "Unknown XML, attributes, page structures, or ComicBook values were lost.");
            var before = (string)book.Metadata["comicInfoXml"]!;
            Reject<KeyNotFoundException>(() => store.EditMetadataBulk(new([first, "missing"], new() { ["Title"] = "Wrong" })));
            Reject<ArgumentException>(() => store.EditMetadataBulk(new([first, second], new() { ["Month"] = "13" })));
            // A valid patch can be invalid only after merging with the second book; neither book may change.
            store.EditMetadata(second, new(new() { ["Year"] = "2023", ["Month"] = "2", ["Day"] = "28" }));
            Reject<ArgumentException>(() => store.EditMetadataBulk(new([first, second], new() { ["Day"] = "29" })));
            Check((string)store.GetBook("metadata-check", first).Metadata!["comicInfoXml"]! == before
                && store.GetMetadata(second).Fields["Day"] == "28", "Rejected bulk edit was not atomic.");
            foreach (var invalid in new Dictionary<string, string?>[]
            {
                new Dictionary<string, string?> { ["Pages"] = "raw" }, new() { ["Year"] = "0" },
                new() { ["Volume"] = "1.5" }, new() { ["Manga"] = "yes" }, new() { ["CommunityRating"] = "NaN" },
                new() { ["Title"] = "bad\0value" }, new() { ["Summary"] = new string('x', 16_385) }
            }) Reject<ArgumentException>(() => store.EditMetadata(first, new(invalid)));
            Reject<ArgumentException>(() => store.EditMetadata(first, new(Custom: new() { [" "] = "bad" })));
            Reject<ArgumentException>(() => store.EditMetadata(first, new()));
            Reject<ArgumentException>(() => store.EditMetadata(first, new(Custom: new() { ["Key"] = "&#44;" })));
            Reject<ArgumentException>(() => store.EditMetadataBulk(new(Enumerable.Repeat(first, 101).ToArray())));
            store.EditMetadataBulk(new([first, second, first], new() { ["Publisher"] = "Bulk", ["Notes"] = "" }, new() { ["Original"] = null }));
            Check(store.GetMetadata(first).Fields["Notes"] == "" && !store.GetMetadata(first).Custom.ContainsKey("Original")
                && store.GetMetadata(second).Fields["Publisher"] == "Bulk", "Bulk deduplication, blank, or removal failed.");
            store.EditMetadata(first, new(new() { ["Title"] = null, ["Summary"] = "" }));
            Check(!store.GetMetadata(first).Fields.ContainsKey("Title") && store.GetBook("metadata-check", first).Title == "First"
                && store.GetMetadata(first).Fields["Summary"] == "", "Null reset, title fallback, or explicit blank failed.");
            var archivePath = Path.Combine(directory, "First.cbz");
            using (var archive = ZipFile.OpenRead(archivePath))
            using (var reader = new StreamReader(archive.GetEntry("ComicInfo.xml")!.Open()))
                Check(reader.ReadToEnd().Contains(">First</Title>"), "Metadata edit wrote to an archive.");
            File.SetLastWriteTimeUtc(archivePath, File.GetLastWriteTimeUtc(archivePath).AddSeconds(5));
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Updated == 1
                && store.GetMetadata(first).Custom["New,=Name"] == "value,=<&>"
                && !store.GetMetadata(first).Custom.ContainsKey("Original")
                && store.GetBook("metadata-check", first).Publisher == "Bulk", "Rescan lost web metadata/custom edits.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
