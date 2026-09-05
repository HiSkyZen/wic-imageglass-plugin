/*
Fast JXR tile-layout parser.
JPEG XR codestream header parsing follows the Microsoft jxrlib ReadWMIHeader layout.
MIT License.
*/
using System.Buffers.Binary;

namespace WicCodec.Wic;

internal sealed class JxrTileLayout
{
    public required int[] X { get; init; }
    public required int[] Y { get; init; }
    public ulong[]? TileWeights { get; init; }

    public int Columns => X.Length - 1;
    public int Rows => Y.Length - 1;


    private sealed class CacheEntry
    {
        public required string Path;
        public long FileLength;
        public long LastWriteTicks;
        public required JxrTileLayout Layout;
        public long LastUse;
    }

    private const int MaxEntries = 64;
    private const int MaxFallbackProbeBytes = 4 * 1024 * 1024;
    private static readonly Lock _lock = new();
    private static readonly List<CacheEntry> _cache = [];
    private static long _clock;


    public static bool TryGet(string path, int expectedWidth, int expectedHeight,
        out JxrTileLayout? layout)
    {
        layout = null;
        if (!TryGetStamp(path, out var length, out var ticks)) return false;

        lock (_lock)
        {
            foreach (var entry in _cache)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Path, path)) continue;
                if (entry.FileLength != length || entry.LastWriteTicks != ticks) continue;

