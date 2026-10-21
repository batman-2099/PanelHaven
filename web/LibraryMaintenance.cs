using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record MaintenanceBook(string Id, string Title, string Location, string Availability, string? DuplicateKey);
public sealed record MaintenancePage(List<MaintenanceBook> Items, int Total, int Page, int PageSize);
public sealed record MissingIssueReport(string Series, string Location, int First, int Last);
public sealed record MissingIssueReportPage(List<MissingIssueReport> Items, int Total, int Page, int PageSize);
public sealed record DuplicateComparisonBook(Book Book, BookMetadata Metadata);
public sealed record DuplicateComparison(DuplicateComparisonBook Anchor, List<DuplicateComparisonBook> Items,
    int Total, int Page, int PageSize, string DuplicateKey);

public sealed partial class LibraryStore
{
    private void InitializeMaintenance(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command(db, "PRAGMA table_info(books)"))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        foreach (var (name, definition) in new[]
        {
            ("availability", "TEXT NOT NULL DEFAULT 'available' CHECK(availability IN ('available','missing','unavailable'))"),
            ("location", "TEXT NOT NULL DEFAULT ''"),
            ("folder", "TEXT NOT NULL DEFAULT ''"),
            ("content_hash", "TEXT"),
            ("added_at", "INTEGER")
        })
        {
            if (columns.Contains(name)) continue;
            using var command = Command(db, $"ALTER TABLE books ADD COLUMN {name} {definition}");
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        using (var command = Command(db, """
            CREATE INDEX IF NOT EXISTS books_availability ON books(availability,id);
            CREATE INDEX IF NOT EXISTS books_content_hash ON books(content_hash)
                WHERE availability='available' AND content_hash IS NOT NULL;
            CREATE INDEX IF NOT EXISTS books_folder ON books(folder,id);
            """))
        {
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        var locations = new List<(string Id, string Location, string Folder)>();
        using (var command = Command(db, "SELECT id,path,location,folder FROM books"))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var (location, folder) = SourceLocation(reader.GetString(1));
                if (location != reader.GetString(2) || folder != reader.GetString(3))
                    locations.Add((reader.GetString(0), location, folder));
            }
        }
        using (var command = Command(db, "UPDATE books SET location=$location,folder=$folder WHERE id=$id",
            ("$id", ""), ("$location", ""), ("$folder", "")))
        {
            command.Transaction = transaction;
            foreach (var (id, location, folder) in locations)
            {
                command.Parameters["$id"].Value = id;
                command.Parameters["$location"].Value = location;
                command.Parameters["$folder"].Value = folder;
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static string PathKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private (string Location, string Folder) SourceLocation(string path)
    {
        var index = Array.FindIndex(roots, root => IsContained(root, path));
        if (index < 0) return ("Unconfigured library", "Unconfigured library");
        var relative = Path.GetRelativePath(roots[index], path).Replace(Path.DirectorySeparatorChar, '/');
        relative = new string(relative.Select(c => char.IsControl(c) ? '_' : c).ToArray());
        var prefix = RootLabel(index);
        var separator = relative.LastIndexOf('/');
        return ($"{prefix}/{relative}", separator < 0 ? prefix : $"{prefix}/{relative[..separator]}");
    }

    private string SourceAvailability(string path)
    {
        var root = roots.FirstOrDefault(root => IsContained(root, path));
        if (root is null) return "unavailable";
        try
        {
            EnsureSafePath(root, root);
            var current = root;
            var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
            for (var index = 0; index < parts.Length; index++)
            {
                // Missing is only confirmed below an accessible, non-symlink configured root and accessible ancestors.
                using (var entries = Directory.EnumerateFileSystemEntries(current).GetEnumerator()) entries.MoveNext();
                current = Path.Combine(current, parts[index]);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    EnsureSafePath(root, Path.GetDirectoryName(current)!);
                    return "missing";
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0) return "unavailable";
                if (index == parts.Length - 1)
                {
                    if ((attributes & FileAttributes.Directory) != 0) return "unavailable";
                    EnsureSafePath(root, current);
                    return "available";
                }
                if ((attributes & FileAttributes.Directory) == 0) return "unavailable";
            }
        }
        catch (Exception ex) when (IsArchiveError(ex)) { }
        return "unavailable";
    }

    private void SetAvailability(SqliteConnection db, string path, string availability)
    {
        var (location, folder) = SourceLocation(path);
        using var command = Command(db, """
            UPDATE books SET availability=$availability,location=$location,folder=$folder
            WHERE path=$path AND (availability<>$availability OR location<>$location OR folder<>$folder)
            """, ("$path", PathKey(path)), ("$availability", availability), ("$location", location), ("$folder", folder));
        command.ExecuteNonQuery();
    }

    private void MarkImportFailure(SqliteConnection db, string path)
    {
        var availability = SourceAvailability(path);
        SetAvailability(db, path, availability == "missing" ? "missing" : "unavailable");
    }

    public MaintenancePage ListMaintenance(string kind, int page = 1, int pageSize = 48)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        var filter = kind switch
        {
            "missing" => "availability='missing'",
            "unavailable" => "availability='unavailable'",
            "duplicates" => """
                availability='available' AND content_hash IN (
                    SELECT content_hash FROM books WHERE availability='available' AND content_hash IS NOT NULL
                    GROUP BY content_hash HAVING COUNT(*)>1)
                """,
            _ => throw new ArgumentException("Invalid maintenance view.")
        };
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = Command(db, "SELECT COUNT(*) FROM books WHERE " + filter);
        command.Transaction = transaction;
        var total = Convert.ToInt32(command.ExecuteScalar());
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        command.CommandText = "SELECT id,title,location,availability," + (kind == "duplicates" ? "content_hash" : "NULL")
            + " FROM books WHERE " + filter + " ORDER BY " + (kind == "duplicates" ? "content_hash," : "")
            + "location,id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var books = new List<MaintenanceBook>(Math.Min(total, pageSize));
        using (var reader = command.ExecuteReader())
            while (reader.Read()) books.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        transaction.Commit();
        return new(books, total, page, pageSize);
    }

