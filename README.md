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
2. Parse the JPEG XR codestream header and index table to recover the exact physical tile
   boundaries and compressed tile weights.
3. Request native `128bpp RGBA Float` pixels, bypassing WIC's generic FP32 -> FP16 converter.
4. Attempt **parallel full-resolution ROI decoding** with independent WIC decoder instances,
   aligned to the physical JPEG XR tile boundaries.
5. Convert each decoded region from FP32 to ImageGlass `RGBA16F` with
   `System.Numerics.Tensors.TensorPrimitives.ConvertToHalf` and multiple CPU cores.
6. If parallel ROI decoding is rejected by the installed codec, fall back to one native
   full-resolution decode followed by parallel/vectorized FP32 -> FP16 conversion.
7. Cache recent full-resolution RGBA16F results and metadata so duplicate ImageGlass requests
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

- Default: all logical processors, clamped to 1-64.
- Allowed override: `1` through `64`.
- `FASTJXR_WORKERS=1` disables the parallel ROI attempt and uses the single-decoder path.
- Strip mode cannot use more workers than the number of physical JPEG XR tile rows.
- Hybrid mode can spend spare workers by splitting the heaviest rows horizontally while still
  issuing exactly one WIC `CopyPixels` call per decoder.

Example:

```powershell
$env:FASTJXR_WORKERS = "8"
```

For high-resolution tiled JXR files, compare the actual measured latency rather than assuming
more workers are faster. The trace records both requested and actual worker counts.

### `FASTJXR_PARTITION`

Selects the full-resolution ROI scheduler:

- `strip` (default): one full-width physical tile-row region per worker. This is the proven
  fast path.
- `hybrid`: starts with one physical tile row per worker, then uses the JPEG XR index-table
  byte weights to split the heaviest rows horizontally when spare workers are available. Each
  decoder still performs exactly one `CopyPixels` call.
- `column`: one full-height vertical band per worker. Like strip mode, each decoder performs
  exactly one `CopyPixels` call; it is intended for landscape images with more tile columns
  than tile rows.
- `grid`: experimental 2D 256x256 tile work queue. Benchmarks on the current HDR corpus show
  substantially higher overhead because each decoder performs many small ROI calls, so grid is
  retained for diagnostics rather than used by default.

All modes preserve the original source resolution. None requests JPEG XR reduced-resolution decode.

Example:

```powershell
$env:FASTJXR_PARTITION = "hybrid"
$env:FASTJXR_WORKERS = "24"
```

More workers are not guaranteed to be faster because decoder setup, tile scheduling and memory
bandwidth eventually dominate.

### `FASTJXR_DIRECT_HALF`

Experimental native-half path. Set to `1` to ask `IWICBitmapSourceTransform` whether the
installed JPEG XR decoder can natively emit `64bpp RGBAHalf`.

The plugin first calls `GetClosestPixelFormat`; direct output is used only when the decoder
returns the exact RGBAHalf format. Otherwise it automatically falls back to the proven
RGBA32F + `TensorPrimitives.ConvertToHalf` path.

```powershell
$env:FASTJXR_DIRECT_HALF = "1"
```

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
