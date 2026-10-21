using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BookDeletePreviewRequest([property: JsonRequired] string[] BookIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BookDeleteRequest([property: JsonRequired] string[] BookIds,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] bool Confirmed);
public sealed record BookDeleteItem(string BookId, string Title, string Source, bool CanDelete, string? Error);
public sealed record BookDeletePreview(string Revision, IReadOnlyList<BookDeleteItem> Items, bool CanDelete);
public sealed record BookDeleteFailure(string BookId, string Error);
public sealed record BookDeleteResult(IReadOnlyList<string> DeletedIds, IReadOnlyList<BookDeleteFailure> Failures, string Message);

public sealed partial class LibraryStore
{
    private sealed record BookDeletePlan(ArchiveCatalog Catalog, string CatalogState, string Root, string Identity,
        ArchiveSecurity Security, string DirectoryStamp, long Size, long Ticks, long ChangeSeconds, uint ChangeNanoseconds, string Hash);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int DeleteFileAt(int directory, string name, int flags);

    private static void ValidateBookDeletion(string[]? ids)
    {
        if (ids is null || ids.Length is < 1 or > 100 || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Select between 1 and 100 unique comicbooks.");
    }

    public BookDeletePreview PreviewBookDeletion(BookDeletePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBookDeletion(request.BookIds);
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            using var transaction = db.BeginTransaction(deferred: true);
            return PlanBookDeletion(db, transaction, request.BookIds, cancellationToken).Preview;
        }
        finally { scanLock.Release(); }
    }

    private (BookDeletePreview Preview, BookDeletePlan?[] Plans) PlanBookDeletion(SqliteConnection db,
        SqliteTransaction transaction, string[] ids, CancellationToken cancellationToken)
    {
        var items = new BookDeleteItem[ids.Length];
        var plans = new BookDeletePlan?[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = ids[index];
            var source = "";
            try
            {
                var catalog = ReadArchiveCatalog(db, transaction, ids[index]);
                title = catalog.Title;
                source = FileDisplayPath(catalog.Path);
                var state = DeletionCatalogState(db, transaction, catalog);
                if (!OperatingSystem.IsLinux()) throw new NotSupportedException();
                var root = FileOperationRootFor(catalog.Path);
                if (!IsArchive(catalog.Path)) throw new UnauthorizedAccessException();
                using var directory = OpenDeletionDirectory(catalog.Path);
                using var file = OpenDeletionSource(directory, catalog.Path);
                if (ArchiveStatx(file.SafeFileHandle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x7ff, out var stat) != 0)
                    throw new UnauthorizedAccessException();
                var identity = FileIdentity(catalog.Path);
                var security = ArchiveWritePermissions(root, catalog.Path, preserveInode: true);
                var directoryStamp = IntakeDestinationStamp(root, catalog.Path);
                var version = new FileInfo(catalog.Path);
                var hash = ArchiveHash(file, cancellationToken);
                var plan = new BookDeletePlan(catalog, state, root, identity, security, directoryStamp,
                    version.Length, version.LastWriteTimeUtc.Ticks, stat.ChangeSeconds, stat.ChangeNanoseconds, hash);
                CheckDeletionSource(plan, directory, file);
                plans[index] = plan;
                items[index] = new(catalog.Id, title, source, true, null);
            }
            catch (Exception exception) when (IsArchiveError(exception) || exception is FileOperationConflictException or KeyNotFoundException)
            {
                items[index] = new(ids[index], title, source, false, DeletionError(exception));
            }
        }
        return (new(FileRevision(new { plans, items }), items, items.All(item => item.CanDelete)), plans);
    }

