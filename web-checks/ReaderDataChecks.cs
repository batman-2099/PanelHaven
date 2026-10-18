using System.IO.Compression;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;

public static class ReaderDataChecks
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "reader.cbz");
            var database = Path.Combine(directory, "reader.sqlite");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            const string xml = """
                <ComicInfo><Title>Reader</Title><Pages>
                  <Page Image="0" Type="FrontCover" Bookmark="&lt;img src=x onerror=alert(1)&gt;"/>
                  <Page Image="1" Bookmark="Chapter one"/>
                  <Page Image="2" Type="Advertisment"/>
                  <Page Image="3" Type="Story, Advertisement"/>
                  <Page Image="4" Type="256"/>
                  <Page Image="5" Type="8, Advertisement"/>
                  <Page Image="6" Type="FutureType"/>
                  <Page Image="7" Type="Story, FutureType"/>
                  <Page Image="8" Type="2048"/>
                  <Page Image="9" Type="-1"/>
                  <Page Image="10" Type="0"/>
                  <Page Image="11" Type=""/>
                  <Page Image="12" Type="All"/>
                  <Page Image="13" Type="AllWithDeleted"/>
                  <Page Image="14" Type="Deleted"/>
                  <Page Image="15" Type="Other"/><Page Image="15" Type="FrontCover"/><Page Image="15" Type="Story"/>
                  <Page Image="16" Type="Story,"/>
                  <Page Image="17" Type="2147483648"/>
                  <Page Image="18" Type="2049"/>
                  <Page Image="-1" Type="Story"/><Page Image="20" Type="Story"/>
                  <Page Image="no" Type="Story"/><Page Type="Story"/>
                </Pages></ComicInfo>
                """;
            void CreateArchive(int count)
            {
                if (File.Exists(path)) File.Delete(path);
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                // Deliberately insert in reverse order: reader indices follow natural filenames, not ZIP order.
                for (var page = count - 1; page >= 0; page--)
                {
                    using var stream = archive.CreateEntry($"{page + 1}.png").Open();
                    stream.Write(png);
                    stream.WriteByte((byte)page);
                }
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write(xml);
            }
            CreateArchive(20);
            var store = new LibraryStore(database, [directory]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Reader fixture import failed.");
            var id = store.ListBooks("alice", null).Items.Single().Id;
            Check(store.GetPage(id, 2).Bytes[^1] == 2, "Reader page ordering is not natural.");
            var info = store.GetPageInfo(id);
            Check(info.Count == 18 && !info.Any(p => p.Page is 15 or 19) && info.All(p => p.Page >= 0 && p.Page < 20),
                "Malformed, duplicate, missing, or out-of-range metadata indices were classified.");
            var masks = new[] { 1, 8, 16, 24, 256, 24, 0, 0, 0, 0, 8, 0, 1023, 2047, 1024, 0, 0, 0, 0 };
            Check(info.All(p => p.TypeMask == masks[p.Page]) && info.Single(p => p.Page == 6).Type == "FutureType",
                "Page flags, upstream defaults, or unknown types parsed incorrectly.");
            Check(info[0].Bookmark == "<img src=x onerror=alert(1)>", "Embedded bookmark text was not preserved.");
            Check(store.GetBookmarks("alice", id).Count == 0, "Embedded bookmarks leaked into personal bookmarks.");
            store.SaveBookmark("alice", id, 19, new("Last"));
            store.SaveBookmark("alice", id, 0, new("First"));
            store.SaveBookmark("alice", id, 0, new("Updated"));
            store.SaveBookmark("bob", id, 0, new("Bob"));
            Check(store.GetBookmarks("alice", id).SequenceEqual(new[] { new ReaderBookmark(0, "Updated"), new ReaderBookmark(19, "Last") }),
                "Bookmark sorting or upsert failed.");
            Check(store.GetBookmarks("bob", id).Single().Name == "Bob", "Bookmark users were not isolated.");
            Reject<KeyNotFoundException>(() => store.DeleteBookmark("bob", id, 19));
            store.DeleteBookmark("bob", id, 0);
            Check(store.GetBookmarks("alice", id).Count == 2 && store.GetBookmarks("bob", id).Count == 0, "Deletion crossed user ownership.");
            foreach (var name in new[] { null, "", " \t ", "bad\nname", "bad\0name", new string('x', 201) })
                Reject<ArgumentException>(() => store.SaveBookmark("alice", id, 0, new(name!)));
            store.SaveBookmark("alice", id, 1, new(new string('x', 200)));
            store.DeleteBookmark("alice", id, 1);
            Reject<ArgumentException>(() => store.SaveBookmark("alice", id, -1, new("Bad")));
            Reject<ArgumentException>(() => store.SaveBookmark("alice", id, 20, new("Bad")));
            Reject<ArgumentException>(() => store.DeleteBookmark("alice", id, -1));
            Reject<KeyNotFoundException>(() => store.GetBookmarks("alice", "missing"));
            Reject<KeyNotFoundException>(() => store.GetPageInfo("missing"));
            Reject<KeyNotFoundException>(() => store.SaveBookmark("alice", "missing", 0, new("Bad")));
            Reject<KeyNotFoundException>(() => store.DeleteBookmark("alice", id, 18));
            store = new LibraryStore(database, [directory]);
            Check(store.GetBookmarks("alice", id).Count == 2, "Bookmarks did not survive a new store.");
            CreateArchive(2);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Updated == 1, "Reader shrink fixture was not rescanned.");
            Check(store.GetPageInfo(id).Count == 2 && store.GetBookmarks("alice", id).Single().Page == 0,
                "Rescan exposed out-of-range metadata or bookmarks.");
            Reject<ArgumentException>(() => store.SaveBookmark("alice", id, 19, new("Stale")));
            store.DeleteBookmark("alice", id, 19);
            CreateArchive(20);
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetBookmarks("alice", id).Single().Page == 0, "Deleting a stale bookmark failed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
