using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class FileOperationChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return; // Production explicitly refuses platforms without the required security preservation.
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
        static string Identity(string path) => (string)typeof(LibraryStore)
            .GetMethod("FileIdentity", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path])!;
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-file-operations-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var second = Path.Combine(directory, "other");
        var cross = Directory.Exists("/dev/shm") ? Path.Combine("/dev/shm", "panelhaven-file-operations-" + Guid.NewGuid().ToString("N")) : second;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(cross);
        var database = Path.Combine(directory, "state", "library.sqlite");
        try
        {
            var path = Path.Combine(root, "original.cbz");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.Comment = "stable-A";
                foreach (var (name, bytes) in new[]
                {
                    ("1.png", png),
                    ("ComicInfo.xml", Encoding.UTF8.GetBytes("<ComicInfo marker='keep'><Title>Original</Title><Pages><Page Image='0' Type='Story' Bookmark='Original'/></Pages><Unknown>untouched</Unknown></ComicInfo>")),
                    ("ComicBook.xml", Encoding.UTF8.GetBytes("<ComicBook><CurrentPage>7</CurrentPage><CustomValuesStore>Keep=value</CustomValuesStore><Unknown a='b'>keep</Unknown></ComicBook>"))
                })
                {
                    using var stream = archive.CreateEntry(name).Open();
                    stream.Write(bytes);
                }
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var original = File.ReadAllBytes(path);
            var roots = cross == second ? new[] { root, second } : new[] { root, second, cross };
            var store = new LibraryStore(database, roots);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "File-operation fixture did not import.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString());
            db.Open();
            object? Sql(string sql, object? value = null)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$value", value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            FileOperationResult Move(int target, string folder, string filename)
            {
                var preview = store.PreviewFileMove(id, new(target, folder, filename));
                Check(preview.CanApply, "Move preview unexpectedly conflicted: " + string.Join("; ", preview.Conflicts));
                return store.ApplyFileMove(id, new(target, folder, filename, preview.Revision, true));
            }
            void SeedLegacyHistory(string pendingId)
            {
                Sql("""
                    ALTER TABLE file_operations ADD COLUMN undo_of TEXT REFERENCES file_operations(id);
                    ALTER TABLE file_operations ADD COLUMN message TEXT;
                    INSERT INTO file_operations(id,book_id,created_at,source,destination,hash,state,committed,
                        source_identity,stage_identity,security,file_size,modified_ticks)
                    SELECT 'legacy-' || legacy.state,book_id,created_at,destination,source,hash,legacy.state,1,
                        source_identity,stage_identity,security,file_size,modified_ticks
                    FROM file_operations CROSS JOIN (SELECT 'applied' AS state UNION ALL SELECT 'undone' UNION ALL SELECT 'failed') legacy
                    WHERE id=$value;
                    UPDATE file_operations SET undo_of='legacy-applied' WHERE id=$value;
                    """, pendingId);
            }
            var initialXml = (string)Sql("SELECT info_xml FROM books WHERE id=$id")!;
            Sql("UPDATE books SET info_xml=$value WHERE id=$id",
                "<ComicInfo xmlns='urn:publisher-check'><Publisher> Catalog/Press. </Publisher><Series> Series/Name. </Series></ComicInfo>");
            Check(store.PreviewFileMove(id, new(0, "new", "publisher.cbz")).CanApply
                && Directory.Exists(Path.Combine(root, "Catalog_Press", "Series_Name"))
                && File.ReadAllBytes(path).SequenceEqual(original) && store.GetFileOperations(id).Count == 0,
                "Preview did not create destination folders safely without moving the comicbook.");
            Check(store.CreateFileOperationFolder(new(0, "new") { BookId = id }).Folder == "Catalog_Press/Series_Name",
                "Book-aware folder creation ignored namespace-aware publisher/series metadata or its sanitization.");
            var publisherPreview = store.PreviewFileMove(id, new(0, "new", "publisher.cbz"));
            var prefixedPreview = store.PreviewFileMove(id, new(0, "Catalog_Press/Series_Name", "publisher.cbz"));
            Check(publisherPreview.CanApply && publisherPreview.Destination == "Library 1/Catalog_Press/Series_Name/publisher.cbz"
                && prefixedPreview.Destination == publisherPreview.Destination && prefixedPreview.Revision == publisherPreview.Revision
                && store.CreateFileOperationFolder(new(0, "Catalog_Press/Series_Name/extra") { BookId = id }).Folder == "Catalog_Press/Series_Name",
                "Exact organization retained a custom folder or repeated publisher/series components.");
            store.EditMetadata(id, new(new() { ["Publisher"] = "Changed Publisher" }));
            store.CreateFileOperationFolder(new(0, "new") { BookId = id });
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, "new", "publisher.cbz", publisherPreview.Revision, true)));
            Check(File.Exists(path) && !File.Exists(Path.Combine(root, "Catalog_Press", "Series_Name", "publisher.cbz"))
                && !File.Exists(Path.Combine(root, "Changed Publisher", "Series_Name", "publisher.cbz")) && store.GetFileOperations(id).Count == 0,
                "A stale publisher preview moved the source or wrote history.");
            Sql("UPDATE books SET info_xml=$value WHERE id=$id", initialXml);
            foreach (var index in Enumerable.Range(1, roots.Length - 1))
                Check(store.CreateFileOperationFolder(new(index, "") { BookId = id }).Folder == "Unknown Publisher/Unknown Series",
                    "Missing publisher and series did not use fallback folders.");
            store.SaveProgress("owner", id, new(0, true));
            store.SaveRating("owner", id, new(4));
            store.SaveBookmark("owner", id, 0, new("Private"));
            var list = store.CreateList("owner", new("Retain", "manual", "all", []));
            store.AddListBooks("owner", list.Id, [id]);
            store.EditMetadata(id, new(new() { ["Title"] = "Catalog title" }, new() { ["Keep"] = "Edited" }));
            var pageDraft = new[] { new PageMetadataRow(0, "FrontCover", "Edited") };
            var pagePreview = store.PreviewPageMetadata(id, new(store.GetPageMetadata(id).Revision, pageDraft));
            store.EditPageMetadata(id, new(pagePreview.Revision, pageDraft));
            var rawXml = (string)Sql("SELECT info_xml || book_xml FROM books WHERE id=$id")!;
            var pageIntents = (long)Sql("SELECT COUNT(*) FROM page_metadata_edits WHERE book_id=$id")!;
            void PrivateState()
            {
                var book = store.GetBook("owner", id);
                Check(book.Id == id && book.Title == "Catalog title" && book.Completed && book.PersonalRating == 4
                    && store.GetBookmarks("owner", id).Single().Name == "Private" && store.GetList("owner", list.Id).BookIds!.Single() == id
                    && !store.GetBook("other", id).Started && store.GetBookmarks("other", id).Count == 0,
                    "File operation changed identity, metadata, or account-scoped state.");
                Check((string)Sql("SELECT info_xml || book_xml FROM books WHERE id=$id")! == rawXml
                    && (long)Sql("SELECT COUNT(*) FROM page_metadata_edits WHERE book_id=$id")! == pageIntents
                    && store.GetPageMetadata(id).Pages[0] == pageDraft[0], "File operation changed raw XML or page intents.");
                Check(store.GetPage(id, 0).Bytes.SequenceEqual(png), "Reader source was not refreshed after file operation.");
            }

            const string createdFolder = "Unknown Publisher/Unknown Series";
            var createdPath = Path.Combine(root, createdFolder);
            Check(store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz")).CanApply
                && Directory.Exists(createdPath) && File.ReadAllBytes(path).SequenceEqual(original)
                && store.GetFileOperations(id).Count == 0, "Automatic folder creation changed archive bytes or move history.");
            Check(store.CreateFileOperationFolder(new(0, "created//nested/") { BookId = id }).Folder == createdFolder && Directory.Exists(createdPath)
                && !Directory.Exists(Path.Combine(root, "created")), "Book-aware folder creation retained a custom subfolder.");
            File.SetUnixFileMode(createdPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Check(store.CreateFileOperationFolder(new(0, createdFolder) { BookId = id }).Folder == createdFolder
                && File.GetUnixFileMode(createdPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                && store.CreateFileOperationFolder(new(0, "")).Folder == ""
                && store.CreateFileOperationFolder(new(0, "created//nested/")).Folder == "created/nested"
                && Directory.Exists(Path.Combine(root, "created", "nested")),
                "Existing-folder creation was not idempotent or broadened permissions.");
            foreach (var request in new[]
            {
                new FileOperationFolderRequest(0, "../other"), new(0, "/absolute"), new(0, "C:/absolute"),
                new(0, "created/../other"), new(0, "created\\child"), new(0, "."), new(0, "bad\nfolder"),
                new(0, new string('a', 4097)), new(0, null!), new(-1, "child"), new(99, "child")
            })
                Reject<ArgumentException>(() => store.CreateFileOperationFolder(request));
            foreach (var collision in new[] { "original.cbz", "original.cbz/child" })
                Reject<UnauthorizedAccessException>(() => store.CreateFileOperationFolder(new(0, collision)));
            var folderDatabase = Path.Combine(Path.GetDirectoryName(database)!, "folder-safety.sqlite");
            var privateRootStore = new LibraryStore(folderDatabase, [directory]);
            Reject<UnauthorizedAccessException>(() => privateRootStore.CreateFileOperationFolder(new(0, "state/new")));
            Check(!Directory.Exists(Path.Combine(directory, "state", "new")), "Folder creation entered application state.");
            Check(File.ReadAllBytes(path).SequenceEqual(original) && !Directory.EnumerateFileSystemEntries(createdPath).Any()
                && store.GetFileOperations(id).Count == 0 && store.GetBook("owner", id).Location == "Library 1/original.cbz",
                "Creating folders overwrote an archive, moved it, or added move history.");

            var missingRoot = Path.Combine(directory, "missing", "library");
            var missingRootStore = new LibraryStore(folderDatabase, [missingRoot]);
            Reject<IOException>(() => missingRootStore.CreateFileOperationFolder(new(0, "child")));
            Check(!Directory.Exists(Path.Combine(directory, "missing")), "Folder creation created a missing library root or its ancestors.");

            var preview = store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz"));
            Check(preview.Source == "Library 1/original.cbz" && preview.Destination == "Library 1/Unknown Publisher/Unknown Series/renamed.cbz"
                && preview.SourceHash == Convert.ToHexString(SHA256.HashData(original))
                && File.ReadAllBytes(path).SequenceEqual(original) && store.GetFileOperations(id).Count == 0
                && !Directory.EnumerateFileSystemEntries(createdPath).Any(),
                "Preview was inexact or mutated filesystem/history.");
            Reject<ArgumentException>(() => store.ApplyFileMove(id, new(0, createdFolder, "renamed.cbz", preview.Revision, false)));
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, createdFolder, "renamed.cbz", "stale", true)));
            Check(store.PreviewFileMove(id, new(0, "", "original.cbz")).CanApply,
                "A same-root move from a legacy path did not add the publisher folder.");
            foreach (var draft in new[] { new FileMoveRequest(0, "../other", "bad.cbz"), new(0, "/absolute", "bad.cbz"), new(0, "", "../bad.cbz"), new(0, "", "bad.cbr"), new(99, "", "bad.cbz") })
                Reject<ArgumentException>(() => store.PreviewFileMove(id, draft));
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Reject<JsonException>(() => JsonSerializer.Deserialize<FileMoveRequest>("{\"rootIndex\":\"0\",\"folder\":\"\",\"fileName\":\"a.cbz\"}", jsonOptions));
            Reject<JsonException>(() => JsonSerializer.Deserialize<FileMoveApplyRequest>("{\"rootIndex\":0,\"folder\":\"\",\"fileName\":\"a.cbz\",\"expectedRevision\":\"x\"}", jsonOptions));
            foreach (var json in new[]
            {
                "{}", "{\"rootIndex\":0}", "{\"folder\":\"new\"}", "{\"rootIndex\":\"0\",\"folder\":\"new\"}",
                "{\"rootIndex\":0.5,\"folder\":\"new\"}", "{\"rootIndex\":0,\"folder\":\"new\",\"unknown\":true}"
            })
                Reject<JsonException>(() => JsonSerializer.Deserialize<FileOperationFolderRequest>(json, jsonOptions));
            Directory.Delete(createdPath);
            Directory.CreateSymbolicLink(createdPath, second);
            Reject<UnauthorizedAccessException>(() => store.PreviewFileMove(id, new(0, "alias", "bad.cbz")));
            Reject<UnauthorizedAccessException>(() => store.CreateFileOperationFolder(new(0, "alias/escaped") { BookId = id }));
            var linkedRootStore = new LibraryStore(folderDatabase, [createdPath]);
            Reject<UnauthorizedAccessException>(() => linkedRootStore.CreateFileOperationFolder(new(0, "escaped")));
            Directory.CreateDirectory(Path.Combine(second, "inside"));
            var linkedAncestorStore = new LibraryStore(folderDatabase, [Path.Combine(createdPath, "inside")]);
            Reject<UnauthorizedAccessException>(() => linkedAncestorStore.CreateFileOperationFolder(new(0, "escaped")));
            Check(!Directory.Exists(Path.Combine(second, "inside", "escaped")), "Folder creation followed a symlink ancestor of its root.");
            Check(!Directory.Exists(Path.Combine(second, "escaped")), "Folder creation followed a symlink outside its root.");
            Directory.Delete(createdPath);
            Directory.CreateDirectory(createdPath);
            File.CreateSymbolicLink(Path.Combine(createdPath, "occupied.cbz"), Path.Combine(second, "missing.cbz"));
            Check(!store.PreviewFileMove(id, new(0, "", "occupied.cbz")).CanApply, "Dangling destination symlink was not a collision.");
            Reject<UnauthorizedAccessException>(() => store.CreateFileOperationFolder(new(0, createdFolder + "/occupied.cbz/escaped")));
            Check(!Directory.Exists(Path.Combine(second, "missing.cbz")), "Folder creation followed a dangling symlink.");
            File.Delete(Path.Combine(createdPath, "occupied.cbz"));
            File.Copy(path, Path.Combine(second, createdFolder, "collision.cbz"));
            Check(!store.PreviewFileMove(id, new(1, "", "collision.cbz")).CanApply, "Equal bytes at destination were not a collision.");
            store.ScanAsync(default).GetAwaiter().GetResult();
            File.Delete(Path.Combine(second, createdFolder, "collision.cbz"));
            Check(!store.PreviewFileMove(id, new(1, "", "collision.cbz")).CanApply, "Missing but cataloged destination was not reserved.");

            // A valid ZIP comment change leaves both length and the restored timestamp unchanged.
            preview = store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz"));
            var ticks = File.GetLastWriteTimeUtc(path);
            var changed = original.ToArray();
            changed[^1] = (byte)'B';
            File.WriteAllBytes(path, changed);
            File.SetLastWriteTimeUtc(path, ticks);
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, createdFolder, "renamed.cbz", preview.Revision, true)));
            File.WriteAllBytes(path, original);
            File.SetLastWriteTimeUtc(path, ticks);
            var originalIdentity = Identity(path);
            var firstPreview = store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz"));
            Directory.Delete(createdPath);
            store.ApplyFileMove(id, new(0, createdFolder, "renamed.cbz", firstPreview.Revision, true));
            Check(!File.Exists(path) && File.ReadAllBytes(Path.Combine(createdPath, "renamed.cbz")).SequenceEqual(original), "Same-device rename into a created folder lost or rewrote bytes.");
            Check(Identity(Path.Combine(createdPath, "renamed.cbz")) == originalIdentity, "Same-device rename unnecessarily copied the inode.");
            Check(!store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz")).CanApply, "Normalized no-op preview was applicable.");
            PrivateState();
            Move(1, "", "moved.cbz");
            Check(store.GetBook("owner", id).Location == "Library 2/Unknown Publisher/Unknown Series/moved.cbz", "Cross-root location was not refreshed.");
            Move(roots.Length - 1, "", "cross-device.cbz");
            Check(File.GetUnixFileMode(Path.Combine(roots[^1], createdFolder, "cross-device.cbz")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), "Cross-device copy lost permissions.");
            PrivateState();
            File.WriteAllBytes(Path.Combine(second, createdFolder, "moved.cbz"), original);
            Check(!store.PreviewFileMove(id, new(1, "", "moved.cbz")).CanApply, "Return move would overwrite an occupied path.");
            File.Delete(Path.Combine(second, createdFolder, "moved.cbz"));
            Move(1, "", "moved.cbz");
            Move(0, "", "original.cbz");
            path = Path.Combine(createdPath, "original.cbz");
            PrivateState();
            Check(File.ReadAllBytes(path).SequenceEqual(original) && store.GetFileOperations(id).Count == 0
                && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 0,
                "Return moves changed bytes or retained completed history.");
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0, "Scan split identity after moves.");

            Move(1, "", "written.cbz");
            Move(0, "nested", "move-chain.cbz");
            Move(1, "", "written.cbz");
            var beforeWrite = store.PreviewFileMove(id, new(0, "", "written-back.cbz"));
            var write = store.PreviewArchiveWrite(id);
            store.ApplyArchiveWrite(id, new(write.Revision, true));
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, "", "written-back.cbz", beforeWrite.Revision, true)));
            original = File.ReadAllBytes(Path.Combine(second, createdFolder, "written.cbz"));
            PrivateState();

            // SQLite failure occurs after publication; the verified original is rolled back without reimport.
            Sql("CREATE TRIGGER fail_file_move BEFORE UPDATE OF path ON books BEGIN SELECT RAISE(ABORT,'injected catalog failure'); END");
            preview = store.PreviewFileMove(id, new(0, "", "failure.cbz"));
            Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "failure.cbz", preview.Revision, true)));
            Check(!File.Exists(Path.Combine(root, createdFolder, "failure.cbz")) && File.ReadAllBytes(Path.Combine(second, createdFolder, "written.cbz")).SequenceEqual(original)
                && store.GetFileOperations(id).Count == 0 && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 0,
                "Catalog failure did not safely roll back and erase its journal.");
            Sql("DROP TRIGGER fail_file_move");
            PrivateState();

            // Retain a real journal reservation by failing both catalog commit and journal finalization.
            Sql("CREATE TRIGGER fail_file_move BEFORE UPDATE OF path ON books BEGIN SELECT RAISE(ABORT,'injected catalog failure'); END");
            Sql("CREATE TRIGGER fail_file_finish BEFORE DELETE ON file_operations BEGIN SELECT RAISE(ABORT,'injected journal failure'); END");
            preview = store.PreviewFileMove(id, new(0, "", "pending.cbz"));
            Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "pending.cbz", preview.Revision, true)));
            Sql("DROP TRIGGER fail_file_move; DROP TRIGGER fail_file_finish;");
            var pending = store.GetFileOperations(id).Single(row => row.State == "pending");
            Check(store.GetBook("owner", id).Availability == "unavailable", "Unresolved rollback remained available before restart.");
            SeedLegacyHistory(pending.Id);
            var pendingDestination = Path.Combine(root, createdFolder, "pending.cbz");
            var pendingSource = Path.Combine(second, createdFolder, "written.cbz");
            var hold = Path.Combine(second, createdFolder, $".panelhaven-move-{pending.Id}.hold");
            var partialStage = Path.Combine(root, createdFolder, $".panelhaven-move-{pending.Id}.stage");
            File.WriteAllText(partialStage, "partial copy; ownership was not published");
            var olderStage = Path.Combine(second, createdFolder, ".panelhaven-move-legacy-failed.stage");
            File.WriteAllText(olderStage, "legacy unverified copy");
            var blockedMigration = false;
            try { _ = new LibraryStore(database, roots); }
            catch (InvalidDataException exception) { blockedMigration = exception.Message.Contains(id) && exception.Message.Contains(olderStage); }
            Check(blockedMigration && File.Exists(olderStage) && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 4
                && (string)Sql("SELECT undo_of FROM file_operations WHERE id=$value", pending.Id)! == "legacy-applied",
                "Overlapping legacy recovery did not fail atomically with actionable staging details.");
            File.Delete(olderStage); // Explicit fixture-only quarantine of the older unrelated stage.
            store = new LibraryStore(database, roots);
            var partialRecovery = store.PreviewFileRecovery(id, pending.Id);
            Check(!partialRecovery.CanApply && partialRecovery.Conflicts.Any(conflict => conflict.Contains(".panelhaven-move-" + pending.Id + ".stage")),
                "Unknown staging file did not block recovery with its location.");
            Reject<FileOperationConflictException>(() => store.RecoverFileOperation(id, pending.Id, new(partialRecovery.Revision, true)));
            store = new LibraryStore(database, roots);
            Check(File.Exists(partialStage) && File.Exists(pendingSource) && !File.Exists(pendingDestination)
                && store.GetFileOperations(id).Single().Id == pending.Id
                && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 1
                && store.GetBook("owner", id).Availability == "unavailable"
                && store.ScanAsync(default).GetAwaiter().GetResult().Added == 0,
                "Migration or partial-copy recovery lost pending legacy reversal, retained history, or imported staging.");
            SeedLegacyHistory(pending.Id);
            Sql("UPDATE file_operations SET state='failed' WHERE id=$value", pending.Id);
            store = new LibraryStore(database, roots);
            Check(store.GetFileOperations(id).Single().Id == pending.Id
                && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 1
                && !store.PreviewFileRecovery(id, pending.Id).CanApply && File.Exists(partialStage),
                "Migration erased a legacy canceled operation whose unverified staging file still needed inspection.");
            File.Delete(partialStage); // Explicit fixture-only manual quarantine simulation.
            File.Move(pendingSource, hold);
            File.Copy(hold, pendingDestination);
            File.SetUnixFileMode(pendingDestination, File.GetUnixFileMode(hold));
            // Model the durable staging inode recorded before publish; no production fault hooks are necessary.
            Sql("UPDATE file_operations SET stage_identity=$value WHERE state='pending' AND book_id=$id", Identity(pendingDestination));
            store = new LibraryStore(database, roots);
            Check(store.GetBook("owner", id).Availability == "unavailable", "Startup did not expose pending reservation.");
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0 && (long)Sql("SELECT COUNT(*) FROM books WHERE path=$value", pendingDestination)! == 0,
                "Restart scan imported a pending destination under a new ID.");
            Reject<FileOperationConflictException>(() => store.RemoveMissingBook(id));
            Reject<FileOperationConflictException>(() => store.PreviewFileMove(id, new(0, "", "other.cbz")));
            Reject<FileOperationConflictException>(() => store.PreviewArchiveWrite(id));
            var uncertainStage = Path.Combine(root, createdFolder, $".panelhaven-move-{pending.Id}.stage");
            File.WriteAllText(uncertainStage, "interrupted, unverified copy");
            Check(!store.PreviewFileRecovery(id, pending.Id).CanApply && File.Exists(uncertainStage),
                "Recovery deleted or accepted an unknown partial staging file.");
            File.Delete(uncertainStage); // Explicit fixture-only manual quarantine simulation.
            var recovery = store.PreviewFileRecovery(id, pending.Id);
            Check(recovery.CanApply && recovery.Operation == "recover"
                && recovery.Destination == "Library 2/Unknown Publisher/Unknown Series/written.cbz", "Recovery preview omitted its cancellation destination.");
            File.WriteAllBytes(pendingDestination, changed);
            Check(!store.PreviewFileRecovery(id, pending.Id).CanApply, "Recovery accepted a later external edit.");
            Reject<FileOperationConflictException>(() => store.RecoverFileOperation(id, pending.Id, new(recovery.Revision, true)));
            Check(File.ReadAllBytes(pendingDestination).SequenceEqual(changed) && File.Exists(hold), "Conflicted recovery deleted external edits or original hold.");
            File.WriteAllBytes(pendingDestination, original);
            recovery = store.PreviewFileRecovery(id, pending.Id);
            store.RecoverFileOperation(id, pending.Id, new(recovery.Revision, true));
            Check(!File.Exists(hold) && !File.Exists(pendingDestination) && File.ReadAllBytes(pendingSource).SequenceEqual(original)
                && store.GetFileOperations(id).Count == 0, "Explicit recovery did not cancel safely and erase its journal.");
            PrivateState();

            // An already committed move with interrupted journal finalization is completed, not reversed.
            Sql("CREATE TRIGGER fail_file_finish BEFORE DELETE ON file_operations BEGIN SELECT RAISE(ABORT,'injected journal failure'); END");
            preview = store.PreviewFileMove(id, new(0, "", "committed.cbz"));
            Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "committed.cbz", preview.Revision, true)));
            Sql("DROP TRIGGER fail_file_finish");
            pending = store.GetFileOperations(id).Single(row => row.State == "pending");
            Check(store.GetBook("owner", id).Availability == "unavailable", "Committed pending cleanup remained available before restart.");
            SeedLegacyHistory(pending.Id);
            store = new LibraryStore(database, roots);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0
                && store.GetFileOperations(id).Single().Id == pending.Id
                && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 1,
                "Migration lost committed legacy reversal, retained completed parents, or split identity.");
            recovery = store.PreviewFileRecovery(id, pending.Id);
            store.RecoverFileOperation(id, pending.Id, new(recovery.Revision, true));
            Check(store.GetFileOperations(id).Count == 0 && (long)Sql("SELECT COUNT(*) FROM file_operations")! == 0
                && File.Exists(Path.Combine(root, createdFolder, "committed.cbz")), "Committed recovery did not retain destination and erase its journal.");
            PrivateState();

            // Every supported container family and alias uses the same byte-preserving move protocol.
            foreach (var (fixture, extension) in new[] { ("", ".zip"), ("rar4.cbr", ".cbr"), ("rar5.cbr", ".rar"), ("solid.cb7", ".cb7"), ("nonsolid.cb7", ".7z"), ("plain.cbt", ".cbt"), ("directories.cbt", ".tar") })
            {
                var source = Path.Combine(root, "format" + extension);
                File.Copy(fixture.Length == 0 ? Path.Combine(root, createdFolder, "committed.cbz") : Path.Combine(AppContext.BaseDirectory, "fixtures", fixture), source);
                Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Container fixture did not import.");
                var formatId = store.ListBooks("owner", null, pageSize: 100).Items.Single(book => book.Location == "Library 1/format" + extension).Id;
                var bytes = File.ReadAllBytes(source);
                var firstPage = store.GetPage(formatId, 0).Bytes;
                var formatFolder = store.CreateFileOperationFolder(new(1, "") { BookId = formatId }).Folder;
                var formatPreview = store.PreviewFileMove(formatId, new(1, "", "format" + extension));
                store.ApplyFileMove(formatId, new(1, "", "format" + extension, formatPreview.Revision, true));
                Check(File.ReadAllBytes(Path.Combine(second, formatFolder, "format" + extension)).SequenceEqual(bytes), "Container move changed bytes.");
                var returnMove = store.PreviewFileMove(formatId, new(0, "", "format" + extension));
                store.ApplyFileMove(formatId, new(0, "", "format" + extension, returnMove.Revision, true));
                Check(store.GetPage(formatId, 0).Bytes.SequenceEqual(firstPage) && store.GetFileOperations(formatId).Count == 0,
                    "Return move changed non-ZIP reader bytes or retained history.");
            }
            Check(!Directory.EnumerateFiles(root, ".panelhaven-move-*", SearchOption.AllDirectories).Any()
                && !Directory.EnumerateFiles(second, ".panelhaven-move-*", SearchOption.AllDirectories).Any(), "Completed operations left staging scaffolds.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
            if (cross != second) Directory.Delete(cross, true);
        }
    }
}
