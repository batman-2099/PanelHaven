using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BulkFileMoveItem([property: JsonRequired] string BookId,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex,
    [property: JsonRequired] string Folder, [property: JsonRequired] string FileName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BulkFileMoveRequest([property: JsonRequired] BulkFileMoveItem[] Items);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BulkFileMoveApplyRequest([property: JsonRequired] BulkFileMoveItem[] Items,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] bool Confirmed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileTemplateRequest([property: JsonRequired] string[] BookIds,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int RootIndex,
    [property: JsonRequired] string FolderTemplate, [property: JsonRequired] string FileNameTemplate);
public sealed record BulkFileMoveRow(string BookId, string Source, string Destination, int RootIndex, string Folder,
    string FileName, string Revision, string SourceHash, bool CanApply, bool NoOp, IReadOnlyList<string> Conflicts);
public sealed record BulkFileMovePreview(string Revision, bool CanApply, IReadOnlyList<BulkFileMoveRow> Items);
public sealed record BulkFileMoveResult(IReadOnlyList<FileOperationResult> Completed, IReadOnlyList<string> Skipped,
    string? FailedBookId, string? Error, IReadOnlyList<string> NotAttempted);

public sealed partial class LibraryStore
{
    public BulkFileMovePreview PreviewFileMoves(BulkFileMoveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateFileMoveItems(request.Items);
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return PlanFileMoves(db, request.Items, null, cancellationToken);
        }
        finally { scanLock.Release(); }
    }

    public BulkFileMovePreview PreviewFileTemplates(FileTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BookIds is null || request.FolderTemplate is null || request.FileNameTemplate is null)
            throw new ArgumentException("Selection and both templates are required.");
        var items = request.BookIds.Select(id => new BulkFileMoveItem(id, request.RootIndex, "", "")).ToArray();
        ValidateFileMoveItems(items);
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            return PlanFileMoves(db, items, request, cancellationToken);
        }
        finally { scanLock.Release(); }
    }

    private static void ValidateFileMoveItems(BulkFileMoveItem[]? items)
    {
        if (items is null || items.Length is < 1 or > 100
            || items.Any(item => item is null || string.IsNullOrWhiteSpace(item.BookId) || item.Folder is null || item.FileName is null)
            || items.Select(item => item.BookId).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Select between 1 and 100 unique books with complete destinations.");
    }

    private BulkFileMovePreview PlanFileMoves(SqliteConnection db, BulkFileMoveItem[] items, FileTemplateRequest? template,
        CancellationToken cancellationToken)
    {
        var rows = new BulkFileMoveRow[items.Length];
        var destinations = new string?[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var source = "";
            var destination = "";
            try
            {
                var catalog = ReadArchiveCatalog(db, null, item.BookId);
                source = FileDisplayPath(catalog.Path);
                var metadata = ParseXml(catalog.Xml, "ComicInfo").Root!;
                if (template is not null)
                {
                    string Expand(string text) => ExpandFileTemplate(text, token => token == "FileName"
                        ? Path.GetFileNameWithoutExtension(catalog.Path)
                        : metadata.Element(metadata.Name.Namespace + token)?.Value);
                    var folder = Expand(template.FolderTemplate);
                    var stem = Expand(template.FileNameTemplate);
                    if (string.IsNullOrWhiteSpace(stem) || stem.EndsWith(' ') || stem.EndsWith('.') || stem is "." or "..")
                        throw new FormatException("Filename template must produce a nonempty safe filename stem.");
                    item = item with { Folder = folder, FileName = stem + Path.GetExtension(catalog.Path) };
                }
                ValidateFileOperationFolder(item.RootIndex, item.Folder);
                var publisher = IntakeComponent(metadata.Element(metadata.Name.Namespace + "Publisher")?.Value ?? "", "Unknown Publisher");
                var targetFolder = item.Folder.Length == 0
                    ? IntakeComponent(metadata.Element(metadata.Name.Namespace + "Series")?.Value ?? "", "Unknown Series") : item.Folder;
                item = item with { Folder = targetFolder.Equals(publisher, PathComparison) || targetFolder.StartsWith(publisher + "/", PathComparison)
                    ? targetFolder : publisher + "/" + targetFolder };
                destinations[index] = FileDestination(new(item.RootIndex, item.Folder, item.FileName));
                destination = FileDisplayPath(destinations[index]!);
                var plan = PlanFileMove(db, item.BookId, destinations[index]!, null, cancellationToken, allowNoOp: true, useRequestedFolder: true);
                if (catalog != plan.Catalog) throw new FileOperationConflictException();
                var preview = plan.Preview;
                rows[index] = new(item.BookId, preview.Source, preview.Destination, item.RootIndex, item.Folder, item.FileName,
                    preview.Revision, preview.SourceHash, preview.CanApply, plan.Catalog.Path.Equals(plan.Destination, PathComparison), preview.Conflicts);
            }
            catch (Exception exception) when (IsArchiveError(exception) || exception is FileOperationConflictException or KeyNotFoundException or FormatException)
            {
                rows[index] = new(item.BookId, source, destination, item.RootIndex, item.Folder, item.FileName,
                    "", "", false, false, [FileMoveError(exception)]);
            }
        }
        foreach (var group in Enumerable.Range(0, items.Length).Where(index => destinations[index] is not null)
            .GroupBy(index => PathKey(destinations[index]!), StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var index in group)
                rows[index] = rows[index] with { CanApply = false,
                    Conflicts = [.. rows[index].Conflicts, "Multiple selected books have the same destination; no files will be moved."] };
        return new(FileRevision(rows), rows.All(row => row.CanApply) && rows.Any(row => !row.NoOp), rows);
    }

    private static string ExpandFileTemplate(string template, Func<string, string?> valueFor)
    {
        if (template.Length > 4096) throw new FormatException("Template is too long.");
        var output = new StringBuilder(template.Length);
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '}') throw new FormatException("Malformed template token.");
            if (template[index] != '{') { output.Append(template[index]); continue; }
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw new FormatException("Malformed template token.");
            var token = template[(index + 1)..end];
            if (token is not ("Series" or "Year" or "Number" or "Title" or "Publisher" or "Volume" or "FileName"))
                throw new FormatException("Unknown or malformed template token. Use Series, Year, Number, Title, Publisher, Volume, or FileName.");
            var value = valueFor(token);
            if (string.IsNullOrWhiteSpace(value) || (token is "Year" or "Volume") && value == "-1")
                throw new FormatException($"Missing metadata for {{{token}}}.");
            if (token != "FileName") value = IntakeComponent(value, "");
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\')
                || value.Contains(':') || value.Any(char.IsControl) || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.'))
                throw new FormatException($"Unsafe value for {{{token}}}; edit metadata or use another template.");
            output.Append(value);
            if (output.Length > 4096) throw new FormatException("Expanded template is too long.");
            index = end;
        }
        return output.ToString();
    }

    private static string FileMoveError(Exception exception) => exception switch
    {
        FileOperationConflictException => exception.Message,
        FormatException => exception.Message,
        KeyNotFoundException => "Book no longer exists in the catalog.",
        OperationCanceledException => "Operation canceled. Review each book's file-operation history before trying again.",
        SqliteException => "Catalog update failed. Review each book's file-operation history and recover any pending operation before trying again.",
        ArgumentException => "Invalid destination: select a configured library, an existing relative folder, and a safe filename with the unchanged comicbook extension.",
        _ => "Comicbook or destination is unavailable, unsafe, or invalid. Review file-operation history for pending recovery."
    };

    public BulkFileMoveResult ApplyFileMoves(BulkFileMoveApplyRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !request.Confirmed) throw new ArgumentException("Explicit confirmation is required.");
        ValidateFileMoveItems(request.Items);
        if (string.IsNullOrEmpty(request.ExpectedRevision)) throw new FileOperationConflictException();
        scanLock.Wait(cancellationToken);
        try
        {
            using var db = Open();
            var preview = PlanFileMoves(db, request.Items, null, cancellationToken);
            if (!preview.CanApply || preview.Revision != request.ExpectedRevision) throw new FileOperationConflictException();
            var completed = new List<FileOperationResult>();
            var skipped = new List<string>();
            for (var index = 0; index < preview.Items.Count; index++)
            {
                var row = preview.Items[index];
                if (row.NoOp) { skipped.Add(row.BookId); continue; }
                // rowid distinguishes this attempt from earlier history, including undone or failed moves.
                var lastOperation = long.MaxValue;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var previous = Command(db, "SELECT COALESCE(MAX(rowid),0) FROM file_operations WHERE book_id=$id", ("$id", row.BookId));
                    lastOperation = (long)previous.ExecuteScalar()!;
                    completed.Add(ApplyFileMoveLocked(row.BookId, FileDestination(new(row.RootIndex, row.Folder, row.FileName)), null,
                        new(row.Revision, true), cancellationToken, useRequestedFolder: true));
                }
                catch (Exception exception) when (IsArchiveError(exception)
                    || exception is FileOperationConflictException or SqliteException or KeyNotFoundException or OperationCanceledException)
                {
                    // A failed final journal cleanup can follow a durable catalog commit. Report that move honestly.
                    try
                    {
                        using var committed = Command(db, "SELECT id FROM file_operations WHERE book_id=$id AND rowid>$previous AND committed=1 ORDER BY rowid DESC LIMIT 1",
                            ("$id", row.BookId), ("$previous", lastOperation));
                        if (committed.ExecuteScalar() is string operationId) completed.Add(new(row.BookId, operationId));
                    }
                    catch (SqliteException) { /* History remains authoritative if the database itself is unavailable. */ }
                    return new(completed, skipped, row.BookId, FileMoveError(exception), preview.Items.Skip(index + 1).Select(item => item.BookId).ToArray());
                }
            }
            return new(completed, skipped, null, null, []);
        }
        finally { scanLock.Release(); }
    }
}
