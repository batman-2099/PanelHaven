using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PanelHaven.Web;

public sealed record KavitaImportUser(int Id, string Name);
public sealed record KavitaImportEntry(string Source, string? BookId, string? Target, string Status);
public sealed record KavitaImportPreview(IReadOnlyList<KavitaImportUser> Users, int? SourceUserId, string? Revision,
    int CompletedFiles, int PartialChapters, int ReadyBooks, int AlreadyReadBooks, int UnmatchedFiles,
    int AmbiguousFiles, IReadOnlyList<KavitaImportEntry> Entries);
public sealed record KavitaImportResult(int MarkedRead, int AlreadyRead);
public sealed class KavitaImportException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed partial class LibraryStore
{
    public const long MaxKavitaUploadBytes = 256L * 1024 * 1024;
    private const long MaxKavitaDatabaseBytes = 512L * 1024 * 1024;
    private const int MaxKavitaFiles = 100_000;
    private sealed record KavitaSource(List<KavitaImportUser> Users, int? UserId, string Hash,
        int PartialChapters, List<string> Files);
    private sealed record KavitaTarget(string Id, string Path, int Pages, int? Page, bool Completed, long? UpdatedAt);

    public async Task<KavitaImportPreview> PreviewKavitaImportAsync(string user, Stream upload, int? sourceUserId,
        CancellationToken cancellationToken = default)
    {
        var source = await ReadKavitaBackupAsync(upload, sourceUserId, cancellationToken);
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        return PlanKavitaImport(db, transaction, user, source, cancellationToken);
    }

    public async Task<KavitaImportResult> ApplyKavitaImportAsync(string user, Stream upload, int sourceUserId,
        string revision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(revision) || revision.Length != 64)
            throw new KavitaImportException(400, "Preview the backup before importing its read status.");
        var source = await ReadKavitaBackupAsync(upload, sourceUserId, cancellationToken);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var preview = PlanKavitaImport(db, transaction, user, source, cancellationToken);
        if (!string.Equals(revision, preview.Revision, StringComparison.Ordinal))
            throw new KavitaImportException(409, "The backup, library matches, or your reading progress changed. Preview again before importing.");
        using var save = Command(db, """
            INSERT INTO progress(user_id,book_id,page,completed,updated_at)
            SELECT $user,id,page_count-1,1,unixepoch() FROM books WHERE id=$book
            ON CONFLICT(user_id,book_id) DO UPDATE SET page=excluded.page,completed=1,updated_at=excluded.updated_at
            WHERE progress.completed=0
            """, ("$user", user), ("$book", ""));
        save.Transaction = transaction;
        foreach (var id in preview.Entries.Where(entry => entry.Status == "ready").Select(entry => entry.BookId!).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            save.Parameters["$book"].Value = id;
            save.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(preview.ReadyBooks, preview.AlreadyReadBooks);
    }

