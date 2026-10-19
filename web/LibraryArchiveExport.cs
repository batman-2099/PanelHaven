using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PanelHaven.Web;

public sealed record ArchiveBackupExportPreview(int Backups, long TotalBytes, int Incomplete);

public sealed partial class LibraryStore
{
    public ArchiveBackupExportPreview PreviewArchiveBackupExport()
    {
        var backups = ReadArchiveExportBackups(out var incomplete);
        return new(backups.Count, backups.Sum(backup => backup.OriginalSize), incomplete);
    }

    private List<ArchiveManifest> ReadArchiveExportBackups(out int incomplete)
    {
        incomplete = 0;
        var backups = new List<ArchiveManifest>();
        if (!Directory.Exists(archiveBackupRoot)) return backups;
        EnsureSafePath(archiveBackupRoot, archiveBackupRoot);
        foreach (var bookDirectory in Directory.EnumerateDirectories(archiveBackupRoot).Order(StringComparer.Ordinal))
        {
            EnsureSafePath(archiveBackupRoot, bookDirectory);
            var bookId = Path.GetFileName(bookDirectory);
            if (!Guid.TryParseExact(bookId, "N", out _)) throw new InvalidDataException("Invalid comicbook backup directory.");
            foreach (var directory in Directory.EnumerateDirectories(bookDirectory).Order(StringComparer.Ordinal))
            {
                EnsureSafePath(archiveBackupRoot, directory);
                var backupId = Path.GetFileName(directory);
                if (!Guid.TryParseExact(backupId, "N", out _)) throw new InvalidDataException("Invalid comicbook backup directory.");
                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath)) { incomplete++; continue; }
                EnsureSafePath(archiveBackupRoot, manifestPath);
                var manifest = ReadArchiveManifest(bookId, backupId);
                var payload = Path.Combine(directory, "original.bin");
                EnsureSafePath(archiveBackupRoot, payload);
                var file = new FileInfo(payload);
                if (!file.Exists || file.Length != manifest.OriginalSize)
                    throw new InvalidDataException("Comicbook backup original is missing or truncated.");
                backups.Add(manifest);
            }
        }
        return backups;
    }

    public FileStream ExportArchiveBackups(CancellationToken cancellationToken = default)
    {
        var backups = ReadArchiveExportBackups(out _);
        if (backups.Count == 0) throw new ArgumentException("No complete comicbook backups are available.");
        var stateDirectory = Path.GetDirectoryName(archiveBackupRoot)!;
        EnsureSafePath(stateDirectory, stateDirectory);
        var path = Path.Combine(stateDirectory, ".panelhaven-archive-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var output = new FileStream(path, options);
        try
        {
            // ponytail: stage on disk before sending headers; a bad original must never become a successful download.
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("archive-backups/").ExternalAttributes = (0x41c0 << 16) | 0x10;
                string? previousBook = null;
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    foreach (var manifest in backups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var prefix = $"archive-backups/{manifest.BookId}/{manifest.Id}/";
                        if (previousBook != manifest.BookId)
                        {
                            zip.CreateEntry($"archive-backups/{manifest.BookId}/").ExternalAttributes = (0x41c0 << 16) | 0x10;
                            previousBook = manifest.BookId;
                        }
                        zip.CreateEntry(prefix).ExternalAttributes = (0x41c0 << 16) | 0x10;
                        var payloadEntry = zip.CreateEntry(prefix + "original.bin", CompressionLevel.NoCompression);
                        payloadEntry.ExternalAttributes = 0x8180 << 16;
                        var originalPath = Path.Combine(ArchiveBackupDirectory(manifest.BookId, manifest.Id), "original.bin");
                        using (var original = OpenArchive(archiveBackupRoot, originalPath, manifest.SourcePath))
                        using (var destination = payloadEntry.Open())
                        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        {
                            long total = 0;
                            int count;
                            while ((count = original.Read(buffer)) != 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                total = checked(total + count);
                                if (total > manifest.OriginalSize) throw new InvalidDataException("Comicbook backup changed during export.");
                                hash.AppendData(buffer, 0, count);
                                destination.Write(buffer, 0, count);
                            }
                            if (total != manifest.OriginalSize || Convert.ToHexString(hash.GetHashAndReset()) != manifest.OriginalHash)
                                throw new InvalidDataException("Comicbook backup hash verification failed.");
                        }
                        var manifestEntry = zip.CreateEntry(prefix + "manifest.json", CompressionLevel.NoCompression);
                        manifestEntry.ExternalAttributes = 0x8180 << 16;
                        using var metadata = manifestEntry.Open();
                        JsonSerializer.Serialize(metadata, manifest);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(true);
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }
}
