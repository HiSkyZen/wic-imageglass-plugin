# Fast JXR HDR Codec for ImageGlass

A performance-focused **full-resolution JPEG XR HDR decoder** for ImageGlass 10 on Windows.

This fork specializes the upstream WIC plugin for `.jxr`, `.wdp`, and `.hdp` and prioritizes
low first-open latency for large HDR JPEG XR files.

**Reduced-resolution JPEG XR decoding is deliberately not exposed.**

## Release 1.3.0

The production fast path is intentionally narrow:

1. Open the Windows JPEG XR / WMP decoder directly instead of generic WIC codec discovery.
2. Detect `128bpp RGBA Float` HDR sources without generic pixel-format probing.
3. Parse the JPEG XR codestream header and recover the **exact physical tile-row boundaries**.
4. Decode independent full-resolution tile-row ROIs concurrently with separate WIC decoder instances.
5. Convert each native RGBA32F region to ImageGlass `RGBA16F` with
   `System.Numerics.Tensors.TensorPrimitives.ConvertToHalf`.
6. Fall back to a conservative single-decoder full-resolution path when the parallel path is unavailable.
7. Cache recent decoded RGBA16F images with zero-copy reference counting and cache metadata separately.

Experimental grid, column, weighted-hybrid, and direct-half schedulers remain on the development
branch and are not part of the stable 1.3.0 release path.

## Measured performance

Reference system: Intel Core i7-13700K, Windows, ImageGlass 10.

| HDR JXR | Resolution | Single decoder | Stable exact-tile strip |
|---|---:|---:|---:|
| Austin Laser | 5456 × 3632 | ~0.90 s | ~0.11–0.12 s |
| Big Sur Coastline | 6016 × 6016 | ~1.65 s | ~0.18–0.19 s |

Actual performance depends on JPEG XR tile layout, compressed complexity, CPU topology, storage,
and the installed Windows Imaging Component decoder.

## Memory trade-off

The codec is latency-oriented and intentionally spends RAM to reduce wall-clock time.

A 6016 × 6016 RGBA32F raster is roughly 552 MiB and the final RGBA16F ImageGlass buffer is roughly
276 MiB. Parallel workers hold disjoint temporary regions rather than one temporary per full image.

The decoded-image cache keeps up to three recent outputs with a 1.5 GiB total cap.

## Tuning

### `FASTJXR_WORKERS`

Maximum number of independent WIC decoder workers.

- Default: all logical processors, capped at 64.
- Allowed override: `1` through `64`.
- The actual count is capped by the number of physical JPEG XR tile rows.
- `FASTJXR_WORKERS=1` uses the single-decoder fallback.

Example:

```powershell
$env:FASTJXR_WORKERS = "24"
```

### `FASTJXR_TRACE`

Set to `1` to enable timing diagnostics.

```powershell
$env:FASTJXR_TRACE = "1"
```

### `FASTJXR_TRACE_FILE`

Optional path for structured benchmark trace output.

```powershell
$env:FASTJXR_TRACE_FILE = "C:\\Temp\\fastjxr.log"
```

## Supported operations

| Operation | Support |
|---|---|
| Decode `.jxr`, `.wdp`, `.hdp` | Yes |
| HDR RGBA16F/scRGB output | Yes |
| Encode | No |
| Reduced-resolution decode callback | No |

## Build

Requires the .NET 10 SDK and Visual Studio C++ build tools for NativeAOT.

```powershell
dotnet publish source/WicCodec.csproj -c Release -r win-x64 -p:Platform=x64
./pack.ps1 -Rid win-x64
```

The plugin installs into `Plugin_FastJxrHdrCodec`, separately from the upstream generic WIC plugin.

## Credits

- Original WIC ImageGlass plugin: **Duong Dieu Phap** — d2phap/wic-imageglass-plugin
- Fast JXR specialization, parallel HDR path, exact-tile scheduling, caching and benchmarking:
  **HiSkyZen**
- ImageGlass and its native codec plugin ABI: ImageGlass project contributors

## License

MIT. See [LICENSE](LICENSE).
