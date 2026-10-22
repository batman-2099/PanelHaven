using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveWriteRequest([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] bool Confirmed);
public sealed record ArchiveMetadataEntry(string Name, string? Before, string? After);
public sealed record ArchiveWritePreview(string BookId, string Title, string Location,
    string Revision, string SourceHash, IReadOnlyList<MetadataChange> Changes,
    IReadOnlyList<ArchiveMetadataEntry> Entries, bool CanApply);
public sealed record ArchiveWriteResult(string BookId);
public sealed class ArchiveWriteConflictException() : Exception("Comicbook or catalog changed. Preview again before applying.") { }
public sealed class ArchiveWriteRecoveryException(Exception inner) : Exception(
    "Comicbook replacement was published, but finalization failed. This operation cannot undo the replacement. Preserve the current file and rescan the library before further changes.", inner) { }

public sealed partial class LibraryStore
{
    private sealed record ArchiveCatalog(string Id, string Path, string Title, string Xml, string? BookXml,
        long Size, long Ticks, long Edited, string? Hash, int PageCount, string PageEdits);
    private sealed record ArchivePlan(ArchiveCatalog Catalog, string Root, long Size, long Ticks,
        ArchiveSecurity Security, ArchiveWritePreview Preview,
        Dictionary<string, byte[]> Replacements);
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

    public ArchiveWritePreview PreviewArchiveWrite(string id, CancellationToken cancellationToken = default)
    {
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return PlanArchiveWrite(ReadArchiveCatalog(db, null, id), cancellationToken).Preview;
        }
        finally { scanLock.Release(); }
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

    private ArchivePlan PlanArchiveWrite(ArchiveCatalog catalog, CancellationToken cancellationToken)
    {
        using (var db = Open()) RefusePendingFileOperation(db, catalog.Id);
        if (Path.GetExtension(catalog.Path).ToLowerInvariant() is not (".cbz" or ".zip"))
            throw new ArgumentException("Comicbook metadata write-back is supported only for CBZ/ZIP files.");
        var root = roots.FirstOrDefault(value => IsContained(value, catalog.Path)) ?? throw new UnauthorizedAccessException();
        var version = ReadSourceVersion(root, catalog.Path);
        var security = ArchiveWritePermissions(root, catalog.Path, preserveInode: true);
        if (OperatingSystem.IsLinux()) _ = ReadIntakeNfsAcl(catalog.Path);
        using var source = OpenArchive(root, catalog.Path);
        var hash = ArchiveHash(source, cancellationToken);
        source.Position = 0;
        using var archive = new ComicArchive(source, catalog.Path, cancellationToken);
        var metadata = archive.ReadMetadata(cancellationToken);
        var beforeInfo = metadata.Info is null ? null : ReadXml(metadata.Info, "ComicInfo", cancellationToken);
        var beforeBook = metadata.Book is null ? null : ReadXml(metadata.Book, "ComicBook", cancellationToken);
        var desiredInfo = ParseXml(catalog.Xml, "ComicInfo");
        var desiredBook = catalog.BookXml is null ? null : ParseXml(catalog.BookXml, "ComicBook");
        var afterInfo = beforeInfo is null ? new XDocument(desiredInfo) : new XDocument(beforeInfo);
        var afterBook = beforeBook is null ? desiredBook is null ? null : new XDocument(desiredBook) : new XDocument(beforeBook);
        MergeEmbeddedMetadata(afterInfo.Root!, afterBook?.Root, desiredInfo.Root!, desiredBook?.Root,
            catalog.PageEdits, catalog.PageCount, archive.Pages.Count);
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
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
                if (text is not null) replacements.Add(name, Encoding.UTF8.GetBytes(text));
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
        var revision = FileRevision(new { catalog, hash, version.Size, version.Ticks, security, entries });
        using var latest = OpenArchive(root, catalog.Path);
        if (ArchiveWritePermissions(root, catalog.Path, preserveInode: true) != security
            || ReadSourceVersion(root, catalog.Path) != version || ArchiveHash(latest, cancellationToken) != hash)
            throw new ArchiveWriteConflictException();
        return new(catalog, root, version.Size, version.Ticks, security,
            new(catalog.Id, catalog.Title, SourceLocation(catalog.Path).Location, revision, hash,
                changes, entries, replacements.Count != 0), replacements);
    }

