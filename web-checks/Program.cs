using PanelHaven.Web;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args is ["--archive-write-crash-child", var mode, var fixture])
{
    ArchiveWriteChecks.RunCrashChild(mode, fixture);
    return;
}

LibraryChecks.Run();
Console.WriteLine("PASS: archive import, natural ordering, metadata preservation, isolated progress and unsafe archive rejection.");
ThumbnailChecks.Run();
Console.WriteLine("PASS: thumbnail resizing, cache reuse, invalidation and image safety.");
ArchiveChecks.Run();
Console.WriteLine("PASS: RAR, 7z and TAR archive behavior and safety.");
MetadataChecks.Run();
Console.WriteLine("PASS: full metadata, custom fields and atomic bulk edits.");
MetadataWorkflowChecks.Run();
Console.WriteLine("PASS: read-only metadata validation, exact conflict-safe previews and selected metadata exports.");
await ComicVineChecks.RunAsync();
Console.WriteLine("PASS: Comic Vine parsing, metadata mapping, input/URL safety, bounded responses, caching and sanitized rate-limit/provider failures.");
await IntakeMatchingChecks.RunAsync();
Console.WriteLine("PASS: read-only Unsorted hints, containment, normalized issue ranking, bounded provider matching and explicit ambiguity/failures.");
IntakeProcessingChecks.Run();
Console.WriteLine("PASS: explicit intake processing, original-organizer naming, verified CBR conversion and originals, shared/private identity, collision/security refusal and journal recovery.");
ArchiveWriteChecks.Run();
Console.WriteLine("PASS: verified archive backups, stale-safe metadata write-back and byte-exact undo.");
PageMetadataChecks.Run();
Console.WriteLine("PASS: stale-safe page metadata editing, preserved structures and protected archive write-back.");
PageOrderChecks.Run();
Console.WriteLine("PASS: shared page order permutations, stale/reset/source protection, identity preservation and relocation merging.");
ArchiveExportChecks.Run();
Console.WriteLine("PASS: verified private archive-original exports and recovery, including orphaned backups.");
ComicExportChecks.Run();
Console.WriteLine("PASS: download-only CBZ conversion, saved order and shared metadata remapping, private exclusion and staged single/batch exports.");
FileOperationChecks.Run();
Console.WriteLine("PASS: reviewed no-clobber file moves, preserved identities, verified undo and pending recovery.");
AclSecurityChecks.Run();
Console.WriteLine("PASS: bounded ACL snapshots, stale security detection and unchanged archive rewrite restrictions.");
NfsFileOperationChecks.Run();
Console.WriteLine("PASS: owned-link recovery, ACL-preserving moves and no-overwrite fallback safety.");
BulkFileOperationChecks.Run();
Console.WriteLine("PASS: metadata naming templates, collision-safe bulk previews and journaled partial completion.");
BookDeletionChecks.Run();
Console.WriteLine("PASS: explicit selected permanent deletion, stale/security/pending refusal, FK cleanup, retained originals and honest partial-failure recovery.");
ListChecks.Run();
Console.WriteLine("PASS: private reading lists, ordering and live smart rules.");
RecentlyAddedChecks.Run();
Console.WriteLine("PASS: durable per-account recently added lists and first-addition age rules.");
ReaderDataChecks.Run();
Console.WriteLine("PASS: private bookmarks, embedded page classifications and rescan boundaries.");
NextIssueChecks.Run();
Console.WriteLine("PASS: exact-series next issues, natural ordering, duplicate/gap skipping, actual locations and unchanged private progress.");
MaintenanceChecks.Run();
Console.WriteLine("PASS: missing-source preservation, safe catalog removal, exact duplicates and private read-only comparison.");
GroupChecks.Run();
Console.WriteLine("PASS: filtered group counts, drilldown, pagination and list ownership.");
SeriesShelfChecks.Run();
Console.WriteLine("PASS: private series shelves, activity/addition windows, unknown-date migration, stable paging and library-only scope.");
LibraryLocationChecks.Run();
Console.WriteLine("PASS: isolated library/archive/intake browsing, scoped filters, readable hidden comicbooks and identity-preserving moves.");
AccountChecks.Run();
Console.WriteLine("PASS: account lifecycle, session revocation, last-administrator protection and private-data cleanup.");
OrganizationChecks.Run();
Console.WriteLine("PASS: private ratings, nested list folders, expanded smart-rule previews and series reading statistics.");
RelocationChecks.Run();
Console.WriteLine("PASS: verified root relocation, private-data preservation and conflict refusal.");
RootRelocationJournalChecks.Run();
Console.WriteLine("PASS: root relocation preserves move history, page-edit intent and immutable archive-backup provenance.");
StateMaintenanceChecks.Run();
Console.WriteLine("PASS: exclusive state access, backup integrity and safe empty-target restore.");
await DeploymentChecks.RunAsync();
Console.WriteLine("PASS: HTTPS requirements, host validation and trusted proxy boundaries.");
ServerSettingsChecks.Run();
Console.WriteLine("PASS: persistent server settings, restart tracking, private recovery, secret keep/set/clear, stale writes, maintenance isolation and array replacement.");
HostManagementChecks.Run();
Console.WriteLine("PASS: fixed service commands, backup destination safety and verified whole-state relocation.");
await WatchChecks.RunAsync();
Console.WriteLine("PASS: watched-folder changes, debounce, startup recovery and scan lifecycle.");
await ScanLogChecks.RunAsync();
Console.WriteLine("PASS: bounded private scan diagnostics, full error export, fatal/cancelled outcomes, restart retention, rotation and independent snapshots.");
var directory = Path.Combine(Path.GetTempPath(), "panelhaven-accounts-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var accounts = new Accounts(Path.Combine(directory, "accounts.sqlite"));
    try
    {
        accounts.Bootstrap(null, null);
        throw new Exception("First startup accepted missing administrator credentials.");
    }
    catch (InvalidOperationException) { }
    accounts.Bootstrap("Admin", "a-check-only-password");
    accounts.Bootstrap(null, null);
    var admin = accounts.Authenticate("admin", "a-check-only-password");
    if (admin?.Role != "admin") throw new Exception("Case-insensitive login failed.");
    if (accounts.Authenticate("admin", "incorrect") is not null || accounts.Authenticate("missing", "a-check-only-password") is not null)
        throw new Exception("Invalid credentials accepted.");
    var reader = accounts.Create("reader", "another-check-password", "reader");
    if (reader.Id == admin.Id || Accounts.Principal(reader).IsInRole("admin"))
        throw new Exception("Reader identity or permissions are incorrect.");
    if (Accounts.Validate("bad user", "another-check-password", "reader") is null
        || Accounts.Validate("reader", "short", "reader") is null
        || Accounts.Validate("reader", "another-check-password", "owner") is null)
        throw new Exception("Invalid account fields accepted.");
    try
    {
        accounts.Create("ADMIN", "another-check-password", "reader");
        throw new Exception("Duplicate username accepted.");
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { }
    Console.WriteLine("PASS: administrator bootstrap, password verification, account validation and role separation.");
    using (var oldDatabase = new SqliteConnection($"Data Source={Path.Combine(directory, "accounts.sqlite")}"))
    {
        oldDatabase.Open();
        using var oldSchema = oldDatabase.CreateCommand();
        oldSchema.CommandText = """
            DROP TABLE reader_preferences;
            CREATE TABLE reader_preferences (
                user_id TEXT PRIMARY KEY REFERENCES users(id), mode TEXT NOT NULL, fit TEXT NOT NULL,
                zoom INTEGER NOT NULL, rtl INTEGER NOT NULL, cover_alone INTEGER NOT NULL
            );
            INSERT INTO reader_preferences VALUES($user,'spread','width',125,1,0);
            """;
        oldSchema.Parameters.AddWithValue("$user", admin.Id);
        oldSchema.ExecuteNonQuery();
    }
    accounts = new Accounts(Path.Combine(directory, "accounts.sqlite"));
    if (accounts.GetReaderPreferences(admin.Id) != new ReaderPreferences("spread", "width", 125, true, false, 0, 100, 100, 0))
        throw new Exception("Reader upgrade lost existing settings or changed image appearance.");

    var adminPreferences = new ReaderPreferences("spread", "width", 150, true, false, 90, 50, 200, 16);
    var readerPreferences = new ReaderPreferences("continuous", "page", 75, false, true, 270, 150, 50, 2047);
    accounts.SaveReaderPreferences(admin.Id, adminPreferences);
    accounts.SaveReaderPreferences(reader.Id, readerPreferences);
    accounts = new Accounts(Path.Combine(directory, "accounts.sqlite"));
    if (accounts.GetReaderPreferences(admin.Id) != adminPreferences || accounts.GetReaderPreferences(reader.Id) != readerPreferences)
        throw new Exception("Reader preferences were not persisted separately.");
    foreach (var invalid in new ReaderPreferences?[]
    {
        null, readerPreferences with { Mode = null! }, readerPreferences with { Mode = "other" },
        readerPreferences with { Fit = null! }, readerPreferences with { Fit = "other" },
        readerPreferences with { Zoom = 49 }, readerPreferences with { Zoom = 301 },
        readerPreferences with { Rotation = -90 }, readerPreferences with { Rotation = 45 }, readerPreferences with { Rotation = 360 },
        readerPreferences with { Brightness = 49 }, readerPreferences with { Brightness = 201 },
        readerPreferences with { Contrast = 49 }, readerPreferences with { Contrast = 201 },
        readerPreferences with { SkipPageTypes = -1 }, readerPreferences with { SkipPageTypes = 2048 }
    })
    {
        try
        {
            accounts.SaveReaderPreferences(reader.Id, invalid);
            throw new Exception("Invalid reader preferences accepted.");
        }
        catch (ArgumentException) { }
        if (accounts.GetReaderPreferences(reader.Id) != readerPreferences || accounts.GetReaderPreferences(admin.Id) != adminPreferences)
            throw new Exception("Invalid preferences clobbered saved state.");
    }
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var json = JsonSerializer.Serialize(readerPreferences, jsonOptions);
    foreach (var field in new[] { "mode", "fit", "zoom", "rtl", "coverAlone", "rotation", "brightness", "contrast", "skipPageTypes" })
    {
        var incomplete = JsonNode.Parse(json)!.AsObject();
        incomplete.Remove(field);
        try
        {
            accounts.SaveReaderPreferences(reader.Id, JsonSerializer.Deserialize<ReaderPreferences>(incomplete.ToJsonString(), jsonOptions));
            throw new Exception("Missing reader preference field accepted: " + field);
        }
        catch (JsonException) { }
    }
    foreach (var field in new[] { "zoom", "rotation", "brightness", "contrast", "skipPageTypes" })
    foreach (var invalidValue in new[] { "100.5", "\"100\"", "null" })
    {
        var invalid = JsonNode.Parse(json)!.AsObject();
        invalid[field] = JsonNode.Parse(invalidValue);
        try
        {
            accounts.SaveReaderPreferences(reader.Id, JsonSerializer.Deserialize<ReaderPreferences>(invalid.ToJsonString(), jsonOptions));
            throw new Exception("Non-integer reader setting accepted: " + field);
        }
        catch (JsonException) { }
    }
    var spoofed = JsonNode.Parse(json)!.AsObject();
    spoofed["userId"] = admin.Id;
    spoofed["zoom"] = 300;
    accounts.SaveReaderPreferences(reader.Id, JsonSerializer.Deserialize<ReaderPreferences>(spoofed.ToJsonString(), jsonOptions));
    if (accounts.GetReaderPreferences(admin.Id) != adminPreferences
        || accounts.GetReaderPreferences(reader.Id) != readerPreferences with { Zoom = 300 })
        throw new Exception("Reader preferences wrote to another account.");
    try
    {
        accounts.SaveReaderPreferences("missing-account", readerPreferences);
        throw new Exception("Preferences accepted a nonexistent account.");
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { }
    Console.WriteLine("PASS: persisted per-user reader preferences, strict payload validation and isolated updates.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}
