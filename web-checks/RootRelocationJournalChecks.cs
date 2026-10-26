using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PanelHaven.Web;

public static class RootRelocationJournalChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-relocation-journal-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(directory, "state");
        var from = Path.Combine(directory, "old");
        var to = Path.Combine(directory, "new");
        var again = Path.Combine(directory, "again");
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(from, "Unknown Publisher"));
        Directory.CreateDirectory(Path.Combine(outside, "Unknown Publisher"));
        var database = Path.Combine(state, "panelhaven.sqlite");
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            foreach (var name in new[] { "inside", "outside", "stationary" })
            {
                using var zip = ZipFile.Open(Path.Combine(from, name + ".cbz"), ZipArchiveMode.Create);
                using (var image = zip.CreateEntry("1.png").Open()) image.Write(png);
                using var xml = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
                xml.Write("<ComicInfo><Title>Original</Title><Pages><Page Image='0' Type='Story'/></Pages></ComicInfo>");
            }
            var store = new LibraryStore(database, [from, outside]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            object? Sql(string sql, params (string, object?)[] values)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString());
                db.Open();
                db.CreateCollation("NATURAL_NUMBER", StringComparer.Ordinal.Compare);
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                return command.ExecuteScalar();
            }
            string Id(string path) => (string)Sql("SELECT id FROM books WHERE path=$path", ("$path", path))!;
            string Snapshot()
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                db.Open();
                var output = new System.Text.StringBuilder();
                foreach (var table in new[] { "books", "file_operations", "intake_operations", "page_metadata_edits", "progress" })
                {
                    using var command = db.CreateCommand();
                    command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var row = new object[reader.FieldCount];
                        reader.GetValues(row);
                        output.AppendLine(JsonSerializer.Serialize(row));
                    }
                }
                return output.ToString();
            }
            string Relocate(string source, string destination, bool apply)
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["relocate-from"] = source, ["relocate-to"] = destination, ["apply-relocation"] = apply ? "true" : null
                }).Build();
                var previous = Console.Out;
                using var output = new StringWriter();
                try { Console.SetOut(output); RelocateCatalog.Run(config, state, [destination, outside]); }
                finally { Console.SetOut(previous); }
                return output.ToString();
            }
            FileOperationResult Move(string id, int root, string name)
            {
                var preview = store.PreviewFileMove(id, new(root, "", name));
                Check(preview.CanApply, "Fixture move conflicted.");
                return store.ApplyFileMove(id, new(root, "", name, preview.Revision, true));
            }
            var ids = new[] { "inside", "outside", "stationary" }.ToDictionary(name => name, name => Id(Path.Combine(from, name + ".cbz")));
            foreach (var id in ids.Values)
            {
                store.EditMetadata(id, new(new() { ["Title"] = "Written" }));
                var preview = store.PreviewArchiveWrite(id);
                store.ApplyArchiveWrite(id, new(preview.Revision, true));
                store.SaveProgress("owner", id, new(0, true));
            }
            var original = File.ReadAllBytes(Path.Combine(from, "inside.cbz"));
            var insideMove = Move(ids["inside"], 0, "renamed.cbz");
            var outsideMove = Move(ids["outside"], 1, "away.cbz");
            Sql("""
                CREATE TABLE archive_backup_owners(owner_id TEXT PRIMARY KEY,book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE);
                CREATE TABLE book_path_provenance(book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                    source TEXT NOT NULL,destination TEXT NOT NULL,PRIMARY KEY(book_id,source,destination));
                INSERT INTO archive_backup_owners VALUES($id,$id);
                INSERT INTO book_path_provenance VALUES($id,$source,$destination);
                """, ("$id", ids["inside"]), ("$source", Path.Combine(from, "inside.cbz")), ("$destination", Path.Combine(from, "Unknown Publisher", "renamed.cbz")));
            var beforeMigration = Snapshot();
            store = new LibraryStore(database, [from, outside]);
            Check((long)Sql("SELECT count(*) FROM sqlite_master WHERE name IN ('archive_backup_owners','book_path_provenance')")! == 0
                && Snapshot() == beforeMigration, "Legacy backup metadata migration changed live books, private state or journals.");
            Directory.Move(from, to);
            var before = Snapshot();
            Check(Relocate(from, to, false).Contains("mapped=3") && Snapshot() == before, "Journal-aware dry run mutated state or omitted outside-root history.");
            Sql("UPDATE file_operations SET state='pending' WHERE id=$id", ("$id", outsideMove.OperationId));
            var pending = Snapshot();
            var refused = false;
            try { Relocate(from, to, true); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && Snapshot() == pending, "Relevant pending journal was not refused before changes.");
            Sql("UPDATE file_operations SET state='applied' WHERE id=$id", ("$id", outsideMove.OperationId));
            Sql("CREATE TRIGGER relocation_journal_abort BEFORE UPDATE ON books BEGIN SELECT RAISE(ABORT,'forced'); END");
            var failed = false;
            try { Relocate(from, to, true); }
            catch (SqliteException) { failed = true; }
            Check(failed && Snapshot() == before, "Relocation failure left partially remapped history.");
            Sql("DROP TRIGGER relocation_journal_abort");
            Check(Relocate(from, to, true).Contains("applied=3"), "Completed journal blocked relocation.");
            Check(Relocate(from, to, true).Contains("mapped=0"), "Journal relocation was not idempotent.");
            Directory.Move(to, again);
            Check(Relocate(to, again, true).Contains("applied=3"), "Repeated relocation lost outside-root history.");
            store = new LibraryStore(database, [again, outside]);
            foreach (var id in ids.Values)
                Check(store.GetBook("owner", id).Completed, "Relocation lost private state.");
            foreach (var (id, operation, name) in new[]
            {
                (ids["inside"], insideMove.OperationId, "inside.cbz"),
                (ids["outside"], outsideMove.OperationId, "outside.cbz")
            })
            {
                var undo = store.PreviewFileUndo(id, operation);
                Check(undo.CanApply && undo.Destination == "Library 1/" + name, "Relocation failed to remap undo destination.");
                store.UndoFileOperation(id, operation, new(undo.Revision, true));
            }
            Check(File.ReadAllBytes(Path.Combine(again, "inside.cbz")).SequenceEqual(original), "Move/relocate/undo changed comicbook bytes.");

            // A previously scanned destination can own independent history and page-edit intent.
            var mergeFrom = Path.Combine(directory, "merge-old");
            var mergeTo = Path.Combine(directory, "merge-new");
            Directory.CreateDirectory(mergeFrom);
            File.Copy(Path.Combine(again, "stationary.cbz"), Path.Combine(mergeFrom, "merge.cbz"));
            store = new LibraryStore(database, [mergeFrom, mergeTo, outside]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var oldId = Id(Path.Combine(mergeFrom, "merge.cbz"));
            Directory.Move(mergeFrom, mergeTo);
            Directory.CreateDirectory(Path.Combine(mergeTo, "Unknown Publisher"));
            store.ScanAsync(default).GetAwaiter().GetResult();
            var duplicate = Id(Path.Combine(mergeTo, "merge.cbz"));
            store.EditMetadata(duplicate, new(new() { ["Title"] = "Destination catalog edit" }));
            Sql("UPDATE books SET edited=1,info_xml=(SELECT info_xml FROM books WHERE id=$new) WHERE id=$old", ("$old", oldId), ("$new", duplicate));
            Sql("INSERT INTO page_metadata_edits(book_id,page,fields) VALUES($id,0,1)", ("$id", duplicate));
            var firstMove = Move(duplicate, 1, "moved.cbz");
            var undoMove = store.PreviewFileUndo(duplicate, firstMove.OperationId);
            store.UndoFileOperation(duplicate, firstMove.OperationId, new(undoMove.Revision, true));
            var pageDraft = new[] { new PageMetadataRow(0, "FrontCover", null) };
            var pagePreview = store.PreviewPageMetadata(oldId, new(store.GetPageMetadata(oldId).Revision, pageDraft));
            store.EditPageMetadata(oldId, new(pagePreview.Revision, pageDraft));
            var incompatible = Snapshot();
            var conflictResult = Relocate(mergeFrom, mergeTo, true);
            Check(conflictResult.Contains("conflicts=1") && Snapshot() == incompatible,
                "Incompatible page metadata merged or partially remapped history: " + conflictResult);
            Sql("UPDATE books SET info_xml=(SELECT info_xml FROM books WHERE id=$new) WHERE id=$old", ("$old", oldId), ("$new", duplicate));
            var mergeSnapshot = Snapshot();
            Sql("CREATE TRIGGER relocation_merge_abort BEFORE UPDATE OF path ON books BEGIN SELECT RAISE(ABORT,'forced merge'); END");
            failed = false;
            try { Relocate(mergeFrom, mergeTo, true); }
            catch (SqliteException) { failed = true; }
            Check(failed && Snapshot() == mergeSnapshot, "Failed merge discarded destination histories or page-edit intent.");
            Sql("DROP TRIGGER relocation_merge_abort");
            Check(Relocate(mergeFrom, mergeTo, true).Contains("merged=1"), "Compatible duplicate failed to merge.");
            store = new LibraryStore(database, [mergeTo, outside]);
            Check(Id(Path.Combine(mergeTo, "merge.cbz")) == oldId && store.GetFileOperations(oldId).Count == 2,
                "Duplicate merge lost identity or undo relationships.");
            Check((long)Sql("SELECT fields FROM page_metadata_edits WHERE book_id=$id", ("$id", oldId))! == 1,
                "Duplicate merge discarded page-edit intent.");
            Check(File.ReadAllBytes(Path.Combine(mergeTo, "merge.cbz")).SequenceEqual(File.ReadAllBytes(Path.Combine(again, "stationary.cbz"))),
                "Duplicate merge changed comicbook bytes.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
