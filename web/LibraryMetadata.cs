using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record MetadataEdit(Dictionary<string, string?>? Fields = null, Dictionary<string, string?>? Custom = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BulkMetadataEdit([property: JsonRequired] string[] BookIds,
    Dictionary<string, string?>? Fields = null, Dictionary<string, string?>? Custom = null, string? ExpectedRevision = null);
public sealed record MetadataField(string Name, string Label, string Type, string[]? Options = null,
    double? Min = null, double? Max = null);
public sealed record BookMetadata(Dictionary<string, string> Fields, Dictionary<string, string> Custom);
public sealed record MetadataChange(string Field, bool Custom, string? Before, string? After);
public sealed record MetadataPreviewBook(string Id, string Title, IReadOnlyList<MetadataChange> Changes);
public sealed record MetadataPreview(string Revision, IReadOnlyList<MetadataPreviewBook> Books);
public sealed class MetadataRevisionException() : Exception("Metadata changed or the preview is missing. Preview the changes again before applying.") { }

public sealed partial class LibraryStore
{
    public static readonly MetadataField[] MetadataFields =
    [
        new("Title", "Title", "text"), new("Series", "Series", "text"), new("Number", "Number", "text"),
        new("Count", "Count", "integer", Min: -1, Max: int.MaxValue),
        new("Volume", "Volume", "integer", Min: -1, Max: int.MaxValue),
        new("AlternateSeries", "Alternate series", "text"), new("AlternateNumber", "Alternate number", "text"),
        new("AlternateCount", "Alternate count", "integer", Min: -1, Max: int.MaxValue),
        new("StoryArc", "Story arc", "text"), new("SeriesGroup", "Series group", "text"),
        new("Summary", "Summary", "textarea"), new("Notes", "Notes", "textarea"), new("Review", "Review", "textarea"),
        new("Year", "Year (-1 unknown)", "integer", Min: -1, Max: 9999),
        new("Month", "Month (-1 unknown)", "integer", Min: -1, Max: 12),
        new("Day", "Day (-1 unknown)", "integer", Min: -1, Max: 31),
        new("Writer", "Writer", "text"), new("Penciller", "Penciller", "text"), new("Inker", "Inker", "text"),
        new("Colorist", "Colorist", "text"), new("Letterer", "Letterer", "text"),
        new("CoverArtist", "Cover artist", "text"), new("Editor", "Editor", "text"), new("Translator", "Translator", "text"),
        new("Publisher", "Publisher", "text"), new("Imprint", "Imprint", "text"), new("Genre", "Genre", "text"),
        new("Web", "Web", "text"), new("PageCount", "Declared page count (reader uses comicbook)", "integer", Min: 0, Max: int.MaxValue),
        new("LanguageISO", "Language ISO", "text"), new("Format", "Format", "text"), new("AgeRating", "Age rating", "text"),
        new("BlackAndWhite", "Black and white", "enum", ["Unknown", "No", "Yes"]),
        new("Manga", "Manga", "enum", ["Unknown", "No", "Yes", "YesAndRightToLeft"]),
        new("PreferredFrontCover", "Preferred front cover", "integer", Min: 0, Max: int.MaxValue),
        new("Characters", "Characters", "text"), new("Teams", "Teams", "text"),
        new("MainCharacterOrTeam", "Main character or team", "text"), new("Locations", "Locations", "text"),
        new("CommunityRating", "Community rating", "number", Min: 0, Max: 5),
        new("ScanInformation", "Scan information", "textarea"), new("Tags", "Tags", "text")
    ];

    private static void InitializeMetadata(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var command = Command(db, "SELECT name FROM pragma_table_info('books')");
        command.Transaction = transaction;
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(0));
        var backfill = false;
        foreach (var (name, definition) in new[]
        {
            ("writer", "TEXT NOT NULL DEFAULT ''"), ("penciller", "TEXT NOT NULL DEFAULT ''"),
            ("genre", "TEXT NOT NULL DEFAULT ''"),
            ("year", "INTEGER CHECK(year IS NULL OR (typeof(year)='integer' AND year BETWEEN 0 AND 9999))")
        })
        {
            if (columns.Contains(name)) continue;
            command.CommandText = $"ALTER TABLE books ADD COLUMN {name} {definition}";
            command.ExecuteNonQuery();
            backfill = true;
        }
        if (backfill)
        {
            // ponytail: one migration pass projects four scalars; smart queries never parse XML.
            command.CommandText = "SELECT id,info_xml FROM books";
            using var reader = command.ExecuteReader();
            using var update = Command(db, "UPDATE books SET writer=$writer,penciller=$penciller,genre=$genre,year=$year WHERE id=$id",
                ("$id", ""), ("$writer", ""), ("$penciller", ""), ("$genre", ""), ("$year", null));
            update.Transaction = transaction;
            while (reader.Read())
            {
                var root = ParseXml(reader.GetString(1), "ComicInfo").Root!;
                string Field(string name) => root.Element(root.Name.Namespace + name)?.Value ?? "";
                update.Parameters["$id"].Value = reader.GetString(0);
                update.Parameters["$writer"].Value = Field("Writer");
                update.Parameters["$penciller"].Value = Field("Penciller");
                update.Parameters["$genre"].Value = Field("Genre");
                update.Parameters["$year"].Value = (object?)MetadataYear(Field("Year")) ?? DBNull.Value;
                update.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static int? MetadataYear(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 0 and <= 9999
            ? year : null;

    public BookMetadata GetMetadata(string id)
    {
        using var db = Open();
        using var command = Command(db, "SELECT info_xml,book_xml FROM books WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException();
        return ReadMetadata(ParseXml(reader.GetString(0), "ComicInfo").Root!,
            reader.IsDBNull(1) ? null : ParseXml(reader.GetString(1), "ComicBook").Root);
    }

    private static BookMetadata ReadMetadata(XElement root, XElement? book)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in MetadataFields)
            if (root.Element(root.Name.Namespace + field.Name) is { } element) fields[field.Name] = element.Value;
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (book is not null)
        {
            foreach (var token in (book.Element(book.Name.Namespace + "CustomValuesStore")?.Value ?? "").Split(','))
            {
                var pair = token.Split('=');
                if (pair.Length == 2) custom.TryAdd(DecodeCustom(pair[0]), DecodeCustom(pair[1]));
            }
        }
        return new(fields, custom);
    }

    private static void MergeEmbeddedMetadata(XElement info, XElement? book, XElement desiredInfo, XElement? desiredBook,
        string pageEdits, int catalogCount, int sourceCount)
    {
        foreach (var field in MetadataFields)
            PatchScalar(info, field.Name, desiredInfo.Element(desiredInfo.Name.Namespace + field.Name)?.Value);
        // Only edited page attributes are authoritative; untouched source pages and extensions survive.
        MergeEditedPages(info, desiredInfo, pageEdits, catalogCount, sourceCount);
        if (book is null) return;
        foreach (var field in MetadataFields)
            if (book.Element(book.Name.Namespace + field.Name) is not null)
                PatchScalar(book, field.Name, desiredInfo.Element(desiredInfo.Name.Namespace + field.Name)?.Value);
        PatchScalar(book, "CustomValuesStore", desiredBook?.Element(desiredBook.Name.Namespace + "CustomValuesStore")?.Value);
    }

    public void EditMetadata(string id, MetadataEdit edit) =>
        EditMetadataBulk(new([id], edit.Fields, edit.Custom));

    private sealed record MetadataUpdate(string Id, string Title, XElement Root, string Xml, string? BookXml,
        MetadataPreviewBook Preview);

    public MetadataPreview PreviewMetadata(BulkMetadataEdit edit)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var updates = StageMetadata(db, transaction, edit, out var revision);
        return new(revision, updates.Select(update => update.Preview).ToArray());
    }

    public void EditMetadataBulk(BulkMetadataEdit edit, bool requirePreview = false)
    {
        if (requirePreview && string.IsNullOrEmpty(edit.ExpectedRevision)) throw new MetadataRevisionException();
        using var db = Open();
        // SQLite's immediate transaction holds the write reservation through revision checking and apply.
        using var transaction = db.BeginTransaction(deferred: false);
        var updates = StageMetadata(db, transaction, edit, out _, requirePreview);
        foreach (var update in updates)
        {
            if (update.Preview.Changes.Count == 0) continue;
            string Field(string name) => update.Root.Element(update.Root.Name.Namespace + name)?.Value ?? "";
            using var command = Command(db, """
                UPDATE books SET title=$title,series=$series,number=$number,publisher=$publisher,summary=$summary,
                    info_xml=$xml,book_xml=$book,writer=$writer,penciller=$penciller,genre=$genre,year=$year,edited=1 WHERE id=$id
                """, ("$id", update.Id), ("$title", update.Title), ("$series", Field("Series")),
                ("$number", Field("Number")), ("$publisher", Field("Publisher")), ("$summary", Field("Summary")),
                ("$xml", update.Xml), ("$book", update.BookXml), ("$writer", Field("Writer")),
                ("$penciller", Field("Penciller")), ("$genre", Field("Genre")), ("$year", MetadataYear(Field("Year"))));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string[] MetadataSelection(string[]? ids)
    {
        if (ids is null || ids.Length is < 1 or > 100 || ids.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Select between 1 and 100 books.");
        return ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static List<MetadataUpdate> StageMetadata(SqliteConnection db, SqliteTransaction transaction,
        BulkMetadataEdit edit, out string revision, bool requirePreview = false)
    {
        var ids = MetadataSelection(edit.BookIds);
        var fields = edit.Fields ?? [];
        var custom = edit.Custom ?? [];
        if (fields.Count == 0 && custom.Count == 0) throw new ArgumentException("Select at least one metadata change.");
        foreach (var (name, value) in fields)
        {
            var field = MetadataFields.FirstOrDefault(f => f.Name == name)
                ?? throw new ArgumentException($"Unknown metadata field: {name}.");
            ValidateMetadataText(value);
            if (string.IsNullOrEmpty(value)) continue;
            if (field.Type == "enum" && !field.Options!.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException($"Invalid {name} value.");
            if (field.Type is "integer" or "number")
            {
                if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)
                    || number < field.Min || number > field.Max
                    || (field.Type == "integer" && !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    || (name is "Year" or "Month" or "Day" && number == 0))
                    throw new ArgumentException($"Invalid {name} value.");
            }
        }
        var customNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in custom)
        {
            ValidateMetadataText(name);
            ValidateMetadataText(value);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)
                || !customNames.Add(name) || name.Contains("&#61;") || name.Contains("&#44;")
                || (value is not null && (value.Contains("&#61;") || value.Contains("&#44;"))))
                throw new ArgumentException("Invalid or duplicate custom name, or ambiguous legacy escape sequence.");
        }
        using var hash = SHA256.Create();
        using var hashing = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using var binding = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true);
        void Bind(string? value)
        {
            binding.Write(value is not null);
            if (value is not null) binding.Write(value);
        }
        foreach (var patch in new[] { fields, custom })
        {
            binding.Write(patch.Count);
            foreach (var (name, value) in patch.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Bind(name);
                Bind(value);
            }
        }
        binding.Write(ids.Length);
        var originals = new List<(string Id, string Path, string Title, string Xml, string? BookXml)>(ids.Length);
        foreach (var id in ids)
        {
            RefusePendingFileOperation(db, id, transaction);
            string path, xml, originalTitle;
            string? bookXml;
            using (var command = Command(db, "SELECT path,info_xml,book_xml,title FROM books WHERE id=$id", ("$id", id)))
            {
                command.Transaction = transaction;
                using var reader = command.ExecuteReader();
                if (!reader.Read()) throw new KeyNotFoundException();
                path = reader.GetString(0);
                xml = reader.GetString(1);
                bookXml = reader.IsDBNull(2) ? null : reader.GetString(2);
                originalTitle = reader.GetString(3);
            }
            Bind(id);
            Bind(path);
            Bind(originalTitle);
            Bind(xml);
            Bind(bookXml);
            originals.Add((id, path, originalTitle, xml, bookXml));
        }
        binding.Flush();
        hashing.FlushFinalBlock();
        revision = Convert.ToHexString(hash.Hash!);
        if (requirePreview && !string.Equals(edit.ExpectedRevision, revision, StringComparison.Ordinal))
            throw new MetadataRevisionException();
        // ponytail: at most 100 edits; stage every result before writing, no partial bulk updates.
        var updates = new List<MetadataUpdate>(ids.Length);
        foreach (var (id, path, originalTitle, xml, originalBookXml) in originals)
        {
            var bookXml = originalBookXml;
            var info = ParseXml(xml, "ComicInfo");
            var root = info.Root!;
            var book = bookXml is null ? null : ParseXml(bookXml, "ComicBook");
            var before = ReadMetadata(root, book?.Root);
            var changes = new List<MetadataChange>();
            foreach (var (name, value) in fields)
            {
                before.Fields.TryGetValue(name, out var old);
                // Validate scalar shape even for an apparently unchanged value.
                PatchScalar(root, name, value);
                if (old != value) changes.Add(new(name, false, old, value));
            }
            if (fields.Keys.Any(name => name is "Year" or "Month" or "Day"))
            {
                int DatePart(string name) => int.TryParse(root.Element(root.Name.Namespace + name)?.Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
                var year = DatePart("Year");
                var month = DatePart("Month");
                var day = DatePart("Day");
                if (ImpossibleDate(year, month, day))
                    throw new ArgumentException("Invalid publication date.");
            }
            foreach (var (name, value) in custom)
            {
                before.Custom.TryGetValue(name, out var old);
                if (old != value) changes.Add(new(name, true, old, value));
            }
            if (changes.Any(change => change.Custom))
            {
                book ??= new XDocument(new XElement("ComicBook"));
                var bookRoot = book.Root!;
                var tokens = (bookRoot.Element(bookRoot.Name.Namespace + "CustomValuesStore")?.Value ?? "").Split(',').ToList();
                foreach (var change in changes.Where(change => change.Custom))
                {
                    var name = before.Custom.Keys.FirstOrDefault(key => key.Equals(change.Field, StringComparison.OrdinalIgnoreCase))
                        ?? change.Field;
                    var value = change.After;
                    tokens.RemoveAll(token => token.Split('=') is { Length: 2 } pair
                        && DecodeCustom(pair[0]).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (value is not null) tokens.Add(EncodeCustom(name) + "=" + EncodeCustom(value));
                }
                PatchScalar(bookRoot, "CustomValuesStore", string.Join(',', tokens));
                bookXml = SerializeMetadata(book);
            }
            var title = root.Element(root.Name.Namespace + "Title")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
            updates.Add(new(id, title, root, changes.Count == 0 ? xml : SerializeMetadata(info), bookXml,
                new(id, originalTitle, changes)));
        }
        return updates;
    }

    private static void ValidateMetadataText(string? value)
    {
        if (value is null) return;
        if (value.Length > 16_384) throw new ArgumentException("Metadata values cannot exceed 16384 characters.");
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException) { throw new ArgumentException("Metadata contains invalid XML characters."); }
    }

    private static void PatchScalar(XElement root, string name, string? value)
    {
        var elements = root.Elements(root.Name.Namespace + name).ToArray();
        if (elements.Length > 1 || elements.Any(e => e.HasElements))
            throw new ArgumentException($"Cannot edit non-scalar or duplicate {name} metadata without losing data.");
        if (elements.FirstOrDefault()?.Value == value) return;
        if (value is null) elements.FirstOrDefault()?.Remove();
        else if (elements.Length == 1)
        {
            elements[0].Nodes().OfType<XText>().Remove();
            elements[0].AddFirst(new XText(value));
        }
        else root.Add(new XElement(root.Name.Namespace + name, value));
    }

    private static string SerializeMetadata(XDocument document)
    {
        var xml = document.ToString(SaveOptions.DisableFormatting);
        if (Encoding.UTF8.GetByteCount(xml) > MaxXmlBytes) throw new ArgumentException("Metadata exceeds the 2 MiB limit.");
        return xml;
    }

    private static string DecodeCustom(string value) => value.Replace("&#61;", "=").Replace("&#44;", ",");
    private static string EncodeCustom(string value) => value.Replace("=", "&#61;").Replace(",", "&#44;");
}
