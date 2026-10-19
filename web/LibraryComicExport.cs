using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace PanelHaven.Web;

public sealed record ComicExport(FileStream Stream, string FileName, string ContentType);
public sealed class ComicExportLimitException() : Exception("Comic downloads are limited to 1 GiB. Select fewer or smaller books.") { }

public sealed partial class LibraryStore
{
    public ComicExport ExportComics(string[] bookIds, CancellationToken cancellationToken = default)
    {
        if (bookIds is null || bookIds.Length is < 1 or > 100 || bookIds.Any(string.IsNullOrWhiteSpace)
            || bookIds.Distinct(StringComparer.Ordinal).Count() != bookIds.Length)
            throw new ArgumentException("Select 1–100 distinct existing books.");
        scanLock.Wait(cancellationToken);
        FileStream? output = null;
        try
        {
            // ponytail: one read transaction captures shared metadata/order; no private state or catalog writes.
            using var db = Open();
            using var transaction = db.BeginTransaction(deferred: true);
            if (bookIds.Length == 1)
            {
                var single = ExportComic(db, transaction, bookIds[0], cancellationToken);
                return new(single.Stream, single.Name, "application/vnd.comicbook+zip");
            }
            output = NewComicExport(cancellationToken);
            var expected = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
            using (var bundle = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                for (var index = 0; index < bookIds.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var comic = ExportComic(db, transaction, bookIds[index], cancellationToken);
                    using var staged = comic.Stream;
                    var name = $"{index + 1:D3} - {comic.Name}";
                    expected.Add(name, (staged.Length, ArchiveHash(staged, cancellationToken)));
                    staged.Position = 0;
                    using var entry = bundle.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    CopyArchiveBytes(staged, entry, MaxArchiveBytes, cancellationToken);
                }
            }
            output.Flush();
            output.Position = 0;
            using (var bundle = new ZipArchive(output, ZipArchiveMode.Read, true))
            {
                if (bundle.Entries.Count != expected.Count) throw new InvalidDataException("Bundle verification failed.");
                foreach (var entry in bundle.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!expected.TryGetValue(entry.FullName, out var value) || entry.Length != value.Size)
                        throw new InvalidDataException("Bundle verification failed.");
                    using var stream = entry.Open();
                    if (HashExportEntry(stream, Stream.Null, entry.Length, null, cancellationToken) != value.Hash)
                        throw new InvalidDataException("Bundle verification failed.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            output.Position = 0;
            return new(output, "panelhaven-comics.zip", "application/zip");
        }
        catch { output?.Dispose(); throw; }
        finally { scanLock.Release(); }
    }

    private (FileStream Stream, string Name) ExportComic(SqliteConnection db, SqliteTransaction transaction,
        string id, CancellationToken cancellationToken)
    {
        RefusePendingFileOperation(db, id, transaction);
        string path, title, xml, hash;
        string? orderJson, orderHash, bookXml;
        int count;
        long size, ticks;
        using (var command = Command(db, """
            SELECT b.path,b.title,b.info_xml,b.content_hash,b.page_count,b.file_size,b.modified_ticks,
                b.availability,o.pages_json,o.source_hash,b.book_xml
            FROM books b LEFT JOIN page_orders o ON o.book_id=b.id WHERE b.id=$id
            """, ("$id", id)))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException();
            if (reader.IsDBNull(3) || reader.GetString(7) != "available")
                throw new InvalidOperationException("Rescan the source comicbook first.");
            path = reader.GetString(0); title = reader.GetString(1); xml = reader.GetString(2); hash = reader.GetString(3);
            count = reader.GetInt32(4); size = reader.GetInt64(5); ticks = reader.GetInt64(6);
            orderJson = reader.IsDBNull(8) ? null : reader.GetString(8);
            orderHash = reader.IsDBNull(9) ? null : reader.GetString(9);
            bookXml = reader.IsDBNull(10) ? null : reader.GetString(10);
        }
        if (count < 1 || count >= MaxEntries) throw new InvalidDataException("Export requires room for pages and ComicInfo.xml.");
        if (orderJson is not null && orderHash != hash) throw new InvalidOperationException("Saved page order is stale.");
        int[]? order;
        try { order = orderJson is null ? Enumerable.Range(0, count).ToArray() : JsonSerializer.Deserialize<int[]>(orderJson); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid saved page order.", ex); }
        ValidatePageOrder(order, count);
        var positions = new int[count];
        for (var index = 0; index < count; index++) positions[order![index]] = index;
        var document = ParseXml(xml, "ComicInfo");
        var metadataRoot = document.Root!;
        foreach (var (physical, page) in EditablePages(metadataRoot, count))
            page.SetAttributeValue("Image", positions[physical].ToString(CultureInfo.InvariantCulture));
        var cover = metadataRoot.Elements(metadataRoot.Name.Namespace + "PreferredFrontCover").ToArray();
        if (cover.Length != 0)
        {
            if (cover.Length != 1 || cover[0].HasElements || !int.TryParse(cover[0].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var physical) || physical < 0 || physical >= count)
                throw new ArgumentException("PreferredFrontCover must identify one physical page.");
            PatchScalar(metadataRoot, "PreferredFrontCover", positions[physical].ToString(CultureInfo.InvariantCulture));
        }
        PatchScalar(metadataRoot, "PageCount", count.ToString(CultureInfo.InvariantCulture));
        // Legacy-only imports may have copied reading state into ComicInfo; extension namespaces remain shared metadata.
        metadataRoot.Elements().Where(element => element.Name.Namespace == metadataRoot.Name.Namespace
            && element.Name.LocalName is "CurrentPage" or "LastPageRead" or "ReadPercentage" or "ReadTime" or "ReadCount"
                or "OpenedTime" or "OpenedCount" or "LastOpenedTime" or "LastReadTime").Remove();
        var metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["ComicInfo.xml"] = Encoding.UTF8.GetBytes(SerializeMetadata(document))
        };
        var legacy = bookXml is null ? null : ParseXml(bookXml, "ComicBook").Root!;
        if (legacy is not null)
            PatchScalar(legacy, "CustomValuesStore", legacy.Element(legacy.Name.Namespace + "CustomValuesStore")?.Value);
        var custom = ReadMetadata(metadataRoot, legacy).Custom;
        if (custom.Count != 0)
        {
            // Only recognized shared custom values cross this boundary; never copy the private legacy document.
            var values = string.Join(',', custom.Select(pair => EncodeCustom(pair.Key) + "=" + EncodeCustom(pair.Value)));
            metadata.Add("ComicBook.xml", Encoding.UTF8.GetBytes(SerializeMetadata(
                new XDocument(new XElement("ComicBook", new XElement("CustomValuesStore", values))))));
        }
        if (count > MaxEntries - metadata.Count) throw new InvalidDataException("Export requires room for pages and shared metadata.");
        var root = roots.FirstOrDefault(value => IsContained(value, path)) ?? throw new UnauthorizedAccessException();
        if (IsContained(archiveBackupRoot, path)) throw new UnauthorizedAccessException();
        if (ReadSourceVersion(root, path) != (size, ticks)) throw new InvalidOperationException("Source comicbook changed.");
        using var source = OpenArchive(root, path);
        if (ArchiveHash(source, cancellationToken) != hash) throw new InvalidOperationException("Source comicbook changed.");
        source.Position = 0;
        using var archive = new ComicArchive(source, path, cancellationToken);
        if (archive.Pages.Count != count) throw new InvalidOperationException("Source page count changed.");
        var pages = new Dictionary<string, (string Name, long Size, int Position)>(StringComparer.OrdinalIgnoreCase);
        var names = new string[count];
        var hashes = new string[count];
        for (var physical = 0; physical < count; physical++)
        {
            var page = archive.Pages[physical];
            var position = positions[physical];
            names[position] = $"{position + 1:D5}{Path.GetExtension(page.Name).ToLowerInvariant()}";
            pages.Add(page.Name, (names[position], page.Size, position));
        }
        var output = NewComicExport(cancellationToken);
        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                // Numeric filenames encode reading order; solid inputs are decoded once, never once per page.
                archive.Visit(null, (entry, stream) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pages.TryGetValue(entry.Name, out var page))
                    {
                        using var destination = zip.CreateEntry(page.Name, CompressionLevel.NoCompression).Open();
                        hashes[page.Position] = HashExportEntry(stream, destination, page.Size, ImageMime(page.Name), cancellationToken);
                    }
                    else
                    {
                        var info = entry.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase);
                        var book = entry.Name.Equals("ComicBook.xml", StringComparison.OrdinalIgnoreCase);
                        var bytes = ComicArchive.Consume(stream, entry.Size, info || book ? MaxXmlBytes : null, cancellationToken);
                        if (bytes is not null) ReadXml(bytes, info ? "ComicInfo" : "ComicBook", cancellationToken);
                    }
                });
                foreach (var (metadataName, bytes) in metadata)
                {
                    using var infoStream = zip.CreateEntry(metadataName, CompressionLevel.NoCompression).Open();
                    infoStream.Write(bytes);
                }
            }
            output.Flush();
            output.Position = 0;
            using (var verified = new ComicArchive(output, "export.cbz", cancellationToken))
            {
                if (!verified.Pages.Select(page => page.Name).SequenceEqual(names)) throw new InvalidDataException("Export page order verification failed.");
                var index = 0;
                var metadataSeen = 0;
                var byName = names.Select((name, position) => (name, position)).ToDictionary(pair => pair.name, pair => pair.position);
                verified.Visit(null, (entry, stream) =>
                {
                    if (metadata.TryGetValue(entry.Name, out var expectedMetadata))
                    {
                        var bytes = ComicArchive.Consume(stream, entry.Size, MaxXmlBytes, cancellationToken)!;
                        if (!bytes.AsSpan().SequenceEqual(expectedMetadata)) throw new InvalidDataException("Export metadata verification failed.");
                        ReadXml(bytes, Path.GetFileNameWithoutExtension(entry.Name), cancellationToken);
                        metadataSeen++;
                    }
                    else
                    {
                        if (!byName.TryGetValue(entry.Name, out var position)
                            || HashExportEntry(stream, Stream.Null, entry.Size, ImageMime(entry.Name), cancellationToken) != hashes[position])
                            throw new InvalidDataException("Export image verification failed.");
                        index++;
                    }
                });
                if (index != count || metadataSeen != metadata.Count) throw new InvalidDataException("Export contents verification failed.");
            }
            using var latest = OpenArchive(root, path);
            if (ReadSourceVersion(root, path) != (size, ticks) || ArchiveHash(source, cancellationToken) != hash
                || ArchiveHash(latest, cancellationToken) != hash || ReadSourceVersion(root, path) != (size, ticks))
                throw new InvalidOperationException("Source comicbook changed during export.");
            cancellationToken.ThrowIfCancellationRequested();
            output.Position = 0;
            var name = new string(title.Select(character => char.IsControl(character) || char.IsSurrogate(character)
                || "/\\:*?\"<>|".Contains(character) ? '_' : character).Take(100).ToArray()).Trim(' ', '.');
            if (name.Length == 0) name = "Comic";
            if (name.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
                or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
                or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9") name = "Comic - " + name;
            return (output, name + ".cbz");
        }
        catch { output.Dispose(); throw; }
    }

    private static string HashExportEntry(Stream source, Stream destination, long size, string? mime, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            if (mime is not null)
            {
                var prefix = buffer.AsSpan(0, (int)Math.Min(12, size));
                cancellationToken.ThrowIfCancellationRequested();
                source.ReadExactly(prefix);
                if (!HasImageSignature(prefix, mime)) throw new InvalidDataException("Invalid page image signature.");
                hash.AppendData(prefix); destination.Write(prefix); total = prefix.Length;
            }
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + read);
                if (total > size) throw new InvalidDataException("Comicbook entry exceeds its declared size.");
                hash.AppendData(buffer, 0, read); destination.Write(buffer, 0, read);
            }
            if (total != size) throw new InvalidDataException("Truncated comicbook entry.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private FileStream NewComicExport(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(archiveBackupRoot)!;
        EnsureSafePath(directory, directory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new LimitedComicExportStream(Path.Combine(directory, ".panelhaven-comic-export-" + Guid.NewGuid().ToString("N") + ".tmp"), options, cancellationToken);
    }

    // Enforce the final download ceiling on every ZIP write, including central-directory overhead.
    private sealed class LimitedComicExportStream(string path, FileStreamOptions options, CancellationToken cancellationToken)
        : FileStream(path, options)
    {
        private void CheckWrite(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position > MaxArchiveBytes - count) throw new ComicExportLimitException();
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); }
    }
}
