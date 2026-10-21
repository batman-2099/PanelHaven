using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

internal static class IntakeProcessingChecks
{
    private static readonly ComicVineMetadata Metadata = new(new()
    {
        ["Series"] = "Example", ["Publisher"] = "Publisher", ["Number"] = "1", ["Title"] = "Accepted title",
        ["Volume"] = "2020", ["Year"] = "2025", ["Month"] = "5", ["Day"] = "14", ["Writer"] = "Creator"
    }, "https://comicvine.gamespot.com/example/4000-1/");

    public static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var format in new[] { "cbz", "zip", "rar4.cbr", "rar5.cbr", "directories-rar4.cbr", "directories-rar5.cbr" })
        {
            using var fixture = new Fixture(format);
            var store = fixture.Store;
            var id = fixture.Id;
            var page0 = store.GetPage(id, 0).Bytes;
            var page1 = store.GetPage(id, 1).Bytes;
            store.SaveProgress("owner", id, new(1, true));
            store.SaveRating("owner", id, new(4));
            store.SaveBookmark("owner", id, 0, new("Private"));
            var list = store.CreateList("owner", new("Keep", "manual", "all", []));
            store.AddListBooks("owner", list.Id, [id]);
            store.SavePageOrder(id, new(store.GetPageOrder(id).Revision, [1, 0]));
            store.EditMetadata(id, new(new() { ["Genre"] = "Retained" }, new() { ["Keep"] = "Edited" }));
            var pages = new[] { new PageMetadataRow(0, "FrontCover", "Shared") };
            var preview = store.PreviewPageMetadata(id, new(store.GetPageMetadata(id).Revision, pages));
            store.EditPageMetadata(id, new(preview.Revision, pages));
            var prepared = fixture.Prepare();
            var extension = format == "zip" ? ".zip" : ".cbz";
            Check(prepared.Destination == "Library 1/Publisher/Example (2020)/Example Vol.2020 #01 (May, 2025)" + extension,
                format + ": original-organizer naming differs.");
            Check(!Directory.Exists(Path.Combine(fixture.Library, "Publisher")) && File.ReadAllBytes(fixture.Source).SequenceEqual(fixture.Original),
                "Prepare mutated folders or original bytes.");
            Reject<FileOperationConflictException>(() => store.ProcessIntake(id, new(1, 2, 0, "stale", true), Metadata));
            Reject<ArgumentException>(() => store.ProcessIntake(id, new(1, 2, 0, prepared.Revision, false), Metadata));
            var changed = new ComicVineMetadata(new(Metadata.Fields) { ["Title"] = "Provider changed" }, Metadata.Url);
            Reject<FileOperationConflictException>(() => store.ProcessIntake(id, new(1, 2, 0, prepared.Revision, true), changed));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Reject<OperationCanceledException>(() => store.ProcessIntake(id, new(1, 2, 0, prepared.Revision, true), Metadata, canceled.Token));
            }
            Check(store.GetIntakeProcessing(id).State == "none", "Preflight failures created a pending mutation.");
            var result = store.ProcessIntake(id, new(1, 2, 0, prepared.Revision, true), Metadata);
            Check(result.State == "completed" && result.OriginalAvailable && !result.CanRecover && result.Converted == format.EndsWith(".cbr"),
                format + ": processing did not complete: " + result.Message);
            Check(!File.Exists(fixture.Source), format + ": original source was not removed after success.");
            var destination = (string)fixture.Sql("SELECT path FROM books WHERE id=$id")!;
            Check(File.Exists(destination) && !result.Source.StartsWith('/') && !result.Destination.StartsWith('/'), "Destination missing or absolute paths leaked.");
            Check(store.GetMetadata(id).Fields["Title"] == "Accepted title" && store.GetMetadata(id).Fields["Genre"] == "Retained"
                && store.GetMetadata(id).Custom["Keep"] == "Edited", "Catalog provider/shared/custom metadata was lost.");
            var current = store.GetBook("owner", id);
            Check(current.Id == id && current.Completed && current.CurrentPage == 1 && current.PersonalRating == 4
                && store.GetBookmarks("owner", id).Single().Name == "Private" && store.GetList("owner", list.Id).BookIds!.Single() == id
                && !store.GetBook("other", id).Started && store.GetBookmarks("other", id).Count == 0, "Private identity/state changed.");
            Check(store.GetPageOrder(id).Pages.SequenceEqual([1, 0]) && store.GetPageOrder(id).Custom
                && store.GetPage(id, 0).Bytes.SequenceEqual(page0) && store.GetPage(id, 1).Bytes.SequenceEqual(page1)
                && store.GetPageMetadata(id).Pages[0] == pages[0], "Physical pages, shared order or page annotations changed.");
            using (var zip = ZipFile.OpenRead(destination))
            {
                var xml = Read(zip, "ComicInfo.xml");
                var legacy = Read(zip, "ComicBook.xml");
                Check(xml.Contains("Accepted title") && xml.Contains("Creator") && xml.Contains("FrontCover") && xml.Contains("Shared")
                    && legacy.Contains("Keep=Edited"), "Embedded metadata was not refreshed.");
                Check(xml.Contains(format is "cbz" or "zip" ? "source-extension" : "keep=\"yes\""), "Unknown source XML was lost.");
                if (format is "cbz" or "zip")
                    Check(Read(zip, "notes.txt") == "nonmetadata payload" && zip.Comment == "source-comment"
                        && zip.GetEntry("empty/") is not null && legacy.Contains("legacy-extension"), "Nonmetadata/legacy ZIP contents changed.");
                if (format.StartsWith("directories", StringComparison.Ordinal)) Check(zip.GetEntry("empty.png/") is not null, "RAR directory entries were dropped.");
            }
            var download = store.ExportIntakeOriginal(id);
            using (download.Stream)
            using (var bytes = new MemoryStream())
            {
                download.Stream.CopyTo(bytes);
                Check(bytes.ToArray().SequenceEqual(fixture.Original) && download.Name == Path.GetFileName(fixture.Source), "Original download was not byte-exact or had wrong format/name.");
                var restoreRoot = Path.Combine(fixture.Directory, "restored");
                System.IO.Directory.CreateDirectory(restoreRoot);
                File.WriteAllBytes(Path.Combine(restoreRoot, download.Name), bytes.ToArray());
                var restored = new LibraryStore(Path.Combine(fixture.Directory, "restore-state", "library.sqlite"), [restoreRoot]);
                Check(restored.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Verified original is not readable in its actual format.");
            }
            Check(store.ScanAsync(default).GetAwaiter().GetResult().Added == 0 && (long)fixture.Sql("SELECT count(*) FROM books")! == 1,
                "Post-processing scan duplicated the original identity.");
            var restarted = new LibraryStore(fixture.Database, [fixture.Library], unsortedPath: fixture.Unsorted);
            Check(restarted.GetIntakeProcessing(id).State == "completed" && restarted.GetPageOrder(id).Pages.SequenceEqual([1, 0]), "Restart lost committed journal/page order.");
        }

        using (var fixture = new Fixture("cbz"))
        {
            var first = fixture.Prepare();
            var destination = Path.Combine(fixture.Library, first.Destination["Library 1/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "foreign collision");
            Reject<FileOperationConflictException>(() => fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, first.Revision, true), Metadata));
            var second = fixture.Prepare();
            Check(second.Destination.EndsWith(" [2].cbz", StringComparison.Ordinal), "Existing destination did not get a numbered suffix.");
            Check(fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, second.Revision, true), Metadata).State == "completed"
                && File.ReadAllText(destination) == "foreign collision", "Destination collision was overwritten.");
        }
        using (var fixture = new Fixture("cbz"))
        {
            var folder = Path.Combine(fixture.Library, "Publisher", "Example (2020)");
            Directory.CreateDirectory(folder);
            Check(SetXattr(folder, "user.DOSATTRIB", [1], 1, 0) == 0, "Cannot set disposable destination attribute.");
            var preview = fixture.Prepare();
            Check(SetXattr(folder, "user.DOSATTRIB", [2], 1, 0) == 0, "Cannot change disposable destination attribute.");
            Reject<FileOperationConflictException>(() => fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, preview.Revision, true), Metadata));
            Check(File.ReadAllBytes(fixture.Source).SequenceEqual(fixture.Original), "Stale destination changed original bytes.");
            var current = fixture.Prepare();
            Check(fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, current.Revision, true), Metadata).State == "completed",
                "Destination folder attributes prevented processing.");
            var attribute = new byte[1];
            Check(GetXattr(folder, "user.DOSATTRIB", attribute, 1) == 1 && attribute[0] == 2,
                "Processing changed destination folder attributes.");
        }
        using (var fixture = new Fixture("cbz"))
        {
            var preview = fixture.Prepare();
            fixture.Store.EditMetadata(fixture.Id, new(new() { ["Genre"] = "Changed" }));
            Reject<FileOperationConflictException>(() => fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, preview.Revision, true), Metadata));
            var mode = File.GetUnixFileMode(fixture.Source);
            File.SetUnixFileMode(fixture.Source, UnixFileMode.UserRead);
            Reject<UnauthorizedAccessException>(() => fixture.Prepare());
            File.SetUnixFileMode(fixture.Source, mode);
            var saved = fixture.Source + ".saved";
            File.Move(fixture.Source, saved);
            File.CreateSymbolicLink(fixture.Source, saved);
            Reject<UnauthorizedAccessException>(() => fixture.Prepare());
            File.Delete(fixture.Source);
            File.Move(saved, fixture.Source);
            if (SetXattr(fixture.Source, "user.panelhaven-check", [1], 1, 0) == 0)
            {
                Reject<UnauthorizedAccessException>(() => fixture.Prepare());
                Check(RemoveXattr(fixture.Source, "user.panelhaven-check") == 0, "Cannot remove disposable xattr.");
            }
            Directory.CreateSymbolicLink(Path.Combine(fixture.Library, "Publisher"), fixture.Unsorted);
            Reject<UnauthorizedAccessException>(() => fixture.Prepare());
            Check(File.ReadAllBytes(fixture.Source).SequenceEqual(fixture.Original), "Refused filesystem condition changed original bytes.");
        }
        using (var fixture = new Fixture("cbz"))
        {
            var fields = new Dictionary<string, string>(Metadata.Fields);
            fields["Number"] = "1.5";
            Check(fixture.Store.PrepareIntake(fixture.Id, new(1, 2, 0), new(fields, Metadata.Url)).Destination.Contains("#1.5 (May, 2025)", StringComparison.Ordinal),
                "Fractional issue number was incorrectly zero-padded.");
            fields.Remove("Month");
            fields.Remove("Day");
            Check(fixture.Store.PrepareIntake(fixture.Id, new(1, 2, 0), new(fields, Metadata.Url)).Destination.EndsWith("(2025).cbz", StringComparison.Ordinal),
                "Missing month did not fall back to year.");
            fields.Remove("Year");
            fields.Remove("Volume");
            fields["Series"] = "../Example:Name";
            fields["Publisher"] = "..";
            var prepared = fixture.Store.PrepareIntake(fixture.Id, new(1, 2, 0), new(fields, Metadata.Url));
            Check(prepared.Destination.Contains("Unknown Publisher/.._Example_Name (Unknown)", StringComparison.Ordinal)
                && prepared.Destination.EndsWith("(Unknown Date).cbz", StringComparison.Ordinal), "Missing-date fallback or conservative component sanitization failed.");
            Reject<ArgumentException>(() => fixture.Store.PrepareIntake(fixture.Id, new(1, 2, 1), Metadata));
        }

        // SQLite faults deterministically interrupt real transitions without production test hooks.
        foreach (var checkpoint in new[] { "backup", "stage", "published", "cleaned" })
        {
            using var fixture = new Fixture("rar5.cbr");
            var condition = checkpoint switch
            {
                "backup" => "json_extract(NEW.journal,'$.BackupReady')=1",
                "stage" => "json_extract(NEW.journal,'$.OutputHash') IS NOT NULL",
                "published" => "json_extract(NEW.journal,'$.Committed')=1",
                _ => "NEW.state='completed'"
            };
            fixture.Sql($"CREATE TRIGGER interrupt_intake BEFORE UPDATE ON intake_operations WHEN {condition} BEGIN SELECT RAISE(ABORT,'disposable interruption'); END");
            var preview = fixture.Prepare();
            var result = fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, preview.Revision, true), Metadata);
            Check(result.State == "pending" && result.CanRecover, checkpoint + ": interruption did not retain a recoverable reservation: " + result.Message);
            fixture.Sql("DROP TRIGGER interrupt_intake");
            var restarted = new LibraryStore(fixture.Database, [fixture.Library], unsortedPath: fixture.Unsorted);
            Check(restarted.ScanAsync(default).GetAwaiter().GetResult().Added == 0 && (long)fixture.Sql("SELECT count(*) FROM books")! == 1,
                "Scan imported a reserved output/source after interruption.");
            Reject<FileOperationConflictException>(() => restarted.EditMetadata(fixture.Id, new(new() { ["Title"] = "Unsafe concurrent edit" })));
            Reject<FileOperationConflictException>(() => restarted.RecoverIntakeProcessing(fixture.Id, new("stale", true)));
            if (checkpoint == "published")
            {
                File.Move(fixture.Source, fixture.Source + ".owned");
                File.WriteAllText(fixture.Source, "foreign source");
                Check(!restarted.GetIntakeProcessing(fixture.Id).CanRecover, "Recovery ignored a replacement source inode.");
                File.Delete(fixture.Source);
                File.Move(fixture.Source + ".owned", fixture.Source);
                var journal = JsonNode.Parse((string)fixture.Sql("SELECT journal FROM intake_operations WHERE book_id=$id")!)!;
                var output = journal["Destination"]!.GetValue<string>();
                File.Move(output, output + ".owned");
                File.WriteAllText(output, "foreign replacement");
                Check(!restarted.GetIntakeProcessing(fixture.Id).CanRecover, "Recovery would delete a replacement destination.");
                Reject<FileOperationConflictException>(() => restarted.RecoverIntakeProcessing(fixture.Id, new(result.Revision, true)));
                Check(File.ReadAllText(output) == "foreign replacement", "Recovery damaged a foreign destination.");
                File.Delete(output);
                File.Move(output + ".owned", output);
            }
            result = restarted.GetIntakeProcessing(fixture.Id);
            result = restarted.RecoverIntakeProcessing(fixture.Id, new(result.Revision, true));
            Check(result.State == (checkpoint == "cleaned" ? "completed" : "failed") && !result.CanRecover,
                checkpoint + ": recovery did not finish safely.");
            if (checkpoint != "cleaned")
            {
                Check(File.ReadAllBytes(fixture.Source).SequenceEqual(fixture.Original), "Rollback changed original bytes.");
                var retry = restarted.PrepareIntake(fixture.Id, new(1, 2, 0), Metadata);
                Check(restarted.ProcessIntake(fixture.Id, new(1, 2, 0, retry.Revision, true), Metadata).State == "completed", "Explicit fresh retry after rollback failed.");
            }
        }

        // Reconstruct the exact committed-before-cleanup window, with a verified source inode and timestamp.
        using (var fixture = new Fixture("rar4.cbr"))
        {
            var preview = fixture.Prepare();
            Check(fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, preview.Revision, true), Metadata).State == "completed", "Commit fixture failed.");
            var journal = JsonNode.Parse((string)fixture.Sql("SELECT journal FROM intake_operations WHERE book_id=$id")!)!;
            File.WriteAllBytes(fixture.Source, fixture.Original);
            File.SetUnixFileMode(fixture.Source, (UnixFileMode)journal["Security"]!["Mode"]!.GetValue<int>());
            File.SetLastWriteTimeUtc(fixture.Source, new DateTime(journal["SourceTicks"]!.GetValue<long>(), DateTimeKind.Utc));
            journal["SourceIdentity"] = (string)typeof(LibraryStore).GetMethod("FileIdentity", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [fixture.Source])!;
            journal["State"] = "pending";
            fixture.Sql("UPDATE intake_operations SET state='pending',journal=$value WHERE book_id=$id", journal.ToJsonString());
            var result = fixture.Store.GetIntakeProcessing(fixture.Id);
            Check(result.CanRecover, "Committed cleanup window was not recoverable.");
            result = fixture.Store.RecoverIntakeProcessing(fixture.Id, new(result.Revision, true));
            Check(result.State == "completed" && !File.Exists(fixture.Source) && result.OriginalAvailable, "Committed cleanup did not safely remove verified source.");
        }
        using (var fixture = new Fixture("cbz"))
        {
            var prepared = fixture.Prepare();
            Check(fixture.Store.ProcessIntake(fixture.Id, new(1, 2, 0, prepared.Revision, true), Metadata).State == "completed", "Relocation fixture processing failed.");
            var moved = Path.Combine(fixture.Directory, "relocated-library");
            Directory.Move(fixture.Library, moved);
            typeof(LibraryStore).GetMethod("Relocate", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [fixture.Database, new[] { moved, fixture.Unsorted }, fixture.Library, moved, true]);
            var relocated = new LibraryStore(fixture.Database, [moved], unsortedPath: fixture.Unsorted);
            Check(relocated.GetIntakeProcessing(fixture.Id).State == "completed", "Root relocation lost processing history.");
            var original = relocated.ExportIntakeOriginal(fixture.Id);
            using (original.Stream)
            using (var bytes = new MemoryStream())
            {
                original.Stream.CopyTo(bytes);
                Check(bytes.ToArray().SequenceEqual(fixture.Original), "Root relocation orphaned the original backup.");
            }
        }
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var json in new[] { "{}", "{\"issueId\":1,\"volumeId\":2,\"rootIndex\":\"0\"}", "{\"issueId\":1,\"volumeId\":2,\"rootIndex\":0,\"path\":\"/private\"}" })
            Reject<JsonException>(() => JsonSerializer.Deserialize<IntakePrepareRequest>(json, options));
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var input = new StreamReader(zip.Entries.Single(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).Open());
        return input.ReadToEnd();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr(string path, string name, byte[] value, nuint size, int flags);
    [DllImport("libc", EntryPoint = "removexattr", SetLastError = true)]
    private static extern int RemoveXattr(string path, string name);
    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(string path, string name, byte[] value, nuint size);

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "panelhaven-intake-processing-" + Guid.NewGuid().ToString("N"));
        public string Library => Path.Combine(Directory, "library");
        public string Unsorted => Path.Combine(Directory, "unsorted");
        public string Database => Path.Combine(Directory, "state", "library.sqlite");
        public string Source { get; }
        public byte[] Original { get; }
        public LibraryStore Store { get; }
        public string Id { get; }
        public Fixture(string format)
        {
            System.IO.Directory.CreateDirectory(Library);
            System.IO.Directory.CreateDirectory(Unsorted);
            Source = Path.Combine(Unsorted, "original." + (format is "cbz" or "zip" ? format : "cbr"));
            var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
            if (format is not ("cbz" or "zip")) File.Copy(Path.Combine(fixtures, format), Source);
            else
            {
                using var zip = ZipFile.Open(Source, ZipArchiveMode.Create);
                zip.Comment = "source-comment";
                zip.CreateEntry("empty/");
                foreach (var name in new[] { "page2.gif", "page10.png" })
                {
                    using var output = zip.CreateEntry(name).Open();
                    output.Write(File.ReadAllBytes(Path.Combine(fixtures, "source", name)));
                }
                foreach (var (name, text) in new[] {
                    ("ComicInfo.xml", "<ComicInfo marker='source-extension'><Title>Original</Title><Unknown>retained</Unknown><Pages><Page Image='0' Type='Story' Extra='keep'/></Pages></ComicInfo>"),
                    ("ComicBook.xml", "<ComicBook marker='legacy-extension'><CustomValuesStore>Keep=Original</CustomValuesStore><Unknown>retained</Unknown></ComicBook>"),
                    ("notes.txt", "nonmetadata payload") })
                {
                    using var output = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                    output.Write(text);
                }
            }
            Original = File.ReadAllBytes(Source);
            Store = new LibraryStore(Database, [Library], unsortedPath: Unsorted);
            Check(Store.ScanAsync(default).GetAwaiter().GetResult().Added == 1, "Intake fixture did not import.");
            Id = (string)Sql("SELECT id FROM books")!;
        }
        public IntakeProcessingPreview Prepare() => Store.PrepareIntake(Id, new(1, 2, 0), Metadata);
        public object? Sql(string sql, object? value = null)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", Id ?? "");
            command.Parameters.AddWithValue("$value", value ?? DBNull.Value);
            return command.ExecuteScalar();
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
