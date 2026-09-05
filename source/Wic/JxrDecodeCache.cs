/*
Fast JXR HDR decode cache for ImageGlass.
Derived from the WIC codec plugin architecture; MIT License.
*/
using ImageGlass.SDK.Plugins;

namespace WicCodec.Wic;

/// <summary>
/// Small full-resolution RGBA cache used to collapse duplicate ImageGlass decode requests.
/// ImageGlass wraps plugin buffers as immutable SKImage pixel storage, so the cache retains a
/// reference to the exact same native buffer instead of copying hundreds of MiB per request.
/// </summary>
internal static unsafe class JxrDecodeCache
{
    private const int MaxEntries = 3;
    private const long MaxBytes = 1536L * 1024 * 1024;

    private sealed class Entry
    {
        public required string Path;
        public long FileLength;
        public long LastWriteTicks;
        public nint Data;
        public nuint ByteCount;
        public int Width;
        public int Height;
        public int Stride;
        public int PixelFormat;
        public long LastUse;
    }

    private static readonly Lock _lock = new();
    private static readonly List<Entry> _entries = [];
    private static long _residentBytes;
    private static long _clock;

    public static bool TryCopyToHost(string path, IGPixelBuffer* outBuf)
    {
        if (outBuf == null || !TryGetStamp(path, out var length, out var ticks)) return false;

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

            if (hit is null || hit.Data == 0 || hit.ByteCount == 0) return false;
            if (!NativeBuffers.RetainPixels((void*)hit.Data)) return false;

            hit.LastUse = ++_clock;
            outBuf->Data = (byte*)hit.Data;
            outBuf->Width = hit.Width;
            outBuf->Height = hit.Height;
            outBuf->Stride = hit.Stride;
            outBuf->PixelFormat = hit.PixelFormat;
            outBuf->ReleaseContext = (void*)hit.Data;
            return true;
        }
    }

    public static void Store(string path, IGPixelBuffer* buffer)
    {
        if (buffer == null || buffer->Data == null || buffer->Width <= 0 || buffer->Height <= 0
            || buffer->Stride <= 0 || !TryGetStamp(path, out var length, out var ticks))
        {
            return;
        }

        var byteCount64 = (long)buffer->Stride * buffer->Height;
        if (byteCount64 <= 0 || byteCount64 > MaxBytes) return;

        var byteCount = (nuint)byteCount64;

        lock (_lock)
        {
            // Retain before publishing the entry. Decode() calls Store() before returning the
            // initial host reference, so the buffer cannot disappear during this operation.
            if (!NativeBuffers.RetainPixels(buffer->Data)) return;
            // Replace a stale/older copy of the same path.
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(_entries[i].Path, path)) continue;
                RemoveAt(i);
            }

            _entries.Add(new Entry
            {
                Path = path,
                FileLength = length,
                LastWriteTicks = ticks,
                Data = (nint)buffer->Data,
                ByteCount = byteCount,
                Width = buffer->Width,
                Height = buffer->Height,
                Stride = buffer->Stride,
                PixelFormat = buffer->PixelFormat,
                LastUse = ++_clock,
            });
            _residentBytes += byteCount64;

            while (_entries.Count > MaxEntries || _residentBytes > MaxBytes)
            {
                var victim = 0;
                var oldest = _entries[0].LastUse;
                for (var i = 1; i < _entries.Count; i++)
                {
                    if (_entries[i].LastUse >= oldest) continue;
                    oldest = _entries[i].LastUse;
                    victim = i;
                }
                RemoveAt(victim);
            }
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                RemoveAt(i);
            }
            _residentBytes = 0;
        }
    }


    private static void RemoveAt(int index)
    {
        var entry = _entries[index];
        _entries.RemoveAt(index);
        _residentBytes -= (long)entry.ByteCount;
        NativeBuffers.FreePixels((void*)entry.Data);
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
