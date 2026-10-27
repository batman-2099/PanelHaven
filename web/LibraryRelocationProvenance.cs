using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed partial class LibraryStore
{
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
        if (RelocationTableExists(db, "intake_operations", transaction))
        {
            using var command = Command(db, "SELECT source,destination FROM intake_operations WHERE book_id IN ($id,$merged)",
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
        using var command = Command(db, "", ("$id", id), ("$merged", mergedId));
        command.Transaction = transaction;
        if (mergedId is not null)
        {
            if (RelocationTableExists(db, "page_metadata_edits", transaction))
            {
                command.CommandText = """
                    INSERT INTO page_metadata_edits(book_id,page,fields)
                        SELECT $id,page,fields FROM page_metadata_edits WHERE book_id=$merged
                        ON CONFLICT(book_id,page) DO UPDATE SET fields=fields|excluded.fields;
                    """;
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
        foreach (var (source, destination) in paths)
        {
            command.Parameters["$source"].Value = source;
            command.Parameters["$destination"].Value = destination;
            if (RelocationTableExists(db, "intake_operations", transaction))
            {
                command.CommandText = """
                    UPDATE intake_operations SET source=CASE WHEN source=$source THEN $destination ELSE source END,
                        destination=CASE WHEN destination=$source THEN $destination ELSE destination END WHERE book_id=$id;
                    """;
                command.ExecuteNonQuery();
            }
        }
    }
}
