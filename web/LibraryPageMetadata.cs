using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PageMetadataRow([property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Page,
    [property: JsonRequired] string? Type, [property: JsonRequired] string? Bookmark);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PageMetadataEdit([property: JsonRequired] string ExpectedRevision,
    [property: JsonRequired] PageMetadataRow[] Pages);
public sealed record BookPageMetadata(string BookId, string Revision, int PageCount,
    IReadOnlyList<PageMetadataRow> Pages, IReadOnlyList<string> Types);
public sealed record PageMetadataChange(int Page, string Field, string? Before, string? After);
public sealed record PageMetadataPreview(string BookId, string Revision,
    IReadOnlyList<PageMetadataChange> Changes, bool CanApply);

public sealed partial class LibraryStore
{
    private static readonly string[] EditablePageTypes =
    [
        "FrontCover", "InnerCover", "Roundup", "Story", "Advertisement", "Advertisment", "Editorial",
        "Letters", "Preview", "BackCover", "Other", "Deleted", "All", "AllWithDeleted"
    ];
    private sealed record PageCatalog(string Id, int Count, string Xml, string? BookXml);
    private sealed record PageEditIntent(int Page, int Fields);

    private static PageCatalog ReadPageCatalog(SqliteConnection db, SqliteTransaction? transaction, string id)
    {
        using var command = Command(db, "SELECT page_count,info_xml,book_xml FROM books WHERE id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return new(id, reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static string PageRevision(PageCatalog catalog, PageMetadataRow[]? draft) =>
        FileRevision(new { catalog, draft });

    // Only elements in the document's established namespace are ComicInfo structures; extensions survive.
    private static Dictionary<int, XElement> EditablePages(XElement root, int count)
    {
        var ns = root.Name.Namespace;
        var containers = root.Elements(ns + "Pages").ToArray();
        if (containers.Length > 1) throw new ArgumentException("Duplicate Pages structures cannot be edited safely.");
        var pages = new Dictionary<int, XElement>();
        if (containers.Length == 0) return pages;
        foreach (var page in containers[0].Elements(ns + "Page"))
        {
            if (!int.TryParse(page.Attribute("Image")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= count || !pages.TryAdd(index, page)
                || page.Elements(ns + "Image").Any())
                throw new ArgumentException("Page metadata contains missing, invalid, or duplicate Image indices.");
        }
        return pages;
    }

    private static XElement EnsureMetadataPage(XElement root, Dictionary<int, XElement> pages, int index)
    {
        if (pages.TryGetValue(index, out var page)) return page;
        var ns = root.Name.Namespace;
        var container = root.Element(ns + "Pages");
        if (container is null) { container = new XElement(ns + "Pages"); root.Add(container); }
        page = new XElement(ns + "Page", new XAttribute("Image", index));
        container.Add(page);
        pages.Add(index, page);
        return page;
    }

    public BookPageMetadata GetPageMetadata(string id)
    {
        using var db = Open();
        var catalog = ReadPageCatalog(db, null, id);
        var entries = EditablePages(ParseXml(catalog.Xml, "ComicInfo").Root!, catalog.Count);
        var pages = new PageMetadataRow[catalog.Count];
        for (var index = 0; index < pages.Length; index++)
        {
            entries.TryGetValue(index, out var page);
            pages[index] = new(index, page?.Attribute("Type")?.Value, page?.Attribute("Bookmark")?.Value);
        }
        return new(id, PageRevision(catalog, null), catalog.Count, pages, EditablePageTypes);
    }

    private static (string Xml, PageMetadataPreview Preview) StagePageMetadata(PageCatalog catalog,
        PageMetadataEdit edit, bool apply)
    {
        if (edit is null || edit.Pages is null || edit.Pages.Length > catalog.Count)
            throw new ArgumentException("Select existing pages only.");
        var revision = PageRevision(catalog, edit.Pages);
        if (string.IsNullOrEmpty(edit.ExpectedRevision)
            || edit.ExpectedRevision != (apply ? revision : PageRevision(catalog, null)))
            throw new MetadataRevisionException();
        var document = ParseXml(catalog.Xml, "ComicInfo");
        var pages = EditablePages(document.Root!, catalog.Count);
        var seen = new HashSet<int>();
        var changes = new List<PageMetadataChange>();
        foreach (var row in edit.Pages)
        {
            if (row is null || row.Page < 0 || row.Page >= catalog.Count || !seen.Add(row.Page))
                throw new ArgumentException("Page indices must be unique and within the physical page count.");
            pages.TryGetValue(row.Page, out var page);
            var type = string.IsNullOrEmpty(row.Type) ? null : row.Type;
            var bookmark = string.IsNullOrEmpty(row.Bookmark) ? null : row.Bookmark;
            // Existing legacy values may be retained while editing another attribute, but never introduced.
            if (type is not null && type != page?.Attribute("Type")?.Value && !EditablePageTypes.Contains(type, StringComparer.Ordinal))
                throw new ArgumentException("Invalid page type.");
            if (bookmark != page?.Attribute("Bookmark")?.Value)
            {
                if (bookmark is not null && (bookmark.Length > 200 || bookmark.Any(char.IsControl)))
                    throw new ArgumentException("Embedded bookmarks allow at most 200 characters and no control characters.");
                ValidateMetadataText(bookmark);
            }
            void Patch(string field, string? value)
            {
                var before = page?.Attribute(field)?.Value;
                if (before == value) return;
                page ??= EnsureMetadataPage(document.Root!, pages, row.Page);
                page.SetAttributeValue(field, value);
                changes.Add(new(row.Page, field, before, value));
            }
            Patch("Type", type);
            Patch("Bookmark", bookmark);
        }
        return (changes.Count == 0 ? catalog.Xml : SerializeMetadata(document),
            new(catalog.Id, revision, changes, changes.Count != 0));
    }

    public PageMetadataPreview PreviewPageMetadata(string id, PageMetadataEdit edit)
    {
        using var db = Open();
        return StagePageMetadata(ReadPageCatalog(db, null, id), edit, false).Preview;
    }

    public BookPageMetadata EditPageMetadata(string id, PageMetadataEdit edit)
    {
        using var db = Open();
        using (var transaction = db.BeginTransaction(deferred: false))
        {
            RefusePendingFileOperation(db, id, transaction);
            var staged = StagePageMetadata(ReadPageCatalog(db, transaction, id), edit, true);
            if (staged.Preview.CanApply)
            {
                using var update = Command(db, "UPDATE books SET info_xml=$xml,edited=1 WHERE id=$id",
                    ("$xml", staged.Xml), ("$id", id));
                update.Transaction = transaction;
                update.ExecuteNonQuery();
                // Durable per-attribute intent, not a stale full-page overlay; undo deliberately retains it.
                using var intent = Command(db, """
                    INSERT INTO page_metadata_edits(book_id,page,fields) VALUES($id,$page,$fields)
                    ON CONFLICT(book_id,page) DO UPDATE SET fields=fields | excluded.fields
                    """, ("$id", id), ("$page", 0), ("$fields", 0));
                intent.Transaction = transaction;
                foreach (var change in staged.Preview.Changes)
                {
                    intent.Parameters["$page"].Value = change.Page;
                    intent.Parameters["$fields"].Value = change.Field == "Type" ? 1 : 2;
                    intent.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        return GetPageMetadata(id);
    }

    private static string ReadPageEditIntents(SqliteConnection db, SqliteTransaction? transaction, string id)
    {
        using var command = Command(db, "SELECT page,fields FROM page_metadata_edits WHERE book_id=$id ORDER BY page", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var intents = new List<PageEditIntent>();
        while (reader.Read()) intents.Add(new(reader.GetInt32(0), reader.GetInt32(1)));
        return JsonSerializer.Serialize(intents);
    }

    private static void MergeEditedPages(XElement source, XElement catalog, string intentJson, int catalogCount, int sourceCount)
    {
        var intents = JsonSerializer.Deserialize<PageEditIntent[]>(intentJson)!;
        if (intents.Length == 0) return;
        if (sourceCount != catalogCount) throw new ArchiveWriteConflictException();
        var desired = EditablePages(catalog, catalogCount);
        var target = EditablePages(source, sourceCount);
        foreach (var intent in intents)
        {
            if (intent.Page < 0 || intent.Page >= sourceCount) throw new ArchiveWriteConflictException();
            desired.TryGetValue(intent.Page, out var page);
            target.TryGetValue(intent.Page, out var destination);
            void Patch(string field, int flag)
            {
                if ((intent.Fields & flag) == 0) return;
                var value = page?.Attribute(field)?.Value;
                if (destination?.Attribute(field)?.Value == value) return;
                destination ??= EnsureMetadataPage(source, target, intent.Page);
                destination.SetAttributeValue(field, value);
            }
            Patch("Type", 1);
            Patch("Bookmark", 2);
        }
    }
}
