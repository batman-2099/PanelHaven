using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonConverter(typeof(ListRuleJsonConverter))]
public sealed record ListRule(string? Field = null, string? Operator = null, string? Value = null,
    string? Match = null, ListRule[]? Rules = null);

public sealed class ListRuleJsonConverter : JsonConverter<ListRule>
{
    private static readonly string[] PropertyNames = ["Field", "Operator", "Value", "Match", "Rules"];
    public override ListRule Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a rule object.");
        string? field = null, operation = null, value = null, match = null;
        ListRule[]? rules = null;
        var seen = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var name = property.Name;
            var index = 0;
            for (; index < PropertyNames.Length; index++)
                if (string.Equals(name, options.PropertyNamingPolicy?.ConvertName(PropertyNames[index]) ?? PropertyNames[index],
                    options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            if (index == PropertyNames.Length || (seen & (1 << index)) != 0) throw new JsonException("Unknown or duplicate rule property.");
            seen |= 1 << index;
            if (index == 4)
            {
                if (property.Value.ValueKind != JsonValueKind.Array) throw new JsonException("Expected group rules.");
                rules = property.Value.Deserialize<ListRule[]>(options);
            }
            else
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw new JsonException("Expected a rule string.");
                var text = property.Value.GetString()!;
                switch (index)
                {
                    case 0: field = text; break;
                    case 1: operation = text; break;
                    case 2: value = text; break;
                    case 3: match = text; break;
                }
            }
        }
        if (seen is not (7 or 24)) throw new JsonException("Use either field/operator/value or match/rules.");
        return new(field, operation, value, match, rules);
    }

    public override void Write(Utf8JsonWriter writer, ListRule rule, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (rule.Match is not null || rule.Rules is not null)
        {
            writer.WriteString(options.PropertyNamingPolicy?.ConvertName("Match") ?? "Match", rule.Match);
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("Rules") ?? "Rules");
            JsonSerializer.Serialize(writer, rule.Rules, options);
        }
        else
        {
            writer.WriteString(options.PropertyNamingPolicy?.ConvertName("Field") ?? "Field", rule.Field);
            writer.WriteString(options.PropertyNamingPolicy?.ConvertName("Operator") ?? "Operator", rule.Operator);
            writer.WriteString(options.PropertyNamingPolicy?.ConvertName("Value") ?? "Value", rule.Value);
        }
        writer.WriteEndObject();
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListEdit([property: JsonRequired] string Name, [property: JsonRequired] string Kind,
    [property: JsonRequired] string Match, [property: JsonRequired] ListRule[] Rules, string? FolderId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SmartListPreview([property: JsonRequired] string Match, [property: JsonRequired] ListRule[] Rules);
public sealed record NativeList(string Id, string Name, string Kind, string Match, ListRule[] Rules,
    string[]? BookIds = null, string? FolderId = null);
public sealed record ListFolder(string Id, string Name, string? ParentId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListFolderEdit([property: JsonRequired] string Name, string? ParentId = null);

public sealed partial class LibraryStore
{
    public static readonly MetadataField[] ListRuleFields;
    private static readonly Dictionary<string, MetadataField> ListRuleSchema;
    private static readonly JsonSerializerOptions CatalogFilterJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16, AllowDuplicateProperties = false
    };

    static LibraryStore()
    {
        ListRuleFields =
        [
            new("Title", "Title", "text"), new("Series", "Series", "text"), new("Number", "Number", "text"),
            new("Publisher", "Publisher", "text"), new("Summary", "Summary", "text"),
            new("Writer", "Writer", "text"), new("Penciller", "Penciller", "text"), new("Genre", "Genre", "text"),
            new("Location", "Location", "text"), new("Folder", "Folder", "text"),
            new("PageCount", "Page count", "integer", Min: 0, Max: int.MaxValue),
            new("DaysSinceAdded", "Days since added", "integer", Min: 0, Max: int.MaxValue),
            new("DaysSinceRead", "Days since last read", "integer", Min: 0, Max: int.MaxValue),
            new("AddedDate", "Date added (UTC)", "date"), new("PublicationDate", "Publication date", "date"),
            new("Year", "Year", "integer", Min: 0, Max: 9999),
            new("PersonalRating", "Personal rating", "integer", Min: 0, Max: 5),
            new("ReadStatus", "Reading status", "readStatus"), new("Availability", "Availability", "availability"),
            new("SeriesReadStatus", "Series reading status", "readStatus"),
            new("HasMissingIssues", "Series has missing issue numbers", "boolean", ["yes", "no"]),
            new("FileSizeMB", "File size (MiB)", "number", Min: 0, Max: long.MaxValue / 1048576d),
            new("FileType", "File type", "text"), new("ManualList", "Manual reading list", "listMembership"),
            .. MetadataFields.Where(field => field.Name is not ("Title" or "Series" or "Number" or "Publisher"
                or "Summary" or "Writer" or "Penciller" or "Genre" or "Year" or "PageCount"))
                .Select(field => field.Type == "integer" ? field with { Label = field.Name, Min = field.Name is "Month" or "Day" ? 1 : 0 }
                    : field.Type is "textarea" or "enum" ? field with { Type = "text" } : field)
        ];
        ListRuleSchema = ListRuleFields.ToDictionary(field => field.Name);
    }

    private static object? SmartMetadataScalar(string xml, string name)
    {
        // ponytail: uncommon fields use catalog XML; add indexed columns only if large-library queries need them.
        var root = ParseXml(xml, "ComicInfo").Root!;
        if (name == "PublicationDate")
        {
            int Part(string part) => int.TryParse(root.Element(root.Name.Namespace + part)?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
            var year = Part("Year");
            var month = Part("Month");
            var day = Part("Day");
            return year is >= 1 and <= 9999 && month is >= 1 and <= 12
                && day >= 1 && day <= DateTime.DaysInMonth(year, month)
                ? new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        }
        var value = root.Element(root.Name.Namespace + name)?.Value ?? "";
        var field = ListRuleSchema[name];
        if (field.Type == "number")
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue)
                && double.IsFinite(decimalValue) && decimalValue >= field.Min && decimalValue <= field.Max ? decimalValue : null;
        if (field.Type != "integer") return value;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && number >= field.Min && number <= field.Max ? number : null;
    }

    private static void InitializeLists(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var command = Command(db, """
            CREATE TABLE IF NOT EXISTS list_folders (
                id TEXT PRIMARY KEY, user_id TEXT NOT NULL,
                name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 200),
                parent_id TEXT REFERENCES list_folders(id) ON DELETE SET NULL
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
            CREATE TABLE IF NOT EXISTS default_list_seeds (
                user_id TEXT PRIMARY KEY NOT NULL
            );
            """);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT 1 FROM pragma_table_info('lists') WHERE name='folder_id'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = "ALTER TABLE lists ADD COLUMN folder_id TEXT REFERENCES list_folders(id) ON DELETE SET NULL";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT 1 FROM pragma_table_info('list_folders') WHERE name='parent_id'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = "ALTER TABLE list_folders ADD COLUMN parent_id TEXT REFERENCES list_folders(id) ON DELETE SET NULL";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS lists_folder ON lists(folder_id);
            CREATE INDEX IF NOT EXISTS list_folders_parent ON list_folders(parent_id);
            CREATE TRIGGER IF NOT EXISTS list_folders_parent_insert BEFORE INSERT ON list_folders
            WHEN NEW.parent_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT,'Invalid folder parent.') WHERE NEW.parent_id=NEW.id OR NOT EXISTS(
                    SELECT 1 FROM list_folders WHERE id=NEW.parent_id AND user_id=NEW.user_id);
            END;
            CREATE TRIGGER IF NOT EXISTS list_folders_parent_update BEFORE UPDATE ON list_folders
            WHEN NEW.parent_id IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT,'Invalid folder parent.') WHERE NOT EXISTS(
                    SELECT 1 FROM list_folders WHERE id=NEW.parent_id AND user_id=NEW.user_id);
                SELECT RAISE(ABORT,'Folder cycle.') WHERE NEW.id IN (
                    WITH RECURSIVE ancestors(id,parent_id) AS (
                        SELECT id,parent_id FROM list_folders WHERE id=NEW.parent_id
                        UNION ALL SELECT f.id,f.parent_id FROM list_folders f JOIN ancestors a ON f.id=a.parent_id
                    ) SELECT id FROM ancestors);
            END;
            CREATE TRIGGER IF NOT EXISTS list_folders_children_owner_update BEFORE UPDATE OF user_id ON list_folders
            WHEN EXISTS(SELECT 1 FROM list_folders WHERE parent_id=OLD.id AND user_id<>NEW.user_id)
            BEGIN SELECT RAISE(ABORT,'Folder contains another account''s folders.'); END;
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
        if (edit.Rules is null || (edit.Kind == "manual" && edit.Rules.Length != 0))
            throw new ArgumentException("Manual lists have no rules; smart lists require rules.");
        if (edit.Kind == "smart") ValidateSmartRules(edit.Match, edit.Rules);
    }

    private static void ValidateSmartRules(string match, ListRule[] rules)
    {
        var leaves = 0;
        var groups = 0;
        ValidateGroup(match, rules, 1);
        void ValidateGroup(string mode, ListRule[] children, int depth)
        {
            if (mode is not ("all" or "any") || children is null || children.Length is < 1 or > 20
                || depth > 4 || ++groups > 20)
                throw new ArgumentException("Smart lists require nonempty all/any groups, at most 4 group levels and 20 groups including the root.");
            foreach (var rule in children)
            {
                if (rule is null) throw new ArgumentException("A rule is required.");
                if (rule.Match is not null || rule.Rules is not null)
                {
                    if (rule.Field is not null || rule.Operator is not null || rule.Value is not null
                        || rule.Match is null || rule.Rules is null)
                        throw new ArgumentException("Use either field/operator/value or match/rules.");
                    ValidateGroup(rule.Match, rule.Rules, depth + 1);
                    continue;
                }
                if (++leaves > 20) throw new ArgumentException("Smart lists support at most 20 total leaf rules.");
                if (rule.Field is null || !ListRuleSchema.TryGetValue(rule.Field, out var field))
                    throw new ArgumentException("Unknown smart list field.");
                if (rule.Value is null || rule.Value.Length > 2000)
                    throw new ArgumentException("Invalid rule value; maximum length is 2000 characters.");
                if (rule.Operator is "isEmpty" or "isNotEmpty")
                {
                    if (rule.Value != "") throw new ArgumentException("Empty checks require an empty rule value.");
                    if (field.Type is "integer" or "number" or "date" or "listMembership"
                        && rule.Field is not ("PageCount" or "PersonalRating" or "FileSizeMB")) continue;
                }
                switch (field.Type)
                {
                    case "text":
                        if (rule.Field == "Number" && rule.Operator is "greaterThan" or "lessThan" or "greaterOrEqual" or "lessOrEqual")
                        {
                            if (string.IsNullOrWhiteSpace(rule.Value)) throw new ArgumentException("Issue comparisons require an issue number.");
                            break;
                        }
                        if (rule.Operator is not ("contains" or "notContains" or "equals" or "notEquals"
                            or "startsWith" or "endsWith" or "isEmpty" or "isNotEmpty"))
                            throw new ArgumentException("Invalid text rule operator.");
                        break;
                    case "integer":
                        if (rule.Operator is not ("equals" or "notEquals" or "greaterThan" or "lessThan" or "greaterOrEqual" or "lessOrEqual")
                            || !int.TryParse(rule.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                            || number < field.Min || number > field.Max)
                            throw new ArgumentException("Numeric rules require an integer in the field's range and a numeric operator.");
                        break;
                    case "number":
                        if (rule.Operator is not ("equals" or "notEquals" or "greaterThan" or "lessThan" or "greaterOrEqual" or "lessOrEqual")
                            || !double.TryParse(rule.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var decimalValue)
                            || !double.IsFinite(decimalValue) || decimalValue < field.Min || decimalValue > field.Max)
                            throw new ArgumentException("Numeric rules require a finite number in the field's range and a numeric operator.");
                        break;
                    case "date":
                        if (rule.Operator is not ("equals" or "notEquals" or "greaterThan" or "lessThan" or "greaterOrEqual" or "lessOrEqual")
                            || !DateOnly.TryParseExact(rule.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                            throw new ArgumentException("Date rules require a valid YYYY-MM-DD date and a comparison operator.");
                        break;
                    case "boolean":
                        if (rule.Operator is not ("equals" or "notEquals") || rule.Value is not ("yes" or "no"))
                            throw new ArgumentException("Boolean rules require yes or no.");
                        break;
                    case "listMembership":
                        if (rule.Operator is not ("equals" or "notEquals"))
                            throw new ArgumentException("Manual list rules require equals, notEquals, isEmpty or isNotEmpty.");
                        ValidateListId(rule.Value);
                        break;
                    case "readStatus":
                        if (rule.Operator is not ("equals" or "notEquals")
                            || rule.Value is not ("unread" or "in-progress" or "completed"))
                            throw new ArgumentException("Invalid reading status rule.");
                        break;
                    case "availability":
                        if (rule.Operator is not ("equals" or "notEquals")
                            || rule.Value is not ("available" or "missing" or "unavailable"))
                            throw new ArgumentException("Invalid availability rule.");
                        break;
                }
            }
        }
    }

    public static SmartListPreview? ParseCatalogFilter(string? filter)
    {
        if (filter is null) return null;
        if (filter.Length > 6000 || Uri.EscapeDataString(filter).Length > 6000)
            throw new ArgumentException("Advanced filters must fit within 6000 URL-encoded characters. Use fewer or shorter rules.");
        try
        {
            var result = JsonSerializer.Deserialize<SmartListPreview>(filter, CatalogFilterJsonOptions)
                ?? throw new ArgumentException("Advanced filters require an object with match and rules.");
            ValidateSmartRules(result.Match, result.Rules);
            return result;
        }
        catch (JsonException exception) { throw new ArgumentException("Advanced filters require valid match/rules JSON: " + exception.Message); }
    }

    public BookPage PreviewSmartList(string user, SmartListPreview edit, int page = 1, int pageSize = 48)
    {
        ValidateListId(user);
        if (edit is null) throw new ArgumentException("Smart rules are required.");
        ValidateSmartRules(edit.Match, edit.Rules);
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid page or page size.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = Command(db, "", ("$user", user));
        command.Transaction = transaction;
        var filter = BookFrom + " WHERE " + SmartListFilter(command, edit.Match, edit.Rules)
            + " AND " + LocationFilter(command, "library");
        var result = ReadBookPage(command, filter, "b.series COLLATE NOCASE ASC", page, pageSize);
        transaction.Commit();
        return result;
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
        using (var seed = Command(db, "SELECT 1 FROM default_list_seeds WHERE user_id=$user", ("$user", user)))
        {
            if (seed.ExecuteScalar() is null)
            {
                using var transaction = db.BeginTransaction();
                seed.Transaction = transaction;
                seed.CommandText = "INSERT INTO default_list_seeds(user_id) VALUES($user) ON CONFLICT(user_id) DO NOTHING";
                if (seed.ExecuteNonQuery() != 0)
                {
                    seed.CommandText = """
                        INSERT INTO lists(id,user_id,name,kind,match,rules_json)
                        SELECT $id,$user,$name,'smart','all',$rules
                        WHERE NOT EXISTS(SELECT 1 FROM lists WHERE user_id=$user AND name=$name COLLATE NOCASE)
                        """;
                    seed.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                    seed.Parameters.AddWithValue("$name", "Recently added");
                    seed.Parameters.AddWithValue("$rules", JsonSerializer.Serialize(new ListRule[] { new("DaysSinceAdded", "lessThan", "30") }));
                    seed.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
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
        using var command = Command(db, "SELECT id,name,parent_id FROM list_folders WHERE user_id=$user ORDER BY name COLLATE NOCASE,id", ("$user", user));
        using var reader = command.ExecuteReader();
        var result = new List<ListFolder>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }

    public ListFolder CreateListFolder(string user, ListFolderEdit edit)
    {
        ValidateListId(user);
        var folder = new ListFolder(Guid.NewGuid().ToString("N"), ListFolderName(edit), edit.ParentId);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        RequireListFolder(db, transaction, user, folder.ParentId);
        using var command = Command(db, "INSERT INTO list_folders(id,user_id,name,parent_id) VALUES($id,$user,$name,$parent)",
            ("$id", folder.Id), ("$user", user), ("$name", folder.Name), ("$parent", folder.ParentId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
        return folder;
    }

    public ListFolder UpdateListFolder(string user, string id, ListFolderEdit edit)
    {
        ValidateListId(user);
        ValidateListId(id);
        var folder = new ListFolder(id, ListFolderName(edit), edit.ParentId);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        RequireListFolder(db, transaction, user, id);
        RequireListFolder(db, transaction, user, folder.ParentId);
        using var command = Command(db, """
            WITH RECURSIVE ancestors(id,parent_id) AS (
                SELECT id,parent_id FROM list_folders WHERE id=$parent
                UNION ALL SELECT f.id,f.parent_id FROM list_folders f JOIN ancestors a ON f.id=a.parent_id
            ) SELECT 1 FROM ancestors WHERE id=$id
            """, ("$id", id), ("$user", user), ("$name", folder.Name), ("$parent", folder.ParentId));
        command.Transaction = transaction;
        if (command.ExecuteScalar() is not null) throw new ArgumentException("A folder cannot contain itself or an ancestor.");
        command.CommandText = "UPDATE list_folders SET name=$name,parent_id=$parent WHERE id=$id AND user_id=$user";
        command.ExecuteNonQuery();
        transaction.Commit();
        return folder;
    }

    public void DeleteListFolder(string user, string id)
    {
        ValidateListId(user);
        ValidateListId(id);
        using var db = Open();
        // SET NULL unfiles direct lists and promotes immediate children without touching list membership.
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

    private string ListMembershipFilter(SqliteCommand command, NativeList list)
    {
        if (list.Kind == "manual")
        {
            command.Parameters.AddWithValue("$list", list.Id);
            return "EXISTS (SELECT 1 FROM list_books lb WHERE lb.list_id=$list AND lb.book_id=b.id)";
        }
        ValidateSmartRules(list.Match, list.Rules);
        return SmartListFilter(command, list.Match, list.Rules);
    }

    private string SmartListFilter(SqliteCommand command, string match, ListRule[] listRules)
    {
        var parameterPrefix = "$rule" + command.Parameters.Count + "_";
        var parameterIndex = 0;
        return GroupFilter(match, listRules);
        string GroupFilter(string mode, ListRule[] children) =>
            "(" + string.Join(mode == "all" ? " AND " : " OR ", children.Select(RuleFilter)) + ")";
        string RuleFilter(ListRule rule)
        {
            if (rule.Rules is not null) return GroupFilter(rule.Match!, rule.Rules);
            var field = ListRuleSchema[rule.Field!];
            if (rule.Field == "FileType")
                command.Connection!.CreateFunction<string, string>("rule_file_type", static path => Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), isDeterministic: true);
            if (rule.Field == "HasMissingIssues")
                command.Connection!.CreateFunction<string, int?>("rule_whole_issue", WholeIssueNumber, isDeterministic: true);
            var parameter = parameterPrefix + parameterIndex++;
            if (rule.Field == "ManualList")
            {
                var membership = "EXISTS (SELECT 1 FROM list_books mlb JOIN lists ml ON ml.id=mlb.list_id"
                    + " WHERE ml.user_id=$user AND ml.kind='manual' AND mlb.book_id=b.id";
                if (rule.Operator is not ("isEmpty" or "isNotEmpty"))
                {
                    command.Parameters.AddWithValue(parameter, rule.Value!);
                    membership += $" AND ml.id={parameter}";
                }
                return (rule.Operator is "notEquals" or "isEmpty" ? "NOT " : "") + membership + ")";
            }
            string? seriesColumn = null;
            if (rule.Field is "SeriesReadStatus" or "HasMissingIssues")
            {
                var archived = LocationFilter(command, "archived", parameter + "Archived");
                var unsorted = LocationFilter(command, "unsorted", parameter + "Unsorted");
                var seriesWhere = "s.series=b.series AND trim(b.series)<>'' AND (" + archived + ")=("
                    + archived.Replace("b.path", "s.path", StringComparison.Ordinal) + ") AND (" + unsorted + ")=("
                    + unsorted.Replace("b.path", "s.path", StringComparison.Ordinal) + ")";
                seriesColumn = rule.Field == "SeriesReadStatus"
                    ? "(SELECT CASE WHEN COUNT(*)=0 THEN NULL WHEN COUNT(sp.book_id)=0 THEN 'unread'"
                        + " WHEN SUM(COALESCE(sp.completed,0))=COUNT(*) THEN 'completed' ELSE 'in-progress' END"
                        + " FROM books s LEFT JOIN progress sp ON sp.book_id=s.id AND sp.user_id=$user WHERE " + seriesWhere + ")"
                    : "(SELECT CASE WHEN MAX(rule_whole_issue(s.number))-MIN(rule_whole_issue(s.number))+1"
                        + ">COUNT(DISTINCT rule_whole_issue(s.number)) THEN 'yes' ELSE 'no' END FROM books s WHERE " + seriesWhere + ")";
            }
            var column = rule.Field switch
            {
                "Title" => "b.title", "Series" => "b.series", "Number" => "b.number", "Publisher" => "b.publisher",
                "Summary" => "b.summary", "PageCount" => "b.page_count", "Writer" => "b.writer",
                "Penciller" => "b.penciller", "Genre" => "b.genre", "Year" => "b.year", "PersonalRating" => "COALESCE(r.rating,0)",
                "DaysSinceAdded" => "max(0,(unixepoch()-b.added_at)/86400)",
                "DaysSinceRead" => "max(0,(unixepoch()-p.updated_at)/86400)",
                "AddedDate" => "date(b.added_at,'unixepoch')",
                "FileSizeMB" => "b.file_size/1048576.0", "FileType" => "rule_file_type(b.path)",
                "SeriesReadStatus" or "HasMissingIssues" => seriesColumn!,
                "ReadStatus" => "(CASE WHEN p.book_id IS NULL THEN 'unread' WHEN p.completed=1 THEN 'completed' ELSE 'in-progress' END)",
                "Location" => "b.location", "Folder" => "b.folder", "Availability" => "b.availability",
                _ => $"metadata_scalar(b.info_xml,'{field.Name}')"
            };
            if (rule.Operator is "isEmpty" or "isNotEmpty")
            {
                return field.Type is "integer" or "number" or "date"
                    ? $"{column} IS {(rule.Operator == "isEmpty" ? "" : "NOT ")}NULL"
                    : $"{column} {(rule.Operator == "isEmpty" ? "=" : "<>")} ''";
            }
            command.Parameters.AddWithValue(parameter, field.Type switch
            {
                "integer" => (object)int.Parse(rule.Value!, CultureInfo.InvariantCulture),
                "number" => double.Parse(rule.Value!, CultureInfo.InvariantCulture),
                _ => rule.Value!
            });
            if (rule.Field == "Number" && rule.Operator is "greaterThan" or "lessThan" or "greaterOrEqual" or "lessOrEqual")
            {
                command.Connection!.CreateCollation("ISSUE_VALUE", static (a, b) => NaturalCompare(a ?? "", b ?? "", ordinalTieBreak: false));
                column = "NULLIF(trim(" + column + "),'') COLLATE ISSUE_VALUE";
            }
            return rule.Operator switch
            {
                "contains" => $"instr(lower({column}),lower({parameter}))>0",
                "notContains" => $"instr(lower({column}),lower({parameter}))=0",
                "startsWith" => $"substr({column},1,length({parameter})) = {parameter} COLLATE NOCASE",
                "endsWith" => $"substr({column},length({column})-length({parameter})+1) = {parameter} COLLATE NOCASE",
                "equals" => $"{column} = {parameter} COLLATE NOCASE",
                "notEquals" => $"{column} <> {parameter} COLLATE NOCASE",
                "greaterThan" => $"{column} > {parameter}",
                "lessThan" => $"{column} < {parameter}",
                "greaterOrEqual" => $"{column} >= {parameter}",
                "lessOrEqual" => $"{column} <= {parameter}",
                _ => throw new ArgumentException("Unknown smart list operator.")
            };
        }
    }
}
