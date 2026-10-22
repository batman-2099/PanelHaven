using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record Book(string Id, string Title, string Series, string Number, string Publisher,
    string Summary, int PageCount, int CurrentPage, bool Completed, bool Started,
    Dictionary<string, object?>? Metadata = null, string Availability = "available", string Location = "",
    int PersonalRating = 0);
public sealed record MissingIssue(string BeforeBookId, int First, int Last);
public sealed record BookPage(List<Book> Items, int Total, int Page, int PageSize)
{
    public List<MissingIssue> MissingIssues { get; init; } = [];
    public IReadOnlyList<BookSection> Sections { get; init; } = Array.Empty<BookSection>();
}
public sealed record ReadingProgress([property: JsonRequired] int Page, [property: JsonRequired] bool Completed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReadingStatusEdit([property: JsonRequired] string[] BookIds, [property: JsonRequired] bool Completed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RatingEdit([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Rating);
public sealed record ScanResult(int Scanned, int Added, int Updated, int Skipped, int ErrorCount, List<string> Errors);
public sealed record ScanLogEntry(DateTimeOffset Timestamp, string Outcome, string? Root, string? Path, string Stage,
    string? ExceptionType = null, string? Message = null, string? Detail = null);
public sealed record PageData(byte[] Bytes, string ContentType);

public sealed partial class LibraryStore
{
    private const int MaxEntries = 10_000, MaxPageBytes = 32 * 1024 * 1024, MaxXmlBytes = 2 * 1024 * 1024;
    private const long MaxArchiveBytes = 1024L * 1024 * 1024, MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private readonly string connectionString;
    private readonly string[] roots;
    private readonly string? archivePath;
    private readonly string? unsortedPath;
    private readonly string stateDirectory;
    private readonly SemaphoreSlim scanLock = new(1, 1);
    internal IReadOnlyList<string> WatchRoots => roots;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public LibraryStore(string databasePath, string[] libraryRoots, string? archivePath = null, string? unsortedPath = null)
    {
        stateDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(stateDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 30
        }.ToString();
        var configuredRoots = libraryRoots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        this.archivePath = string.IsNullOrWhiteSpace(archivePath) ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(archivePath));
        this.unsortedPath = string.IsNullOrWhiteSpace(unsortedPath) ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(unsortedPath));
        var specialRoots = new[] { this.archivePath, this.unsortedPath }.OfType<string>().ToArray();
        foreach (var special in specialRoots)
            if (configuredRoots.Any(root => IsContained(root, special) || IsContained(special, root)))
                throw new ArgumentException("Archived and Unsorted paths must not overlap regular library paths.");
        if (this.archivePath is not null && this.unsortedPath is not null
            && (IsContained(this.archivePath, this.unsortedPath) || IsContained(this.unsortedPath, this.archivePath)))
            throw new ArgumentException("Archived and Unsorted paths must not overlap.");
        roots = configuredRoots.Where(root => !configuredRoots.Any(other =>
            !root.Equals(other, PathComparison) && IsContained(other, root))).Concat(specialRoots).ToArray();
        using var db = Open();
        using var command = Command(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS books (
                id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE, title TEXT NOT NULL,
                series TEXT NOT NULL, number TEXT NOT NULL, publisher TEXT NOT NULL, summary TEXT NOT NULL,
                page_count INTEGER NOT NULL CHECK(page_count > 0), info_xml TEXT NOT NULL,
                book_xml TEXT, edited INTEGER NOT NULL DEFAULT 0,
                file_size INTEGER NOT NULL, modified_ticks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS progress (
                user_id TEXT NOT NULL, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                page INTEGER NOT NULL CHECK(page >= 0), completed INTEGER NOT NULL CHECK(completed IN (0,1)),
                PRIMARY KEY(user_id, book_id)
            );
            CREATE TABLE IF NOT EXISTS ratings (
                user_id TEXT NOT NULL, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                rating INTEGER NOT NULL CHECK(typeof(rating)='integer' AND rating BETWEEN 1 AND 5),
                PRIMARY KEY(user_id,book_id)
            );
            CREATE INDEX IF NOT EXISTS books_series ON books(series COLLATE NOCASE,id);
            CREATE INDEX IF NOT EXISTS books_title ON books(title COLLATE NOCASE,id);
            CREATE INDEX IF NOT EXISTS books_number ON books(number COLLATE NATURAL_NUMBER,id);
            CREATE INDEX IF NOT EXISTS books_publisher ON books(publisher,id);
            CREATE INDEX IF NOT EXISTS books_page_count ON books(page_count,id);
            """);
        command.ExecuteNonQuery();
        InitializeLists(db);
        InitializeReader(db);
        InitializeMaintenance(db);
        InitializePageOrder(db);
        InitializeMetadata(db);
        InitializeFileOperations(db);
    }

    private string RootLabel(int index) => roots[index].Equals(archivePath, PathComparison) ? "Archived"
        : roots[index].Equals(unsortedPath, PathComparison) ? "Unsorted" : $"Library {index + 1}";

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        db.CreateCollation("NATURAL_NUMBER", static (a, b) => NaturalCompare(a ?? "", b ?? ""));
        db.CreateFunction<string, string, object?>("metadata_scalar", SmartMetadataScalar, isDeterministic: true);
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }


    public Book GetBook(string user, string id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT " + BookColumns + ",b.info_xml,b.book_xml" + BookFrom
            + "WHERE b.id=$id", ("$user", user), ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return ReadBook(reader, true);
    }

    private static Book ReadBook(SqliteDataReader reader, bool includeMetadata)
    {
        Dictionary<string, object?>? metadata = null;
        if (includeMetadata)
        {
            var xml = reader.GetString(13);
            metadata = new Dictionary<string, object?>();
            foreach (var group in ParseXml(xml, "ComicInfo").Root!.Elements().GroupBy(e => e.Name.ToString()))
                metadata[group.Key] = group.Count() == 1 ? group.First().Value : group.Select(e => e.Value).ToArray();
            // Full XML keeps attributes, nested Pages, namespaces and unknown fields losslessly in the data model.
            metadata["comicInfoXml"] = xml;
            metadata["comicBookXml"] = reader.IsDBNull(14) ? null : reader.GetString(14);
        }
        return new Book(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetBoolean(8),
            reader.GetBoolean(9), metadata, reader.GetString(10), reader.GetString(11), reader.GetInt32(12));
    }

    public void SaveProgress(string user, string id, ReadingProgress progress)
    {
        if (progress.Page < 0) throw new ArgumentException("Page is outside the book.");
        using var db = Open();
        using var command = Command(db, """
            INSERT INTO progress(user_id,book_id,page,completed,updated_at)
            SELECT $user,id,$page,$completed,unixepoch() FROM books WHERE id=$id AND $page<page_count
            ON CONFLICT(user_id,book_id) DO UPDATE SET page=excluded.page,completed=excluded.completed,updated_at=excluded.updated_at
            """, ("$user", user), ("$id", id), ("$page", progress.Page), ("$completed", progress.Completed));
        if (command.ExecuteNonQuery() == 0)
        {
            GetBook(user, id);
            throw new ArgumentException("Page is outside the book.");
        }
    }

    public void SetReadingStatus(string user, ReadingStatusEdit edit)
    {
        if (edit is null) throw new ArgumentException("Reading status is required.");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        ValidateListBooks(db, transaction, edit.BookIds);
        using var command = Command(db, edit.Completed ? """
            INSERT INTO progress(user_id,book_id,page,completed,updated_at)
            SELECT $user,id,page_count-1,1,unixepoch() FROM books WHERE id=$book
            ON CONFLICT(user_id,book_id) DO UPDATE SET page=excluded.page,completed=excluded.completed,updated_at=excluded.updated_at
            """ : "DELETE FROM progress WHERE user_id=$user AND book_id=$book", ("$user", user), ("$book", ""));
        command.Transaction = transaction;
        foreach (var book in edit.BookIds)
        {
            command.Parameters["$book"].Value = book;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SaveRating(string user, string id, RatingEdit edit)
    {
        if (edit is null || edit.Rating is < 0 or > 5) throw new ArgumentException("Rating must be an integer from 0 to 5.");
        using var db = Open();
        if (edit.Rating == 0)
        {
            using var clear = Command(db, "SELECT 1 FROM books WHERE id=$id", ("$id", id), ("$user", user));
            if (clear.ExecuteScalar() is null) throw new KeyNotFoundException();
            clear.CommandText = "DELETE FROM ratings WHERE user_id=$user AND book_id=$id";
            clear.ExecuteNonQuery();
            return;
        }
        using var command = Command(db, """
            INSERT INTO ratings(user_id,book_id,rating)
            SELECT $user,id,$rating FROM books WHERE id=$id
            ON CONFLICT(user_id,book_id) DO UPDATE SET rating=excluded.rating
            """, ("$user", user), ("$id", id), ("$rating", edit.Rating));
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
    }


    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken, Action<ScanResult>? onProgress = null,
        Action<ScanLogEntry>? onLog = null)
    {
        var scanned = 0;
        var added = 0;
        var updated = 0;
        var skipped = 0;
        var errorCount = 0;
        var errors = new List<string>();
        ScanResult Snapshot() => new(scanned, added, updated, skipped, errorCount, [.. errors]);
        void Publish() => onProgress?.Invoke(Snapshot());
        void Log(string outcome, string? root, string? path, string stage, string? message = null, Exception? exception = null)
            => onLog?.Invoke(new(DateTimeOffset.UtcNow, outcome, root, path, stage,
                exception?.GetType().FullName, exception?.Message ?? message, exception?.ToString()));
        void Error(string message)
        {
            errorCount++;
            if (errors.Count < 100) errors.Add(message);
        }
        var acquired = false;
        try
        {
            await scanLock.WaitAsync(cancellationToken);
            acquired = true;
            Publish();
            if (roots.Length == 0)
            {
                const string message = "No library roots are configured. Ask an administrator to configure Libraries.";
                Error(message);
                Log("error", null, null, "configuration", message);
            }
            using var db = Open();
            var reservedPaths = ReservedFileOperationPaths(db);
            var remaining = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            using (var command = Command(db, "SELECT path FROM books"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) remaining.Add(reader.GetString(0));
            foreach (var root in roots)
            {
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out var directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        EnsureSafePath(root, directory);
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var isDirectory = Directory.Exists(path);
                            var isArchive = !isDirectory && IsArchive(path);
                            if (isArchive)
                            {
                                scanned++;
                                remaining.Remove(path);
                                if (reservedPaths.Contains(path))
                                {
                                    SetAvailability(db, path, "unavailable");
                                    skipped++;
                                    Log("reserved", root, path, "reservation", "Skipped: source reserved by a file operation.");
                                    Publish();
                                    continue;
                                }
                            }
                            var stage = "path-validation";
                            try
                            {
                                EnsureSafePath(root, path);
                                if (isDirectory) pending.Push(path);
                                else if (isArchive)
                                {
                                    var outcome = Import(db, root, path, cancellationToken, ref stage);
                                    Log(outcome switch { ImportOutcome.Added => "added", ImportOutcome.Updated => "updated", _ => "skipped" }, root, path, stage);
                                    switch (outcome)
                                    {
                                        case ImportOutcome.Added: added++; break;
                                        case ImportOutcome.Updated: updated++; break;
                                        case ImportOutcome.Skipped: skipped++; break;
                                    }
                                }
                            }
                            catch (SqliteException ex)
                            {
                                Log("error", root, path, stage, exception: ex);
                                throw;
                            }
                            catch (Exception ex) when (IsArchiveError(ex))
                            {
                                if (isArchive) MarkImportFailure(db, path);
                                Error($"{SafeRelative(root, path)}: {(ex is ArchiveFeatureException ? ex.Message : "comicbook unavailable, invalid, or exceeds safety limits.")}");
                                Log("error", root, path, stage, exception: ex);
                            }
                            if (isArchive) Publish();
                        }
                    }
                    catch (Exception ex) when (IsArchiveError(ex))
                    {
                        Error($"{SafeRelative(root, directory)}: directory unavailable or unsafe.");
                        Log("error", root, directory, "directory-enumeration", exception: ex);
                        Publish();
                    }
                }
            }
            // Reconcile catalog paths directly: a failed directory traversal is never evidence of deletion.
            foreach (var path in remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = roots.FirstOrDefault(r => IsContained(r, path));
                if (reservedPaths.Contains(path))
                {
                    SetAvailability(db, path, "unavailable");
                    Log("reserved", root, path, "reconciliation", "Source reserved by a file operation; catalog marked unavailable.");
                    continue;
                }
                var availability = SourceAvailability(path);
                if (availability != "available")
                {
                    SetAvailability(db, path, availability);
                    Log(availability, root, path, "reconciliation",
                        availability == "missing" ? "Source no longer exists; catalog entry retained."
                            : "Source is outside configured roots, inaccessible, unsafe, or exceeds file-size limits; catalog entry retained.");
                    continue;
                }
                scanned++;
                var stage = "path-validation";
                try
                {
                    var outcome = Import(db, root!, path, cancellationToken, ref stage);
                    Log(outcome switch { ImportOutcome.Added => "added", ImportOutcome.Updated => "updated", _ => "skipped" }, root, path, stage);
                    switch (outcome)
                    {
                        case ImportOutcome.Added: added++; break;
                        case ImportOutcome.Updated: updated++; break;
                        case ImportOutcome.Skipped: skipped++; break;
                    }
                }
                catch (SqliteException ex)
                {
                    Log("error", root, path, stage, exception: ex);
                    throw;
                }
                catch (Exception ex) when (IsArchiveError(ex))
                {
                    MarkImportFailure(db, path);
                    Error($"{SafeRelative(root!, path)}: comicbook unavailable, invalid, or exceeds safety limits.");
                    Log("error", root, path, stage, exception: ex);
                }
                Publish();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Snapshot();
        }
        finally
        {
            if (acquired) scanLock.Release();
            Publish();
        }
    }

    private enum ImportOutcome { Added, Updated, Skipped }

    private ImportOutcome Import(SqliteConnection db, string root, string path, CancellationToken cancellationToken, ref string stage)
    {
        stage = "path-validation";
        var version = ReadSourceVersion(root, path);
        var key = PathKey(path);
        string? existing = null;
        var unchanged = false;
        stage = "catalog-lookup";
        using (var read = Command(db, "SELECT id,file_size,modified_ticks,availability,content_hash FROM books WHERE path=$path", ("$path", key)))
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                existing = reader.GetString(0);
                unchanged = reader.GetInt64(1) == version.Size && reader.GetInt64(2) == version.Ticks
                    && reader.GetString(3) == "available" && !reader.IsDBNull(4);
            }
        }
        if (unchanged)
        {
            SetAvailability(db, path, "available");
            return ImportOutcome.Skipped;
        }
        // A changed or unvalidated source must stop participating in duplicate groups, even if this scan is cancelled.
        if (existing is not null) SetAvailability(db, path, "unavailable");
        cancellationToken.ThrowIfCancellationRequested();
        stage = "archive-headers";
        using var file = OpenArchive(root, path);
        if (file.Length != version.Size || ReadSourceVersion(root, path) != version) throw new IOException("Comicbook changed during scan.");
        stage = "archive-entries";
        using var archive = new ComicArchive(file, path, cancellationToken);
        stage = "metadata-read";
        var metadata = archive.ReadMetadata(cancellationToken);
        stage = "metadata-xml";
        var legacy = metadata.Book is null ? null : ReadXml(metadata.Book, "ComicBook", cancellationToken);
        var info = metadata.Info is null ? new XDocument(new XElement("ComicInfo")) : ReadXml(metadata.Info, "ComicInfo", cancellationToken);
        if (metadata.Info is null && legacy is not null)
            foreach (var element in legacy.Root!.Elements()) info.Root!.Add(new XElement(element));
        var infoXml = info.ToString(SaveOptions.DisableFormatting);
        if (Encoding.UTF8.GetByteCount(infoXml) > MaxXmlBytes)
            throw new InvalidDataException($"Normalized ComicInfo XML exceeds the {MaxXmlBytes}-byte metadata limit.");
        string Field(string name) => info.Root!.Element(info.Root.Name.Namespace + name)?.Value ?? "";
        var title = Field("Title");
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
        stage = "hashing";
        file.Position = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[16 * 1024];
        int readBytes;
        while ((readBytes = file.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasher.AppendData(buffer[..readBytes]);
        }
        hasher.GetHashAndReset(buffer[..32]);
        var hash = Convert.ToHexString(buffer[..32]);
        var (location, folder) = SourceLocation(path);
        cancellationToken.ThrowIfCancellationRequested();
        stage = "catalog-write";
        using var transaction = db.BeginTransaction();
        using var command = Command(db, """
            INSERT INTO books(id,path,title,series,number,publisher,summary,page_count,info_xml,book_xml,file_size,modified_ticks,
                availability,location,folder,content_hash,writer,penciller,genre,year,added_at)
            VALUES($id,$path,$title,$series,$number,$publisher,$summary,$pages,$xml,$legacy,$size,$ticks,
                'available',$location,$folder,$hash,$writer,$penciller,$genre,$year,unixepoch())
            ON CONFLICT(path) DO UPDATE SET
                title=CASE WHEN books.edited=0 THEN excluded.title ELSE books.title END,
                series=CASE WHEN books.edited=0 THEN excluded.series ELSE books.series END,
                number=CASE WHEN books.edited=0 THEN excluded.number ELSE books.number END,
                publisher=CASE WHEN books.edited=0 THEN excluded.publisher ELSE books.publisher END,
                summary=CASE WHEN books.edited=0 THEN excluded.summary ELSE books.summary END,
                writer=CASE WHEN books.edited=0 THEN excluded.writer ELSE books.writer END,
                penciller=CASE WHEN books.edited=0 THEN excluded.penciller ELSE books.penciller END,
                genre=CASE WHEN books.edited=0 THEN excluded.genre ELSE books.genre END,
                year=CASE WHEN books.edited=0 THEN excluded.year ELSE books.year END,
                info_xml=CASE WHEN books.edited=0 THEN excluded.info_xml ELSE books.info_xml END,
                book_xml=CASE WHEN books.edited=0 THEN excluded.book_xml ELSE books.book_xml END,page_count=excluded.page_count,
                file_size=excluded.file_size,modified_ticks=excluded.modified_ticks,
                availability='available',location=excluded.location,folder=excluded.folder,content_hash=excluded.content_hash
            """, ("$id", existing ?? Guid.NewGuid().ToString("N")), ("$path", key), ("$title", title),
            ("$series", Field("Series")), ("$number", Field("Number")), ("$publisher", Field("Publisher")),
            ("$summary", Field("Summary")), ("$pages", archive.Pages.Count), ("$xml", infoXml),
            ("$legacy", legacy?.ToString(SaveOptions.DisableFormatting)), ("$size", version.Size), ("$ticks", version.Ticks),
            ("$location", location), ("$folder", folder), ("$hash", hash),
            ("$writer", Field("Writer")), ("$penciller", Field("Penciller")), ("$genre", Field("Genre")),
            ("$year", MetadataYear(Field("Year"))));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Length != version.Size || ReadSourceVersion(root, path) != version) throw new IOException("Comicbook changed during scan.");
        transaction.Commit();
        return existing is null ? ImportOutcome.Added : ImportOutcome.Updated;
    }

    private (string Root, string Path, long Size, long Ticks) GetSource(string id, int? page = null)
    {
        string path;
        long size, ticks;
        bool available;
        using (var db = Open())
        using (var command = Command(db, "SELECT path,page_count,file_size,modified_ticks,availability FROM books WHERE id=$id", ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException();
            available = reader.GetString(4) == "available";
            if (page >= reader.GetInt32(1)) throw new ArgumentException("Page is outside the book.");
            path = reader.GetString(0);
            size = reader.GetInt64(2);
            ticks = reader.GetInt64(3);
        }
        var root = roots.FirstOrDefault(r => IsContained(r, path)) ?? throw new UnauthorizedAccessException();
        if (ReadSourceVersion(root, path) != (size, ticks))
            throw new InvalidOperationException("Comicbook changed; an administrator must rescan the library.");
        if (!available) throw new InvalidOperationException("Comicbook is unavailable; rescan or recover its pending operation before reading.");
        return (root, path, size, ticks);
    }

    public string GetCoverVersion(string id)
    {
        var source = GetSource(id);
        return $"{source.Size:x}-{source.Ticks:x}";
    }

    public PageData GetPage(string id, int page) => GetPage(id, page, default);

    public PageData GetPage(string id, int page, CancellationToken cancellationToken)
    {
        if (page < 0) throw new ArgumentException("Page is outside the book.");
        cancellationToken.ThrowIfCancellationRequested();
        var source = GetSource(id, page);
        using var file = OpenArchive(source.Root, source.Path);
        if (file.Length != source.Size || ReadSourceVersion(source.Root, source.Path) != (source.Size, source.Ticks))
            throw new InvalidOperationException("Comicbook changed; an administrator must rescan the library.");
        using var archive = new ComicArchive(file, source.Path, cancellationToken);
        if (page >= archive.Pages.Count) throw new InvalidDataException();
        var entry = archive.Pages[page];
        var bytes = archive.ReadPage(entry, cancellationToken);
        var mime = ImageMime(entry.Name)!;
        if (!HasImageSignature(bytes, mime)) throw new InvalidDataException();
        if (file.Length != source.Size || ReadSourceVersion(source.Root, source.Path) != (source.Size, source.Ticks))
            throw new InvalidOperationException("Comicbook changed; an administrator must rescan the library.");
        return new PageData(bytes, mime);
    }

    private static (long Size, long Ticks) ReadSourceVersion(string root, string path)
    {
        EnsureSafePath(root, path);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path)
            & (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0)
            throw new UnauthorizedAccessException("Source has no Unix read permission bits.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Comicbook source no longer exists.");
        if (info.Length < 22 || info.Length > MaxArchiveBytes)
            throw new InvalidDataException($"Comicbook size {info.Length} bytes is outside the allowed range 22–{MaxArchiveBytes} bytes.");
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static bool IsArchive(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".cbz" or ".zip" or ".cbr" or ".rar" or ".cb7" or ".7z" or ".cbt" or ".tar";

    private static bool IsContained(string root, string path) => path.Equals(root, PathComparison)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    internal static void EnsureSafePath(string root, string path)
    {
        if (!IsContained(root, Path.GetFullPath(path))) throw new UnauthorizedAccessException("Source path is outside its configured library root.");
        // Reject every symlink/reparse component, including root ancestors; no extraction is ever performed.
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Source path contains a symbolic link or reparse point.");
    }

    private static FileStream OpenArchive(string root, string path, string? formatPath = null)
    {
        EnsureSafePath(root, path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            EnsureSafePath(root, path);
            if (file.Length < 22 || file.Length > MaxArchiveBytes)
                throw new InvalidDataException($"Comicbook size {file.Length} bytes is outside the allowed range 22–{MaxArchiveBytes} bytes.");
            if (Path.GetExtension(formatPath ?? path).ToLowerInvariant() is not (".cbz" or ".zip")) return file;
            // Bound the central directory before ZipArchive materializes it. ZIP64/multidisk are deliberately unsupported.
            var tail = new byte[(int)Math.Min(file.Length, 65557)];
            file.Position = file.Length - tail.Length;
            file.ReadExactly(tail);
            var valid = false;
            for (var i = tail.Length - 22; i >= 0; i--)
            {
                var end = tail.AsSpan(i);
                if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054b50
                    || i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != tail.Length) continue;
                var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
                var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
                var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
                if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0
                    || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0
                    || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count
                    || count == 0 || count > MaxEntries || directorySize > 16 * 1024 * 1024
                    || (long)directoryOffset + directorySize != file.Length - tail.Length + i)
                    throw new InvalidDataException($"Invalid ZIP directory layout: multidisk/ZIP64, empty directory, more than {MaxEntries} entries, directory over 16 MiB, or directory offset/size mismatch.");
                ValidateZipHeaders(file, directoryOffset, count);
                valid = true;
                break;
            }
            if (!valid) throw new InvalidDataException("ZIP end-of-central-directory record is missing or its comment length is invalid.");
            file.Position = 0;
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static void ValidateZipHeaders(FileStream file, long directoryOffset, int count)
    {
        Span<byte> header = stackalloc byte[46], local = stackalloc byte[30];
        file.Position = directoryOffset;
        for (var entry = 0; entry < count; entry++)
        {
            file.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw new InvalidDataException($"ZIP central-directory entry {entry} has an invalid header signature.");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            if ((flags & (1 | 64 | 8192)) != 0) throw new ArchiveFeatureException("Encrypted comicbooks are not supported.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0)
                throw new InvalidDataException("ZIP64 and multipart comicbooks are not supported.");
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            file.Position += BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            ValidateZipExtra(file, BinaryPrimitives.ReadUInt16LittleEndian(header[30..]));
            file.Position += BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var next = file.Position;
            if (offset >= directoryOffset) throw new InvalidDataException($"ZIP entry {entry} local-header offset overlaps the central directory.");
            file.Position = offset;
            file.ReadExactly(local);
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != 0x04034b50
                || BinaryPrimitives.ReadUInt16LittleEndian(local[6..]) != flags)
                throw new InvalidDataException($"ZIP entry {entry} has an invalid local-header signature or inconsistent flags.");
            file.Position += BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);
            ValidateZipExtra(file, BinaryPrimitives.ReadUInt16LittleEndian(local[28..]));
            file.Position = next;
        }
    }

    private static void ValidateZipExtra(FileStream file, int remaining)
    {
        Span<byte> header = stackalloc byte[4];
        if (file.Position + remaining > file.Length) throw new InvalidDataException("ZIP extra-field block extends beyond the source file.");
        while (remaining != 0)
        {
            if (remaining < 4) throw new InvalidDataException("ZIP extra-field header is truncated.");
            file.ReadExactly(header);
            var kind = BinaryPrimitives.ReadUInt16LittleEndian(header);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            if (kind == 1) throw new InvalidDataException("ZIP64 comicbooks are not supported.");
            if (length > remaining - 4) throw new InvalidDataException("ZIP extra-field length exceeds its containing block.");
            remaining -= 4 + length;
            file.Position += length;
        }
    }


    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 4096 || name[0] == '/' || name.Contains('\\')
            || name.Contains(':') || name.Any(char.IsControl)
            || name.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("Archive entry name is empty, exceeds 4096 characters, or contains an absolute path, traversal, backslash, colon, or control character.");
    }

    private static string? ImageMime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => null
    };

    private static bool HasImageSignature(ReadOnlySpan<byte> bytes, string mime) => mime switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/png" => bytes.StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };


    private static XDocument ReadXml(byte[] bytes, string rootName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, XmlSettings());
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Root?.Name.LocalName != rootName)
            throw new InvalidDataException($"Metadata XML must have a {rootName} root element; found {document.Root?.Name.LocalName ?? "(none)"}.");
        return document;
    }

    private static XDocument ParseXml(string xml, string rootName)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, XmlSettings());
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName != rootName) throw new InvalidDataException();
        return document;
    }

    private static XmlReaderSettings XmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes
    };

    private static string SafeRelative(string root, string path) => new(Path.GetRelativePath(root, path)
        .Select(c => char.IsControl(c) ? '_' : c).Take(240).ToArray());

    internal static bool IsArchiveError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or XmlException or ArgumentException or NotSupportedException or OverflowException or InvalidOperationException;

    internal static int NaturalCompare(string a, string b, bool ordinalTieBreak = true)
    {
        var x = 0;
        var y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (char.IsAsciiDigit(a[x]) && char.IsAsciiDigit(b[y]))
            {
                var ax = x;
                var by = y;
                while (x < a.Length && char.IsAsciiDigit(a[x])) x++;
                while (y < b.Length && char.IsAsciiDigit(b[y])) y++;
                while (ax < x && a[ax] == '0') ax++;
                while (by < y && b[by] == '0') by++;
                var length = (x - ax).CompareTo(y - by);
                if (length != 0) return length;
                var digits = a.AsSpan(ax, x - ax).SequenceCompareTo(b.AsSpan(by, y - by));
                if (digits != 0) return digits;
            }
            else
            {
                var compare = char.ToUpperInvariant(a[x++]).CompareTo(char.ToUpperInvariant(b[y++]));
                if (compare != 0) return compare;
            }
        }
        var remainder = (a.Length - x).CompareTo(b.Length - y);
        return remainder != 0 ? remainder : ordinalTieBreak ? string.CompareOrdinal(a, b) : 0;
    }

}
