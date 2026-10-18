using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;

namespace PanelHaven.Web;

public sealed partial class LibraryStore
{
    private sealed class ArchiveFeatureException(string message) : IOException(message) { }

    // No extraction, source writes, or whole-archive buffering. Solid reads replay only the preceding entries.
    private sealed class ComicArchive : IDisposable
    {
        private const int MaxHeaderBytes = 16 * 1024 * 1024, MaxDictionaryBytes = 64 * 1024 * 1024;
        private readonly ZipArchive? zip;
        private readonly IArchive? archive;
        private readonly bool solid;
        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        public List<Entry> Pages { get; } = [];
        public sealed record Entry(string Name, long Size, bool Directory, ZipArchiveEntry? Zip, IArchiveEntry? Compressed);

        public ComicArchive(FileStream file, string path, CancellationToken cancellationToken = default)
        {
            try
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is ".cbz" or ".zip") zip = new ZipArchive(file, ZipArchiveMode.Read, true);
                else
                {
                    if (extension is ".cbr" or ".rar") PreflightRar(file, cancellationToken);
                    else if (extension is ".cb7" or ".7z") PreflightSevenZip(file, cancellationToken);
                    file.Position = 0;
                    var options = new ReaderOptions { LeaveStreamOpen = true, LookForHeader = false };
                    archive = extension switch
                    {
                        ".cbr" or ".rar" => RarArchive.OpenArchive(file, options),
                        ".cb7" or ".7z" => SevenZipArchive.OpenArchive(file, options),
                        _ => TarArchive.OpenArchive(file, options)
                    };
                }
                solid = archive?.IsSolid == true;
                long total = 0;
                void Add(string name, long size, long packed, bool directory, int attributes, ZipArchiveEntry? z, IArchiveEntry? a)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateEntryName(name);
                    var entry = new Entry(name, size, directory, z, a);
                    if (entries.Count == MaxEntries || !entries.TryAdd(name, entry) || (attributes & 0xf000) == 0xa000
                        || ((attributes >> 16) & 0xf000) == 0xa000
                        || (attributes & (int)FileAttributes.ReparsePoint) != 0 || size < 0 || directory && size != 0) throw new InvalidDataException();
                    total = checked(total + size);
                    if (total > MaxExpandedBytes) throw new InvalidDataException();
                    // Solid RAR uses the aggregate ratio below; 7z exposes no per-entry packed size and is checked per folder in preflight.
                    if (!solid && archive?.Type != SharpCompress.Common.ArchiveType.SevenZip && size > 0
                        && (packed <= 0 || size / packed > 1000)) throw new InvalidDataException();
                    if (directory || ImageMime(name) is null) return;
                    if (size == 0 || size > MaxPageBytes) throw new InvalidDataException();
                    Pages.Add(entry);
                }
                if (zip is not null)
                    foreach (var e in zip.Entries)
                    {
                        if (e.IsEncrypted) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                        Add(e.FullName, e.Length, e.CompressedLength, e.Name.Length == 0, e.ExternalAttributes >> 16, e, null);
                    }
                else
                    foreach (var e in archive!.Entries)
                    {
                        if (e.IsEncrypted) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                        // SevenZip's FilePart.Index is a file index, not a volume index.
                        if (!e.IsComplete || e.IsSplitAfter || archive.Type == SharpCompress.Common.ArchiveType.Rar
                            && (e.VolumeIndexFirst != 0 || e.VolumeIndexLast != 0))
                            throw new ArchiveFeatureException("Multipart archives are not supported.");
                        if (e.LinkTarget is not null) throw new InvalidDataException();
                        // TAR exposes links separately; its inherited Attrib getter is not implemented.
                        var attributes = e is SharpCompress.Common.Tar.TarEntry ? 0 : e.Attrib ?? 0;
                        Add(e.Key ?? "", e.Size, e.CompressedSize, e.IsDirectory, attributes, null, e);
                    }
                if (total / Math.Max(1, archive?.TotalSize ?? file.Length) > 1000 || Pages.Count == 0) throw new InvalidDataException();
                Pages.Sort((a, b) => NaturalCompare(a.Name, b.Name));
            }
            catch (Exception ex) when (DecoderError(ex))
            {
                Dispose();
                throw new InvalidDataException("Invalid archive data.", ex);
            }
            catch { Dispose(); throw; }
        }

        private static bool DecoderError(Exception ex) => ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true
            || ex is IndexOutOfRangeException or NullReferenceException or FormatException or NotImplementedException;

        public (byte[]? Info, byte[]? Book) ReadMetadata(CancellationToken cancellationToken)
        {
            byte[]? info = null, book = null;
            try
            {
                // A single pass also verifies actual expanded sizes of unselected entries without retaining their bytes.
                Visit(null, (entry, stream) =>
                {
                    var isInfo = entry.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase);
                    var isBook = entry.Name.Equals("ComicBook.xml", StringComparison.OrdinalIgnoreCase);
                    var bytes = Consume(stream, entry.Size, isInfo || isBook ? MaxXmlBytes : null, cancellationToken);
                    if (isInfo) info = bytes;
                    if (isBook) book = bytes;
                });
                return (info, book);
            }
            catch (Exception ex) when (DecoderError(ex)) { throw new InvalidDataException("Invalid archive data.", ex); }
        }

        public byte[] ReadPage(Entry target)
        {
            try
            {
                byte[]? result = null;
                Visit(target, (entry, stream) => result = Consume(stream, entry.Size, entry == target ? MaxPageBytes : null, default));
                return result ?? throw new InvalidDataException();
            }
            catch (Exception ex) when (DecoderError(ex)) { throw new InvalidDataException("Invalid archive data.", ex); }
        }

        private void Visit(Entry? target, Action<Entry, Stream> consume)
        {
            if (solid)
            {
                using var reader = archive!.ExtractAllEntries();
                long expanded = 0;
                while (reader.MoveToNextEntry())
                {
                    if (!entries.TryGetValue(reader.Entry.Key ?? "", out var entry)) throw new InvalidDataException();
                    if (entry.Directory) continue;
                    expanded = checked(expanded + entry.Size);
                    if (expanded > MaxExpandedBytes) throw new InvalidDataException();
                    using var stream = reader.OpenEntryStream();
                    try { consume(entry, stream); }
                    catch { reader.Cancel(); throw; } // EntryStream.Dispose otherwise drains an untrusted remainder.
                    if (entry == target) return;
                }
                if (target is not null) throw new InvalidDataException();
            }
            else
            {
                void Read(Entry entry)
                {
                    if (entry.Directory) return;
                    using var stream = entry.Zip is not null ? entry.Zip.Open() : entry.Compressed!.OpenEntryStream();
                    consume(entry, stream);
                }
                if (target is not null) Read(target);
                else foreach (var entry in entries.Values) Read(entry);
            }
        }

        private static byte[]? Consume(Stream stream, long size, int? retainLimit, CancellationToken cancellationToken)
        {
            if (size < 0 || size > MaxExpandedBytes || retainLimit.HasValue && size > retainLimit.Value) throw new InvalidDataException();
            var bytes = retainLimit.HasValue ? new byte[(int)size] : null;
            var buffer = bytes is null ? ArrayPool<byte>.Shared.Rent(81920) : null;
            try
            {
                long read = 0;
                while (read < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = bytes is null ? buffer!.AsSpan(0, (int)Math.Min(buffer!.Length, size - read)) : bytes.AsSpan((int)read);
                    var count = stream.Read(destination);
                    if (count == 0) throw new InvalidDataException("Truncated archive entry.");
                    read += count;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("Archive entry exceeds its declared size.");
                return bytes;
            }
            finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
        }

        public void Dispose() { zip?.Dispose(); archive?.Dispose(); }

        private static ulong RarInt(BinaryReader reader)
        {
            ulong value = 0;
            for (var shift = 0; shift < 63; shift += 7)
            {
                var b = reader.ReadByte();
                value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException();
        }

        private static void PreflightRar(FileStream file, CancellationToken cancellationToken)
        {
            // RAR4 windows are at most 4 MiB; its byte-sized PPMd model selector is inherently capped at 256 MiB.
            using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, true);
            var signature = reader.ReadBytes(7);
            var v5 = signature.AsSpan().SequenceEqual("Rar!\x1a\x07\x01"u8);
            if (!v5 && !signature.AsSpan().SequenceEqual("Rar!\x1a\x07\x00"u8)) throw new InvalidDataException();
            if (v5 && reader.ReadByte() != 0) throw new InvalidDataException();
            var count = 0;
            long headerBytes = 0;
            while (file.Position < file.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = file.Position;
                long end, packed = 0;
                if (v5)
                {
                    reader.ReadUInt32();
                    var length = checked((long)RarInt(reader));
                    end = checked(file.Position + length);
                    if (length > MaxHeaderBytes || end > file.Length) throw new InvalidDataException();
                    var type = RarInt(reader);
                    var flags = RarInt(reader);
                    var extra = (flags & 1) != 0 ? checked((long)RarInt(reader)) : 0;
                    if ((flags & 2) != 0) packed = checked((long)RarInt(reader));
                    if ((flags & 0x18) != 0) throw new ArchiveFeatureException("Multipart archives are not supported.");
                    if (type == 4) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                    if (type == 1 && (RarInt(reader) & 1) != 0) throw new ArchiveFeatureException("Multipart archives are not supported.");
                    if (type is 2 or 3)
                    {
                        if (++count > MaxEntries) throw new InvalidDataException();
                        var fileFlags = RarInt(reader);
                        if ((fileFlags & 8) != 0 || RarInt(reader) > MaxExpandedBytes) throw new InvalidDataException();
                        var attributes = RarInt(reader);
                        if ((attributes & 0xf000) == 0xa000) throw new InvalidDataException();
                        if ((fileFlags & 2) != 0) reader.ReadUInt32();
                        if ((fileFlags & 4) != 0) reader.ReadUInt32();
                        var compression = RarInt(reader);
                        if ((compression & 63) != 0 || (0x20000UL << (int)((compression >> 10) & 15)) > MaxDictionaryBytes)
                            throw new InvalidDataException("RAR dictionary exceeds 64 MiB or uses an unsupported version.");
                        RarInt(reader); // host OS
                        var nameLength = checked((long)RarInt(reader));
                        if (nameLength > 4096) throw new InvalidDataException();
                        file.Position = checked(file.Position + nameLength);
                        if (extra < 0 || file.Position != end - extra) throw new InvalidDataException();
                        while (file.Position < end)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var extraLength = checked((long)RarInt(reader));
                            var next = checked(file.Position + extraLength);
                            if (next > end || extraLength == 0) throw new InvalidDataException();
                            var extraType = RarInt(reader);
                            if (extraType == 1) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                            if (extraType == 5) throw new InvalidDataException("Archive links are not supported.");
                            file.Position = next;
                        }
                    }
                    if (type == 5 && (RarInt(reader) & 1) != 0) throw new ArchiveFeatureException("Multipart archives are not supported.");
                }
                else
                {
                    reader.ReadUInt16();
                    var type = reader.ReadByte();
                    var flags = reader.ReadUInt16();
                    var length = reader.ReadUInt16();
                    end = checked(start + length);
                    if (length < 7 || end > file.Length) throw new InvalidDataException();
                    if ((flags & 0x8000) != 0) packed = reader.ReadUInt32();
                    if (type == 0x73)
                    {
                        if ((flags & 1) != 0) throw new ArchiveFeatureException("Multipart archives are not supported.");
                        if ((flags & 0x80) != 0) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                    }
                    if (type is 0x74 or 0x7a)
                    {
                        if (++count > MaxEntries || length < 32) throw new InvalidDataException();
                        if ((flags & 3) != 0) throw new ArchiveFeatureException("Multipart archives are not supported.");
                        if ((flags & 4) != 0) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                        reader.ReadUInt32(); reader.ReadByte(); reader.ReadUInt32(); reader.ReadUInt32();
                        reader.ReadByte(); reader.ReadByte();
                        if (reader.ReadUInt16() > 4096) throw new InvalidDataException();
                        if ((reader.ReadUInt32() & 0xf000) == 0xa000) throw new InvalidDataException();
                        if ((flags & 0x100) != 0) packed = checked(packed + ((long)reader.ReadUInt32() << 32));
                    }
                    if (type == 0x7b && (flags & 1) != 0) throw new ArchiveFeatureException("Multipart archives are not supported.");
                }
                headerBytes = checked(headerBytes + end - start);
                if (headerBytes > MaxHeaderBytes || file.Position > end || packed < 0 || packed > file.Length - end) throw new InvalidDataException();
                file.Position = end + packed;
            }
        }

        private static void PreflightSevenZip(FileStream file, CancellationToken cancellationToken)
        {
            using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, true);
            if (!reader.ReadBytes(6).AsSpan().SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c]))
                throw new InvalidDataException();
            reader.ReadUInt16(); reader.ReadUInt32();
            var offset = reader.ReadUInt64();
            var length = reader.ReadUInt64();
            reader.ReadUInt32();
            if (length > MaxHeaderBytes || offset > (ulong)(file.Length - 32)
                || length > (ulong)(file.Length - 32) - offset) throw new InvalidDataException();
            file.Position = checked(32 + (long)offset);
            var header = reader.ReadBytes((int)length);
            for (var depth = 0; depth < 2; depth++)
            {
                using var input = new MemoryStream(header, false);
                using var parser = new SevenHeader(input, file.Length, cancellationToken);
                var type = parser.ReadByte();
                if (type == 1) { parser.Header(); return; }
                if (type != 23 || depth != 0) throw new InvalidDataException();
                var streams = parser.Streams(MaxHeaderBytes);
                if (streams.Folders.Count != 1 || streams.Packed.Count != 1
                    || streams.Folders[0].Methods.Count != 1) throw new NotSupportedException("Unsupported encoded 7z header.");
                var folder = streams.Folders[0];
                var method = folder.Methods[0];
                var packed = streams.Packed[0];
                if (packed > MaxHeaderBytes || streams.Offset > file.Length - 32 || packed > file.Length - 32 - streams.Offset)
                    throw new InvalidDataException();
                file.Position = 32 + streams.Offset;
                // Only the small encoded header is buffered; image/archive payloads remain streamed.
                using var compressed = new MemoryStream(reader.ReadBytes((int)packed), false);
                if (method.Id == 0)
                    header = Consume(compressed, folder.Size, MaxHeaderBytes, cancellationToken)!;
                else if (method.Id is 0x030101 or 0x21)
                {
                    using var decoder = LzmaStream.Create(method.Properties, compressed, packed, folder.Size, true);
                    header = Consume(decoder, folder.Size, MaxHeaderBytes, cancellationToken)!;
                }
                else throw new NotSupportedException("Unsupported encoded 7z header codec.");
            }
            throw new InvalidDataException();
        }

        // SharpCompress 0.50.4 has no decoder-memory limit. Inspect headers before it allocates dictionaries,
        // file arrays, or decoded headers. Accept bounded single-input coder chains (including solid LZMA/LZMA2).
        private sealed class SevenHeader(Stream stream, long sourceLength, CancellationToken cancellationToken) : BinaryReader(stream)
        {
            public sealed record Method(ulong Id, byte[] Properties);
            public sealed class Folder
            {
                public List<Method> Methods { get; } = [];
                public long Size;
                public bool Crc;
                public int Substreams = 1;
            }
            public sealed record StreamInfo(long Offset, List<long> Packed, List<Folder> Folders);

            private ulong Number()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = ReadByte();
                ulong value = 0;
                for (var i = 0; i < 8; i++)
                {
                    var mask = 0x80 >> i;
                    if ((first & mask) == 0) return value | ((ulong)(first & (mask - 1)) << (8 * i));
                    value |= (ulong)ReadByte() << (8 * i);
                }
                return value;
            }
            private int Count(int maximum = MaxEntries)
            {
                var n = Number();
                return n <= (ulong)maximum ? (int)n : throw new InvalidDataException();
            }
            private long Size(long maximum)
            {
                var n = Number();
                return n <= (ulong)maximum ? (long)n : throw new InvalidDataException();
            }
            private void Expect(byte value) { if (ReadByte() != value) throw new InvalidDataException(); }
            private void Skip(long length)
            {
                if (length < 0 || length > BaseStream.Length - BaseStream.Position) throw new InvalidDataException();
                BaseStream.Position += length;
            }
            private bool[] Defined(int count)
            {
                var bits = new bool[count];
                if (ReadByte() != 0) { Array.Fill(bits, true); return bits; }
                byte b = 0;
                for (var i = 0; i < count; i++)
                {
                    if (i % 8 == 0) b = ReadByte();
                    bits[i] = (b & (0x80 >> (i % 8))) != 0;
                }
                return bits;
            }
            private bool[] Digests(int count)
            {
                var bits = Defined(count);
                Skip(4L * bits.Count(b => b));
                return bits;
            }

            private Folder ReadFolder()
            {
                var folder = new Folder();
                var count = Count(4);
                if (count == 0) throw new InvalidDataException();
                long memory = 0;
                for (var i = 0; i < count; i++)
                {
                    var flags = ReadByte();
                    var idLength = flags & 15;
                    if (idLength is 0 or > 8 || (flags & 0xc0) != 0) throw new InvalidDataException();
                    ulong id = 0;
                    for (var j = 0; j < idLength; j++) id = (id << 8) | ReadByte();
                    if (id == 0x06f10701) throw new ArchiveFeatureException("Encrypted archives are not supported.");
                    if ((flags & 16) != 0 && (Number() != 1 || Number() != 1))
                        throw new NotSupportedException("Unsupported multi-input 7z codec.");
                    var properties = (flags & 32) != 0 ? ReadBytes(Count(16)) : [];
                    switch (id)
                    {
                        case 0x030101:
                            if (properties.Length != 5 || properties[0] >= 225) throw new InvalidDataException();
                            memory += BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
                            break;
                        case 0x21:
                            if (properties.Length != 1 || properties[0] > 40) throw new InvalidDataException();
                            memory += properties[0] == 40 ? uint.MaxValue : (2L | (properties[0] & 1)) << (properties[0] / 2 + 11);
                            break;
                        case 0x030401:
                            if (properties.Length != 5 || properties[0] is < 2 or > 64) throw new InvalidDataException();
                            memory += BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
                            break;
                        case 0x040202: memory += 4 * 1024 * 1024; break;
                        case 0x040108: memory += 128 * 1024; break;
                        case 3:
                            if (properties.Length != 1) throw new InvalidDataException();
                            break;
                        case 0: case 0x03030103: case 0x03030205: case 0x03030401: case 0x03030501:
                        case 0x03030701: case 0x03030805: case 0x0a: case 0x0b:
                            if (properties.Length != 0) throw new InvalidDataException();
                            break;
                        default: throw new NotSupportedException("Unsupported 7z codec.");
                    }
                    if (memory > MaxDictionaryBytes) throw new InvalidDataException("7z decoder dictionaries exceed 64 MiB.");
                    folder.Methods.Add(new Method(id, properties));
                }
                var inputs = new HashSet<int>();
                var outputs = new HashSet<int>();
                var links = new Dictionary<int, int>();
                for (var i = 1; i < count; i++)
                {
                    var input = Count(count - 1);
                    var output = Count(count - 1);
                    if (!inputs.Add(input) || !outputs.Add(output)) throw new InvalidDataException();
                    links.Add(input, output);
                }
                for (var i = 0; i < count; i++)
                {
                    var visited = new HashSet<int>();
                    for (var current = i; links.TryGetValue(current, out var next); current = next)
                        if (!visited.Add(current)) throw new InvalidDataException();
                }
                return folder;
            }

            public StreamInfo Streams(long limit)
            {
                long offset = 0, expanded = 0;
                var packed = new List<long>();
                var folders = new List<Folder>();
                var type = ReadByte();
                if (type == 6)
                {
                    offset = Size(MaxArchiveBytes);
                    var count = Count();
                    Expect(9);
                    for (var i = 0; i < count; i++) packed.Add(Size(MaxArchiveBytes));
                    type = ReadByte();
                    if (type == 10) { Digests(count); type = ReadByte(); }
                    if (type != 0) throw new InvalidDataException();
                    type = ReadByte();
                }
                if (type == 7)
                {
                    Expect(11);
                    var count = Count();
                    Expect(0); // external folder tables would bypass these bounds
                    for (var i = 0; i < count; i++) folders.Add(ReadFolder());
                    Expect(12);
                    foreach (var folder in folders)
                    {
                        for (var i = 0; i < folder.Methods.Count; i++)
                        {
                            var size = Size(limit);
                            if (i == 0) folder.Size = size;
                            else if (folder.Size != size) throw new InvalidDataException(); // accepted filters preserve length
                        }
                        expanded = checked(expanded + folder.Size);
                        if (expanded > limit) throw new InvalidDataException();
                    }
                    type = ReadByte();
                    if (type == 10)
                    {
                        var crcs = Digests(count);
                        for (var i = 0; i < count; i++) folders[i].Crc = crcs[i];
                        type = ReadByte();
                    }
                    if (type != 0) throw new InvalidDataException();
                    type = ReadByte();
                }
                if (packed.Count != folders.Count) throw new InvalidDataException();
                if (offset > sourceLength - 32 || packed.Sum() > sourceLength - 32 - offset) throw new InvalidDataException();
                for (var i = 0; i < folders.Count; i++)
                    if (folders[i].Size > 0 && (packed[i] == 0 || folders[i].Size / packed[i] > 1000)) throw new InvalidDataException();
                if (type == 8)
                {
                    type = ReadByte();
                    if (type == 13)
                    {
                        var total = 0;
                        foreach (var folder in folders)
                        {
                            folder.Substreams = Count();
                            total = checked(total + folder.Substreams);
                            if (total > MaxEntries) throw new InvalidDataException();
                        }
                        type = ReadByte();
                    }
                    if (type == 9)
                    {
                        foreach (var folder in folders)
                        {
                            long sum = 0;
                            for (var i = 1; i < folder.Substreams; i++) sum = checked(sum + Size(limit));
                            if (sum > folder.Size) throw new InvalidDataException();
                        }
                        type = ReadByte();
                    }
                    else if (folders.Any(f => f.Substreams > 1)) throw new InvalidDataException();
                    if (type == 10)
                    {
                        Digests(folders.Sum(f => f.Substreams == 1 && f.Crc ? 0 : f.Substreams));
                        type = ReadByte();
                    }
                    if (type != 0) throw new InvalidDataException();
                    type = ReadByte();
                }
                if (type != 0) throw new InvalidDataException();
                return new StreamInfo(offset, packed, folders);
            }

            public void Header()
            {
                var type = ReadByte();
                if (type == 2)
                {
                    while (ReadByte() != 0) Skip(Size(MaxHeaderBytes));
                    type = ReadByte();
                }
                if (type == 3) throw new NotSupportedException("External 7z header streams are not supported.");
                if (type == 4) { Streams(MaxExpandedBytes); type = ReadByte(); }
                if (type != 5) throw new InvalidDataException();
                var files = Count();
                while ((type = ReadByte()) != 0)
                {
                    var length = Size(MaxHeaderBytes);
                    var end = checked(BaseStream.Position + length);
                    if (end > BaseStream.Length) throw new InvalidDataException();
                    if (type == 17) Expect(0); // names must live in this bounded header
                    else if (type is 18 or 19 or 20 or 21 or 24) { Defined(files); Expect(0); }
                    if (BaseStream.Position > end) throw new InvalidDataException();
                    BaseStream.Position = end;
                }
                Expect(0);
                if (BaseStream.Position != BaseStream.Length) throw new InvalidDataException();
            }
        }
    }
}