    private static string DeletionCatalogState(SqliteConnection db, SqliteTransaction transaction, ArchiveCatalog catalog)
    {
        RefusePendingFileOperation(db, catalog.Id, transaction);
        using var command = Command(db, """
            SELECT 1 FROM file_operations WHERE state='pending' AND (source=$path OR destination=$path)
            UNION ALL SELECT 1 FROM intake_operations WHERE state='pending' AND (source=$path OR destination=$path) LIMIT 1
            """, ("$path", PathKey(catalog.Path)), ("$id", catalog.Id));
        command.Transaction = transaction;
        if (command.ExecuteScalar() is not null)
            throw new FileOperationConflictException("A pending file operation reserves this source. Resolve its recovery before deletion.");
        command.CommandText = "SELECT b.availability, p.revision FROM books b LEFT JOIN page_orders p ON p.book_id=b.id WHERE b.id=$id";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return FileRevision(new { Availability = reader.GetString(0), PageOrder = reader.IsDBNull(1) ? null : reader.GetString(1) });
    }

    private static SafeFileHandle OpenDeletionDirectory(string path)
    {
        const int flags = 0x10000 | 0x20000 | 0x80000; // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        var descriptor = ArchiveOpen("/", flags);
        if (descriptor < 0) throw new UnauthorizedAccessException();
        try
        {
            foreach (var part in Path.GetDirectoryName(path)!.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = FileOpenDirectoryAt(descriptor, part, flags);
                if (child < 0) throw new UnauthorizedAccessException();
                ArchiveClose(descriptor);
                descriptor = child;
            }
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }
        catch { ArchiveClose(descriptor); throw; }
    }

