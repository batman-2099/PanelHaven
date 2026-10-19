using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class ArchiveExportChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-archive-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.cbz");
            var database = Path.Combine(directory, "state.sqlite");
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                using (var page = zip.CreateEntry("1.png").Open())
                    page.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
                using var xml = new StreamWriter(zip.CreateEntry("ComicInfo.xml").Open());
                xml.Write("<ComicInfo><Title>Original</Title></ComicInfo>");
            }
            var original = File.ReadAllBytes(source);
            var store = new LibraryStore(database, [directory]);
            store.ScanAsync(default).GetAwaiter().GetResult();
            var id = store.ListBooks("owner", null).Items.Single().Id;
            Check(store.PreviewArchiveBackupExport() == new ArchiveBackupExportPreview(0, 0, 0), "Empty vault summary was wrong.");
            Reject<ArgumentException>(() => store.ExportArchiveBackups());
            store.SaveProgress("owner", id, new(0, true));
            store.EditMetadata(id, new(new() { ["Title"] = "Edited" }));
            var preview = store.PreviewArchiveWrite(id);
            var backup = store.ApplyArchiveWrite(id, new(preview.Revision, true)).BackupId!;
            var written = File.ReadAllBytes(source);
            var vault = Path.Combine(directory, "archive-backups");
            Directory.CreateDirectory(Path.Combine(vault, id, Guid.NewGuid().ToString("N")));
            Check(store.PreviewArchiveBackupExport() == new ArchiveBackupExportPreview(1, original.Length, 1),
                "Export summary included an unpublished attempt or omitted the verified original.");
            string temporary;
            using (var export = store.ExportArchiveBackups())
            {
                temporary = export.Name;
                if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(temporary) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Export temporary file is not private.");
                using var zip = new ZipArchive(export, ZipArchiveMode.Read, true);
                var prefix = $"archive-backups/{id}/{backup}/";
                using var payload = zip.GetEntry(prefix + "original.bin")!.Open();
                using var bytes = new MemoryStream();
                payload.CopyTo(bytes);
                Check(bytes.ToArray().SequenceEqual(original), "Exported original bytes differ.");
                using var metadata = zip.GetEntry(prefix + "manifest.json")!.Open();
                using var manifest = JsonDocument.Parse(metadata);
                Check(manifest.RootElement.GetProperty("BookId").GetString() == id
                    && manifest.RootElement.GetProperty("OriginalHash").GetString() == Convert.ToHexString(SHA256.HashData(original)),
                    "Export lost backup identity or original hash.");
                Check(zip.Entries.Count(entry => !entry.FullName.EndsWith('/')) == 2, "Export leaked state or included unfinished backups.");
                Check(File.ReadAllBytes(source).SequenceEqual(written) && store.GetBook("owner", id).Completed
                    && store.GetMetadata(id).Fields["Title"] == "Edited", "Export mutated source or catalog/private data.");
                Directory.Move(vault, vault + ".saved");
                zip.ExtractToDirectory(directory);
            }
            Check(!File.Exists(temporary), "Export temporary file survived disposal.");
            // Operators must retain private permissions even when their extraction tool ignores ZIP mode attributes.
            if (!OperatingSystem.IsWindows())
                foreach (var path in Directory.EnumerateDirectories(vault, "*", SearchOption.AllDirectories).Prepend(vault))
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var restore = store.PreviewArchiveRestore(id, backup);
            store.RestoreArchiveBackup(id, backup, new(restore.Revision, true));
            Check(File.ReadAllBytes(source).SequenceEqual(original) && store.GetMetadata(id).Fields["Title"] == "Edited",
                "Exported vault could not restore original bytes with the matching catalog.");
            var payloadPath = Path.Combine(vault, id, backup, "original.bin");
            var corrupt = original.ToArray(); corrupt[40] ^= 1; File.WriteAllBytes(payloadPath, corrupt);
            Reject<InvalidDataException>(() => store.ExportArchiveBackups());
            Check(!Directory.EnumerateFiles(directory, ".panelhaven-archive-export-*.tmp").Any(), "Failed export leaked a temporary file.");
            File.WriteAllBytes(payloadPath, original);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Reject<OperationCanceledException>(() => store.ExportArchiveBackups(cancelled.Token));
            }
            File.Delete(source);
            store.ScanAsync(default).GetAwaiter().GetResult();
            store.RemoveMissingBook(id);
            Check(store.PreviewArchiveBackupExport().Backups == 2, "Orphaned historical originals were omitted.");
            using var orphanExport = store.ExportArchiveBackups();
            using var orphanZip = new ZipArchive(orphanExport, ZipArchiveMode.Read);
            Check(orphanZip.Entries.Count(entry => entry.Name == "original.bin") == 2, "Orphaned originals could not be exported.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
