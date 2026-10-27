using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class BulkFileOperationChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
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
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-bulk-files-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var target = Path.Combine(directory, "target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Publisher", "Series"));
        Directory.CreateDirectory(Path.Combine(target, "Publisher", "Series"));
        var database = Path.Combine(directory, "state", "library.sqlite");
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            for (var index = 1; index <= 3; index++)
            {
                using var archive = ZipFile.Open(Path.Combine(root, $"book{index}.cbz"), ZipArchiveMode.Create);
                archive.Comment = "stable-A";
                using (var stream = archive.CreateEntry("1.png").Open()) stream.Write(png);
                using var xml = archive.CreateEntry("ComicInfo.xml").Open();
                xml.Write(Encoding.UTF8.GetBytes($"<ComicInfo xmlns='urn:panelhaven-check'><Title>Title {index}</Title><Series xmlns=''>Series</Series><Number>{index}</Number><Year>2025</Year><Volume>2</Volume><Publisher>Publisher</Publisher><Unknown>keep</Unknown></ComicInfo>"));
            }
            var store = new LibraryStore(database, [root, target]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 3, "Bulk fixture did not import.");
            var books = store.ListBooks("owner", null).Items.OrderBy(book => book.Location, StringComparer.Ordinal).ToArray();
            var ids = books.Select(book => book.Id).ToArray();
            foreach (var id in ids) store.EditMetadata(id, new(new() { ["Series"] = "Series" }));
            var original = Enumerable.Range(1, 3).Select(index => File.ReadAllBytes(Path.Combine(root, $"book{index}.cbz"))).ToArray();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            db.Open();
            object? Sql(string sql)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            BulkFileMoveItem[] Items(BulkFileMovePreview preview) => preview.Items
                .Select(row => new BulkFileMoveItem(row.BookId, row.RootIndex, row.Folder, row.FileName)).ToArray();
            BulkFileMovePreview Preview(BulkFileMoveItem[] items) => store.PreviewFileMoves(new(items));
            BulkFileMoveResult Apply(BulkFileMovePreview preview) => store.ApplyFileMoves(new(Items(preview), preview.Revision, true));
            void Undo(FileOperationResult operation)
            {
                var preview = store.PreviewFileUndo(operation.BookId, operation.OperationId);
                Check(preview.CanApply, "Bulk move did not retain undo.");
                store.UndoFileOperation(operation.BookId, operation.OperationId, new(preview.Revision, true));
            }
            var drafts = ids.Select((id, index) => new BulkFileMoveItem(id, 1, "", $"moved{index + 1}.cbz")).ToArray();
            var templates = store.PreviewFileTemplates(new(ids, 1, "{Publisher}/{Series}", "{Series} ({Year}) v{Volume} #{Number} - {Title} - {FileName}"));
            Check(templates.CanApply && templates.Items.Count == 3 && templates.Items.All(row => row.Folder == "Publisher/Series")
                && templates.Items[0].FileName == "Series (2025) v2 #1 - Title 1 - book1.cbz"
                && templates.Items[0].Destination == "Library 2/Publisher/Series/Series (2025) v2 #1 - Title 1 - book1.cbz"
                && templates.Revision == Preview(Items(templates)).Revision,
                "Template expansion or explicit/template revision parity failed.");
            var exact = Preview([drafts[0]]);
            foreach (var folder in new[] { "custom/nested", "Publisher", "Publisher/Series", "Publisher/Series/custom" })
            {
                var normalized = Preview([drafts[0] with { Folder = folder }]);
                Check(normalized.CanApply && normalized.Items.Single().Folder == "Publisher/Series"
                    && normalized.Items.Single().Destination == exact.Items.Single().Destination && normalized.Revision == exact.Revision,
                    "Custom or already-prefixed folder changed the exact metadata destination.");
                var templated = store.PreviewFileTemplates(new([ids[0]], 1, folder, Path.GetFileNameWithoutExtension(drafts[0].FileName)));
                Check(templated.CanApply && templated.Revision == exact.Revision, "Custom template folder changed the exact metadata destination.");
            }
            Check((long)Sql("SELECT COUNT(*) FROM file_operations")! == 0 && File.ReadAllBytes(Path.Combine(root, "book1.cbz")).SequenceEqual(original[0]),
                "Template preview mutated archive or history.");
            foreach (var template in new[] { "{Unknown}", "{Series", "Series}", "{{Series}}", "{Writer}", "../bad", "", ".", "bad " })
            {
                var invalid = store.PreviewFileTemplates(new(ids, 1, "", template));
                Check(!invalid.CanApply && invalid.Items.Count == 3 && invalid.Items.All(row => row.Conflicts.Count > 0), "Invalid template did not produce every row conflict.");
            }
            store.EditMetadata(ids[1], new(new() { ["Publisher"] = null }));
            Check(!store.PreviewFileTemplates(new(ids, 1, "{Publisher}", "{FileName}")).Items[1].CanApply, "Missing metadata was silently replaced.");
            var fallback = store.PreviewFileTemplates(new([ids[1]], 1, "", "{FileName}"));
            Check(!fallback.CanApply && fallback.Items.Single().Folder == "Unknown Publisher/Series"
                && !Directory.Exists(Path.Combine(target, "Unknown Publisher")), "Missing publisher preview created its destination folder.");
            Directory.CreateDirectory(Path.Combine(target, "Unknown Publisher", "Series"));
            fallback = store.PreviewFileTemplates(new([ids[1]], 1, "", "{FileName}"));
            Check(fallback.CanApply && fallback.Items.Single().Folder == "Unknown Publisher/Series"
                && fallback.Items.Single().Destination == "Library 2/Unknown Publisher/Series/book2.cbz"
                && fallback.Revision == Preview(Items(fallback)).Revision, "Implicit missing publisher did not use the normalized fallback.");
            Check(!store.PreviewFileTemplates(new([ids[1]], 1, "{Publisher}", "{FileName}")).CanApply,
                "Explicit missing publisher token bypassed template validation.");
            store.EditMetadata(ids[1], new(new() { ["Publisher"] = "../escape" }));
            Check(!store.PreviewFileTemplates(new(ids, 1, "{Publisher}", "{FileName}")).Items[1].CanApply, "Token injected a directory separator.");
            store.EditMetadata(ids[1], new(new() { ["Publisher"] = "Publisher", ["Year"] = "-1" }));
            Check(!store.PreviewFileTemplates(new(ids, 1, "", "{Year}-{FileName}")).Items[1].CanApply, "Unknown numeric metadata became a name.");
            store.EditMetadata(ids[1], new(new() { ["Year"] = "2025" }));
            store.EditMetadata(ids[1], new(new() { ["Series"] = null }));
            var missingSeries = Preview([drafts[1]]);
            Check(!missingSeries.CanApply && missingSeries.Items.Single().Folder == "Publisher/Unknown Series"
                && !Directory.Exists(Path.Combine(target, "Publisher", "Unknown Series")), "Missing series preview created its fallback folder.");
            Directory.CreateDirectory(Path.Combine(target, "Publisher", "Unknown Series"));
            Check(Preview([drafts[1]]).CanApply
                && !store.PreviewFileTemplates(new([ids[1]], 1, "{Series}", "{FileName}")).CanApply,
                "Missing series fallback or explicit template validation failed.");
            store.EditMetadata(ids[1], new(new() { ["Series"] = "Series" }));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var json in new[] { "{}", "{\"items\":[{\"bookId\":\"id\",\"rootIndex\":\"0\",\"folder\":\"\",\"fileName\":\"x.cbz\"}]}",
                "{\"items\":[{\"bookId\":\"id\",\"rootIndex\":0,\"folder\":\"\"}]}", "{\"items\":[],\"extra\":true}" })
                Reject<JsonException>(() => JsonSerializer.Deserialize<BulkFileMoveRequest>(json, options));
            Reject<JsonException>(() => JsonSerializer.Deserialize<BulkFileMoveApplyRequest>("{\"items\":[],\"expectedRevision\":\"x\"}", options));
            Reject<JsonException>(() => JsonSerializer.Deserialize<FileTemplateRequest>("{\"bookIds\":[],\"rootIndex\":\"0\",\"folderTemplate\":\"\",\"fileNameTemplate\":\"{FileName}\"}", options));
            Reject<ArgumentException>(() => Preview([]));
            Reject<ArgumentException>(() => Preview([drafts[0], drafts[0]]));
            Reject<ArgumentException>(() => Preview(Enumerable.Repeat(drafts[0], 101).ToArray()));
            Reject<ArgumentException>(() => Preview([drafts[0] with { Folder = null! }]));
            Check(!Preview([drafts[0] with { BookId = "missing" }]).CanApply, "Missing book did not produce a row conflict.");
            foreach (var invalid in new[] { drafts[0] with { RootIndex = 99 }, drafts[0] with { Folder = "../books" },
                drafts[0] with { Folder = root }, drafts[0] with { FileName = "bad.cbr" } })
            {
                var row = Preview([invalid]).Items.Single();
                Check(!row.CanApply && row.Conflicts.All(message => !message.Contains(directory)), "Invalid destination leaked an absolute path or was accepted.");
            }
            var collision = Preview([drafts[0] with { Folder = "custom-a" }, drafts[1] with { Folder = "custom-b", FileName = drafts[0].FileName }]);
            Check(!collision.CanApply && collision.Items.All(row => !row.CanApply && row.Folder == "Publisher/Series")
                && collision.Items[0].Destination == collision.Items[1].Destination, "Custom folders bypassed the same-publisher/series collision.");
            Reject<FileOperationConflictException>(() => Apply(collision));
            var preview = Preview(drafts);
            Reject<ArgumentException>(() => store.ApplyFileMoves(new(drafts, preview.Revision, false)));
            store.EditMetadata(ids[2], new(new() { ["Title"] = "Catalog title" }));
            Reject<FileOperationConflictException>(() => Apply(preview));
            Check((long)Sql("SELECT COUNT(*) FROM file_operations")! == 0, "Stale later-row catalog changed an earlier row before rejection.");
            Directory.CreateDirectory(Path.Combine(target, "Publisher", "Changed Series"));
            preview = Preview(drafts);
            store.EditMetadata(ids[2], new(new() { ["Series"] = "Changed Series" }));
            Reject<FileOperationConflictException>(() => Apply(preview));
            Check((long)Sql("SELECT COUNT(*) FROM file_operations")! == 0, "Stale series preview changed an earlier row before rejection.");
            store.EditMetadata(ids[2], new(new() { ["Series"] = "Series" }));
            preview = Preview(drafts);
            var source = Path.Combine(root, "book3.cbz");
            var timestamp = File.GetLastWriteTimeUtc(source);
            var changed = original[2].ToArray();
            changed[^1] = (byte)'B';
            File.WriteAllBytes(source, changed);
            File.SetLastWriteTimeUtc(source, timestamp);
            Reject<FileOperationConflictException>(() => Apply(preview));
            Check((long)Sql("SELECT COUNT(*) FROM file_operations")! == 0, "Same-size/timestamp later-row edit bypassed full preflight.");
            File.WriteAllBytes(source, original[2]);
            File.SetLastWriteTimeUtc(source, timestamp);
            preview = Preview(drafts);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Reject<OperationCanceledException>(() => store.ApplyFileMoves(new(drafts, preview.Revision, true), canceled.Token));
            }
            Check((long)Sql("SELECT COUNT(*) FROM file_operations")! == 0, "Canceled batch mutated history.");
            store.SaveProgress("owner", ids[0], new(0, true));
            store.SaveBookmark("owner", ids[0], 0, new("Private"));
            store.SaveRating("owner", ids[0], new(4));
            var list = store.CreateList("owner", new("Bulk retained", "manual", "all", []));
            store.AddListBooks("owner", list.Id, ids);
            var rawXml = (string)Sql($"SELECT info_xml FROM books WHERE id='{ids[0]}'")!;
            var prepare = store.PreviewFileMove(ids[1], new(0, "", "book2.cbz"));
            Check(prepare.CanApply && prepare.Destination == "Library 1/Publisher/Series/book2.cbz", "An unprefixed source was treated as a no-op.");
            var prepared = store.ApplyFileMove(ids[1], new(0, "", "book2.cbz", prepare.Revision, true));
            var mixed = Preview([drafts[0], new(ids[1], 0, "", "book2.cbz"), drafts[2]]);
            Check(mixed.Items.All(row => row.Folder == "Publisher/Series") && mixed.Revision == Preview(Items(mixed)).Revision,
                "Normalized bulk folders did not round-trip.");
            var moved = Apply(mixed);
            Check(moved.Completed.Count == 2 && moved.Skipped.SequenceEqual([ids[1]]) && moved.FailedBookId is null && moved.Error is null && moved.NotAttempted.Count == 0,
                "Mixed no-op batch reported an incorrect result.");
            Check(store.GetBook("owner", ids[0]).Completed && store.GetBook("owner", ids[0]).PersonalRating == 4
                && store.GetBookmarks("owner", ids[0]).Single().Name == "Private" && !store.GetBook("other", ids[0]).Started
                && store.GetList("owner", list.Id).BookIds!.Length == 3 && store.GetPage(ids[0], 0).Bytes.SequenceEqual(png)
                && (string)Sql($"SELECT info_xml FROM books WHERE id='{ids[0]}'")! == rawXml,
                "Bulk move changed identity, private state, XML, membership, or reader data.");
            Check(File.ReadAllBytes(Path.Combine(target, "Publisher", "Series", "moved1.cbz")).SequenceEqual(original[0])
                && File.ReadAllBytes(Path.Combine(target, "Publisher", "Series", "moved3.cbz")).SequenceEqual(original[2]) && store.GetFileOperations(ids[1]).Count == 1,
                "Bulk move rewrote bytes or journaled a no-op.");
            var swap = Preview([new(ids[0], 1, "", "moved3.cbz"), new(ids[2], 1, "", "moved1.cbz")]);
            Check(!swap.CanApply && swap.Items.All(row => !row.CanApply), "Selected-book path swap was accepted.");
            var noOps = Preview(Items(mixed));
            Check(!noOps.CanApply && noOps.Items.All(row => row.CanApply && row.NoOp && row.Conflicts.Count == 0), "No-op batch should be valid but not applicable.");
            Reject<FileOperationConflictException>(() => Apply(noOps));
            foreach (var operation in moved.Completed) Undo(operation);
            Undo(prepared);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0, "Bulk undo split catalog identities.");

            // Fail row two after row one commits, and retain its pending journal by refusing finalization.
            Sql($"CREATE TRIGGER fail_bulk_move BEFORE UPDATE OF path ON books WHEN OLD.id='{ids[1]}' BEGIN SELECT RAISE(ABORT,'injected'); END");
            Sql($"CREATE TRIGGER fail_bulk_finish BEFORE UPDATE OF state ON file_operations WHEN OLD.book_id='{ids[1]}' BEGIN SELECT RAISE(ABORT,'injected'); END");
            var partial = Apply(Preview(drafts));
            Check(partial.Completed.Count == 1 && partial.Completed[0].BookId == ids[0] && partial.FailedBookId == ids[1]
                && partial.Error is not null && !partial.Error.Contains(directory) && partial.NotAttempted.SequenceEqual([ids[2]]),
                "Partial failure discarded completion or attempted later rows.");
            Check(File.ReadAllBytes(Path.Combine(target, "Publisher", "Series", "moved1.cbz")).SequenceEqual(original[0])
                && File.ReadAllBytes(Path.Combine(root, "book2.cbz")).SequenceEqual(original[1])
                && File.ReadAllBytes(Path.Combine(root, "book3.cbz")).SequenceEqual(original[2]) && !File.Exists(Path.Combine(target, "Publisher", "Series", "moved3.cbz")),
                "Partial failure lost data or moved an unattempted row.");
            var pending = store.GetFileOperations(ids[1]).Single(row => row.State == "pending");
            Sql("DROP TRIGGER fail_bulk_move; DROP TRIGGER fail_bulk_finish;");
            store = new LibraryStore(database, [root, target]);
            Check(!Preview([drafts[1]]).CanApply, "Pending failed row was eligible for automatic retry.");
            var recovery = store.PreviewFileRecovery(ids[1], pending.Id);
            Check(recovery.CanApply, "Pending bulk failure could not recover through existing journal.");
            store.RecoverFileOperation(ids[1], pending.Id, new(recovery.Revision, true));
            Undo(partial.Completed.Single());

            // A durable move whose journal finalization fails is both completed and in need of recovery.
            Sql("CREATE TRIGGER fail_bulk_finish BEFORE UPDATE OF state ON file_operations BEGIN SELECT RAISE(ABORT,'injected'); END");
            var committedFailure = Apply(Preview(drafts));
            Check(committedFailure.Completed.Count == 1 && committedFailure.Completed[0].BookId == ids[0]
                && committedFailure.FailedBookId == ids[0] && committedFailure.NotAttempted.SequenceEqual(ids.Skip(1)),
                "Committed move with pending cleanup was falsely reported as not moved.");
            Sql("DROP TRIGGER fail_bulk_finish;");
            var committedOperation = committedFailure.Completed.Single();
            recovery = store.PreviewFileRecovery(ids[0], committedOperation.OperationId);
            Check(recovery.CanApply && recovery.Message!.StartsWith("Finish"), "Committed bulk move did not retain finish recovery.");
            store.RecoverFileOperation(ids[0], committedOperation.OperationId, new(recovery.Revision, true));
            Undo(committedOperation);
            store.EditMetadata(ids[1], new(new() { ["Publisher"] = "Other Publisher" }));
            Directory.CreateDirectory(Path.Combine(target, "Other Publisher", "Series"));
            var publishers = Preview([drafts[0] with { FileName = "shared.cbz" }, drafts[1] with { FileName = "shared.cbz" }]);
            Check(publishers.CanApply && publishers.Items[0].Folder == "Publisher/Series" && publishers.Items[1].Folder == "Other Publisher/Series"
                && publishers.Items[0].Destination == "Library 2/Publisher/Series/shared.cbz"
                && publishers.Items[1].Destination == "Library 2/Other Publisher/Series/shared.cbz",
                "Different publishers collided on the same requested filename.");
            var separated = Apply(publishers);
            Check(separated.Completed.Count == 2 && separated.FailedBookId is null
                && File.ReadAllBytes(Path.Combine(target, "Publisher", "Series", "shared.cbz")).SequenceEqual(original[0])
                && File.ReadAllBytes(Path.Combine(target, "Other Publisher", "Series", "shared.cbz")).SequenceEqual(original[1]),
                "Applying normalized publisher folders doubled the prefix or changed the destination.");
            foreach (var operation in separated.Completed) Undo(operation);
            store.EditMetadata(ids[1], new(new() { ["Publisher"] = "Publisher", ["Series"] = "Other Series" }));
            Directory.CreateDirectory(Path.Combine(target, "Publisher", "Other Series"));
            var series = Preview([drafts[0] with { FileName = "shared.cbz" }, drafts[1] with { FileName = "shared.cbz" }]);
            Check(series.CanApply && series.Items[0].Destination == "Library 2/Publisher/Series/shared.cbz"
                && series.Items[1].Destination == "Library 2/Publisher/Other Series/shared.cbz",
                "Different series collided on the same requested filename.");
            separated = Apply(series);
            Check(separated.Completed.Count == 2 && separated.FailedBookId is null
                && File.ReadAllBytes(Path.Combine(target, "Publisher", "Series", "shared.cbz")).SequenceEqual(original[0])
                && File.ReadAllBytes(Path.Combine(target, "Publisher", "Other Series", "shared.cbz")).SequenceEqual(original[1]),
                "Applying different series changed the exact destination or source bytes.");
            foreach (var operation in separated.Completed) Undo(operation);
            for (var index = 0; index < ids.Length; index++)
                Check(File.ReadAllBytes(Path.Combine(root, $"book{index + 1}.cbz")).SequenceEqual(original[index]), "Bulk failure/recovery/undo changed bytes.");
            Check(!Directory.EnumerateFiles(root, ".panelhaven-move-*", SearchOption.AllDirectories).Any()
                && !Directory.EnumerateFiles(target, ".panelhaven-move-*", SearchOption.AllDirectories).Any(), "Bulk operations left staging files.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
