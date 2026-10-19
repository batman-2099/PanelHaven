using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class PageMetadataChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-page-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "pages.cbz");
            var database = Path.Combine(directory, "library.sqlite");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            const string info = """
                <ComicInfo xmlns="urn:comic" xmlns:x="urn:extension" marker="keep"><Title>Pages</Title><Unknown x:flag="yes"><Nested>keep</Nested></Unknown><!--keep--><x:Pages><x:Page Image="0"/></x:Pages><Pages marker="pages"><Page Image="0" Type="FrontCover" Bookmark="Original" ImageWidth="100" x:flag="keep"><x:Nested>keep</x:Nested></Page><Page Image="1" Type="Story" Bookmark="Untouched"/></Pages></ComicInfo>
                """;
            const string legacy = "<ComicBook><CurrentPage>9</CurrentPage><CustomValuesStore>Keep=value</CustomValuesStore><Unknown>keep</Unknown></ComicBook>";
            void WriteSource(string xml, int count = 3)
            {
                using var output = File.Create(path);
                using var archive = new ZipArchive(output, ZipArchiveMode.Create);
                for (var page = 0; page < count; page++)
                {
                    using var stream = archive.CreateEntry($"{page}.png").Open();
                    stream.Write(png);
                }
                foreach (var (name, value) in new[] { ("ComicInfo.xml", xml), ("ComicBook.xml", legacy), ("unknown.txt", "keep") })
                {
                    using var stream = archive.CreateEntry(name).Open();
                    stream.Write(Encoding.UTF8.GetBytes(value));
                }
            }
            WriteSource(info);
            var original = File.ReadAllBytes(path);
            var store = new LibraryStore(database, [directory]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Page fixture did not import.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            object? Sql(string sql, object? value = null)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$value", value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            string CatalogXml() => (string)Sql("SELECT info_xml FROM books WHERE id=$id")!;
            void SetXml(string xml) => Sql("UPDATE books SET info_xml=$value WHERE id=$id", xml);
            store.SaveProgress("owner", id, new(1, true));
            store.SaveRating("owner", id, new(4));
            store.SaveBookmark("owner", id, 0, new("Private"));
            var list = store.CreateList("owner", new("Keep", "manual", "all", []));
            store.AddListBooks("owner", list.Id, [id]);
            void PrivateState()
            {
                var book = store.GetBook("owner", id);
                Check(book.Id == id && book.Completed && book.CurrentPage == 1 && book.PersonalRating == 4
                    && store.GetBookmarks("owner", id).Single().Name == "Private"
                    && store.GetList("owner", list.Id).BookIds!.Single() == id
                    && !store.GetBook("other", id).Started && store.GetBookmarks("other", id).Count == 0,
                    "Page changes altered identity or private state.");
            }
            var loaded = store.GetPageMetadata(id);
            Check(loaded.PageCount == 3 && loaded.Pages.Count == 3 && loaded.Pages[2] == new PageMetadataRow(2, null, null)
                && loaded.Types.Contains("Story") && store.GetPageInfo(id)[0].TypeMask == 1,
                "Physical page coverage or namespace-aware reader metadata failed.");
            var untouched = CatalogXml();
            var noOp = store.PreviewPageMetadata(id, new(loaded.Revision, []));
            Check(!noOp.CanApply && noOp.Changes.Count == 0, "Empty page draft was not a no-op.");
            store.EditPageMetadata(id, new(noOp.Revision, []));
            noOp = store.PreviewPageMetadata(id, new(loaded.Revision, [loaded.Pages[0]]));
            store.EditPageMetadata(id, new(noOp.Revision, [loaded.Pages[0]]));
            Check(CatalogXml() == untouched && (long)Sql("SELECT edited FROM books WHERE id=$id")! == 0
                && (long)Sql("SELECT COUNT(*) FROM page_metadata_edits WHERE book_id=$id")! == 0,
                "No-op changed catalog XML, edited flag, or write intent.");
            foreach (var rows in new PageMetadataRow[][]
            {
                [new(-1, null, null)], [new(3, null, null)], [new(0, null, null), new(0, "Story", null)],
                [new(0, "unknown", null)], [new(0, "Story", new string('x', 201))], [new(0, "Story", "bad\n")],
                [new(0, "Story", "\ud800")], [null!]
            }) Reject<ArgumentException>(() => store.PreviewPageMetadata(id, new(loaded.Revision, rows)));
            Reject<ArgumentException>(() => store.PreviewPageMetadata(id, new(loaded.Revision, null!)));
            Reject<KeyNotFoundException>(() => store.GetPageMetadata("missing"));
            var draft = new[] { new PageMetadataRow(0, "BackCover", "Edited"), new PageMetadataRow(2, "Story", "New") };
            var preview = store.PreviewPageMetadata(id, new(loaded.Revision, draft));
            Check(preview.CanApply && preview.Changes.Count == 4 && preview.Changes.Any(change => change.Page == 0
                && change.Field == "Type" && change.Before == "FrontCover" && change.After == "BackCover"), "Preview omitted page changes.");
            Reject<MetadataRevisionException>(() => store.EditPageMetadata(id, new(loaded.Revision, draft)));
            Reject<MetadataRevisionException>(() => store.EditPageMetadata(id, new(preview.Revision, [new(0, "Other", "Edited")])));
            // Every catalog XML document and the physical count participate, not only edited attributes.
            Sql("UPDATE books SET book_xml=$value WHERE id=$id", legacy.Replace("keep", "later"));
            Reject<MetadataRevisionException>(() => store.EditPageMetadata(id, new(preview.Revision, draft)));
            Sql("UPDATE books SET book_xml=$value WHERE id=$id", legacy);
            Sql("UPDATE books SET page_count=4 WHERE id=$id");
            Reject<MetadataRevisionException>(() => store.EditPageMetadata(id, new(preview.Revision, draft)));
            Sql("UPDATE books SET page_count=3 WHERE id=$id");
            store.EditMetadata(id, new(new() { ["Title"] = "Later title" }));
            Reject<MetadataRevisionException>(() => store.EditPageMetadata(id, new(preview.Revision, draft)));
            Reject<MetadataRevisionException>(() => store.PreviewPageMetadata(id, new(loaded.Revision, draft)));
            loaded = store.GetPageMetadata(id);
            preview = store.PreviewPageMetadata(id, new(loaded.Revision, draft));
            var saved = store.EditPageMetadata(id, new(preview.Revision, draft));
            Check(saved.Pages[0] == draft[0] && saved.Pages[2] == draft[1]
                && File.ReadAllBytes(path).SequenceEqual(original), "Page edit mutated archive or failed catalog save.");
            var ns = (XNamespace)"urn:comic";
            var xml = XDocument.Parse(CatalogXml());
            var first = xml.Root!.Element(ns + "Pages")!.Elements(ns + "Page").First();
            Check(first.Attribute("ImageWidth")?.Value == "100" && first.Attribute(XName.Get("flag", "urn:extension"))?.Value == "keep"
                && first.Element(XName.Get("Nested", "urn:extension"))?.Value == "keep"
                && xml.Root.Element(ns + "Unknown")!.Element(ns + "Nested")!.Value == "keep"
                && XNode.DeepEquals(xml.Root.Element(ns + "Pages")!.Elements(ns + "Page").ElementAt(1),
                    XDocument.Parse(info).Root!.Element(ns + "Pages")!.Elements(ns + "Page").ElementAt(1))
                && xml.Root.Nodes().OfType<XComment>().Single().Value == "keep"
                && (string)Sql("SELECT book_xml FROM books WHERE id=$id")! == legacy,
                "Page editing discarded unrelated nodes, attributes, or unselected pages.");
            Check(store.GetPageInfo(id).Single(page => page.Page == 0) == new ReaderPageInfo(0, "BackCover", 256, "Edited"),
                "Reader did not immediately reflect edited page metadata.");
            PrivateState();
            var typeOnly = new[] { new PageMetadataRow(1, "Other", "Untouched") };
            preview = store.PreviewPageMetadata(id, new(store.GetPageMetadata(id).Revision, typeOnly));
            Check(preview.Changes.Count == 1 && preview.Changes[0].Field == "Type", "Unchanged bookmark created edit intent.");
            store.EditPageMetadata(id, new(preview.Revision, typeOnly));
            store = new LibraryStore(database, [directory]);
            var external = info.Replace("ImageWidth=\"100\"", "ImageWidth=\"200\" sourceOnly=\"yes\"")
                .Replace("Bookmark=\"Untouched\"", "Bookmark=\"External\"").Replace(">keep</Nested>", ">external</Nested>");
            WriteSource(external);
            var externalBytes = File.ReadAllBytes(path);
            var write = store.PreviewArchiveWrite(id);
            var writtenXml = XDocument.Parse(write.Entries.Single(entry => entry.Name == "ComicInfo.xml").After!);
            var sourcePages = writtenXml.Root!.Element(ns + "Pages")!.Elements(ns + "Page").ToArray();
            Check(sourcePages[0].Attribute("Type")?.Value == "BackCover" && sourcePages[0].Attribute("Bookmark")?.Value == "Edited"
                && sourcePages[0].Attribute("ImageWidth")?.Value == "200" && sourcePages[0].Attribute("sourceOnly")?.Value == "yes"
                && sourcePages[1].Attribute("Type")?.Value == "Other" && sourcePages[1].Attribute("Bookmark")?.Value == "External"
                && writtenXml.Root.Element(ns + "Unknown")!.Element(ns + "Nested")!.Value == "external"
                && File.ReadAllBytes(path).SequenceEqual(externalBytes), "Page write preview overwrote source-only metadata or mutated source.");
            var applied = store.ApplyArchiveWrite(id, new(write.Revision, true));
            Check(!store.PreviewArchiveWrite(id).CanApply && store.GetPage(id, 2).Bytes.SequenceEqual(png), "Page write was not a stable archive update.");
            var restore = store.PreviewArchiveRestore(id, applied.BackupId!);
            store.RestoreArchiveBackup(id, applied.BackupId!, new(restore.Revision, true));
            Check(File.ReadAllBytes(path).SequenceEqual(externalBytes) && store.GetPageMetadata(id).Pages[0] == draft[0],
                "Undo failed exact source restoration or discarded page catalog edits.");
            PrivateState();
            WriteSource(external, 2);
            Reject<ArchiveWriteConflictException>(() => store.PreviewArchiveWrite(id));
            WriteSource(external.Replace("</Pages>", "<Page Image=\"0\"/></Pages>"));
            Reject<ArgumentException>(() => store.PreviewArchiveWrite(id));
            WriteSource(external);
            loaded = store.GetPageMetadata(id);
            var clear = new[] { new PageMetadataRow(0, null, ""), new PageMetadataRow(2, "", null) };
            preview = store.PreviewPageMetadata(id, new(loaded.Revision, clear));
            store.EditPageMetadata(id, new(preview.Revision, clear));
            Check(store.GetPageInfo(id).Single(page => page.Page == 0) == new ReaderPageInfo(0, "Story", 8, ""),
                "Clearing Type/Bookmark did not reset reader defaults.");
            write = store.PreviewArchiveWrite(id);
            writtenXml = XDocument.Parse(write.Entries.Single(entry => entry.Name == "ComicInfo.xml").After!);
            first = writtenXml.Root!.Element(ns + "Pages")!.Elements(ns + "Page").First();
            Check(first.Attribute("Type") is null && first.Attribute("Bookmark") is null && first.Attribute("sourceOnly")?.Value == "yes",
                "Cleared page attributes were resurrected or unrelated attributes lost.");
            store.ApplyArchiveWrite(id, new(write.Revision, true));
            var fresh = new LibraryStore(Path.Combine(directory, "fresh.sqlite"), [directory]);
            Check(fresh.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Written page archive did not import.");
            Check(fresh.GetPageMetadata(fresh.ListBooks("owner", null).Items.Single().Id).Pages[0] == new PageMetadataRow(0, null, null),
                "Fresh import resurrected cleared attributes.");
            PrivateState();
            untouched = CatalogXml();
            foreach (var ambiguous in new[]
            {
                "<ComicInfo><Pages/><Pages/></ComicInfo>",
                "<ComicInfo><Pages><Page Image='0'/><Page Image='0'/></Pages></ComicInfo>",
                "<ComicInfo><Pages><Page/></Pages></ComicInfo>",
                "<ComicInfo><Pages><Page Image='3'/></Pages></ComicInfo>",
                "<ComicInfo><Pages><Page Image='0'><Image>1</Image></Page></Pages></ComicInfo>"
            })
            {
                SetXml(ambiguous);
                Reject<ArgumentException>(() => store.GetPageMetadata(id));
            }
            SetXml(untouched);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var malformed in new[]
            {
                "{\"expectedRevision\":\"x\"}", "{\"pages\":[]}",
                "{\"expectedRevision\":\"x\",\"pages\":[{\"page\":0,\"type\":null}]}",
                "{\"expectedRevision\":\"x\",\"pages\":[{\"page\":\"0\",\"type\":null,\"bookmark\":null}]}",
                "{\"expectedRevision\":\"x\",\"pages\":[],\"extra\":true}"
            }) Reject<JsonException>(() => JsonSerializer.Deserialize<PageMetadataEdit>(malformed, json));

            var tarPath = Path.Combine(directory, "catalog-only.cbt");
            using (var output = File.Create(tarPath))
            using (var tar = new TarWriter(output, TarEntryFormat.Ustar))
            using (var image = new MemoryStream(png))
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "1.png") { DataStream = image });
            var tarBytes = File.ReadAllBytes(tarPath);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var tarId = store.ListBooks("owner", null).Items.Single(book => book.Id != id).Id;
            var tarDraft = new[] { new PageMetadataRow(0, "FrontCover", "Catalog only") };
            preview = store.PreviewPageMetadata(tarId, new(store.GetPageMetadata(tarId).Revision, tarDraft));
            store.EditPageMetadata(tarId, new(preview.Revision, tarDraft));
            Check(store.GetPageMetadata(tarId).Pages[0] == tarDraft[0] && File.ReadAllBytes(tarPath).SequenceEqual(tarBytes),
                "Non-ZIP page editing failed catalog-only behavior.");
            Reject<ArgumentException>(() => store.PreviewArchiveWrite(tarId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
