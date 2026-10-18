using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace ComicRack.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MetadataExportSelection([property: JsonRequired] string[] BookIds);
public sealed record MetadataExportBook(string Id, string Title, Dictionary<string, string> Fields,
    Dictionary<string, string> Custom, string ComicInfoXml, string? ComicBookXml);
public sealed record MetadataIssue(string Field, string Code, string? Value, string Message);
public sealed record MetadataReportBook(string Id, string Title, IReadOnlyList<MetadataIssue> Issues);
public sealed record MetadataReport(IReadOnlyList<MetadataReportBook> Items, int Total, int Page, int PageSize);

public sealed partial class LibraryStore
{
    private static readonly string[] ReportNameFields =
        ["Publisher", "Writer", "Penciller", "Inker", "Colorist", "Letterer", "CoverArtist", "Editor", "Translator"];

    public MetadataReport GetMetadataReport(string kind = "all", int page = 1, int pageSize = 48)
    {
        if (kind is not ("all" or "missing" or "dates" or "names") || page < 1 || pageSize is < 1 or > 100)
            throw new ArgumentException("Invalid metadata report kind or pagination.");
        var names = kind is "all" or "names";
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (kind is "all" or "missing") wanted.UnionWith(["Series", "Number"]);
        if (kind is "all" or "dates") wanted.UnionWith(["Year", "Month", "Day"]);
        if (names) wanted.UnionWith(ReportNameFields);
        var books = new List<(string Id, string Title, Dictionary<string, string> Fields)>();
        var variants = new Dictionary<(string Field, string Normalized), HashSet<string>>();
        using var db = Open();
        using var command = Command(db, "SELECT id,title,info_xml FROM books ORDER BY title COLLATE NOCASE,id");
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                // ponytail: on-demand catalog scan retains only relevant scalars, never every book's raw XML.
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                using var text = rows.GetTextReader(2);
                using var xml = XmlReader.Create(text, XmlSettings());
                xml.MoveToContent();
                var ns = xml.NamespaceURI;
                while (xml.Read())
                {
                    if (xml.NodeType != XmlNodeType.Element || xml.Depth != 1 || xml.NamespaceURI != ns
                        || !wanted.Contains(xml.LocalName)) continue;
                    var name = xml.LocalName;
                    using var subtree = xml.ReadSubtree();
                    fields.TryAdd(name, XElement.Load(subtree).Value);
                }
                books.Add((rows.GetString(0), rows.GetString(1), fields));
                if (!names) continue;
                foreach (var field in ReportNameFields)
                {
                    if (!fields.TryGetValue(field, out var value)) continue;
                    foreach (var name in NameValues(field, value))
                    {
                        var normalized = NormalizeName(name);
                        if (normalized.Length == 0) continue;
                        var key = (field, normalized);
                        if (!variants.TryGetValue(key, out var spellings)) variants[key] = spellings = new(StringComparer.Ordinal);
                        spellings.Add(name);
                    }
                }
            }
        }
        var items = new List<MetadataReportBook>();
        var total = 0;
        var offset = (long)(page - 1) * pageSize;
        foreach (var book in books)
        {
            var issues = new List<MetadataIssue>();
            if (kind is "all" or "missing")
                foreach (var field in new[] { "Series", "Number" })
                    if (!book.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
                        issues.Add(new(field, "missing", value, $"{field} is missing or blank."));
            if (kind is "all" or "dates") AddDateIssues(book.Fields, issues);
            if (names)
                foreach (var field in ReportNameFields)
                {
                    if (!book.Fields.TryGetValue(field, out var value)) continue;
                    if (NameValues(field, value).Any(name => variants.TryGetValue((field, NormalizeName(name)), out var spellings)
                        && spellings.Count > 1))
                        issues.Add(new(field, "name-variation", value,
                            "Potential case or whitespace variation in this field elsewhere in the catalog; review before changing."));
                }
            if (issues.Count == 0) continue;
            if (total >= offset && items.Count < pageSize) items.Add(new(book.Id, book.Title, issues));
            total++;
        }
        return new(items, total, page, pageSize);
    }

    private static IEnumerable<string> NameValues(string field, string value) =>
        field == "Publisher" ? [value] : value.Split(',');

    private static string NormalizeName(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool ImpossibleDate(int year, int month, int day) => month is >= 1 and <= 12
        && day > DateTime.DaysInMonth(year is >= 1 and <= 9999 ? year : 2000, month);

    private static void AddDateIssues(Dictionary<string, string> fields, List<MetadataIssue> issues)
    {
        int Part(string field, int max)
        {
            if (!fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value)) return -1;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                issues.Add(new(field, "date-malformed", value, $"{field} is not an integer."));
                return -1;
            }
            if (number == -1) return -1;
            if (number < 1 || number > max)
            {
                issues.Add(new(field, "date-out-of-range", value, $"{field} must be 1–{max}, or -1/blank for unknown."));
                return -1;
            }
            return number;
        }
        var year = Part("Year", 9999);
        var month = Part("Month", 12);
        var day = Part("Day", 31);
        if (ImpossibleDate(year, month, day))
            issues.Add(new("Day", "date-impossible", fields.GetValueOrDefault("Day"), "Day does not exist in this publication month/year."));
    }

    public IReadOnlyList<MetadataExportBook> GetMetadataExport(string[] bookIds)
    {
        var ids = MetadataSelection(bookIds);
        var books = new List<MetadataExportBook>(ids.Length);
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = Command(db, "SELECT title,info_xml,book_xml FROM books WHERE id=$id", ("$id", ""));
        command.Transaction = transaction;
        foreach (var id in ids)
        {
            command.Parameters["$id"].Value = id;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException();
            var xml = reader.GetString(1);
            var bookXml = reader.IsDBNull(2) ? null : reader.GetString(2);
            var metadata = ReadMetadata(ParseXml(xml, "ComicInfo").Root!, bookXml);
            books.Add(new(id, reader.GetString(0), metadata.Fields, metadata.Custom, xml, bookXml));
        }
        return books;
    }

    public static async Task WriteMetadataCsv(Stream output, IReadOnlyList<MetadataExportBook> books,
        CancellationToken cancellationToken = default)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        async Task Cell(string value, bool last = false)
        {
            // A leading apostrophe neutralizes formulas even behind whitespace/control prefixes in spreadsheet imports.
            var first = value.FirstOrDefault(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
            if (first is '=' or '+' or '-' or '@') value = "'" + value;
            await writer.WriteAsync(("\"" + value.Replace("\"", "\"\"") + "\"" + (last ? "\r\n" : ",")).AsMemory(), cancellationToken);
        }
        await Cell("Id");
        foreach (var field in MetadataFields) await Cell(field.Name);
        await Cell("CustomValues", last: true);
        foreach (var book in books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Cell(book.Id);
            foreach (var field in MetadataFields) await Cell(book.Fields.GetValueOrDefault(field.Name) ?? "");
            await Cell(JsonSerializer.Serialize(book.Custom), last: true);
        }
        await writer.FlushAsync(cancellationToken);
    }
}
