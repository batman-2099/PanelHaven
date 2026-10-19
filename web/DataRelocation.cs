using System.Security.Cryptography;

namespace PanelHaven.Web;

public static class DataRelocation
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("Use an absolute path without control characters.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Symlinks and reparse points are not allowed: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public static bool Overlaps(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return first.Equals(second, comparison)
            || first.StartsWith(Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar, comparison)
            || second.StartsWith(Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar, comparison);
    }

    public static void Validate(ServerSettingsDocument document, string destination, string settingsPath)
    {
        destination = SafePath(destination);
        var source = SafePath(document.Settings.DataPath);
        if (Path.Exists(destination)) throw new IOException("The destination must not exist; existing content is never overwritten.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent)) throw new IOException("The destination parent directory must already exist.");
        var excluded = document.Settings.Libraries.Concat(new[] { document.Settings.ArchivePath, document.Settings.UnsortedPath }.OfType<string>())
            .Concat(new[] { source, source + ".state.lock", source + ".backup.lock",
                SafePath(settingsPath), SafePath(settingsPath + ".previous"), SafePath(AppContext.BaseDirectory) });
        if (!string.IsNullOrWhiteSpace(document.Settings.Backup.Destination))
            excluded = excluded.Append(document.Settings.Backup.Destination);
        if (excluded.Any(path => Overlaps(destination, SafePath(path))))
            throw new ArgumentException("The destination must be separate from state, libraries, backups, settings and application files.");
    }

    public static void Execute(ServerSettings store)
    {
        var document = store.Document;
        if (document.PendingDataPath is not { } destination) return;
        try { Validate(document, destination, store.Path); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SaveFailure(error);
            return;
        }
        // A handoff may start before the old process releases its lease. Leave the queued move intact so systemd retries.
        using var lease = StateMaintenance.Acquire(document.Settings.DataPath);
        using var destinationLease = StateMaintenance.Acquire(destination);
        try
        {
            CopyLocked(document.Settings.DataPath, destination, () => store.Save(document with { Revision = document.Revision + 1,
                Settings = document.Settings with { DataPath = SafePath(destination) }, PendingDataPath = null, RelocationError = null }));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Xml.XmlException or Microsoft.Data.Sqlite.SqliteException)
        {
            SaveFailure(error);
        }

        void SaveFailure(Exception error) => store.Save(document with { Revision = document.Revision + 1, PendingDataPath = null,
            RelocationError = "Data relocation failed; the original data remains active. " + error.Message });
    }

    public static void Copy(string source, string destination)
    {
        using var lease = StateMaintenance.Acquire(source);
        using var destinationLease = StateMaintenance.Acquire(destination);
        CopyLocked(source, destination, null);
    }

    private static void CopyLocked(string source, string destination, Action? commit)
    {
        source = SafePath(source);
        destination = SafePath(destination);
        if (Overlaps(source, destination) || Path.Exists(destination))
            throw new IOException("Relocation requires a new, non-overlapping destination.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent)) throw new IOException("The destination parent must already exist.");
        var stage = Path.Combine(parent, ".panelhaven-relocation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = Path.Combine(source, "panelhaven.sqlite");
            RequireRegular(database);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (Path.Exists(SafePath(database + suffix))) RequireRegular(database + suffix);
            var keys = SafePath(Path.Combine(source, "keys"));
            foreach (var key in Directory.EnumerateFileSystemEntries(keys)) RequireRegular(key);
            // Reuse the validated SQLite snapshot and complete key-ring copy, including committed WAL state.
            var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["backup"] = stage }).Build();
            StateMaintenance.Run(options, source);
            LibraryStore.FlushArchivePath(Path.Combine(stage, "panelhaven.sqlite"), false);
            foreach (var key in Directory.EnumerateFiles(Path.Combine(stage, "keys"))) LibraryStore.FlushArchivePath(key, false);
            LibraryStore.FlushArchivePath(Path.Combine(stage, "keys"), true);
            using var gate = StateMaintenance.AcquireBackupGate(source);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(entry);
                if (name is "keys" or "panelhaven.sqlite" or "panelhaven.sqlite-wal" or "panelhaven.sqlite-shm" or "panelhaven.sqlite-journal") continue;
                CopyEntry(entry, Path.Combine(stage, name));
            }
            LibraryStore.FlushArchivePath(stage, true);
            SafePath(destination);
            if (Path.Exists(destination)) throw new IOException("The destination appeared during relocation; nothing was overwritten.");
            Directory.Move(stage, destination);
            LibraryStore.FlushArchivePath(parent, true);
            commit?.Invoke();
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static void RequireRegular(string path)
    {
        SafePath(path);
        if (OperatingSystem.IsLinux() && (LibraryStore.ArchiveStatx(-100, path, 0x100, 0x1, out var stat) != 0
            || (stat.Mask & 0x1) == 0 || (stat.Mode & 0xf000) != 0x8000))
            throw new IOException("Relocation accepts only regular state files, not devices, sockets or FIFOs: " + path);
    }

    private static void CopyEntry(string source, string destination)
    {
        SafePath(source);
        if (Directory.Exists(source))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(destination);
            else Directory.CreateDirectory(destination, PrivateDirectory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                CopyEntry(entry, Path.Combine(destination, Path.GetFileName(entry)));
            LibraryStore.FlushArchivePath(destination, true);
            return;
        }
        RequireRegular(source);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, options))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        using var original = File.OpenRead(SafePath(source));
        using var copied = File.OpenRead(destination);
        Span<byte> originalHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> copiedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(original, originalHash);
        SHA256.HashData(copied, copiedHash);
        if (!originalHash.SequenceEqual(copiedHash)) throw new IOException("Relocated state failed byte verification: " + source);
    }
}
