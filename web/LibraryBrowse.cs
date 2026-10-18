using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record BookGroup(string Key, string Label, int Count, string? CoverId,
    int Unread, int InProgress, int Completed, int CompletionPercent);
public sealed record BookGroupPage(List<BookGroup> Items, int Total, int Page, int PageSize);

public sealed partial class LibraryStore
{
    private const string BookColumns = """
        b.id,b.title,b.series,b.number,b.publisher,b.summary,b.page_count,
        MIN(COALESCE(p.page,0),b.page_count-1),COALESCE(p.completed,0),p.book_id IS NOT NULL,
        b.availability,b.location,COALESCE(r.rating,0)
        """;
    private const string BookFrom = " FROM books b LEFT JOIN progress p ON p.book_id=b.id AND p.user_id=$user "
        + "LEFT JOIN ratings r ON r.book_id=b.id AND r.user_id=$user ";

    public BookPage ListBooks(string user, string? query, int page = 1, int pageSize = 48,
        string sort = "series", string direction = "asc", string status = "all", string? publisher = null,
        string? listId = null, string availability = "all", string groupBy = "none", string? groupKey = null,
        int? rating = null)
    {
        var groupColumn = GroupColumn(groupBy);
        if (groupKey is not null && groupColumn is null)
            throw new ArgumentException("A group key requires a grouping.");
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var list = listId is null ? null : LoadList(db, transaction, user, listId);
        var order = sort switch
        {
            "series" => "b.series COLLATE NOCASE",
            "list" => list?.Kind == "manual"
                ? "(SELECT lb.position FROM list_books lb WHERE lb.list_id=$list AND lb.book_id=b.id)"
                : "b.series COLLATE NOCASE",
            "title" => "b.title COLLATE NOCASE",
            "number" => "b.number COLLATE NATURAL_NUMBER",
            "publisher" => "b.publisher COLLATE NOCASE",
            "pageCount" => "b.page_count",
            "personalRating" => "COALESCE(r.rating,0)",
            _ => throw new ArgumentException("Invalid sort.")
        };
        var ordering = direction switch
        {
            "asc" => " ASC", "desc" => " DESC", _ => throw new ArgumentException("Invalid sort direction.")
        };
        using var command = Command(db, "");
        command.Transaction = transaction;
        var filter = BrowseFilter(command, user, query, status, publisher, availability, list, rating);
        if (groupKey is not null)
        {
            filter += " AND " + groupColumn + "=$groupKey";
            command.Parameters.AddWithValue("$groupKey", groupKey);
        }
        command.CommandText = "SELECT COUNT(*)" + filter;
        var total = Convert.ToInt32(command.ExecuteScalar());
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        command.CommandText = "SELECT " + BookColumns + filter + " ORDER BY " + order + ordering
            + ",b.id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var result = new List<Book>(Math.Min(pageSize, total));
        using (var reader = command.ExecuteReader())
            while (reader.Read()) result.Add(ReadBook(reader, false));
        transaction.Commit();
        return new BookPage(result, total, page, pageSize);
    }

    public BookGroupPage ListGroups(string user, string? query, string groupBy, int page = 1,
        int pageSize = 48, string status = "all", string? publisher = null, string? listId = null,
        string availability = "all", int? rating = null)
    {
        var column = GroupColumn(groupBy) ?? throw new ArgumentException("A grouping is required.");
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var list = listId is null ? null : LoadList(db, transaction, user, listId);
        using var command = Command(db, "");
        command.Transaction = transaction;
        var filter = BrowseFilter(command, user, query, status, publisher, availability, list, rating);
        var grouped = filter + " GROUP BY " + column;
        command.CommandText = "SELECT COUNT(*) FROM (SELECT " + column + grouped + ")";
        var total = Convert.ToInt32(command.ExecuteScalar());
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        command.CommandText = "SELECT " + column + ",CASE WHEN " + column
            + "='' THEN $unknown ELSE " + column + " END AS label,COUNT(*),"
            + "MIN(CASE WHEN b.availability='available' THEN b.id END),"
            + "SUM(p.book_id IS NULL),SUM(p.book_id IS NOT NULL AND p.completed=0),SUM(COALESCE(p.completed,0))" + grouped
            + " ORDER BY label COLLATE NOCASE," + column + " LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$unknown", "Unknown " + groupBy);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var result = new List<BookGroup>(Math.Min(pageSize, total));
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                result.Add(new BookGroup(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetInt32(6), (int)(reader.GetInt64(6) * 100 / reader.GetInt64(2))));
        transaction.Commit();
        return new BookGroupPage(result, total, page, pageSize);
    }

    private static string? GroupColumn(string groupBy) => groupBy switch
    {
        "none" => null, "series" => "b.series", "publisher" => "b.publisher", "folder" => "b.folder",
        _ => throw new ArgumentException("Invalid grouping.")
    };

    private static string BrowseFilter(SqliteCommand command, string user, string? query, string status,
        string? publisher, string availability, NativeList? list, int? rating)
    {
        if (query?.Length > 2000) throw new ArgumentException("Search is too long.");
        if (publisher?.Length > 2000) throw new ArgumentException("Publisher is too long.");
        if (rating is < 0 or > 5) throw new ArgumentException("Rating must be between 0 and 5.");
        if (availability is not ("all" or "available" or "missing" or "unavailable"))
            throw new ArgumentException("Invalid availability.");
        var progressFilter = status switch
        {
            "all" => "1", "unread" => "p.book_id IS NULL",
            "in-progress" => "p.book_id IS NOT NULL AND p.completed=0",
            "completed" => "p.completed=1", _ => throw new ArgumentException("Invalid reading status.")
        };
        var filter = BookFrom + """
            WHERE ($q='' OR instr(lower(b.title || ' ' || b.series || ' ' || b.number || ' ' || b.publisher || ' ' || b.summary),lower($q))>0)
            AND (
            """ + progressFilter + ")";
        command.Parameters.AddWithValue("$user", user);
        command.Parameters.AddWithValue("$q", query?.Trim() ?? "");
        if (!string.IsNullOrEmpty(publisher))
        {
            filter += " AND b.publisher=$publisher";
            command.Parameters.AddWithValue("$publisher", publisher);
        }
        if (availability != "all")
        {
            filter += " AND b.availability=$availability";
            command.Parameters.AddWithValue("$availability", availability);
        }
        if (rating is not null)
        {
            filter += " AND COALESCE(r.rating,0)=$rating";
            command.Parameters.AddWithValue("$rating", rating.Value);
        }
        if (list is not null) filter += " AND " + ListMembershipFilter(command, list);
        return filter;
    }

    public List<string> ListPublishers()
    {
        using var db = Open();
        using var command = Command(db, "SELECT DISTINCT publisher FROM books WHERE publisher<>'' ORDER BY publisher COLLATE NOCASE,publisher");
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
