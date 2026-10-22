using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PanelHaven.Web;

public static class AclSecurityChecks
{
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(string path, byte[] name, byte[] value, nuint size, int flags);
    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int RemoveXattr(string path, byte[] name);

    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (TargetInvocationException exception) when (exception.InnerException is UnauthorizedAccessException) { return; }
            catch (UnauthorizedAccessException) { return; }
            throw new Exception("Expected the archive security check to refuse this file.");
        }
        static byte[] Name(string name) => Encoding.UTF8.GetBytes(name + '\0');
        static void Set(string path, byte[] name, byte[] value)
        {
            if (SetXattr(path, name, value, (nuint)value.Length, 0) != 0)
                throw new Exception($"Cannot set disposable extended attribute: errno {Marshal.GetLastPInvokeError()}.");
        }
        static void Remove(string path, byte[] name)
        {
            if (RemoveXattr(path, name) != 0)
                throw new Exception($"Cannot remove disposable extended attribute: errno {Marshal.GetLastPInvokeError()}.");
        }
        var permissions = typeof(LibraryStore).GetMethod("ArchiveWritePermissions", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fingerprint = typeof(LibraryStore).GetMethod("ReadArchiveExtendedAttributesHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-acl-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "archive.cbz");
            byte[] contents = [0, 1, 255, 0, 42];
            File.WriteAllBytes(path, contents);
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            File.SetUnixFileMode(path, mode);
            object Snapshot(bool preserveInode) => permissions.Invoke(null, [directory, path, preserveInode])!;
            string? Hash(string target) => (string?)fingerprint.Invoke(null, [target]);
            var plain = Snapshot(false);
            var securityType = plain.GetType();
            var hashProperty = securityType.GetProperty("ExtendedAttributesHash")!;
            Check(Hash(path) is null && hashProperty.GetValue(plain) is null && plain.Equals(Snapshot(true)),
                "An attribute-free inode must retain the legacy null security fingerprint.");
            var legacy = JsonSerializer.SerializeToNode(plain, securityType)!.AsObject();
            legacy.Remove("ExtendedAttributesHash");
            Check(plain.Equals(JsonSerializer.Deserialize(legacy.ToJsonString(), securityType)),
                "An old journal without ExtendedAttributesHash no longer deserializes to the same security snapshot.");

            (byte[] Name, byte[] Value)[] attributes =
            [
                (Name("user.panelhaven.z"), [0, 255, 1, 0]),
                (Name("user.panelhaven.empty"), []),
                (Name("user.panelhaven.a"), [254, 0, 128]),
                ([.. Encoding.ASCII.GetBytes("user.panelhaven.raw-"), 255, 0], [4, 0, 5])
            ];
            foreach (var attribute in attributes) Set(path, attribute.Name, attribute.Value);
            var original = Snapshot(true);
            var originalHash = Hash(path);
            Check(originalHash is { Length: 64 } && Equals(hashProperty.GetValue(original), originalHash),
                "Preserved-inode security did not include every extended attribute.");
            foreach (var property in new[] { "Mode", "Attributes", "Owner", "Group" })
                Check(Equals(securityType.GetProperty(property)!.GetValue(plain), securityType.GetProperty(property)!.GetValue(original)),
                    "Extended attribute inspection changed or lost ordinary file security.");
            Reject(() => permissions.Invoke(null, [directory, path, Type.Missing]));
            Reject(() => Snapshot(false));
            Check(original.Equals(JsonSerializer.Deserialize(JsonSerializer.Serialize(original, securityType), securityType)),
                "The extended attribute fingerprint was lost in journal serialization.");

            Set(path, attributes[0].Name, [0, 255, 2, 0]);
            Check(Hash(path) != originalHash && !original.Equals(Snapshot(true)) && File.GetUnixFileMode(path) == mode,
                "A same-length raw value mutation without a mode change did not invalidate the security snapshot.");
            Set(path, attributes[0].Name, attributes[0].Value);
            Check(original.Equals(Snapshot(true)), "Restoring exact extended attribute bytes did not restore the security snapshot.");
            foreach (var attribute in attributes)
            {
                Remove(path, attribute.Name);
                Check(Hash(path) != originalHash, "An omitted extended attribute did not change the complete fingerprint.");
                Set(path, attribute.Name, attribute.Value);
            }
            foreach (var attribute in attributes) Remove(path, attribute.Name);
            for (var index = attributes.Length - 1; index >= 0; index--) Set(path, attributes[index].Name, attributes[index].Value);
            Check(Hash(path) == originalHash && original.Equals(Snapshot(true)), "Attribute enumeration order changed the fingerprint.");
            Remove(path, attributes[1].Name);
            var renamed = Name("user.panelhaven.other");
            Set(path, renamed, []);
            Check(Hash(path) != originalHash, "Attribute names were not included in the security fingerprint.");
            Remove(path, renamed);
            Set(path, attributes[1].Name, []);

            var folder = Path.Combine(directory, "folder");
            Directory.CreateDirectory(folder);
            Check(Hash(folder) is null, "An attribute-free directory did not have a null fingerprint.");
            foreach (var attribute in attributes) Set(folder, attribute.Name, attribute.Value);
            Check(Hash(folder) == originalHash, "Destination-directory inspection did not fingerprint the same complete attribute set.");
            var link = Path.Combine(directory, "link.cbz");
            File.CreateSymbolicLink(link, path);
            Reject(() => Hash(link));
            Reject(() => permissions.Invoke(null, [directory, link, true]));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
            Reject(() => Snapshot(true));
            File.SetUnixFileMode(path, mode | UnixFileMode.SetUser);
            Reject(() => Snapshot(true));
            File.SetUnixFileMode(path, mode);
            foreach (var attribute in attributes) Remove(path, attribute.Name);
            Check(Hash(path) is null && plain.Equals(Snapshot(true)) && plain.Equals(Snapshot(false))
                && File.ReadAllBytes(path).SequenceEqual(contents), "Inspection or attribute removal changed archive bytes or legacy security.");

            using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                using var image = archive.CreateEntry("1.png").Open();
                image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
            }
            var store = new LibraryStore(Path.Combine(directory, "library.sqlite"), [directory]);
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Security fixture did not import.");
            var id = store.ListBooks("owner", null).Items.Single().Id;
            store.EditMetadata(id, new(new() { ["Title"] = "Security check" }));
            var preview = store.PreviewArchiveWrite(id);
            var originalBytes = File.ReadAllBytes(path);
            Set(path, attributes[0].Name, attributes[0].Value);
            Reject(() => store.PreviewArchiveWrite(id));
            Reject(() => store.ApplyArchiveWrite(id, new(preview.Revision, true)));
            Check(File.ReadAllBytes(path).SequenceEqual(originalBytes) && Hash(path) is not null,
                "Metadata writing changed a source or stripped an unsupported attribute.");
            Remove(path, attributes[0].Name);
            store.ApplyArchiveWrite(id, new(preview.Revision, true));
            Check(File.GetUnixFileMode(path) == mode && Hash(path) is null,
                "Attribute-free metadata replacement changed file security.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
