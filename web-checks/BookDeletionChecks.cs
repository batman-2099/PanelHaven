using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

internal static class BookDeletionChecks
{
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(string path, string name, byte[] value, nuint size, int flags);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string destination);

    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-book-deletion-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var intake = Path.Combine(directory, "intake");
        var state = Path.Combine(directory, "state");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(intake);
        var database = Path.Combine(state, "library.sqlite");
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            var paths = Enumerable.Range(0, 4).Select(index => Path.Combine(root, $"book{index}.cbz"))
                .Append(Path.Combine(intake, "intake.cbz")).ToArray();
            for (var index = 0; index < paths.Length; index++)
            {
                using var archive = ZipFile.Open(paths[index], ZipArchiveMode.Create);
                archive.Comment = "stable-A";
                for (var page = 0; page < 2; page++)
                {
                    using var image = archive.CreateEntry($"{page}.png").Open();
                    image.Write(png);
                }
                using var xml = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                xml.Write($"<ComicInfo><Title>Delete {index}</Title><Series>Series</Series><Number>{index}</Number></ComicInfo>");
            }
            var store = new LibraryStore(database, [root], unsortedPath: intake);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 5, "Deletion fixture did not import.");
            var ids = store.ListBooks("owner", null).Items.Concat(store.ListBooks("owner", null, location: "unsorted").Items)
                .OrderBy(book => book.Title, StringComparer.Ordinal).Select(book => book.Id).ToArray();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            object? Sql(string sql, params (string Name, object? Value)[] values)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            BookDeletePreview Preview(params string[] selected) => store.PreviewBookDeletion(new(selected));
            BookDeleteResult Apply(BookDeletePreview preview, CancellationToken token = default) => store.DeleteBooks(
                new(preview.Items.Select(item => item.BookId).ToArray(), preview.Revision, true), token);
            void UnchangedSources() => Check(paths.Take(4).All(File.Exists), "Preflight deleted an earlier selected source.");

            // Actual immutable originals, including an intake operation whose source has already been consumed.
            store.EditMetadata(ids[0], new(new() { ["Title"] = "Written title" }));
            var write = store.PreviewArchiveWrite(ids[0]);
            store.ApplyArchiveWrite(ids[0], new(write.Revision, true));
            var metadata = new ComicVineMetadata(new() { ["Series"] = "Processed", ["Number"] = "1", ["Title"] = "Accepted" },
                "https://comicvine.gamespot.com/check/4000-1/");
            var prepared = store.PrepareIntake(ids[4], new(1, 2, 0), metadata);
            Check(store.ProcessIntake(ids[4], new(1, 2, 0, prepared.Revision, true), metadata).State == "completed", "Intake fixture did not complete.");
            paths[4] = (string)Sql("SELECT path FROM books WHERE id=$id", ("$id", ids[4]))!;
            var backups = Directory.EnumerateFiles(Path.Combine(state, "archive-backups"), "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(state, "intake-originals"), "*", SearchOption.AllDirectories))
                .ToDictionary(path => path, File.ReadAllBytes);
            var move = store.PreviewFileMove(ids[0], new(0, "", "moved.cbz"));
            var moved = store.ApplyFileMove(ids[0], new(0, "", "moved.cbz", move.Revision, true));
            var undo = store.PreviewFileUndo(ids[0], moved.OperationId);
            var undone = store.UndoFileOperation(ids[0], moved.OperationId, new(undo.Revision, true));

            foreach (var user in new[] { "owner", "other" })
            {
                var list = store.CreateList(user, new("Keep list", "manual", "all", []));
                store.AddListBooks(user, list.Id, ids);
                foreach (var id in ids)
                {
                    store.SaveProgress(user, id, new(1, true));
                    store.SaveBookmark(user, id, 0, new("Private bookmark"));
                    store.SaveRating(user, id, new(4));
                }
            }
            foreach (var id in ids)
            {
                store.SavePageOrder(id, new(store.GetPageOrder(id).Revision, [1, 0]));
                var pages = new[] { new PageMetadataRow(0, "FrontCover", "Shared bookmark") };
                var pageEdit = store.PreviewPageMetadata(id, new(store.GetPageMetadata(id).Revision, pages));
                store.EditPageMetadata(id, new(pageEdit.Revision, pages));
            }
            var selected = new[] { ids[0], ids[1], ids[2], ids[4] };
            var preview = Preview(selected);
            Check(preview.CanDelete && preview.Items.All(item => item.CanDelete && item.Error is null
                && !item.Source.Contains(directory) && !Path.IsPathRooted(item.Source)), "Preview denied valid selection or leaked absolute paths.");
            Check(preview.Items[0].Title == "Written title" && preview.Items[3].Title == "Accepted", "Preview omitted exact catalog titles.");
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var json in new[] { "{}", "{\"bookIds\":[],\"extra\":true}", "{\"bookIds\":[1]}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<BookDeletePreviewRequest>(json, options));
            foreach (var json in new[] { "{\"bookIds\":[],\"expectedRevision\":\"x\"}", "{\"bookIds\":[],\"expectedRevision\":\"x\",\"confirmed\":\"true\"}",
                "{\"bookIds\":[],\"expectedRevision\":\"x\",\"confirmed\":true,\"extra\":1}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<BookDeleteRequest>(json, options));
            foreach (var invalid in new string[][] { [], null!, [" "], [null!], [ids[0], ids[0]], Enumerable.Range(0, 101).Select(index => index.ToString()).ToArray() })
            {
                Reject<ArgumentException>(() => store.PreviewBookDeletion(new(invalid)));
                Reject<ArgumentException>(() => store.DeleteBooks(new(invalid, preview.Revision, true)));
            }
            Reject<ArgumentException>(() => store.DeleteBooks(new(selected, preview.Revision, false)));
            Reject<FileOperationConflictException>(() => store.DeleteBooks(new(selected, "", true)));
            Reject<FileOperationConflictException>(() => Apply(preview with { Revision = "stale" }));
            Reject<FileOperationConflictException>(() => store.DeleteBooks(new(selected.Reverse().ToArray(), preview.Revision, true)));
            Check(!Preview(ids[0], "missing").CanDelete, "Unknown selected ID was silently omitted.");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Reject<OperationCanceledException>(() => Apply(preview, canceled.Token));
            }
            store.EditMetadata(ids[2], new(new() { ["Summary"] = "Changed after preview" }));
            Reject<FileOperationConflictException>(() => Apply(preview));
            preview = Preview(selected);
            store.SavePageOrder(ids[2], new(store.GetPageOrder(ids[2]).Revision, [0, 1]));
            Reject<FileOperationConflictException>(() => Apply(preview));
            UnchangedSources();

            var source = paths[2];
            var original = File.ReadAllBytes(source);
            var timestamp = File.GetLastWriteTimeUtc(source);
            preview = Preview(selected);
            var changed = original.ToArray();
            changed[^1] ^= 1;
            File.WriteAllBytes(source, changed);
            File.SetLastWriteTimeUtc(source, timestamp);
            Reject<FileOperationConflictException>(() => Apply(preview));
            File.WriteAllBytes(source, original);
            File.SetLastWriteTimeUtc(source, timestamp);
            preview = Preview(selected);
            var held = Path.Combine(directory, "held.cbz");
            File.Move(source, held);
            File.WriteAllBytes(source, original);
            File.SetLastWriteTimeUtc(source, timestamp);
            Reject<FileOperationConflictException>(() => Apply(preview));
            File.Delete(source);
            File.Move(held, source);
            var mode = File.GetUnixFileMode(source);
            preview = Preview(selected);
            File.SetUnixFileMode(source, UnixFileMode.UserRead);
            Check(!Preview(selected).CanDelete, "Read-only source was deletable.");
            Reject<FileOperationConflictException>(() => Apply(preview));
            File.SetUnixFileMode(source, mode);
            Check(SetXattr(source, "user.panelhaven.delete", [1], 1, 0) == 0, "Cannot create disposable source attribute.");
            preview = Preview(selected);
            Check(preview.CanDelete, "An arbitrary source attribute blocked deletion rather than being fingerprinted.");
            Check(SetXattr(source, "user.panelhaven.delete", [2], 1, 0) == 0, "Cannot change disposable source attribute.");
            Reject<FileOperationConflictException>(() => Apply(preview));
            preview = Preview(selected);
            Check(SetXattr(root, "user.panelhaven.delete", [1], 1, 0) == 0, "Cannot change disposable parent security.");
            Reject<FileOperationConflictException>(() => Apply(preview));
            preview = Preview(selected);
            File.Move(source, held);
            File.CreateSymbolicLink(source, paths[3]);
            Check(!Preview(selected).CanDelete, "Symlink source was deletable.");
            Reject<FileOperationConflictException>(() => Apply(preview));
            File.Delete(source);
            File.Move(held, source);
            var alias = Path.Combine(directory, "alias.cbz");
            Check(Link(source, alias) == 0 && !Preview(selected).CanDelete, "Hard-link alias was deletable.");
            File.Delete(alias);
            var offline = Path.Combine(directory, "offline");
            Directory.Move(root, offline);
            Directory.CreateSymbolicLink(root, offline);
            Check(!Preview(selected).CanDelete, "Symlink root was deletable.");
            Directory.Delete(root);
            Directory.Move(offline, root);
            Sql("UPDATE books SET path=$path WHERE id=$id", ("$path", Path.Combine(state, "foreign.cbz")), ("$id", ids[2]));
            File.WriteAllBytes(Path.Combine(state, "foreign.cbz"), original);
            Check(!Preview(selected).CanDelete, "Unconfigured state path was deletable.");
            Sql("UPDATE books SET path=$path WHERE id=$id", ("$path", source), ("$id", ids[2]));
            Check(!new LibraryStore(database, [intake]).PreviewBookDeletion(new(selected)).CanDelete, "Changed configured roots accepted an old source.");
            UnchangedSources();

            preview = Preview(selected);
            Sql("UPDATE file_operations SET state='pending' WHERE id=$id", ("$id", undone.OperationId));
            Check(!Preview(selected).CanDelete, "Pending move was deletable.");
            Reject<FileOperationConflictException>(() => Apply(preview));
            Sql("UPDATE file_operations SET state='applied' WHERE id=$id", ("$id", undone.OperationId));
            Sql("INSERT INTO intake_operations(id,book_id,source,destination,state,journal) VALUES('pending',$id,$source,$destination,'pending','{}')",
                ("$id", ids[3]), ("$source", paths[3]), ("$destination", source));
            Check(!Preview(selected).CanDelete && !Preview(ids[3]).CanDelete, "Pending intake owner or destination reservation was deletable.");
            Sql("DELETE FROM intake_operations WHERE id='pending'");
            UnchangedSources();

            // Corrupt/truncated regular sources can still be explicitly deleted; deletion need not parse a comicbook.
            File.WriteAllBytes(source, []);
            preview = Preview(selected);
            Check(preview.CanDelete, "Truncated but ordinary source could not be deleted.");
            var unrelated = File.ReadAllBytes(paths[3]);
            Sql($"CREATE TRIGGER fail_selected_delete BEFORE DELETE ON books WHEN OLD.id='{ids[1]}' BEGIN SELECT RAISE(ABORT,'injected'); END");
            using var disconnected = new CancellationTokenSource();
            using var seenDeletion = new ManualResetEventSlim();
            using var watcher = new FileSystemWatcher(root, Path.GetFileName(paths[0]));
            watcher.Deleted += (_, _) => { disconnected.Cancel(); seenDeletion.Set(); };
            watcher.EnableRaisingEvents = true;
            var result = Apply(preview, disconnected.Token);
            Check(seenDeletion.Wait(TimeSpan.FromSeconds(5)), "Deletion event was not observed.");
            watcher.EnableRaisingEvents = false;
            Check(result.DeletedIds.SequenceEqual([ids[0], ids[2], ids[4]]) && result.Failures.Count == 1
                && result.Failures[0].BookId == ids[1] && result.Failures[0].Error.Contains("Source file was deleted")
                && result.Failures[0].Error.Contains("Maintenance") && !result.Failures[0].Error.Contains(directory),
                "Partial deletion or client cancellation lost successful results or misreported an already-deleted source.");
            Check(selected.All(id => !File.Exists(paths[Array.IndexOf(ids, id)]))
                && File.ReadAllBytes(paths[3]).SequenceEqual(unrelated), "Deletion left selected files or changed an unrelated file.");
            Check(File.ReadAllBytes(Path.Combine(state, "foreign.cbz")).SequenceEqual(original), "Deletion removed an unrelated state file.");
            foreach (var id in result.DeletedIds)
            {
                Reject<KeyNotFoundException>(() => store.GetBook("owner", id));
                foreach (var table in new[] { "progress", "ratings", "bookmarks", "list_books", "page_orders", "page_metadata_edits", "file_operations", "intake_operations" })
                    Check((long)Sql($"SELECT COUNT(*) FROM {table} WHERE book_id=$id", ("$id", id))! == 0, "Deletion left dependent rows in " + table);
            }
            foreach (var id in new[] { ids[1], ids[3] })
                foreach (var user in new[] { "owner", "other" })
                    Check(store.GetBook(user, id).Completed && store.GetBook(user, id).PersonalRating == 4
                        && store.GetBookmarks(user, id).Single().Name == "Private bookmark", "Failed or unselected private state was lost.");
            Check((long)Sql("SELECT COUNT(*) FROM lists")! == 2 && (long)Sql("SELECT COUNT(*) FROM list_books")! == 4,
                "Deletion removed whole private lists or unrelated membership.");
            Check(store.GetPageOrder(ids[3]).Pages.SequenceEqual([1, 0]) && store.GetPageMetadata(ids[3]).Pages[0].Bookmark == "Shared bookmark",
                "Unselected shared reader state changed.");
            foreach (var backup in backups) Check(File.ReadAllBytes(backup.Key).SequenceEqual(backup.Value), "Deletion changed or removed a retained original/manifest.");
            Check(store.PreviewArchiveBackupExport().Backups == 1, "Deleted book's retained archive original was no longer exportable.");
            Sql("DROP TRIGGER fail_selected_delete");
            store.ScanAsync(default).GetAwaiter().GetResult();
            Check(store.GetBook("owner", ids[1]).Availability == "missing", "Failed catalog cleanup did not reconcile to missing.");
            store.RemoveMissingBook(ids[1]);
            Check((long)Sql("SELECT COUNT(*) FROM books")! == 1 && (long)Sql("SELECT COUNT(*) FROM progress")! == 2,
                "Missing-entry recovery removed unrelated state or retained failed deletion state.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
