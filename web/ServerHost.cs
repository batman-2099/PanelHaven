using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace PanelHaven.Web;

public sealed record ServerHostStatus(bool Supported, bool Installed, bool Running, bool Enabled, string? Message,
    bool BackupEnabled = false, string? BackupMessage = null);

public sealed class ServerHost(string settingsPath, string bootstrapDataPath)
{
    private const string Owner = "# Managed by Panel Haven server settings v1";
    private const string AppUnit = "panelhaven.service";
    private const string BackupUnit = "panelhaven-backup.service";
    private const string TimerUnit = "panelhaven-backup.timer";
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    private const string LegacyService = "[Unit]\nDescription=Panel Haven live state backup\n\n[Service]\nType=oneshot\nEnvironmentFile=%h/.config/panelhaven/backup.env\nExecStart=/usr/bin/python3 %h/.local/lib/panelhaven/backup.py\nUMask=0077";
    private const string LegacyTimer = "[Unit]\nDescription=Daily Panel Haven live state backup\n\n[Timer]\nOnCalendar=*-*-* 03:00:00\nPersistent=true\n\n[Install]\nWantedBy=timers.target";
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();
    private string? backupError;
    private static string ConfigDirectory => Path.GetFullPath(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));
    private static string UnitDirectory => Path.Combine(ConfigDirectory, "systemd", "user");
    private static string UnitPath(string name) => Path.Combine(UnitDirectory, name);
    private static string Ownership(string path) => Owner + "\n# Settings SHA256: "
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));

    public static BackupSettings? ReadExistingBackup(string dataPath)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var service = UnitPath(BackupUnit);
        var timer = UnitPath(TimerUnit);
        if (!File.Exists(service) || !File.Exists(timer)) return null;
        try
        {
            RequireOwned(BackupUnit);
            RequireOwned(TimerUnit);
            if (File.ReadAllText(service).Replace("\r\n", "\n").TrimEnd() != LegacyService
                || File.ReadAllText(timer).Replace("\r\n", "\n").TrimEnd() != LegacyTimer) return null;
            var values = LegacyValues(dataPath);
            if (values is null || !values.TryGetValue("PANELHAVEN_BACKUP_ROOT", out var destination)) return null;
            var keep = values.TryGetValue("PANELHAVEN_BACKUP_KEEP", out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : 14;
            var enabled = File.Exists(Path.Combine(UnitDirectory, "timers.target.wants", TimerUnit));
            return new(enabled, destination, "03:00", keep);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine("Existing backup configuration was not imported: " + error.Message);
            return null;
        }
    }

    private static Dictionary<string, string>? LegacyValues(string dataPath)
    {
        var environment = DataRelocation.SafePath(Path.Combine(ConfigDirectory, "panelhaven", "backup.env"));
        if (!File.Exists(environment)) return null;
        var values = File.ReadAllLines(environment).Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim().Trim('"', '\''), StringComparer.Ordinal);
        return values.TryGetValue("PANELHAVEN_DATA_PATH", out var original)
            && DataRelocation.SafePath(original) == DataRelocation.SafePath(dataPath) ? values : null;
    }

    public static void ValidateBackup(ServerSettingsValues settings, string settingsPath)
    {
        var backup = settings.Backup;
        if (!TimeOnly.TryParseExact(backup.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || backup.Keep is < 1 or > 3650)
            throw new ArgumentException("Backups require a daily HH:mm time and retention between 1 and 3650.");
        if (string.IsNullOrWhiteSpace(backup.Destination))
        {
            if (backup.Enabled) throw new ArgumentException("Enabled backups require a destination.");
            return;
        }
        var destination = DataRelocation.SafePath(backup.Destination);
        var excluded = settings.Libraries.Concat(new[] { settings.ArchivePath, settings.UnsortedPath }.OfType<string>())
            .Concat(new[] { settings.DataPath, settings.DataPath + ".state.lock", settings.DataPath + ".backup.lock",
                settingsPath, settingsPath + ".previous", AppContext.BaseDirectory });
        if (destination == Path.GetPathRoot(destination)
            || destination == Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            || excluded.Any(path => DataRelocation.Overlaps(destination, DataRelocation.SafePath(path))))
            throw new ArgumentException("Use a dedicated backup directory separate from state, libraries, settings and application files.");
        if (File.Exists(destination)) throw new ArgumentException("The backup destination must be a directory.");
        if (!Directory.Exists(destination) && !Directory.Exists(Path.GetDirectoryName(destination)))
            throw new ArgumentException("The backup destination parent must already exist.");
        if (OperatingSystem.IsLinux() && Directory.Exists(destination)
            && (File.GetUnixFileMode(destination) != PrivateDirectory
                || LibraryStore.ArchiveStatx(-100, destination, 0x100, 0x9, out var stat) != 0
                || (stat.Mask & 0x9) != 0x9 || (stat.Mode & 0xf000) != 0x4000 || stat.Owner != EffectiveUserId()))
            throw new ArgumentException("An existing backup destination must belong to this user and be private (permissions 0700).");
    }

    public async Task<ServerHostStatus> GetStatusAsync()
    {
        if (!OperatingSystem.IsLinux()) return new(false, false, false, false, "Host management requires Linux user systemd.");
        try
        {
            var state = await PreflightAsync(AppUnit);
            var installed = File.Exists(UnitPath(AppUnit));
            var timer = await PreflightAsync(TimerUnit);
            var enabled = state.GetValueOrDefault("UnitFileState") == "enabled";
            var timerEnabled = timer.GetValueOrDefault("UnitFileState") == "enabled" && timer.GetValueOrDefault("ActiveState") == "active";
            return new(true, installed, state.GetValueOrDefault("ActiveState") == "active", enabled,
                backupError ?? "User service startup requires a login session or administrator-enabled linger for boot startup.", timerEnabled, backupError);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, false, false, false, error.Message, false, backupError);
        }
    }

    public async Task SetStartupAsync(bool enabled)
    {
        RequireLinux();
        await PreflightAsync(AppUnit);
        var command = LaunchCommand();
        var text = $"{Owner}\n[Unit]\nDescription=Panel Haven comic library\nStartLimitIntervalSec=0\n\n[Service]\nType=simple\nExecStartPre=/usr/bin/test -f {Quote(Path.GetFullPath(settingsPath))}\nExecStart={command} --contentRoot {Quote(AppContext.BaseDirectory)} --ServerSettingsPath {Quote(Path.GetFullPath(settingsPath))} --DataPath {Quote(Path.GetFullPath(bootstrapDataPath))}\nRestart=on-failure\nRestartSec=3\nTimeoutStopSec=60\nUMask=0077\n\n[Install]\nWantedBy=default.target\n";
        WriteUnit(AppUnit, text);
        await ControlAsync("daemon-reload");
        await ControlAsync(enabled ? "enable" : "disable", AppUnit);
    }

    public async Task RestartAsync()
    {
        RequireLinux();
        await PreflightAsync(AppUnit);
        if (!File.Exists(UnitPath(AppUnit))) throw new InvalidOperationException("Install the managed user service before restarting.");
        await ControlAsync("--no-block", "restart", AppUnit);
    }

    public async Task ApplyBackupAsync(BackupSettings backup)
    {
        backupError = null;
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                if (backup.Enabled) throw new InvalidOperationException("Scheduled backups require Linux user systemd.");
                return;
            }
            await PreflightAsync(BackupUnit);
            await PreflightAsync(TimerUnit);
            if (!backup.Enabled)
            {
                if (File.Exists(UnitPath(TimerUnit))) await ControlAsync("disable", "--now", TimerUnit);
                return;
            }
            if (!TimeOnly.TryParseExact(backup.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || backup.Keep is < 1 or > 3650)
                throw new ArgumentException("Invalid backup schedule or retention.");
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "backup.py");
            if (!File.Exists(script) || !File.Exists("/usr/bin/python3"))
                throw new IOException("Scheduled backups require the published scripts/backup.py and /usr/bin/python3.");
            var runtime = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application executable.");
            var assembly = Assembly.GetEntryAssembly()?.Location;
            var command = $"/usr/bin/python3 {Quote(script)} --settings {Quote(Path.GetFullPath(settingsPath))} --runtime {Quote(runtime)}";
            if (Path.GetFileNameWithoutExtension(runtime) == "dotnet")
                command += " --assembly " + Quote(assembly ?? throw new InvalidOperationException("Cannot determine the application assembly."));
            WriteUnit(BackupUnit, $"{Owner}\n[Unit]\nDescription=Panel Haven live state backup\n\n[Service]\nType=oneshot\nExecStart={command}\nUMask=0077\n");
            WriteUnit(TimerUnit, $"{Owner}\n[Unit]\nDescription=Daily Panel Haven live state backup\n\n[Timer]\nOnCalendar=*-*-* {backup.Time}:00\nPersistent=true\n\n[Install]\nWantedBy=timers.target\n");
            await ControlAsync("daemon-reload");
            await ControlAsync("enable", TimerUnit);
            await ControlAsync("restart", TimerUnit);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            backupError = "Saved backup configuration is not applied: " + error.Message;
        }
    }

    // systemd parses ExecStart itself: no shell, and neither specifiers nor environment references may expand user paths.
    public static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl)) throw new ArgumentException("Service arguments cannot contain control characters.");
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%");
        return "\"" + escaped.Replace("$", "$$") + "\"";
    }

    private static string LaunchCommand()
    {
        var runtime = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application executable.");
        var command = Quote(runtime);
        if (Path.GetFileNameWithoutExtension(runtime) == "dotnet")
            command += " " + Quote(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot determine the application assembly."));
        return command;
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Host management requires Linux user systemd.");
    }

    public static void RequireOwned(string name, string? expectedSettingsPath = null, string? legacyDataPath = null)
    {
        if (name is not (AppUnit or BackupUnit or TimerUnit)) throw new ArgumentException("Unknown managed unit.");
        var path = DataRelocation.SafePath(UnitPath(name));
        if (!File.Exists(path)) return;
        if (OperatingSystem.IsLinux() && (LibraryStore.ArchiveStatx(-100, path, 0x100, 0x9, out var stat) != 0
            || (stat.Mask & 0x9) != 0x9 || (stat.Mode & 0xf000) != 0x8000 || stat.Owner != EffectiveUserId()
            || (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0))
            throw new IOException("Managed units must be regular files owned and writable only by this user.");
        var content = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd();
        var legacy = name == BackupUnit && content == LegacyService || name == TimerUnit && content == LegacyTimer;
        if (legacy && expectedSettingsPath is not null)
            legacy = legacyDataPath is not null && LegacyValues(legacyDataPath) is not null;
        if (!content.StartsWith((expectedSettingsPath is null ? Owner : Ownership(expectedSettingsPath)) + "\n", StringComparison.Ordinal) && !legacy)
            throw new IOException("Refusing to replace an unrelated user unit: " + name);
    }

    private async Task<Dictionary<string, string>> PreflightAsync(string name)
    {
        RequireOwned(name, settingsPath, bootstrapDataPath);
        var state = await InspectAsync(name);
        if (!File.Exists(UnitPath(name)) && state.GetValueOrDefault("LoadState") != "not-found")
            throw new IOException("An unrelated unit is already loaded: " + name);
        var fragment = state.GetValueOrDefault("FragmentPath");
        if (!string.IsNullOrEmpty(fragment) && Path.GetFullPath(fragment) != UnitPath(name))
            throw new IOException("Refusing to replace a unit loaded from another location: " + name);
        if (!string.IsNullOrEmpty(state.GetValueOrDefault("DropInPaths")) || Directory.Exists(UnitPath(name) + ".d"))
            throw new IOException("Remove custom systemd drop-ins before managing " + name + ".");
        return state;
    }

    private void WriteUnit(string name, string content)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Host management requires Linux user systemd.");
        RequireOwned(name, settingsPath, bootstrapDataPath);
        content = Ownership(settingsPath) + content[Owner.Length..];
        DataRelocation.SafePath(UnitDirectory);
        Directory.CreateDirectory(UnitDirectory);
        var path = UnitPath(name);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile }))
            {
                file.Write(Encoding.UTF8.GetBytes(content));
                file.Flush(flushToDisk: true);
            }
            RequireOwned(name, settingsPath, bootstrapDataPath);
            File.Move(temporary, path, overwrite: true);
            LibraryStore.FlushArchivePath(UnitDirectory, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<Dictionary<string, string>> InspectAsync(string name)
    {
        var result = await ControlAsync("show", name, "--property=LoadState", "--property=ActiveState", "--property=UnitFileState",
            "--property=FragmentPath", "--property=DropInPaths");
        return result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
    }

    private static async Task<string> ControlAsync(params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/systemctl")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--user");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new IOException("The user systemd manager did not respond in time.");
        }
        var detail = (await error).Trim();
        var result = await output;
        if (process.ExitCode != 0 && !(arguments[0] == "show"
            && result.Split('\n').Contains("LoadState=not-found")))
            throw new IOException("User systemd command failed. " + detail);
        return result;
    }
}
