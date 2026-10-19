using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelHaven.Web;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BackupSettings(
    [property: JsonRequired] bool Enabled, [property: JsonRequired] string Destination,
    [property: JsonRequired] string Time, [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int Keep);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerSettingsValues(
    [property: JsonRequired] string DataPath, [property: JsonRequired] string[] Libraries,
    [property: JsonRequired] string[] Urls, [property: JsonRequired] bool RequireHttps,
    [property: JsonRequired] string[] AllowedHosts, [property: JsonRequired] string[] KnownProxies,
    [property: JsonRequired] string? CertificatePath, [property: JsonRequired] string? CertificateKeyPath,
    [property: JsonRequired] BackupSettings Backup, string? ArchivePath = null, string? UnsortedPath = null);
public sealed record ServerSettingsDocument(
    [property: JsonRequired] long Revision, [property: JsonRequired] ServerSettingsValues Settings,
    [property: JsonRequired] string? ComicVineApiKey, [property: JsonRequired] string? CertificatePassword,
    string? PendingDataPath = null, string? RelocationError = null);

public sealed class ServerSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ManagedKeys = ["DataPath", "Libraries", "ArchivePath", "UnsortedPath", "urls", "Deployment:RequireHttps",
        "Deployment:AllowedHosts", "Deployment:KnownProxies", "ComicVine:ApiKey", "Kestrel:Certificates:Default", "Backup"];
    public string Path { get; }
    public string RecoveryPath => Path + ".previous";
    public ServerSettingsDocument Document { get; private set; }
    public ServerSettingsDocument Active { get; private set; }
    public SemaphoreSlim MutationGate { get; } = new(1, 1);
    public bool Persisted => File.Exists(Path);
    public bool RestartRequired
    {
        get
        {
            var saved = Document.Settings;
            var active = Active.Settings;
            return Document.PendingDataPath is not null || saved.DataPath != active.DataPath || saved.RequireHttps != active.RequireHttps
                || saved.CertificatePath != active.CertificatePath || saved.CertificateKeyPath != active.CertificateKeyPath
                || saved.ArchivePath != active.ArchivePath || saved.UnsortedPath != active.UnsortedPath
                || !saved.Libraries.AsSpan().SequenceEqual(active.Libraries) || !saved.Urls.AsSpan().SequenceEqual(active.Urls)
                || !saved.AllowedHosts.AsSpan().SequenceEqual(active.AllowedHosts) || !saved.KnownProxies.AsSpan().SequenceEqual(active.KnownProxies)
                || Document.ComicVineApiKey != Active.ComicVineApiKey || Document.CertificatePassword != Active.CertificatePassword;
        }
    }

    private ServerSettings(string path, ServerSettingsDocument document)
    {
        Path = path;
        Document = document;
        Active = document;
    }

    public static bool IsMaintenance(IConfiguration configuration) =>
        new[] { "backup", "backup-live", "restore", "relocate-from", "relocate-to", "apply-relocation" }
            .Any(key => configuration[key] is not null);

    public static ServerSettings Load(IConfiguration configuration, string contentRoot)
    {
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
            throw new ArgumentException("Use managed listen URLs and the default TLS certificate instead of Kestrel:Endpoints before enabling server settings.");
        var dataPath = System.IO.Path.GetFullPath(configuration["DataPath"] ?? "data", contentRoot);
        var path = System.IO.Path.GetFullPath(configuration["ServerSettingsPath"] ?? dataPath + ".settings.json", contentRoot);
        DataRelocation.SafePath(path);
        ServerSettingsDocument document;
        if (File.Exists(path))
        {
            MakePrivate(path);
            try
            {
                if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Server settings file is too large.");
                document = JsonSerializer.Deserialize<ServerSettingsDocument>(File.ReadAllText(path), Json)
                    ?? throw new JsonException("Empty settings document.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Cannot read server settings. Restore the last-known file {path}.previous manually while the server is stopped.", exception);
            }
        }
        else
        {
            string[] List(string name) => configuration.GetSection(name).Get<string[]>() ?? [];
            var backup = (!configuration.GetSection("Backup").Exists() ? ServerHost.ReadExistingBackup(dataPath) : null)
                ?? new BackupSettings(configuration.GetValue<bool>("Backup:Enabled"),
                configuration["Backup:Destination"] ?? "", configuration["Backup:Time"] ?? "03:00", configuration.GetValue("Backup:Keep", 14));
            document = new(0, new(dataPath, List("Libraries").Select(value => System.IO.Path.GetFullPath(value, contentRoot)).ToArray(),
                (configuration["urls"] ?? "http://localhost:5000").Split(';', StringSplitOptions.RemoveEmptyEntries),
                configuration.GetValue<bool>("Deployment:RequireHttps"), List("Deployment:AllowedHosts"), List("Deployment:KnownProxies"),
                Absolute(configuration["Kestrel:Certificates:Default:Path"]), Absolute(configuration["Kestrel:Certificates:Default:KeyPath"]), backup,
                Absolute(configuration["ArchivePath"]), Absolute(configuration["UnsortedPath"])),
                configuration["ComicVine:ApiKey"], configuration["Kestrel:Certificates:Default:Password"]);
            string? Absolute(string? value) => string.IsNullOrEmpty(value) ? null : System.IO.Path.GetFullPath(value, contentRoot);
        }
        var store = new ServerSettings(path, document);
        store.Validate(document);
        return store;
    }

    public void Apply(ConfigurationManager configuration)
    {
        // Flatten once at startup so saved arrays replace, rather than merge with, environment/CLI indices.
        var values = configuration.AsEnumerable().Where(pair => !ManagedKeys.Any(key =>
            pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ConfigurationValues(Document)) values[pair.Key] = pair.Value;
        configuration.Sources.Clear();
        configuration.AddInMemoryCollection(values);
        Active = Document;
    }

    private static Dictionary<string, string?> ConfigurationValues(ServerSettingsDocument document)
    {
        var settings = document.Settings;
        var values = new Dictionary<string, string?>
        {
            ["DataPath"] = settings.DataPath, ["urls"] = string.Join(';', settings.Urls),
            ["ArchivePath"] = settings.ArchivePath, ["UnsortedPath"] = settings.UnsortedPath,
            ["Deployment:RequireHttps"] = settings.RequireHttps.ToString(), ["ComicVine:ApiKey"] = document.ComicVineApiKey,
            ["Kestrel:Certificates:Default:Path"] = settings.CertificatePath,
            ["Kestrel:Certificates:Default:KeyPath"] = settings.CertificateKeyPath,
            ["Kestrel:Certificates:Default:Password"] = document.CertificatePassword,
            ["Backup:Enabled"] = settings.Backup.Enabled.ToString(), ["Backup:Destination"] = settings.Backup.Destination,
            ["Backup:Time"] = settings.Backup.Time, ["Backup:Keep"] = settings.Backup.Keep.ToString(CultureInfo.InvariantCulture)
        };
        if (document.ComicVineApiKey is null) values.Remove("ComicVine:ApiKey");
        if (settings.CertificatePath is null) values.Remove("Kestrel:Certificates:Default:Path");
        if (settings.CertificateKeyPath is null) values.Remove("Kestrel:Certificates:Default:KeyPath");
        if (document.CertificatePassword is null) values.Remove("Kestrel:Certificates:Default:Password");
        Add("Libraries", settings.Libraries);
        Add("Deployment:AllowedHosts", settings.AllowedHosts);
        Add("Deployment:KnownProxies", settings.KnownProxies);
        void Add(string key, string[] items)
        {
            for (var index = 0; index < items.Length; index++) values[$"{key}:{index}"] = items[index];
        }
        return values;
    }

    public void RequireRevision(long revision)
    {
        if (revision != Document.Revision) throw new InvalidOperationException("Server settings changed. Reload them before trying again.");
    }

    public void Update(long expectedRevision, ServerSettingsValues settings, string? comicVineApiKey,
        bool clearComicVineApiKey, string? certificatePassword, bool clearCertificatePassword)
    {
        RequireRevision(expectedRevision);
        if (settings is null || settings.DataPath != Document.Settings.DataPath)
            throw new ArgumentException("Use data relocation to change the server data path.");
        if (Document.PendingDataPath is not null) throw new InvalidOperationException("Restart to finish pending data relocation before editing settings.");
        Save(Document with { Revision = Document.Revision + 1, Settings = settings,
            ComicVineApiKey = Secret(Document.ComicVineApiKey, comicVineApiKey, clearComicVineApiKey),
            CertificatePassword = Secret(Document.CertificatePassword, certificatePassword, clearCertificatePassword) });
    }

    private static string? Secret(string? current, string? value, bool clear)
    {
        if (clear && value is not null) throw new ArgumentException("A secret cannot be both set and cleared.");
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)))
            throw new ArgumentException("Secrets must be nonempty, at most 4096 characters, and contain no control characters.");
        return clear ? null : value ?? current;
    }

    public void Validate(ServerSettingsDocument document)
    {
        var settings = document.Settings ?? throw new ArgumentException("Settings are required.");
        if (document.Revision < 0) throw new ArgumentException("Invalid settings revision.");
        Absolute(settings.DataPath, "Data path");
        if (DataRelocation.Overlaps(settings.DataPath, Path)) throw new ArgumentException("ServerSettingsPath must be outside the movable data directory.");
        if (settings.Libraries is null || settings.Urls is null || settings.AllowedHosts is null || settings.KnownProxies is null
            || settings.Backup is null || settings.Backup.Destination is null || settings.Libraries.Length > 256 || settings.Urls.Length is < 1 or > 32
            || settings.AllowedHosts.Length > 256 || settings.KnownProxies.Length > 256)
            throw new ArgumentException("Invalid or oversized server settings lists.");
        foreach (var root in settings.Libraries) Absolute(root, "Library root");
        var specialRoots = new[] { settings.ArchivePath, settings.UnsortedPath }.OfType<string>().ToArray();
        var excluded = settings.Libraries.Concat(new[] { settings.DataPath, settings.DataPath + ".state.lock",
            settings.DataPath + ".backup.lock", Path, RecoveryPath, AppContext.BaseDirectory });
        for (var index = 0; index < specialRoots.Length; index++)
        {
            Absolute(specialRoots[index], "Archived and Unsorted paths");
            var root = DataRelocation.SafePath(specialRoots[index]);
            if (File.Exists(root)) throw new ArgumentException("Archived and Unsorted paths must be directories.");
            if (excluded.Concat(specialRoots.Take(index)).Any(path => DataRelocation.Overlaps(root, DataRelocation.SafePath(path))))
                throw new ArgumentException("Archived and Unsorted paths must be separate from each other, regular libraries, state, settings and application files.");
        }
        foreach (var url in settings.Urls)
        {
            if (string.IsNullOrWhiteSpace(url) || url != url.Trim() || url.Contains(';')
                || !Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port == 0)
                throw new ArgumentException("Listen URLs must be HTTP(S) origins with a nonzero port and no credentials, query or path.");
        }
        if (settings.AllowedHosts.Any(value => value is null) || settings.KnownProxies.Any(value => value is null))
            throw new ArgumentException("Host and proxy entries cannot be null.");
        try { DeploymentSecurity.Configure(new ServiceCollection(), new ConfigurationBuilder().AddInMemoryCollection(ConfigurationValues(document)).Build()); }
        catch (InvalidOperationException exception) { throw new ArgumentException(exception.Message); }
        if (settings.RequireHttps && settings.KnownProxies.Length == 0 && !settings.Urls.Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("HTTPS enforcement requires an HTTPS listener or an explicitly trusted reverse proxy.");
        if (settings.CertificatePath is null && settings.Urls.Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("HTTPS listen URLs require an explicit TLS certificate and private key.");
        if (settings.CertificatePath is not null) Absolute(settings.CertificatePath, "Certificate path");
        if (settings.CertificateKeyPath is not null) Absolute(settings.CertificateKeyPath, "Certificate key path");
        if (settings.CertificateKeyPath is not null && settings.CertificatePath is null)
            throw new ArgumentException("A certificate key requires a certificate path.");
        if (settings.CertificatePath is not null)
        {
            try
            {
                using var certificate = settings.CertificateKeyPath is null
                    ? X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, document.CertificatePassword)
                    : string.IsNullOrEmpty(document.CertificatePassword)
                        ? X509Certificate2.CreateFromPemFile(settings.CertificatePath, settings.CertificateKeyPath)
                        : X509Certificate2.CreateFromEncryptedPemFile(settings.CertificatePath, document.CertificatePassword, settings.CertificateKeyPath);
                if (!certificate.HasPrivateKey) throw new ArgumentException("TLS certificate must include a private key.");
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
            { throw new ArgumentException("Cannot load the TLS certificate and private key with the supplied password."); }
        }
        ServerHost.ValidateBackup(settings, Path);
    }

    public void Save(ServerSettingsDocument document)
    {
        Validate(document);
        DataRelocation.SafePath(Path);
        DataRelocation.SafePath(RecoveryPath);
        var parent = System.IO.Path.GetDirectoryName(Path)!;
        if (!Directory.Exists(parent)) throw new IOException("The server settings parent directory does not exist.");
        // Preserve the running configuration across any number of unverified, pre-restart saves.
        AtomicWrite(RecoveryPath, JsonSerializer.Serialize(Active with { PendingDataPath = null }, Json));
        AtomicWrite(Path, JsonSerializer.Serialize(document, Json));
        Document = document;
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                using (var writer = new StreamWriter(stream, leaveOpen: true)) { writer.Write(content); writer.Flush(); }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            LibraryStore.FlushArchivePath(System.IO.Path.GetDirectoryName(path)!, directory: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Absolute(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path)
            || System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)) != System.IO.Path.TrimEndingDirectorySeparator(path))
            throw new ArgumentException(name + " must be a normalized absolute path.");
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