                entry.LastUse = ++_clock;
                layout = entry.Layout;
                return true;
            }
        }

        if (!TryRead(path, expectedWidth, expectedHeight, out layout) || layout is null)
        {
            return false;
        }

        lock (_lock)
        {
            for (var i = _cache.Count - 1; i >= 0; i--)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(_cache[i].Path, path))
                {
                    _cache.RemoveAt(i);
                }
            }

            _cache.Add(new CacheEntry
            {
                Path = path,
                FileLength = length,
                LastWriteTicks = ticks,
                Layout = layout,
                LastUse = ++_clock,
            });

            while (_cache.Count > MaxEntries)
            {
                var victim = 0;
                var oldest = _cache[0].LastUse;
                for (var i = 1; i < _cache.Count; i++)
                {
                    if (_cache[i].LastUse >= oldest) continue;
                    oldest = _cache[i].LastUse;
                    victim = i;
                }
                _cache.RemoveAt(victim);
            }
        }

        return true;
    }


    private static bool TryRead(string path, int expectedWidth, int expectedHeight,
        out JxrTileLayout? layout)
    {
        layout = null;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.RandomAccess);

            if (!TryLocateCodestream(stream, out var imageOffset, out var imageByteCount)) return false;

            stream.Position = imageOffset;

            // The fixed header plus the maximum practical tile-boundary table is tiny.
            // Read 64 KiB first; grow only if a pathological header needs more.
            var available = Math.Min(128 * 1024L, stream.Length - imageOffset);
            if (available < 16 || available > int.MaxValue) return false;

            var bytes = new byte[(int)available];
            stream.ReadExactly(bytes);

            ReadOnlySpan<byte> signature = "WMPHOTO\0"u8;
            if (!bytes.AsSpan().StartsWith(signature)) return false;

            var reader = new MsbBitReader(bytes.AsSpan(8));

            _ = reader.Read(4); // codec version
            _ = reader.Read(4); // codec subversion

            var tilingPresent = reader.Read(1) != 0;
            var bitstreamFormat = checked((int)reader.Read(1));
            _ = reader.Read(3); // presentation orientation
            var hasIndexTable = reader.Read(1) != 0;
            var overlap = reader.Read(2);
            if (overlap == 3) return false;

            var abbreviatedHeader = reader.Read(1) != 0;
            _ = reader.Read(1); // long-word flag
            var inscribed = reader.Read(1) != 0;
            _ = reader.Read(1); // trim flexbits
            var tileStretch = reader.Read(1) != 0;
            _ = reader.Read(1); // red/blue swap
            _ = reader.Read(1); // reserved
            _ = reader.Read(1); // alpha

            _ = reader.Read(4); // source color format
            var sourceBitDepth = checked((int)reader.Read(4));

            var sizeBits = abbreviatedHeader ? 16 : 32;
            var width64 = (long)reader.Read(sizeBits) + 1;
            var height64 = (long)reader.Read(sizeBits) + 1;
            if (width64 <= 0 || height64 <= 0 || width64 > int.MaxValue || height64 > int.MaxValue)
            {
                return false;
            }

            var width = (int)width64;
            var height = (int)height64;

            // WIC exposes the visible image dimensions. For the HDR corpus these match the
            // codestream header directly; reject anything surprising and fall back to 256px bands.
            if (width != expectedWidth || height != expectedHeight) return false;

            var verticalMinus1 = 0;
            var horizontalMinus1 = 0;
            if (tilingPresent)
            {
                verticalMinus1 = checked((int)reader.Read(12));
                horizontalMinus1 = checked((int)reader.Read(12));
            }

            var columns = verticalMinus1 + 1;
            var rows = horizontalMinus1 + 1;
            if (columns <= 0 || rows <= 0 || columns > 4096 || rows > 4096) return false;

            var x = new int[columns + 1];
            var y = new int[rows + 1];
            x[0] = 0;
            y[0] = 0;

            var tileSizeBits = abbreviatedHeader ? 8 : 16;

            long macroblock = 0;
            for (var i = 0; i < verticalMinus1; i++)
            {
                macroblock += reader.Read(tileSizeBits);
                var pixel = macroblock * 16;
                if (pixel <= x[i] || pixel >= width || pixel > int.MaxValue) return false;
                x[i + 1] = (int)pixel;
            }
            x[^1] = width;

            macroblock = 0;
            for (var i = 0; i < horizontalMinus1; i++)
            {
                macroblock += reader.Read(tileSizeBits);
                var pixel = macroblock * 16;
                if (pixel <= y[i] || pixel >= height || pixel > int.MaxValue) return false;
                y[i + 1] = (int)pixel;
            }
            y[^1] = height;

            if (tileStretch)
            {
                for (var i = 0; i < checked(columns * rows); i++) _ = reader.Read(8);
            }

            if (inscribed)
            {
                _ = reader.Read(6);
                _ = reader.Read(6);
                _ = reader.Read(6);
                _ = reader.Read(6);
            }

            ulong[]? tileWeights = null;
            var weightReader = reader;
            _ = TryReadTileWeights(
                ref weightReader,
                sourceBitDepth,
                bitstreamFormat,
                hasIndexTable,
                columns,
                rows,
                imageByteCount,
                out tileWeights);

            layout = new JxrTileLayout { X = x, Y = y, TileWeights = tileWeights };
            return true;
        }
        catch
        {
            layout = null;
            return false;
        }
    }


    private static bool TryReadTileWeights(ref MsbBitReader reader,
        int sourceBitDepth, int bitstreamFormat, bool hasIndexTable,
        int columns, int rows, long imageByteCount, out ulong[]? weights)
    {
        weights = null;
        if (!hasIndexTable || columns <= 0 || rows <= 0 || imageByteCount <= 0) return false;

        try
        {
            reader.AlignByte();

            var internalColor = checked((int)reader.Read(3));
            _ = reader.Read(1); // scaled arithmetic
            var subband = checked((int)reader.Read(4));

            var channels = internalColor switch
            {
                0 => 1, // Y_ONLY
                1 or 2 or 3 => 3,
                4 => 4, // CMYK
                6 => checked((int)reader.Read(4) + 1), // NCOMPONENT
                _ => 0,
            };
            if (channels <= 0 || channels > 16) return false;

            if (internalColor is 1 or 2 or 3)
            {
                _ = reader.Read(8);
            }
            else if (internalColor == 6)
            {
                _ = reader.Read(4);
            }

            // Source BITDEPTH_BITS: 16/16S/32/32S have one 8-bit parameter;
            // 32F has mantissa length + exponent bias.
            if (sourceBitDepth is 2 or 3 or 5 or 6)
            {
                _ = reader.Read(8);
            }
            else if (sourceBitDepth == 7)
            {
                _ = reader.Read(8);
                _ = reader.Read(8);
            }

            if (reader.Read(1) != 0) SkipQuantizer(ref reader, channels);

            if (subband != 3) // not DC-only
            {
                if (reader.Read(1) == 0 && reader.Read(1) != 0)
                {
                    SkipQuantizer(ref reader, channels);
                }

                if (subband != 2) // not "no highpass"
                {
                    if (reader.Read(1) == 0 && reader.Read(1) != 0)
                    {
                        SkipQuantizer(ref reader, channels);
                    }
                }
            }

            reader.AlignByte();

            var bands = subband switch
            {
                3 => 1,
                2 => 2,
                1 => 3,
                0 => 4,
                _ => 0,
            };
            if (bands == 0) return false;

            var bitIoCount = bitstreamFormat == 0
                ? columns
                : checked(columns * bands);
            var entryCount = checked(bitIoCount * rows);
            if (entryCount <= 0 || entryCount > 16_777_216) return false;

            if (reader.Read(16) != 1) return false;

            var offsets = new ulong[entryCount];
            for (var i = 0; i < entryCount; i++)
            {
                offsets[i] = ReadVlWord(ref reader);
            }

            var extraHeaderBytes = ReadVlWord(ref reader);
            reader.AlignByte();

            var dataStart = checked((ulong)reader.BytePosition + extraHeaderBytes);
            var codestreamBytes = checked((ulong)imageByteCount);
            if (dataStart >= codestreamBytes) return false;

            var tileCount = checked(columns * rows);
            var firstBandStride = bitstreamFormat == 0 ? 1 : bands;
            var result = new ulong[tileCount];

            for (var tile = 0; tile < tileCount; tile++)
            {
                var offsetIndex = checked(tile * firstBandStride);
                var start = offsets[offsetIndex];

                ulong end;
                if (tile + 1 < tileCount)
                {
                    end = offsets[checked((tile + 1) * firstBandStride)];
                }
                else
                {
                    end = codestreamBytes - dataStart;
                }

                if (end < start) return false;
                result[tile] = end - start;
            }

            weights = result;
            return true;
        }
        catch
        {
            weights = null;
            return false;
        }
    }


    private static void SkipQuantizer(ref MsbBitReader reader, int channels)
    {
        var mode = channels > 1 ? checked((int)reader.Read(2)) : 0;
        _ = reader.Read(8);

        if (mode == 1)
        {
            _ = reader.Read(8);
        }
        else if (mode > 1)
        {
            for (var i = 1; i < channels; i++) _ = reader.Read(8);
        }
    }


    private static ulong ReadVlWord(ref MsbBitReader reader)
    {
        reader.AlignByte();

        var prefix = reader.Read(8);
        if (prefix is 0xFD or 0xFE or 0xFF) return 0;

        if (prefix < 0xFB)
        {
            return ((ulong)prefix << 8) | reader.Read(8);
        }

        var large = prefix - 0xFB;
        ulong value = 0;

        if (large != 0)
        {
            value = (ulong)reader.Read(16) << 16;
            value = (value | reader.Read(16)) << 16;
            value <<= 16;
        }

        value |= (ulong)reader.Read(16) << 16;
        value |= reader.Read(16);
        return value;
    }


    private static bool TryLocateCodestream(FileStream stream, out long imageOffset,
        out long imageByteCount)
    {
        imageOffset = 0;
        imageByteCount = 0;

        // JXR is a TIFF-like little-endian container. BCC0/BCC1 are ImageOffset/ImageByteCount.
        Span<byte> header = stackalloc byte[8];
        stream.Position = 0;
        if (stream.Read(header) == header.Length
            && header[0] == (byte)'I'
            && header[1] == (byte)'I')
        {
            var ifdOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (ifdOffset > 0 && ifdOffset < stream.Length - 2)
            {
                stream.Position = ifdOffset;

                Span<byte> countBytes = stackalloc byte[2];
                if (stream.Read(countBytes) == 2)
                {
                    var count = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
                    Span<byte> entry = stackalloc byte[12];
                    long candidateOffset = 0;
                    long candidateCount = 0;

                    for (var i = 0; i < count; i++)
                    {
                        if (stream.Read(entry) != entry.Length) break;

                        var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry[0..2]);
                        var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..4]);
                        var valueCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..8]);
                        if (type != 4 || valueCount != 1) continue;

                        var value = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
                        if (tag == 0xBCC0) candidateOffset = value;
                        else if (tag == 0xBCC1) candidateCount = value;
                    }

                    if (candidateOffset > 0
                        && candidateOffset < stream.Length - 8
                        && HasSignatureAt(stream, candidateOffset))
                    {
                        imageOffset = candidateOffset;
                        imageByteCount = candidateCount > 0
                            ? Math.Min(candidateCount, stream.Length - candidateOffset)
                            : stream.Length - candidateOffset;
                        return true;
                    }
                }
            }
        }

        // Compatibility fallback for unusual containers: scan only the leading metadata area.
        stream.Position = 0;
        var probeLength = (int)Math.Min(stream.Length, MaxFallbackProbeBytes);
        if (probeLength < 8) return false;

        var probe = new byte[probeLength];
        stream.ReadExactly(probe);
        var index = probe.AsSpan().IndexOf("WMPHOTO\0"u8);
        if (index < 0) return false;

        imageOffset = index;
        imageByteCount = stream.Length - imageOffset;
        return true;
    }


    private static bool HasSignatureAt(FileStream stream, long offset)
    {
        var saved = stream.Position;
        try
        {
            Span<byte> signature = stackalloc byte[8];
            stream.Position = offset;
            return stream.Read(signature) == signature.Length
                && signature.SequenceEqual("WMPHOTO\0"u8);
        }
        finally
        {
            stream.Position = saved;
        }
    }


    private static bool TryGetStamp(string path, out long length, out long ticks)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                length = 0;
                ticks = 0;
                return false;
            }

            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            length = 0;
            ticks = 0;
            return false;
        }
    }


    private ref struct MsbBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bitOffset;

        public MsbBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitOffset = 0;
        }

        public int BytePosition => _bitOffset >> 3;


        public void AlignByte()
        {
            _bitOffset = (_bitOffset + 7) & ~7;
        }


        public uint Read(int count)
        {
            if (count is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
            if (_bitOffset + count > _data.Length * 8) throw new EndOfStreamException();

            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                var bit = _bitOffset++;
                var current = _data[bit >> 3];
                value = (value << 1) | (uint)((current >> (7 - (bit & 7))) & 1);
            }
            return value;
        }
    }
}
