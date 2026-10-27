using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record BookGroup(string Key, string Label, int Count, string? CoverId,
    int Unread, int InProgress, int Completed, int CompletionPercent);
public sealed record BookGroupPage(List<BookGroup> Items, int Total, int Page, int PageSize)
{
    public IReadOnlyList<BookSection> Sections { get; init; } = Array.Empty<BookSection>();
}
public sealed record BookSection(string Key, string Label, int Start)
{
    public int Count { get; internal set; } = 1;
}

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
        int? rating = null, string location = "library", string sectionBy = "none", string? sectionKey = null,
        SmartListPreview? advancedFilter = null)
    {
        var groupColumn = GroupColumn(groupBy);
        if (groupKey is not null && groupColumn is null)
            throw new ArgumentException("A group key requires a grouping.");
        var sectionColumn = GroupColumn(sectionBy);
        if (sectionKey is not null && sectionColumn is null)
            throw new ArgumentException("A section key requires a section grouping.");
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var list = listId is null ? null : LoadList(db, transaction, user, listId);
        var (expression, collation) = ArrangeColumn(sort, list);
        var order = expression + collation + SortDirection(direction);
        if (sectionColumn is not null)
            order = sectionColumn + " COLLATE NOCASE," + sectionColumn + "," + order;
        using var command = Command(db, "");
        command.Transaction = transaction;
        var filter = BrowseFilter(command, user, query, status, publisher, availability, list, rating, location, advancedFilter);
        if (groupKey is not null)
        {
            filter += " AND " + groupColumn + "=$groupKey";
            command.Parameters.AddWithValue("$groupKey", groupKey);
        }
        if (sectionKey is not null)
        {
            filter += " AND " + sectionColumn + "=$sectionKey";
            command.Parameters.AddWithValue("$sectionKey", sectionKey);
        }
        var result = ReadBookPage(command, filter, order, page, pageSize, sectionBy, sectionColumn);
        if (groupBy == "series" && !string.IsNullOrWhiteSpace(groupKey) && result.Items.Count > 0)
            foreach (var (_, gap) in ReadMissingIssues(db, transaction, location, groupKey,
                sort == "number" && direction == "desc"))
                if (result.Items.Exists(book => book.Id == gap.BeforeBookId))
                    result.MissingIssues.Add(gap);
        transaction.Commit();
        return result;
    }

    public string? GetNextIssue(string id)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = Command(db, "SELECT series,number,path FROM books WHERE id=$id", ("$id", id));
        command.Transaction = transaction;
        string series, number, path;
        using (var current = command.ExecuteReader())
        {
            if (!current.Read()) throw new KeyNotFoundException();
            series = current.GetString(0);
            number = current.GetString(1);
            path = current.GetString(2);
        }
        if (string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(number)) return null;
        var location = archivePath is not null && IsContained(archivePath, path) ? "archived"
            : unsortedPath is not null && IsContained(unsortedPath, path) ? "unsorted" : "library";
        command.CommandText = """
            SELECT b.id,b.number FROM books b
            WHERE b.series=$series AND b.number COLLATE NATURAL_NUMBER>$number
                AND b.availability='available' AND
            """ + LocationFilter(command, location) + " ORDER BY b.number COLLATE NATURAL_NUMBER,b.id";
        command.Parameters.AddWithValue("$series", series);
        command.Parameters.AddWithValue("$number", number);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var candidate = reader.GetString(1);
            // The catalog's ordinal tie-break sorts variants like 02 and 2, not different issues.
            if (!string.IsNullOrWhiteSpace(candidate) && NaturalCompare(candidate, number, ordinalTieBreak: false) > 0)
                return reader.GetString(0);
        }
        return null;
    }

    private static int? WholeIssueNumber(string value) =>
        int.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private IEnumerable<(string Series, MissingIssue Gap)> ReadMissingIssues(SqliteConnection db,
        SqliteTransaction transaction, string location, string? series = null, bool descending = false, bool? ignored = null)
    {
        using var command = Command(db, "");
        command.Transaction = transaction;
        command.CommandText = "SELECT b.series,b.id,b.number,metadata_scalar(b.info_xml,'Volume') AS volume FROM books b WHERE "
            + LocationFilter(command, location) + (series is null ? "" : " AND b.series=$series")
            + (ignored is null ? "" : " AND EXISTS(SELECT 1 FROM ignored_missing_issue_series i WHERE i.series=b.series)=$ignored")
            + " ORDER BY b.series,volume,b.id";
        if (series is not null) command.Parameters.AddWithValue("$series", series);
        if (ignored is not null) command.Parameters.AddWithValue("$ignored", ignored.Value);
        using var reader = command.ExecuteReader();
        var numbers = new SortedDictionary<int, string>();
        string? currentSeries = null;
        int? currentVolume = null;
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var volume = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name != currentSeries || volume != currentVolume)
            {
                foreach (var gap in Gaps()) yield return (currentSeries!, gap);
                numbers.Clear();
                currentSeries = name;
                currentVolume = volume;
            }
            if (WholeIssueNumber(reader.GetString(2)) is { } number)
                numbers.TryAdd(number, reader.GetString(1));
        }
        foreach (var gap in Gaps()) yield return (currentSeries!, gap);

        IEnumerable<MissingIssue> Gaps()
        {
            KeyValuePair<int, string>? previous = null;
            foreach (var issue in numbers)
            {
                if (previous is { } lower && (long)issue.Key - lower.Key > 1)
                    yield return new(descending ? lower.Value : issue.Value, lower.Key + 1, issue.Key - 1, currentVolume);
                previous = issue;
            }
        }
    }

    private static BookPage ReadBookPage(SqliteCommand command, string filter, string order, int page, int pageSize,
        string sectionBy = "none", string? sectionColumn = null)
    {
        command.CommandText = "SELECT COUNT(*)" + filter;
        var total = Convert.ToInt32(command.ExecuteScalar());
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        command.CommandText = "SELECT " + BookColumns + (sectionColumn is null ? "" : "," + sectionColumn)
            + filter + " ORDER BY " + order
            + ",b.id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var sections = sectionColumn is null ? null : new List<BookSection>();
        var result = new BookPage(new List<Book>(Math.Min(pageSize, total)), total, page, pageSize)
        {
            Sections = sections is null ? Array.Empty<BookSection>() : sections
        };
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (sections is not null)
                    AppendSection(sections, reader.GetString(13), sectionBy, result.Items.Count);
                result.Items.Add(ReadBook(reader, false));
            }
        return result;
    }

    public BookGroupPage ListGroups(string user, string? query, string groupBy, int page = 1,
        int pageSize = 48, string status = "all", string? publisher = null, string? listId = null,
        string availability = "all", int? rating = null, string location = "library",
        string sectionBy = "none", string? sort = null, string direction = "asc", string? sectionKey = null,
        SmartListPreview? advancedFilter = null)
    {
        var column = GroupColumn(groupBy) ?? throw new ArgumentException("A grouping is required.");
        var sectionColumn = GroupColumn(sectionBy);
        if (sectionKey is not null && sectionColumn is null)
            throw new ArgumentException("A section key requires a section grouping.");
        var ordering = SortDirection(direction);
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var list = listId is null ? null : LoadList(db, transaction, user, listId);
        var order = "label COLLATE NOCASE," + column;
        if (sort is not null)
        {
            var (expression, collation) = ArrangeColumn(sort, list);
            // Apply the collation both inside MIN/MAX and to its result: aggregates do not inherit it.
            order = (direction == "asc" ? "MIN(" : "MAX(") + expression + collation + ")"
                + collation + ordering + "," + order;
        }
        if (sectionColumn is not null)
            order = sectionColumn + " COLLATE NOCASE," + sectionColumn + "," + order;
        using var command = Command(db, "");
        command.Transaction = transaction;
        var filter = BrowseFilter(command, user, query, status, publisher, availability, list, rating, location, advancedFilter);
        if (sectionKey is not null)
        {
            filter += " AND " + sectionColumn + "=$sectionKey";
            command.Parameters.AddWithValue("$sectionKey", sectionKey);
        }
        var grouped = filter + " GROUP BY " + (sectionColumn is null ? "" : sectionColumn + ",") + column;
        command.CommandText = "SELECT COUNT(*) FROM (SELECT " + column + grouped + ")";
        var total = Convert.ToInt32(command.ExecuteScalar());
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        var cover = groupBy == "series" ? SeriesCoverColumn : "MIN(CASE WHEN b.availability='available' THEN b.id END)";
        command.CommandText = (groupBy == "series"
            ? SeriesCoverRanks(filter, sectionColumn is null ? column : sectionColumn + "," + column) : "")
            + "SELECT " + column + ",CASE WHEN " + column
            + "='' THEN $unknown ELSE " + column + " END AS label,COUNT(*),"
            + cover + ","
            + "SUM(p.book_id IS NULL),SUM(p.book_id IS NOT NULL AND p.completed=0),SUM(COALESCE(p.completed,0))"
            + (sectionColumn is null ? "" : "," + sectionColumn) + grouped
            + " ORDER BY " + order + " LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$unknown", "Unknown " + groupBy);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var sections = sectionColumn is null ? null : new List<BookSection>();
        var result = new BookGroupPage(new List<BookGroup>(Math.Min(pageSize, total)), total, page, pageSize)
        {
            Sections = sections is null ? Array.Empty<BookSection>() : sections
        };
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (sections is not null)
                    AppendSection(sections, reader.GetString(7), sectionBy, result.Items.Count);
                result.Items.Add(new BookGroup(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetInt32(6), (int)(reader.GetInt64(6) * 100 / reader.GetInt64(2))));
            }
        transaction.Commit();
        return result;
    }

    public BookGroupPage ListSeriesShelf(string user, string kind, int page = 1, int pageSize = 12)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        var eligibility = kind switch
        {
            "on-deck" => """
                unread<count AND completed<count AND
                (last_activity >= $now-30*86400 OR last_added >= $now-7*86400 OR last_activity IS NULL)
                """,
            "recently-updated" => "last_added >= $now-12*86400",
            "newly-added" => "1",
            _ => throw new ArgumentException("Invalid series shelf.")
        };
        var order = kind switch
        {
            "on-deck" => "publisher COLLATE NOCASE,",
            "recently-updated" => "last_added DESC,",
            _ => "first_added DESC,"
        };
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = Command(db, "", ("$user", user), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        command.Transaction = transaction;
        var filter = " FROM books b LEFT JOIN progress p ON p.book_id=b.id AND p.user_id=$user"
            + " WHERE trim(b.series)<>'' AND " + LocationFilter(command, "library");
        var grouped = SeriesCoverRanks(filter, "b.series") + """
            ,series AS (
                SELECT b.series AS key,MIN(b.publisher COLLATE NOCASE) AS publisher,COUNT(*) AS count,
            """ + SeriesCoverColumn + """
                    AS cover,
                    SUM(p.book_id IS NULL) AS unread,
                    SUM(p.book_id IS NOT NULL AND p.completed=0) AS in_progress,
                    SUM(COALESCE(p.completed,0)) AS completed,
                    MAX(p.updated_at) AS last_activity,MAX(b.added_at) AS last_added,
                    CASE WHEN COUNT(b.added_at)=COUNT(*) THEN MIN(b.added_at) END AS first_added
            """ + filter + " GROUP BY b.series) ";
        command.CommandText = grouped + "SELECT COUNT(*) FROM series WHERE " + eligibility;
        var total = Convert.ToInt32(command.ExecuteScalar());
        page = Math.Min(page, Math.Max(1, (int)((total + (long)pageSize - 1) / pageSize)));
        command.CommandText = grouped
            + "SELECT key,count,cover,unread,in_progress,completed FROM series WHERE " + eligibility
            + " ORDER BY " + order + "key COLLATE NOCASE,key LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * (long)pageSize);
        var result = new List<BookGroup>(Math.Min(pageSize, total));
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                result.Add(new BookGroup(reader.GetString(0), reader.GetString(0), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.GetInt32(5), (int)(reader.GetInt64(5) * 100 / reader.GetInt64(1))));
        transaction.Commit();
        return new BookGroupPage(result, total, page, pageSize);
    }

    private const string SeriesCoverColumn = "MIN(CASE WHEN b.id IN (SELECT id FROM series_covers WHERE position=1) THEN b.id END)";

    private static string SeriesCoverRanks(string filter, string partition) =>
        "WITH series_covers AS (SELECT b.id,ROW_NUMBER() OVER (PARTITION BY " + partition
        + " ORDER BY COALESCE(p.completed,0),b.number COLLATE NATURAL_NUMBER,b.id) AS position"
        + filter + " AND b.availability='available') ";

    private static (string Expression, string Collation) ArrangeColumn(string sort, NativeList? list) => sort switch
    {
        "series" => ("b.series", " COLLATE NOCASE"),
        "list" => list?.Kind == "manual"
            ? ("(SELECT lb.position FROM list_books lb WHERE lb.list_id=$list AND lb.book_id=b.id)", "")
            : ("b.series", " COLLATE NOCASE"),
        "title" => ("b.title", " COLLATE NOCASE"),
        "number" => ("b.number", " COLLATE NATURAL_NUMBER"),
        "publisher" => ("b.publisher", " COLLATE NOCASE"),
        "pageCount" => ("b.page_count", ""),
        "personalRating" => ("COALESCE(r.rating,0)", ""),
        _ => throw new ArgumentException("Invalid sort.")
    };

    private static string SortDirection(string direction) => direction switch
    {
        "asc" => " ASC", "desc" => " DESC", _ => throw new ArgumentException("Invalid sort direction.")
    };

    private static void AppendSection(List<BookSection> sections, string key, string sectionBy, int index)
    {
        if (sections.Count > 0 && sections[^1].Key == key) sections[^1].Count++;
        else sections.Add(new(key, key.Length == 0 ? "Unknown " + sectionBy : key, index));
    }

    private static string? GroupColumn(string groupBy) => groupBy switch
    {
        "none" => null, "series" => "b.series", "publisher" => "b.publisher", "folder" => "b.folder",
        _ => throw new ArgumentException("Invalid grouping.")
    };

    private string BrowseFilter(SqliteCommand command, string user, string? query, string status,
        string? publisher, string availability, NativeList? list, int? rating, string location, SmartListPreview? advancedFilter)
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
            """ + progressFilter + ") AND " + LocationFilter(command, location);
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
        if (advancedFilter is not null)
        {
            ValidateSmartRules(advancedFilter.Match, advancedFilter.Rules);
            filter += " AND " + SmartListFilter(command, advancedFilter.Match, advancedFilter.Rules);
        }
        return filter;
    }

    private string LocationFilter(SqliteCommand command, string location, string parameterPrefix = "$")
    {
        string Within(string? root, string parameter)
        {
            if (root is null) return "0";
            var key = PathKey(root);
            command.Parameters.AddWithValue(parameter, key);
            command.Parameters.AddWithValue(parameter + "Prefix",
                Path.EndsInDirectorySeparator(key) ? key : key + Path.DirectorySeparatorChar);
            // Stored paths use PathKey; instr is literal and case-sensitive, unlike SQLite LIKE.
            return $"(b.path={parameter} OR instr(b.path,{parameter}Prefix)=1)";
        }
        return location switch
        {
            "library" => "(NOT " + Within(archivePath, parameterPrefix + "archived") + " AND NOT " + Within(unsortedPath, parameterPrefix + "unsorted") + ")",
            "archived" => Within(archivePath, parameterPrefix + "archived"),
            "unsorted" => Within(unsortedPath, parameterPrefix + "unsorted"),
            _ => throw new ArgumentException("Invalid library location.")
        };
    }

    public List<string> ListPublishers(string location = "library")
    {
        using var db = Open();
        using var command = Command(db, "");
        command.CommandText = "SELECT DISTINCT b.publisher FROM books b WHERE b.publisher<>'' AND "
            + LocationFilter(command, location) + " ORDER BY b.publisher COLLATE NOCASE,b.publisher";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
