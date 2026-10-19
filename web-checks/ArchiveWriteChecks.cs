using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class ArchiveWriteChecks
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
            Check(store.ApplyArchiveWrite(id, new(empty.Revision, true)).BackupId is null
                && store.GetArchiveBackups(id).Count == 0 && File.ReadAllBytes(path).SequenceEqual(original), "No-op wrote or backed up the source.");
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
            Check(result.BackupId is not null && !result.Restored, "Write did not retain its backup.");
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
            var restore = store.PreviewArchiveRestore(id, result.BackupId!);
            var backupFile = Path.Combine(directory, "archive-backups", id, result.BackupId!, "original.bin");
            Check(File.ReadAllBytes(backupFile).SequenceEqual(original), "Backup was not the complete original archive.");
            File.WriteAllBytes(backupFile, original.Select((value, index) => index == 40 ? (byte)(value ^ 1) : value).ToArray());
            Reject<InvalidDataException>(() => store.RestoreArchiveBackup(id, result.BackupId!, new(restore.Revision, true)));
            File.WriteAllBytes(backupFile, original);
            File.Move(backupFile, backupFile + ".missing");
            Reject<FileNotFoundException>(() => store.PreviewArchiveRestore(id, result.BackupId!));
            File.Move(backupFile + ".missing", backupFile);
            var restored = store.RestoreArchiveBackup(id, result.BackupId!, new(restore.Revision, true));
            Check(restored.Restored && File.ReadAllBytes(path).SequenceEqual(original)
                && store.GetMetadata(id).Fields["Title"] == "Edited" && store.GetMetadata(id).Custom["Keep"] == "new", "Undo failed exact bytes or discarded catalog edits.");
            CheckPrivate();
            Reject<ArchiveWriteConflictException>(() => store.RestoreArchiveBackup(id, result.BackupId!, new(restore.Revision, true)));
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), "Source permissions changed.");

            // Real database failure after replacement exercises rollback, rather than a production-only test hook.
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            void Sql(string text)
            {
                using var command = db.CreateCommand();
                command.CommandText = text;
                command.ExecuteNonQuery();
            }
            preview = store.PreviewArchiveWrite(id);
            var stamp = File.GetLastWriteTimeUtc(path);
            Sql("CREATE TRIGGER reject_archive_refresh BEFORE UPDATE OF content_hash ON books BEGIN SELECT RAISE(ABORT,'Injected catalog failure'); END;");
            Reject<SqliteException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            Sql("DROP TRIGGER reject_archive_refresh;");
            Check(File.ReadAllBytes(path).SequenceEqual(original) && File.GetLastWriteTimeUtc(path) == stamp
                && store.GetArchiveBackups(id).Count == 3, "Catalog failure lost original/version or durable recovery history.");
            CheckPrivate();

            preview = store.PreviewArchiveWrite(id);
            var altered = original.ToArray();
            var comment = Encoding.UTF8.GetBytes("Archive comment");
            var offset = altered.AsSpan().IndexOf(comment);
            Check(offset >= 0, "Fixture has no archive comment.");
            altered[offset] = (byte)'a';
            File.WriteAllBytes(path, altered);
            File.SetLastWriteTimeUtc(path, stamp);
            Reject<ArchiveWriteConflictException>(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            Check(File.ReadAllBytes(path).SequenceEqual(altered), "Stale write overwrote a same-size/mtime external edit.");
            File.WriteAllBytes(path, original);
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
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Scanned == 1, "Private archive backups entered scanner imports.");
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
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
