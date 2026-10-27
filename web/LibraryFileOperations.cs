using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileOperationFolderRequest([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex,
    [property: JsonRequired] string Folder)
{
    public string? BookId { get; init; }
}
public sealed record FileOperationFolderResult(string Folder);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileMoveRequest([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex,
    [property: JsonRequired] string Folder, [property: JsonRequired] string FileName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileMoveApplyRequest([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex,
    [property: JsonRequired] string Folder, [property: JsonRequired] string FileName,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] bool Confirmed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileOperationRequest([property: JsonRequired] string ExpectedRevision, [property: JsonRequired] bool Confirmed);
public sealed record FileOperationRoot(int Index, string Label);
public sealed record FileOperationPreview(string BookId, string Operation, string Revision, string? OperationId,
    string Source, string Destination, string SourceHash, bool CanApply, IReadOnlyList<string> Conflicts, string? Message = null);
public sealed record FileOperationResult(string BookId, string OperationId);
public sealed record FileOperationHistory(string Id, string CreatedAt, string Source, string Destination,
    string Hash, string State, bool CanUndo, bool CanRecover, string? Message);
public sealed class FileOperationConflictException(string message = "Comicbook, destination, or catalog changed. Preview again before applying.") : Exception(message) { }

public sealed partial class LibraryStore
{
    private sealed record FileMovePlan(ArchiveCatalog Catalog, string SourceRoot, string DestinationRoot,
        string Destination, ArchiveSecurity Security, string SourceIdentity, bool SameDevice, long Size, long Ticks, FileOperationPreview Preview);
    private sealed record FileJournal(string Id, string BookId, string CreatedAt, string Source, string Destination,
        string Hash, string State, string? UndoOf, bool Committed, string SourceIdentity, string? StageIdentity,
        ArchiveSecurity Security, long Size, long Ticks, string? Message = null);
    private sealed record FileRecovery(FileJournal Journal, ArchiveCatalog Catalog, FileOperationPreview Preview, bool PreserveStage);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int FileRenameNoReplace(int oldDirectory, string source, int newDirectory, string destination, uint flags);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int FileLinkNoReplace(string source, string destination);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int FileOpenDirectoryAt(int directory, string path, int flags);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int FileMakeDirectoryAt(int directory, string path, uint mode);

    private void MoveFileNoReplace(FileJournal row, string source, string destination, CancellationToken cancellationToken)
    {
        var identity = source == row.Source || source == FileHold(row) ? row.SourceIdentity : row.StageIdentity;
        CheckOwnedFile(row, source, identity, cancellationToken);
        int error;
        try { error = FileRenameNoReplace(-100, source, -100, destination, 1) == 0 ? 0 : Marshal.GetLastPInvokeError(); }
        catch (EntryPointNotFoundException) { error = 38; } // ENOSYS: older libc has no renameat2 entry point.
        if (error != 0)
        {
            // NFS can reject RENAME_NOREPLACE. link is exclusive too; plain rename is never safe here.
            if (error is not (22 or 38 or 95) || FileLinkNoReplace(source, destination) != 0)
                throw new FileOperationConflictException("No-clobber publication failed; a path, mount, or permission may have changed. Preview recovery if the operation is pending.");
            FlushArchivePath(Path.GetDirectoryName(destination)!, true);
            CheckOwnedFile(row, destination, identity, cancellationToken);
            File.Delete(source);
        }
        else FlushArchivePath(Path.GetDirectoryName(destination)!, true);
        FlushArchivePath(Path.GetDirectoryName(source)!, true);
    }

    private static void InitializeFileOperations(SqliteConnection db)
    {
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS file_operations (
                id TEXT PRIMARY KEY, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                created_at TEXT NOT NULL, source TEXT NOT NULL, destination TEXT NOT NULL, hash TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('pending','applied','undone','failed')),
                undo_of TEXT REFERENCES file_operations(id), committed INTEGER NOT NULL DEFAULT 0,
                source_identity TEXT NOT NULL, stage_identity TEXT, security TEXT NOT NULL,
                file_size INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, message TEXT
            );
            CREATE INDEX IF NOT EXISTS file_operations_book ON file_operations(book_id,created_at DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS file_operations_pending ON file_operations(book_id) WHERE state='pending';
            DROP TABLE IF EXISTS archive_backup_owners;
            DROP TABLE IF EXISTS book_path_provenance;
            """);
        command.ExecuteNonQuery();
        InitializeIntakeProcessing(db);
        // Pending rows are the recovery journal. Startup never guesses which of two files to delete.
        using var reserve = Command(db, "UPDATE books SET availability='unavailable' WHERE id IN (SELECT book_id FROM file_operations WHERE state='pending')");
        reserve.ExecuteNonQuery();
    }

    public IReadOnlyList<FileOperationRoot> GetFileOperationRoots() => roots.Select((_, index) => new FileOperationRoot(index, RootLabel(index))).ToArray();

    public FileOperationFolderResult CreateFileOperationFolder(FileOperationFolderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateFileOperationFolder(request.RootIndex, request.Folder);
        if (request.BookId is not null)
        {
            using var db = Open();
            var metadata = ParseXml(ReadArchiveCatalog(db, null, request.BookId).Xml, "ComicInfo").Root!;
            request = request with { Folder = ComicbookFolder(metadata) };
            ValidateFileOperationFolder(request.RootIndex, request.Folder);
        }
        if (!OperatingSystem.IsLinux()) throw new ArgumentException("Safe file organization currently requires Linux file-security support.");
        var root = roots[request.RootIndex];
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, request.Folder)));
        if (!IsContained(root, path) || path.Any(char.IsControl))
            throw new ArgumentException("Folder must be a canonical path inside the selected library.");
        if (IsContained(stateDirectory, path))
            throw new UnauthorizedAccessException("File organization cannot use the application state directory.");
        EnsureSafePath(root, root);

        const int directoryFlags = 0x10000 | 0x20000 | 0x80000; // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        var descriptor = ArchiveOpen("/", directoryFlags);
        if (descriptor < 0) throw new UnauthorizedAccessException("Cannot open the destination filesystem.");
        try
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var rootDepth = root == "/" ? 0 : root.Count(character => character == '/');
            // Walk root ancestors too; only descendants of the existing configured root may be created.
            for (var index = 0; index < parts.Length; index++)
            {
                var child = FileOpenDirectoryAt(descriptor, parts[index], directoryFlags);
                if (child < 0 && Marshal.GetLastPInvokeError() == 2 && index >= rootDepth) // ENOENT
                {
                    var created = FileMakeDirectoryAt(descriptor, parts[index], 0x1ff) == 0; // 0777, subject to umask
                    if (!created && Marshal.GetLastPInvokeError() != 17) // EEXIST: verify the concurrent entry below.
                        throw new IOException("Cannot create the destination folder.");
                    if (created && ArchiveFsync(descriptor) != 0)
                        throw new IOException("Cannot flush the destination folder's parent.");
                    child = FileOpenDirectoryAt(descriptor, parts[index], directoryFlags);
                }
                if (child < 0)
                    throw new UnauthorizedAccessException("Destination components must be accessible directories, never symbolic links or files.");
                ArchiveClose(descriptor);
                descriptor = child;
            }
            var folder = Path.GetRelativePath(root, path);
            return new(folder == "." ? "" : folder.Replace(Path.DirectorySeparatorChar, '/'));
        }
        finally { ArchiveClose(descriptor); }
    }

    public FileOperationPreview PreviewFileMove(string id, FileMoveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return PlanFileMove(db, id, FileDestination(request), null, cancellationToken).Preview;
        }
        finally { scanLock.Release(); }
    }

    public FileOperationResult ApplyFileMove(string id, FileMoveApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyFileMoveOperation(id, FileDestination(new(request.RootIndex, request.Folder, request.FileName)), null,
            new(request.ExpectedRevision, request.Confirmed), cancellationToken);
    }

    public FileOperationPreview PreviewFileUndo(string id, string operationId, CancellationToken cancellationToken = default)
    {
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            var journal = ReadFileJournal(db, id, operationId);
            return PlanFileMove(db, id, journal.Source, journal, cancellationToken).Preview;
        }
        finally { scanLock.Release(); }
    }

    public FileOperationResult UndoFileOperation(string id, string operationId, FileOperationRequest request, CancellationToken cancellationToken = default) =>
        ApplyFileMoveOperation(id, null, operationId, request, cancellationToken);

    public IReadOnlyList<FileOperationHistory> GetFileOperations(string id)
    {
        using var db = Open();
        var catalog = ReadArchiveCatalog(db, null, id);
        using var command = Command(db, "SELECT * FROM file_operations WHERE book_id=$id ORDER BY (state='pending') DESC,created_at DESC,id DESC LIMIT 100", ("$id", id));
        using var reader = command.ExecuteReader();
        var history = new List<FileOperationHistory>();
        var hasPending = false;
        while (reader.Read())
        {
            var row = ReadFileJournal(reader);
            hasPending |= row.State == "pending";
            history.Add(new(row.Id, row.CreatedAt, FileDisplayPath(row.Source), FileDisplayPath(row.Destination), row.Hash, row.State,
                !hasPending && row.State == "applied" && row.UndoOf is null && catalog.Path.Equals(row.Destination, PathComparison),
                row.State == "pending", row.State == "pending" ? RecoveryMessage(row) : row.Message));
        }
        return history;
    }

    private void ValidateFileOperationFolder(int rootIndex, string? folder)
    {
        if (rootIndex < 0 || rootIndex >= roots.Length || folder is null)
            throw new ArgumentException("Select a configured library and relative folder.");
        if (folder.Length > 4096 || folder.Contains('\\') || folder.Contains(':')
            || Path.IsPathRooted(folder) || folder.Any(char.IsControl)
            || folder.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Folder must be a relative path without traversal or aliases.");
    }

    private string FileDestination(FileMoveRequest request)
    {
        ValidateFileOperationFolder(request.RootIndex, request.Folder);
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255
            || request.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || request.FileName.Contains('/')
            || request.FileName.Contains('\\') || request.FileName.Contains(':') || request.FileName.Any(char.IsControl)
            || request.FileName is "." or ".." || request.FileName.EndsWith(' ') || request.FileName.EndsWith('.'))
            throw new ArgumentException("Filename must be one valid comicbook filename.");
        return Path.GetFullPath(Path.Combine(roots[request.RootIndex], request.Folder, request.FileName));
    }

    private static string ComicbookFolder(XElement metadata) =>
        IntakeComponent(metadata.Element(metadata.Name.Namespace + "Publisher")?.Value ?? "", "Unknown Publisher") + "/" +
        IntakeComponent(metadata.Element(metadata.Name.Namespace + "Series")?.Value ?? "", "Unknown Series");

    private string FileOperationRootFor(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new ArgumentException("Safe file organization currently requires Linux file-security support.");
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || path.Any(char.IsControl))
            throw new ArgumentException("The comicbook path is not canonical or contains control characters.");
        var root = roots.FirstOrDefault(root => IsContained(root, path)) ?? throw new UnauthorizedAccessException("Path is outside the configured libraries.");
        if (IsContained(stateDirectory, path))
            throw new UnauthorizedAccessException("File organization cannot use the application state directory.");
        var parent = Path.GetDirectoryName(path)!;
        try { EnsureSafePath(root, parent); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileOperationConflictException($"Folder not found: {FileDisplayPath(parent)}. For a new destination, choose Create folder before previewing. If this is the source folder, restore it before moving the comic.");
        }
        if (!Directory.Exists(parent)) throw new ArgumentException("The comicbook's parent path must be an existing folder.");
        return root;
    }

    private string FileDisplayPath(string path)
    {
        var index = Array.FindIndex(roots, root => IsContained(root, path));
        return index < 0 ? "Unconfigured library/" + Path.GetFileName(path)
            : RootLabel(index) + "/" + Path.GetRelativePath(roots[index], path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool FileEntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static (string Identity, uint Links) ReadFileIdentity(string path)
    {
        if (ArchiveStatx(-100, path, 0x100, 0x7ff, out var stat) != 0 || stat.Links == 0 || (stat.Mode & 0xf000) != 0x8000)
            throw new UnauthorizedAccessException("File organization requires an ordinary file.");
        return ($"{stat.DeviceMajor:x}:{stat.DeviceMinor:x}:{stat.Inode:x}", stat.Links);
    }

    private static string FileIdentity(string path)
    {
        var file = ReadFileIdentity(path);
        if (file.Links != 1)
            throw new UnauthorizedAccessException("File organization refuses hard-link aliases outside a pending operation.");
        return file.Identity;
    }

    private FileMovePlan PlanFileMove(SqliteConnection db, string id, string destination, FileJournal? undo, CancellationToken cancellationToken, bool allowNoOp = false)
    {
        var catalog = ReadArchiveCatalog(db, null, id);
        RefusePendingFileOperation(db, id);
        var sourceRoot = FileOperationRootFor(catalog.Path);
        if (undo is null)
        {
            var rootIndex = Array.FindIndex(roots, root => IsContained(root, destination));
            if (rootIndex < 0) throw new UnauthorizedAccessException("Path is outside the configured libraries.");
            var catalogMetadata = ParseXml(catalog.Xml, "ComicInfo").Root!;
            destination = FileDestination(new(rootIndex, ComicbookFolder(catalogMetadata), Path.GetFileName(destination)));
        }
        var destinationRoot = FileOperationRootFor(destination);
        if (!IsArchive(catalog.Path) || !Path.GetExtension(destination).Equals(Path.GetExtension(catalog.Path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Keep the same supported comicbook extension.");
        if (ArchiveStatx(-100, catalog.Path, 0x100, 0x17ff, out var sourceStat) != 0
            || ArchiveStatx(-100, Path.GetDirectoryName(destination)!, 0x100, 0x17ff, out var directoryStat) != 0)
            throw new UnauthorizedAccessException("Cannot verify the source and destination filesystems.");
        // Device equality alone is insufficient across bind mounts; unknown mount IDs use verified copying.
        var sameDevice = (sourceStat.Mask & directoryStat.Mask & 0x1000) != 0 && sourceStat.MountId == directoryStat.MountId
            && sourceStat.DeviceMajor == directoryStat.DeviceMajor && sourceStat.DeviceMinor == directoryStat.DeviceMinor;
        if (!sameDevice) RefuseCrossMountAttributes(catalog.Path, destination);
        var version = ReadSourceVersion(sourceRoot, catalog.Path);
        var security = ArchiveWritePermissions(sourceRoot, catalog.Path, preserveInode: sameDevice);
        var identity = FileIdentity(catalog.Path);
        using var source = OpenArchive(sourceRoot, catalog.Path);
        using var archive = new ComicArchive(source, catalog.Path, cancellationToken);
        var metadata = archive.ReadMetadata(cancellationToken);
        if (metadata.Info is not null) ReadXml(metadata.Info, "ComicInfo", cancellationToken);
        if (metadata.Book is not null) ReadXml(metadata.Book, "ComicBook", cancellationToken);
        var hash = ArchiveHash(source, cancellationToken);
        var conflicts = new List<string>();
        if (archive.Pages.Count != catalog.PageCount) conflicts.Add("Comicbook page count changed; rescan before organizing it.");
        var noOp = catalog.Path.Equals(destination, PathComparison);
        if (noOp && !allowNoOp) conflicts.Add("Source and destination are the same path.");
        else if (!noOp && FileEntryExists(destination)) conflicts.Add("Destination already exists; files are never overwritten.");
        if ((!noOp || !allowNoOp) && FileCatalogCollision(db, destination, null)) conflicts.Add("Destination is already cataloged or reserved by another operation.");
        if (undo is not null && (undo.State != "applied" || undo.UndoOf is not null || !catalog.Path.Equals(undo.Destination, PathComparison) || hash != undo.Hash))
            conflicts.Add("Undo requires this move's unchanged destination comicbook and an applied original operation.");
        var revision = FileRevision(new { catalog, destination, security, identity, sameDevice, version.Size, version.Ticks, hash, conflicts, undo });
        using var latest = OpenArchive(sourceRoot, catalog.Path);
        if (ArchiveHash(latest, cancellationToken) != hash || FileIdentity(catalog.Path) != identity
            || ReadSourceVersion(sourceRoot, catalog.Path) != version
            || ArchiveWritePermissions(sourceRoot, catalog.Path, preserveInode: sameDevice) != security)
            throw new FileOperationConflictException();
        return new(catalog, sourceRoot, destinationRoot, destination, security, identity, sameDevice, version.Size, version.Ticks,
            new(id, undo is null ? "move" : "undo", revision, undo?.Id, FileDisplayPath(catalog.Path), FileDisplayPath(destination), hash,
                conflicts.Count == 0, conflicts));
    }

    private static void RefuseCrossMountAttributes(string source, string destination)
    {
        if (ReadArchiveExtendedAttributesHash(source) is not null
            || ReadArchiveExtendedAttributesHash(Path.GetDirectoryName(destination)!) is not null)
            throw new FileOperationConflictException("Cross-mount moves with source or destination-directory extended attributes (including ACLs) are unsupported; no ACLs will be stripped.");
    }

    private static string FileRevision<T>(T value)
    {
        using var hash = SHA256.Create();
        using var binding = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(binding, value);
        binding.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }

    private static bool FileCatalogCollision(SqliteConnection db, string path, string? operationId, SqliteTransaction? transaction = null)
    {
        using var command = Command(db, """
            SELECT 1 FROM books WHERE path=$path
            UNION ALL SELECT 1 FROM file_operations WHERE state='pending' AND id<>COALESCE($operation,'')
                AND (source=$path OR destination=$path)
            UNION ALL SELECT 1 FROM intake_operations WHERE state='pending' AND id<>COALESCE($operation,'')
                AND (source=$path OR destination=$path) LIMIT 1
            """, ("$path", PathKey(path)), ("$operation", operationId));
        command.Transaction = transaction;
        return command.ExecuteScalar() is not null;
    }

    private static void RefusePendingFileOperation(SqliteConnection db, string id, SqliteTransaction? transaction = null)
    {
        using var command = Command(db, """
            SELECT 1 FROM file_operations WHERE book_id=$id AND state='pending'
            UNION ALL SELECT 1 FROM intake_operations WHERE book_id=$id AND state='pending' LIMIT 1
            """, ("$id", id));
        command.Transaction = transaction;
        if (command.ExecuteScalar() is not null)
            throw new FileOperationConflictException("A pending file operation reserves this book. Preview its recovery before making another change.");
    }

    private static HashSet<string> ReservedFileOperationPaths(SqliteConnection db)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using var command = Command(db, """
            SELECT source,destination FROM file_operations WHERE state='pending'
            UNION ALL SELECT source,destination FROM intake_operations WHERE state='pending'
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read()) { paths.Add(reader.GetString(0)); paths.Add(reader.GetString(1)); }
        return paths;
    }

    private FileOperationResult ApplyFileMoveOperation(string id, string? destination, string? undoId, FileOperationRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new FileOperationConflictException();
        scanLock.Wait(cancellationToken);
        try { return ApplyFileMoveLocked(id, destination, undoId, request, cancellationToken); }
        finally { scanLock.Release(); }
    }

    // The caller owns scanLock; bulk preflight and all of its serial moves share the same reservation.
    private FileOperationResult ApplyFileMoveLocked(string id, string? destination, string? undoId, FileOperationRequest request, CancellationToken cancellationToken)
    {
        FileJournal? journal = null;
        try
        {
            using var db = Open();
            var undo = undoId is null ? null : ReadFileJournal(db, id, undoId);
            var plan = PlanFileMove(db, id, undo?.Source ?? destination!, undo, cancellationToken);
            if (!plan.Preview.CanApply || plan.Preview.Revision != request.ExpectedRevision) throw new FileOperationConflictException();
            journal = new(Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow.ToString("O"), plan.Catalog.Path, plan.Destination,
                plan.Preview.SourceHash, "pending", undo?.Id, false, plan.SourceIdentity, plan.SameDevice ? plan.SourceIdentity : null,
                plan.Security, plan.Size, plan.Ticks);
            using (var transaction = db.BeginTransaction(deferred: false))
            {
                if (ReadArchiveCatalog(db, transaction, id) != plan.Catalog || FileCatalogCollision(db, plan.Destination, null, transaction))
                    throw new FileOperationConflictException();
                InsertFileJournal(db, transaction, journal);
                transaction.Commit();
            }
            var stage = FileStage(journal);
            if (!plan.SameDevice)
            {
                RefuseCrossMountAttributes(journal.Source, journal.Destination);
                using (var source = OpenArchive(plan.SourceRoot, plan.Catalog.Path))
                using (var output = NewPrivateFile(stage))
                {
                    if (CopyArchiveBytes(source, output, MaxArchiveBytes, cancellationToken) != plan.Size
                        || ArchiveHash(output, cancellationToken) != journal.Hash) throw new FileOperationConflictException();
                    output.Flush(true);
                }
                ApplyArchivePermissions(stage, plan.Security);
                File.SetLastWriteTimeUtc(stage, new DateTime(plan.Ticks, DateTimeKind.Utc));
                FlushArchivePath(stage, false);
                FlushArchivePath(Path.GetDirectoryName(stage)!, true);
                journal = journal with { StageIdentity = FileIdentity(stage) };
                using var command = Command(db, "UPDATE file_operations SET stage_identity=$identity WHERE id=$id", ("$identity", journal.StageIdentity), ("$id", journal.Id));
                command.ExecuteNonQuery();
                CheckOwnedFile(journal, stage, journal.StageIdentity, cancellationToken);
            }
            if (FileEntryExists(journal.Destination)) throw new FileOperationConflictException();
            MoveFileNoReplace(journal, journal.Source, FileHold(journal), cancellationToken);
            if (!plan.SameDevice) CheckOwnedFile(journal, FileHold(journal), journal.SourceIdentity, cancellationToken);
            MoveFileNoReplace(journal, plan.SameDevice ? FileHold(journal) : stage, journal.Destination, cancellationToken);
            CheckOwnedFile(journal, journal.Destination, journal.StageIdentity, cancellationToken);
            if (!plan.SameDevice) CheckOwnedFile(journal, FileHold(journal), journal.SourceIdentity, cancellationToken);
            if (FileEntryExists(journal.Source)) throw new FileOperationConflictException();
            cancellationToken.ThrowIfCancellationRequested();
            var version = ReadSourceVersion(plan.DestinationRoot, journal.Destination);
            using (var transaction = db.BeginTransaction(deferred: false))
            {
                if (ReadArchiveCatalog(db, transaction, id) != plan.Catalog || FileCatalogCollision(db, journal.Destination, journal.Id, transaction))
                    throw new FileOperationConflictException();
                var (location, folder) = SourceLocation(journal.Destination);
                using var command = Command(db, """
                    UPDATE books SET path=$path,file_size=$size,modified_ticks=$ticks,content_hash=$hash,
                        availability='available',location=$location,folder=$folder WHERE id=$id;
                    UPDATE file_operations SET committed=1 WHERE id=$operation;
                    """, ("$path", PathKey(journal.Destination)), ("$size", version.Size), ("$ticks", version.Ticks),
                    ("$hash", journal.Hash), ("$location", location), ("$folder", folder), ("$id", id), ("$operation", journal.Id));
                command.Transaction = transaction;
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            journal = journal with { Committed = true };
            CompleteFileRecovery(db, PlanFileRecovery(db, journal, default), default);
            return new(id, journal.Id);
        }
        catch
        {
            if (journal is not null)
            {
                try
                {
                    using var db = Open();
                    var recovery = PlanFileRecovery(db, ReadFileJournal(db, id, journal.Id), default);
                    if (recovery.Preview.CanApply) CompleteFileRecovery(db, recovery, default);
                }
                catch (Exception exception) when (IsArchiveError(exception) || exception is SqliteException or FileOperationConflictException or KeyNotFoundException)
                {
                    // Leave the durable reservation intact. Recovery requires a fresh preview, never a blind retry.
                }
            }
            throw;
        }
    }

    private static string FileStage(FileJournal row) => Path.Combine(Path.GetDirectoryName(row.Destination)!, $".panelhaven-move-{row.Id}.stage");
    private static string FileHold(FileJournal row) => Path.Combine(Path.GetDirectoryName(row.Source)!, $".panelhaven-move-{row.Id}.hold");

    private static (string Path, string? Identity)[] FileJournalPaths(FileJournal row) =>
        [(row.Source, row.SourceIdentity), (FileHold(row), row.SourceIdentity),
            (row.Destination, row.StageIdentity), (FileStage(row), row.StageIdentity)];

    private uint CheckFileLinks(FileJournal row, string path, string identity)
    {
        if (ArchiveStatx(-100, path, 0x100, 0x7ff, out var file) != 0 || file.Links == 0
            || (file.Mode & 0xf000) != 0x8000 || $"{file.DeviceMajor:x}:{file.DeviceMinor:x}:{file.Inode:x}" != identity)
            throw new FileOperationConflictException("An operation file was replaced; no files were removed.");
        var paths = FileJournalPaths(row);
        uint ownedLinks = 0;
        for (var index = 0; index < paths.Length; index++)
        {
            var entry = paths[index];
            for (var previous = 0; previous < index; previous++)
                if (paths[previous].Path == entry.Path) throw new FileOperationConflictException("Operation paths must be distinct.");
            var root = FileOperationRootFor(entry.Path);
            if (!FileEntryExists(entry.Path)) continue;
            if (ArchiveStatx(-100, entry.Path, 0x100, 0x7ff, out var stat) != 0)
                throw new FileOperationConflictException("Cannot verify operation link ownership.");
            if (stat.DeviceMajor != file.DeviceMajor || stat.DeviceMinor != file.DeviceMinor || stat.Inode != file.Inode) continue;
            EnsureSafePath(root, entry.Path);
            if (entry.Identity != identity || stat.Links != file.Links || (stat.Mode & 0xf000) != 0x8000)
                throw new FileOperationConflictException("An operation alias has uncertain ownership; no files were removed.");
            ownedLinks++;
        }
        if (ownedLinks != file.Links)
            throw new FileOperationConflictException("An operation file has unaccounted hard-link aliases; no files were removed.");
        return file.Links;
    }

    private uint CheckOwnedFile(FileJournal row, string path, string? identity, CancellationToken cancellationToken)
    {
        var root = FileOperationRootFor(path);
        if (identity is null) throw new FileOperationConflictException("A staging file has uncertain ownership; preserve it for manual inspection.");
        EnsureSafePath(root, path);
        var links = CheckFileLinks(row, path, identity);
        var security = ArchiveWritePermissions(root, path, preserveInode: row.SourceIdentity == row.StageIdentity);
        // Unix Hidden is derived from the staging filename, not an ACL or a permission.
        const FileAttributes nameAttributes = FileAttributes.Hidden | FileAttributes.Normal;
        if ((security with { Attributes = row.Security.Attributes }) != row.Security
            || (security.Attributes & ~nameAttributes) != (row.Security.Attributes & ~nameAttributes))
            throw new FileOperationConflictException("An operation file was replaced or its security changed; no files were removed.");
        using var file = OpenArchive(root, path, row.Source);
        if (file.Length != row.Size || ArchiveHash(file, cancellationToken) != row.Hash
            || CheckFileLinks(row, path, identity) != links
            || ArchiveWritePermissions(root, path, preserveInode: row.SourceIdentity == row.StageIdentity) != security)
            throw new FileOperationConflictException("An operation file changed; no files were removed.");
        return links;
    }

    public FileOperationPreview PreviewFileRecovery(string id, string operationId, CancellationToken cancellationToken = default)
    {
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return PlanFileRecovery(db, ReadFileJournal(db, id, operationId), cancellationToken).Preview;
        }
        finally { scanLock.Release(); }
    }

    public FileOperationResult RecoverFileOperation(string id, string operationId, FileOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            var plan = PlanFileRecovery(db, ReadFileJournal(db, id, operationId), cancellationToken);
            if (!plan.Preview.CanApply || plan.Preview.Revision != request.ExpectedRevision) throw new FileOperationConflictException();
            CompleteFileRecovery(db, plan, cancellationToken);
            return new(id, operationId);
        }
        finally { scanLock.Release(); }
    }

    private static string RecoveryMessage(FileJournal row) => row.Committed
        ? "Finish the committed move: keep the destination and remove only verified redundant operation files."
        : "Cancel the interrupted move: restore the original source and remove only verified operation copies. Changed or unrecognized files require manual inspection; never delete them blindly.";

    private FileRecovery PlanFileRecovery(SqliteConnection db, FileJournal row, CancellationToken cancellationToken)
    {
        var catalog = ReadArchiveCatalog(db, null, row.BookId);
        var conflicts = new List<string>();
        var observations = new List<object>();
        if (row.State != "pending") conflicts.Add("This operation is no longer pending.");
        if (!catalog.Path.Equals(row.Committed ? row.Destination : row.Source, PathComparison)) conflicts.Add("Catalog identity no longer matches this operation.");
        var sourceExists = false;
        var holdExists = false;
        var destinationExists = false;
        var stageExists = false;
        string? stageConflict = null;
        foreach (var (path, identity) in FileJournalPaths(row))
        {
            try
            {
                FileOperationRootFor(path);
                var exists = FileEntryExists(path);
                if (path == row.Source) sourceExists = exists;
                else if (path == FileHold(row)) holdExists = exists;
                else if (path == row.Destination) destinationExists = exists;
                else if (path == FileStage(row)) stageExists = exists;
                var links = exists ? CheckOwnedFile(row, path, identity, cancellationToken) : 0;
                observations.Add(new { path, exists, identity, links, ticks = exists ? File.GetLastWriteTimeUtc(path).Ticks : 0 });
            }
            catch (Exception exception) when (IsArchiveError(exception) || exception is FileOperationConflictException)
            {
                var conflict = $"{FileDisplayPath(path)} is changed, unsafe, or not a verified operation file. Preserve all copies for manual inspection; recovery will not guess which copy owns the book.";
                if (path == FileStage(row)) stageConflict = conflict;
                else conflicts.Add(conflict);
            }
        }
        if (row.Committed && !destinationExists) conflicts.Add("Committed recovery requires the unchanged destination.");
        var originalCount = (sourceExists ? 1 : 0) + (holdExists ? 1 : 0)
            + (row.StageIdentity == row.SourceIdentity ? (destinationExists ? 1 : 0) + (stageExists ? 1 : 0) : 0);
        if (!row.Committed && originalCount == 0) conflicts.Add("Cancellation requires a verified original source, hold, or same-device operation alias.");
        var preserveStage = stageConflict is not null && !row.Committed && sourceExists && !holdExists && !destinationExists && conflicts.Count == 0;
        if (stageConflict is not null && !preserveStage) conflicts.Add(stageConflict);
        var message = RecoveryMessage(row) + (preserveStage ? $" Leave the unverified staging file untouched at {FileDisplayPath(FileStage(row))}; it will not be imported." : "");
        var revision = FileRevision(new { row, catalog, observations, conflicts, preserveStage });
        return new(row, catalog, new(row.BookId, "recover", revision, row.Id,
            FileDisplayPath(row.Committed ? row.Source : row.Destination), FileDisplayPath(row.Committed ? row.Destination : row.Source),
            row.Hash, conflicts.Count == 0, conflicts, message), preserveStage);
    }

    private void CompleteFileRecovery(SqliteConnection db, FileRecovery plan, CancellationToken cancellationToken)
    {
        var row = plan.Journal;
        var fresh = PlanFileRecovery(db, row, cancellationToken);
        if (!fresh.Preview.CanApply || fresh.Preview.Revision != plan.Preview.Revision) throw new FileOperationConflictException();
        if (!row.Committed && !FileEntryExists(row.Source))
        {
            var original = FileEntryExists(FileHold(row)) ? FileHold(row) : FileEntryExists(row.Destination) ? row.Destination : FileStage(row);
            MoveFileNoReplace(row, original, row.Source, cancellationToken);
        }
        // A surviving alias may itself be a link whose directory flush was interrupted.
        FlushArchivePath(Path.GetDirectoryName(row.Committed ? row.Destination : row.Source)!, true);
        foreach (var path in row.Committed ? new[] { row.Source, FileHold(row), FileStage(row) } : new[] { FileHold(row), row.Destination, FileStage(row) })
        {
            if (plan.PreserveStage && path == FileStage(row) || !FileEntryExists(path)) continue;
            CheckOwnedFile(row, row.Committed ? row.Destination : row.Source, row.Committed ? row.StageIdentity : row.SourceIdentity, cancellationToken);
            CheckOwnedFile(row, path, path == row.Source || path == FileHold(row) ? row.SourceIdentity : row.StageIdentity, cancellationToken);
            File.Delete(path);
            FlushArchivePath(Path.GetDirectoryName(path)!, true);
        }
        CheckOwnedFile(row, row.Committed ? row.Destination : row.Source, row.Committed ? row.StageIdentity : row.SourceIdentity, cancellationToken);
        using var transaction = db.BeginTransaction(deferred: false);
        if (ReadArchiveCatalog(db, transaction, row.BookId) != plan.Catalog) throw new FileOperationConflictException();
        using var command = Command(db, """
            UPDATE file_operations SET state=$state,message=$message WHERE id=$operation AND state='pending';
            UPDATE books SET availability='available' WHERE id=$id;
            """, ("$state", row.Committed ? "applied" : "failed"), ("$operation", row.Id), ("$id", row.BookId),
            ("$message", plan.PreserveStage ? $"Canceled safely; unverified staging file retained untouched at {FileDisplayPath(FileStage(row))}. Inspect it manually; it is not a comicbook and will not be imported." : null));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        if (row.Committed && row.UndoOf is not null)
        {
            using var undo = Command(db, "UPDATE file_operations SET state='undone' WHERE id=$id AND state='applied'", ("$id", row.UndoOf));
            undo.Transaction = transaction;
            undo.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void InsertFileJournal(SqliteConnection db, SqliteTransaction transaction, FileJournal row)
    {
        using var command = Command(db, """
            INSERT INTO file_operations(id,book_id,created_at,source,destination,hash,state,undo_of,source_identity,stage_identity,security,file_size,modified_ticks)
            VALUES($id,$book,$created,$source,$destination,$hash,'pending',$undo,$identity,$stageIdentity,$security,$size,$ticks)
            """, ("$id", row.Id), ("$book", row.BookId), ("$created", row.CreatedAt), ("$source", row.Source), ("$destination", row.Destination),
            ("$hash", row.Hash), ("$undo", row.UndoOf), ("$identity", row.SourceIdentity), ("$stageIdentity", row.StageIdentity), ("$security", JsonSerializer.Serialize(row.Security)),
            ("$size", row.Size), ("$ticks", row.Ticks));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static FileJournal ReadFileJournal(SqliteConnection db, string id, string operationId)
    {
        using var command = Command(db, "SELECT * FROM file_operations WHERE book_id=$id AND id=$operation", ("$id", id), ("$operation", operationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return ReadFileJournal(reader);
    }

    private static FileJournal ReadFileJournal(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8) != 0, reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), JsonSerializer.Deserialize<ArchiveSecurity>(reader.GetString(11)) ?? throw new InvalidDataException(),
        reader.GetInt64(12), reader.GetInt64(13), reader.IsDBNull(14) ? null : reader.GetString(14));

    private static void RefuseFileOperationRelocation(SqliteConnection db, string from, string to)
    {
        foreach (var table in new[] { "file_operations", "intake_operations" })
        {
            if (!RelocationTableExists(db, table)) continue;
            using var command = Command(db, $"""
                SELECT h.source,h.destination,b.path FROM {table} o JOIN books b ON b.id=o.book_id
                JOIN {table} h ON h.book_id=o.book_id WHERE o.state='pending'
                """);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2) }.Any(path => IsContained(from, path) || IsContained(to, path)))
                    throw new InvalidOperationException("Resolve pending file operations touching either relocation root before offline relocation.");
        }
    }
}