    public MissingIssueReportPage ListMissingIssues(int page = 1, int pageSize = 48)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var gaps = new List<MissingIssueReport>();
        foreach (var location in new[] { "library", "archived", "unsorted" })
            foreach (var (series, gap) in ReadMissingIssues(db, transaction, location))
                gaps.Add(new(series, location, gap.First, gap.Last));
        page = Math.Min(page, Math.Max(1, (int)((gaps.Count + (long)pageSize - 1) / pageSize)));
        var offset = (page - 1) * pageSize;
        var result = new MissingIssueReportPage(gaps.GetRange(offset, Math.Min(pageSize, gaps.Count - offset)),
            gaps.Count, page, pageSize);
        transaction.Commit();
        return result;
    }

    public DuplicateComparison CompareDuplicates(string user, string id, int page = 1, int pageSize = 3)
    {
        if (page < 1 || pageSize is < 1 or > 3) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = Command(db, "SELECT " + BookColumns + ",b.info_xml,b.book_xml,b.content_hash" + BookFrom
            + "WHERE b.id=$id AND b.availability='available' AND b.content_hash IS NOT NULL",
            ("$user", user), ("$id", id));
        command.Transaction = transaction;
        DuplicateComparisonBook anchor;
        string hash;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException();
            anchor = ReadComparisonBook(reader);
            hash = reader.GetString(15);
        }
        const string peers = " WHERE b.availability='available' AND b.content_hash=$hash AND b.id<>$id";
        command.CommandText = "SELECT COUNT(*) FROM books b" + peers;
        command.Parameters.AddWithValue("$hash", hash);
        var total = Convert.ToInt32(command.ExecuteScalar());
        if (total == 0) throw new KeyNotFoundException();
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        command.CommandText = "SELECT " + BookColumns + ",b.info_xml,b.book_xml" + BookFrom + peers
            + " ORDER BY b.location,b.id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var items = new List<DuplicateComparisonBook>(Math.Min(total, pageSize));
        using (var reader = command.ExecuteReader())
            while (reader.Read()) items.Add(ReadComparisonBook(reader));
        transaction.Commit();
        return new(anchor, items, total, page, pageSize, hash);

        static DuplicateComparisonBook ReadComparisonBook(SqliteDataReader reader) =>
            new(ReadBook(reader, false), ReadMetadata(ParseXml(reader.GetString(13), "ComicInfo").Root!,
                reader.IsDBNull(14) ? null : ParseXml(reader.GetString(14), "ComicBook").Root));
    }

    public void RemoveMissingBook(string id)
    {
        if (!scanLock.Wait(0)) throw new InvalidOperationException("A scan is running; wait before removing a catalog entry.");
        try
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            RefusePendingFileOperation(db, id, transaction);
            string path;
            using (var command = Command(db, "SELECT path,availability FROM books WHERE id=$id", ("$id", id)))
            {
                command.Transaction = transaction;
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new KeyNotFoundException();
                path = reader.GetString(0);
                if (reader.GetString(1) != "missing") throw new InvalidOperationException("Only confirmed missing books can be removed.");
            }
            if (SourceAvailability(path) != "missing")
                throw new InvalidOperationException("Source is present or unavailable; rescan before removing this catalog entry.");
            // SQLite cascades progress, ratings, bookmarks and list membership; source files are never changed.
            using (var command = Command(db, "DELETE FROM books WHERE id=$id", ("$id", id)))
            {
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }
            if (SourceAvailability(path) != "missing")
                throw new InvalidOperationException("Source availability changed; the catalog entry was not removed.");
            transaction.Commit();
        }
        finally { scanLock.Release(); }
    }
}
