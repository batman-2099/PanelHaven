using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace PanelHaven.Web;

public sealed record Account(string Id, string Username, string Role,
    [property: JsonIgnore] long SessionVersion = 0, [property: JsonIgnore] bool MustChangePassword = false);
public sealed record LoginRequest(string? Username, string? Password);
public sealed record CreateUserRequest(string? Username, string? Password, string? Role);
public sealed record ReaderPreferences(
    [property: JsonRequired] string Mode,
    [property: JsonRequired] string Fit,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Zoom,
    [property: JsonRequired] bool Rtl,
    [property: JsonRequired] bool CoverAlone,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Rotation,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Brightness,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Contrast,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int SkipPageTypes);

public sealed class Accounts
{
    private readonly string connectionString;
    private readonly PasswordHasher<string> hasher = new();
    private readonly string dummyHash;

    public Accounts(string databasePath)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 30
        }.ToString();
        dummyHash = hasher.HashPassword("", Guid.NewGuid().ToString());
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('admin','reader')),
                session_version INTEGER NOT NULL DEFAULT 0 CHECK(typeof(session_version)='integer' AND session_version >= 0),
                must_change_password INTEGER NOT NULL DEFAULT 0 CHECK(must_change_password IN (0,1))
            );
            CREATE TABLE IF NOT EXISTS reader_preferences (
                user_id TEXT PRIMARY KEY NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                mode TEXT NOT NULL CHECK(mode IN ('single','continuous','spread')),
                fit TEXT NOT NULL CHECK(fit IN ('page','width')),
                zoom INTEGER NOT NULL CHECK(typeof(zoom) = 'integer' AND zoom BETWEEN 50 AND 300),
                rtl INTEGER NOT NULL CHECK(rtl IN (0,1)),
                cover_alone INTEGER NOT NULL CHECK(cover_alone IN (0,1))
            );
            """;
        command.ExecuteNonQuery();
        using var transaction = db.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM pragma_table_info('users')";
        var userColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) userColumns.Add(reader.GetString(0));
        foreach (var (name, definition) in new[]
        {
            ("session_version", "INTEGER NOT NULL DEFAULT 0 CHECK(typeof(session_version)='integer' AND session_version >= 0)"),
            ("must_change_password", "INTEGER NOT NULL DEFAULT 0 CHECK(must_change_password IN (0,1))")
        })
        {
            if (userColumns.Contains(name)) continue;
            command.CommandText = $"ALTER TABLE users ADD COLUMN {name} {definition}";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT name FROM pragma_table_info('reader_preferences')";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(0));
        foreach (var (name, definition) in new[]
        {
            ("rotation", "INTEGER NOT NULL DEFAULT 0 CHECK(rotation IN (0,90,180,270))"),
            ("brightness", "INTEGER NOT NULL DEFAULT 100 CHECK(typeof(brightness)='integer' AND brightness BETWEEN 50 AND 200)"),
            ("contrast", "INTEGER NOT NULL DEFAULT 100 CHECK(typeof(contrast)='integer' AND contrast BETWEEN 50 AND 200)"),
            ("skip_page_types", "INTEGER NOT NULL DEFAULT 0 CHECK(typeof(skip_page_types)='integer' AND skip_page_types BETWEEN 0 AND 2047)")
        })
        {
            if (columns.Contains(name)) continue;
            command.CommandText = $"ALTER TABLE reader_preferences ADD COLUMN {name} {definition}";
            command.ExecuteNonQuery();
        }
        // ponytail: library-only databases keep synthetic IDs; deployed startup initializes LibraryStore first.
        foreach (var table in new[] { "progress", "bookmarks", "lists", "ratings", "list_folders" })
        {
            command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$table";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() is null) continue;
            command.Parameters.Clear();
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS {table}_account_insert BEFORE INSERT ON {table}
                WHEN NOT EXISTS(SELECT 1 FROM users WHERE id=NEW.user_id)
                BEGIN SELECT RAISE(ABORT,'Account no longer exists.'); END;
                CREATE TRIGGER IF NOT EXISTS {table}_account_update BEFORE UPDATE ON {table}
                WHEN NOT EXISTS(SELECT 1 FROM users WHERE id=NEW.user_id)
                BEGIN SELECT RAISE(ABORT,'Account no longer exists.'); END;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public void Bootstrap(string? username, string? password)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users";
        if ((long)command.ExecuteScalar()! != 0) return;
        var error = Validate(username, password, "admin");
        if (error is not null)
            throw new InvalidOperationException("Set PANELHAVEN_ADMIN_USERNAME and PANELHAVEN_ADMIN_PASSWORD for first startup. " + error);
        Create(username!, password!, "admin");
    }

    public static string? Validate(string? username, string? password, string? role)
    {
        if (username is null || !Regex.IsMatch(username, @"\A[a-zA-Z0-9_.-]{1,64}\z"))
            return "Username must contain 1–64 letters, numbers, dots, hyphens or underscores.";
        var passwordError = ValidatePassword(password);
        if (passwordError is not null) return passwordError;
        if (role is not ("admin" or "reader")) return "Role must be admin or reader.";
        return null;
    }
    private static string? ValidatePassword(string? password) => password is null || password.Length is < 12 or > 256
        ? "Password must contain 12–256 characters." : null;

    public Account Create(string username, string password, string role)
    {
        using var db = Open();
        return Create(db, null, username, password, role);
    }

    private Account Create(SqliteConnection db, SqliteTransaction? transaction, string username, string password, string role)
    {
        var error = Validate(username, password, role);
        if (error is not null) throw new ArgumentException(error);
        var user = new Account(Guid.NewGuid().ToString("N"), username, role);
        var hash = hasher.HashPassword(username, password);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO users(id,username,password_hash,role) VALUES($id,$username,$hash,$role)";
        command.Parameters.AddWithValue("$id", user.Id);
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$role", role);
        command.ExecuteNonQuery();
        return user;
    }

    private static Account ReadAccount(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetBoolean(4));

    private static Account? FindAccount(SqliteConnection db, SqliteTransaction? transaction, string id)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,username,role,session_version,must_change_password FROM users WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public Account? ValidateSession(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var forced = principal.FindFirstValue("must_change_password");
        if (string.IsNullOrEmpty(id)
            || !long.TryParse(principal.FindFirstValue("session_version"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || forced is not ("true" or "false")) return null;
        using var db = Open();
        var account = FindAccount(db, null, id);
        return account is not null && account.SessionVersion == version
            && account.Role == principal.FindFirstValue(ClaimTypes.Role)
            && account.Username == principal.FindFirstValue(ClaimTypes.Name)
            && account.MustChangePassword == (forced == "true") ? account : null;
    }

    public List<Account> ListUsers()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,username,role,session_version,must_change_password FROM users ORDER BY username COLLATE NOCASE,id";
        using var reader = command.ExecuteReader();
        var accounts = new List<Account>();
        while (reader.Read()) accounts.Add(ReadAccount(reader));
        return accounts;
    }

    private static void RequireAdmin(SqliteConnection db, SqliteTransaction transaction, string actorId, long actorVersion)
    {
        var actor = FindAccount(db, transaction, actorId);
        if (actor is null || actor.SessionVersion != actorVersion || actor.Role != "admin" || actor.MustChangePassword)
            throw new UnauthorizedAccessException("Administrator session is no longer authorized.");
    }

    public Account CreateManaged(string actorId, long actorVersion, string username, string password, string role)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        RequireAdmin(db, transaction, actorId, actorVersion);
        try
        {
            var account = Create(db, transaction, username, password, role);
            transaction.Commit();
            return account;
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 2067)
        {
            throw new InvalidOperationException("Username is already in use.");
        }
    }

    public bool ChangePassword(string userId, long sessionVersion, string? currentPassword, string? newPassword)
    {
        var error = ValidatePassword(newPassword);
        if (error is not null) throw new ArgumentException(error);
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT username,password_hash,session_version FROM users WHERE id=$id";
        command.Parameters.AddWithValue("$id", userId);
        string username, hash;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(2) != sessionVersion)
                throw new UnauthorizedAccessException("Session is no longer authorized.");
            username = reader.GetString(0);
            hash = reader.GetString(1);
        }
        if (currentPassword is null || currentPassword.Length > 256
            || hasher.VerifyHashedPassword(username, hash, currentPassword) == PasswordVerificationResult.Failed) return false;
        if (newPassword == currentPassword) throw new ArgumentException("New password must differ from the current password.");
        command.CommandText = """
            UPDATE users SET password_hash=$hash,must_change_password=0,session_version=session_version+1
            WHERE id=$id AND session_version=$version
            """;
        command.Parameters.AddWithValue("$hash", hasher.HashPassword(username, newPassword!));
        command.Parameters.AddWithValue("$version", sessionVersion);
        if (command.ExecuteNonQuery() != 1) throw new UnauthorizedAccessException("Session is no longer authorized.");
        transaction.Commit();
        return true;
    }

    public void ResetPassword(string actorId, long actorVersion, string userId, string? password)
    {
        var error = ValidatePassword(password);
        if (error is not null) throw new ArgumentException(error);
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        RequireAdmin(db, transaction, actorId, actorVersion);
        if (actorId == userId) throw new ArgumentException("Use Change password to change your own password.");
        var account = FindAccount(db, transaction, userId) ?? throw new KeyNotFoundException("Account not found.");
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE users SET password_hash=$hash,must_change_password=1,session_version=session_version+1 WHERE id=$id";
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$hash", hasher.HashPassword(account.Username, password!));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void PreserveAdministrator(SqliteConnection db, SqliteTransaction transaction, Account account)
    {
        if (account.Role != "admin") return;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM users WHERE role='admin'";
        if ((long)command.ExecuteScalar()! <= 1)
            throw new InvalidOperationException("At least one administrator must remain.");
    }

    public void ChangeRole(string actorId, long actorVersion, string userId, string? role)
    {
        if (role is not ("admin" or "reader")) throw new ArgumentException("Role must be admin or reader.");
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        RequireAdmin(db, transaction, actorId, actorVersion);
        var account = FindAccount(db, transaction, userId) ?? throw new KeyNotFoundException("Account not found.");
        if (account.Role == role) return;
        PreserveAdministrator(db, transaction, account);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE users SET role=$role,session_version=session_version+1 WHERE id=$id";
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$role", role);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void DeleteUser(string actorId, long actorVersion, string userId)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: false);
        RequireAdmin(db, transaction, actorId, actorVersion);
        var account = FindAccount(db, transaction, userId) ?? throw new KeyNotFoundException("Account not found.");
        PreserveAdministrator(db, transaction, account);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        foreach (var table in new[] { "progress", "bookmarks", "lists", "ratings", "list_folders" })
        {
            command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$table";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() is null) continue;
            command.CommandText = $"DELETE FROM {table} WHERE user_id=$id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", userId);
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", userId);
        command.CommandText = "DELETE FROM reader_preferences WHERE user_id=$id; DELETE FROM users WHERE id=$id";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public Account? Authenticate(string? username, string? password)
    {
        if (username is null || password is null || username.Length > 64 || password.Length > 256) return null;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,username,role,session_version,must_change_password,password_hash FROM users WHERE username=$username";
        command.Parameters.AddWithValue("$username", username);
        Account? user = null;
        var hash = dummyHash;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                user = ReadAccount(reader);
                hash = reader.GetString(5);
            }
        }
        var result = hasher.VerifyHashedPassword(username, hash, password);
        if (user is null || result == PasswordVerificationResult.Failed) return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE users SET password_hash=$hash WHERE id=$id AND password_hash=$oldHash AND session_version=$version";
            update.Parameters.AddWithValue("$hash", hasher.HashPassword(username, password));
            update.Parameters.AddWithValue("$id", user.Id);
            update.Parameters.AddWithValue("$oldHash", hash);
            update.Parameters.AddWithValue("$version", user.SessionVersion);
            if (update.ExecuteNonQuery() == 0) return null;
        }
        return user;
    }

    public ReaderPreferences GetReaderPreferences(string userId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT mode,fit,zoom,rtl,cover_alone,rotation,brightness,contrast,skip_page_types FROM reader_preferences WHERE user_id=$user";
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ReaderPreferences(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetBoolean(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8))
            : new ReaderPreferences("single", "page", 100, false, true, 0, 100, 100, 0);
    }

    public void SaveReaderPreferences(string userId, ReaderPreferences? preferences)
    {
        if (preferences is null) throw new ArgumentException("Reader preferences are required.");
        if (preferences.Mode is not ("single" or "continuous" or "spread"))
            throw new ArgumentException("Reader mode must be single, continuous or spread.");
        if (preferences.Fit is not ("page" or "width"))
            throw new ArgumentException("Reader fit must be page or width.");
        if (preferences.Zoom is < 50 or > 300)
            throw new ArgumentException("Reader zoom must be an integer from 50 to 300.");
        if (preferences.Rotation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("Rotation must be 0, 90, 180 or 270 degrees.");
        if (preferences.Brightness is < 50 or > 200 || preferences.Contrast is < 50 or > 200)
            throw new ArgumentException("Brightness and contrast must be integers from 50 to 200.");
        if (preferences.SkipPageTypes is < 0 or > 2047)
            throw new ArgumentException("Invalid skipped page types.");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO reader_preferences(user_id,mode,fit,zoom,rtl,cover_alone,rotation,brightness,contrast,skip_page_types)
            VALUES($user,$mode,$fit,$zoom,$rtl,$cover,$rotation,$brightness,$contrast,$skip)
            ON CONFLICT(user_id) DO UPDATE SET
                mode=excluded.mode,fit=excluded.fit,zoom=excluded.zoom,rtl=excluded.rtl,cover_alone=excluded.cover_alone,
                rotation=excluded.rotation,brightness=excluded.brightness,contrast=excluded.contrast,skip_page_types=excluded.skip_page_types
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$mode", preferences.Mode);
        command.Parameters.AddWithValue("$fit", preferences.Fit);
        command.Parameters.AddWithValue("$zoom", preferences.Zoom);
        command.Parameters.AddWithValue("$rtl", preferences.Rtl);
        command.Parameters.AddWithValue("$cover", preferences.CoverAlone);
        command.Parameters.AddWithValue("$rotation", preferences.Rotation);
        command.Parameters.AddWithValue("$brightness", preferences.Brightness);
        command.Parameters.AddWithValue("$contrast", preferences.Contrast);
        command.Parameters.AddWithValue("$skip", preferences.SkipPageTypes);
        command.ExecuteNonQuery();
    }

    public static ClaimsPrincipal Principal(Account user) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role),
         new Claim("session_version", user.SessionVersion.ToString(CultureInfo.InvariantCulture)),
         new Claim("must_change_password", user.MustChangePassword ? "true" : "false")],
        "Cookies"));
}
