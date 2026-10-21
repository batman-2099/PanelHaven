using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PanelHaven.Web;

public static class HostManagementChecks
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { return; }
            throw new Exception("Unsafe host management operation was accepted.");
        }
        static IDataProtector Protector(string path) => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(path, "keys")),
            options => options.SetApplicationName("PanelHaven.Web")).CreateProtector("host-relocation-check");

        var root = Path.Combine(Path.GetTempPath(), "panelhaven-host-check-" + Guid.NewGuid().ToString("N"));
        var oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "relocated");
            var settingsPath = Path.Combine(root, "settings.json");
            var libraries = Path.Combine(root, "comics");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(libraries);
            var database = Path.Combine(source, "panelhaven.sqlite");
            _ = new LibraryStore(database, [libraries]);
            var accounts = new Accounts(database);
            var owner = accounts.Create("relocation-reader", "relocation-check-password", "reader");
            var protectedValue = Protector(source).Protect("durable-cookie-secret");
            var vault = Path.Combine(source, "archive-backups", "book", "version");
            Directory.CreateDirectory(vault);
            File.WriteAllText(Path.Combine(vault, "original.cbz"), "opaque original archive");
            File.WriteAllText(Path.Combine(source, "durable-state"), "all state, not only the backup scope");
            var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = source, ["ServerSettingsPath"] = settingsPath, ["Libraries:0"] = libraries
            }).Build();
            var store = ServerSettings.Load(options, root);
            Reject(() => DataRelocation.Validate(store.Document, source, settingsPath));
            Reject(() => DataRelocation.Validate(store.Document, Path.Combine(source, "nested"), settingsPath));
            Reject(() => DataRelocation.Validate(store.Document, Path.Combine(libraries, "nested"), settingsPath));
            Reject(() => DataRelocation.Validate(store.Document, settingsPath, settingsPath));
            Reject(() => DataRelocation.Validate(store.Document, Path.Combine(root, "missing", "new"), settingsPath));
            using (StateMaintenance.Acquire(source)) Reject(() => DataRelocation.Copy(source, destination));
            Check(!Directory.Exists(destination), "Lease contention published relocation state.");
            store.Save(store.Document with { PendingDataPath = destination });
            var queued = store.Document;
            var queuedFile = File.ReadAllText(settingsPath);
            foreach (var busyPath in new[] { source, destination })
            {
                using (StateMaintenance.Acquire(busyPath)) Reject(() => DataRelocation.Execute(store));
                Check(store.Document == queued && File.ReadAllText(settingsPath) == queuedFile && !Directory.Exists(destination),
                    "Startup lease contention cleared or changed the queued relocation instead of allowing handoff retry.");
            }
            DataRelocation.Execute(store);
            Check(store.Document.Settings.DataPath == destination && store.Document.PendingDataPath is null
                && store.Document.RelocationError is null, "Verified relocation was not committed.");
            Check(Directory.Exists(source) && File.ReadAllText(Path.Combine(vault, "original.cbz")) == "opaque original archive",
                "Relocation deleted or changed the recovery source.");
            Check(File.ReadAllText(Path.Combine(destination, "archive-backups", "book", "version", "original.cbz")) == "opaque original archive"
                && File.ReadAllText(Path.Combine(destination, "durable-state")) == "all state, not only the backup scope",
                "Relocation omitted durable state or the original-archive vault.");
            Check(Protector(destination).Unprotect(protectedValue) == "durable-cookie-secret", "Relocation lost key material.");
            Check(new Accounts(Path.Combine(destination, "panelhaven.sqlite")).Authenticate("relocation-reader", "relocation-check-password")?.Id == owner.Id,
                "Relocated database failed account recovery.");
            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                    .Where(file => !file.EndsWith("-wal") && !file.EndsWith("-shm")))
                    Check(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Relocation exposed private state.");
                var badTarget = Path.Combine(root, "failed");
                var link = Path.Combine(destination, "unsafe-link");
                File.CreateSymbolicLink(link, Path.Combine(source, "durable-state"));
                store.Save(store.Document with { PendingDataPath = badTarget });
                DataRelocation.Execute(store);
                Check(store.Document.Settings.DataPath == destination && store.Document.PendingDataPath is null
                    && store.Document.RelocationError is not null && !Directory.Exists(badTarget), "Failed relocation changed active state or will retry forever.");
                File.Delete(link);
                Check(!Directory.EnumerateDirectories(root, ".panelhaven-relocation-*").Any(), "Failed relocation leaked its staging copy.");
                if (OperatingSystem.IsLinux())
                {
                    var fifo = Path.Combine(destination, "fifo");
                    Check(MakeFifo(fifo, 0x180) == 0, "Could not create the special-file fixture.");
                    Reject(() => DataRelocation.Copy(destination, badTarget));
                    File.Delete(fifo);
                    Check(!Directory.Exists(badTarget), "A special state file was copied.");
                }
            }
            var occupied = Path.Combine(root, "occupied");
            Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "keep"), "untouched");
            store.Save(store.Document with { PendingDataPath = occupied });
            DataRelocation.Execute(store);
            Check(store.Document.Settings.DataPath == destination && File.ReadAllText(Path.Combine(occupied, "keep")) == "untouched",
                "A failed move clobbered existing content.");

            Reject(() => ServerHost.ValidateBackup(store.Document.Settings with { Backup = new(true, destination, "03:00", 14) }, settingsPath));
            Reject(() => ServerHost.ValidateBackup(store.Document.Settings with { Backup = new(true, root, "03:00", 14) }, settingsPath));
            Reject(() => ServerHost.ValidateBackup(store.Document.Settings with { Backup = new(true, Path.Combine(root, "backups"), "25:00", 14) }, settingsPath));
            ServerHost.ValidateBackup(store.Document.Settings with { Backup = new(true, Path.Combine(root, "backups"), "04:12", 3) }, settingsPath);
            Check(ServerHost.Quote("/tmp/a b;$HOME%h\\\"") == "\"/tmp/a b;$$HOME%%h\\\\\\\"\"", "systemd argument escaping allows expansion or loses literals.");
            Reject(() => ServerHost.Quote("/tmp/path\nExecStart=/bin/false"));
            var units = Path.Combine(root, "config", "systemd", "user");
            Directory.CreateDirectory(units);
            var unit = Path.Combine(units, "panelhaven.service");
            File.WriteAllText(unit, "[Service]\nExecStart=/bin/true\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(unit, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Reject(() => ServerHost.RequireOwned("panelhaven.service", settingsPath));
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsPath)));
            File.WriteAllText(unit, "# Managed by Panel Haven server settings v1\n# Settings SHA256: " + identity + "\n[Service]\nExecStart=/bin/true\n");
            ServerHost.RequireOwned("panelhaven.service", settingsPath);
            Reject(() => ServerHost.RequireOwned("panelhaven.service", Path.Combine(root, "other-settings.json")));
            Check(File.ReadAllText(unit).Contains(identity), "Ownership checking changed an unrelated unit.");
            if (OperatingSystem.IsLinux())
            {
                var legacyService = Path.Combine(units, "panelhaven-backup.service");
                var legacyTimer = Path.Combine(units, "panelhaven-backup.timer");
                File.WriteAllText(legacyService, "[Unit]\nDescription=Panel Haven live state backup\n\n[Service]\nType=oneshot\nEnvironmentFile=%h/.config/panelhaven/backup.env\nExecStart=/usr/bin/python3 %h/.local/lib/panelhaven/backup.py\nUMask=0077\n");
                File.WriteAllText(legacyTimer, "[Unit]\nDescription=Daily Panel Haven live state backup\n\n[Timer]\nOnCalendar=*-*-* 03:00:00\nPersistent=true\n\n[Install]\nWantedBy=timers.target\n");
                File.SetUnixFileMode(legacyService, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(legacyTimer, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var environment = Path.Combine(root, "config", "panelhaven", "backup.env");
                Directory.CreateDirectory(Path.GetDirectoryName(environment)!);
                File.WriteAllText(environment, $"PANELHAVEN_DATA_PATH={source}\nPANELHAVEN_BACKUP_ROOT={root}/backups\nPANELHAVEN_BACKUP_KEEP=9\n");
                Check(ServerHost.ReadExistingBackup(source)?.Keep == 9, "Matching legacy backup configuration was not imported.");
                Check(ServerHost.ReadExistingBackup(destination) is null, "Another installation imported the legacy backup configuration.");
                ServerHost.RequireOwned("panelhaven-backup.service", settingsPath, source);
                Reject(() => ServerHost.RequireOwned("panelhaven-backup.service", settingsPath, destination));
                // Exercise real timer ownership rejection without contacting the user's systemd manager.
                File.WriteAllText(legacyService, "[Service]\nExecStart=/bin/false\n");
                File.WriteAllText(legacyTimer, "[Timer]\nOnCalendar=daily\n");
                var host = new ServerHost(settingsPath, source);
                host.ApplyBackupAsync(new(true, Path.Combine(root, "backups"), "03:00", 3)).GetAwaiter().GetResult();
                var serviceStatus = new ServerHostStatus(true, true, true, true, "application status");
                var readBackupStatus = typeof(ServerHost).GetMethod("ReadBackupStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var status = ((Task<ServerHostStatus>)readBackupStatus.Invoke(host, [serviceStatus])!).GetAwaiter().GetResult();
                Check(status.Supported && status.Installed && status.Running && status.Enabled
                    && status.Message == serviceStatus.Message && !status.BackupEnabled,
                    "A foreign backup timer disabled application service controls.");
                Check(status.BackupMessage?.Contains("panelhaven-backup.service") == true
                    && status.BackupMessage.Contains("panelhaven-backup.timer"),
                    "Backup status lost either the apply failure or timer preflight diagnostic.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
