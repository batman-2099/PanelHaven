using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class NfsFileOperationChecks
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetAttribute(string path, string name, byte[] value, nuint size, int flags);
    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetAttribute(string path, string name, byte[] value, nuint size);
    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int RemoveAttribute(string path, string name);

    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
        static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        static string Identity(string path) => (string)typeof(LibraryStore)
            .GetMethod("FileIdentity", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path])!;
        static object Security(string root, string path) => typeof(LibraryStore)
            .GetMethod("ArchiveWritePermissions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [root, path, true])!;
        static void Attribute(string path, string name, byte[] bytes) =>
            Check(SetAttribute(path, name, bytes, (nuint)bytes.Length, 0) == 0, $"Cannot set disposable {name}: errno {Marshal.GetLastPInvokeError()}.");
        static void Alias(string source, string destination) =>
            Check(Link(source, destination) == 0, $"Cannot create disposable alias: errno {Marshal.GetLastPInvokeError()}.");

        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-nfs-moves-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var cross = Directory.Exists("/dev/shm") ? Path.Combine("/dev/shm", "panelhaven-nfs-moves-" + Guid.NewGuid().ToString("N")) : null;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Unknown Publisher", "Unknown Series"));
        if (cross is not null) Directory.CreateDirectory(Path.Combine(cross, "Unknown Publisher", "Unknown Series"));
        var database = Path.Combine(directory, "state", "library.sqlite");
        try
        {
            var source = Path.Combine(root, "Unknown Publisher", "Unknown Series", "original.cbz");
            var destination = Path.Combine(root, "Unknown Publisher", "Unknown Series", "destination.cbz");
            var foreign = Path.Combine(root, "external-link");
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            using (var page = archive.CreateEntry("1.png").Open())
                page.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
            // Linux ACL xattr: owner rw, named uid 12345 r, group r, mask r, other none.
            var acl = Convert.FromHexString("0200000001000600FFFFFFFF020004003930000004000400FFFFFFFF10000400FFFFFFFF20000000FFFFFFFF");
            Attribute(source, "system.posix_acl_access", acl);
            Attribute(source, "user.panelhaven-check", [0, 255, 1, 0]);
            var bytes = File.ReadAllBytes(source);
            var security = Security(root, source);
            var identity = Identity(source);
            var store = new LibraryStore(database, cross is null ? [root] : [root, cross]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "ACL fixture was not imported.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            void Sql(string sql, string? operation = null, string? stageIdentity = null)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$operation", operation ?? "");
                command.Parameters.AddWithValue("$stage", (object?)stageIdentity ?? DBNull.Value);
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$destination", destination);
                command.ExecuteNonQuery();
            }
            void Original(string path)
            {
                var readAcl = new byte[acl.Length];
                Check(File.ReadAllBytes(path).SequenceEqual(bytes) && Identity(path) == identity
                    && Security(root, path).Equals(security)
                    && GetAttribute(path, "system.posix_acl_access", readAcl, (nuint)readAcl.Length) == readAcl.Length
                    && readAcl.SequenceEqual(acl), "Move/recovery changed bytes, inode, mode, owner/group, or exact ACL values.");
            }
            void MoveBack()
            {
                var preview = store.PreviewFileMove(id, new(0, "", "original.cbz"));
                Check(preview.CanApply, "ACL return move was refused.");
                store.ApplyFileMove(id, new(0, "", "original.cbz", preview.Revision, true));
            }
            string Journal()
            {
                var preview = store.PreviewFileMove(id, new(0, "", "destination.cbz"));
                Check(preview.CanApply, "ACL move was refused.");
                store.ApplyFileMove(id, new(0, "", "destination.cbz", preview.Revision, true));
                Check(!File.Exists(source) && File.ReadAllBytes(destination).SequenceEqual(bytes), "Move lost its only archive.");
                MoveBack();
                Check(store.GetFileOperations(id).Count == 0, "Completed ACL moves retained history.");
                Sql("""
                    CREATE TRIGGER fail_move BEFORE UPDATE OF path ON books BEGIN SELECT RAISE(ABORT,'injected'); END;
                    CREATE TRIGGER fail_finish BEFORE DELETE ON file_operations BEGIN SELECT RAISE(ABORT,'injected'); END;
                    """);
                preview = store.PreviewFileMove(id, new(0, "", "destination.cbz"));
                Reject<SqliteException>(() => store.ApplyFileMove(id, new(0, "", "destination.cbz", preview.Revision, true)));
                Sql("""
                    DROP TRIGGER fail_move; DROP TRIGGER fail_finish;
                    DROP TABLE IF EXISTS recovery_fixture;
                    CREATE TEMP TABLE recovery_fixture AS SELECT * FROM file_operations;
                    """);
                return store.GetFileOperations(id).Single().Id;
            }
            var preview = store.PreviewFileMove(id, new(0, "", "destination.cbz"));
            File.WriteAllText(destination, "external destination");
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, "", "destination.cbz", preview.Revision, true)));
            Check(File.ReadAllText(destination) == "external destination", "Move overwrote a destination collision.");
            File.Delete(destination);
            Alias(source, foreign);
            Reject<UnauthorizedAccessException>(() => store.PreviewFileMove(id, new(0, "", "destination.cbz")));
            File.Delete(foreign);
            var changedAcl = acl.ToArray();
            changedAcl[14] = 0; // Named-user permissions only: mask and stat mode stay unchanged.
            preview = store.PreviewFileMove(id, new(0, "", "destination.cbz"));
            Attribute(source, "system.posix_acl_access", changedAcl);
            Reject<FileOperationConflictException>(() => store.ApplyFileMove(id, new(0, "", "destination.cbz", preview.Revision, true)));
            Attribute(source, "system.posix_acl_access", acl);
            Original(source);
            if (cross is not null)
            {
                Reject<FileOperationConflictException>(() => store.PreviewFileMove(id, new(1, "", "cross.cbz")));
                Check(!File.Exists(Path.Combine(cross, "Unknown Publisher", "Unknown Series", "cross.cbz")), "Unsupported ACL transfer touched its destination.");
            }

            var operation = Journal();
            Original(source);
            var hold = Path.Combine(Path.GetDirectoryName(source)!, $".panelhaven-move-{operation}.hold");
            var stage = Path.Combine(root, "Unknown Publisher", "Unknown Series", $".panelhaven-move-{operation}.stage");
            void Pending(bool committed = false, string? stageIdentity = null)
            {
                Sql("INSERT OR IGNORE INTO file_operations SELECT * FROM recovery_fixture;");
                Sql("UPDATE file_operations SET state='pending',committed=0,stage_identity=COALESCE($stage,source_identity) WHERE id=$operation; "
                    + "UPDATE books SET path=$source,availability='unavailable' WHERE id=$id;", operation, stageIdentity);
                if (committed) Sql("UPDATE file_operations SET committed=1 WHERE id=$operation; UPDATE books SET path=$destination WHERE id=$id;", operation);
                store = new LibraryStore(database, cross is null ? [root] : [root, cross]);
            }
            void Recover()
            {
                var recovery = store.PreviewFileRecovery(id, operation);
                Check(recovery.CanApply, "Verified alias recovery was refused: " + string.Join("; ", recovery.Conflicts));
                store.RecoverFileOperation(id, operation, new(recovery.Revision, true));
                Check(!File.Exists(hold) && !File.Exists(stage) && store.GetFileOperations(id).Count == 0,
                    "Recovery retained a verified redundant alias or completed journal.");
            }

            Pending();
            Alias(source, hold); // Crash after source -> hold link, before unlinking source.
            var recovery = store.PreviewFileRecovery(id, operation);
            Check(recovery.CanApply, "Source+hold was not recoverable.");
            Alias(source, destination);
            Check(store.PreviewFileRecovery(id, operation).CanApply, "Accounted restoration aliases were not recoverable.");
            Reject<FileOperationConflictException>(() => store.RecoverFileOperation(id, operation, new(recovery.Revision, true)));
            Check(File.Exists(source) && File.Exists(hold) && File.Exists(destination), "Stale topology preview removed aliases.");
            File.Delete(destination);
            Alias(source, foreign);
            Check(!store.PreviewFileRecovery(id, operation).CanApply, "Recovery accepted an external hard link.");
            Reject<FileOperationConflictException>(() => store.RecoverFileOperation(id, operation, new(recovery.Revision, true)));
            File.Delete(foreign);
            Attribute(source, "system.posix_acl_access", changedAcl);
            Check(!store.PreviewFileRecovery(id, operation).CanApply, "Recovery accepted changed ACL values with unchanged mode.");
            Reject<FileOperationConflictException>(() => store.RecoverFileOperation(id, operation, new(recovery.Revision, true)));
            Check(File.Exists(source) && File.Exists(hold), "ACL conflict removed an original alias.");
            Attribute(source, "system.posix_acl_access", acl);
            Recover();
            Original(source);

            Pending();
            File.Move(source, hold);
            Alias(hold, destination); // Crash after hold -> destination link.
            Recover();
            Original(source);
            Check(!File.Exists(destination), "Cancellation retained a destination alias.");

            Pending();
            File.Move(source, destination);
            Alias(destination, source); // Crash while restoring destination -> source.
            Recover();
            Original(source);

            Pending();
            File.Move(source, destination);
            Alias(destination, hold);
            Pending(committed: true);
            Recover();
            Original(destination);
            MoveBack();
            Original(source);

            // Old journals have no xattr field. Cross-mount copies still use distinct, verified inodes.
            Check(RemoveAttribute(source, "system.posix_acl_access") == 0 && RemoveAttribute(source, "user.panelhaven-check") == 0,
                "Cannot remove fixture attributes.");
            if (cross is not null)
            {
                Attribute(Path.Combine(cross, "Unknown Publisher", "Unknown Series"), "system.posix_acl_default", acl);
                Reject<FileOperationConflictException>(() => store.PreviewFileMove(id, new(1, "", "cross.cbz")));
                Check(RemoveAttribute(Path.Combine(cross, "Unknown Publisher", "Unknown Series"), "system.posix_acl_default") == 0, "Cannot remove fixture directory ACL.");
                var crossPreview = store.PreviewFileMove(id, new(1, "", "cross.cbz"));
                store.ApplyFileMove(id, new(1, "", "cross.cbz", crossPreview.Revision, true));
                Check(File.ReadAllBytes(Path.Combine(cross, "Unknown Publisher", "Unknown Series", "cross.cbz")).SequenceEqual(bytes), "Plain cross-mount move changed bytes.");
                MoveBack();
            }
            operation = Journal();
            hold = Path.Combine(Path.GetDirectoryName(source)!, $".panelhaven-move-{operation}.hold");
            stage = Path.Combine(root, "Unknown Publisher", "Unknown Series", $".panelhaven-move-{operation}.stage");
            Sql("UPDATE file_operations SET security=json_remove(security,'$.ExtendedAttributesHash') WHERE id=$operation; "
                + "UPDATE recovery_fixture SET security=json_remove(security,'$.ExtendedAttributesHash');", operation);
            Pending();
            Alias(source, hold);
            Recover();
            Check(File.ReadAllBytes(source).SequenceEqual(bytes), "Old journal recovery changed bytes.");

            foreach (var committed in new[] { false, true })
            {
                File.Copy(source, stage);
                File.SetUnixFileMode(stage, File.GetUnixFileMode(source));
                File.SetLastWriteTimeUtc(stage, File.GetLastWriteTimeUtc(source));
                var stageIdentity = Identity(stage);
                File.Move(source, hold);
                Alias(stage, destination); // Cross-mount publication interrupted before stage unlink.
                Pending(committed, stageIdentity);
                Recover();
                Check(File.ReadAllBytes(committed ? destination : source).SequenceEqual(bytes), "Stage+destination recovery lost bytes.");
                if (committed) MoveBack();
                else Check(!File.Exists(destination), "Canceled copied publication retained its destination.");
            }
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0, "Recovery split catalog identity.");
            Check(!Directory.EnumerateFiles(root, ".panelhaven-move-*", SearchOption.AllDirectories).Any(), "Completed recovery left journal aliases.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
            if (cross is not null) Directory.Delete(cross, true);
        }
    }
}
