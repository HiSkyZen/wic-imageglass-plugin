# Changelog

## 1.3.0 - 2026-09-06

### Added
- Fast JXR HDR specialization for ImageGlass 10.
- Full-resolution parallel JPEG XR ROI decoding for 128bpp RGBA-float HDR images.
- Exact JPEG XR physical tile-boundary parsing for worker partitioning.
- SIMD FP32 to FP16 conversion through System.Numerics.Tensors.
- Direct JPEG XR/WMP decoder opening with conservative WIC fallback.
- Zero-copy ref-counted decoded-image cache and metadata cache.
- Structured timing diagnostics and an automated worker benchmark script.
- Separate plugin identity and packaging so the upstream generic WIC plugin can remain installed.

### Changed
- Production decode path is intentionally limited to the validated exact-tile horizontal strip scheduler.
- Default worker request uses all logical processors, capped at 64 and bounded by physical tile rows.
- Fast JXR has higher JXR extension priority than the generic WIC codec.

### Removed from stable
- Reduced-resolution decode callback.
- Encoder exposure.
- Experimental grid, column, weighted-hybrid and direct-half decode paths. These remain available on the development branch.

### Performance reference
On an Intel Core i7-13700K with the tested 128bpp RGBA-float HDR corpus:
- 5456 x 3632: approximately 0.90 s single-decoder to 0.11-0.12 s stable parallel decode.
- 6016 x 6016: approximately 1.65 s single-decoder to 0.18-0.19 s stable parallel decode.
