using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;

namespace ComicRack.Web;
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
            catch (KeyNotFoundException) { return Error(404, "Book, list, folder, or bookmark not found."); }
            catch (FileNotFoundException) { return Error(404, "Source archive not found."); }
            catch (DirectoryNotFoundException) { return Error(404, "Source archive not found."); }
            catch (MetadataRevisionException exception) { return Error(409, exception.Message); }
            catch (ArgumentException) { return Error(400, "Invalid page, bookmark, search, sort, filter, or metadata fields."); }
            catch (XmlException) { return Error(400, "Metadata contains invalid XML characters."); }
            catch (InvalidOperationException) { return Error(409, "Archive changed or is unavailable; ask an administrator to rescan."); }
            catch (Exception ex) when (LibraryStore.IsArchiveError(ex))
            {
                return Error(422, "Archive is unavailable, unsafe, or invalid.");
            }
            catch (SqliteException)
            {
                return Error(503, "Library database is unavailable. Try again later.");
            }
        });
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
            string? availability, string? groupBy, string? groupKey, int? rating) =>
            Results.Ok(store.ListBooks(UserId(user), q, page ?? 1, pageSize ?? 48, sort ?? "series",
                direction ?? "asc", status ?? "all", publisher, listId, availability ?? "all", groupBy ?? "none", groupKey, rating)));
        api.MapGet("/book-filters", (LibraryStore store) => Results.Ok(new { publishers = store.ListPublishers() }));
        api.MapGet("/book-groups", (LibraryStore store, ClaimsPrincipal user, string? q, string groupBy,
            int? page, int? pageSize, string? status, string? publisher, string? listId, string? availability, int? rating) =>
            Results.Ok(store.ListGroups(UserId(user), q, groupBy, page ?? 1, pageSize ?? 48,
                status ?? "all", publisher, listId, availability ?? "all", rating)));
        api.MapGet("/maintenance", (LibraryStore store, string kind, int? page, int? pageSize) =>
            Results.Ok(store.ListMaintenance(kind, page ?? 1, pageSize ?? 48)))
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapDelete("/books/{id}", (LibraryStore store, string id) =>
        {
            store.RemoveMissingBook(id);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole("admin"));
        api.MapGet("/books/{id}/thumbnail", async (ThumbnailStore thumbnails, string id, CancellationToken cancellationToken) =>
            Results.Bytes(await thumbnails.GetAsync(id, cancellationToken), "image/jpeg"));
        api.MapGet("/books/{id}", (LibraryStore store, ClaimsPrincipal user, string id) =>
            Results.Ok(store.GetBook(UserId(user), id)));
        api.MapGet("/books/{id}/pages/{page:int}", (LibraryStore store, HttpContext context, string id, int page) =>
        {
            var image = store.GetPage(id, page);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(image.Bytes, image.ContentType);
        });
        api.MapGet("/books/{id}/page-info", (LibraryStore store, string id) =>
            Results.Ok(store.GetPageInfo(id)));
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
                    "text/csv; charset=utf-8", fileDownloadName: "comicrack-metadata.csv");
            context.Response.Headers.ContentDisposition = "attachment; filename=\"comicrack-metadata.json\"";
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
