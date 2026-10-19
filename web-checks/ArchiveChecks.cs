using System.Buffers.Binary;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;
using SkiaSharp;

// Fixture provenance: all contents are original synthetic test data, freely redistributable with this project.
// PNG is a generated 2x2 RGB image; GIF is the same synthetic 1x1 test pixel used by LibraryChecks.
// No RAR/7-Zip executables or third-party archive contents are redistributed.
// Generated from fixtures/source (explicit order: page10.png page2.gif ComicInfo.xml ComicBook.xml):
//   RAR 6.23 (https://www.rarlab.com/rar/rarlinux-x64-623.tar.gz):
//     rar a -ep -ma4 -s ../rar4.cbr <files>
//     rar a -ep -ma5 -s -md4m ../rar5.cbr <files>
//     rar a -ep -ma5 -hpfixture ../encrypted.cbr <files>
//     rar a -ma5 -v64k multi.rar payload.bin (100000 deterministic random bytes; retain first volume as multipart.rar)
//   libarchive bsdtar: bsdtar --format=7zip -cf ../solid.cb7 <files>
//                     bsdtar --format=ustar -cf ../plain.cbt <files>
//   7-Zip 23.01 (https://www.7-zip.org/a/7z2301-linux-x64.tar.xz):
//     7zz a -t7z -m0=lzma2 -ms=on -md=4m ../lzma2.cb7 <files>
//     7zz a -t7z -pfixture -mhe=on ../encrypted.cb7 <files>
//     7zz a -t7z -pfixture -mhe=off ../encrypted-files.cb7 <files>
//     7zz a -t7z -m0=lzma2 -mhc=off ../large-dictionary.cb7 <files>
//       then set LZMA2 dictionary property to 32 (256MiB), updating next-header/start-header CRC32s.
// Directory variants use the same source XML, images under pages/, and an empty directory named empty.png.
// Run the same RAR4/RAR5/7-Zip/ustar commands with inputs: pages empty.png ComicInfo.xml ComicBook.xml
// (omit RAR -ep so directory records and nested page names are retained).
// nonsolid.cb7 uses the lzma2.cb7 command with -ms=off.
public static class ArchiveChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-archive-check-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        Directory.CreateDirectory(root);
        try
        {
            var good = new[] { "rar4.cbr", "rar5.cbr", "solid.cb7", "lzma2.cb7", "plain.cbt",
                "directories-rar4.cbr", "directories-rar5.cbr", "directories.cb7", "directories.cbt", "nonsolid.cb7" };
            var store = new LibraryStore(Path.Combine(directory, "library.sqlite"), [root]);
            var gif = File.ReadAllBytes(Path.Combine(fixtures, "source", "page2.gif"));
            var png = File.ReadAllBytes(Path.Combine(fixtures, "source", "page10.png"));
            using var thumbnails = new ThumbnailStore(store, Path.Combine(directory, "thumbnails"));
            var ids = new List<string>();
            foreach (var name in good)
            {
                var path = Path.Combine(root, name);
                File.Copy(Path.Combine(fixtures, name), path);
                var scan = store.ScanAsync(default).GetAwaiter().GetResult();
                Check(scan.Added == 1 && scan.ErrorCount == 0, $"{name}: import failed: {string.Join("; ", scan.Errors)}");
                var book = store.ListBooks("reader", null).Items.Single(b => !ids.Contains(b.Id));
                ids.Add(book.Id);
                Check(book.Title == "Archive fixture" && book.Series == "Formats" && book.PageCount == 2, name + ": metadata import failed.");
                var metadata = store.GetBook("reader", book.Id).Metadata!;
                Check(((string)metadata["comicInfoXml"]!).Contains("keep=\"yes\"")
                    && ((string)metadata["comicBookXml"]!).Contains("Legacy fixture"), name + ": metadata preservation failed.");
                Check(store.GetPage(book.Id, 0).Bytes.SequenceEqual(gif) && store.GetPage(book.Id, 1).Bytes.SequenceEqual(png),
                    name + ": natural page order or exact page bytes failed.");
                var thumb = thumbnails.GetAsync(book.Id, default).GetAwaiter().GetResult();
                using var image = SKBitmap.Decode(thumb);
                Check(image is { Width: 1, Height: 1 }, name + ": thumbnail decoding failed.");
                var version = store.GetCoverVersion(book.Id);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var restarted = new ThumbnailStore(store, Path.Combine(directory, "thumbnails")))
                {
                    scan = store.ScanAsync(default).GetAwaiter().GetResult();
                    Check(scan.Skipped == ids.Count && scan.ErrorCount == 0, name + ": incremental scan reopened an unchanged source.");
                    Check(thumb.SequenceEqual(restarted.GetAsync(book.Id, default).GetAwaiter().GetResult()), name + ": persistent thumbnail cache failed.");
                }
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
                scan = store.ScanAsync(default).GetAwaiter().GetResult();
                Check(scan.Updated == 1 && scan.ErrorCount == 0 && version != store.GetCoverVersion(book.Id), name + ": changed source refresh failed.");
            }
            // Non-comic extensions use exactly the same format reader; PDF remains out of scope.
            foreach (var (fixture, alias) in new[] { ("rar5.cbr", "alias.rar"), ("solid.cb7", "alias.7z"), ("plain.cbt", "alias.tar") })
                File.Copy(Path.Combine(fixtures, fixture), Path.Combine(root, alias));
            File.WriteAllText(Path.Combine(root, "ignored.pdf"), "not a PDF");
            var aliases = store.ScanAsync(default).GetAwaiter().GetResult();
            Check(aliases.Scanned == good.Length + 3 && aliases.Added == 3 && aliases.ErrorCount == 0, "Archive aliases or PDF exclusion failed.");

            var bad = Path.Combine(directory, "bad");
            Directory.CreateDirectory(bad);
            var invalid = new LibraryStore(Path.Combine(directory, "bad.sqlite"), [bad]);
            foreach (var name in new[] { "encrypted.cbr", "encrypted.cb7", "encrypted-files.cb7", "multipart.rar", "large-dictionary.cb7" })
                File.Copy(Path.Combine(fixtures, name), Path.Combine(bad, name));
            foreach (var (fixture, name) in new[] { ("rar4.cbr", "truncated.cbr"), ("solid.cb7", "truncated.cb7"), ("plain.cbt", "truncated.cbt") })
                File.WriteAllBytes(Path.Combine(bad, name), File.ReadAllBytes(Path.Combine(fixtures, fixture))[..40]);
            void Tar(string name, params (string Name, byte[] Bytes, TarEntryType Type)[] entries)
            {
                using var output = File.Create(Path.Combine(bad, name));
                using var writer = new TarWriter(output, TarEntryFormat.Ustar);
                foreach (var entry in entries)
                {
                    var item = new UstarTarEntry(entry.Type, entry.Name);
                    if (entry.Type == TarEntryType.SymbolicLink) item.LinkName = "outside";
                    else item.DataStream = new MemoryStream(entry.Bytes, false);
                    writer.WriteEntry(item);
                    item.DataStream?.Dispose();
                }
            }
            Tar("traversal.cbt", ("../page.gif", gif, TarEntryType.RegularFile));
            Tar("duplicate.cbt", ("page.gif", gif, TarEntryType.RegularFile), ("PAGE.gif", gif, TarEntryType.RegularFile));
            Tar("symlink.cbt", ("page.gif", gif, TarEntryType.SymbolicLink));
            Tar("dtd.cbt", ("page.gif", gif, TarEntryType.RegularFile),
                ("ComicInfo.xml", Encoding.UTF8.GetBytes("<!DOCTYPE ComicInfo [<!ENTITY x 'bad'>]><ComicInfo>&x;</ComicInfo>"), TarEntryType.RegularFile));
            var shortZip = Path.Combine(bad, "understated.cbz");
            using (var archive = ZipFile.Open(shortZip, ZipArchiveMode.Create))
            using (var page = archive.CreateEntry("page.png").Open()) page.Write(png);
            var bytes = File.ReadAllBytes(shortZip);
            var central = bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x50, 0x4b, 0x01, 0x02]);
            Check(central >= 0, "ZIP fixture central directory is missing.");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24), 1);
            File.WriteAllBytes(shortZip, bytes);
            var rejected = invalid.ScanAsync(default).GetAwaiter().GetResult();
            Check(rejected.Scanned == 13 && rejected.ErrorCount == 12 && rejected.Added == 1,
                "Unsafe archives were accepted: " + string.Join("; ", rejected.Errors));
            // .NET bounds ZIP deflate output to the declared size. The truncated image must never reach a reader.
            var understated = invalid.ListBooks("reader", null).Items.Single();
            Check(understated.Title == "understated", "Unexpected invalid archive was imported.");
            var pageRejected = false;
            try { invalid.GetPage(understated.Id, 0); }
            catch (InvalidDataException) { pageRejected = true; }
            Check(pageRejected, "An understated ZIP entry reached the reader as an invalid image.");
            Check(rejected.Errors.Count(e => e.Contains("Encrypted")) == 3 && rejected.Errors.Any(e => e.Contains("Multipart")),
                "Encrypted/multipart scan errors were not explicit.");
            Check(rejected.Errors.All(e => !e.Contains(directory)), "Archive errors leaked source paths.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
