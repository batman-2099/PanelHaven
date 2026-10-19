using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using System.Xml;
using System.Xml.Linq;

namespace PanelHaven.Web;

public static class StateMaintenance
{
    private const string Database = "panelhaven.sqlite";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModePrivate = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static FileStream Acquire(string dataPath) => AcquireLock(dataPath, ".state.lock",
        "Cannot acquire the state lease. Stop the Panel Haven service before maintenance; another process may be using this DataPath.");

    public static FileStream AcquireBackupGate(string dataPath) => AcquireLock(dataPath, ".backup.lock",
        "Cannot acquire the backup gate. Another backup, maintenance operation, or server startup is using this DataPath.");

    private static FileStream AcquireLock(string dataPath, string suffix, string message)
    {
        dataPath = SafePath(dataPath);
        var lockPath = SafePath(dataPath + suffix);
        // Keep the inode after release: unlinking a lease lets another process lock a different inode.
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModePrivate;
        try
        {
            var lease = new FileStream(lockPath, options);
            try { MakePrivate(lockPath); }
            catch { lease.Dispose(); throw; }
            return lease;
        }
        catch (IOException exception)
        {
            throw new IOException(message, exception);
        }
    }

    public static bool ValidateOptions(IConfiguration configuration, string dataPath)
    {
        var backup = configuration["backup"];
        var live = configuration["backup-live"];
        var restore = configuration["restore"];
        var from = configuration["relocate-from"];
        var to = configuration["relocate-to"];
        var apply = configuration["apply-relocation"];
        var relocating = from is not null || to is not null || apply is not null;
        if ((backup is not null ? 1 : 0) + (live is not null ? 1 : 0) + (restore is not null ? 1 : 0)
            + (relocating ? 1 : 0) > 1)
            throw new ArgumentException("Choose exactly one operation: backup, backup-live, restore, or relocation.");
        if (relocating)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)
                || !Path.IsPathFullyQualified(from) || !Path.IsPathFullyQualified(to))
                throw new ArgumentException("Relocation requires absolute --relocate-from and --relocate-to roots.");
            if (apply is not null && !bool.TryParse(apply, out _))
                throw new ArgumentException("--apply-relocation must be true or false; omit it for a preview.");
        }
        var requested = backup ?? live ?? restore;
        if (requested is not null)
        {
            if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("Backup and restore require a directory path.");
            dataPath = SafePath(dataPath);
            var other = SafePath(requested);
            if (Contains(dataPath, other) || Contains(other, dataPath)
                || other.Equals(dataPath + ".state.lock", PathComparison)
                || other.Equals(dataPath + ".backup.lock", PathComparison))
                throw new ArgumentException("State and backup directories must be separate, non-overlapping trees.");
        }
        return live is not null;
    }

    public static bool Run(IConfiguration configuration, string dataPath)
    {
        ValidateOptions(configuration, dataPath);
        var backup = configuration["backup"] ?? configuration["backup-live"];
        var restore = configuration["restore"];
        if (backup is null && restore is null) return false;
        dataPath = SafePath(dataPath);
        var other = SafePath(backup ?? restore!);
        using var gate = AcquireBackupGate(dataPath);
        if (backup is not null) Backup(dataPath, other);
        else Restore(other, dataPath);
        Console.WriteLine(backup is not null ? $"Backup completed: {other}" : $"Restore completed: {dataPath}");
        return true;
    }

    private static void Backup(string source, string destination)
    {
        if (Path.Exists(destination)) throw new IOException("Backup destination already exists; nothing will be overwritten.");
        var stage = CreateStage(destination);
        try
        {
            CopyState(source, stage, restoring: false);
            Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private static void Restore(string source, string destination)
    {
        RequireEmpty(destination);
        var stage = CreateStage(destination);
        try
        {
            CopyState(source, stage, restoring: true);
            RequireEmpty(destination);
            // The lease is a sibling, so the complete validated directory can be published atomically on Windows too.
            Directory.Delete(destination);
            try { Directory.Move(stage, destination); }
            catch { CreatePrivateDirectory(destination); throw; }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private static void RequireEmpty(string directory)
    {
        if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Restore requires an empty DataPath; existing state will never be overwritten.");
    }

    private static string CreateStage(string destination)
    {
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("A filesystem root is not a destination.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination parent directory must already exist.");
        var stage = Path.Combine(parent, ".panelhaven-state-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(stage);
        return stage;
    }

    private static void CopyState(string source, string stage, bool restoring)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The source state directory does not exist.");
        if (restoring)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                if (Path.GetFileName(entry) is not (Database or "keys"))
                    throw new InvalidDataException("A backup must contain only panelhaven.sqlite and keys.");
        }
        var database = SafePath(Path.Combine(source, Database));
        var keys = SafePath(Path.Combine(source, "keys"));
        if (!File.Exists(database) || !Directory.Exists(keys))
            throw new InvalidDataException("State must contain panelhaven.sqlite and its complete keys directory.");
        // Reject sidecar links before SQLite can follow them, even for a read-only connection.
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) SafePath(database + suffix);
        var targetKeys = Path.Combine(stage, "keys");
        CreatePrivateDirectory(targetKeys);
        var entries = Directory.GetFileSystemEntries(keys);
        foreach (var entry in entries)
        {
            SafePath(entry);
            if (Directory.Exists(entry) || !Path.GetExtension(entry).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The keys directory must contain only Data Protection XML files.");
            var target = Path.Combine(targetKeys, Path.GetFileName(entry));
            File.Copy(entry, target, overwrite: false);
            MakePrivate(target);
            ValidateKey(target);
        }
        var targetDatabase = Path.Combine(stage, Database);
        using (var input = Open(database, SqliteOpenMode.ReadOnly))
        using (var output = Open(targetDatabase, SqliteOpenMode.ReadWriteCreate))
        {
            input.BackupDatabase(output);
            using var command = output.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE";
            command.ExecuteNonQuery();
            ValidateDatabase(output);
        }
        MakePrivate(targetDatabase);
        // Data Protection writes keys independently of SQLite; refuse a changing ring rather than retrying.
        if (!entries.Order(StringComparer.Ordinal).SequenceEqual(
            Directory.EnumerateFileSystemEntries(SafePath(keys)).Order(StringComparer.Ordinal)))
            throw new IOException("The key ring changed during the snapshot; no backup was published.");
        Span<byte> sourceHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> targetHash = stackalloc byte[SHA256.HashSizeInBytes];
        foreach (var entry in entries)
        {
            using var original = File.OpenRead(SafePath(entry));
            using var copied = File.OpenRead(Path.Combine(targetKeys, Path.GetFileName(entry)));
            SHA256.HashData(original, sourceHash);
            SHA256.HashData(copied, targetHash);
            if (!sourceHash.SequenceEqual(targetHash))
                throw new IOException("The key ring changed during the snapshot; no backup was published.");
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            connection.CreateCollation("NATURAL_NUMBER", static (a, b) => LibraryStore.NaturalCompare(a ?? "", b ?? ""));
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void ValidateDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new InvalidDataException("The backup database failed SQLite integrity checking.");
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("The backup database has broken foreign keys.");
        foreach (var (table, required) in new (string, string[])[]
        {
            ("books", ["id", "path", "title", "series", "number", "publisher", "summary", "page_count", "info_xml", "book_xml", "edited", "file_size", "modified_ticks"]),
            ("users", ["id", "username", "password_hash", "role", "session_version", "must_change_password"]),
            ("progress", ["user_id", "book_id", "page", "completed"]),
            ("ratings", ["user_id", "book_id", "rating"]),
            ("lists", ["id", "user_id", "name", "kind", "match", "rules_json", "folder_id"]),
            ("list_books", ["list_id", "book_id", "position"]),
            ("list_folders", ["id", "user_id", "name"]),
            ("bookmarks", ["user_id", "book_id", "page", "name"]),
            ("reader_preferences", ["user_id", "mode", "fit", "zoom", "rtl", "cover_alone", "rotation", "brightness", "contrast", "skip_page_types"])
        })
        {
            command.CommandText = "SELECT type FROM sqlite_schema WHERE name=$table";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() as string != "table") throw new InvalidDataException($"Missing state table: {table}.");
            command.CommandText = "SELECT name FROM pragma_table_info($table)";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
            if (required.Any(column => !columns.Contains(column))) throw new InvalidDataException($"Invalid state table: {table}.");
        }
    }

    private static void ValidateKey(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name == "key")
        {
            if (!Guid.TryParse((string?)root.Attribute("id"), out _) || (string?)root.Attribute("version") != "1"
                || !ValidDate(root.Element("creationDate")) || !ValidDate(root.Element("activationDate"))
                || !ValidDate(root.Element("expirationDate")) || root.Element("descriptor") is not { } descriptor
                || string.IsNullOrWhiteSpace((string?)descriptor.Attribute("deserializerType")) || !descriptor.Elements().Any())
                throw new InvalidDataException("Invalid Data Protection key XML.");
        }
        else if (root?.Name != "revocation" || (string?)root.Attribute("version") != "1"
            || !ValidDate(root.Element("revocationDate"))
            || (string?)root.Element("key")?.Attribute("id") is not { } revoked
            || revoked != "*" && !Guid.TryParse(revoked, out _))
            throw new InvalidDataException("Invalid Data Protection key or revocation XML.");

        static bool ValidDate(XElement? element) => DateTimeOffset.TryParse(element?.Value,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    }

    private static string SafePath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("State maintenance refuses symlinks and reparse points: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static bool Contains(string parent, string child) => child.Equals(parent, PathComparison)
        || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, DirectoryMode);
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, FileModePrivate);
    }
}
