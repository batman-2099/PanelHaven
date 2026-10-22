using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class ArchiveWriteChecks
{
    public static void RunCrashChild(string mode, string directory)
    {
        var root = Path.Combine(directory, "books");
        var database = Path.Combine(directory, "state", "library.sqlite");
        var store = new LibraryStore(database, [root]);
        var id = store.ListBooks("owner", null).Items.Single().Id;
        if (mode == "write")
        {
            var preview = store.PreviewArchiveWrite(id);
            store.ApplyArchiveWrite(id, new(preview.Revision, true));
            throw new Exception("Crash fixture unexpectedly committed.");
        }
        if (mode != "recover") throw new ArgumentException("Unknown crash fixture mode.");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DROP TRIGGER block_archive_commit;";
            command.ExecuteNonQuery();
        }
        if (store.GetBook("owner", id).Availability != "unavailable")
            throw new Exception("Restart exposed the interrupted replacement as available.");
        try { store.GetPage(id, 0); throw new Exception("Restart served an unreconciled replacement."); }
        catch (InvalidOperationException) { }
        store.ScanAsync(default).GetAwaiter().GetResult();
    }

    private static void CrashRecovery()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-archive-crash-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var database = Path.Combine(directory, "state", "library.sqlite");
        Directory.CreateDirectory(root);
        Process Start(string mode)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ArchiveWriteChecks).Assembly.Location);
            start.ArgumentList.Add("--archive-write-crash-child");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(directory);
            return Process.Start(start)!;
        }
        static void Stop(Process child)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (!child.WaitForExit(10_000)) throw new Exception("Crash fixture child did not terminate.");
        }
        try
        {
            var path = Path.Combine(root, "crash.cbz");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                for (var page = 0; page < 3; page++)
                {
                    using var image = archive.CreateEntry($"{page}.png").Open();
                    image.Write(png);
                }
                using var xml = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                xml.Write("<ComicInfo><Title>Original</Title></ComicInfo>");
            }
            var original = File.ReadAllBytes(path);
            var store = new LibraryStore(database, [root]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Crash fixture did not import.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            store.SaveProgress("owner", id, new(1, true));
            store.SaveRating("owner", id, new(4));
            store.SaveBookmark("owner", id, 2, new("Private"));
            var list = store.CreateList("owner", new("Keep", "manual", "all", []));
            store.AddListBooks("owner", list.Id, [id]);
            var order = store.SavePageOrder(id, new(store.GetPageOrder(id).Revision, [2, 0, 1]));
            store.EditMetadata(id, new(new() { ["Title"] = "Edited" }));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                // UNION ALL of a constant never completes and uses constant memory. This blocks only
                // the isolated fixture's post-replace UPDATE, before catalog commit.
                command.CommandText = """
                    CREATE TRIGGER block_archive_commit BEFORE UPDATE OF content_hash ON books BEGIN
                        SELECT count(*) FROM (
                            WITH RECURSIVE spin(x) AS (SELECT 1 UNION ALL SELECT x FROM spin)
                            SELECT x FROM spin
                        );
                    END;
                    """;
                command.ExecuteNonQuery();
            }
            using (var child = Start("write"))
            {
                try
                {
                    var timeout = Stopwatch.StartNew();
                    while (File.ReadAllBytes(path).SequenceEqual(original) && !child.HasExited
                        && timeout.Elapsed < TimeSpan.FromSeconds(30))
                        Thread.Sleep(10);
                    Check(!child.HasExited && !File.ReadAllBytes(path).SequenceEqual(original),
                        "Crash fixture did not reach post-replace/pre-commit boundary.");
                }
                finally { Stop(child); }
                Check(child.ExitCode != 0, "Crash fixture exited normally instead of being killed.");
            }
            var published = File.ReadAllBytes(path);
            Check(!published.SequenceEqual(original), "Killed writer removed the published replacement.");
            Check(!Directory.Exists(Path.Combine(directory, "state", "archive-backups"))
                && !Directory.EnumerateFiles(root, ".panelhaven-*.tmp").Any(), "Killed writer retained a hidden original or staging copy.");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT content_hash FROM books;";
                Check((string)command.ExecuteScalar()! == Convert.ToHexString(SHA256.HashData(original)),
                    "Killed writer committed its new source hash.");
                command.CommandText = "SELECT pages_json FROM page_orders;";
                Check(JsonSerializer.Deserialize<int[]>((string)command.ExecuteScalar()!)!.SequenceEqual(order.Pages),
                    "Killed writer committed a page-order reset.");
                command.CommandText = "SELECT availability FROM books;";
                Check((string)command.ExecuteScalar()! == "unavailable", "Killed writer lost its durable serving guard.");
            }
            using (var child = Start("recover"))
            {
                try
                {
                    Check(child.WaitForExit(30_000), "Restarted archive recovery timed out.");
                    Check(child.ExitCode == 0, "Restarted archive recovery failed.");
                }
                finally { Stop(child); }
            }
            Check(File.ReadAllBytes(path).SequenceEqual(published), "Restart recovery changed the verified published bytes.");
            store = new LibraryStore(database, [root]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var book = store.ListBooks("owner", null).Items.Single();
            Check(book.Id == id && book.CurrentPage == 1 && book.Completed && book.PersonalRating == 4
                && store.GetBookmarks("owner", id).Single() == new ReaderBookmark(2, "Private")
                && store.GetList("owner", list.Id).BookIds!.Single() == id
                && !store.GetBook("other", id).Started && store.GetBookmarks("other", id).Count == 0,
                "Crash recovery lost identity or private/account-scoped data.");
            Check(store.GetMetadata(id).Fields["Title"] == "Edited"
                && store.GetPageOrder(id).Revision != order.Revision
                && !store.GetPageOrder(id).Custom
                && store.GetPage(id, 2).Bytes.SequenceEqual(png),
                "Rescan lost catalog edits or page payload, or retained stale source-bound order.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static T Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T exception) { return exception; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-archive-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "original.cbz");
            var database = Path.Combine(directory, "library.sqlite");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            var unknown = RandomNumberGenerator.GetBytes(257);
            const string info = "<ComicInfo marker='root'><Title source='kept'>Original</Title><Unknown a='b'><Nested>keep</Nested></Unknown><Pages><Page Image='0' Bookmark='kept'/></Pages></ComicInfo>";
            const string legacy = "<ComicBook marker='legacy'><Title>Original</Title><CurrentPage>9</CurrentPage><Other a='b'>keep</Other><CustomValuesStore>Keep=old,BrokenToken</CustomValuesStore></ComicBook>";
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.Comment = "Archive comment";
                void Add(string name, byte[] bytes)
                {
                    var entry = archive.CreateEntry(name);
                    entry.LastWriteTime = new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.Zero);
                    entry.ExternalAttributes = unchecked((int)0x81a40000);
                    entry.Comment = "Entry " + name;
                    using var stream = entry.Open();
                    stream.Write(bytes);
                }
                Add("1.png", png);
                Add("unknown/data.bin", unknown);
                Add("comicinfo.XML", Encoding.UTF8.GetBytes(info));
                Add("ComicBook.xml", Encoding.UTF8.GetBytes(legacy));
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var original = File.ReadAllBytes(path);
            var store = new LibraryStore(database, [directory]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Archive write fixture did not import.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            var empty = store.PreviewArchiveWrite(id);
            Check(!empty.CanApply && empty.Entries.Count == 0 && empty.Changes.Count == 0, "Unedited archive was not a no-op.");
            Check(store.ApplyArchiveWrite(id, new(empty.Revision, true)).BookId == id
                && File.ReadAllBytes(path).SequenceEqual(original), "No-op changed the source or comicbook identity.");
            store.SaveProgress("owner", id, new(0, true));
            store.SaveRating("owner", id, new(4));
            store.SaveBookmark("owner", id, 0, new("Personal"));
            var list = store.CreateList("owner", new("Retain", "manual", "all", []));
            store.AddListBooks("owner", list.Id, [id]);
            void CheckPrivate()
            {
                var book = store.GetBook("owner", id);
                Check(book.Id == id && book.Completed && book.PersonalRating == 4
                    && store.GetBookmarks("owner", id).Single().Name == "Personal"
                    && store.GetList("owner", list.Id).BookIds!.Single() == id
                    && !store.GetBook("other", id).Completed && store.GetBookmarks("other", id).Count == 0,
                    "Archive mutation changed IDs or private/account-scoped state.");
                Check(store.GetPage(id, 0).Bytes.SequenceEqual(png), "Reader source version/page payload was not refreshed safely.");
            }
            store.EditMetadata(id, new(new() { ["Title"] = "Edited" }, new() { ["Keep"] = "new" }));
            var preview = store.PreviewArchiveWrite(id);
            Check(preview.CanApply && preview.Changes.Any(change => change.Field == "Title" && change.Before == "Original" && change.After == "Edited")
                && preview.Changes.Any(change => change.Custom && change.Field == "Keep" && change.After == "new")
                && File.ReadAllBytes(path).SequenceEqual(original), "Preview changed source or omitted actual source/catalog differences.");
            Reject<ArgumentException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, false)));
            Reject<ArchiveWriteConflictException>(() => store.ApplyArchiveWrite(id, new("wrong", true)));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Reject<OperationCanceledException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true), cancelled.Token));
            }
            store.EditMetadata(id, new(new() { ["Publisher"] = "Later" }));
            Reject<ArchiveWriteConflictException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            preview = store.PreviewArchiveWrite(id);
            var result = store.ApplyArchiveWrite(id, new(preview.Revision, true));
            Check(result.BookId == id && !Directory.Exists(Path.Combine(directory, "archive-backups"))
                && !Directory.EnumerateFiles(directory, ".panelhaven-*.tmp").Any(), "Write retained an original backup or staging copy.");
            using (var archive = ZipFile.OpenRead(path))
            {
                Check(archive.Comment == "Archive comment"
                    && archive.Entries.Select(entry => entry.FullName).SequenceEqual(new[] { "1.png", "unknown/data.bin", "comicinfo.XML", "ComicBook.xml" }),
                    "Archive order, names, or comment changed.");
                foreach (var entry in archive.Entries)
                {
                    using var stream = entry.Open();
                    using var bytes = new MemoryStream();
                    stream.CopyTo(bytes);
                    Check(entry.Comment == "Entry " + entry.FullName && entry.ExternalAttributes == unchecked((int)0x81a40000)
                        && entry.LastWriteTime.Year == 2020, "Entry properties were not preserved.");
                    if (entry.FullName == "1.png") Check(bytes.ToArray().SequenceEqual(png), "Page bytes changed.");
                    else if (entry.FullName == "unknown/data.bin") Check(bytes.ToArray().SequenceEqual(unknown), "Unknown entry bytes changed.");
                    else Check(bytes.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(preview.Entries.Single(item => item.Name == entry.FullName).After!)),
                        "Written XML did not exactly match preview UTF-8.");
                }
                using var reader = new StreamReader(archive.GetEntry("comicinfo.XML")!.Open());
                var xml = XDocument.Parse(reader.ReadToEnd());
                Check(xml.Root!.Attribute("marker")?.Value == "root" && xml.Root.Element("Title")!.Attribute("source")?.Value == "kept"
                    && xml.Root.Element("Unknown")!.Element("Nested")!.Value == "keep"
                    && xml.Root.Element("Pages")!.Element("Page")!.Attribute("Bookmark")?.Value == "kept", "Unknown XML/page structures were lost.");
            }
            CheckPrivate();
            Reject<ArchiveWriteConflictException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            Check(!store.PreviewArchiveWrite(id).CanApply, "Applied write was not subsequently a no-op.");
            var written = File.ReadAllBytes(path);
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), "Source permissions changed.");

            // Real SQLite failures exercise both sides of atomic publication without production test hooks.
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            void Sql(string text)
            {
                using var command = db.CreateCommand();
                command.CommandText = text;
                command.ExecuteNonQuery();
            }
            store.EditMetadata(id, new(new() { ["Title"] = "Recovery write" }));
            preview = store.PreviewArchiveWrite(id);
            var stamp = File.GetLastWriteTimeUtc(path);
            Sql("CREATE TRIGGER reject_archive_reservation BEFORE UPDATE OF availability ON books BEGIN SELECT RAISE(ABORT,'Injected reservation failure'); END;");
            Reject<SqliteException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            Sql("DROP TRIGGER reject_archive_reservation;");
            Check(File.ReadAllBytes(path).SequenceEqual(written) && File.GetLastWriteTimeUtc(path) == stamp
                && store.GetBook("owner", id).Availability == "available"
                && !Directory.EnumerateFiles(directory, ".panelhaven-*.tmp").Any(), "Pre-publication failure changed source or leaked staging.");
            Sql("CREATE TRIGGER reject_archive_refresh BEFORE UPDATE OF content_hash ON books BEGIN SELECT RAISE(ABORT,'Injected catalog failure'); END;");
            var failure = Reject<ArchiveWriteRecoveryException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            var published = File.ReadAllBytes(path);
            Check(failure.InnerException is SqliteException && failure.Message.Contains("cannot undo")
                && !published.SequenceEqual(written) && store.GetBook("owner", id).Availability == "unavailable",
                "Post-publication failure hid its cause, discarded verified output, or exposed stale catalog data.");
            Sql("DROP TRIGGER reject_archive_refresh;");
            // Equal size/timestamp is not evidence that an interrupted publication was cataloged.
            using (var matchVersion = db.CreateCommand())
            {
                matchVersion.CommandText = "UPDATE books SET file_size=$size,modified_ticks=$ticks WHERE id=$id";
                matchVersion.Parameters.AddWithValue("$size", new FileInfo(path).Length);
                matchVersion.Parameters.AddWithValue("$ticks", File.GetLastWriteTimeUtc(path).Ticks);
                matchVersion.Parameters.AddWithValue("$id", id);
                matchVersion.ExecuteNonQuery();
            }
            store = new LibraryStore(database, [directory]);
            Reject<InvalidOperationException>(() => store.GetPage(id, 0));
            Reject<InvalidOperationException>(() => store.GetCoverVersion(id));
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Updated == 1
                && File.ReadAllBytes(path).SequenceEqual(published)
                && !store.PreviewArchiveWrite(id).CanApply, "Rescan did not reconcile the retained replacement.");
            CheckPrivate();
            Check(!Directory.Exists(Path.Combine(directory, "archive-backups"))
                && !Directory.EnumerateFiles(directory, ".panelhaven-*.tmp").Any(),
                "Write failure created an original backup or leaked staging.");
            stamp = File.GetLastWriteTimeUtc(path);

            store.EditMetadata(id, new(new() { ["Title"] = "Stale guarded write" }));
            preview = store.PreviewArchiveWrite(id);
            var altered = published.ToArray();
            var comment = Encoding.UTF8.GetBytes("Archive comment");
            var offset = altered.AsSpan().IndexOf(comment);
            Check(offset >= 0, "Fixture has no archive comment.");
            altered[offset] = (byte)'a';
            File.WriteAllBytes(path, altered);
            File.SetLastWriteTimeUtc(path, stamp);
            Reject<ArchiveWriteConflictException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            Check(File.ReadAllBytes(path).SequenceEqual(altered), "Stale write overwrote a same-size/mtime external edit.");
            File.WriteAllBytes(path, published);
            File.SetLastWriteTimeUtc(path, stamp);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead);
                Reject<UnauthorizedAccessException>(() => store.PreviewArchiveWrite(id));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                var link = Path.Combine(directory, "linked.cbz");
                File.Move(path, link);
                File.CreateSymbolicLink(path, link);
                Reject<UnauthorizedAccessException>(() => store.PreviewArchiveWrite(id));
                File.Delete(path);
                File.Move(link, path);
            }
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Scanned == 1, "Written comicbook did not remain scannable.");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Reject<JsonException>(() => JsonSerializer.Deserialize<ArchiveWriteRequest>("{\"confirmed\":true}", json));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ArchiveWriteRequest>("{\"expectedRevision\":\"x\"}", json));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ArchiveWriteRequest>("{\"expectedRevision\":\"x\",\"confirmed\":true,\"path\":\"bad\"}", json));

            // A newer external extension must survive an older edited catalog overlay.
            var external = Path.Combine(directory, "external.tmp");
            using (var source = ZipFile.OpenRead(path))
            using (var destination = ZipFile.Open(external, ZipArchiveMode.Create))
                foreach (var entry in source.Entries)
                {
                    using var input = entry.Open();
                    using var output = destination.CreateEntry(entry.FullName).Open();
                    if (entry.FullName == "comicinfo.XML")
                    {
                        using var reader = new StreamReader(input);
                        output.Write(Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace(">keep</Nested>", ">external</Nested>")));
                    }
                    else input.CopyTo(output);
                }
            File.Move(external, path, true);
            store.EditMetadata(id, new(new() { ["Title"] = "External extension write" }));
            preview = store.PreviewArchiveWrite(id);
            Check(preview.Entries.Single(entry => entry.Name == "comicinfo.XML").After!.Contains(">external</Nested>"),
                "Preview would overwrite source-only unknown XML with stale catalog XML.");
            store.ApplyArchiveWrite(id, new(preview.Revision, true));
            byte[] ReadEntry(string name)
            {
                using var archive = ZipFile.OpenRead(path);
                using var input = archive.GetEntry(name)!.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            }
            var untouchedLegacy = ReadEntry("ComicBook.xml");
            store.EditMetadata(id, new(new() { ["Publisher"] = "Second write" }));
            preview = store.PreviewArchiveWrite(id);
            Check(preview.Entries.Count == 1 && preview.Entries[0].Name == "comicinfo.XML", "Unchanged metadata was staged for replacement.");
            store.ApplyArchiveWrite(id, new(preview.Revision, true));
            Check(ReadEntry("ComicBook.xml").SequenceEqual(untouchedLegacy), "Unchanged metadata entry bytes were rewritten.");
            CheckPrivate();
            store.EditMetadata(id, new(new() { ["Title"] = null }));
            preview = store.PreviewArchiveWrite(id);
            Check(preview.Entries.Count == 2 && preview.Entries.All(entry => !entry.After!.Contains("<Title")),
                "A cleared scalar remained in legacy metadata and could reappear on import.");
            store.ApplyArchiveWrite(id, new(preview.Revision, true));
            var fresh = new LibraryStore(Path.Combine(directory, "fresh.sqlite"), [directory]);
            Check(fresh.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Written archive did not re-import.");
            Check(!fresh.GetMetadata(fresh.ListBooks("owner", null).Items.Single().Id).Fields.ContainsKey("Title"),
                "Re-import resurrected a scalar removed by write-back.");
            Check(!Directory.Exists(Path.Combine(directory, "archive-backups"))
                && !Directory.EnumerateFiles(directory, ".panelhaven-*.tmp").Any(),
                "Later metadata writes created an original backup or leaked staging.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
        CrashRecovery();
    }
}