    private KavitaImportPreview PlanKavitaImport(SqliteConnection db, SqliteTransaction transaction, string user,
        KavitaSource source, CancellationToken cancellationToken)
    {
        if (source.UserId is null) return new(source.Users, null, null, 0, 0, 0, 0, 0, 0, []);
        var stems = source.Files.Select(file => KavitaMatchParts(file).Stem).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, List<KavitaTarget>>(StringComparer.OrdinalIgnoreCase);
        var suffixes = new Dictionary<string, List<KavitaTarget>>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command(db, """
            SELECT b.id,b.path,b.page_count,p.page,COALESCE(p.completed,0),p.updated_at
            FROM books b LEFT JOIN progress p ON p.book_id=b.id AND p.user_id=$user ORDER BY b.id
            """, ("$user", user)))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = reader.GetString(1);
                var (_, stem, suffix) = KavitaMatchParts(path);
                if (stem is null || !stems.Contains(stem)) continue;
                if (!targets.TryGetValue(stem, out var candidates)) targets.Add(stem, candidates = []);
                var target = new KavitaTarget(reader.GetString(0), path, reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetInt64(5));
                candidates.Add(target);
                if (suffix is not null)
                {
                    if (!suffixes.TryGetValue(suffix, out var narrowed)) suffixes.Add(suffix, narrowed = []);
                    narrowed.Add(target);
                }
            }
        }
        var entries = new List<KavitaImportEntry>(source.Files.Count);
        var ready = new HashSet<string>(StringComparer.Ordinal);
        var already = new HashSet<string>(StringComparer.Ordinal);
        var unmatched = 0;
        var ambiguous = 0;
        foreach (var file in source.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, stem, suffix) = KavitaMatchParts(file);
            if (stem is null || !targets.TryGetValue(stem, out var candidates))
            {
                entries.Add(new(name, null, null, "unmatched"));
                unmatched++;
                continue;
            }
            var matches = candidates;
            if (candidates.Count > 1 && suffix is not null && suffixes.TryGetValue(suffix, out var narrowed))
                matches = narrowed;
            if (matches.Count != 1)
            {
                entries.Add(new(name, null, null, "ambiguous"));
                ambiguous++;
                continue;
            }
            var target = matches[0];
            (target.Completed ? already : ready).Add(target.Id);
            entries.Add(new(name, target.Id, FileDisplayPath(target.Path), target.Completed ? "already-read" : "ready"));
        }
        var revision = FileRevision(new { user, source.UserId, source.Hash, targets });
        return new(source.Users, source.UserId, revision, source.Files.Count, source.PartialChapters,
            ready.Count, already.Count, unmatched, ambiguous, entries);
    }

    private static (string Name, string? Stem, string? Suffix) KavitaMatchParts(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length == 0 ? "" : parts[^1];
        var extension = Path.GetExtension(name);
        if (name.Contains(':') || name.Any(char.IsControl) || !IsArchive(name)) return (name, null, null);
        var stem = name[..^extension.Length];
        if (stem.Length == 0) return (name, null, null);
        return (name, stem, parts.Length < 3 ? null : parts[^3] + "/" + parts[^2] + "/" + stem);
    }

    private static async Task<KavitaSource> ReadKavitaBackupAsync(Stream upload, int? sourceUserId, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("panelhaven-kavita-");
        try
        {
            var databasePath = Path.Combine(directory.FullName, "source.sqlite");
            string hash;
            using (var zipFile = NewPrivateFile(Path.Combine(directory.FullName, "upload.zip")))
            {
                await CopyKavitaBytesAsync(upload, zipFile, MaxKavitaUploadBytes, cancellationToken);
                ValidateKavitaZipDirectory(zipFile);
                zipFile.Position = 0;
                using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > 10_000) throw new KavitaImportException(413, "The backup contains more than 10,000 ZIP entries.");
                ZipArchiveEntry? database = null;
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.FullName.Length > 4096 || entry.Length < 0 || entry.CompressedLength < 0 || entry.CompressedLength > zipFile.Length)
                        throw new KavitaImportException(422, "The backup has invalid ZIP entry declarations.");
                    var name = entry.FullName.Replace('\\', '/').Split('/')[^1];
                    if (name.StartsWith("kavita.db-", StringComparison.OrdinalIgnoreCase))
                        throw new KavitaImportException(422, "The backup contains SQLite sidecar files. Create a clean, checkpointed Kavita backup first.");
                    if (!name.Equals("kavita.db", StringComparison.OrdinalIgnoreCase)) continue;
                    if (database is not null || entry.FullName != "kavita.db")
                        throw new KavitaImportException(422, "The backup must contain exactly one root kavita.db.");
                    database = entry;
                }
                if (database is null) throw new KavitaImportException(422, "No root kavita.db was found. Upload a Kavita backup ZIP, not a CSV export.");
                if (database.Length > MaxKavitaDatabaseBytes)
                    throw new KavitaImportException(413, "The Kavita database exceeds the 512 MiB expanded limit.");
                using var output = NewPrivateFile(databasePath);
                using var digest = SHA256.Create();
                using var hashedOutput = new CryptoStream(output, digest, CryptoStreamMode.Write, leaveOpen: true);
                using var input = database.Open();
                var copied = await CopyKavitaBytesAsync(input, hashedOutput, MaxKavitaDatabaseBytes, cancellationToken, database.Crc32);
                if (copied != database.Length || copied < 100)
                    throw new KavitaImportException(422, "The backup contains a truncated or invalid Kavita database.");
                await hashedOutput.FlushFinalBlockAsync(cancellationToken);
                hash = Convert.ToHexString(digest.Hash!);
            }
            return ReadKavitaDatabase(databasePath, sourceUserId, hash, cancellationToken);
        }
        catch (SqliteException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new KavitaImportException(422, "The Kavita database is invalid, unsupported, or exceeded the safe query limit. Create a fresh Kavita backup.");
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            throw new KavitaImportException(413, "The backup exceeds the 256 MiB upload limit.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new KavitaImportException(422, "The backup could not be read safely. Upload an intact Kavita backup ZIP.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<long> CopyKavitaBytesAsync(Stream input, Stream output, long limit,
        CancellationToken cancellationToken, uint? expectedCrc = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        var crc = uint.MaxValue;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
            {
                total += read;
                if (total > limit) throw new KavitaImportException(413, "The backup exceeds the 256 MiB upload or 512 MiB expanded database limit.");
                if (expectedCrc is not null)
                    for (var index = 0; index < read; index++) crc = KavitaCrcTable[(crc ^ buffer[index]) & 255] ^ (crc >> 8);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (expectedCrc is not null && ~crc != expectedCrc)
                throw new KavitaImportException(422, "The Kavita database ZIP checksum is invalid. Upload an intact backup.");
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private static readonly uint[] KavitaCrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320U);
        return crc;
    }).ToArray();


    private static void ValidateKavitaZipDirectory(FileStream zip)
    {
        // Bound the central directory before ZipArchive materializes entries; ordinary Kavita backups do not need ZIP64.
        var end = new byte[(int)Math.Min(zip.Length, 65_557)];
        zip.Position = zip.Length - end.Length;
        zip.ReadExactly(end);
        for (var offset = end.Length - 22; offset >= 0; offset--)
        {
            var record = end.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06054b50
                || BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != record.Length - 22) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (count > 10_000 || size > 4 * 1024 * 1024)
                throw new KavitaImportException(413, "The backup ZIP directory exceeds the 10,000-entry or 4 MiB limit; ZIP64 backups are unsupported.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count
                || (long)start + size != zip.Length - end.Length + offset)
                throw new KavitaImportException(422, "Multipart, ZIP64, or invalid backup ZIP directories are unsupported.");
            ValidateZipHeaders(zip, start, count);
            return;
        }
        throw new KavitaImportException(422, "The upload is not an intact backup ZIP.");
    }

    private static KavitaSource ReadKavitaDatabase(string path, int? sourceUserId, string hash, CancellationToken cancellationToken)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
        }.ToString());
        db.Open();
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_LENGTH, 1024 * 1024);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_SQL_LENGTH, 1024 * 1024);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_COLUMN, 256);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_EXPR_DEPTH, 100);
        raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        var clock = Stopwatch.StartNew();
        var steps = 0;
        raw.sqlite3_progress_handler(db.Handle, 1000, _ =>
            cancellationToken.IsCancellationRequested || ++steps > 200_000 || clock.Elapsed > TimeSpan.FromSeconds(30) ? 1 : 0, null!);
        using (var safety = Command(db, "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8192;"))
            safety.ExecuteNonQuery();
        var schema = new (string Table, (string Name, string Type, bool Key)[] Columns)[]
        {
            ("AspNetUsers", [("Id", "INTEGER", true), ("UserName", "TEXT", false)]),
            ("Chapter", [("Id", "INTEGER", true), ("Pages", "INTEGER", false)]),
            ("AppUserProgresses", [("AppUserId", "INTEGER", false), ("ChapterId", "INTEGER", false), ("PagesRead", "INTEGER", false)]),
            ("MangaFile", [("Id", "INTEGER", true), ("ChapterId", "INTEGER", false), ("FilePath", "TEXT", false)])
        };
        foreach (var (table, columns) in schema)
        {
            using var ordinary = Command(db, "SELECT count(*) FROM pragma_table_list WHERE schema='main' AND name=$table AND type='table'", ("$table", table));
            if (Convert.ToInt32(ordinary.ExecuteScalar()) != 1)
                throw new KavitaImportException(422, "The backup lacks the required ordinary Kavita tables; views and virtual tables are not supported.");
            foreach (var (name, type, key) in columns)
            {
                using var column = Command(db, "SELECT count(*) FROM pragma_table_xinfo($table) WHERE name=$name AND upper(type)=$type AND hidden=0 AND ($key=0 OR pk=1)",
                    ("$table", table), ("$name", name), ("$type", type), ("$key", key));
                if (Convert.ToInt32(column.ExecuteScalar()) != 1)
                    throw new KavitaImportException(422, "The backup has an unsupported Kavita table schema.");
            }
            if (columns.Any(column => column.Key))
            {
                using var primary = Command(db, "SELECT count(*) FROM pragma_table_xinfo($table) WHERE pk>0", ("$table", table));
                if (Convert.ToInt32(primary.ExecuteScalar()) != 1)
                    throw new KavitaImportException(422, "The backup has unsupported composite Kavita identifiers.");
            }
        }
        using (var integrity = Command(db, "PRAGMA quick_check(1)"))
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new KavitaImportException(422, "The Kavita database failed its integrity check. Create a fresh backup.");
        var users = new List<KavitaImportUser>();
        using (var command = Command(db, "SELECT Id,UserName FROM AspNetUsers ORDER BY Id LIMIT 1001"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (users.Count == 1000) throw new KavitaImportException(413, "The backup exceeds the 1,000-user limit.");
                users.Add(new(KavitaInteger(reader, 0), KavitaText(reader, 1, 256)));
            }
        if (users.Count == 0) throw new KavitaImportException(422, "The backup contains no Kavita users.");
        if (sourceUserId is null && users.Count == 1) sourceUserId = users[0].Id;
        if (sourceUserId is not null && !users.Any(user => user.Id == sourceUserId))
            throw new KavitaImportException(400, "Choose a user present in this Kavita backup.");
        if (sourceUserId is null) return new(users, null, hash, 0, []);
        var completed = new HashSet<int>();
        var partial = new HashSet<int>();
        using (var command = Command(db, """
            SELECT p.AppUserId,p.ChapterId,p.PagesRead,c.Pages FROM AppUserProgresses p
            JOIN Chapter c ON c.Id=p.ChapterId WHERE p.AppUserId=$user LIMIT 1000001
            """, ("$user", sourceUserId)))
        using (var reader = command.ExecuteReader())
        {
            var count = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 1_000_000) throw new KavitaImportException(413, "The selected Kavita user exceeds the 1,000,000-progress-row limit.");
                if (KavitaInteger(reader, 0) != sourceUserId) throw new KavitaImportException(422, "Invalid Kavita progress user.");
                var chapter = KavitaInteger(reader, 1);
                var read = KavitaInteger(reader, 2);
                var pages = KavitaInteger(reader, 3);
                if (pages > 0 && read >= pages) completed.Add(chapter);
                else if (pages > 0 && read > 0) partial.Add(chapter);
            }
        }
        partial.ExceptWith(completed);
        var files = new List<string>();
        using (var command = Command(db, """
            SELECT f.Id,f.ChapterId,f.FilePath FROM MangaFile f WHERE EXISTS (
                SELECT 1 FROM AppUserProgresses p JOIN Chapter c ON c.Id=p.ChapterId
                WHERE p.AppUserId=$user AND p.ChapterId=f.ChapterId AND c.Pages>0 AND p.PagesRead>=c.Pages
            ) ORDER BY f.Id LIMIT 100001
            """, ("$user", sourceUserId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count == MaxKavitaFiles) throw new KavitaImportException(413, "The selected Kavita user exceeds the 100,000-completed-file limit.");
                KavitaInteger(reader, 0);
                if (!completed.Contains(KavitaInteger(reader, 1)))
                    throw new KavitaImportException(422, "The backup contains inconsistent completed chapters.");
                files.Add(KavitaText(reader, 2, 4096));
            }
        return new(users, sourceUserId, hash, partial.Count, files);
    }

    private static int KavitaInteger(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long number || number is < 0 or > int.MaxValue)
            throw new KavitaImportException(422, "The backup contains invalid Kavita identifiers or page counts.");
        return (int)number;
    }

    private static string KavitaText(SqliteDataReader reader, int ordinal, int limit)
    {
        if (reader.GetValue(ordinal) is not string text || text.Length is 0 || text.Length > limit || text.Any(char.IsControl))
            throw new KavitaImportException(422, "The backup contains missing, oversized, or invalid Kavita user names or file paths.");
        return text;
    }
}
