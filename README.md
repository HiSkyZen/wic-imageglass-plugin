# Fast JXR HDR Codec for ImageGlass

A performance-focused **full-resolution JPEG XR HDR** codec plugin for ImageGlass 10 on Windows.

This fork is specialized for `.jxr`, `.wdp`, and `.hdp`. It intentionally gives up the
upstream plugin's general-purpose WIC format/encoding support in exchange for a narrower and
more aggressive JPEG XR decode path.

## Goals

- Always decode the requested JPEG XR image at **full source resolution**.
- Reduce the latency of large HDR JPEG XR files, especially
  `GUID_WICPixelFormat128bppRGBAFloat` sources.
- Spend additional CPU cores and RAM when doing so lowers wall-clock latency.
- Preserve ImageGlass's RGBA16F/scRGB HDR path.
- Keep a conservative WIC fallback when an optimized path cannot be used.

**Reduced-resolution JPEG XR decoding is deliberately disabled.**

## Fast paths

For the 128bpp RGBA-float HDR files this fork targets:

1. Open the WIC JPEG XR/WMP decoder directly instead of performing generic codec discovery.
2. Request native `128bpp RGBA Float` pixels, bypassing WIC's generic FP32 -> FP16 converter.
3. Attempt **parallel full-resolution ROI decoding** with independent WIC decoder instances.
   JPEG XR's tiled representation allows regions to be decoded independently.
4. Convert each decoded region from FP32 to ImageGlass `RGBA16F` with
   `System.Numerics.Tensors.TensorPrimitives.ConvertToHalf` and multiple CPU cores.
5. If parallel ROI decoding is rejected by the installed codec, fall back to one native
   full-resolution decode followed by parallel/vectorized FP32 -> FP16 conversion.
6. Cache recent full-resolution RGBA16F results and metadata so duplicate ImageGlass requests
   do not repeat an expensive JPEG XR decode.

The NativeAOT build uses `OptimizationPreference=Speed`.

## Memory trade-off

The plugin is intentionally latency-oriented. A 6016 x 6016 128bpp RGBA-float image is about
552 MiB when expanded to FP32 and about 276 MiB as the RGBA16F buffer handed to ImageGlass.

Parallel ROI decoding avoids one monolithic FP32 temporary, but several per-region FP32
temporaries can be resident at once. The full-resolution pixel cache can retain up to three
recent images, capped at 1.5 GiB total.

## Tuning

### `FASTJXR_WORKERS`

Controls the maximum number of independent WIC decoder workers used for parallel ROI decoding.

- Default: half of the logical processor count, clamped to 1-8.
- Allowed override: `1` through `16`.
- `FASTJXR_WORKERS=1` disables the parallel ROI attempt and uses the single-decoder path.

Example:

```powershell
$env:FASTJXR_WORKERS = "8"
```

For high-resolution tiled JXR files, try 4, 6, 8, 12, and 16 and compare actual latency.
More workers are not guaranteed to be faster because memory bandwidth and decoder overhead
eventually dominate.

### `FASTJXR_TRACE`

Set to `1` to emit timing information through ImageGlass's plugin log channel.

```powershell
$env:FASTJXR_TRACE = "1"
```

The trace reports metadata/cache hits, total full-resolution decode latency, and whether the
parallel ROI or sequential fallback path was used.

## Supported formats

| Operation | Formats |
|---|---|
| Decode | `.jxr`, `.wdp`, `.hdp` |
| Encode | Not supported |
| Reduced-resolution decode | **Not supported** |

## Build

Requires the .NET 10 SDK and Visual Studio C++ build tools for NativeAOT.

```powershell
# NativeAOT x64
dotnet publish source/WicCodec.csproj -c Release -r win-x64 -p:Platform=x64

# Package
./pack.ps1 -Rid win-x64

# Build and deploy directly to ImageGlass for local testing
./pack.ps1 -Rid win-x64 -Deploy
```

The deployed plugin lives in a separate `Plugin_FastJxrHdrCodec` directory, so it does not
overwrite the upstream general-purpose WIC plugin.

## CI

Pushes to `dev/**` NativeAOT-publish the x64 plugin and upload the result as a workflow artifact.

## Upstream

Based on [d2phap/wic-imageglass-plugin](https://github.com/d2phap/wic-imageglass-plugin).

## License

MIT. See [LICENSE](LICENSE).
