using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record ReaderBookmark(int Page, string Name);
public sealed record BookmarkEdit([property: JsonRequired] string Name);
public sealed record ReaderPageInfo(int Page, string Type, int TypeMask, string Bookmark);

public sealed partial class LibraryStore
{
    private static void InitializeReader(SqliteConnection db)
    {
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS bookmarks (
                user_id TEXT NOT NULL, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                page INTEGER NOT NULL CHECK(page >= 0), name TEXT NOT NULL CHECK(length(name) BETWEEN 1 AND 200),
                PRIMARY KEY(user_id,book_id,page)
            );
            CREATE TABLE IF NOT EXISTS page_metadata_edits (
                book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                page INTEGER NOT NULL CHECK(page >= 0),
                fields INTEGER NOT NULL CHECK(fields BETWEEN 1 AND 3),
                PRIMARY KEY(book_id,page)
            );
            """);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT 1 FROM pragma_table_info('progress') WHERE name='updated_at'";
        if (command.ExecuteScalar() is null)
        {
            // Unknown legacy activity stays unknown until the reader saves progress.
            command.CommandText = "ALTER TABLE progress ADD COLUMN updated_at INTEGER";
            command.ExecuteNonQuery();
        }
    }

    public List<ReaderBookmark> GetBookmarks(string user, string id)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT m.page,m.name FROM books b LEFT JOIN bookmarks m
                ON m.book_id=b.id AND m.user_id=$user AND m.page<b.page_count
            WHERE b.id=$id ORDER BY m.page
            """, ("$user", user), ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        var result = new List<ReaderBookmark>();
        do
        {
            if (!reader.IsDBNull(0)) result.Add(new(reader.GetInt32(0), reader.GetString(1)));
        } while (reader.Read());
        return result;
    }

    public void SaveBookmark(string user, string id, int page, BookmarkEdit edit)
    {
        if (edit is null || string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 200 || edit.Name.Any(char.IsControl))
            throw new ArgumentException("Bookmark name must contain 1–200 characters and no control characters.");
        if (page < 0) throw new ArgumentException("Page is outside the book.");
        using var db = Open();
        using var command = Command(db, """
            INSERT INTO bookmarks(user_id,book_id,page,name)
            SELECT $user,id,$page,$name FROM books WHERE id=$id AND $page<page_count
            ON CONFLICT(user_id,book_id,page) DO UPDATE SET name=excluded.name
            """, ("$user", user), ("$id", id), ("$page", page), ("$name", edit.Name));
        if (command.ExecuteNonQuery() == 0)
        {
            using var exists = Command(db, "SELECT 1 FROM books WHERE id=$id", ("$id", id));
            if (exists.ExecuteScalar() is null) throw new KeyNotFoundException();
            throw new ArgumentException("Page is outside the book.");
        }
    }

    public void DeleteBookmark(string user, string id, int page)
    {
        if (page < 0) throw new ArgumentException("Page is outside the book.");
        using var db = Open();
        // Stale bookmarks remain deletable after an archive shrinks.
        using var command = Command(db, "DELETE FROM bookmarks WHERE user_id=$user AND book_id=$id AND page=$page",
            ("$user", user), ("$id", id), ("$page", page));
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
    }

    public List<ReaderPageInfo> GetPageInfo(string id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT page_count,info_xml FROM books WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        var count = reader.GetInt32(0);
        var xml = ParseXml(reader.GetString(1), "ComicInfo");
        var pages = new Dictionary<int, ReaderPageInfo>();
        var seen = new HashSet<int>();
        var ns = xml.Root!.Name.Namespace;
        foreach (var entry in xml.Root.Elements(ns + "Pages").Elements(ns + "Page"))
        {
            if (!int.TryParse(entry.Attribute("Image")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page)
                || page < 0 || page >= count) continue;
            // ponytail: ambiguous duplicate indices are unclassified rather than guessing which entry wins.
            if (!seen.Add(page)) { pages.Remove(page); continue; }
            var type = entry.Attribute("Type")?.Value ?? "Story";
            var mask = PageTypeMask(type);
            pages.Add(page, new(page, type, mask, entry.Attribute("Bookmark")?.Value ?? ""));
        }
        return pages.Values.OrderBy(page => page.Page).ToList();
    }

    private static int PageTypeMask(string type)
    {
        var mask = 0;
        foreach (var part in type.Split(','))
        {
            var token = part.Trim();
            var value = token switch
            {
                "FrontCover" => 1, "InnerCover" => 2, "Roundup" => 4, "Story" => 8,
                "Advertisement" or "Advertisment" => 16, "Editorial" => 32, "Letters" => 64,
                "Preview" => 128, "BackCover" => 256, "Other" => 512, "Deleted" => 1024,
                "All" => 1023, "AllWithDeleted" => 2047,
                _ => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) ? numeric : -1
            };
            // Unknown flags must never make a page silently skippable, even alongside known flags.
            if (value < 0 || (value & ~2047) != 0) return 0;
            mask |= value;
        }
        return mask == 0 ? 8 : mask; // Upstream treats an explicit zero as Story.
    }
}
