using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Xml;
using PanelHaven.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

public static class StateMaintenanceChecks
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject(Action action, Type? errorType = null)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or SqliteException or XmlException)
            {
                Check(errorType is null || error.GetType() == errorType, "Maintenance failed with an unexpected exception type: " + error.GetType().Name);
                return;
            }
            throw new Exception("Unsafe state maintenance was accepted.");
        }
        static IConfiguration Options(params (string Key, string Value)[] values) => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build();
        static IDataProtector Protector(string path) => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(path, "keys")),
            options => options.SetApplicationName("PanelHaven.Web")).CreateProtector("state-maintenance-check");
        static void CopyBackup(string source, string destination)
        {
            Directory.CreateDirectory(Path.Combine(destination, "keys"));
            File.Copy(Path.Combine(source, "panelhaven.sqlite"), Path.Combine(destination, "panelhaven.sqlite"));
            foreach (var key in Directory.EnumerateFiles(Path.Combine(source, "keys")))
                File.Copy(key, Path.Combine(destination, "keys", Path.GetFileName(key)));
        }

        var root = Path.Combine(Path.GetTempPath(), "panelhaven-state-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var untouched = Path.Combine(root, "untouched");
            foreach (var options in new[]
            {
                Options(("backup-live", "one"), ("backup", "two")),
                Options(("backup-live", "one"), ("restore", "two")),
                Options(("backup-live", "one"), ("relocate-from", "/old"), ("relocate-to", "/new")),
                Options(("backup-live", "one"), ("apply-relocation", "false")),
                Options(("backup-live", "")),
                Options(("relocate-from", "/old")),
                Options(("relocate-from", "/old"), ("relocate-to", "/new"), ("apply-relocation", "invalid"))
            })
                Reject(() => StateMaintenance.ValidateOptions(options, untouched));
            Check(!Directory.EnumerateFileSystemEntries(root).Any(), "Invalid CLI options mutated state or lock files.");
            var source = Path.Combine(root, "live");
            var backup = Path.Combine(root, "backup");
            var restored = Path.Combine(root, "restored");
            var comics = Path.Combine(root, "comics");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(comics);
            using var lease = StateMaintenance.Acquire(source);
            Reject(() => StateMaintenance.Acquire(source).Dispose());
            Reject(() => StateMaintenance.Acquire(Path.Combine(source, ".")).Dispose());
            Check(!StateMaintenance.Run(Options(), source), "Normal startup was treated as maintenance.");
            Check(StateMaintenance.ValidateOptions(Options(("backup-live", backup)), source), "Live backup did not bypass the lifetime lease.");
            Check(!StateMaintenance.ValidateOptions(Options(("backup", backup)), source), "Offline backup bypassed the lifetime lease.");
            using (StateMaintenance.AcquireBackupGate(source))
            {
                Reject(() => StateMaintenance.AcquireBackupGate(Path.Combine(source, ".")).Dispose(), typeof(IOException));
                Reject(() => StateMaintenance.Run(Options(("backup-live", backup)), source), typeof(IOException));
                Reject(() => StateMaintenance.Run(Options(("backup", backup)), source), typeof(IOException));
                Reject(() => StateMaintenance.Run(Options(("restore", backup)), source), typeof(IOException));
                Reject(() => RelocateCatalog.Run(Options(("relocate-from", Path.Combine(root, "old")),
                    ("relocate-to", comics)), source, [comics]), typeof(IOException));
            }
            Check(!Directory.Exists(backup), "Gate contention left a backup destination.");
            var database = Path.Combine(source, "panelhaven.sqlite");
            var library = new LibraryStore(database, [comics]);
            var accounts = new Accounts(database);
            var owner = accounts.Create("backup-reader", "original-backup-password", "reader");
            var other = accounts.Create("another-reader", "another-backup-password", "reader");
            using (var archive = ZipFile.Open(Path.Combine(comics, "book.cbz"), ZipArchiveMode.Create))
            {
                for (var page = 1; page <= 2; page++)
                {
                    using var stream = archive.CreateEntry($"{page}.png").Open();
                    stream.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
                }
            }
            Check(library.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Backup comic fixture did not import.");
            var book = library.ListBooks(owner.Id, null).Items.Single();
            var pageOrder = library.SavePageOrder(book.Id, new(library.GetPageOrder(book.Id).Revision, [1, 0]));
            library.SaveProgress(owner.Id, book.Id, new ReadingProgress(1, true));
            library.SaveRating(owner.Id, book.Id, new RatingEdit(5));
            var folder = library.CreateListFolder(owner.Id, new ListFolderEdit("Private folder"));
            var childFolder = library.CreateListFolder(owner.Id, new ListFolderEdit("Nested folder", folder.Id));
            var list = library.CreateList(owner.Id, new ListEdit("Private list", "manual", "all", [], childFolder.Id));
            library.AddListBooks(owner.Id, list.Id, [book.Id]);
            var protectedValue = Protector(source).Protect("original-cookie-key-material");
            Directory.CreateDirectory(Path.Combine(source, "thumbnails"));
            File.WriteAllText(Path.Combine(source, "thumbnails", "cache"), "regenerable");

            // Keep WAL state open: copying only the main database would lose this committed update.
            using (var wal = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                wal.Open();
                using var command = wal.CreateCommand();
                command.CommandText = "PRAGMA wal_autocheckpoint=0; UPDATE progress SET completed=0 WHERE user_id=$user";
                command.Parameters.AddWithValue("$user", owner.Id);
                command.ExecuteNonQuery();
                Check(File.Exists(database + "-wal") && new FileInfo(database + "-wal").Length > 0, "Fixture has no committed WAL state.");
                var sourceHash = SHA256.HashData(File.ReadAllBytes(database));
                var walHash = SHA256.HashData(File.ReadAllBytes(database + "-wal"));
                var keyHashes = Directory.EnumerateFiles(Path.Combine(source, "keys"))
                    .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
                Check(StateMaintenance.Run(Options(("backup-live", backup)), source), "Live backup was not handled while the server lease was held.");
                Check(SHA256.HashData(File.ReadAllBytes(database)).SequenceEqual(sourceHash)
                    && SHA256.HashData(File.ReadAllBytes(database + "-wal")).SequenceEqual(walHash)
                    && keyHashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)),
                    "Live backup changed source state.");
            }
            Check(StateMaintenance.Run(Options(("backup", Path.Combine(root, "offline-backup"))), source), "Offline backup was not handled.");
            Check(Directory.EnumerateFileSystemEntries(backup).Select(Path.GetFileName).Order().SequenceEqual(new[] { "keys", "panelhaven.sqlite" }),
                "Backup included transient or regenerable data.");
            Check(Directory.EnumerateFiles(Path.Combine(backup, "keys")).Any(), "Backup omitted the key ring.");
            if (!OperatingSystem.IsWindows())
            {
                Check(File.GetUnixFileMode(backup) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Backup directory is not private.");
                foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
                    Check(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Backup file is not private.");
            }
            var backupHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(backup, "panelhaven.sqlite")));
            Directory.CreateDirectory(restored);
            using (StateMaintenance.Acquire(restored))
            {
                Check(StateMaintenance.Run(Options(("restore", backup)), restored), "Restore was not handled.");
                var restoredAccounts = new Accounts(Path.Combine(restored, "panelhaven.sqlite"));
                var authenticated = restoredAccounts.Authenticate("backup-reader", "original-backup-password");
                Check(authenticated?.Id == owner.Id && authenticated.Role == "reader", "Restored original credentials do not work.");
                Check(restoredAccounts.Authenticate("backup-reader", "incorrect-password") is null, "Restore bypassed authentication.");
                Check(restoredAccounts.ValidateSession(Accounts.Principal(owner))?.Id == owner.Id, "Restore lost the original session identity.");
                var restoredLibrary = new LibraryStore(Path.Combine(restored, "panelhaven.sqlite"), [comics]);
                var restoredOrder = restoredLibrary.GetPageOrder(book.Id);
                Check(restoredOrder.Custom && restoredOrder.Revision == pageOrder.Revision && restoredOrder.Pages.SequenceEqual([1, 0]),
                    "Restore lost shared page order or its stale-edit generation.");
                var restoredBook = restoredLibrary.GetBook(owner.Id, book.Id);
                Check(restoredBook.CurrentPage == 1 && !restoredBook.Completed && restoredBook.PersonalRating == 5,
                    "Restore lost committed WAL progress or personal ratings.");
                var restoredList = restoredLibrary.GetList(owner.Id, list.Id);
                var restoredFolders = restoredLibrary.GetListFolders(owner.Id);
                Check(restoredList.BookIds!.SequenceEqual([book.Id]) && restoredList.FolderId == childFolder.Id
                    && restoredFolders.Count == 2 && restoredFolders.Contains(folder) && restoredFolders.Contains(childFolder),
                    "Restore lost private list membership or the folder hierarchy.");
                Check(restoredLibrary.GetLists(other.Id).All(list => list.Name == "Recently added") && restoredLibrary.GetBook(other.Id, book.Id).PersonalRating == 0,
                    "Restore crossed private account boundaries.");
                Check(Protector(restored).Unprotect(protectedValue) == "original-cookie-key-material", "Restored keys cannot decrypt original protected data.");
                Reject(() => StateMaintenance.Run(Options(("restore", backup)), restored));
            }
            Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(backup, "panelhaven.sqlite"))).SequenceEqual(backupHash),
                "Restore changed the source backup.");
            using (StateMaintenance.Acquire(restored)) { }
            Reject(() => StateMaintenance.Run(Options(("backup", backup)), source));
            Reject(() => StateMaintenance.Run(Options(("backup", source)), source));
            Reject(() => StateMaintenance.Run(Options(("backup", Path.Combine(source, "nested"))), source));
            Reject(() => StateMaintenance.Run(Options(("backup", root)), source));
            Reject(() => StateMaintenance.Run(Options(("backup", source + ".state.lock")), source));
            Reject(() => StateMaintenance.Run(Options(("backup-live", source + ".backup.lock")), source));
            Reject(() => StateMaintenance.Run(Options(("backup-live", backup)), source));
            Reject(() => StateMaintenance.Run(Options(("backup-live", Path.Combine(source, "nested"))), source));
            Reject(() => StateMaintenance.Run(Options(("backup", "")), source));
            Reject(() => StateMaintenance.Run(Options(("backup", backup), ("restore", backup)), source));
            Reject(() => StateMaintenance.Run(Options(("backup", backup), ("relocate-from", comics)), source));

            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            using (StateMaintenance.Acquire(empty))
            {
                Reject(() => StateMaintenance.Run(Options(("restore", root)), empty));
                foreach (var defect in new[] { "corrupt", "schema", "xml", "dtd", "unexpected", "missing" })
                {
                    var invalid = Path.Combine(root, defect);
                    CopyBackup(backup, invalid);
                    var invalidDatabase = Path.Combine(invalid, "panelhaven.sqlite");
                    var invalidKey = Directory.EnumerateFiles(Path.Combine(invalid, "keys")).First();
                    switch (defect)
                    {
                        case "corrupt": File.WriteAllText(invalidDatabase, "not SQLite"); break;
                        case "schema":
                            File.Delete(invalidDatabase);
                            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = invalidDatabase, Pooling = false }.ToString()))
                            {
                                db.Open();
                                using var command = db.CreateCommand();
                                command.CommandText = "CREATE TABLE unrelated(value TEXT)";
                                command.ExecuteNonQuery();
                            }
                            break;
                        case "xml": File.WriteAllText(invalidKey, "<key>"); break;
                        case "dtd": File.WriteAllText(invalidKey, "<!DOCTYPE key [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><key>&secret;</key>"); break;
                        case "unexpected": File.WriteAllText(Path.Combine(invalid, "panelhaven.sqlite-wal"), "transient"); break;
                        case "missing": Directory.Delete(Path.Combine(invalid, "keys"), recursive: true); break;
                    }
                    Reject(() => StateMaintenance.Run(Options(("restore", invalid)), empty));
                    Check(!Directory.EnumerateFileSystemEntries(empty).Any(), "Rejected restore left partial state.");
                }
                if (!OperatingSystem.IsWindows())
                {
                    var alias = Path.Combine(root, "alias");
                    Directory.CreateSymbolicLink(alias, source);
                    Reject(() => StateMaintenance.Acquire(alias).Dispose());
                    Reject(() => StateMaintenance.Run(Options(("backup", Path.Combine(alias, "nested"))), source));
                    Reject(() => StateMaintenance.Run(Options(("backup-live", Path.Combine(alias, "nested"))), source));
                    Directory.Delete(alias);
                    var invalid = Path.Combine(root, "linked-key");
                    CopyBackup(backup, invalid);
                    var key = Directory.EnumerateFiles(Path.Combine(invalid, "keys")).First();
                    File.Delete(key);
                    File.CreateSymbolicLink(key, Directory.EnumerateFiles(Path.Combine(backup, "keys")).First());
                    Reject(() => StateMaintenance.Run(Options(("restore", invalid)), empty));
                    File.Delete(key);
                    Check(!Directory.EnumerateFileSystemEntries(empty).Any(), "Unsafe key left partial restored state.");
                }
            }
            if (OperatingSystem.IsLinux())
            {
                foreach (var defect in new[] { "database", "key", "-wal", "-shm", "-journal" })
                {
                    var invalid = Path.Combine(root, "fifo-" + defect);
                    CopyBackup(backup, invalid);
                    var special = defect == "key" ? Directory.EnumerateFiles(Path.Combine(invalid, "keys")).First()
                        : Path.Combine(invalid, "panelhaven.sqlite" + (defect == "database" ? "" : defect));
                    File.Delete(special);
                    Check(MakeFifo(special, 0x180) == 0, "Could not create maintenance FIFO fixture.");
                    foreach (var operation in new[] { "backup", "backup-live", "restore" })
                    {
                        var target = Path.Combine(root, "fifo-target");
                        if (operation == "restore") Directory.CreateDirectory(target);
                        var configuration = Options((operation, operation == "restore" ? invalid : target));
                        var rejection = Task.Run(() => Reject(() => StateMaintenance.Run(configuration,
                            operation == "restore" ? target : invalid)));
                        Check(rejection.Wait(TimeSpan.FromSeconds(5)), $"Maintenance blocked opening a {defect} FIFO.");
                        Check(!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any(),
                            "Special-file rejection published partial state.");
                        if (Directory.Exists(target)) Directory.Delete(target);
                        Check(!Directory.EnumerateDirectories(root, ".panelhaven-state-*").Any(),
                            "Special-file rejection leaked staged private data.");
                    }
                    File.Delete(special);
                }
            }
            // A failed backup is not published, and both maintenance paths clean their staging directory.
            var failedBackup = Path.Combine(root, "failed-backup");
            var sourceKey = Directory.EnumerateFiles(Path.Combine(source, "keys")).First();
            File.WriteAllText(sourceKey, "<invalid/>");
            Reject(() => StateMaintenance.Run(Options(("backup", failedBackup)), source));
            Reject(() => StateMaintenance.Run(Options(("backup-live", failedBackup)), source));
            Check(!Directory.Exists(failedBackup) && !Directory.EnumerateDirectories(root, ".panelhaven-state-*").Any(),
                "Failed maintenance published a destination or leaked staged private data.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
