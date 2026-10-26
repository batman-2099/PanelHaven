using System.Buffers.Binary;
using System.IO.Compression;
using PanelHaven.Web;
using Microsoft.Data.Sqlite;
using SkiaSharp;

public static class ThumbnailChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception($"Expected {typeof(T).Name}.");
        }
        static byte[] Raster(int width, int height, SKColor color, bool split = false,
            SKEncodedImageFormat format = SKEncodedImageFormat.Png)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(color);
                if (split)
                {
                    using var paint = new SKPaint { Color = SKColors.Blue };
                    canvas.DrawRect(new SKRect(width / 2, 0, width, height), paint);
                }
            }
            bitmap.SetImmutable();
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(format, 95);
            return data.ToArray();
        }
        static void CheckImage(byte[] bytes, int width, int height, SKColor color)
        {
            Check(bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
                "Thumbnail is not a JPEG.");
            using var bitmap = SKBitmap.Decode(bytes);
            Check(bitmap is not null && bitmap.Width == width && bitmap.Height == height,
                "Thumbnail size/aspect ratio changed.");
            var pixel = bitmap!.GetPixel(width / 2, height / 2);
            Check(Math.Abs(pixel.Red - color.Red) < 15 && Math.Abs(pixel.Green - color.Green) < 15
                && Math.Abs(pixel.Blue - color.Blue) < 15, "Thumbnail did not preserve cover colors.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "panelhaven-thumbnail-check-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "books");
        var cache = Path.Combine(directory, "thumbnails");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(directory, "library.sqlite");
            var library = new LibraryStore(database, [root]);
            using var thumbnails = new ThumbnailStore(library, cache);
            byte[] Get(string id) => thumbnails.GetAsync(id, default).GetAwaiter().GetResult();
            void Archive(string name, byte[] image, string extension = "png")
            {
                var path = Path.Combine(root, name + ".cbz");
                File.Delete(path);
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                using var entry = archive.CreateEntry("cover." + extension, CompressionLevel.NoCompression).Open();
                entry.Write(image);
            }
            string Import(string name, byte[] image, string extension = "png")
            {
                Archive(name, image, extension);
                var scan = library.ScanAsync(default).GetAwaiter().GetResult();
                Check(scan.ErrorCount == 0, "Thumbnail fixture import failed.");
                return library.ListBooks("reader", name).Items.Single().Id;
            }

            var original = Raster(800, 1200, SKColors.Red);
            var id = Import("portrait", original);
            var first = Get(id);
            CheckImage(first, 320, 480, SKColors.Red);
            Check(first.Length < original.Length, "Thumbnail is not smaller than its source raster.");
            var cacheFile = Directory.GetFiles(cache).Single();
            var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(cacheFile, stamp);
            // An exclusive source handle makes archive reopening fail, but metadata-only validation still works.
            using (var source = new FileStream(Path.Combine(root, "portrait.cbz"), FileMode.Open, FileAccess.Read, FileShare.None))
            using (var restarted = new ThumbnailStore(library, cache))
                Check(first.SequenceEqual(restarted.GetAsync(id, default).GetAwaiter().GetResult()), "Persistent cache was not reused.");
            Check(File.GetLastWriteTimeUtc(cacheFile) == stamp, "Cache hit regenerated the thumbnail.");
            // A busy decoder must not hold up a cover that is already cached.
            var gates = (SemaphoreSlim[])typeof(ThumbnailStore)
                .GetField("gates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(thumbnails)!;
            foreach (var gate in gates) gate.Wait();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Check(first.SequenceEqual(thumbnails.GetAsync(id, deadline.Token).GetAwaiter().GetResult()),
                    "Cached cover waited for an unrelated thumbnail decode.");
            }
            finally
            {
                foreach (var gate in gates) gate.Release();
            }
            var validCache = File.ReadAllBytes(cacheFile);
            foreach (var damaged in new[] { Array.Empty<byte>(), new byte[] { 0x80 },
                new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff },
                validCache[..(validCache.Length - first.Length)], validCache[..^2] })
            {
                File.WriteAllBytes(cacheFile, damaged);
                CheckImage(Get(id), 320, 480, SKColors.Red);
                Check(File.ReadAllBytes(cacheFile).SequenceEqual(validCache), "Damaged cache was not replaced.");
            }
            Reject<ArgumentException>(() => Get("../outside"));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Reject<OperationCanceledException>(() => thumbnails.GetAsync(id, cancelled.Token).GetAwaiter().GetResult());
            }

            Archive("portrait", Raster(900, 450, SKColors.Lime));
            File.SetLastWriteTimeUtc(Path.Combine(root, "portrait.cbz"), DateTime.UtcNow.AddSeconds(2));
            Reject<InvalidOperationException>(() => Get(id));
            Check(library.ScanAsync(default).GetAwaiter().GetResult().Updated == 1, "Replacement archive was not rescanned.");
            CheckImage(Get(id), 320, 160, SKColors.Lime);
            Check(Directory.GetFiles(cache).Length == 1, "Old thumbnail revisions accumulated.");
            CheckImage(Get(Import("small", Raster(64, 32, SKColors.Blue))), 64, 32, SKColors.Blue);
            CheckImage(Get(Import("transparent", Raster(20, 30, SKColors.Transparent))), 20, 30, SKColors.White);

            var concurrent = Import("concurrent", Raster(600, 900, SKColors.Red));
            var requests = Enumerable.Range(0, 12).Select(_ => thumbnails.GetAsync(concurrent, default)).ToArray();
            var results = Task.WhenAll(requests).GetAwaiter().GetResult();
            Check(results.All(bytes => bytes.SequenceEqual(results[0])), "Concurrent cache requests disagreed.");
            CheckImage(results[0], 320, 480, SKColors.Red);
            Check(!Directory.EnumerateFiles(cache, "*.tmp").Any(), "Successful requests left temporary files.");

            var oriented = Raster(80, 40, SKColors.Red, split: true, format: SKEncodedImageFormat.Jpeg);
            // EXIF little-endian TIFF: orientation 6 rotates the left/right bands into top/bottom bands.
            byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0,
                1, 0, 0x12, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
            byte[] rotatedJpeg = [.. oriented.AsSpan(0, 2), .. exif, .. oriented.AsSpan(2)];
            using (var rotated = SKBitmap.Decode(Get(Import("oriented", rotatedJpeg, "jpg"))))
            {
                Check(rotated.Width == 40 && rotated.Height == 80, "EXIF orientation did not swap dimensions.");
                Check(rotated.GetPixel(20, 10).Red > 230 && rotated.GetPixel(20, 70).Blue > 230,
                    "EXIF orientation did not rotate the cover.");
            }
            // Two actual GIF frames: red then blue. The thumbnail must only render frame zero.
            byte[] animated = [71, 73, 70, 56, 57, 97, 1, 0, 1, 0, 0x80, 0, 0, 255, 0, 0, 0, 0, 255,
                0x21, 0xf9, 4, 0, 1, 0, 0, 0, 0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0,
                0x21, 0xf9, 4, 0, 1, 0, 0, 0, 0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x4c, 1, 0, 0x3b];
            CheckImage(Get(Import("animated", animated, "gif")), 1, 1, SKColors.Red);

            var corrupt = Import("corrupt", original[..16]);
            Reject<InvalidDataException>(() => Get(corrupt));
            var oversized = Raster(1, 1, SKColors.Red);
            BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16), 6_000);
            BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(20), 6_000);
            uint crc = uint.MaxValue;
            foreach (var value in oversized.AsSpan(12, 17))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(29), ~crc);
            using (var header = SKCodec.Create(new MemoryStream(oversized)))
                Check(header is not null && (long)header.Info.Width * header.Info.Height > 32_000_000,
                    "Oversized fixture did not expose oversized dimensions before decode.");
            Reject<InvalidDataException>(() => Get(Import("oversized", oversized)));
            Check(!Directory.EnumerateFiles(cache, "*.tmp").Any(), "Rejected images left temporary files.");

            using (var unconfigured = new ThumbnailStore(new LibraryStore(database, []), cache))
                Reject<UnauthorizedAccessException>(() => unconfigured.GetAsync(id, default).GetAwaiter().GetResult());
            var archivePath = Path.Combine(root, "portrait.cbz");
            if (!OperatingSystem.IsWindows())
            {
                var outside = Path.Combine(directory, "outside.cbz");
                File.Move(archivePath, outside);
                File.CreateSymbolicLink(archivePath, outside);
                Reject<UnauthorizedAccessException>(() => Get(id));
                File.Delete(archivePath);
                File.Move(outside, archivePath);
            }
            File.Delete(archivePath);
            Reject<FileNotFoundException>(() => Get(id));

            var boundPath = Path.Combine(root, "same-version.cbz");
            void BoundArchive(SKColor color)
            {
                var pixels = (byte[])animated.Clone();
                pixels[13] = color.Red; pixels[14] = color.Green; pixels[15] = color.Blue;
                Archive("same-version", pixels, "gif");
                using (var archive = ZipFile.Open(boundPath, ZipArchiveMode.Update))
                using (var entry = archive.CreateEntry("page2.gif", CompressionLevel.NoCompression).Open())
                    entry.Write(pixels);
                File.SetLastWriteTimeUtc(boundPath, stamp);
            }
            BoundArchive(SKColors.Red);
            Check(library.ScanAsync(default).GetAwaiter().GetResult().ErrorCount == 0,
                "Same-version thumbnail fixture failed to import.");
            var boundId = library.ListBooks("reader", null).Items.Single(book => book.Title == "same-version").Id;
            CheckImage(Get(boundId), 1, 1, SKColors.Red);
            var order = library.GetPageOrder(boundId);
            library.SavePageOrder(boundId, new(order.Revision, [1, 0]));
            var originalSize = new FileInfo(boundPath).Length;
            var originalTime = File.GetLastWriteTimeUtc(boundPath);
            BoundArchive(SKColors.Blue);
            Check(new FileInfo(boundPath).Length == originalSize && File.GetLastWriteTimeUtc(boundPath) == originalTime,
                "Replacement fixture changed size or timestamp.");
            Reject<InvalidOperationException>(() => library.GetPageOrder(boundId));
            Reject<InvalidOperationException>(() => Get(boundId));
            Check(library.ScanAsync(default).GetAwaiter().GetResult().Updated == 1,
                "Same-version replacement did not rescan after source validation.");
            CheckImage(Get(boundId), 1, 1, SKColors.Blue);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
