using System.Text;
using SkiaSharp;

namespace PanelHaven.Web;

public sealed class ThumbnailStore(LibraryStore library, string cacheDirectory) : IDisposable
{
    private const long MaxPixels = 32_000_000;
    // ponytail: one cache gate bounds native decode memory; use a small fixed pool only if throughput requires it.
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<byte[]> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var guid)) throw new ArgumentException("Invalid book ID.");
        id = guid.ToString("N");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var revision = library.GetCoverVersion(id);
            var path = Path.Combine(cacheDirectory, id + ".thumbnail");
            if (File.Exists(path))
            {
                using var cached = File.OpenRead(path);
                using var reader = new BinaryReader(cached, Encoding.UTF8, leaveOpen: true);
                if (reader.ReadString() == revision)
                {
                    var bytes = new byte[checked((int)(cached.Length - cached.Position))];
                    await cached.ReadExactlyAsync(bytes, cancellationToken);
                    EnsureRevision(id, revision);
                    return bytes;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var jpeg = Render(library.GetPage(id, 0).Bytes, cancellationToken);
            EnsureRevision(id, revision);
            Directory.CreateDirectory(cacheDirectory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true)) writer.Write(revision);
                    await file.WriteAsync(jpeg, cancellationToken);
                    await file.FlushAsync(cancellationToken);
                }
                EnsureRevision(id, revision);
                cancellationToken.ThrowIfCancellationRequested();
                // Revision and JPEG move together; one fixed cache file per book survives rescans.
                File.Move(temporary, path, overwrite: true);
                return jpeg;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureRevision(string id, string revision)
    {
        if (library.GetCoverVersion(id) != revision)
            throw new InvalidOperationException("Comicbook changed; an administrator must rescan the library.");
    }

    private static byte[] Render(byte[] source, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Invalid cover image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException("Cover exceeds the 32-million-pixel limit.");
        var swapsAxes = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var orientedWidth = swapsAxes ? info.Height : info.Width;
        var orientedHeight = swapsAxes ? info.Width : info.Height;
        var scale = Math.Min(1d, Math.Min(320d / orientedWidth, 480d / orientedHeight));
        var width = Math.Max(1, (int)Math.Round(orientedWidth * scale));
        var height = Math.Max(1, (int)Math.Round(orientedHeight * scale));
        var decodedSize = codec.GetScaledDimensions((float)scale);
        if (decodedSize.Width <= 0 || decodedSize.Height <= 0 || (long)decodedSize.Width * decodedSize.Height > MaxPixels)
            throw new InvalidDataException("Invalid decoded cover dimensions.");
        var decodedInfo = new SKImageInfo(decodedSize.Width, decodedSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(decodedInfo);
        // Default codec options decode frame zero; incomplete input must not become a cached cover.
        if (codec.GetPixels(decodedInfo, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("Invalid cover image.");
        cancellationToken.ThrowIfCancellationRequested();
        bitmap.SetImmutable();
        using var cover = SKImage.FromBitmap(bitmap);
        using var thumbnail = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(thumbnail))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(width, height);
            var orientation = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, 1, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, 1, 0, -1, 1, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, 1, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, 1, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, 1, -1, 0, 1, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, 1, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.Concat(orientation);
            canvas.DrawImage(cover, new SKRect(0, 0, 1, 1), new SKSamplingOptions(SKFilterMode.Linear));
        }
        thumbnail.SetImmutable();
        using var image = SKImage.FromBitmap(thumbnail);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 82)
            ?? throw new InvalidDataException("Cover could not be encoded.");
        return encoded.ToArray();
    }

    public void Dispose() => gate.Dispose();
}