    public ArchiveWriteResult ApplyArchiveWrite(string id, ArchiveWriteRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new ArchiveWriteConflictException();
        // ponytail: share the scan lock; per-book locks only if archive-write throughput ever warrants them.
        scanLock.Wait(cancellationToken);
        string? staged = null;
        try
        {
            using var db = Open();
            var plan = PlanArchiveWrite(ReadArchiveCatalog(db, null, id), cancellationToken);
            if (plan.Preview.Revision != request.ExpectedRevision) throw new ArchiveWriteConflictException();
            if (!plan.Preview.CanApply) return new(id);
            staged = Path.Combine(Path.GetDirectoryName(plan.Catalog.Path)!, ".panelhaven-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = NewPrivateFile(staged))
            {
                WriteArchiveStage(plan.Root, plan.Catalog.Path, plan.Replacements, output, cancellationToken);
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
            }
            ApplyArchivePermissions(staged, plan.Security,
                OperatingSystem.IsLinux() ? ReadIntakeNfsAcl(plan.Catalog.Path) : null);
            // Persist the serving guard before publication so interruption cannot expose a stale catalog.
            using (var reservation = db.BeginTransaction(deferred: false))
            {
                if (ReadArchiveCatalog(db, reservation, id) != plan.Catalog) throw new ArchiveWriteConflictException();
                CheckArchiveSource(plan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using var reserve = Command(db, "UPDATE books SET availability='unavailable',edited=1 WHERE id=$id", ("$id", id));
                reserve.Transaction = reservation;
                if (reserve.ExecuteNonQuery() != 1) throw new ArchiveWriteConflictException();
                reservation.Commit();
            }
            using var transaction = db.BeginTransaction(deferred: false);
            if (ReadArchiveCatalog(db, transaction, id) != (plan.Catalog with { Edited = 1 })) throw new ArchiveWriteConflictException();
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
                if (ArchiveWritePermissions(plan.Root, plan.Catalog.Path, preserveInode: true) != plan.Security)
                    throw new UnauthorizedAccessException("Published comicbook security did not verify.");
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
            catch (Exception exception) when (replaced)
            {
                // There is no retained original. Leave the published file and durable rescan guard intact.
                throw new ArchiveWriteRecoveryException(exception);
            }
            return new(id);
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

    private static void ApplyArchivePermissions(string path, ArchiveSecurity security, byte[]? nfsAcl = null)
    {
        if (OperatingSystem.IsLinux() && ReadIntakeNfsAcl(path) is not null && nfsAcl is null)
            throw new UnauthorizedAccessException("Destination inherits an ACL absent from the source; no ACL will be stripped.");
        if (security.Owner.HasValue && ArchiveChown(path, security.Owner.Value, security.Group!.Value) != 0)
            throw new UnauthorizedAccessException("Cannot preserve the comicbook owner and group.");
        if (!OperatingSystem.IsWindows() && security.Mode.HasValue) File.SetUnixFileMode(path, security.Mode.Value);
        File.SetAttributes(path, security.Attributes);
        if (nfsAcl is not null)
        {
            var descriptor = ArchiveOpen(path, 0x20000 | 0x80000 | 0x800);
            if (descriptor < 0) throw new UnauthorizedAccessException("Cannot open comicbook staging security.");
            try
            {
                if (IntakeSetXattr(descriptor, IntakeNfsAclName, nfsAcl, (nuint)nfsAcl.Length, 0) != 0)
                    throw new UnauthorizedAccessException("Cannot preserve the source NFS4 ACL.");
            }
            finally { ArchiveClose(descriptor); }
        }
        var copied = ArchiveWritePermissions(Path.GetDirectoryName(path)!, path, preserveInode: true);
        const FileAttributes nameAttributes = FileAttributes.Hidden | FileAttributes.Normal;
        if ((copied with { Attributes = security.Attributes }) != security
            || (copied.Attributes & ~nameAttributes) != (security.Attributes & ~nameAttributes))
            throw new UnauthorizedAccessException("Copied comicbook security did not verify.");
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

    private static void WriteArchiveStage(string root, string path, Dictionary<string, byte[]> replacements,
        FileStream output, CancellationToken cancellationToken)
    {
        using var input = OpenArchive(root, path);
        using var source = new ZipArchive(input, ZipArchiveMode.Read, true);
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            destination.Comment = source.Comment;
            var pending = new Dictionary<string, byte[]>(replacements, StringComparer.OrdinalIgnoreCase);
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
            || ArchiveWritePermissions(plan.Root, plan.Catalog.Path, preserveInode: true) != plan.Security) throw new ArchiveWriteConflictException();
        using var current = OpenArchive(plan.Root, plan.Catalog.Path);
        if (ArchiveHash(current, cancellationToken) != plan.Preview.SourceHash) throw new ArchiveWriteConflictException();
    }
}
