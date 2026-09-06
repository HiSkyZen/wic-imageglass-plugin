/*
Fast JXR metadata cache for ImageGlass.
MIT License.
*/
using ImageGlass.SDK.Plugins;

namespace WicCodec.Wic;

/// <summary>
/// Caches immutable JPEG XR metadata by file stamp. ICC bytes are retained as managed data and
/// republished into NativeBuffers on every hit so no cached entry ever retains a stale ABI pointer.
/// </summary>
internal static unsafe class JxrMetadataCache
{
    private const int MaxEntries = 64;

    private sealed class Entry
    {
        public required string Path;
        public long FileLength;
        public long LastWriteTicks;
        public int Width;
        public int Height;
        public int PixelFormat;
        public int HasAlpha;
        public int HdrTransferFn;
        public int FrameCount;
        public long FileSizeBytes;
        public int Orientation;
        public int ColorSpace;
        public Guid SourcePixelFormat;
        public int SourceOrientation;
        public byte[]? Icc;
        public long LastUse;
    }

    private static readonly Lock _lock = new();
    private static readonly List<Entry> _entries = [];
    private static long _clock;

    public static bool TryFill(string path, IGImageInfo* outInfo)
    {
        if (outInfo == null || !TryGetStamp(path, out var length, out var ticks)) return false;

        lock (_lock)
        {
            Entry? hit = null;
            foreach (var entry in _entries)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Path, path)) continue;
                if (entry.FileLength != length || entry.LastWriteTicks != ticks) continue;
                hit = entry;
                break;
            }

            if (hit is null) return false;
            hit.LastUse = ++_clock;

            *outInfo = default;
            outInfo->Width = hit.Width;
            outInfo->Height = hit.Height;
            outInfo->PixelFormat = hit.PixelFormat;
            outInfo->HasAlpha = hit.HasAlpha;
            outInfo->HdrTransferFn = hit.HdrTransferFn;
            outInfo->FrameCount = hit.FrameCount;
            outInfo->FileSizeBytes = hit.FileSizeBytes;
            outInfo->Orientation = hit.Orientation;
            outInfo->ColorSpace = hit.ColorSpace;

            if (hit.Icc is { Length: > 0 })
            {
                var block = NativeBuffers.PublishIccProfile(hit.Icc);
                if (block != null)
                {
                    outInfo->IccProfileData = block;
                    outInfo->IccProfileSize = hit.Icc.Length;
                }
            }

            return true;
        }
    }

    public static bool TryGetDecodeTraits(string path, out Guid sourcePixelFormat, out int sourceOrientation)
    {
        sourcePixelFormat = default;
        sourceOrientation = 1;
        if (!TryGetStamp(path, out var length, out var ticks)) return false;

        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Path, path)) continue;
                if (entry.FileLength != length || entry.LastWriteTicks != ticks) continue;
                if (entry.SourcePixelFormat == default) return false;

                entry.LastUse = ++_clock;
                sourcePixelFormat = entry.SourcePixelFormat;
                sourceOrientation = entry.SourceOrientation;
                return true;
            }
        }

        return false;
    }

    public static void Store(string path, IGImageInfo* info, byte[]? icc,
        Guid sourcePixelFormat, int sourceOrientation)
    {
        if (info == null || !TryGetStamp(path, out var length, out var ticks)) return;

        lock (_lock)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(_entries[i].Path, path))
                {
                    _entries.RemoveAt(i);
                }
            }

            _entries.Add(new Entry
            {
                Path = path,
                FileLength = length,
                LastWriteTicks = ticks,
                Width = info->Width,
                Height = info->Height,
                PixelFormat = info->PixelFormat,
                HasAlpha = info->HasAlpha,
                HdrTransferFn = info->HdrTransferFn,
                FrameCount = info->FrameCount,
                FileSizeBytes = info->FileSizeBytes,
                Orientation = info->Orientation,
                ColorSpace = info->ColorSpace,
                SourcePixelFormat = sourcePixelFormat,
                SourceOrientation = sourceOrientation,
                Icc = icc,
                LastUse = ++_clock,
            });

            while (_entries.Count > MaxEntries)
            {
                var victim = 0;
                var oldest = _entries[0].LastUse;
                for (var i = 1; i < _entries.Count; i++)
                {
                    if (_entries[i].LastUse >= oldest) continue;
                    oldest = _entries[i].LastUse;
                    victim = i;
                }
                _entries.RemoveAt(victim);
            }
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
}
