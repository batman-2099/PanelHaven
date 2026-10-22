using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;

namespace PanelHaven.Web;
public sealed record ListBooksSelection([property: JsonRequired] string[] BookIds);
public sealed record ListMove([property: JsonRequired] string BookId, [property: JsonRequired] string? BeforeId);


public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ComicVineException exception) { return Error(exception.StatusCode, exception.Message); }
            catch (KeyNotFoundException) { return Error(404, "Book, list, folder, or bookmark not found."); }
            catch (FileNotFoundException) { return Error(404, "Source comicbook not found."); }
            catch (DirectoryNotFoundException) { return Error(404, "Source comicbook not found."); }
            catch (MetadataRevisionException exception) { return Error(409, exception.Message); }
            catch (ArchiveWriteConflictException exception) { return Error(409, exception.Message); }
            catch (FileOperationConflictException exception) { return Error(409, exception.Message); }
            catch (ArgumentException) { return Error(400, "Invalid page, bookmark, search, sort, filter, metadata, or file-operation fields."); }
            catch (XmlException) { return Error(400, "Metadata contains invalid XML characters."); }
            catch (InvalidOperationException) { return Error(409, "Comicbook changed or is unavailable; ask an administrator to rescan."); }
            catch (Exception ex) when (LibraryStore.IsArchiveError(ex))
            {
                return Error(422, "Comicbook is unavailable, unsafe, or invalid.");
            }
            catch (SqliteException)
            {
                return Error(503, "Library database is unavailable. Try again later.");
            }
        });
        var comicVine = api.MapGroup("/comic-vine").RequireAuthorization(policy => policy.RequireRole("admin"));
        comicVine.MapGet("/status", (ComicVine provider) => Results.Ok(new { configured = provider.Configured }));
        comicVine.MapGet("/volumes", async (ComicVine provider, string? q, int? offset, CancellationToken cancellationToken) =>
            Results.Ok(await provider.VolumesAsync(q, offset ?? 0, cancellationToken)));
        comicVine.MapGet("/issues", async (ComicVine provider, int volumeId, string? number, int? offset, CancellationToken cancellationToken) =>
            Results.Ok(await provider.IssuesAsync(volumeId, number, offset ?? 0, cancellationToken)));
        comicVine.MapGet("/issues/{id}/metadata", async (ComicVine provider, int id, int volumeId, CancellationToken cancellationToken) =>
            Results.Ok(await provider.MetadataAsync(id, volumeId, cancellationToken)));
        api.MapGet("/reader-preferences", (Accounts accounts, ClaimsPrincipal user) =>
            Results.Ok(accounts.GetReaderPreferences(UserId(user))));
        api.MapPut("/reader-preferences", async (Accounts accounts, ClaimsPrincipal user, HttpRequest request) =>
        {
            if (!request.HasJsonContentType()) return Error(400, "Reader preferences must be a JSON object.");
            try
            {
                var preferences = await request.ReadFromJsonAsync<ReaderPreferences>(request.HttpContext.RequestAborted);
                accounts.SaveReaderPreferences(UserId(user), preferences);
                return Results.NoContent();
            }
            catch (JsonException)
            {
                return Error(400, "Reader preferences require mode, fit, integer zoom, boolean rtl, boolean coverAlone, integer rotation, integer brightness, integer contrast and integer skipPageTypes.");
            }
            catch (ArgumentException exception) { return Error(400, exception.Message); }
        });
        api.MapGet("/books", (LibraryStore store, ClaimsPrincipal user, string? q, int? page, int? pageSize,
            string? sort, string? direction, string? status, string? publisher, string? listId,
            string? availability, string? groupBy, string? groupKey, int? rating, string? location,
            string? sectionBy, string? sectionKey, string? filter) =>
            Results.Ok(store.ListBooks(UserId(user), q, page ?? 1, pageSize ?? 48, sort ?? "series",
                direction ?? "asc", status ?? "all", publisher, listId, availability ?? "all", groupBy ?? "none",
                groupKey, rating, location ?? "library", sectionBy ?? "none", sectionKey, LibraryStore.ParseCatalogFilter(filter))));
        api.MapGet("/book-filters", (LibraryStore store, string? location) => Results.Ok(new { publishers = store.ListPublishers(location ?? "library") }));
        api.MapGet("/book-groups", (LibraryStore store, ClaimsPrincipal user, string? q, string groupBy,
            int? page, int? pageSize, string? status, string? publisher, string? listId, string? availability,
            int? rating, string? location, string? sectionBy, string? sort, string? direction, string? sectionKey, string? filter) =>
            Results.Ok(store.ListGroups(UserId(user), q, groupBy, page ?? 1, pageSize ?? 48,
                status ?? "all", publisher, listId, availability ?? "all", rating, location ?? "library",
                sectionBy ?? "none", sort, direction ?? "asc", sectionKey, LibraryStore.ParseCatalogFilter(filter))));
        api.MapGet("/series-shelf", (LibraryStore store, ClaimsPrincipal user, string kind, int? page, int? pageSize) =>
            Results.Ok(store.ListSeriesShelf(UserId(user), kind, page ?? 1, pageSize ?? 12)));
        api.MapGet("/maintenance", (LibraryStore store, string kind, int? page, int? pageSize) =>
        {
            if (kind == "missing-issues") return Results.Ok((object)store.ListMissingIssues(page ?? 1, pageSize ?? 48));
            return Results.Ok((object)store.ListMaintenance(kind, page ?? 1, pageSize ?? 48));
        })
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/duplicates/{id}", (LibraryStore store, ClaimsPrincipal user, string id, int? page, int? pageSize) =>
            Results.Ok(store.CompareDuplicates(UserId(user), id, page ?? 1, pageSize ?? 3)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapDelete("/books/{id}", (LibraryStore store, string id) =>
        {
            store.RemoveMissingBook(id);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/delete-preview", (LibraryStore store, BookDeletePreviewRequest request, CancellationToken cancellationToken) =>
            Results.Ok(store.PreviewBookDeletion(request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/delete", (LibraryStore store, BookDeleteRequest request, CancellationToken cancellationToken) =>
            Results.Ok(store.DeleteBooks(request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/thumbnail", async (ThumbnailStore thumbnails, string id, CancellationToken cancellationToken) =>
            Results.Bytes(await thumbnails.GetAsync(id, cancellationToken), "image/jpeg"));
        api.MapGet("/books/{id}", (LibraryStore store, ClaimsPrincipal user, string id) =>
            Results.Ok(store.GetBook(UserId(user), id)));
        api.MapGet("/books/{id}/next-issue", (LibraryStore store, string id) =>
            Results.Ok(new { id = store.GetNextIssue(id) }));
        api.MapGet("/books/{id}/intake", (LibraryStore store, string id) => Results.Ok(store.GetIntake(id)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/intake/matches", async (LibraryStore store, ComicVine provider, string id, HttpRequest request) =>
        {
            if (!request.HasJsonContentType()) return Error(400, "Intake matching requires a JSON hints object.");
            try
            {
                var hints = await request.ReadFromJsonAsync<IntakeHints>(request.HttpContext.RequestAborted);
                var source = store.GetIntake(id);
                var matches = await provider.MatchIntakeAsync(hints, request.HttpContext.RequestAborted);
                if (store.GetIntake(id).Source != source.Source)
                    return Error(409, "The comicbook moved during matching. Refresh the review queue.");
                return Results.Ok(matches);
            }
            catch (JsonException) { return Error(400, "Intake hints require series, number, publisher and nullable integer year and volume only."); }
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/intake/prepare", async (LibraryStore store, ComicVine provider, string id,
            IntakePrepareRequest request, CancellationToken cancellationToken) =>
        {
            store.GetIntake(id);
            var metadata = await provider.MetadataAsync(request.IssueId, request.VolumeId, cancellationToken, intake: true);
            return Results.Ok(store.PrepareIntake(id, request, metadata, cancellationToken));
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/intake/process", async (LibraryStore store, ComicVine provider, string id,
            IntakeProcessRequest request, CancellationToken cancellationToken) =>
        {
            if (!request.Confirmed) return Error(400, "Explicit confirmation is required.");
            store.GetIntake(id);
            var metadata = await provider.MetadataAsync(request.IssueId, request.VolumeId, cancellationToken, intake: true);
            return Results.Ok(store.ProcessIntake(id, request, metadata, cancellationToken));
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/intake-processing", (LibraryStore store, string id) => Results.Ok(store.GetIntakeProcessing(id)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/intake-processing/recover", (LibraryStore store, string id,
            FileOperationRequest request, CancellationToken cancellationToken) =>
            Results.Ok(store.RecoverIntakeProcessing(id, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/intake-processing/original", (LibraryStore store, string id, HttpContext context) =>
        {
            var original = store.ExportIntakeOriginal(id);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(original.Stream, "application/octet-stream", original.Name);
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/pages/{page:int}", (LibraryStore store, HttpContext context, string id, int page) =>
        {
            var image = store.GetPage(id, page, context.RequestAborted);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(image.Bytes, image.ContentType);
        });
        api.MapPost("/comics/export", async (LibraryStore store, HttpContext context) =>
        {
            if (!context.Request.HasJsonContentType()) return Error(400, "Export requires a JSON object containing bookIds only.");
            try
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return Error(400, "Export requires bookIds only.");
                var fields = json.RootElement.EnumerateObject().ToArray();
                if (fields.Length != 1 || fields[0].Name != "bookIds" || fields[0].Value.ValueKind != JsonValueKind.Array)
                    return Error(400, "Export requires bookIds only.");
                var ids = fields[0].Value;
                if (ids.GetArrayLength() is < 1 or > 100 || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String))
                    return Error(400, "Select 1–100 distinct existing books.");
                var export = store.ExportComics(ids.EnumerateArray().Select(id => id.GetString()!).ToArray(), context.RequestAborted);
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(export.Stream, export.ContentType, export.FileName);
            }
            catch (JsonException) { return Error(400, "Export requires a JSON object containing bookIds only."); }
            catch (ArgumentException exception) { return Error(400, exception.Message); }
            catch (ComicExportLimitException exception) { return Error(413, exception.Message); }
        }).RequireAuthorization(policy => policy.RequireRole("reader", "admin"));
        api.MapGet("/books/{id}/page-info", (LibraryStore store, string id) =>
            Results.Ok(store.GetPageInfo(id)));
        api.MapGet("/books/{id}/page-order", (LibraryStore store, string id, CancellationToken cancellationToken) =>
            Results.Ok(store.GetPageOrder(id, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("reader", "admin"));
        api.MapPut("/books/{id}/page-order", async (LibraryStore store, string id, HttpRequest request) =>
        {
            if (!request.HasJsonContentType()) return Error(400, "Page order must be a JSON object.");
            try
            {
                var edit = await request.ReadFromJsonAsync<PageOrderEdit>(request.HttpContext.RequestAborted);
                return Results.Ok(store.SavePageOrder(id, edit!, request.HttpContext.RequestAborted));
            }
            catch (JsonException) { return Error(400, "Page order requires expectedRevision and an array of integer pages only."); }
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/page-metadata", (LibraryStore store, string id) => Results.Ok(store.GetPageMetadata(id)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/page-metadata/preview", (LibraryStore store, string id, PageMetadataEdit edit) =>
            Results.Ok(store.PreviewPageMetadata(id, edit)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPut("/books/{id}/page-metadata", (LibraryStore store, string id, PageMetadataEdit edit) =>
            Results.Ok(store.EditPageMetadata(id, edit)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/bookmarks", (LibraryStore store, ClaimsPrincipal user, string id) =>
            Results.Ok(store.GetBookmarks(UserId(user), id)));
        api.MapPut("/books/{id}/bookmarks/{page:int}", async (LibraryStore store, ClaimsPrincipal user,
            string id, int page, HttpRequest request) =>
        {
            if (!request.HasJsonContentType()) return Error(400, "Bookmark must be a JSON object with a name.");
            try
            {
                var edit = await request.ReadFromJsonAsync<BookmarkEdit>(request.HttpContext.RequestAborted);
                if (edit is null) return Error(400, "Bookmark must be a JSON object with a name.");
                store.SaveBookmark(UserId(user), id, page, edit);
                return Results.NoContent();
            }
            catch (JsonException) { return Error(400, "Bookmark requires a string name."); }
            catch (ArgumentException exception) { return Error(400, exception.Message); }
        });
        api.MapDelete("/books/{id}/bookmarks/{page:int}", (LibraryStore store, ClaimsPrincipal user, string id, int page) =>
        {
            store.DeleteBookmark(UserId(user), id, page);
            return Results.NoContent();
        });
        api.MapPost("/books/{id}/progress", (LibraryStore store, ClaimsPrincipal user, string id, ReadingProgress progress) =>
        {
            store.SaveProgress(UserId(user), id, progress);
            return Results.NoContent();
        });
        api.MapPost("/reading-status", async (LibraryStore store, ClaimsPrincipal user, HttpRequest request) =>
        {
            if (!request.HasJsonContentType()) return Error(400, "Reading status must be a JSON object.");
            try
            {
                var edit = await request.ReadFromJsonAsync<ReadingStatusEdit>(request.HttpContext.RequestAborted);
                store.SetReadingStatus(UserId(user), edit!);
                return Results.NoContent();
            }
            catch (JsonException) { return Error(400, "Reading status requires only bookIds and boolean completed."); }
            catch (ArgumentException exception) { return Error(400, exception.Message); }
        }).RequireAuthorization(policy => policy.RequireRole("reader", "admin"));
        api.MapPut("/books/{id}/rating", async (LibraryStore store, ClaimsPrincipal user, string id, HttpRequest request) =>
        {
            if (!request.HasJsonContentType()) return Error(400, "Rating must be a JSON object with an integer rating.");
            try
            {
                var edit = await request.ReadFromJsonAsync<RatingEdit>(request.HttpContext.RequestAborted);
                if (edit is null) return Error(400, "Rating must be a JSON object with an integer rating.");
                store.SaveRating(UserId(user), id, edit);
                return Results.NoContent();
            }
            catch (JsonException) { return Error(400, "Rating requires only an integer rating from 0 to 5."); }
            catch (ArgumentException exception) { return Error(400, exception.Message); }
        });
        api.MapGet("/metadata-fields", () => Results.Ok(LibraryStore.MetadataFields));
        api.MapGet("/books/{id}/metadata", (LibraryStore store, string id) => Results.Ok(store.GetMetadata(id)));
        api.MapGet("/metadata-report", (LibraryStore store, string? kind, int? page, int? pageSize) =>
            Results.Ok(store.GetMetadataReport(kind ?? "all", page ?? 1, pageSize ?? 48)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/metadata/preview", (LibraryStore store, BulkMetadataEdit edit) =>
            Results.Ok(store.PreviewMetadata(edit)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/metadata/export", (LibraryStore store, HttpContext context,
            string format, MetadataExportSelection selection) =>
        {
            if (format is not ("json" or "csv")) return Error(400, "Metadata export format must be json or csv.");
            var books = store.GetMetadataExport(selection.BookIds);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (format == "csv")
                return Results.Stream(stream => LibraryStore.WriteMetadataCsv(stream, books, context.RequestAborted),
                    "text/csv; charset=utf-8", fileDownloadName: "panelhaven-metadata.csv");
            context.Response.Headers.ContentDisposition = "attachment; filename=\"panelhaven-metadata.json\"";
            return Results.Json(books);
        });
        api.MapPost("/books/metadata", (LibraryStore store, BulkMetadataEdit edit) =>
        {
            store.EditMetadataBulk(edit, requirePreview: true);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPut("/books/{id}/metadata", (LibraryStore store, string id, MetadataEdit edit) =>
        {
            store.EditMetadata(id, edit);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/archive-write/preview", (LibraryStore store, string id, CancellationToken cancellationToken) =>
            Results.Ok(store.PreviewArchiveWrite(id, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/archive-write", (LibraryStore store, string id, ArchiveWriteRequest request, CancellationToken cancellationToken) =>
            Results.Ok(store.ApplyArchiveWrite(id, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/archive-backups", (LibraryStore store, string id) =>
            Results.Ok(store.GetArchiveBackups(id)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/archive-backups/{backupId}/preview", (LibraryStore store, string id, string backupId, CancellationToken cancellationToken) =>
            Results.Ok(store.PreviewArchiveRestore(id, backupId, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/archive-backups/{backupId}/restore",
            (LibraryStore store, string id, string backupId, ArchiveWriteRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.RestoreArchiveBackup(id, backupId, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/archive-backups/export/preview", (LibraryStore store) => Results.Ok(store.PreviewArchiveBackupExport()))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/archive-backups/export", (LibraryStore store, HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(store.ExportArchiveBackups(cancellationToken), "application/zip",
                $"panelhaven-archive-backups-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/file-operation-roots", (LibraryStore store) => Results.Ok(store.GetFileOperationRoots()))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/file-operation-folders", (LibraryStore store, FileOperationFolderRequest request) =>
                Results.Ok(store.CreateFileOperationFolder(request)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/file-operations/preview",
            (LibraryStore store, BulkFileMoveRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.PreviewFileMoves(request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/file-operations/template-preview",
            (LibraryStore store, FileTemplateRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.PreviewFileTemplates(request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/file-operations",
            (LibraryStore store, BulkFileMoveApplyRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.ApplyFileMoves(request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/file-operations/preview",
            (LibraryStore store, string id, FileMoveRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.PreviewFileMove(id, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/file-operations",
            (LibraryStore store, string id, FileMoveApplyRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.ApplyFileMove(id, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/file-operations", (LibraryStore store, string id) => Results.Ok(store.GetFileOperations(id)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/file-operations/{operationId}/undo/preview",
            (LibraryStore store, string id, string operationId, CancellationToken cancellationToken) =>
                Results.Ok(store.PreviewFileUndo(id, operationId, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/file-operations/{operationId}/undo",
            (LibraryStore store, string id, string operationId, FileOperationRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.UndoFileOperation(id, operationId, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/file-operations/{operationId}/recover/preview",
            (LibraryStore store, string id, string operationId, CancellationToken cancellationToken) =>
                Results.Ok(store.PreviewFileRecovery(id, operationId, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/books/{id}/file-operations/{operationId}/recover",
            (LibraryStore store, string id, string operationId, FileOperationRequest request, CancellationToken cancellationToken) =>
                Results.Ok(store.RecoverFileOperation(id, operationId, request, cancellationToken)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/list-rule-fields", () => Results.Ok(LibraryStore.ListRuleFields));
        api.MapGet("/list-folders", (LibraryStore store, ClaimsPrincipal user) => Results.Ok(store.GetListFolders(UserId(user))));
        api.MapPost("/list-folders", (LibraryStore store, ClaimsPrincipal user, ListFolderEdit edit) =>
            Results.Json(store.CreateListFolder(UserId(user), edit), statusCode: 201));
        api.MapPut("/list-folders/{id}", (LibraryStore store, ClaimsPrincipal user, string id, ListFolderEdit edit) =>
            Results.Ok(store.UpdateListFolder(UserId(user), id, edit)));
        api.MapDelete("/list-folders/{id}", (LibraryStore store, ClaimsPrincipal user, string id) =>
        {
            store.DeleteListFolder(UserId(user), id);
            return Results.NoContent();
        });
        api.MapGet("/lists", (LibraryStore store, ClaimsPrincipal user) => Results.Ok(store.GetLists(UserId(user))));
        api.MapPost("/lists/preview", (LibraryStore store, ClaimsPrincipal user, SmartListPreview edit, int? page, int? pageSize) =>
            Results.Ok(store.PreviewSmartList(UserId(user), edit, page ?? 1, pageSize ?? 48)));
        api.MapGet("/lists/{id}", (LibraryStore store, ClaimsPrincipal user, string id) => Results.Ok(store.GetList(UserId(user), id)));
        api.MapPost("/lists", (LibraryStore store, ClaimsPrincipal user, ListEdit edit) =>
            Results.Json(store.CreateList(UserId(user), edit), statusCode: 201));
        api.MapPut("/lists/{id}", (LibraryStore store, ClaimsPrincipal user, string id, ListEdit edit) =>
            Results.Ok(store.UpdateList(UserId(user), id, edit)));
        api.MapDelete("/lists/{id}", (LibraryStore store, ClaimsPrincipal user, string id) =>
        {
            store.DeleteList(UserId(user), id);
            return Results.NoContent();
        });
        api.MapPost("/lists/{id}/books", (LibraryStore store, ClaimsPrincipal user, string id, ListBooksSelection selection) =>
        {
            store.AddListBooks(UserId(user), id, selection.BookIds);
            return Results.NoContent();
        });
        api.MapDelete("/lists/{id}/books", (LibraryStore store, ClaimsPrincipal user, string id, [FromBody] ListBooksSelection selection) =>
        {
            store.RemoveListBooks(UserId(user), id, selection.BookIds);
            return Results.NoContent();
        });
        api.MapPut("/lists/{id}/order", (LibraryStore store, ClaimsPrincipal user, string id, ListMove move) =>
        {
            store.MoveListBook(UserId(user), id, move.BookId, move.BeforeId);
            return Results.NoContent();
        });
        api.MapGet("/scan", (ScanWorker worker) => Results.Ok(worker.Status))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/scan/log", (ScanWorker worker, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            try
            {
                var log = worker.ExportLog(context.RequestAborted);
                return log is null
                    ? Error(404, "No detailed scan log is available. Run a new scan; historical failures cannot be reconstructed.")
                    : Results.File(log, "application/x-ndjson",
                        $"panelhaven-scan-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Error(503, "The detailed scan log could not be exported. Check the server log.");
            }
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/watch-status", (ScanWorker worker) => Results.Ok(worker.WatchStatus))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/scan", (ScanWorker worker) => worker.TryStart(out var status)
            ? Results.Json(status, statusCode: 202)
            : Error(409, "A scan is already running or the server is stopping."))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapPost("/scan/cancel", (ScanWorker worker) => Results.Ok(worker.Cancel()))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
