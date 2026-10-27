using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public static class RelocateCatalog
{
    public static bool Run(IConfiguration configuration, string dataPath, string[] roots)
    {
        StateMaintenance.ValidateOptions(configuration, dataPath);
        var from = configuration["relocate-from"];
        var to = configuration["relocate-to"];
        var applyText = configuration["apply-relocation"];
        if (from is null && to is null && applyText is null) return false;
        var apply = applyText is not null && bool.Parse(applyText);
        using var gate = StateMaintenance.AcquireBackupGate(dataPath);
        LibraryStore.Relocate(Path.Combine(dataPath, "panelhaven.sqlite"), roots, from!, to!, apply);
        return true;
    }
}

public sealed partial class LibraryStore
{
    internal static void Relocate(string database, string[] configuredRoots, string from, string to, bool apply)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        var normalized = configuredRoots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (!normalized.Any(root => root.Equals(to, PathComparison)))
            throw new ArgumentException("Relocation destination must exactly equal a configured library root.");
        if (IsContained(from, to) || IsContained(to, from))
            throw new ArgumentException("Relocation roots must be distinct and must not overlap.");
        EnsureSafePath(to, to);
        var roots = normalized.Where(root => !normalized.Any(other =>
            !root.Equals(other, PathComparison) && IsContained(other, root))).ToArray();
        var rootIndex = Array.FindIndex(roots, root => IsContained(root, to));
        bool AliasCorresponds()
        {
            var parent = Path.GetDirectoryName(from);
            if (parent is null || !Directory.Exists(parent)) return false;
            EnsureSafePath(parent, parent);
            var info = new DirectoryInfo(from);
            return info.LinkTarget is not null && info.ResolveLinkTarget(true) is DirectoryInfo target
                && Path.TrimEndingDirectorySeparator(target.FullName).Equals(to, PathComparison);
        }
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = apply ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            ForeignKeys = true, Pooling = false
        }.ToString());
        db.Open();
        using var progressColumn = Command(db, "SELECT 1 FROM pragma_table_info('progress') WHERE name='updated_at'");
        var hasProgressTimestamp = progressColumn.ExecuteScalar() is not null;
        RefuseFileOperationRelocation(db, from, to);
        db.CreateCollation("NATURAL_NUMBER", static (a, b) => NaturalCompare(a ?? "", b ?? ""));
        var prefix = PathKey(Path.EndsInDirectorySeparator(from) ? from : from + Path.DirectorySeparatorChar);
        var sources = new List<(string Id, string Path)>();
        using (var command = Command(db, "SELECT id,path FROM books WHERE substr(path,1,length($prefix))=$prefix ORDER BY path", ("$prefix", prefix)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) sources.Add((reader.GetString(0), reader.GetString(1)));
        var sourceIds = sources.Select(book => book.Id).ToHashSet(StringComparer.Ordinal);
        // Completed intake records still bind their original and published paths.
        if (RelocationTableExists(db, "intake_operations"))
        {
            using var command = Command(db, """
                SELECT DISTINCT b.id,b.path FROM books b JOIN intake_operations h ON h.book_id=b.id
                WHERE substr(h.source,1,length($prefix))=$prefix OR substr(h.destination,1,length($prefix))=$prefix
                """, ("$prefix", prefix));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (sourceIds.Add(reader.GetString(0))) sources.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var table in new[] { "file_operations", "intake_operations" })
            if (RelocationTableExists(db, table))
                foreach (var (id, _) in sources)
                {
                    using var command = Command(db, $"SELECT 1 FROM {table} WHERE book_id=$id AND state='pending'", ("$id", id));
                    if (command.ExecuteScalar() is not null)
                        throw new InvalidOperationException("Resolve pending file operations for books with relevant relocation history first.");
                }
        var mapped = 0;
        var skipped = 0;
        var conflicts = 0;
        var applied = 0;
        var merged = 0;
        foreach (var (id, source) in sources)
        {
            if (!sourceIds.Contains(id)) continue;
            try
            {
                if (!Path.IsPathFullyQualified(source) || !Path.GetFullPath(source).Equals(source, PathComparison))
                    throw new InvalidDataException("Noncanonical source catalog path.");
                var moving = IsContained(from, source);
                var relative = moving ? Path.GetRelativePath(from, source) : source;
                var destination = RelocatedPath(source, from, to);
                var destinationRoot = moving ? to : roots.FirstOrDefault(root => IsContained(root, destination))
                    ?? throw new InvalidDataException("Book with relevant history is outside configured libraries.");
                var version = ReadSourceVersion(destinationRoot, destination);
                using var file = OpenArchive(destinationRoot, destination);
                using var archive = new ComicArchive(file, destination);
                // Validate the archive, not just a stale catalog fingerprint, before accepting any mapping.
                var metadata = archive.ReadMetadata(default);
                var freshBook = metadata.Book is null ? null : ReadXml(metadata.Book, "ComicBook", default);
                var freshInfo = metadata.Info is null ? new XDocument(new XElement("ComicInfo")) : ReadXml(metadata.Info, "ComicInfo", default);
                if (metadata.Info is null && freshBook is not null)
                    foreach (var element in freshBook.Root!.Elements()) freshInfo.Root!.Add(new XElement(element));
                file.Position = 0;
                var hash = Convert.ToHexString(SHA256.HashData(file));
                if (file.Length != version.Size || ReadSourceVersion(destinationRoot, destination) != version)
                    throw new IOException("Destination changed during validation; rescan and retry.");
                using var transaction = db.BeginTransaction(deferred: !apply);
                using var command = Command(db, "SELECT content_hash,edited,info_xml,book_xml FROM books WHERE id=$old AND path=$source",
                    ("$old", id), ("$source", source), ("$path", PathKey(destination)));
                command.Transaction = transaction;
                string? historicalHash;
                bool oldEdited;
                string oldInfo;
                string? oldBook;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException("Source catalog row changed; retry.");
                    historicalHash = reader.IsDBNull(0) ? null : reader.GetString(0);
                    oldEdited = reader.GetBoolean(1);
                    oldInfo = reader.GetString(2);
                    oldBook = reader.IsDBNull(3) ? null : reader.GetString(3);
                }
                var alias = moving && historicalHash is null && AliasCorresponds();
                if (historicalHash is not null && !historicalHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Historical SHA256 differs from destination; no relocation.");
                if (historicalHash is null && !alias)
                    throw new InvalidDataException("No historical hash or verified old-root symlink correspondence; cannot verify mapping.");
                string? destinationId = null;
                var infoXml = oldEdited ? oldInfo : freshInfo.ToString(SaveOptions.DisableFormatting);
                var bookXml = oldEdited ? oldBook : oldBook ?? freshBook?.ToString(SaveOptions.DisableFormatting);
                var edited = oldEdited;
                var conflict = "";
                command.CommandText = "SELECT id,edited,info_xml,book_xml FROM books WHERE path=$path AND id<>$old";
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        destinationId = reader.GetString(0);
                        if (reader.GetBoolean(1))
                        {
                            var destinationInfo = reader.GetString(2);
                            var destinationBook = reader.IsDBNull(3) ? null : reader.GetString(3);
                            if ((oldEdited && oldInfo != destinationInfo) || (oldBook is not null && oldBook != destinationBook))
                                conflict = "edited metadata differs";
                            else { infoXml = destinationInfo; bookXml = destinationBook; edited = true; }
                        }
                        else if (oldBook is not null && !reader.IsDBNull(3) && oldBook != reader.GetString(3))
                            conflict = "legacy metadata differs";
                    }
                }
                command.Parameters.AddWithValue("$new", (object?)destinationId ?? DBNull.Value);
                // A shared key must agree exactly. In particular, list positions are never silently reordered.
                foreach (var (table, key, difference) in new[]
                {
                    ("progress", "user_id", "a.page<>b.page OR a.completed<>b.completed"),
                    ("ratings", "user_id", "a.rating<>b.rating"),
                    ("bookmarks", "user_id", "a.name<>b.name"),
                    ("list_books", "list_id", "a.position<>b.position")
                })
                {
                    command.CommandText = $"SELECT 1 FROM {table} a JOIN {table} b ON a.{key}=b.{key} "
                        + (table == "bookmarks" ? "AND a.page=b.page " : "")
                        + $"WHERE a.book_id=$old AND b.book_id=$new AND ({difference}) LIMIT 1";
                    if (command.ExecuteScalar() is not null) conflict += (conflict.Length == 0 ? "" : "; ") + table + " values differ";
                }
                command.CommandText = "SELECT 1 FROM progress WHERE book_id IN ($old,$new) AND page >= $pages UNION ALL SELECT 1 FROM bookmarks WHERE book_id IN ($old,$new) AND page >= $pages LIMIT 1";
                command.Parameters.AddWithValue("$pages", archive.Pages.Count);
                if (command.ExecuteScalar() is not null) conflict += (conflict.Length == 0 ? "" : "; ") + "saved page exceeds destination page count";
                if (RelocationTableExists(db, "page_metadata_edits", transaction))
                {
                    command.CommandText = "SELECT 1 FROM page_metadata_edits WHERE book_id IN ($old,$new) AND page >= $pages LIMIT 1";
                    if (command.ExecuteScalar() is not null) conflict += (conflict.Length == 0 ? "" : "; ") + "edited page exceeds destination page count";
                }
                if (RelocationTableExists(db, "page_orders", transaction))
                {
                    command.Parameters.AddWithValue("$orderHash", hash);
                    command.CommandText = """
                        SELECT 1 FROM page_orders WHERE book_id IN ($old,$new) AND pages_json IS NOT NULL AND source_hash IS NOT $orderHash
                        UNION ALL
                        SELECT 1 FROM page_orders a JOIN page_orders b ON a.book_id=$old AND b.book_id=$new
                        WHERE a.pages_json IS NOT NULL AND b.pages_json IS NOT NULL AND a.pages_json<>b.pages_json LIMIT 1
                        """;
                    if (command.ExecuteScalar() is not null) conflict += (conflict.Length == 0 ? "" : "; ") + "page orders differ or source changed";
                }
                if (conflict.Length != 0)
                {
                    conflicts++;
                    Console.WriteLine($"CONFLICT {id} {relative}: {conflict}; both catalog rows and all private data retained.");
                    continue;
                }
                if (Encoding.UTF8.GetByteCount(infoXml) > MaxXmlBytes) throw new InvalidDataException("Metadata exceeds the catalog limit.");
                var historyPaths = RelocationPaths(db, transaction, id, destinationId, source, from, to);
                var info = ParseXml(infoXml, "ComicInfo").Root!;
                string Field(string name) => info.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
                var title = Field("Title");
                if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(destination);
                if (apply)
                {
                    ApplyRelocationHistory(db, transaction, id, destinationId, historyPaths);
                    foreach (var (table, columns) in new[]
                    {
                        ("progress", "user_id,page,completed" + (hasProgressTimestamp ? ",updated_at" : "")), ("ratings", "user_id,rating"),
                        ("bookmarks", "user_id,page,name"), ("list_books", "list_id,position")
                    })
                    {
                        command.CommandText = $"INSERT INTO {table}(book_id,{columns}) SELECT $old,{columns} FROM {table} WHERE book_id=$new "
                            + (table == "progress" && hasProgressTimestamp
                                ? """
                                    ON CONFLICT(user_id,book_id) DO UPDATE SET updated_at=
                                    CASE WHEN progress.updated_at IS NULL THEN excluded.updated_at
                                        WHEN excluded.updated_at IS NULL THEN progress.updated_at
                                        ELSE MAX(progress.updated_at,excluded.updated_at) END
                                    """
                                : "ON CONFLICT DO NOTHING");
                        command.ExecuteNonQuery();
                    }
                    if (RelocationTableExists(db, "page_orders", transaction))
                    {
                        command.CommandText = """
                            INSERT INTO page_orders(book_id,source_hash,revision,pages_json)
                            SELECT $old,source_hash,lower(hex(randomblob(16))),pages_json FROM page_orders WHERE book_id=$new
                            ON CONFLICT(book_id) DO UPDATE SET source_hash=excluded.source_hash,
                                revision=excluded.revision,pages_json=excluded.pages_json
                            WHERE page_orders.pages_json IS NULL AND excluded.pages_json IS NOT NULL
                            """;
                        command.ExecuteNonQuery();
                    }
                    command.CommandText = "DELETE FROM books WHERE id=$new";
                    command.ExecuteNonQuery();
                    var destinationIndex = moving ? rootIndex : Array.FindIndex(roots, root => IsContained(root, destination));
                    var displayRelative = Path.GetRelativePath(roots[destinationIndex], destination).Replace(Path.DirectorySeparatorChar, '/');
                    displayRelative = new string(displayRelative.Select(c => char.IsControl(c) ? '_' : c).ToArray());
                    var location = $"Library {destinationIndex + 1}/{displayRelative}";
                    var folder = location[..location.LastIndexOf('/')];
                    command.CommandText = """
                        UPDATE books SET path=$path,title=$title,series=$series,number=$number,publisher=$publisher,summary=$summary,
                            writer=$writer,penciller=$penciller,genre=$genre,year=$year,info_xml=$xml,book_xml=$book,edited=$edited,
                            page_count=$pages,file_size=$size,modified_ticks=$ticks,availability='available',
                            location=$location,folder=$folder,content_hash=$hash WHERE id=$old
                        """;
                    foreach (var (name, value) in new (string, object?)[]
                    {
                        ("$title", title), ("$series", Field("Series")), ("$number", Field("Number")), ("$publisher", Field("Publisher")),
                        ("$summary", Field("Summary")), ("$writer", Field("Writer")), ("$penciller", Field("Penciller")),
                        ("$genre", Field("Genre")), ("$year", MetadataYear(Field("Year"))), ("$xml", infoXml), ("$book", bookXml),
                        ("$edited", edited), ("$size", version.Size), ("$ticks", version.Ticks), ("$location", location), ("$folder", folder), ("$hash", hash)
                    }) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
                if (file.Length != version.Size || ReadSourceVersion(destinationRoot, destination) != version || (alias && !AliasCorresponds()))
                    throw new IOException("Filesystem correspondence changed before commit; this pair was rolled back.");
                if (apply) transaction.Commit();
                if (destinationId is not null) sourceIds.Remove(destinationId);
                mapped++;
                if (apply) { applied++; if (destinationId is not null) merged++; }
                Console.WriteLine($"{(apply ? "APPLIED" : "MAPPED")} {id} {relative}: {(destinationId is null ? "move path" : "merge " + destinationId)}; "
                    + (alias ? "verified root symlink correspondence, historical byte match unavailable" : "historical SHA256 matches freshly hashed destination"));
            }
            catch (Exception exception) when (IsArchiveError(exception))
            {
                skipped++;
                Console.WriteLine($"SKIPPED {id} {source}: {exception.Message}");
            }
        }
        Console.WriteLine($"Relocation {(apply ? "applied" : "preview")}: mapped={mapped} skipped={skipped} conflicts={conflicts} applied={applied} merged={merged}."
            + (apply ? " Source files were not changed." : " No changes. Apply with --apply-relocation true after a backup."));
    }
}
