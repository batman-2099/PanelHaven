using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PanelHaven.Web;

internal static class ServerSettingsChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ConfigurationManager Configuration()
            {
                var configuration = new ConfigurationManager();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = Path.Combine(directory, "data"), ["ServerSettingsPath"] = Path.Combine(directory, "settings.json"),
                    ["Libraries:0"] = Path.Combine(directory, "library-a"), ["Libraries:1"] = Path.Combine(directory, "library-b"),
                    ["ArchivePath"] = "archived", ["UnsortedPath"] = "unsorted",
                    ["Deployment:AllowedHosts:0"] = "localhost", ["Deployment:AllowedHosts:1"] = "old.example.com",
                    ["ComicVine:ApiKey"] = "environment-secret", ["Kestrel:Certificates:Default:Password"] = "environment-password",
                    ["urls"] = "http://127.0.0.1:5099", ["Backup:Enabled"] = "false", ["Unmanaged:Value"] = "preserved"
                });
                return configuration;
            }
            using var configuration = Configuration();
            var store = ServerSettings.Load(configuration, directory);
            store.Apply(configuration);
            Check(!store.Persisted && !store.RestartRequired, "Initial settings were persisted or marked stale unexpectedly.");
            Check(store.Document.Settings.ArchivePath == Path.Combine(directory, "archived")
                && store.Document.Settings.UnsortedPath == Path.Combine(directory, "unsorted"),
                "Configured special roots were not normalized relative to the content root.");
            var reduced = store.Document.Settings with { Libraries = [Path.Combine(directory, "library-a")], AllowedHosts = [],
                ArchivePath = Path.Combine(directory, "archived-saved"), UnsortedPath = Path.Combine(directory, "unsorted-saved") };
            store.Update(0, reduced, null, false, null, false);
            Check(store.Document.ComicVineApiKey == "environment-secret" && store.Document.CertificatePassword == "environment-password",
                "An unrelated update discarded environment-provided secrets.");
            Check(store.RestartRequired && store.Document.Revision == 1, "A saved application change did not require restart.");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var previous = JsonSerializer.Deserialize<ServerSettingsDocument>(File.ReadAllText(store.RecoveryPath), json)!;
            Check(previous.Revision == 0 && previous.Settings.Libraries.Length == 2, "First save did not preserve the last-known configuration.");
            using var restartedConfiguration = Configuration();
            var restarted = ServerSettings.Load(restartedConfiguration, directory);
            restarted.Apply(restartedConfiguration);
            Check((restartedConfiguration.GetSection("Libraries").Get<string[]>() ?? []).Length == 1
                && !restartedConfiguration.GetSection("Deployment:AllowedHosts").GetChildren().Any()
                && restartedConfiguration["Unmanaged:Value"] == "preserved" && !restarted.RestartRequired,
                "Saved arrays merged old indexed tails or dropped unmanaged configuration.");
            Check(restartedConfiguration["ArchivePath"] == reduced.ArchivePath && restartedConfiguration["UnsortedPath"] == reduced.UnsortedPath,
                "Saved special roots did not replace startup configuration.");
            Reject<InvalidOperationException>(() => restarted.Update(0, reduced, null, false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced, "secret", true, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced, "\n", false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced with { DataPath = Path.Combine(directory, "other") }, null, false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced with { KnownProxies = ["127.0.0.1"] }, null, false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced with { Urls = ["http://localhost:0"] }, null, false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced with { Urls = ["https://localhost:5099"] }, null, false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced with { Backup = reduced.Backup with { Keep = 0 } }, null, false, null, false));
            foreach (var invalid in new[] { "", "relative", Path.Combine(directory, "parent", "..", "archived"), "bad\npath",
                reduced.Libraries[0], Path.Combine(reduced.Libraries[0], "child"), directory, reduced.DataPath,
                Path.Combine(reduced.DataPath, "child"), reduced.DataPath + ".state.lock", reduced.DataPath + ".backup.lock",
                store.Path, store.RecoveryPath, AppContext.BaseDirectory })
            {
                Reject<ArgumentException>(() => restarted.Update(1, reduced with { ArchivePath = invalid }, null, false, null, false));
                Reject<ArgumentException>(() => restarted.Update(1, reduced with { UnsortedPath = invalid }, null, false, null, false));
            }
            foreach (var overlap in new[] { reduced.ArchivePath!, Path.Combine(reduced.ArchivePath!, "child") })
                Reject<ArgumentException>(() => restarted.Update(1, reduced with { UnsortedPath = overlap }, null, false, null, false));
            Reject<ArgumentException>(() => restarted.Update(1, reduced with { ArchivePath = Path.Combine(reduced.UnsortedPath!, "child") }, null, false, null, false));
            foreach (var root in new[] { reduced.ArchivePath!, reduced.UnsortedPath! })
            {
                Reject<ArgumentException>(() => restarted.Update(1, reduced with { Backup = reduced.Backup with { Destination = root } }, null, false, null, false));
                Reject<ArgumentException>(() => DataRelocation.Validate(restarted.Document, root, restarted.Path));
            }
            Check(restarted.Document.Revision == 1, "Rejected input mutated the settings revision.");
            restarted.Update(1, reduced, "replacement-key", false, null, true);
            Check(restarted.Document.ComicVineApiKey == "replacement-key" && restarted.Document.CertificatePassword is null,
                "Secret set/clear did not persist.");
            previous = JsonSerializer.Deserialize<ServerSettingsDocument>(File.ReadAllText(restarted.RecoveryPath), json)!;
            Check(previous.Revision == 1 && previous.CertificatePassword == "environment-password", "Recovery did not retain the prior document.");
            var withoutSpecials = reduced with { ArchivePath = null, UnsortedPath = null };
            restarted.Update(2, withoutSpecials, null, true, null, false);
            previous = JsonSerializer.Deserialize<ServerSettingsDocument>(File.ReadAllText(restarted.RecoveryPath), json)!;
            Check(previous.Revision == 1 && previous.ComicVineApiKey == "environment-secret",
                "Repeated pre-restart saves replaced the known running recovery configuration.");
            using var clearedConfiguration = Configuration();
            var cleared = ServerSettings.Load(clearedConfiguration, directory);
            cleared.Apply(clearedConfiguration);
            Check(clearedConfiguration["ComicVine:ApiKey"] is null && clearedConfiguration["Kestrel:Certificates:Default:Password"] is null,
                "Cleared secrets fell back to startup environment values.");
            Check(clearedConfiguration["ArchivePath"] is null && clearedConfiguration["UnsortedPath"] is null,
                "Cleared special roots fell back to startup configuration.");
            cleared.Update(3, withoutSpecials with { Backup = reduced.Backup with { Time = "04:15" } }, null, false, null, false);
            Check(!cleared.RestartRequired, "Backup-only changes incorrectly require an application restart.");
            cleared.Update(4, cleared.Document.Settings with { ArchivePath = reduced.ArchivePath }, null, false, null, false);
            Check(cleared.RestartRequired, "Changing only Archived did not require restart.");
            cleared.Update(5, cleared.Document.Settings with { ArchivePath = null, UnsortedPath = reduced.UnsortedPath }, null, false, null, false);
            Check(cleared.RestartRequired, "Changing only Unsorted did not require restart.");
            cleared.Apply(clearedConfiguration);
            cleared.Update(6, cleared.Document.Settings with { UnsortedPath = null }, null, false, null, false);
            Check(cleared.RestartRequired, "Clearing a special root did not require restart.");
            foreach (var flag in new[] { "backup", "backup-live", "restore", "relocate-from", "relocate-to", "apply-relocation" })
            {
                using var maintenance = Configuration();
                maintenance[flag] = "requested";
                Check(ServerSettings.IsMaintenance(maintenance), "Maintenance command did not bypass managed settings: " + flag);
            }
            Reject<JsonException>(() => JsonSerializer.Deserialize<SaveServerSettingsRequest>("{\"currentPassword\":\"secret\"}", json));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ServerStartupRequest>("{\"expectedRevision\":1,\"enabled\":true,\"command\":\"arbitrary\"}", json));
            Reject<JsonException>(() => JsonSerializer.Deserialize<ServerStartupRequest>("{\"expectedRevision\":\"1\",\"enabled\":true}", json));
            using var endpointConfiguration = Configuration();
            endpointConfiguration["Kestrel:Endpoints:Hidden:Url"] = "http://localhost:5100";
            Reject<ArgumentException>(() => ServerSettings.Load(endpointConfiguration, directory));
            if (!OperatingSystem.IsWindows())
            {
                var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                Check(File.GetUnixFileMode(store.Path) == privateMode && File.GetUnixFileMode(store.RecoveryPath) == privateMode,
                    "Persisted settings or recovery secret file is not private.");
                var linked = Path.Combine(directory, "linked.json");
                File.CreateSymbolicLink(linked, store.Path);
                using var linkedConfiguration = Configuration();
                linkedConfiguration["ServerSettingsPath"] = linked;
                Reject<IOException>(() => ServerSettings.Load(linkedConfiguration, directory));
                var linkedRoot = Path.Combine(directory, "linked-root");
                Directory.CreateSymbolicLink(linkedRoot, directory);
                Reject<IOException>(() => cleared.Update(7, cleared.Document.Settings with { ArchivePath = linkedRoot }, null, false, null, false));
                Reject<IOException>(() => cleared.Update(7, cleared.Document.Settings with { UnsortedPath = linkedRoot }, null, false, null, false));
            }
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.Path))!;
            legacy["settings"]!.AsObject().Remove("archivePath");
            legacy["settings"]!.AsObject().Remove("unsortedPath");
            File.WriteAllText(store.Path, legacy.ToJsonString());
            using var legacyConfiguration = Configuration();
            var legacyStore = ServerSettings.Load(legacyConfiguration, directory);
            legacyStore.Apply(legacyConfiguration);
            Check(legacyStore.Document.Settings.ArchivePath is null && legacyStore.Document.Settings.UnsortedPath is null
                && legacyConfiguration["ArchivePath"] is null && legacyConfiguration["UnsortedPath"] is null && !legacyStore.RestartRequired,
                "Legacy settings without special-root fields did not load as unconfigured.");
            var savedBytes = File.ReadAllBytes(legacyStore.Path);
            var recoveryBytes = File.ReadAllBytes(legacyStore.RecoveryPath);
            var savedDocument = legacyStore.Document;
            var escapedRoots = Enumerable.Range(0, 200)
                .Select(index => Path.Combine(directory, $"library-{index}-" + new string('\'', 150))).ToArray();
            Reject<ArgumentException>(() => legacyStore.Update(savedDocument.Revision,
                savedDocument.Settings with { Libraries = escapedRoots }, null, false, null, false));
            Check(legacyStore.Document == savedDocument && File.ReadAllBytes(legacyStore.Path).SequenceEqual(savedBytes)
                && File.ReadAllBytes(legacyStore.RecoveryPath).SequenceEqual(recoveryBytes),
                "Oversized serialized settings changed the revision, saved file, or recovery file.");
            var boundary = savedDocument with { Revision = savedDocument.Revision + 1, RelocationError = "" };
            var persistedJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            var padding = 128 * 1024 - JsonSerializer.SerializeToUtf8Bytes(boundary, persistedJson).Length;
            boundary = boundary with { RelocationError = new string('x', padding) };
            legacyStore.Save(boundary);
            Check(new FileInfo(legacyStore.Path).Length == 128 * 1024, "Settings boundary fixture did not reach the file limit.");
            using var boundaryConfiguration = Configuration();
            var boundaryStore = ServerSettings.Load(boundaryConfiguration, directory);
            Check(boundaryStore.Document.Revision == boundary.Revision && boundaryStore.Document.RelocationError == boundary.RelocationError,
                "Accepted boundary-sized settings did not reload.");
            savedBytes = File.ReadAllBytes(legacyStore.Path);
            recoveryBytes = File.ReadAllBytes(legacyStore.RecoveryPath);
            Reject<ArgumentException>(() => legacyStore.Save(boundary with { RelocationError = boundary.RelocationError + "x" }));
            Check(legacyStore.Document == boundary && File.ReadAllBytes(legacyStore.Path).SequenceEqual(savedBytes)
                && File.ReadAllBytes(legacyStore.RecoveryPath).SequenceEqual(recoveryBytes),
                "One-byte oversized settings changed persisted state.");
            File.WriteAllText(store.Path, "not-json");
            using var brokenConfiguration = Configuration();
            Reject<InvalidDataException>(() => ServerSettings.Load(brokenConfiguration, directory));
            Check(File.Exists(store.RecoveryPath), "Malformed settings destroyed the manual recovery document.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected rejection: " + typeof(T).Name);
    }
}
