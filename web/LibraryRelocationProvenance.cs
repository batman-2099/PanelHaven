using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed partial class LibraryStore
{
    private static void InitializeRelocationProvenance(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS book_path_provenance (
                book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                source TEXT NOT NULL, destination TEXT NOT NULL,
                PRIMARY KEY(book_id,source,destination)
            );
            CREATE TABLE IF NOT EXISTS archive_backup_owners (
                owner_id TEXT PRIMARY KEY, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE
            );
            """);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static bool RelocationTableExists(SqliteConnection db, string table, SqliteTransaction? transaction = null)
    {
        using var command = Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table", ("$table", table));
        command.Transaction = transaction;
        return command.ExecuteScalar() is not null;
    }

    private static string RelocatedPath(string path, string from, string to)
    {
        if (!IsContained(from, path)) return path;
        if (!Path.IsPathFullyQualified(path) || !Path.GetFullPath(path).Equals(path, PathComparison))
            throw new InvalidDataException("Noncanonical historical path; no relocation.");
        return Path.GetFullPath(Path.Combine(to, Path.GetRelativePath(from, path)));
    }

    private static List<(string Source, string Destination)> RelocationPaths(SqliteConnection db, SqliteTransaction transaction,
        string id, string? mergedId, string source, string from, string to)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { source };
        foreach (var table in new[] { "file_operations", "intake_operations", "book_path_provenance" })
        {
            if (!RelocationTableExists(db, table, transaction)) continue;
            using var command = Command(db, $"SELECT source,destination FROM {table} WHERE book_id IN ($id,$merged)",
                ("$id", id), ("$merged", mergedId));
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read()) { paths.Add(reader.GetString(0)); paths.Add(reader.GetString(1)); }
        }
        return paths.Where(path => IsContained(from, path)).Select(path => (path, RelocatedPath(path, from, to))).ToList();
    }

    private static void ApplyRelocationHistory(SqliteConnection db, SqliteTransaction transaction, string id, string? mergedId,
        List<(string Source, string Destination)> paths)
    {
        InitializeRelocationProvenance(db, transaction);
        using var command = Command(db, "", ("$id", id), ("$merged", mergedId));
        command.Transaction = transaction;
        if (mergedId is not null)
        {
            // Keep immutable originals in their original owner directory, including earlier merges.
            command.CommandText = """
                INSERT INTO archive_backup_owners(owner_id,book_id) VALUES($merged,$id)
                    ON CONFLICT(owner_id) DO UPDATE SET book_id=$id;
                UPDATE archive_backup_owners SET book_id=$id WHERE book_id=$merged;
                INSERT OR IGNORE INTO book_path_provenance(book_id,source,destination)
                    SELECT $id,source,destination FROM book_path_provenance WHERE book_id=$merged;
                """;
            command.ExecuteNonQuery();
            if (RelocationTableExists(db, "page_metadata_edits", transaction))
            {
                command.CommandText = """
                    INSERT INTO page_metadata_edits(book_id,page,fields)
                        SELECT $id,page,fields FROM page_metadata_edits WHERE book_id=$merged
                        ON CONFLICT(book_id,page) DO UPDATE SET fields=fields|excluded.fields;
                    """;
                command.ExecuteNonQuery();
            }
            if (RelocationTableExists(db, "file_operations", transaction))
            {
                command.CommandText = "UPDATE file_operations SET book_id=$id WHERE book_id=$merged";
                command.ExecuteNonQuery();
            }
            if (RelocationTableExists(db, "intake_operations", transaction))
            {
                command.CommandText = "UPDATE intake_operations SET book_id=$id WHERE book_id=$merged";
                command.ExecuteNonQuery();
            }
        }
        command.Parameters.AddWithValue("$source", "");
        command.Parameters.AddWithValue("$destination", "");
        var hasJournal = RelocationTableExists(db, "file_operations", transaction);
        foreach (var (source, destination) in paths)
        {
            command.Parameters["$source"].Value = source;
            command.Parameters["$destination"].Value = destination;
            command.CommandText = """
                UPDATE OR IGNORE book_path_provenance SET destination=$destination WHERE book_id=$id AND destination=$source;
                DELETE FROM book_path_provenance WHERE book_id=$id AND destination=$source;
                INSERT OR IGNORE INTO book_path_provenance(book_id,source,destination) VALUES($id,$source,$destination);
                """;
            command.ExecuteNonQuery();
            if (RelocationTableExists(db, "intake_operations", transaction))
            {
                command.CommandText = """
                    UPDATE intake_operations SET source=CASE WHEN source=$source THEN $destination ELSE source END,
                        destination=CASE WHEN destination=$source THEN $destination ELSE destination END WHERE book_id=$id;
                    """;
                command.ExecuteNonQuery();
            }
            if (!hasJournal) continue;
            command.CommandText = """
                UPDATE file_operations SET source=CASE WHEN source=$source THEN $destination ELSE source END,
                    destination=CASE WHEN destination=$source THEN $destination ELSE destination END WHERE book_id=$id;
                """;
            command.ExecuteNonQuery();
        }
    }

    private List<string> ArchiveBackupOwners(SqliteConnection db, string id)
    {
        var owners = new List<string> { id };
        if (!RelocationTableExists(db, "archive_backup_owners")) return owners;
        using var command = Command(db, "SELECT owner_id FROM archive_backup_owners WHERE book_id=$id AND owner_id<>$id ORDER BY owner_id", ("$id", id));
        using var reader = command.ExecuteReader();
        while (reader.Read()) owners.Add(reader.GetString(0));
        return owners;
    }
}
