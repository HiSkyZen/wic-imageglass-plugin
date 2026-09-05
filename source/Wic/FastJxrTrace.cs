/*
Optional low-overhead timing diagnostics for Fast JXR.
MIT License.
*/
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace WicCodec.Wic;

internal static class FastJxrTrace
{
    private static readonly string? _traceFile =
        Environment.GetEnvironmentVariable("FASTJXR_TRACE_FILE");

    private static readonly bool _enabled =
        string.Equals(Environment.GetEnvironmentVariable("FASTJXR_TRACE"), "1", StringComparison.Ordinal)
        || !string.IsNullOrWhiteSpace(_traceFile);

    private static readonly Lock _fileLock = new();

    public static bool Enabled => _enabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => _enabled ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(string operation, long start)
    {
        if (!_enabled || start == 0) return;

        var elapsed = Stopwatch.GetElapsedTime(start);
        var ms = elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);

        HostChannel.Log(1, $"FastJXR: {operation} {ms} ms");
        WriteFile($"event=timing\top={operation}\tms={ms}");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(string message)
    {
        if (!_enabled) return;

        HostChannel.Log(1, $"FastJXR: {message}");
        WriteFile($"event=info\tmessage={message}");
    }

    private static void WriteFile(string payload)
    {
        if (string.IsNullOrWhiteSpace(_traceFile)) return;

        try
        {
            lock (_fileLock)
            {
                var directory = Path.GetDirectoryName(_traceFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var workers = Environment.GetEnvironmentVariable("FASTJXR_WORKERS") ?? "default";
                var line =
                    $"{DateTime.UtcNow:O}\tpid={Environment.ProcessId}\tworkers={workers}\t{payload}";
                File.AppendAllText(_traceFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never affect image decoding.
        }
    }
}
