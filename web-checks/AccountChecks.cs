using System.IO.Compression;
using System.Security.Claims;
using PanelHaven.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

public static class AccountChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T exception) when (exception is not SqliteException sqlite || sqlite.SqliteErrorCode == 19) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-account-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "accounts.sqlite");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database, ForeignKeys = true
            }.ToString());
            db.Open();
            void Execute(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                command.ExecuteNonQuery();
            }
            object? Scalar(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return command.ExecuteScalar();
            }

            const string originalPassword = "old-legacy-password";
            var oldHasher = new PasswordHasher<string>(Options.Create(new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2
            }));
            var originalHash = oldHasher.HashPassword("LegacyAdmin", originalPassword);
            Execute("""
                CREATE TABLE users (
                    id TEXT PRIMARY KEY, username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    password_hash TEXT NOT NULL, role TEXT NOT NULL CHECK(role IN ('admin','reader')));
                CREATE TABLE reader_preferences (
                    user_id TEXT PRIMARY KEY REFERENCES users(id), mode TEXT NOT NULL, fit TEXT NOT NULL,
                    zoom INTEGER NOT NULL, rtl INTEGER NOT NULL, cover_alone INTEGER NOT NULL);
                INSERT INTO users VALUES('legacy-admin','LegacyAdmin',$hash,'admin');
                INSERT INTO reader_preferences VALUES('legacy-admin','spread','width',125,1,0);
                """, ("$hash", originalHash));
            var archivePath = Path.Combine(directory, "shared.cbz");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var stream = archive.CreateEntry("page.png").Open())
                    stream.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
                using var writer = new StreamWriter(archive.CreateEntry("ComicInfo.xml").Open());
                writer.Write("<ComicInfo><Title>Shared title</Title><Summary>Preserve me</Summary></ComicInfo>");
            }
            var library = new LibraryStore(database, [directory]);
            Check(library.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Account fixture import failed.");
            var bookId = library.ListBooks("legacy-admin", null).Items.Single().Id;
            library.SaveProgress("legacy-admin", bookId, new ReadingProgress(0, true));
            var accounts = new Accounts(database);
            Check((string)Scalar("SELECT password_hash FROM users WHERE id='legacy-admin'")! == originalHash,
                "Account migration changed an existing password.");
            var legacy = accounts.ListUsers().Single();
            Check(legacy.Id == "legacy-admin" && legacy.SessionVersion == 0 && !legacy.MustChangePassword,
                "Original accounts did not migrate to version zero and normal sign-in.");
            Check(accounts.GetReaderPreferences(legacy.Id) == new ReaderPreferences("spread", "width", 125, true, false, 0, 100, 100, 0)
                && library.GetBook(legacy.Id, bookId).Completed, "Migration lost preferences or progress.");
            var admin = accounts.Authenticate("legacyadmin", originalPassword)!;
            Check(admin == legacy && (string)Scalar("SELECT password_hash FROM users WHERE id=$id", ("$id", admin.Id))! != originalHash,
                "Legacy password rehash changed identity/version or did not upgrade the hash.");
            accounts = new Accounts(database);
            Check(accounts.Authenticate("LegacyAdmin", originalPassword) == admin, "Repeated migrations changed a valid account.");
            var adminSession = Accounts.Principal(admin);
            Check(accounts.ValidateSession(adminSession) == admin, "Valid session was rejected.");
            foreach (var claim in new[] { ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Role, "session_version", "must_change_password" })
            {
                var incomplete = new ClaimsPrincipal(new ClaimsIdentity(adminSession.Claims.Where(value => value.Type != claim), "Cookies"));
                Check(accounts.ValidateSession(incomplete) is null, "Incomplete cookie was accepted: " + claim);
            }
            foreach (var value in new[] { "", "-1", "1.5", " 0", "9223372036854775808", "not-a-version" })
            {
                var malformed = new ClaimsPrincipal(new ClaimsIdentity(adminSession.Claims.Where(claim => claim.Type != "session_version")
                    .Append(new Claim("session_version", value)), "Cookies"));
                Check(accounts.ValidateSession(malformed) is null, "Malformed session version was accepted.");
            }
            Check(accounts.ValidateSession(new ClaimsPrincipal(new ClaimsIdentity())) is null, "Anonymous identity became authenticated.");
            Check(accounts.ValidateSession(null) is null, "Missing identity became authenticated.");
            var reader = accounts.CreateManaged(admin.Id, admin.SessionVersion, "reader", "reader-old-password", "reader");
            var readerSession = Accounts.Principal(reader);
            Check(!reader.MustChangePassword && accounts.ValidateSession(readerSession) == reader, "New account was incorrectly forced to reset.");
            Check(accounts.ValidateSession(Accounts.Principal(reader with { Role = "admin" })) is null,
                "Claimed administrator role overrode the stored reader role.");
            Check(accounts.ValidateSession(Accounts.Principal(reader with { MustChangePassword = true })) is null,
                "Incorrect password-change claim was accepted.");
            Reject<InvalidOperationException>(() => accounts.CreateManaged(admin.Id, 0, "READER", "reader-new-password", "reader"));
            Reject<UnauthorizedAccessException>(() => accounts.CreateManaged(reader.Id, 0, "forbidden", "reader-new-password", "reader"));
            Reject<ArgumentException>(() => accounts.CreateManaged(admin.Id, 0, "bad user", "reader-new-password", "reader"));

            Check(!accounts.ChangePassword(reader.Id, 0, "incorrect-current", "reader-new-password"), "Incorrect current password was accepted.");
            Check(!accounts.ChangePassword(reader.Id, 0, null, "reader-new-password"), "Missing current password was accepted.");
            Reject<ArgumentException>(() => accounts.ChangePassword(reader.Id, 0, "reader-old-password", "reader-old-password"));
            foreach (var invalid in new string?[] { null, "short", new('x', 257) })
                Reject<ArgumentException>(() => accounts.ChangePassword(reader.Id, 0, "reader-old-password", invalid));
            Check(accounts.ValidateSession(readerSession) == reader && accounts.Authenticate(reader.Username, "reader-old-password") == reader,
                "Rejected password change mutated credentials or session version.");
            const string spacedPassword = "  new-reader-password  ";
            Check(accounts.ChangePassword(reader.Id, 0, "reader-old-password", spacedPassword), "Valid password change failed.");
            Check(accounts.ValidateSession(readerSession) is null && accounts.Authenticate(reader.Username, "reader-old-password") is null
                && accounts.Authenticate(reader.Username, spacedPassword.Trim()) is null, "Changed password was trimmed or old credentials/session survived.");
            reader = accounts.Authenticate(reader.Username, spacedPassword)!;
            Check(reader.SessionVersion == 1 && !reader.MustChangePassword, "Own password change did not bump version and clear forced state.");
            Reject<UnauthorizedAccessException>(() => accounts.ChangePassword(reader.Id, 0, spacedPassword, "stale-new-password"));
            Reject<ArgumentException>(() => accounts.ResetPassword(admin.Id, 0, admin.Id, "temporary-password"));
            Reject<ArgumentException>(() => accounts.ResetPassword(admin.Id, 0, reader.Id, "short"));
            Reject<KeyNotFoundException>(() => accounts.ResetPassword(admin.Id, 0, "missing", "temporary-password"));
            readerSession = Accounts.Principal(reader);
            accounts.ResetPassword(admin.Id, 0, reader.Id, "temporary-password");
            Check(accounts.ValidateSession(readerSession) is null && accounts.Authenticate(reader.Username, spacedPassword) is null,
                "Administrator reset left an old password or session valid.");
            reader = accounts.Authenticate(reader.Username, "temporary-password")!;
            Check(reader.MustChangePassword && reader.SessionVersion == 2
                && accounts.ValidateSession(Accounts.Principal(reader)) == reader, "Reset did not create a valid forced-change session.");
            Check(!accounts.ChangePassword(reader.Id, 2, "wrong-temporary", "reader-final-password")
                && accounts.ListUsers().Single(user => user.Id == reader.Id).MustChangePassword, "Wrong temporary password cleared the reset requirement.");
            Check(accounts.ChangePassword(reader.Id, 2, "temporary-password", "reader-final-password"), "Forced password change failed.");
            Check(accounts.ValidateSession(Accounts.Principal(reader)) is null, "Forced-change session survived password replacement.");
            reader = accounts.Authenticate(reader.Username, "reader-final-password")!;
            Check(reader.SessionVersion == 3 && !reader.MustChangePassword, "Forced-change completion was not persisted.");

            accounts.ChangeRole(admin.Id, 0, reader.Id, "reader");
            Check(accounts.ValidateSession(Accounts.Principal(reader)) == reader, "No-op role edit revoked a session.");
            Reject<ArgumentException>(() => accounts.ChangeRole(admin.Id, 0, reader.Id, "owner"));
            Reject<KeyNotFoundException>(() => accounts.ChangeRole(admin.Id, 0, "missing", "reader"));
            Reject<KeyNotFoundException>(() => accounts.DeleteUser(admin.Id, 0, "missing"));
            Reject<InvalidOperationException>(() => accounts.ChangeRole(admin.Id, 0, admin.Id, "reader"));
            Reject<InvalidOperationException>(() => accounts.DeleteUser(admin.Id, 0, admin.Id));
            accounts.ChangeRole(admin.Id, 0, reader.Id, "admin");
            Check(accounts.ValidateSession(Accounts.Principal(reader)) is null, "Role promotion did not revoke old sessions.");
            var promoted = accounts.Authenticate(reader.Username, "reader-final-password")!;
            Check(promoted.Role == "admin" && promoted.SessionVersion == 4, "Promoted role/version was not persisted.");
            accounts.ResetPassword(admin.Id, 0, promoted.Id, "forced-admin-password");
            var forcedAdmin = accounts.Authenticate(promoted.Username, "forced-admin-password")!;
            Reject<UnauthorizedAccessException>(() => accounts.CreateManaged(forcedAdmin.Id, forcedAdmin.SessionVersion, "forbidden", "forbidden-password", "reader"));
            Reject<UnauthorizedAccessException>(() => accounts.ResetPassword(forcedAdmin.Id, forcedAdmin.SessionVersion, admin.Id, "forbidden-password"));
            Reject<UnauthorizedAccessException>(() => accounts.ChangeRole(forcedAdmin.Id, forcedAdmin.SessionVersion, admin.Id, "reader"));
            Reject<UnauthorizedAccessException>(() => accounts.DeleteUser(forcedAdmin.Id, forcedAdmin.SessionVersion, admin.Id));
            Check(accounts.ChangePassword(forcedAdmin.Id, forcedAdmin.SessionVersion, "forced-admin-password", "reader-final-password"),
                "Forced administrator could not change their password.");
            promoted = accounts.Authenticate(reader.Username, "reader-final-password")!;
            accounts.ChangeRole(admin.Id, 0, promoted.Id, "reader");
            Reject<UnauthorizedAccessException>(() => accounts.CreateManaged(promoted.Id, promoted.SessionVersion, "forbidden", "forbidden-password", "reader"));
            Reject<UnauthorizedAccessException>(() => accounts.ResetPassword(promoted.Id, promoted.SessionVersion, admin.Id, "forbidden-password"));
            Reject<UnauthorizedAccessException>(() => accounts.ChangeRole(promoted.Id, promoted.SessionVersion, admin.Id, "reader"));
            Reject<UnauthorizedAccessException>(() => accounts.DeleteUser(promoted.Id, promoted.SessionVersion, admin.Id));
            Check(accounts.ValidateSession(Accounts.Principal(promoted)) is null, "Demotion left an administrator session valid.");
            reader = accounts.Authenticate(reader.Username, "reader-final-password")!;

            var preferences = new ReaderPreferences("single", "page", 100, false, true, 0, 100, 100, 0);
            accounts.SaveReaderPreferences(reader.Id, preferences);
            library.SaveProgress(reader.Id, bookId, new ReadingProgress(0, false));
            library.SaveBookmark(reader.Id, bookId, 0, new BookmarkEdit("Reader bookmark"));
            library.SaveBookmark(admin.Id, bookId, 0, new BookmarkEdit("Admin bookmark"));
            var listEdit = new ListEdit("Private", "manual", "all", []);
            var readerList = library.CreateList(reader.Id, listEdit);
            var adminList = library.CreateList(admin.Id, listEdit);
            library.AddListBooks(reader.Id, readerList.Id, [bookId]);
            library.AddListBooks(admin.Id, adminList.Id, [bookId]);
            library.GetLists(reader.Id);
            var originalXml = Scalar("SELECT info_xml FROM books WHERE id=$id", ("$id", bookId));
            var originalBytes = File.ReadAllBytes(archivePath);
            readerSession = Accounts.Principal(reader);
            accounts.DeleteUser(admin.Id, 0, reader.Id);
            Check(accounts.ValidateSession(readerSession) is null && accounts.Authenticate(reader.Username, "reader-final-password") is null,
                "Deleted account still authenticated.");
            foreach (var table in new[] { "progress", "bookmarks", "lists", "reader_preferences", "default_list_seeds" })
                Check((long)Scalar($"SELECT COUNT(*) FROM {table} WHERE user_id=$id", ("$id", reader.Id))! == 0, "Deletion left private rows in " + table);
            Check((long)Scalar("SELECT COUNT(*) FROM list_books WHERE list_id=$id", ("$id", readerList.Id))! == 0,
                "Deleted list membership did not cascade.");
            Check(library.GetBook(admin.Id, bookId).Completed && library.GetBookmarks(admin.Id, bookId).Single().Name == "Admin bookmark"
                && library.GetList(admin.Id, adminList.Id).BookIds!.SequenceEqual([bookId])
                && accounts.GetReaderPreferences(admin.Id).Zoom == 125, "Deletion modified another account's data.");
            Check(Equals(Scalar("SELECT info_xml FROM books WHERE id=$id", ("$id", bookId)), originalXml)
                && File.ReadAllBytes(archivePath).SequenceEqual(originalBytes), "Deletion modified shared metadata or archive files.");
            Reject<SqliteException>(() => library.SaveProgress(reader.Id, bookId, new ReadingProgress(0, false)));
            Reject<SqliteException>(() => library.SaveBookmark(reader.Id, bookId, 0, new BookmarkEdit("Stale request")));
            Reject<SqliteException>(() => library.CreateList(reader.Id, listEdit));
            Reject<SqliteException>(() => accounts.SaveReaderPreferences(reader.Id, preferences));
            foreach (var table in new[] { "progress", "bookmarks", "lists" })
                Reject<SqliteException>(() => Execute($"UPDATE {table} SET user_id=$deleted WHERE user_id=$admin", ("$deleted", reader.Id), ("$admin", admin.Id)));
            Reject<KeyNotFoundException>(() => library.AddListBooks(reader.Id, readerList.Id, [bookId]));
            var replacement = accounts.CreateManaged(admin.Id, 0, reader.Username, "replacement-password", "reader");
            Check(replacement.Id != reader.Id && library.GetLists(replacement.Id).All(list => list.Name == "Recently added")
                && library.GetBookmarks(replacement.Id, bookId).Count == 0
                && !library.GetBook(replacement.Id, bookId).Started, "Recreated username inherited deleted private data.");
            var successor = accounts.CreateManaged(admin.Id, 0, "successor", "successor-password", "admin");
            accounts.DeleteUser(admin.Id, 0, admin.Id);
            Check(accounts.ValidateSession(adminSession) is null && accounts.ListUsers().Single(user => user.Role == "admin").Id == successor.Id,
                "Self-deletion did not preserve the other administrator.");
            Check((long)Scalar("SELECT COUNT(*) FROM reader_preferences WHERE user_id=$id", ("$id", admin.Id))! == 0,
                "Legacy non-cascading preferences prevented account deletion.");

            var concurrent = new Accounts(Path.Combine(directory, "concurrent.sqlite"));
            var first = concurrent.Create("first", "first-admin-password", "admin");
            var second = concurrent.Create("second", "second-admin-password", "admin");
            using (var barrier = new Barrier(2))
            {
                Task<bool> Attempt(Action action) => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { action(); return true; }
                    catch (InvalidOperationException) { return false; }
                });
                var demote = Attempt(() => concurrent.ChangeRole(first.Id, 0, first.Id, "reader"));
                var delete = Attempt(() => concurrent.DeleteUser(second.Id, 0, second.Id));
                Task.WaitAll(demote, delete);
                Check(demote.Result != delete.Result && concurrent.ListUsers().Count(user => user.Role == "admin") == 1,
                    "Concurrent demotion/deletion removed the final administrator.");
            }
            var racingReader = concurrent.Create("racing", "racing-old-password", "reader");
            using (var barrier = new Barrier(2))
            {
                Task<bool> Change(string password) => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { return concurrent.ChangePassword(racingReader.Id, 0, "racing-old-password", password); }
                    catch (UnauthorizedAccessException) { return false; }
                });
                var left = Change("racing-left-password");
                var right = Change("racing-right-password");
                Task.WaitAll(left, right);
                Check(left.Result != right.Result && concurrent.ListUsers().Single(user => user.Id == racingReader.Id).SessionVersion == 1,
                    "Concurrent password changes reused an old session version.");
                Check((concurrent.Authenticate("racing", "racing-left-password") is not null) == left.Result
                    && (concurrent.Authenticate("racing", "racing-right-password") is not null) == right.Result,
                    "Concurrent password winner does not match persisted credentials.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
