/*
Optional low-overhead timing diagnostics for Fast JXR.
MIT License.
*/
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WicCodec.Wic;

internal static class FastJxrTrace
{
    private static readonly bool _enabled =
        string.Equals(Environment.GetEnvironmentVariable("FASTJXR_TRACE"), "1", StringComparison.Ordinal);

    public static bool Enabled => _enabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => _enabled ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(string operation, long start)
    {
        if (!_enabled || start == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(start);
        HostChannel.Log(1, $"FastJXR: {operation} {elapsed.TotalMilliseconds:F1} ms");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string message)
    {
        if (_enabled) HostChannel.Log(1, $"FastJXR: {message}");
    }
}
