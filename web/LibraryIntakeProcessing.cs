using System.Globalization;
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
public sealed record IntakePrepareRequest([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int IssueId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int VolumeId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntakeProcessRequest([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int IssueId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int VolumeId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] bool Confirmed);
public sealed record IntakeProcessingPreview(string Revision, string Source, string Destination, bool Converted, string[] Warnings);
public sealed record IntakeProcessingStatus(string BookId, string State, string Source, string Destination, bool Converted,
    string Message, string Revision, bool CanRecover);

public sealed partial class LibraryStore
{
    private sealed record IntakePlan(ArchiveCatalog Catalog, MetadataUpdate Metadata, string SourceRoot, string Destination,
        int RootIndex, string Folder, ArchiveSecurity Security, string Identity, string Hash, long Size, long Ticks,
        string[] Pages, string Contents, Dictionary<string, byte[]> Replacements, IntakeProcessingPreview Preview);
    private sealed record IntakeJournal(string Id, string BookId, string Source, string Destination,
        string State, bool Committed, ArchiveCatalog Catalog, ArchiveSecurity Security,
        string SourceIdentity, string SourceHash, long SourceSize, long SourceTicks,
        string? OutputIdentity = null, string? OutputHash = null, long OutputSize = 0);
    private static string IntakeStage(IntakeJournal row) => Path.Combine(Path.GetDirectoryName(row.Destination)!, ".panelhaven-intake-" + row.Id + ".stage");

    private static void InitializeIntakeProcessing(SqliteConnection db)
    {
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS intake_operations (
                id TEXT PRIMARY KEY, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                source TEXT NOT NULL, destination TEXT NOT NULL, state TEXT NOT NULL,
                journal TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS intake_pending ON intake_operations(book_id) WHERE state='pending';
            UPDATE intake_operations SET journal=json_remove(journal,'$.BackupReady','$.BackupOwner','$.OriginalName')
                WHERE (instr(journal,'"BackupReady"')>0 OR instr(journal,'"BackupOwner"')>0 OR instr(journal,'"OriginalName"')>0)
                    AND json_valid(journal);
            UPDATE books SET availability='unavailable' WHERE id IN (SELECT book_id FROM intake_operations WHERE state='pending');
            """);
        command.ExecuteNonQuery();
    }

    public IntakeProcessingPreview PrepareIntake(string id, IntakePrepareRequest request, ComicVineMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        scanLock.Wait(cancellationToken);
        try { using var db = Open(); return PlanIntake(db, id, request, metadata, cancellationToken).Preview; }
        finally { scanLock.Release(); }
    }

    public IntakeProcessingPreview PreviewCbzReplacement(string id, CancellationToken cancellationToken = default)
    {
        scanLock.Wait(cancellationToken);
        try { using var db = Open(); return PlanCbzReplacement(db, id, cancellationToken).Preview; }
        finally { scanLock.Release(); }
    }

    private IntakePlan PlanCbzReplacement(SqliteConnection db, string id, CancellationToken cancellationToken)
    {
        RefusePendingFileOperation(db, id);
        var catalog = ReadArchiveCatalog(db, null, id);
        if (Path.GetExtension(catalog.Path).ToLowerInvariant() is not (".cbr" or ".rar" or ".cb7" or ".7z" or ".cbt" or ".tar"))
            throw new ArgumentException("CBZ replacement supports CBR, RAR, CB7, 7Z, CBT and TAR comicbooks only.");
        var root = FileOperationRootFor(catalog.Path);
        var destination = Path.ChangeExtension(catalog.Path, ".cbz");
        if (FileEntryExists(destination) || FileCatalogCollision(db, destination, null))
            throw new FileOperationConflictException("The same-name CBZ destination already exists or is reserved. Nothing was replaced.");
        var security = ArchiveWritePermissions(root, catalog.Path, preserveInode: true);
        _ = ReadIntakeNfsAcl(catalog.Path);
        var identity = FileIdentity(catalog.Path);
        var version = ReadSourceVersion(root, catalog.Path);
        using var source = OpenArchive(root, catalog.Path);
        var hash = ArchiveHash(source, cancellationToken);
        if (catalog.Size != version.Size || catalog.Ticks != version.Ticks || catalog.Hash != hash)
            throw new FileOperationConflictException("Source changed; rescan before converting this comicbook.");
        source.Position = 0;
        using var archive = new ComicArchive(source, catalog.Path, cancellationToken);
        if (archive.Pages.Count != catalog.PageCount) throw new FileOperationConflictException();
        var desired = ParseXml(catalog.Xml, "ComicInfo");
        var update = new MetadataUpdate(id, catalog.Title, desired.Root!, catalog.Xml, catalog.BookXml, new(id, catalog.Title, []));
        var replacements = IntakeMetadataReplacements(archive, catalog, update, cancellationToken);
        var destinationSecurity = IntakeDestinationStamp(root, destination);
        var revision = FileRevision(new { catalog, destination, destinationSecurity, security, identity, hash, version });
        if (ArchiveWritePermissions(root, catalog.Path, preserveInode: true) != security || FileIdentity(catalog.Path) != identity
            || ReadSourceVersion(root, catalog.Path) != version || ArchiveHash(source, cancellationToken) != hash)
            throw new FileOperationConflictException();
        var folder = Path.GetRelativePath(root, Path.GetDirectoryName(destination)!);
        return new(catalog, update, root, destination, Array.IndexOf(roots, root), folder == "." ? "" : folder,
            security, identity, hash, version.Size, version.Ticks,
            archive.Pages.Select(page => page.Name).ToArray(), IntakeContents(archive, cancellationToken), replacements,
            new(revision, FileDisplayPath(catalog.Path), FileDisplayPath(destination), true,
                ["Creates a same-folder CBZ with saved metadata. Replaces the source after verified publication and catalog commit. This cannot be undone."]));
    }

    private IntakePlan PlanIntake(SqliteConnection db, string id, IntakePrepareRequest request, ComicVineMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IssueId <= 0 || request.VolumeId <= 0 || request.RootIndex < 0 || request.RootIndex >= roots.Length
            || !RootLabel(request.RootIndex).StartsWith("Library ", StringComparison.Ordinal))
            throw new ArgumentException("Select a provider issue and a regular destination library.");
        GetIntake(id);
        RefusePendingFileOperation(db, id);
        var catalog = ReadArchiveCatalog(db, null, id);
        var extension = Path.GetExtension(catalog.Path).ToLowerInvariant();
        if (extension is not (".cbz" or ".zip" or ".cbr"))
            throw new ArgumentException("Accept and process supports CBZ, ZIP and CBR comicbooks only.");
        var root = FileOperationRootFor(catalog.Path);
        var security = ArchiveWritePermissions(root, catalog.Path, preserveInode: true);
        _ = ReadIntakeNfsAcl(catalog.Path);
        var identity = FileIdentity(catalog.Path);
        var version = ReadSourceVersion(root, catalog.Path);
        using var source = OpenArchive(root, catalog.Path);
        var hash = ArchiveHash(source, cancellationToken);
        if (catalog.Size != version.Size || catalog.Ticks != version.Ticks || catalog.Hash != hash)
            throw new FileOperationConflictException("Source changed; rescan before accepting this candidate.");
        source.Position = 0;
        using var archive = new ComicArchive(source, catalog.Path, cancellationToken);
        if (archive.Pages.Count != catalog.PageCount) throw new FileOperationConflictException();
        MetadataUpdate update;
        using (var transaction = db.BeginTransaction(deferred: true))
            update = StageMetadata(db, transaction, new([id], metadata.Fields.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)), out _).Single();
        var replacements = IntakeMetadataReplacements(archive, catalog, update, cancellationToken);
        var desired = ParseXml(update.Xml, "ComicInfo");
        string Field(string name) => desired.Root!.Element(desired.Root.Name.Namespace + name)?.Value.Trim() ?? "";
        var series = IntakeComponent(Field("Series"), "Unknown Series");
        var publisher = IntakeComponent(Field("Publisher"), "Unknown Publisher");
        var year = Field("Year");
        var volume = Field("Volume");
        if (volume.Length == 0 || volume == "-1") volume = year.Length == 0 ? "Unknown" : year;
        var number = Field("Number");
        number = number.Length == 0 ? "00" : number.All(char.IsAsciiDigit) ? number.PadLeft(2, '0') : number;
        var date = year.Length == 0 ? "Unknown Date" : int.TryParse(Field("Month"), out var month) && month is >= 1 and <= 12
            ? CultureInfo.GetCultureInfo("en-US").DateTimeFormat.GetMonthName(month) + ", " + year : year;
        var folder = publisher + "/" + series;
        var stem = IntakeComponent(series + " Vol." + volume + " #" + number + " (" + date + ")", "Unknown");
        var outputExtension = extension == ".cbr" ? ".cbz" : extension;
        var destination = FileDestination(new(request.RootIndex, folder, stem + outputExtension));
        ValidateIntakeDestination(roots[request.RootIndex], destination);
        for (var suffix = 2; FileEntryExists(destination) || FileCatalogCollision(db, destination, null); suffix++)
        {
            if (suffix > 10000) throw new FileOperationConflictException("Too many destination collisions; choose another library.");
            destination = FileDestination(new(request.RootIndex, folder, stem + " [" + suffix.ToString(CultureInfo.InvariantCulture) + "]" + outputExtension));
        }
        var destinationSecurity = IntakeDestinationStamp(roots[request.RootIndex], destination);
        var revision = FileRevision(new { catalog, request, update.Xml, update.BookXml, destination, destinationSecurity, security, identity, hash, version });
        if (ArchiveWritePermissions(root, catalog.Path, preserveInode: true) != security || FileIdentity(catalog.Path) != identity
            || ReadSourceVersion(root, catalog.Path) != version || ArchiveHash(source, cancellationToken) != hash)
            throw new FileOperationConflictException();
        return new(catalog, update, root, destination, request.RootIndex, folder, security, identity, hash, version.Size, version.Ticks,
            archive.Pages.Select(page => page.Name).ToArray(), IntakeContents(archive, cancellationToken), replacements,
            new(revision, FileDisplayPath(catalog.Path), FileDisplayPath(destination), extension == ".cbr",
                ["Replaces the source after verified publication and catalog commit. This cannot be undone."]));
    }

    private static Dictionary<string, byte[]> IntakeMetadataReplacements(ComicArchive archive, ArchiveCatalog catalog,
        MetadataUpdate update, CancellationToken cancellationToken)
    {
        var embedded = archive.ReadMetadata(cancellationToken);
        var desired = ParseXml(update.Xml, "ComicInfo");
        var info = embedded.Info is null ? new XDocument(desired) : ReadXml(embedded.Info, "ComicInfo", cancellationToken);
        var desiredBook = update.BookXml is null ? null : ParseXml(update.BookXml, "ComicBook");
        var book = embedded.Book is null ? desiredBook : ReadXml(embedded.Book, "ComicBook", cancellationToken);
        MergeEmbeddedMetadata(info.Root!, book?.Root, desired.Root!, desiredBook?.Root,
            catalog.PageEdits, catalog.PageCount, archive.Pages.Count);
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        { ["ComicInfo.xml"] = Encoding.UTF8.GetBytes(SerializeMetadata(info)) };
        if (book is not null) replacements["ComicBook.xml"] = Encoding.UTF8.GetBytes(SerializeMetadata(book));
        return replacements;
    }

    private static string IntakeContents(ComicArchive archive, CancellationToken cancellationToken)
    {
        var entries = new List<(string Name, string Hash)>();
        foreach (var directory in archive.Directories) entries.Add((directory.TrimEnd('/') + "/", ""));
        archive.Visit(null, (entry, input) =>
        {
            if (entry.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("ComicBook.xml", StringComparison.OrdinalIgnoreCase))
            {
                ComicArchive.Consume(input, entry.Size, null, cancellationToken);
                return;
            }
            using var hash = SHA256.Create();
            using var digest = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            if (CopyArchiveBytes(input, digest, entry.Size, cancellationToken) != entry.Size) throw new InvalidDataException("Truncated source entry.");
            digest.FlushFinalBlock();
            entries.Add((entry.Name, Convert.ToHexString(hash.Hash!)));
        });
        return FileRevision(entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => new { entry.Name, entry.Hash }));
    }

    private static string IntakeComponent(string text, string fallback)
    {
        var value = new string(text.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (value is "" or "." or "..") value = fallback;
        if (Encoding.UTF8.GetByteCount(value) > 220) throw new ArgumentException("Metadata produces an overlong filename or folder; edit the metadata or choose a different match.");
        return value;
    }

    private static void ValidateIntakeDestination(string root, string path)
    {
        if (!IsContained(root, path)) throw new UnauthorizedAccessException();
        var parent = Path.GetDirectoryName(path)!;
        while (!FileEntryExists(parent)) parent = Path.GetDirectoryName(parent)!;
        EnsureSafePath(root, parent);
        if (!Directory.Exists(parent)) throw new UnauthorizedAccessException("Destination must be a directory.");
    }

    private static string IntakeDestinationStamp(string root, string path)
    {
        ValidateIntakeDestination(root, path);
        var parent = Path.GetDirectoryName(path)!;
        var parts = new List<string>();
        while (IsContained(root, parent))
        {
            if (FileEntryExists(parent))
            {
                EnsureSafePath(root, parent);
                if (ArchiveStatx(-100, parent, 0x100, 0x7ff, out var stat) != 0 || (stat.Mode & 0xf000) != 0x4000)
                    throw new UnauthorizedAccessException("Cannot inspect destination folder security.");
                var attributes = ReadArchiveExtendedAttributesHash(parent);
                parts.Add(FileRevision(new { parent, stat.DeviceMajor, stat.DeviceMinor, stat.Inode, stat.Mode, stat.Owner, stat.Group, attributes }));
            }
            if (parent == root) break;
            parent = Path.GetDirectoryName(parent)!;
        }
        return FileRevision(parts);
    }

    public IntakeProcessingStatus ProcessIntake(string id, IntakeProcessRequest request, ComicVineMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            var plan = PlanIntake(db, id, new(request.IssueId, request.VolumeId, request.RootIndex), metadata, cancellationToken);
            return ExecuteIntakePlan(db, id, plan, request.ExpectedRevision, cancellationToken);
        }
        finally { scanLock.Release(); }
    }

    public IntakeProcessingStatus ReplaceWithCbz(string id, FileOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return ExecuteIntakePlan(db, id, PlanCbzReplacement(db, id, cancellationToken), request.ExpectedRevision, cancellationToken);
        }
        finally { scanLock.Release(); }
    }

    private IntakeProcessingStatus ExecuteIntakePlan(SqliteConnection db, string id, IntakePlan plan,
        string expectedRevision, CancellationToken cancellationToken)
    {
        if (plan.Preview.Revision != expectedRevision) throw new FileOperationConflictException();
        cancellationToken.ThrowIfCancellationRequested();
        // Once reserved, finish independently of a disconnected request; only explicit recovery resumes a crash.
        var row = new IntakeJournal(Guid.NewGuid().ToString("N"), id, plan.Catalog.Path, plan.Destination,
            "pending", false, plan.Catalog, plan.Security, plan.Identity, plan.Hash, plan.Size, plan.Ticks);
        using (var transaction = db.BeginTransaction(deferred: false))
        {
            if (ReadArchiveCatalog(db, transaction, id) != plan.Catalog) throw new FileOperationConflictException();
            RefusePendingFileOperation(db, id, transaction);
            if (FileCatalogCollision(db, row.Destination, null, transaction)) throw new FileOperationConflictException();
            SaveIntakeJournal(db, transaction, row);
            using var reserve = Command(db, "UPDATE books SET availability='unavailable' WHERE id=$id", ("$id", id));
            reserve.Transaction = transaction;
            reserve.ExecuteNonQuery();
            transaction.Commit();
        }
        try
        {
            if (Path.GetDirectoryName(row.Source) != Path.GetDirectoryName(row.Destination))
                CreateFileOperationFolder(new(plan.RootIndex, plan.Folder));
            ValidateIntakeDestination(roots[plan.RootIndex], row.Destination);
            var stage = IntakeStage(row);
            using (var output = NewPrivateFile(stage))
            {
                WriteIntakeStage(plan, output);
                output.Flush(true);
            }
            ApplyIntakePermissions(row, stage);
            using (var output = OpenArchive(roots[plan.RootIndex], stage, row.Destination))
            {
                using var archive = new ComicArchive(output, row.Destination);
                var written = archive.ReadMetadata(default);
                if (written.Info is null || !XNode.DeepEquals(ReadXml(written.Info, "ComicInfo", default), ReadXml(plan.Replacements["ComicInfo.xml"], "ComicInfo", default))
                    || !archive.Pages.Select(page => page.Name).SequenceEqual(plan.Pages, StringComparer.Ordinal)
                    || IntakeContents(archive, default) != plan.Contents)
                    throw new InvalidDataException("Processed comicbook metadata or physical pages failed verification.");
                if (plan.Replacements.TryGetValue("ComicBook.xml", out var expectedBook)
                    && (written.Book is null || !XNode.DeepEquals(ReadXml(written.Book, "ComicBook", default), ReadXml(expectedBook, "ComicBook", default))))
                    throw new InvalidDataException("Processed legacy metadata failed verification.");
                row = row with { OutputHash = ArchiveHash(output, default), OutputSize = output.Length, OutputIdentity = FileIdentity(stage) };
            }
            SaveIntakeJournal(db, null, row);
            CheckIntakeSource(row);
            using (var transaction = db.BeginTransaction(deferred: false))
            {
                if (ReadArchiveCatalog(db, transaction, id) != plan.Catalog || FileCatalogCollision(db, row.Destination, row.Id, transaction))
                    throw new FileOperationConflictException();
                MoveFileNoReplace(IntakeOutputJournal(row), stage, row.Destination, default);
                CheckIntakeOutput(row, row.Destination);
                CommitIntakeCatalog(db, transaction, row, plan.Metadata);
                row = row with { Committed = true };
                SaveIntakeJournal(db, transaction, row);
                transaction.Commit();
            }
            FinishIntake(db, row);
        }
        catch (Exception exception) when (IsArchiveError(exception) || exception is SqliteException or FileOperationConflictException or MetadataRevisionException)
        {
            // Keep the reservation and exact ownership evidence. Never guess whether commit/publication succeeded.
            return IntakeStatus(db, id);
        }
        return IntakeStatus(db, id);
    }

    private static void WriteIntakeStage(IntakePlan plan, FileStream output)
    {
        if (!plan.Preview.Converted)
        {
            WriteArchiveStage(plan.SourceRoot, plan.Catalog.Path, plan.Replacements, output, default);
            return;
        }
        using var input = OpenArchive(plan.SourceRoot, plan.Catalog.Path);
        using var source = new ComicArchive(input, plan.Catalog.Path);
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var pending = new Dictionary<string, byte[]>(plan.Replacements, StringComparer.OrdinalIgnoreCase);
            foreach (var directory in source.Directories) destination.CreateEntry(directory.EndsWith('/') ? directory : directory + "/");
            source.Visit(null, (entry, stream) =>
            {
                var target = destination.CreateEntry(entry.Name, CompressionLevel.Optimal);
                using var to = target.Open();
                if (pending.Remove(entry.Name, out var replacement))
                {
                    ComicArchive.Consume(stream, entry.Size, null, default);
                    to.Write(replacement);
                }
                else if (CopyArchiveBytes(stream, to, entry.Size, default) != entry.Size) throw new InvalidDataException("Truncated source entry.");
                if (output.Length > MaxArchiveBytes) throw new InvalidDataException("Processed comicbook exceeds the safety limit.");
            });
            foreach (var (name, bytes) in pending)
            {
                using var to = destination.CreateEntry(name, CompressionLevel.Optimal).Open();
                to.Write(bytes);
            }
        }
        if (output.Length > MaxArchiveBytes) throw new InvalidDataException("Processed comicbook exceeds the safety limit.");
    }

    private static void SaveIntakeJournal(SqliteConnection db, SqliteTransaction? transaction, IntakeJournal row)
    {
        using var command = Command(db, """
            INSERT INTO intake_operations(id,book_id,source,destination,state,journal) VALUES($id,$book,$source,$destination,$state,$journal)
            ON CONFLICT(id) DO UPDATE SET state=excluded.state,journal=excluded.journal
            """, ("$id", row.Id), ("$book", row.BookId), ("$source", row.Source), ("$destination", row.Destination),
            ("$state", row.State), ("$journal", JsonSerializer.Serialize(row)));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static IntakeJournal? ReadIntakeJournal(SqliteConnection db, string id)
    {
        using var command = Command(db, """
            SELECT journal,source,destination,state FROM intake_operations
            WHERE book_id=$id ORDER BY state='pending' DESC,rowid DESC LIMIT 1
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = JsonSerializer.Deserialize<IntakeJournal>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid processing journal.");
        return row with { BookId = id, Source = reader.GetString(1), Destination = reader.GetString(2), State = reader.GetString(3) };
    }

    private void CheckIntakeSource(IntakeJournal row)
    {
        var root = FileOperationRootFor(row.Source);
        if (FileIdentity(row.Source) != row.SourceIdentity || ArchiveWritePermissions(root, row.Source, preserveInode: true) != row.Security
            || ReadSourceVersion(root, row.Source) != (row.SourceSize, row.SourceTicks)) throw new FileOperationConflictException("Original source changed; no files were removed.");
        using var source = OpenArchive(root, row.Source);
        if (ArchiveHash(source, default) != row.SourceHash || FileIdentity(row.Source) != row.SourceIdentity)
            throw new FileOperationConflictException("Original source changed; no files were removed.");
    }

    [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)]
    private static extern int IntakeSetXattr(int descriptor, byte[] name, byte[] value, nuint size, int flags);
    private static readonly byte[] IntakeNfsAclName = "system.nfs4_acl\0"u8.ToArray();

    private static byte[]? ReadIntakeNfsAcl(string path)
    {
        var descriptor = ArchiveOpen(path, 0x20000 | 0x80000 | 0x800);
        if (descriptor < 0) throw new UnauthorizedAccessException("Cannot inspect processing file security.");
        try
        {
            var size = ArchiveListFileXattr(descriptor, null, 0);
            if (size == 0) return null;
            // Only the observed, round-trippable NFS4 ACL is supported, never silently drop arbitrary xattrs.
            var expectedName = IntakeNfsAclName;
            if (size != expectedName.Length) throw new UnauthorizedAccessException("Processing refuses unsupported extended attributes.");
            var names = new byte[(int)size];
            if (ArchiveListFileXattr(descriptor, names, (nuint)names.Length) != size || !names.AsSpan().SequenceEqual(expectedName))
                throw new UnauthorizedAccessException("Processing refuses unsupported or changing extended attributes.");
            var length = ArchiveGetFileXattr(descriptor, expectedName, [], 0);
            if (length <= 0 || length > 65536) throw new UnauthorizedAccessException("Cannot read the complete NFS4 ACL.");
            var value = new byte[(int)length];
            if (ArchiveGetFileXattr(descriptor, expectedName, value, (nuint)value.Length) != length)
                throw new UnauthorizedAccessException("NFS4 ACL changed during inspection.");
            return value;
        }
        finally { ArchiveClose(descriptor); }
    }

    private void ApplyIntakePermissions(IntakeJournal row, string stage)
    {
        CheckIntakeSource(row);
        ApplyArchivePermissions(stage, row.Security, ReadIntakeNfsAcl(row.Source));
        CheckIntakeSource(row);
    }

    private static FileJournal IntakeOutputJournal(IntakeJournal row) => new(row.Id, row.BookId, "", IntakeStage(row), row.Destination,
        row.OutputHash!, "pending", row.Committed, row.OutputIdentity!, row.OutputIdentity, row.Security, row.OutputSize, 0);

    private void CheckIntakeOutput(IntakeJournal row, string path) => CheckOwnedFile(IntakeOutputJournal(row), path, row.OutputIdentity, default);

    private void CommitIntakeCatalog(SqliteConnection db, SqliteTransaction transaction, IntakeJournal row, MetadataUpdate metadata)
    {
        var order = ReadPageOrderCatalog(db, transaction, row.BookId);
        var version = ReadSourceVersion(FileOperationRootFor(row.Destination), row.Destination);
        var (location, folder) = SourceLocation(row.Destination);
        string Field(string name) => metadata.Root.Element(metadata.Root.Name.Namespace + name)?.Value ?? "";
        using var command = Command(db, """
            UPDATE books SET path=$path,file_size=$size,modified_ticks=$ticks,content_hash=$hash,
                title=$title,series=$series,number=$number,publisher=$publisher,summary=$summary,info_xml=$xml,book_xml=$book,
                writer=$writer,penciller=$penciller,genre=$genre,year=$year,edited=1,location=$location,folder=$folder,
                availability='unavailable' WHERE id=$id;
            """, ("$id", row.BookId), ("$path", PathKey(row.Destination)), ("$size", version.Size), ("$ticks", version.Ticks),
            ("$hash", row.OutputHash), ("$title", metadata.Title), ("$series", Field("Series")), ("$number", Field("Number")),
            ("$publisher", Field("Publisher")), ("$summary", Field("Summary")), ("$xml", metadata.Xml), ("$book", metadata.BookXml),
            ("$writer", Field("Writer")), ("$penciller", Field("Penciller")), ("$genre", Field("Genre")), ("$year", MetadataYear(Field("Year"))),
            ("$location", location), ("$folder", folder));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new FileOperationConflictException();
        // The physical page names and bytes are unchanged; carry the permutation across the source-hash trigger.
        if (order.SavedHash == row.SourceHash && order.Json is not null)
        {
            using var restoreOrder = Command(db, "UPDATE page_orders SET source_hash=$hash,pages_json=$pages,revision=$revision WHERE book_id=$id",
                ("$hash", row.OutputHash), ("$pages", order.Json), ("$revision", Guid.NewGuid().ToString("N")), ("$id", row.BookId));
            restoreOrder.Transaction = transaction;
            restoreOrder.ExecuteNonQuery();
        }
    }

    private void ValidateIntakeRecovery(SqliteConnection db, IntakeJournal row)
    {
        var catalog = ReadArchiveCatalog(db, null, row.BookId);
        if (row.Committed)
        {
            if (catalog.Path != row.Destination || catalog.Hash != row.OutputHash) throw new FileOperationConflictException();
            CheckIntakeOutput(row, row.Destination);
            if (FileEntryExists(row.Source)) CheckIntakeSource(row);
        }
        else
        {
            if (catalog != row.Catalog) throw new FileOperationConflictException("Catalog changed; manual recovery is required.");
            CheckIntakeSource(row);
            if (FileEntryExists(row.Destination)) CheckIntakeOutput(row, row.Destination);
        }
        if (FileEntryExists(IntakeStage(row)) && row.OutputIdentity is not null) CheckIntakeOutput(row, IntakeStage(row));
    }

    private void FinishIntake(SqliteConnection db, IntakeJournal row)
    {
        ValidateIntakeRecovery(db, row);
        if (row.Committed)
        {
            if (FileEntryExists(row.Source))
            {
                CheckIntakeSource(row);
                File.Delete(row.Source);
                FlushArchivePath(Path.GetDirectoryName(row.Source)!, true);
            }
        }
        else if (FileEntryExists(row.Destination))
        {
            CheckIntakeOutput(row, row.Destination);
            File.Delete(row.Destination);
            FlushArchivePath(Path.GetDirectoryName(row.Destination)!, true);
        }
        // Unverified partial staging remains untouched, never imported; the original is still present.
        if (row.OutputIdentity is not null && FileEntryExists(IntakeStage(row)))
        {
            CheckIntakeOutput(row, IntakeStage(row));
            File.Delete(IntakeStage(row));
            FlushArchivePath(Path.GetDirectoryName(row.Destination)!, true);
        }
        using var transaction = db.BeginTransaction(deferred: false);
        row = row with { State = row.Committed ? "completed" : "failed" };
        SaveIntakeJournal(db, transaction, row);
        using var available = Command(db, "UPDATE books SET availability='available' WHERE id=$id", ("$id", row.BookId));
        available.Transaction = transaction;
        available.ExecuteNonQuery();
        transaction.Commit();
    }

    public IntakeProcessingStatus GetIntakeProcessing(string id)
    {
        scanLock.Wait();
        try { using var db = Open(); return IntakeStatus(db, id); }
        finally { scanLock.Release(); }
    }

    private IntakeProcessingStatus IntakeStatus(SqliteConnection db, string id)
    {
        _ = ReadArchiveCatalog(db, null, id);
        var row = ReadIntakeJournal(db, id);
        if (row is null) return new(id, "none", "", "", false, "No processing operation has started.", "", false);
        var canRecover = false;
        var converted = Path.GetExtension(row.Source).ToLowerInvariant() is not (".cbz" or ".zip");
        var message = row.State == "completed" ? (converted ? "Metadata embedded, comicbook converted to CBZ and source removed." : "Metadata embedded, comicbook organized and source removed.")
            : row.State == "failed" ? "Processing rolled back safely; the original source remains unchanged. You may preview again."
            : "Processing is interrupted. Files are reserved; inspect status before recovery.";
        if (row.State == "pending")
        {
            try
            {
                ValidateIntakeRecovery(db, row);
                canRecover = true;
                message = row.Committed ? "Catalog commit succeeded. Recover to finish verified source cleanup."
                    : "Catalog was not committed. Recover to roll back verified output and keep the original source.";
            }
            catch (Exception exception) when (IsArchiveError(exception) || exception is FileOperationConflictException)
            { message = "File ownership, security or catalog changed. Automatic recovery is unsafe; preserve all files for administrator recovery."; }
        }
        if (!row.Committed && row.OutputIdentity is null && FileEntryExists(IntakeStage(row)))
            message += " Unverified staging is retained untouched at " + FileDisplayPath(IntakeStage(row)) + ".";
        message += " Original-file undo is unavailable.";
        return new(id, row.State, FileDisplayPath(row.Source), FileDisplayPath(row.Destination), converted,
            message, FileRevision(new { row, canRecover }), canRecover);
    }

    public IntakeProcessingStatus RecoverIntakeProcessing(string id, FileOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            var status = IntakeStatus(db, id);
            if (!status.CanRecover || status.Revision != request.ExpectedRevision) throw new FileOperationConflictException();
            cancellationToken.ThrowIfCancellationRequested();
            FinishIntake(db, ReadIntakeJournal(db, id)!);
            return IntakeStatus(db, id);
        }
        finally { scanLock.Release(); }
    }
}
