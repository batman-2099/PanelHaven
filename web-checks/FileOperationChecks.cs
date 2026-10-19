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
        Directory.CreateDirectory(Path.Combine(root, "nested"));
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
            FileOperationResult Move(int target, string folder, string filename)
            {
                var preview = store.PreviewFileMove(id, new(target, folder, filename));
                Check(preview.CanApply, "Move preview unexpectedly conflicted: " + string.Join("; ", preview.Conflicts));
                return store.ApplyFileMove(id, new(target, folder, filename, preview.Revision, true));
            }
            void Undo(string operation)
            {
                var preview = store.PreviewFileUndo(id, operation);
                Check(preview.CanApply, "Undo preview unexpectedly conflicted.");
                store.UndoFileOperation(id, operation, new(preview.Revision, true));
            }
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

            const string createdFolder = "created/nested";
            var createdPath = Path.Combine(root, createdFolder);
            Reject<FileOperationConflictException>(() => store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz")));
            Check(!Directory.Exists(Path.Combine(root, "created")) && File.ReadAllBytes(path).SequenceEqual(original)
                && store.GetFileOperations(id).Count == 0, "Preview created a missing folder or changed archive/history.");
            Check(store.CreateFileOperationFolder(new(0, "created//nested/")).Folder == createdFolder && Directory.Exists(createdPath),
                "Explicit folder creation did not create nested folders or return their canonical relative path.");
            File.SetUnixFileMode(createdPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Check(store.CreateFileOperationFolder(new(0, createdFolder)).Folder == createdFolder
                && File.GetUnixFileMode(createdPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                && store.CreateFileOperationFolder(new(0, "")).Folder == "",
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
            foreach (var folder in new[] { "state/new", "state/archive-backups/new" })
                Reject<UnauthorizedAccessException>(() => privateRootStore.CreateFileOperationFolder(new(0, folder)));
            Check(!Directory.Exists(Path.Combine(directory, "state", "new"))
                && !Directory.Exists(Path.Combine(directory, "state", "archive-backups", "new")),
                "Folder creation entered application state or backup storage.");
            Check(File.ReadAllBytes(path).SequenceEqual(original) && !Directory.EnumerateFileSystemEntries(createdPath).Any()
                && store.GetFileOperations(id).Count == 0 && store.GetBook("owner", id).Location == "Library 1/original.cbz",
                "Creating folders overwrote an archive, moved it, or added move history.");

            var missingRoot = Path.Combine(directory, "missing", "library");
            var missingRootStore = new LibraryStore(folderDatabase, [missingRoot]);
            Reject<IOException>(() => missingRootStore.CreateFileOperationFolder(new(0, "child")));
            Check(!Directory.Exists(Path.Combine(directory, "missing")), "Folder creation created a missing library root or its ancestors.");

            var preview = store.PreviewFileMove(id, new(0, createdFolder, "renamed.cbz"));
            Check(preview.Source == "Library 1/original.cbz" && preview.Destination == "Library 1/created/nested/renamed.cbz"
                && preview.SourceHash == Convert.ToHexString(SHA256.HashData(original))
                && File.ReadAllBytes(path).SequenceEqual(original) && store.GetFileOperations(id).Count == 0
                && !Directory.EnumerateFileSystemEntries(createdPath).Any(),
                "Preview was inexact or mutated filesystem/history.");
            Reject<ArgumentException>(() => store.ApplyFileMove(id, new(0, createdFolder, "renamed.cbz", preview.Revision, false)));
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, createdFolder, "renamed.cbz", "stale", true)));
            Check(!store.PreviewFileMove(id, new(0, "", "original.cbz")).CanApply, "No-op preview was applicable.");
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
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), second);
            Reject<UnauthorizedAccessException>(() => store.PreviewFileMove(id, new(0, "alias", "bad.cbz")));
            Reject<UnauthorizedAccessException>(() => store.CreateFileOperationFolder(new(0, "alias/escaped")));
            var linkedRootStore = new LibraryStore(folderDatabase, [Path.Combine(root, "alias")]);
            Reject<UnauthorizedAccessException>(() => linkedRootStore.CreateFileOperationFolder(new(0, "escaped")));
            Directory.CreateDirectory(Path.Combine(second, "inside"));
            var linkedAncestorStore = new LibraryStore(folderDatabase, [Path.Combine(root, "alias", "inside")]);
            Reject<UnauthorizedAccessException>(() => linkedAncestorStore.CreateFileOperationFolder(new(0, "escaped")));
            Check(!Directory.Exists(Path.Combine(second, "inside", "escaped")), "Folder creation followed a symlink ancestor of its root.");
            Check(!Directory.Exists(Path.Combine(second, "escaped")), "Folder creation followed a symlink outside its root.");
            File.CreateSymbolicLink(Path.Combine(root, "occupied.cbz"), Path.Combine(second, "missing.cbz"));
            Check(!store.PreviewFileMove(id, new(0, "", "occupied.cbz")).CanApply, "Dangling destination symlink was not a collision.");
            Reject<UnauthorizedAccessException>(() => store.CreateFileOperationFolder(new(0, "occupied.cbz/escaped")));
            Check(!Directory.Exists(Path.Combine(second, "missing.cbz")), "Folder creation followed a dangling symlink.");
            File.Delete(Path.Combine(root, "occupied.cbz"));
            Directory.Delete(Path.Combine(root, "alias"));
            File.Copy(path, Path.Combine(second, "collision.cbz"));
            Check(!store.PreviewFileMove(id, new(1, "", "collision.cbz")).CanApply, "Equal bytes at destination were not a collision.");
            store.ScanAsync(default).GetAwaiter().GetResult();
            File.Delete(Path.Combine(second, "collision.cbz"));
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
            var first = Move(0, createdFolder, "renamed.cbz");
            Check(!File.Exists(path) && File.ReadAllBytes(Path.Combine(createdPath, "renamed.cbz")).SequenceEqual(original), "Same-device rename into a created folder lost or rewrote bytes.");
            Check(Identity(Path.Combine(createdPath, "renamed.cbz")) == originalIdentity, "Same-device rename unnecessarily copied the inode.");
            PrivateState();
            var secondMove = Move(1, "", "moved.cbz");
            Check(store.GetBook("owner", id).Location == "Library 2/moved.cbz", "Cross-root location was not refreshed.");
            Check(!store.PreviewFileUndo(id, first.OperationId).CanApply, "Older move was undoable before its descendant.");
            var thirdMove = Move(roots.Length - 1, "", "cross-device.cbz");
            Check(File.GetUnixFileMode(Path.Combine(roots[^1], "cross-device.cbz")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead), "Cross-device copy lost permissions.");
            PrivateState();
            File.WriteAllBytes(Path.Combine(second, "moved.cbz"), original);
            Check(!store.PreviewFileUndo(id, thirdMove.OperationId).CanApply, "Undo would overwrite an occupied original path.");
            File.Delete(Path.Combine(second, "moved.cbz"));
            Undo(thirdMove.OperationId);
            Undo(secondMove.OperationId);
            Undo(first.OperationId);
            PrivateState();
            Check(File.ReadAllBytes(path).SequenceEqual(original) && !Directory.EnumerateFileSystemEntries(createdPath).Any()
                && store.GetFileOperations(id).Count(row => row.State == "undone") == 3,
                "Undo chain did not restore original bytes/path or durable history.");
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0, "Scan split identity after moves/undo.");

            // A pre-move original-backup manifest remains immutable and usable at the new path.
            var write = store.PreviewArchiveWrite(id);
            var backup = store.ApplyArchiveWrite(id, new(write.Revision, true)).BackupId!;
            var manifest = Path.Combine(directory, "state", "archive-backups", id, backup, "manifest.json");
            var manifestBytes = File.ReadAllBytes(manifest);
            var backupMove = Move(1, "", "with-backup.cbz");
            var chainMove = Move(0, "nested", "backup-chain.cbz");
            Sql("UPDATE file_operations SET created_at='2000-01-01T00:00:00Z' WHERE id=$value", chainMove.OperationId);
            Check(store.PreviewArchiveRestore(id, backup).CanApply, "Backup provenance broke across a move chain or backward clock.");
            Undo(chainMove.OperationId);
            var restore = store.PreviewArchiveRestore(id, backup);
            Check(store.GetArchiveBackups(id).Any(item => item.Id == backup) && restore.CanApply, "Prior archive backup was unusable after move.");
            store.RestoreArchiveBackup(id, backup, new(restore.Revision, true));
            Check(File.ReadAllBytes(Path.Combine(second, "with-backup.cbz")).SequenceEqual(original)
                && File.ReadAllBytes(manifest).SequenceEqual(manifestBytes), "Archive backup restore changed manifest or failed original-byte restoration.");
            Check(!store.PreviewFileUndo(id, backupMove.OperationId).CanApply, "Move undo accepted a changed archive hash.");
            PrivateState();

            // SQLite failure occurs after publication; the verified original is rolled back without reimport.
            Sql("CREATE TRIGGER fail_file_move BEFORE UPDATE OF path ON books BEGIN SELECT RAISE(ABORT,'injected catalog failure'); END");
            preview = store.PreviewFileMove(id, new(0, "", "failure.cbz"));
            Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "failure.cbz", preview.Revision, true)));
            Check(!File.Exists(Path.Combine(root, "failure.cbz")) && File.ReadAllBytes(Path.Combine(second, "with-backup.cbz")).SequenceEqual(original)
                && store.GetFileOperations(id).Any(row => row.State == "failed"), "Catalog failure did not safely roll back filesystem state.");
            Sql("DROP TRIGGER fail_file_move");
            PrivateState();

            // Retain a real journal reservation by failing both catalog commit and journal finalization.
            Sql("CREATE TRIGGER fail_file_move BEFORE UPDATE OF path ON books BEGIN SELECT RAISE(ABORT,'injected catalog failure'); END");
            Sql("CREATE TRIGGER fail_file_finish BEFORE UPDATE OF state ON file_operations BEGIN SELECT RAISE(ABORT,'injected journal failure'); END");
            preview = store.PreviewFileMove(id, new(0, "", "pending.cbz"));
            Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "pending.cbz", preview.Revision, true)));
            Sql("DROP TRIGGER fail_file_move; DROP TRIGGER fail_file_finish;");
            var pending = store.GetFileOperations(id).Single(row => row.State == "pending");
            var pendingDestination = Path.Combine(root, "pending.cbz");
            var pendingSource = Path.Combine(second, "with-backup.cbz");
            var hold = Path.Combine(second, $".panelhaven-move-{pending.Id}.hold");
            var partialStage = Path.Combine(root, $".panelhaven-move-{pending.Id}.stage");
            File.WriteAllText(partialStage, "partial copy; ownership was not published");
            store = new LibraryStore(database, roots);
            var partialRecovery = store.PreviewFileRecovery(id, pending.Id);
            Check(partialRecovery.CanApply && partialRecovery.Message!.Contains("Library 1/.panelhaven-move-" + pending.Id + ".stage"),
                "An intact source could not cancel an interrupted copy while retaining its unknown staging file.");
            store.RecoverFileOperation(id, pending.Id, new(partialRecovery.Revision, true));
            store = new LibraryStore(database, roots);
            Check(File.Exists(partialStage) && File.Exists(pendingSource) && !File.Exists(pendingDestination)
                && store.GetFileOperations(id).Single(row => row.Id == pending.Id).Message!.Contains("retained untouched")
                && store.ScanAsync(default).GetAwaiter().GetResult().Added == 0,
                "Partial-copy cancellation lost bytes, omitted durable guidance, or imported its staging file.");
            PrivateState();
            File.Delete(partialStage); // Remove only this check's intentionally retained partial copy.
            Sql("UPDATE file_operations SET state='pending',message=NULL WHERE id=$value", pending.Id);
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
            var uncertainStage = Path.Combine(root, $".panelhaven-move-{pending.Id}.stage");
            File.WriteAllText(uncertainStage, "interrupted, unverified copy");
            Check(!store.PreviewFileRecovery(id, pending.Id).CanApply && File.Exists(uncertainStage),
                "Recovery deleted or accepted an unknown partial staging file.");
            File.Delete(uncertainStage); // Explicit fixture-only manual quarantine simulation.
            var recovery = store.PreviewFileRecovery(id, pending.Id);
            Check(recovery.CanApply && recovery.Operation == "recover" && recovery.Message!.StartsWith("Cancel")
                && recovery.Destination == "Library 2/with-backup.cbz", "Recovery preview omitted exact cancellation outcome.");
            File.WriteAllBytes(pendingDestination, changed);
            Check(!store.PreviewFileRecovery(id, pending.Id).CanApply, "Recovery accepted a later external edit.");
            Reject<FileOperationConflictException>(() => store.RecoverFileOperation(id, pending.Id, new(recovery.Revision, true)));
            Check(File.ReadAllBytes(pendingDestination).SequenceEqual(changed) && File.Exists(hold), "Conflicted recovery deleted external edits or original hold.");
            File.WriteAllBytes(pendingDestination, original);
            recovery = store.PreviewFileRecovery(id, pending.Id);
            store.RecoverFileOperation(id, pending.Id, new(recovery.Revision, true));
            Check(!File.Exists(hold) && !File.Exists(pendingDestination) && File.ReadAllBytes(pendingSource).SequenceEqual(original), "Explicit recovery did not cancel safely.");
            PrivateState();

            // An already committed move with interrupted journal finalization is completed, not reversed.
            Sql("CREATE TRIGGER fail_file_finish BEFORE UPDATE OF state ON file_operations BEGIN SELECT RAISE(ABORT,'injected journal failure'); END");
            preview = store.PreviewFileMove(id, new(0, "", "committed.cbz"));
            Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "committed.cbz", preview.Revision, true)));
            Sql("DROP TRIGGER fail_file_finish");
            pending = store.GetFileOperations(id).Single(row => row.State == "pending");
            store = new LibraryStore(database, roots);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0, "Committed pending path split identity on restart.");
            recovery = store.PreviewFileRecovery(id, pending.Id);
            Check(recovery.CanApply && recovery.Message!.StartsWith("Finish"), "Committed recovery did not explain finish outcome.");
            store.RecoverFileOperation(id, pending.Id, new(recovery.Revision, true));
            Check(store.GetFileOperations(id).Single(row => row.Id == pending.Id).State == "applied", "Committed recovery was not durable.");
            PrivateState();

            // Every supported container family and alias uses the same byte-preserving move protocol.
            foreach (var (fixture, extension) in new[] { ("", ".zip"), ("rar4.cbr", ".cbr"), ("rar5.cbr", ".rar"), ("solid.cb7", ".cb7"), ("nonsolid.cb7", ".7z"), ("plain.cbt", ".cbt"), ("directories.cbt", ".tar") })
            {
                var source = Path.Combine(root, "format" + extension);
                File.Copy(fixture.Length == 0 ? Path.Combine(root, "committed.cbz") : Path.Combine(AppContext.BaseDirectory, "fixtures", fixture), source);
                Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Container fixture did not import.");
                var formatId = store.ListBooks("owner", null, pageSize: 100).Items.Single(book => book.Location == "Library 1/format" + extension).Id;
                var bytes = File.ReadAllBytes(source);
                var firstPage = store.GetPage(formatId, 0).Bytes;
                var formatPreview = store.PreviewFileMove(formatId, new(1, "", "format" + extension));
                var moved = store.ApplyFileMove(formatId, new(1, "", "format" + extension, formatPreview.Revision, true));
                Check(File.ReadAllBytes(Path.Combine(second, "format" + extension)).SequenceEqual(bytes), "Container move changed bytes.");
                var undo = store.PreviewFileUndo(formatId, moved.OperationId);
                store.UndoFileOperation(formatId, moved.OperationId, new(undo.Revision, true));
                Check(store.GetPage(formatId, 0).Bytes.SequenceEqual(firstPage), "Moved/undone non-ZIP reader returned different page bytes.");
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