    private static FileStream OpenDeletionSource(SafeFileHandle directory, string path)
    {
        var descriptor = FileOpenDirectoryAt(directory.DangerousGetHandle().ToInt32(), Path.GetFileName(path), 0x20000 | 0x80000 | 0x800);
        if (descriptor < 0) throw new UnauthorizedAccessException();
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            if (ArchiveStatx(descriptor, "", 0x1000, 0x7ff, out var stat) != 0 || (stat.Mode & 0xf000) != 0x8000 || stat.Links != 1)
                throw new UnauthorizedAccessException();
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    private static void CheckDeletionSource(BookDeletePlan plan, SafeFileHandle directory, FileStream file)
    {
        var path = plan.Catalog.Path;
        var descriptor = directory.DangerousGetHandle().ToInt32();
        if (file.Length != plan.Size
            || File.GetLastWriteTimeUtc(path).Ticks != plan.Ticks
            || ArchiveWritePermissions(plan.Root, path, preserveInode: true) != plan.Security
            || IntakeDestinationStamp(plan.Root, path) != plan.DirectoryStamp
            || ArchiveStatx(descriptor, "", 0x1000, 0x7ff, out var parent) != 0
            || ArchiveStatx(-100, Path.GetDirectoryName(path)!, 0x100, 0x7ff, out var currentParent) != 0
            || parent.Inode != currentParent.Inode || parent.DeviceMajor != currentParent.DeviceMajor || parent.DeviceMinor != currentParent.DeviceMinor
            || ArchiveStatx(file.SafeFileHandle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x7ff, out var held) != 0
            || ArchiveStatx(descriptor, Path.GetFileName(path), 0x100, 0x7ff, out var current) != 0
            || $"{current.DeviceMajor:x}:{current.DeviceMinor:x}:{current.Inode:x}" != plan.Identity || current.Links != 1
            || held.SecurityStamp != current.SecurityStamp || current.ChangeSeconds != plan.ChangeSeconds
            || current.ChangeNanoseconds != plan.ChangeNanoseconds)
            throw new FileOperationConflictException("Comicbook identity, contents, or security changed. Preview deletion again.");
    }

    public BookDeleteResult DeleteBooks(BookDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        ValidateBookDeletion(request.BookIds);
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new FileOperationConflictException();
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            BookDeletePlan?[] plans;
            // Reserve the catalog writer across the complete preflight, not just the first selected book.
            using (var transaction = db.BeginTransaction(deferred: false))
            {
                var planned = PlanBookDeletion(db, transaction, request.BookIds, cancellationToken);
                if (!planned.Preview.CanDelete || planned.Preview.Revision != request.ExpectedRevision)
                    throw new FileOperationConflictException("Selection, comicbooks, or catalog changed. Preview deletion again; no files were deleted.");
                plans = planned.Plans;
                transaction.Commit();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = new List<string>();
            var failures = new List<BookDeleteFailure>();
            foreach (var nullable in plans)
            {
                var plan = nullable!;
                var sourceDeleted = false;
                var deletionAttempted = false;
                try
                {
                    // Once deletion starts, finish/report independently of a disconnected client. Never retry an unlink.
                    using var transaction = db.BeginTransaction(deferred: false);
                    if (ReadArchiveCatalog(db, transaction, plan.Catalog.Id) != plan.Catalog
                        || DeletionCatalogState(db, transaction, plan.Catalog) != plan.CatalogState)
                        throw new FileOperationConflictException("Catalog changed after preflight; this comicbook was not deleted.");
                    using var directory = OpenDeletionDirectory(plan.Catalog.Path);
                    using var file = OpenDeletionSource(directory, plan.Catalog.Path);
                    if (ArchiveHash(file, CancellationToken.None) != plan.Hash)
                        throw new FileOperationConflictException("Comicbook contents changed; this comicbook was not deleted.");
                    CheckDeletionSource(plan, directory, file);
                    var descriptor = directory.DangerousGetHandle().ToInt32();
                    deletionAttempted = true;
                    if (DeleteFileAt(descriptor, Path.GetFileName(plan.Catalog.Path), 0) != 0)
                        throw new IOException("Source deletion failed.");
                    sourceDeleted = true;
                    if (ArchiveFsync(descriptor) != 0) throw new IOException("Source directory flush failed.");
                    // Existing FK cascades remove private/shared per-book state, never retained original files.
                    using var remove = Command(db, "DELETE FROM books WHERE id=$id", ("$id", plan.Catalog.Id));
                    remove.Transaction = transaction;
                    if (remove.ExecuteNonQuery() != 1) throw new FileOperationConflictException();
                    transaction.Commit();
                    deleted.Add(plan.Catalog.Id);
                }
                catch (Exception exception) when (IsArchiveError(exception) || exception is FileOperationConflictException or SqliteException or KeyNotFoundException)
                {
                    // ponytail: no trash or deletion journal; existing missing-entry maintenance handles an interrupted catalog cleanup.
                    failures.Add(new(plan.Catalog.Id, sourceDeleted
                        ? "Source file was deleted, but catalog cleanup or directory durability could not be confirmed. Rescan, then remove its missing entry in Maintenance if it remains. No rollback is possible; retained backups were not removed."
                        : deletionAttempted
                            ? "Source deletion could not be confirmed; the catalog entry was retained. Inspect the source and rescan; if it is missing, remove its entry in Maintenance. No automatic retry was performed."
                            : DeletionError(exception)));
                }
            }
            return new(deleted, failures, failures.Count == 0
                ? $"Permanently deleted {deleted.Count} comicbook(s) and their catalog entries. Retained backups were not removed."
                : $"Deleted {deleted.Count} comicbook(s) and their catalog entries; {failures.Count} item(s) need attention. Review each failure; a source file may already be deleted. No automatic retry or rollback was performed.");
        }
        finally { scanLock.Release(); }
    }

    private static string DeletionError(Exception exception) => exception switch
    {
        FileOperationConflictException => exception.Message,
        KeyNotFoundException => "Comicbook no longer exists in the catalog.",
        NotSupportedException => "Safe permanent deletion currently requires Linux file-security support.",
        SqliteException => "Catalog is unavailable; this comicbook was not deleted.",
        _ => "Source is missing, inaccessible, read-only, unsafe, or changed; this comicbook was not deleted. Resolve the source before previewing again."
    };
}
