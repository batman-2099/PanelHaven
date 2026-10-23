using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using PanelHaven.Web;

public static class KavitaImportChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-import-check-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "library");
        var archived = Path.Combine(directory, "archived");
        var unsorted = Path.Combine(directory, "unsorted");
        var database = Path.Combine(directory, "state", "library.sqlite");
        var initialTemps = Directory.GetDirectories(Path.GetTempPath(), "panelhaven-kavita-*").ToHashSet(StringComparer.Ordinal);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
            foreach (var path in new[] { Path.Combine(root, "Foo.cbz"), Path.Combine(archived, "Bar.cbz"),
                Path.Combine(unsorted, "Partial.cbz"), Path.Combine(root, "Publisher", "Series", "Twin.cbz"),
                Path.Combine(root, "Other", "Series", "Twin.cbz") })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var comic = ZipFile.Open(path, ZipArchiveMode.Create);
                using (var page = comic.CreateEntry("1.png").Open()) page.Write(png);
                using (var page = comic.CreateEntry("2.png").Open()) page.Write(png);
            }
            var store = new LibraryStore(database, [root], archived, unsorted);
            Check((await store.ScanAsync(default)).Added == 5, "Kavita catalog fixtures did not scan.");
            var accounts = new Accounts(database);
            accounts.Bootstrap("admin", "kavita-check-password");
            var alice = accounts.Authenticate("admin", "kavita-check-password")!.Id;
            var bob = accounts.Create("reader", "kavita-reader-password", "reader").Id;
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var db = Open(database))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT path,id FROM books";
                using var rows = command.ExecuteReader();
                while (rows.Read()) ids[rows.GetString(0)] = rows.GetString(1);
            }
            var foo = ids[Path.Combine(root, "Foo.cbz")];
            var bar = ids[Path.Combine(archived, "Bar.cbz")];
            var partial = ids[Path.Combine(unsorted, "Partial.cbz")];
            store.SaveProgress(alice, bar, new(0, true));
            store.SaveProgress(bob, foo, new(0, false));
            Execute(database, "UPDATE progress SET updated_at=123 WHERE completed=1");
            var backup = Backup("", twoUsers: false);
            var preview = await Preview(backup);
            Check(preview.Users.Count == 1 && preview.SourceUserId == 1 && preview.Revision?.Length == 64,
                "Kavita single-user auto-selection/revision failed.");
            Check(preview.CompletedFiles == 14 && preview.PartialChapters == 1 && preview.ReadyBooks == 2
                && preview.AlreadyReadBooks == 1 && preview.UnmatchedFiles == 2 && preview.AmbiguousFiles == 1,
                "Kavita completion, multi-file, deduplication or conservative matching counts failed.");
            Check(preview.Entries.Count == 14 && preview.Entries.All(entry => !entry.Source.Contains('/') && !entry.Source.Contains('\\'))
                && preview.Entries.All(entry => entry.Target is null || !entry.Target.Contains(directory, StringComparison.Ordinal)),
                "Kavita preview exposed absolute source or catalog paths.");
            Check(preview.Entries.Single(entry => entry.Source == "Twin.cbr").BookId == ids[Path.Combine(root, "Publisher", "Series", "Twin.cbz")]
                && preview.Entries.Single(entry => entry.Source == "Twin.zip").Status == "ambiguous"
                && preview.Entries.Single(entry => entry.Source.Contains(':')).Status == "unmatched",
                "Kavita suffix disambiguation or unsupported filename safety failed.");
            Check(!store.GetBook(alice, foo).Started && !store.GetBook(alice, partial).Started, "Preview mutated private progress.");
            await Expect(409, () => Apply(backup, preview.Revision!, bob));
            store.SaveProgress(alice, foo, new(0, false));
            await Expect(409, () => Apply(backup, preview.Revision!));
            Check(!store.GetBook(alice, foo).Completed && !store.GetBook(alice, ids[Path.Combine(root, "Publisher", "Series", "Twin.cbz")]).Completed,
                "Stale Kavita apply performed a partial import.");
            preview = await Preview(backup);
            var altered = Backup("UPDATE MangaFile SET FilePath='/different/Missing.cbz' WHERE Id=1", twoUsers: false);
            await Expect(409, () => Apply(altered, preview.Revision!));
            Execute(database, "UPDATE books SET page_count=3 WHERE id='" + foo + "'");
            await Expect(409, () => Apply(backup, preview.Revision!));
            preview = await Preview(backup);
            Execute(database, "CREATE TRIGGER fail_import BEFORE INSERT ON progress WHEN NEW.book_id='"
                + ids[Path.Combine(root, "Publisher", "Series", "Twin.cbz")] + "' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;");
            try
            {
                await Apply(backup, preview.Revision!);
                throw new Exception("Kavita import ignored a transactional failure.");
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { }
            finally { Execute(database, "DROP TRIGGER fail_import"); }
            Check(!store.GetBook(alice, foo).Completed, "Kavita import failed to roll back an earlier write.");
            Directory.Delete(root, recursive: true);
            Directory.Delete(archived, recursive: true);
            Directory.Delete(unsorted, recursive: true);
            await store.ScanAsync(default);
            // Missing catalog sources remain matchable; importing never opens comicbook files.
            preview = await Preview(backup);
            var result = await Apply(backup, preview.Revision!);
            Check(result.MarkedRead == 2 && result.AlreadyRead == 1 && store.GetBook(alice, foo).CurrentPage == 2
                && store.GetBook(alice, foo).Completed && !store.GetBook(alice, partial).Started,
                "Kavita apply did not use the final physical page or imported partial chapters.");
            Check(!store.GetBook(bob, foo).Completed && store.GetBook(bob, foo).CurrentPage == 0 && !store.GetBook(bob, bar).Started,
                "Kavita apply changed another account's progress.");
            using (var db = Open(database))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT page,updated_at FROM progress WHERE user_id=$user AND book_id=$book";
                command.Parameters.AddWithValue("$user", alice);
                command.Parameters.AddWithValue("$book", bar);
                using var row = command.ExecuteReader();
                Check(row.Read() && row.GetInt32(0) == 0 && row.GetInt64(1) == 123, "Already-read page or timestamp was overwritten.");
            }
            preview = await Preview(backup);
            result = await Apply(backup, preview.Revision!);
            Check(result.MarkedRead == 0 && result.AlreadyRead == 3, "Repeated Kavita import was not idempotent.");
            var multiple = Backup("", twoUsers: true);
            var choose = await Preview(multiple);
            Check(choose.Users.Count == 2 && choose.SourceUserId is null && choose.Revision is null && choose.Entries.Count == 0,
                "Multiple Kavita users were silently combined or selected.");
            var second = await Preview(multiple, 2);
            Check(second.CompletedFiles == 1 && second.ReadyBooks == 1 && second.Entries[0].BookId == partial,
                "Selected Kavita user's completion was not isolated.");
            await Expect(409, () => Apply(multiple, second.Revision!, sourceUser: 1));
            await Expect(400, () => Preview(multiple, 99));
            await Expect(400, () => Apply(backup, "invalid"));
            await Expect(422, () => Preview([1, 2, 3]));
            await Expect(422, () => Preview(Zip([("nested/kavita.db", Encoding.UTF8.GetBytes("bad"))])));
            await Expect(422, () => Preview(Zip([("kavita.db", [1]), ("kavita.db", [2])])));
            await Expect(422, () => Preview(Zip([("kavita.db", [1]), ("kavita.db-wal", [2])])));
            await Expect(422, () => Preview(Zip([("kavita.db", new byte[100])])));
            await Expect(422, () => Preview(Backup("ALTER TABLE MangaFile RENAME TO Hidden; CREATE VIEW MangaFile AS SELECT * FROM Hidden;")));
            await Expect(422, () => Preview(Backup("DROP TABLE MangaFile; CREATE VIRTUAL TABLE MangaFile USING fts5(Id,ChapterId,FilePath);")));
            await Expect(422, () => Preview(Backup("DROP TABLE MangaFile; CREATE TABLE MangaFile(Id INTEGER,ChapterId INTEGER,FilePath TEXT,PRIMARY KEY(Id,ChapterId));")));
            await Expect(422, () => Preview(Backup("UPDATE Chapter SET Pages='invalid' WHERE Id=1")));
            await Expect(422, () => Preview(Backup("UPDATE MangaFile SET FilePath=zeroblob(5000) WHERE Id=1")));
            await Expect(422, () => Preview(Backup("UPDATE AspNetUsers SET UserName=replace(hex(zeroblob(257)),'0','x') WHERE Id=1")));
            await Expect(413, () => Preview(Backup("""
                DELETE FROM MangaFile;
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<100001)
                INSERT INTO MangaFile SELECT x,1,'/library/Foo.cbz' FROM n;
                """)));
            await Expect(413, () => Preview(Backup("""
                WITH RECURSIVE n(x) AS (SELECT 2 UNION ALL SELECT x+1 FROM n WHERE x<1001)
                INSERT INTO AspNetUsers SELECT x,'user'||x FROM n;
                """)));
            var tooManyEntries = Zip(Enumerable.Range(0, 10001).Select(index => (index.ToString(), Array.Empty<byte>())).ToArray());
            await Expect(413, () => Preview(tooManyEntries));
            var largeDeclaration = Zip([("kavita.db", new byte[100])]);
            for (var offset = 0; offset < largeDeclaration.Length - 46; offset++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(largeDeclaration.AsSpan(offset)) == 0x02014b50)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(largeDeclaration.AsSpan(offset + 24), 512U * 1024 * 1024 + 1);
                    break;
                }
            await Expect(413, () => Preview(largeDeclaration));
            var badChecksum = (byte[])backup.Clone();
            for (var offset = 0; offset < badChecksum.Length - 46; offset++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(badChecksum.AsSpan(offset)) == 0x02014b50)
                {
                    badChecksum[offset + 16] ^= 1;
                    break;
                }
            await Expect(422, () => Preview(badChecksum));
            await Expect(422, () => Preview(backup[..^10]));
            using (var oversized = new ZeroStream(LibraryStore.MaxKavitaUploadBytes + 1))
                await Expect(413, () => store.PreviewKavitaImportAsync(alice, oversized, null));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    using var stream = new MemoryStream(backup, writable: false);
                    await store.PreviewKavitaImportAsync(alice, stream, null, cancelled.Token);
                    throw new Exception("Cancelled Kavita upload was accepted.");
                }
                catch (OperationCanceledException) { }
            }
            var tempRemainder = Directory.GetDirectories(Path.GetTempPath(), "panelhaven-kavita-*").Where(path => !initialTemps.Contains(path));
            Check(!tempRemainder.Any(), "Kavita temporary uploads/databases survived success, errors, or cancellation.");

            async Task<KavitaImportPreview> Preview(byte[] bytes, int? sourceUser = null)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return await store.PreviewKavitaImportAsync(alice, stream, sourceUser);
            }
            async Task<KavitaImportResult> Apply(byte[] bytes, string revision, string? user = null, int sourceUser = 1)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return await store.ApplyKavitaImportAsync(user ?? alice, stream, sourceUser, revision);
            }
            byte[] Backup(string change, bool twoUsers = false)
            {
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sqlite");
                Execute(path, """
                    CREATE TABLE AspNetUsers(Id INTEGER PRIMARY KEY,UserName TEXT);
                    CREATE TABLE Chapter(Id INTEGER PRIMARY KEY,Pages INTEGER);
                    CREATE TABLE AppUserProgresses(AppUserId INTEGER,ChapterId INTEGER,PagesRead INTEGER);
                    CREATE INDEX progress_lookup ON AppUserProgresses(AppUserId,ChapterId);
                    CREATE TABLE MangaFile(Id INTEGER PRIMARY KEY,ChapterId INTEGER,FilePath TEXT);
                    INSERT INTO AspNetUsers VALUES(1,'source');
                    INSERT INTO Chapter VALUES(1,10),(2,10),(3,0),(4,10);
                    INSERT INTO AppUserProgresses VALUES(1,1,10),(1,1,10),(1,2,4),(1,3,9),(1,4,0);
                    INSERT INTO MangaFile VALUES
                        (1,1,'C:\\Comics\\Foo.cbr'),(2,1,'/old/Bar.zip'),(3,1,'/old/Publisher/Series/Twin.cbr'),
                        (4,1,'/unknown/Twin.zip'),(5,1,'/old/Missing.cbz'),(6,1,'/old/Foo.cbz:ComicRackInfo'),
                        (7,2,'/old/Partial.cbz'),(8,3,'/old/Foo.cbz'),(9,4,'/old/Bar.cbz'),
                        (10,1,'/old/FOO.cbz'),(11,1,'/old/foo.zip'),(12,1,'/old/Foo.cbr'),(13,1,'/old/Foo.rar'),
                        (14,1,'/old/Foo.cb7'),(15,1,'/old/Foo.7z'),(16,1,'/old/Foo.cbt'),(17,1,'/old/Foo.tar');
                    """ + (twoUsers ? "INSERT INTO AspNetUsers VALUES(2,'second'); INSERT INTO AppUserProgresses VALUES(2,2,11);" : "") + change);
                var bytes = File.ReadAllBytes(path);
                File.Delete(path);
                return Zip([("kavita.db", bytes), ("../../ignored-secret.json", Encoding.UTF8.GetBytes("not extracted"))]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Expect(int status, Func<Task> action)
    {
        try { await action(); }
        catch (KavitaImportException exception) when (exception.StatusCode == status)
        {
            if (exception.Message.Contains("SELECT", StringComparison.Ordinal) || exception.Message.Contains("/tmp/", StringComparison.Ordinal))
                throw new Exception("Kavita error exposed SQL or a private path.");
            return;
        }
        throw new Exception($"Kavita backup was not rejected with status {status}.");
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void Execute(string path, string sql)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Zip((string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        return output.ToArray();
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get => Length - remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, read);
            remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
