/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using System.Runtime.InteropServices;

namespace WicCodec.Wic;

/// <summary>
/// Ownership bookkeeping for the native memory the plugin hands across the ABI.
/// </summary>
internal static unsafe class NativeBuffers
{
    // Reference counts for plugin pixel buffers. A decoded image can be retained by the
    // full-resolution cache while ImageGlass/Skia owns another reference to the exact same
    // immutable pixels. Unknown pointers are still refused (e.g. host-owned encode buffers).
    private static readonly Lock _lock = new();
    private static readonly Dictionary<nint, int> _live = [];

    // The host reads IGImageInfo.IccProfileData AFTER LoadMetadata returns, so profiles go
    // into a small process-lifetime ring instead of being freed on the way out.
    private const int ICC_RING_SIZE = 4;
    private static readonly Lock _iccLock = new();
    private static readonly nint[] _iccRing = new nint[ICC_RING_SIZE];
    private static int _iccSlot;


    /// <summary>Allocates a pixel buffer and records it as host-owned.</summary>
    public static byte* AllocPixels(nuint byteCount)
    {
        var block = (byte*)NativeMemory.Alloc(byteCount);
        lock (_lock)
        {
            _live[(nint)block] = 1;
        }
        return block;
    }


    /// <summary>
    /// Frees a buffer allocated by <see cref="AllocPixels"/>, but only once and only if it
    /// really came from here. Callable from any thread, including the GC finalizer thread.
    /// </summary>
    public static bool RetainPixels(void* block)
    {
        if (block == null) return false;

        lock (_lock)
        {
            var key = (nint)block;
            if (!_live.TryGetValue(key, out var refs) || refs <= 0) return false;
            if (refs == int.MaxValue) return false;
            _live[key] = refs + 1;
            return true;
        }
    }


    /// <summary>
    /// Releases one reference to a plugin pixel buffer. The native allocation is freed only
    /// after both the host and any decode-cache entries have released their references.
    /// </summary>
    public static bool FreePixels(void* block)
    {
        if (block == null) return false;

        var free = false;
        lock (_lock)
        {
            var key = (nint)block;
            if (!_live.TryGetValue(key, out var refs) || refs <= 0) return false;

            if (refs == 1)
            {
                _live.Remove(key);
                free = true;
            }
            else
            {
                _live[key] = refs - 1;
            }
        }

        if (free) NativeMemory.Free(block);
        return true;
    }


    /// <summary>
    /// Drops a buffer the plugin allocated but never handed to the host (a failed decode).
    /// </summary>
    public static void Discard(void* block)
    {
        if (block == null) return;
        FreePixels(block);
    }


    /// <summary>
    /// Copies ICC bytes into the next ring slot, freeing whatever occupied it before.
    /// Returns <c>null</c> when the allocation fails.
    /// </summary>
    public static byte* PublishIccProfile(ReadOnlySpan<byte> icc)
    {
        if (icc.IsEmpty) return null;

        lock (_iccLock)
        {
            var slot = _iccSlot;
            _iccSlot = (_iccSlot + 1) % ICC_RING_SIZE;

            if (_iccRing[slot] != 0)
            {
                NativeMemory.Free((void*)_iccRing[slot]);
                _iccRing[slot] = 0;
            }

            var block = (byte*)NativeMemory.Alloc((nuint)icc.Length);
            if (block == null) return null;

            icc.CopyTo(new Span<byte>(block, icc.Length));
            _iccRing[slot] = (nint)block;
            return block;
        }
    }


    /// <summary>Allocates a null-terminated, process-lifetime UTF-16 copy of a string.</summary>
    public static char* AllocUtf16(string value)
    {
        var block = (char*)NativeMemory.Alloc((nuint)((value.Length + 1) * sizeof(char)));
        for (var i = 0; i < value.Length; i++) block[i] = value[i];
        block[value.Length] = '\0';
        return block;
    }
}
