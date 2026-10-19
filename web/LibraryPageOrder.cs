using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PageOrderEdit([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int[] Pages);
public sealed record BookPageOrder(string BookId, string Revision, int[] Pages, bool Custom);

public sealed partial class LibraryStore
{
    private static void InitializePageOrder(SqliteConnection db)
    {
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS page_orders (
                book_id TEXT PRIMARY KEY NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                source_hash TEXT, revision TEXT NOT NULL, pages_json TEXT
            );
            CREATE TRIGGER IF NOT EXISTS page_order_source_changed
            AFTER UPDATE OF content_hash,page_count ON books
            WHEN NEW.content_hash IS NOT OLD.content_hash OR NEW.page_count<>OLD.page_count
            BEGIN
                UPDATE page_orders SET source_hash=NEW.content_hash, pages_json=NULL,
                    revision=lower(hex(randomblob(16))) WHERE book_id=NEW.id;
            END;
            """);
        command.ExecuteNonQuery();
    }

    public BookPageOrder GetPageOrder(string id, CancellationToken cancellationToken = default) =>
        ReadOrSavePageOrder(id, null, cancellationToken);

    public BookPageOrder SavePageOrder(string id, PageOrderEdit edit, CancellationToken cancellationToken = default)
    {
        if (edit is null || edit.Pages is null || string.IsNullOrWhiteSpace(edit.ExpectedRevision))
            throw new ArgumentException("Page order requires a revision and every physical page exactly once.");
        return ReadOrSavePageOrder(id, edit, cancellationToken);
    }

    private BookPageOrder ReadOrSavePageOrder(string id, PageOrderEdit? edit, CancellationToken cancellationToken)
    {
        // Reads use the database transaction below; only edits must wait for scans and moves.
        if (edit is not null) scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            using var transaction = db.BeginTransaction(deferred: false);
            string path, generation, availability;
            string? hash, savedHash, json;
            int count;
            long size, ticks;
            using (var command = Command(db, """
                SELECT b.path,b.page_count,b.file_size,b.modified_ticks,b.content_hash,b.availability,
                    o.source_hash,COALESCE(o.revision,''),o.pages_json
                FROM books b LEFT JOIN page_orders o ON o.book_id=b.id WHERE b.id=$id
                """, ("$id", id)))
            {
                command.Transaction = transaction;
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new KeyNotFoundException();
                path = reader.GetString(0);
                count = reader.GetInt32(1);
                size = reader.GetInt64(2);
                ticks = reader.GetInt64(3);
                hash = reader.IsDBNull(4) ? null : reader.GetString(4);
                availability = reader.GetString(5);
                savedHash = reader.IsDBNull(6) ? null : reader.GetString(6);
                generation = reader.GetString(7);
                json = reader.IsDBNull(8) ? null : reader.GetString(8);
            }
            var revision = FileRevision(new { id, hash, count, generation });
            if (edit is not null)
            {
                if (edit.ExpectedRevision != revision) throw new MetadataRevisionException();
                ValidatePageOrder(edit.Pages, count);
            }
            var root = roots.FirstOrDefault(root => IsContained(root, path)) ?? throw new UnauthorizedAccessException();
            if (availability != "available" || hash is null) throw new InvalidOperationException("Rescan the source comicbook first.");
            var version = ReadSourceVersion(root, path);
            var custom = json is not null && savedHash == hash;
            var changed = version != (size, ticks);
            // Natural order cannot misapply a permutation; only saved overrides and writes need a fresh byte hash.
            if (!changed && (custom || edit is not null))
            {
                using var source = OpenArchive(root, path);
                changed = ArchiveHash(source, cancellationToken) != hash || ReadSourceVersion(root, path) != version;
            }
            if (changed)
            {
                // Even a same-size/same-timestamp replacement must be imported by the next ordinary scan.
                using var unavailable = Command(db, "UPDATE books SET availability='unavailable' WHERE id=$id", ("$id", id));
                unavailable.Transaction = transaction;
                unavailable.ExecuteNonQuery();
                transaction.Commit();
                throw new InvalidOperationException("Comicbook changed; an administrator must rescan the library.");
            }
            var pages = edit?.Pages ?? (custom ? JsonSerializer.Deserialize<int[]>(json!)! : Enumerable.Range(0, count).ToArray());
            if (edit is null && custom) ValidatePageOrder(pages, count);
            if (edit is not null)
            {
                custom = false;
                for (var index = 0; index < count; index++)
                    if (pages[index] != index) { custom = true; break; }
                generation = Guid.NewGuid().ToString("N");
                using var save = Command(db, """
                    INSERT INTO page_orders(book_id,source_hash,revision,pages_json) VALUES($id,$hash,$revision,$pages)
                    ON CONFLICT(book_id) DO UPDATE SET source_hash=excluded.source_hash,
                        revision=excluded.revision,pages_json=excluded.pages_json
                    """, ("$id", id), ("$hash", hash), ("$revision", generation),
                    ("$pages", custom ? JsonSerializer.Serialize(pages) : null));
                save.Transaction = transaction;
                save.ExecuteNonQuery();
                revision = FileRevision(new { id, hash, count, generation });
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(id, revision, pages, custom);
        }
        finally { if (edit is not null) scanLock.Release(); }
    }

    private static void ValidatePageOrder(int[]? pages, int count)
    {
        if (pages is null || pages.Length != count) throw new ArgumentException("Include every physical page exactly once.");
        var seen = new bool[count];
        foreach (var page in pages)
        {
            if (page < 0 || page >= count || seen[page]) throw new ArgumentException("Include every physical page exactly once.");
            seen[page] = true;
        }
    }
}
