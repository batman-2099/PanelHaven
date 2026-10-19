using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveWriteRequest([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] bool Confirmed);
public sealed record ArchiveMetadataEntry(string Name, string? Before, string? After);
public sealed record ArchiveWritePreview(string BookId, string Title, string Location, string Operation,
    string Revision, string? BackupId, string SourceHash, IReadOnlyList<MetadataChange> Changes,
    IReadOnlyList<ArchiveMetadataEntry> Entries, bool CanApply);
public sealed record ArchiveWriteResult(string BookId, string? BackupId, bool Restored);
public sealed record ArchiveBackup(string Id, string CreatedAt, string OriginalHash, string WrittenHash, long OriginalSize);
public sealed class ArchiveWriteConflictException() : Exception("Comicbook or catalog changed. Preview again before applying.") { }

public sealed partial class LibraryStore
{
    private sealed record ArchiveCatalog(string Id, string Path, string Title, string Xml, string? BookXml,
        long Size, long Ticks, long Edited, string? Hash, int PageCount, string PageEdits);
    private sealed record ArchiveManifest(string Id, string CreatedAt, string BookId, string SourcePath,
        string OriginalHash, string WrittenHash, long OriginalSize);
    private sealed record ArchivePlan(ArchiveCatalog Catalog, string Root, long Size, long Ticks,
        ArchiveSecurity Security, ArchiveWritePreview Preview,
        Dictionary<string, byte[]> Replacements, ArchiveManifest? Restore);
    private sealed record ArchiveSecurity(UnixFileMode? Mode, FileAttributes Attributes, uint? Owner, uint? Group,
        string? ExtendedAttributesHash = null);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    // Linux statx has a fixed UAPI layout; unlike struct stat these owner offsets do not vary with libc.
    internal struct ArchiveStat
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
        public readonly (uint, uint, ulong, ushort, uint, uint, uint, long, uint) SecurityStamp =>
            (DeviceMajor, DeviceMinor, Inode, Mode, Owner, Group, Links, ChangeSeconds, ChangeNanoseconds);
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    internal static extern int ArchiveStatx(int directory, string path, int flags, uint mask, out ArchiveStat stat);
    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    private static extern nint ArchiveListXattr(string path, nint list, nuint size);
    [DllImport("libc", EntryPoint = "flistxattr", SetLastError = true)]
    private static extern nint ArchiveListFileXattr(int descriptor, [Out] byte[]? list, nuint size);
    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)]
    private static extern nint ArchiveGetFileXattr(int descriptor, byte[] name, [Out] byte[] value, nuint size);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ArchiveChown(string path, uint owner, uint group);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int ArchiveOpen(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int ArchiveFsync(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int ArchiveClose(int descriptor);

    public ArchiveWritePreview PreviewArchiveWrite(string id, CancellationToken cancellationToken = default) =>
        PreviewArchiveOperation(id, null, cancellationToken);

    public ArchiveWritePreview PreviewArchiveRestore(string id, string backupId, CancellationToken cancellationToken = default) =>
        PreviewArchiveOperation(id, backupId, cancellationToken);

    private ArchiveWritePreview PreviewArchiveOperation(string id, string? backupId, CancellationToken cancellationToken)
    {
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return PlanArchiveOperation(ReadArchiveCatalog(db, null, id), backupId, cancellationToken).Preview;
        }
        finally { scanLock.Release(); }
    }

    public ArchiveWriteResult ApplyArchiveWrite(string id, ArchiveWriteRequest request, CancellationToken cancellationToken = default) =>
        ApplyArchiveOperation(id, null, request, cancellationToken);

    public ArchiveWriteResult RestoreArchiveBackup(string id, string backupId, ArchiveWriteRequest request,
        CancellationToken cancellationToken = default) => ApplyArchiveOperation(id, backupId, request, cancellationToken);

    public IReadOnlyList<ArchiveBackup> GetArchiveBackups(string id)
    {
        using var db = Open();
        var catalog = ReadArchiveCatalog(db, null, id);
        var backups = new List<ArchiveBackup>();
        foreach (var owner in ArchiveBackupOwners(db, id))
        {
            var directory = ArchiveBackupDirectory(owner);
            if (!Directory.Exists(directory)) continue;
            EnsureSafePath(archiveBackupRoot, directory);
            foreach (var backup in Directory.EnumerateDirectories(directory).Select(Path.GetFileName)
                .Where(value => value is not null && Guid.TryParseExact(value, "N", out _))
                .Where(value => File.Exists(Path.Combine(directory, value!, "manifest.json"))))
            {
                var manifest = ReadArchiveManifest(catalog.Id, backup!, catalog.Path);
                backups.Add(new(manifest.Id, manifest.CreatedAt, manifest.OriginalHash, manifest.WrittenHash, manifest.OriginalSize));
            }
        }
        return backups.OrderByDescending(value => value.CreatedAt, StringComparer.Ordinal).ToArray();
    }

    private static ArchiveCatalog ReadArchiveCatalog(SqliteConnection db, SqliteTransaction? transaction, string id)
    {
        using var command = Command(db, "SELECT path,title,info_xml,book_xml,file_size,modified_ticks,edited,content_hash,page_count FROM books WHERE id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8), ReadPageEditIntents(db, transaction, id));
    }

    private ArchivePlan PlanArchiveOperation(ArchiveCatalog catalog, string? backupId, CancellationToken cancellationToken)
    {
        using (var db = Open()) RefusePendingFileOperation(db, catalog.Id);
        if (Path.GetExtension(catalog.Path).ToLowerInvariant() is not (".cbz" or ".zip"))
            throw new ArgumentException("Comicbook metadata write-back is supported only for CBZ/ZIP files.");
        var root = roots.FirstOrDefault(value => IsContained(value, catalog.Path)) ?? throw new UnauthorizedAccessException();
        if (IsContained(archiveBackupRoot, catalog.Path)) throw new UnauthorizedAccessException();
        var version = ReadSourceVersion(root, catalog.Path);
        var security = ArchiveWritePermissions(root, catalog.Path);
        using var source = OpenArchive(root, catalog.Path);
        var hash = ArchiveHash(source, cancellationToken);
        source.Position = 0;
        using var archive = new ComicArchive(source, catalog.Path, cancellationToken);
        var metadata = archive.ReadMetadata(cancellationToken);
        var beforeInfo = metadata.Info is null ? null : ReadXml(metadata.Info, "ComicInfo", cancellationToken);
        var beforeBook = metadata.Book is null ? null : ReadXml(metadata.Book, "ComicBook", cancellationToken);
        XDocument? afterInfo, afterBook;
        ArchiveManifest? restore = null;
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (backupId is not null)
        {
            restore = ReadArchiveManifest(catalog.Id, backupId, catalog.Path);
            if (hash != restore.WrittenHash) throw new ArchiveWriteConflictException();
            using var original = OpenVerifiedBackup(catalog.Path, restore, cancellationToken);
            using var originalArchive = new ComicArchive(original, catalog.Path, cancellationToken);
            var originalMetadata = originalArchive.ReadMetadata(cancellationToken);
            afterInfo = originalMetadata.Info is null ? null : ReadXml(originalMetadata.Info, "ComicInfo", cancellationToken);
            afterBook = originalMetadata.Book is null ? null : ReadXml(originalMetadata.Book, "ComicBook", cancellationToken);
        }
        else
        {
            var desiredInfo = ParseXml(catalog.Xml, "ComicInfo");
            var desiredBook = catalog.BookXml is null ? null : ParseXml(catalog.BookXml, "ComicBook");
            // Only edited page attributes are authoritative; untouched source pages and extensions survive.
            afterInfo = beforeInfo is null ? new XDocument(desiredInfo) : new XDocument(beforeInfo);
            foreach (var field in MetadataFields)
                PatchScalar(afterInfo.Root!, field.Name, desiredInfo.Root!.Element(desiredInfo.Root.Name.Namespace + field.Name)?.Value);
            MergeEditedPages(afterInfo.Root!, desiredInfo.Root!, catalog.PageEdits, catalog.PageCount, archive.Pages.Count);
            afterBook = beforeBook is null ? desiredBook is null ? null : new XDocument(desiredBook) : new XDocument(beforeBook);
            if (afterBook is not null)
            {
                foreach (var field in MetadataFields)
                    if (afterBook.Root!.Elements(afterBook.Root.Name.Namespace + field.Name).Any())
                        PatchScalar(afterBook.Root, field.Name, desiredInfo.Root!.Element(desiredInfo.Root.Name.Namespace + field.Name)?.Value);
                PatchScalar(afterBook.Root!, "CustomValuesStore", desiredBook?.Root?.Element(desiredBook.Root.Name.Namespace + "CustomValuesStore")?.Value);
            }
        }
        var entries = new List<ArchiveMetadataEntry>();
        source.Position = 0;
        using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
        {
            void Compare(string canonical, XDocument? before, XDocument? after)
            {
                if (XNode.DeepEquals(before, after)) return;
                var name = zip.Entries.FirstOrDefault(entry => entry.FullName.Equals(canonical, StringComparison.OrdinalIgnoreCase))?.FullName ?? canonical;
                var text = after is null ? null : SerializeMetadata(after);
                entries.Add(new(name, before?.ToString(SaveOptions.DisableFormatting), text));
                if (backupId is null && text is not null) replacements.Add(name, Encoding.UTF8.GetBytes(text));
            }
            Compare("ComicInfo.xml", beforeInfo, afterInfo);
            Compare("ComicBook.xml", beforeBook, afterBook);
        }
        var beforeValues = ReadMetadata((beforeInfo ?? beforeBook)?.Root ?? new XElement("ComicInfo"), beforeBook?.Root);
        var afterValues = ReadMetadata((afterInfo ?? afterBook)?.Root ?? new XElement("ComicInfo"), afterBook?.Root);
        var changes = new List<MetadataChange>();
        void Diff(Dictionary<string, string> before, Dictionary<string, string> after, bool custom)
        {
            foreach (var name in before.Keys.Union(after.Keys, custom ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                before.TryGetValue(name, out var old);
                after.TryGetValue(name, out var next);
                if (old != next) changes.Add(new(name, custom, old, next));
            }
        }
        Diff(beforeValues.Fields, afterValues.Fields, false);
        Diff(beforeValues.Custom, afterValues.Custom, true);
        var operation = backupId is null ? "write" : "restore";
        using var revisionHash = SHA256.Create();
        using var binding = new CryptoStream(Stream.Null, revisionHash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(binding, new { catalog, hash, version.Size, version.Ticks, security, operation, restore, entries });
        binding.FlushFinalBlock();
        var revision = Convert.ToHexString(revisionHash.Hash!);
        using var latest = OpenArchive(root, catalog.Path);
        if (ReadSourceVersion(root, catalog.Path) != version || ArchiveHash(latest, cancellationToken) != hash)
            throw new ArchiveWriteConflictException();
        return new(catalog, root, version.Size, version.Ticks, security,
            new(catalog.Id, catalog.Title, SourceLocation(catalog.Path).Location, operation, revision, backupId, hash,
                changes, entries, backupId is null ? replacements.Count != 0 : hash != restore!.OriginalHash), replacements, restore);
    }

    private ArchiveWriteResult ApplyArchiveOperation(string id, string? backupId, ArchiveWriteRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new ArchiveWriteConflictException();
        // ponytail: share the scan lock; per-book locks only if archive-write throughput ever warrants them.
        scanLock.Wait(cancellationToken);
        string? staged = null;
        try
        {
            using var db = Open();
            var plan = PlanArchiveOperation(ReadArchiveCatalog(db, null, id), backupId, cancellationToken);
            if (plan.Preview.Revision != request.ExpectedRevision) throw new ArchiveWriteConflictException();
            if (!plan.Preview.CanApply) return new(id, null, backupId is not null);
            staged = Path.Combine(Path.GetDirectoryName(plan.Catalog.Path)!, ".panelhaven-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = NewPrivateFile(staged))
            {
                if (plan.Restore is not null)
                {
                    using var original = OpenVerifiedBackup(plan.Catalog.Path, plan.Restore, cancellationToken);
                    CopyArchiveBytes(original, output, MaxArchiveBytes, cancellationToken);
                }
                else WriteArchiveStage(plan, output, cancellationToken);
                output.Flush(true);
            }
            string writtenHash;
            int pages;
            using (var stagedFile = OpenArchive(plan.Root, staged, plan.Catalog.Path))
            {
                using var checkedArchive = new ComicArchive(stagedFile, plan.Catalog.Path, cancellationToken);
                var checkedMetadata = checkedArchive.ReadMetadata(cancellationToken);
                if (checkedMetadata.Info is not null) ReadXml(checkedMetadata.Info, "ComicInfo", cancellationToken);
                if (checkedMetadata.Book is not null) ReadXml(checkedMetadata.Book, "ComicBook", cancellationToken);
                pages = checkedArchive.Pages.Count;
                writtenHash = ArchiveHash(stagedFile, cancellationToken);
                if (plan.Restore is not null && writtenHash != plan.Restore.OriginalHash) throw new InvalidDataException("Comicbook backup verification failed.");
            }
            var manifest = SaveArchiveBackup(plan, writtenHash, cancellationToken);
            ApplyArchivePermissions(staged, plan.Security);
            // Metadata edits take the same SQLite write reservation. No DB lock during compression or backup copying.
            using var transaction = db.BeginTransaction(deferred: false);
            if (ReadArchiveCatalog(db, transaction, id) != plan.Catalog) throw new ArchiveWriteConflictException();
            CheckArchiveSource(plan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var replaced = false;
            try
            {
                File.Replace(staged, plan.Catalog.Path, null);
                replaced = true;
                staged = null;
                FlushArchivePath(Path.GetDirectoryName(plan.Catalog.Path)!, true);
                var version = ReadSourceVersion(plan.Root, plan.Catalog.Path);
                using var current = OpenArchive(plan.Root, plan.Catalog.Path);
                if (ArchiveHash(current, default) != writtenHash) throw new ArchiveWriteConflictException();
                var (location, folder) = SourceLocation(plan.Catalog.Path);
                using var update = Command(db, """
                    UPDATE books SET file_size=$size,modified_ticks=$ticks,content_hash=$hash,page_count=$pages,
                        availability='available',location=$location,folder=$folder,edited=1 WHERE id=$id
                    """, ("$size", version.Size), ("$ticks", version.Ticks), ("$hash", writtenHash), ("$pages", pages),
                    ("$location", location), ("$folder", folder), ("$id", id));
                update.Transaction = transaction;
                if (update.ExecuteNonQuery() != 1) throw new ArchiveWriteConflictException();
                transaction.Commit();
            }
            catch
            {
                if (replaced) RollbackArchive(plan, manifest, writtenHash);
                throw;
            }
            return new(id, manifest.Id, backupId is not null);
        }
        finally
        {
            try { if (staged is not null) File.Delete(staged); }
            finally { scanLock.Release(); }
        }
    }

    private static ArchiveSecurity ArchiveWritePermissions(string root, string path, bool preserveInode = false)
    {
        EnsureSafePath(root, path);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Encrypted | FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The source comicbook is read-only or has unsupported file security.");
        UnixFileMode? mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
        if (mode.HasValue && (mode.Value & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            throw new UnauthorizedAccessException("The source comicbook is read-only.");
        uint? owner = null, group = null;
        string? extendedAttributesHash = null;
        ArchiveStat stat = default;
        if (!OperatingSystem.IsWindows())
        {
            if (!OperatingSystem.IsLinux()) throw new NotSupportedException("Comicbook writes require Linux or Windows file-security support.");
            if ((mode!.Value & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0
                || (!preserveInode && ArchiveListXattr(path, 0, 0) != 0)
                || ArchiveStatx(-100, path, 0x100, 0x7ff, out stat) != 0 || (stat.Mask & 0x19f) != 0x19f
                || (stat.Mode & 0xf000) != 0x8000 || (stat.Mode & 0xfff) != (int)mode.Value)
                throw new UnauthorizedAccessException("Comicbook writes refuse special permissions or extended attributes (including ACLs).");
            owner = stat.Owner;
            group = stat.Group;
            if (preserveInode) extendedAttributesHash = ReadArchiveExtendedAttributesHash(path);
        }
        // Also ask the OS: write permission on the directory alone must not bypass a read-only source.
        using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        EnsureSafePath(root, path);
        if (OperatingSystem.IsLinux()
            && (ArchiveStatx(-100, path, 0x100, 0x7ff, out var latest) != 0 || (latest.Mask & 0x19f) != 0x19f
                || latest.SecurityStamp != stat.SecurityStamp || File.GetAttributes(path) != attributes))
            throw new UnauthorizedAccessException("Comicbook security changed while it was being inspected.");
        return new(mode, attributes, owner, group, extendedAttributesHash);
    }

    private static string? ReadArchiveExtendedAttributesHash(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new NotSupportedException("Extended attribute inspection requires Linux.");
        // Open once, without following a final symlink; nonblocking also avoids opening a substituted FIFO.
        var descriptor = ArchiveOpen(path, 0x20000 | 0x80000 | 0x800);
        if (descriptor < 0) throw new UnauthorizedAccessException("Cannot inspect comicbook extended attributes.");
        try
        {
            if (ArchiveStatx(descriptor, "", 0x1000, 0x7ff, out var before) != 0 || (before.Mask & 0x19f) != 0x19f
                || (before.Mode & 0xf000) is not (0x8000 or 0x4000))
                throw new UnauthorizedAccessException("Cannot verify comicbook extended attribute ownership.");
            var first = ReadSnapshot();
            if (ReadSnapshot() != first
                || ArchiveStatx(-100, path, 0x100, 0x7ff, out var after) != 0 || (after.Mask & 0x19f) != 0x19f
                || before.SecurityStamp != after.SecurityStamp)
                throw new UnauthorizedAccessException("Comicbook extended attributes changed while they were being inspected.");
            return first;

            string? ReadSnapshot()
            {
                // Linux caps each list/value at 64 KiB. Bound aggregate work too; never truncate an ACL.
                const int limit = 65536, totalLimit = 16 * 1024 * 1024;
                var size = ArchiveListFileXattr(descriptor, null, 0);
                if (size < 0 || size > limit)
                    throw new UnauthorizedAccessException("Cannot inspect all comicbook extended attributes within safety limits.");
                if (size == 0) return null;
                var list = ArrayPool<byte>.Shared.Rent((int)size);
                var value = ArrayPool<byte>.Shared.Rent(limit);
                var name = ArrayPool<byte>.Shared.Rent(256);
                try
                {
                    if (ArchiveListFileXattr(descriptor, list, (nuint)size) != size)
                        throw new UnauthorizedAccessException("Comicbook extended attribute names changed during inspection.");
                    var names = new List<(int Offset, int Length)>();
                    for (var offset = 0; offset < (int)size;)
                    {
                        var length = list.AsSpan(offset, (int)size - offset).IndexOf((byte)0);
                        if (length is <= 0 or > 255)
                            throw new UnauthorizedAccessException("Comicbook extended attribute names are invalid or incomplete.");
                        names.Add((offset, length));
                        offset += length + 1;
                    }
                    names.Sort((left, right) => list.AsSpan(left.Offset, left.Length).SequenceCompareTo(list.AsSpan(right.Offset, right.Length)));
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    Span<byte> lengths = stackalloc byte[8];
                    var total = 0;
                    for (var index = 0; index < names.Count; index++)
                    {
                        var entry = names[index];
                        var bytes = list.AsSpan(entry.Offset, entry.Length);
                        if (index != 0 && bytes.SequenceEqual(list.AsSpan(names[index - 1].Offset, names[index - 1].Length)))
                            throw new UnauthorizedAccessException("Comicbook extended attribute names are duplicated.");
                        bytes.CopyTo(name);
                        name[entry.Length] = 0;
                        var length = ArchiveGetFileXattr(descriptor, name, value, 0);
                        if (length < 0 || length > limit || (total += (int)length) > totalLimit)
                            throw new UnauthorizedAccessException("Cannot inspect all comicbook extended attribute values within safety limits.");
                        if (ArchiveGetFileXattr(descriptor, name, value, (nuint)length) != length)
                            throw new UnauthorizedAccessException("Comicbook extended attribute values changed during inspection.");
                        BinaryPrimitives.WriteInt32LittleEndian(lengths, entry.Length);
                        BinaryPrimitives.WriteInt32LittleEndian(lengths[4..], (int)length);
                        hash.AppendData(lengths);
                        hash.AppendData(bytes);
                        hash.AppendData(value, 0, (int)length);
                    }
                    return Convert.ToHexString(hash.GetHashAndReset());
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(name);
                    ArrayPool<byte>.Shared.Return(value);
                    ArrayPool<byte>.Shared.Return(list);
                }
            }
        }
        finally { ArchiveClose(descriptor); }
    }

    private static void ApplyArchivePermissions(string path, ArchiveSecurity security)
    {
        if (security.Owner.HasValue && ArchiveChown(path, security.Owner.Value, security.Group!.Value) != 0)
            throw new UnauthorizedAccessException("Cannot preserve the comicbook owner and group.");
        if (OperatingSystem.IsLinux() && ArchiveListXattr(path, 0, 0) != 0)
            throw new UnauthorizedAccessException("Comicbook staging inherited unsupported extended attributes.");
        if (!OperatingSystem.IsWindows() && security.Mode.HasValue) File.SetUnixFileMode(path, security.Mode.Value);
        File.SetAttributes(path, security.Attributes);
        FlushArchivePath(path, false);
    }

    internal static void FlushArchivePath(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        var descriptor = ArchiveOpen(path, 0x20000 | (directory ? 0x10000 : 0));
        if (descriptor < 0) throw new IOException("Cannot flush comicbook data or its directory.");
        try
        {
            if (ArchiveFsync(descriptor) != 0) throw new IOException("Cannot flush comicbook data or its directory.");
        }
        finally { ArchiveClose(descriptor); }
    }

    private static FileStream NewPrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static string ArchiveHash(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static long CopyArchiveBytes(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + read);
                if (total > limit) throw new InvalidDataException("Comicbook exceeds its declared size or safety limit.");
                destination.Write(buffer, 0, read);
            }
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void WriteArchiveStage(ArchivePlan plan, FileStream output, CancellationToken cancellationToken)
    {
        using var input = OpenArchive(plan.Root, plan.Catalog.Path);
        using var source = new ZipArchive(input, ZipArchiveMode.Read, true);
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            destination.Comment = source.Comment;
            var pending = new Dictionary<string, byte[]>(plan.Replacements, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = destination.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                target.LastWriteTime = entry.LastWriteTime;
                target.ExternalAttributes = entry.ExternalAttributes;
                target.Comment = entry.Comment;
                using (var stream = target.Open())
                {
                    if (pending.Remove(entry.FullName, out var replacement)) stream.Write(replacement);
                    else
                    {
                        using var original = entry.Open();
                        if (CopyArchiveBytes(original, stream, entry.Length, cancellationToken) != entry.Length) throw new InvalidDataException();
                    }
                }
                if (output.Length > MaxArchiveBytes) throw new InvalidDataException("Written comicbook exceeds the 1 GiB limit.");
            }
            foreach (var (name, bytes) in pending)
            {
                using var stream = destination.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(bytes);
            }
        }
        if (output.Length > MaxArchiveBytes) throw new InvalidDataException("Written comicbook exceeds the 1 GiB limit.");
    }

    private void CheckArchiveSource(ArchivePlan plan, CancellationToken cancellationToken)
    {
        if (ReadSourceVersion(plan.Root, plan.Catalog.Path) != (plan.Size, plan.Ticks)
            || ArchiveWritePermissions(plan.Root, plan.Catalog.Path) != plan.Security) throw new ArchiveWriteConflictException();
        using var current = OpenArchive(plan.Root, plan.Catalog.Path);
        if (ArchiveHash(current, cancellationToken) != plan.Preview.SourceHash) throw new ArchiveWriteConflictException();
    }

    private string ArchiveBackupDirectory(string id, string? backupId = null)
    {
        if (!Guid.TryParseExact(id, "N", out _) || backupId is not null && !Guid.TryParseExact(backupId, "N", out _))
            throw new ArgumentException("Invalid comicbook backup identifier.");
        return backupId is null ? Path.Combine(archiveBackupRoot, id) : Path.Combine(archiveBackupRoot, id, backupId);
    }

    private static void PrivateArchiveDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        EnsureSafePath(parent, parent);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        EnsureSafePath(parent, path);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("Comicbook backups require a private directory.");
        FlushArchivePath(parent, true);
    }

    private ArchiveManifest SaveArchiveBackup(ArchivePlan plan, string writtenHash, CancellationToken cancellationToken)
    {
        PrivateArchiveDirectory(archiveBackupRoot);
        PrivateArchiveDirectory(ArchiveBackupDirectory(plan.Catalog.Id));
        var manifest = new ArchiveManifest(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O"), plan.Catalog.Id,
            plan.Catalog.Path, plan.Preview.SourceHash, writtenHash, plan.Size);
        var directory = ArchiveBackupDirectory(plan.Catalog.Id, manifest.Id);
        PrivateArchiveDirectory(directory);
        var payload = Path.Combine(directory, "original.bin");
        using (var original = OpenArchive(plan.Root, plan.Catalog.Path))
        using (var output = NewPrivateFile(payload))
        {
            if (CopyArchiveBytes(original, output, MaxArchiveBytes, cancellationToken) != manifest.OriginalSize
                || ArchiveHash(output, cancellationToken) != manifest.OriginalHash) throw new ArchiveWriteConflictException();
            output.Flush(true);
        }
        // Publish the manifest last. No automatic pruning: interrupted copies and verified originals remain recoverable.
        var temporary = Path.Combine(directory, "manifest.tmp");
        using (var output = NewPrivateFile(temporary))
        {
            JsonSerializer.Serialize(output, manifest);
            output.Flush(true);
        }
        File.Move(temporary, Path.Combine(directory, "manifest.json"));
        FlushArchivePath(directory, true);
        using var verified = OpenVerifiedBackup(plan.Catalog.Path, manifest, cancellationToken);
        return manifest;
    }

    private ArchiveManifest ReadArchiveManifest(string bookId, string backupId, string? expectedPath = null)
    {
        var owner = bookId;
        if (expectedPath is not null)
        {
            using var db = Open();
            var owners = ArchiveBackupOwners(db, bookId)
                .Where(candidate => File.Exists(Path.Combine(ArchiveBackupDirectory(candidate, backupId), "manifest.json"))).ToArray();
            if (owners.Length != 1) throw new InvalidDataException("Comicbook backup is missing or has ambiguous ownership.");
            owner = owners[0];
        }
        var directory = ArchiveBackupDirectory(owner, backupId);
        var path = Path.Combine(directory, "manifest.json");
        EnsureSafePath(archiveBackupRoot, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 16384) throw new InvalidDataException("Invalid comicbook backup manifest.");
        ArchiveManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ArchiveManifest>(stream); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid comicbook backup manifest.", ex); }
        if (manifest is null || manifest.Id != backupId || manifest.BookId != owner
            || string.IsNullOrEmpty(manifest.SourcePath) || !Path.IsPathFullyQualified(manifest.SourcePath)
            || Path.GetExtension(manifest.SourcePath).ToLowerInvariant() is not (".cbz" or ".zip")
            || expectedPath is not null && !FileOperationProvesPath(bookId, manifest.SourcePath, expectedPath)
            || manifest.OriginalSize is < 22 or > MaxArchiveBytes || !ValidArchiveHash(manifest.OriginalHash)
            || !ValidArchiveHash(manifest.WrittenHash) || !DateTimeOffset.TryParse(manifest.CreatedAt, out _))
            throw new InvalidDataException("Invalid comicbook backup manifest.");
        return manifest;
    }

    private static bool ValidArchiveHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

    private FileStream OpenVerifiedBackup(string sourcePath, ArchiveManifest manifest, CancellationToken cancellationToken)
    {
        var path = Path.Combine(ArchiveBackupDirectory(manifest.BookId, manifest.Id), "original.bin");
        var stream = OpenArchive(archiveBackupRoot, path, sourcePath);
        try
        {
            if (stream.Length != manifest.OriginalSize || ArchiveHash(stream, cancellationToken) != manifest.OriginalHash)
                throw new InvalidDataException("Comicbook backup verification failed.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private void RollbackArchive(ArchivePlan plan, ArchiveManifest manifest, string writtenHash)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(plan.Catalog.Path)!, ".panelhaven-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var original = OpenVerifiedBackup(plan.Catalog.Path, manifest, default))
            using (var output = NewPrivateFile(temporary))
            {
                CopyArchiveBytes(original, output, MaxArchiveBytes, default);
                output.Flush(true);
            }
            ApplyArchivePermissions(temporary, plan.Security);
            File.SetLastWriteTimeUtc(temporary, new DateTime(plan.Ticks, DateTimeKind.Utc));
            FlushArchivePath(temporary, false);
            EnsureSafePath(plan.Root, plan.Catalog.Path);
            using (var current = OpenArchive(plan.Root, plan.Catalog.Path))
                if (ArchiveHash(current, default) != writtenHash) return; // Never overwrite a later external edit.
            File.Replace(temporary, plan.Catalog.Path, null);
            FlushArchivePath(Path.GetDirectoryName(plan.Catalog.Path)!, true);
        }
        catch (Exception ex) when (IsArchiveError(ex))
        {
            // The durable original and manifest remain available to explicit restore after failed rollback.
        }
        finally { File.Delete(temporary); }
    }
}
