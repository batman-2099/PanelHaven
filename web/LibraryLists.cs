using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace ComicRack.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListRule([property: JsonRequired] string Field,
    [property: JsonRequired] string Operator, [property: JsonRequired] string Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListEdit([property: JsonRequired] string Name, [property: JsonRequired] string Kind,
    [property: JsonRequired] string Match, [property: JsonRequired] ListRule[] Rules, string? FolderId = null);
public sealed record NativeList(string Id, string Name, string Kind, string Match, ListRule[] Rules,
    string[]? BookIds = null, string? FolderId = null);
public sealed record ListFolder(string Id, string Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListFolderEdit([property: JsonRequired] string Name);

public sealed partial class LibraryStore
{
    private static void InitializeLists(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS list_folders (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL,
                name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 200)
            );
            CREATE INDEX IF NOT EXISTS list_folders_user ON list_folders(user_id,name COLLATE NOCASE,id);
            CREATE TABLE IF NOT EXISTS lists (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL, name TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('manual','smart')),
                match TEXT NOT NULL CHECK(match IN ('all','any')), rules_json TEXT NOT NULL,
                folder_id TEXT REFERENCES list_folders(id) ON DELETE SET NULL
            );
            CREATE INDEX IF NOT EXISTS lists_user ON lists(user_id,name COLLATE NOCASE,id);
            CREATE TABLE IF NOT EXISTS list_books (
                list_id TEXT NOT NULL REFERENCES lists(id) ON DELETE CASCADE,
                book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                position INTEGER NOT NULL CHECK(position >= 0), PRIMARY KEY(list_id,book_id)
            );
            CREATE INDEX IF NOT EXISTS list_books_order ON list_books(list_id,position,book_id);
            """);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT 1 FROM pragma_table_info('lists') WHERE name='folder_id'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = "ALTER TABLE lists ADD COLUMN folder_id TEXT REFERENCES list_folders(id) ON DELETE SET NULL";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS lists_folder ON lists(folder_id);
            CREATE TRIGGER IF NOT EXISTS lists_folder_insert BEFORE INSERT ON lists
            WHEN NEW.folder_id IS NOT NULL AND NOT EXISTS(
                SELECT 1 FROM list_folders WHERE id=NEW.folder_id AND user_id=NEW.user_id)
            BEGIN SELECT RAISE(ABORT,'Folder does not belong to this account.'); END;
            CREATE TRIGGER IF NOT EXISTS lists_folder_update BEFORE UPDATE ON lists
            WHEN NEW.folder_id IS NOT NULL AND NOT EXISTS(
                SELECT 1 FROM list_folders WHERE id=NEW.folder_id AND user_id=NEW.user_id)
            BEGIN SELECT RAISE(ABORT,'Folder does not belong to this account.'); END;
            CREATE TRIGGER IF NOT EXISTS list_folders_owner_update BEFORE UPDATE OF user_id ON list_folders
            WHEN EXISTS(SELECT 1 FROM lists WHERE folder_id=OLD.id AND user_id<>NEW.user_id)
            BEGIN SELECT RAISE(ABORT,'Folder contains another account''s lists.'); END;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ValidateListId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("Invalid list, user, or book ID.");
    }

    private static void ValidateListEdit(ListEdit edit)
    {
        if (edit is null || string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 200)
            throw new ArgumentException("List name must contain 1–200 characters.");
        if (edit.Kind is not ("manual" or "smart") || edit.Match is not ("all" or "any"))
            throw new ArgumentException("Invalid list kind or rule match mode.");
        if (edit.Rules is null || (edit.Kind == "manual" ? edit.Rules.Length != 0 : edit.Rules.Length is < 1 or > 20))
            throw new ArgumentException("Manual lists have no rules; smart lists require 1–20 rules.");
        foreach (var rule in edit.Rules)
        {
            if (rule is null || rule.Value is null || rule.Value.Length > 2000)
                throw new ArgumentException("Invalid rule value; maximum length is 2000 characters.");
            switch (rule.Field)
            {
                case "Title": case "Series": case "Number": case "Publisher": case "Summary":
                case "Writer": case "Penciller": case "Genre":
                    if (rule.Operator is not ("contains" or "equals" or "notEquals"))
                        throw new ArgumentException("Invalid text rule operator.");
                    break;
                case "PageCount": case "Year": case "PersonalRating":
                    if (rule.Operator is not ("equals" or "greaterThan" or "lessThan")
                        || !int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                        || number < 0 || (rule.Field == "Year" && number > 9999) || (rule.Field == "PersonalRating" && number > 5))
                        throw new ArgumentException("Numeric rules require an integer in the field's range and a numeric operator.");
                    break;
                case "ReadStatus":
                    if (rule.Operator is not ("equals" or "notEquals")
                        || rule.Value is not ("unread" or "in-progress" or "completed"))
                        throw new ArgumentException("Invalid reading status rule.");
                    break;
                default: throw new ArgumentException("Unknown smart list field.");
            }
        }
    }

    private static NativeList ReadList(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), JsonSerializer.Deserialize<ListRule[]>(reader.GetString(4))!,
        FolderId: reader.IsDBNull(5) ? null : reader.GetString(5));

    private static NativeList LoadList(SqliteConnection db, SqliteTransaction transaction, string user, string id)
    {
        ValidateListId(user);
        ValidateListId(id);
        using var command = Command(db, "SELECT id,name,kind,match,rules_json,folder_id FROM lists WHERE id=$id AND user_id=$user",
            ("$id", id), ("$user", user));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return ReadList(reader);
    }

    public List<NativeList> GetLists(string user)
    {
        ValidateListId(user);
        using var db = Open();
        using var command = Command(db, "SELECT id,name,kind,match,rules_json,folder_id FROM lists WHERE user_id=$user ORDER BY name COLLATE NOCASE,id",
            ("$user", user));
        using var reader = command.ExecuteReader();
        var result = new List<NativeList>();
        while (reader.Read()) result.Add(ReadList(reader));
        return result;
    }

    public NativeList GetList(string user, string id)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var list = LoadList(db, transaction, user, id);
        if (list.Kind == "manual")
        {
            using var command = Command(db, "SELECT book_id FROM list_books WHERE list_id=$id ORDER BY position,book_id", ("$id", id));
            command.Transaction = transaction;
            var ids = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetString(0));
            list = list with { BookIds = ids.ToArray() };
        }
        transaction.Commit();
        return list;
    }

    public NativeList CreateList(string user, ListEdit edit)
    {
        ValidateListId(user);
        ValidateListEdit(edit);
        var list = new NativeList(Guid.NewGuid().ToString("N"), edit.Name.Trim(), edit.Kind, edit.Match, edit.Rules, FolderId: edit.FolderId);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        RequireListFolder(db, transaction, user, edit.FolderId);
        using var command = Command(db, "INSERT INTO lists(id,user_id,name,kind,match,rules_json,folder_id) VALUES($id,$user,$name,$kind,$match,$rules,$folder)",
            ("$id", list.Id), ("$user", user), ("$name", list.Name), ("$kind", list.Kind), ("$match", list.Match),
            ("$rules", JsonSerializer.Serialize(list.Rules)), ("$folder", list.FolderId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
        return list;
    }

    public NativeList UpdateList(string user, string id, ListEdit edit)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var list = LoadList(db, transaction, user, id);
        ValidateListEdit(edit);
        if (list.Kind != edit.Kind) throw new ArgumentException("List kind cannot change.");
        RequireListFolder(db, transaction, user, edit.FolderId);
        using var command = Command(db, "UPDATE lists SET name=$name,match=$match,rules_json=$rules,folder_id=$folder WHERE id=$id AND user_id=$user",
            ("$id", id), ("$user", user), ("$name", edit.Name.Trim()), ("$match", edit.Match),
            ("$rules", JsonSerializer.Serialize(edit.Rules)), ("$folder", edit.FolderId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
        return list with { Name = edit.Name.Trim(), Match = edit.Match, Rules = edit.Rules, FolderId = edit.FolderId };
    }

    public void DeleteList(string user, string id)
    {
        ValidateListId(user);
        ValidateListId(id);
        using var db = Open();
        using var command = Command(db, "DELETE FROM lists WHERE id=$id AND user_id=$user", ("$id", id), ("$user", user));
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
    }

    private static void RequireListFolder(SqliteConnection db, SqliteTransaction transaction, string user, string? folderId)
    {
        if (folderId is null) return;
        ValidateListId(folderId);
        using var command = Command(db, "SELECT 1 FROM list_folders WHERE id=$id AND user_id=$user", ("$id", folderId), ("$user", user));
        command.Transaction = transaction;
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException();
    }

    private static string ListFolderName(ListFolderEdit edit)
    {
        var name = edit?.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new ArgumentException("Folder name must contain 1–200 characters and no control characters.");
        return name;
    }

    public List<ListFolder> GetListFolders(string user)
    {
        ValidateListId(user);
        using var db = Open();
        using var command = Command(db, "SELECT id,name FROM list_folders WHERE user_id=$user ORDER BY name COLLATE NOCASE,id", ("$user", user));
        using var reader = command.ExecuteReader();
        var result = new List<ListFolder>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public ListFolder CreateListFolder(string user, ListFolderEdit edit)
    {
        ValidateListId(user);
        var folder = new ListFolder(Guid.NewGuid().ToString("N"), ListFolderName(edit));
        using var db = Open();
        using var command = Command(db, "INSERT INTO list_folders(id,user_id,name) VALUES($id,$user,$name)",
            ("$id", folder.Id), ("$user", user), ("$name", folder.Name));
        command.ExecuteNonQuery();
        return folder;
    }

    public ListFolder UpdateListFolder(string user, string id, ListFolderEdit edit)
    {
        ValidateListId(user);
        ValidateListId(id);
        var folder = new ListFolder(id, ListFolderName(edit));
        using var db = Open();
        using var command = Command(db, "UPDATE list_folders SET name=$name WHERE id=$id AND user_id=$user",
            ("$id", id), ("$user", user), ("$name", folder.Name));
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
        return folder;
    }

    public void DeleteListFolder(string user, string id)
    {
        ValidateListId(user);
        ValidateListId(id);
        using var db = Open();
        // ON DELETE SET NULL unfiles lists atomically without touching their membership or positions.
        using var command = Command(db, "DELETE FROM list_folders WHERE id=$id AND user_id=$user", ("$id", id), ("$user", user));
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException();
    }

    private static void RequireManualList(SqliteConnection db, SqliteTransaction transaction, string user, string id)
    {
        if (LoadList(db, transaction, user, id).Kind != "manual")
            throw new ArgumentException("Smart list membership is determined by its rules.");
    }

    private static void ValidateListBooks(SqliteConnection db, SqliteTransaction transaction, string[] bookIds)
    {
        if (bookIds is null || bookIds.Length is < 1 or > 100)
            throw new ArgumentException("Select 1–100 books.");
        using var command = Command(db, "");
        command.Transaction = transaction;
        for (var i = 0; i < bookIds.Length; i++)
        {
            ValidateListId(bookIds[i]);
            command.Parameters.AddWithValue("$book" + i, bookIds[i]);
        }
        command.CommandText = "SELECT COUNT(*) FROM books WHERE id IN ("
            + string.Join(',', Enumerable.Range(0, bookIds.Length).Select(i => "$book" + i)) + ")";
        if (Convert.ToInt32(command.ExecuteScalar()) != bookIds.Distinct(StringComparer.Ordinal).Count())
            throw new KeyNotFoundException("A selected book does not exist.");
    }

    public void AddListBooks(string user, string id, string[] bookIds)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        RequireManualList(db, transaction, user, id);
        ValidateListBooks(db, transaction, bookIds);
        using var command = Command(db, """
            INSERT INTO list_books(list_id,book_id,position)
            SELECT $id,$book,COALESCE(MAX(position),-1)+1 FROM list_books WHERE list_id=$id
            ON CONFLICT(list_id,book_id) DO NOTHING
            """, ("$id", id), ("$book", ""));
        command.Transaction = transaction;
        foreach (var book in bookIds)
        {
            command.Parameters["$book"].Value = book;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void RemoveListBooks(string user, string id, string[] bookIds)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        RequireManualList(db, transaction, user, id);
        ValidateListBooks(db, transaction, bookIds);
        using var command = Command(db, "DELETE FROM list_books WHERE list_id=$id AND book_id=$book", ("$id", id), ("$book", ""));
        command.Transaction = transaction;
        foreach (var book in bookIds)
        {
            command.Parameters["$book"].Value = book;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void MoveListBook(string user, string id, string bookId, string? beforeId)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        RequireManualList(db, transaction, user, id);
        ValidateListId(bookId);
        if (beforeId is not null) ValidateListId(beforeId);
        using var command = Command(db, "SELECT position FROM list_books WHERE list_id=$id AND book_id=$book",
            ("$id", id), ("$book", bookId));
        command.Transaction = transaction;
        var original = Convert.ToInt64(command.ExecuteScalar() ?? throw new KeyNotFoundException("Book is not in this list."));
        long target;
        if (beforeId is null)
        {
            command.CommandText = "SELECT MAX(position)+1 FROM list_books WHERE list_id=$id";
            target = Convert.ToInt64(command.ExecuteScalar());
        }
        else
        {
            command.Parameters["$book"].Value = beforeId;
            target = Convert.ToInt64(command.ExecuteScalar() ?? throw new KeyNotFoundException("Target book is not in this list."));
        }
        if (target > original) target--;
        // Shift only the crossed members; gaps left by removals need no compaction.
        command.CommandText = """
            UPDATE list_books SET position=position + CASE WHEN $original<$target THEN -1 ELSE 1 END
            WHERE list_id=$id AND ((position>$original AND position<=$target) OR (position>=$target AND position<$original));
            UPDATE list_books SET position=$target WHERE list_id=$id AND book_id=$book
            """;
        command.Parameters["$book"].Value = bookId;
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$original", original);
        if (original != target) command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string ListMembershipFilter(SqliteCommand command, NativeList list)
    {
        if (list.Kind == "manual")
        {
            command.Parameters.AddWithValue("$list", list.Id);
            return "EXISTS (SELECT 1 FROM list_books lb WHERE lb.list_id=$list AND lb.book_id=b.id)";
        }
        var rules = new string[list.Rules.Length];
        for (var i = 0; i < list.Rules.Length; i++)
        {
            var rule = list.Rules[i];
            var column = rule.Field switch
            {
                "Title" => "b.title", "Series" => "b.series", "Number" => "b.number", "Publisher" => "b.publisher",
                "Summary" => "b.summary", "PageCount" => "b.page_count", "Writer" => "b.writer",
                "Penciller" => "b.penciller", "Genre" => "b.genre", "Year" => "b.year", "PersonalRating" => "COALESCE(r.rating,0)",
                "ReadStatus" => "(CASE WHEN p.book_id IS NULL THEN 'unread' WHEN p.completed=1 THEN 'completed' ELSE 'in-progress' END)",
                _ => throw new ArgumentException("Unknown smart list field.")
            };
            var parameter = "$rule" + i;
            command.Parameters.AddWithValue(parameter, rule.Field is "PageCount" or "Year" or "PersonalRating"
                ? (object)int.Parse(rule.Value, CultureInfo.InvariantCulture) : rule.Value);
            rules[i] = rule.Operator switch
            {
                "contains" => $"instr(lower({column}),lower({parameter}))>0",
                "equals" => $"{column} = {parameter} COLLATE NOCASE",
                "notEquals" => $"{column} <> {parameter} COLLATE NOCASE",
                "greaterThan" => $"{column} > {parameter}",
                "lessThan" => $"{column} < {parameter}",
                _ => throw new ArgumentException("Unknown smart list operator.")
            };
        }
        return "(" + string.Join(list.Match == "all" ? " AND " : " OR ", rules) + ")";
    }
}
