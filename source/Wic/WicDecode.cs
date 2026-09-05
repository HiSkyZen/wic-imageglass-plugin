/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Vortice.Win32;
using Vortice.Win32.Com;
using Vortice.Win32.Graphics.Imaging;
using Apis = Vortice.Win32.Graphics.Imaging.Apis;

namespace WicCodec.Wic;

/// <summary>
/// Reads images through WIC: metadata probing and single-frame raster decoding.
/// <para>
/// Every entry point opens the file, does its work and releases every COM object before
/// returning. Nothing is cached between calls, because a WIC object belongs to the apartment
/// that created it and the host calls in from arbitrary background threads.
/// </para>
/// </summary>
internal static unsafe class WicDecode
{
    /// <summary>
    /// EXIF orientation is baked into the pixels by <see cref="Decode"/>, so the tags below are
    /// only read to know which rotation to apply and how the reported size must be swapped.
    /// Containers put the IFD in different places; the first path that resolves wins.
    /// </summary>
    private static readonly string[] _orientationQueries =
        ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "/app1/{ushort=274}", "/{ushort=274}"];


    /// <summary>
    /// Fills <paramref name="outInfo"/> from the file's first frame.
    /// </summary>
    public static IGStatus LoadMetadata(string path, IGImageInfo* outInfo, void* cancellation)
    {
        var traceStart = FastJxrTrace.Start();
        if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;
        if (JxrMetadataCache.TryFill(path, outInfo))
        {
            FastJxrTrace.End("metadata cache hit", traceStart);
            return IGStatus.OK;
        }

        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;

        try
        {
            var status = Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return status;

            uint frameCount = 0;
            decoder->GetFrameCount(&frameCount);
            if (frameCount == 0) frameCount = 1;

            if (decoder->GetFrame(0, &frame).Failure) return IGStatus.DecodeFailed;

            uint width = 0, height = 0;
            if (frame->GetSize(&width, &height).Failure || width == 0 || height == 0)
            {
                return IGStatus.DecodeFailed;
            }

            Guid sourceFormat;
            if (frame->GetPixelFormat(&sourceFormat).Failure) sourceFormat = default;
            var plan = WicPixels.Choose(factory, sourceFormat);

            var orientation = ReadOrientation(frame);
            if (SwapsAxes(orientation)) (width, height) = (height, width);

            outInfo->Width = (int)width;
            outInfo->Height = (int)height;
            outInfo->PixelFormat = (int)plan.IgFormat;
            outInfo->HasAlpha = plan.HasAlpha ? 1 : 0;
            outInfo->HdrTransferFn = (int)plan.Hdr;
            outInfo->FrameCount = (int)frameCount;
            outInfo->FileSizeBytes = FileSizeOf(path);

            // The decoded buffer is already upright, so reporting the source tag would make a
            // host that honors it rotate a second time.
            outInfo->Orientation = 1;

            ApplyColorProfile(factory, frame, outInfo, out var iccBytes);
            JxrMetadataCache.Store(path, outInfo, iccBytes, sourceFormat, orientation);
            FastJxrTrace.End("metadata WIC load", traceStart);
            return IGStatus.OK;
        }
        finally
        {
            ComInterop.Release(ref frame);
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    /// <summary>
    /// Decodes one frame at full size into a freshly allocated native buffer owned by the host.
    /// </summary>
    public static IGStatus Decode(string path, int frameIndex, IGPixelBuffer* outBuf, void* cancellation)
    {
        var traceStart = FastJxrTrace.Start();

        // ImageGlass can ask the codec for the same still more than once while transitioning
        // from metadata/preview to the final view. Full-resolution decoding is expensive, so a
        // short-lived native cache turns duplicate requests into a ref-counted zero-copy hit.
        if (frameIndex == 0 && JxrDecodeCache.TryCopyToHost(path, outBuf))
        {
            FastJxrTrace.End("full-res pixel cache hit", traceStart);
            return IGStatus.OK;
        }

        var status = DecodeCore(path, frameIndex, 0, 0, outBuf, cancellation);
        if (status == IGStatus.OK && frameIndex == 0)
        {
            JxrDecodeCache.Store(path, outBuf);
        }
        if (FastJxrTrace.Enabled)
        {
            FastJxrTrace.End($"full-res decode ({status})", traceStart);
        }
        return status;
    }


    /// <summary>
    /// Decodes one frame no larger than <paramref name="maxWidth"/> x <paramref name="maxHeight"/>,
    /// for the host's thumbnails and previews.
    /// </summary>
    public static IGStatus DecodeScaled(string path, int frameIndex, int maxWidth, int maxHeight,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return IGStatus.InvalidArg;

        return DecodeCore(path, frameIndex, maxWidth, maxHeight, outBuf, cancellation);
    }


    /// <summary>
    /// The one decode path. A non-zero box scales the frame down to fit it; 0 means full size.
    /// </summary>
    private static IGStatus DecodeCore(string path, int frameIndex, int maxWidth, int maxHeight,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapScaler* scaler = null;
        IWICBitmapSource* converted = null;
        IWICBitmapFlipRotator* rotator = null;
        byte* pixels = null;

        try
        {
            var status = Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return status;

            uint frameCount = 0;
            decoder->GetFrameCount(&frameCount);
            if (frameIndex < 0 || (frameCount > 0 && frameIndex >= frameCount)) return IGStatus.InvalidArg;

            if (decoder->GetFrame((uint)frameIndex, &frame).Failure) return IGStatus.DecodeFailed;

            Guid sourceFormat;
            int orientation;
            if (!JxrMetadataCache.TryGetDecodeTraits(path, out sourceFormat, out orientation))
            {
                if (frame->GetPixelFormat(&sourceFormat).Failure) sourceFormat = default;
                orientation = ReadOrientation(frame);
            }

            // The HDR files this fork is optimized for are 128bpp RGBA float JXR. WIC's
            // generic format converter is effectively single-threaded on this path, so decode
            // the native FP32 raster once and fan the FP32->FP16 conversion across all cores.
            // This deliberately trades a large temporary buffer for lower wall-clock latency.
            if (maxWidth == 0 && maxHeight == 0
                && orientation == 1
                && sourceFormat == Apis.GUID_WICPixelFormat128bppRGBAFloat)
            {
                return DecodeRgbaFloat32FullResolution(path, frameIndex, frame, outBuf, cancellation);
            }

            var plan = WicPixels.Choose(factory, sourceFormat);

            // Scale, convert, then rotate. The scaler must sit on the FRAME: it reaches the
            // frame's IWICBitmapSourceTransform there (a JPEG's DCT scales), which the format
            // converter does not forward. The rotator is format-agnostic, and rotating the
            // smaller pre-conversion buffer would still need a second pass afterwards.
            var source = (IWICBitmapSource*)frame;
            if (maxWidth > 0 && maxHeight > 0)
            {
                var scaled = TryScale(factory, frame, orientation,
                    (uint)maxWidth, (uint)maxHeight, &scaler);
                if (scaled != null) source = scaled;
            }

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (sourceFormat != plan.TargetFormat)
            {
                if (!TryConvert(source, ref plan, &converted)) return IGStatus.Unsupported;
                source = converted;
            }

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            var transform = ToTransform(orientation);
            if (transform != WICBitmapTransformOptions.Rotate0)
            {
                if (factory->CreateBitmapFlipRotator(&rotator).Failure) return IGStatus.Internal;
                if (rotator->Initialize(source, transform).Failure) return IGStatus.Internal;
                source = (IWICBitmapSource*)rotator;
            }

            uint width = 0, height = 0;
            if (source->GetSize(&width, &height).Failure || width == 0 || height == 0)
            {
                return IGStatus.DecodeFailed;
            }

            var stride = (long)width * plan.BytesPerPixel;
            var byteCount = stride * height;
            if (stride > int.MaxValue || byteCount > int.MaxValue) return IGStatus.OutOfMemory;

            pixels = NativeBuffers.AllocPixels((nuint)byteCount);
            if (pixels == null) return IGStatus.OutOfMemory;

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (source->CopyPixels(null, (uint)stride, (uint)byteCount, pixels).Failure)
            {
                return IGStatus.DecodeFailed;
            }

            outBuf->Data = pixels;
            outBuf->Width = (int)width;
            outBuf->Height = (int)height;
            outBuf->Stride = (int)stride;
            outBuf->PixelFormat = (int)plan.IgFormat;
            outBuf->ReleaseContext = pixels;
            pixels = null;   // handed over; the host frees it via FreePixelBuffer

            return IGStatus.OK;
        }
        finally
        {
            // Only reached with a non-null pixels on a failure path.
            NativeBuffers.Discard(pixels);

            ComInterop.Release(ref rotator);
            ComInterop.Release(ref converted);
            ComInterop.Release(ref scaler);
            ComInterop.Release(ref frame);
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    /// <summary>
    /// Fast path for 128bpp RGBA-float JPEG XR. It first attempts parallel ROI decoding with
    /// independent WIC decoder instances. JPEG XR tiles make those full-resolution regions
    /// independently decodable; if the installed codec rejects the strategy, the proven
    /// single-decoder path is used immediately.
    /// </summary>
    private static IGStatus DecodeRgbaFloat32FullResolution(string path, int frameIndex,
        IWICBitmapFrameDecode* frame, IGPixelBuffer* outBuf, void* cancellation)
    {
        uint width = 0, height = 0;
        if (frame->GetSize(&width, &height).Failure || width == 0 || height == 0)
        {
            return IGStatus.DecodeFailed;
        }

        var workers = GetRegionWorkerCount((int)height);
        if (workers > 1)
        {
            var parallelStatus = TryDecodeRgbaFloat32Regions(
                path, frameIndex, width, height, workers, outBuf, cancellation);

            if (parallelStatus == IGStatus.OK || parallelStatus == IGStatus.Canceled)
            {
                if (FastJxrTrace.Enabled)
                {
                    FastJxrTrace.Info($"parallel ROI path workers={workers}, size={width}x{height}, status={parallelStatus}");
                }
                return parallelStatus;
            }

            HostChannel.Log(3, $"FastJXR: parallel ROI decode fell back to single decoder ({parallelStatus}).");
        }

        if (FastJxrTrace.Enabled)
        {
            FastJxrTrace.Info($"sequential RGBA32F path size={width}x{height}");
        }
        return DecodeRgbaFloat32Sequential(frame, width, height, outBuf, cancellation);
    }


    /// <summary>
    /// Decodes independent full-resolution horizontal regions concurrently. There is no image
    /// scaling: uiWidth/uiHeight always remain the source dimensions, and only the ROI changes.
    /// </summary>
    private static IGStatus TryDecodeRgbaFloat32Regions(string path, int frameIndex,
        uint width, uint height, int workers, IGPixelBuffer* outBuf, void* cancellation)
    {
        var srcStride64 = (long)width * 16;
        var dstStride64 = (long)width * 8;
        var dstBytes64 = dstStride64 * height;
        if (srcStride64 <= 0 || dstStride64 <= 0 || srcStride64 > uint.MaxValue
            || dstStride64 > int.MaxValue || dstBytes64 > int.MaxValue)
        {
            return IGStatus.OutOfMemory;
        }

        byte* destPixels = null;
        try
        {
            destPixels = NativeBuffers.AllocPixels((nuint)dstBytes64);
            if (destPixels == null) return IGStatus.OutOfMemory;

            var destBase = (nint)destPixels;
            var cancellationPtr = (nint)cancellation;
            var dstStride = (int)dstStride64;
            var srcStride = (uint)srcStride64;
            var floatsPerRow = checked((int)width * 4);
            var totalTileRows = ((int)height + 255) / 256;
            var failed = 0;
            var canceled = 0;

            var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
            Parallel.For(0, workers, options, worker =>
            {
                if (Volatile.Read(ref failed) != 0 || Volatile.Read(ref canceled) != 0) return;
                if (HostChannel.IsCanceled((void*)cancellationPtr))
                {
                    Interlocked.Exchange(ref canceled, 1);
                    return;
                }

                var tileRowStart = totalTileRows * worker / workers;
                var tileRowEnd = totalTileRows * (worker + 1) / workers;
                var y0 = tileRowStart * 256;
                var y1 = Math.Min((int)height, tileRowEnd * 256);
                var rows = y1 - y0;
                if (rows <= 0) return;

                IWICImagingFactory* workerFactory = null;
                IWICBitmapDecoder* workerDecoder = null;
                IWICBitmapFrameDecode* workerFrame = null;
                IWICBitmapSourceTransform* sourceTransform = null;
                byte* sourcePixels = null;

                try
                {
                    var openStatus = Open(path, (void*)cancellationPtr, ref workerFactory, ref workerDecoder);
                    if (openStatus != IGStatus.OK)
                    {
                        Interlocked.CompareExchange(ref failed, (int)openStatus, 0);
                        return;
                    }

                    if (workerDecoder->GetFrame((uint)frameIndex, &workerFrame).Failure)
                    {
                        Interlocked.CompareExchange(ref failed, (int)IGStatus.DecodeFailed, 0);
                        return;
                    }

                    Guid sourceFormat;
                    if (workerFrame->GetPixelFormat(&sourceFormat).Failure
                        || sourceFormat != Apis.GUID_WICPixelFormat128bppRGBAFloat)
                    {
                        Interlocked.CompareExchange(ref failed, (int)IGStatus.Unsupported, 0);
                        return;
                    }

                    var transformIid = IWICBitmapSourceTransform.IID_IWICBitmapSourceTransform;
                    if (workerFrame->QueryInterface(&transformIid, (void**)&sourceTransform).Failure)
                    {
                        Interlocked.CompareExchange(ref failed, (int)IGStatus.Unsupported, 0);
                        return;
                    }

                    var sourceBytes64 = (long)srcStride * rows;
                    if (sourceBytes64 <= 0 || sourceBytes64 > uint.MaxValue)
                    {
                        Interlocked.CompareExchange(ref failed, (int)IGStatus.OutOfMemory, 0);
                        return;
                    }

                    sourcePixels = (byte*)NativeMemory.Alloc((nuint)sourceBytes64);
                    if (sourcePixels == null)
                    {
                        Interlocked.CompareExchange(ref failed, (int)IGStatus.OutOfMemory, 0);
                        return;
                    }

                    var rect = new System.Drawing.Rectangle(0, y0, (int)width, rows);
                    var requestedFormat = Apis.GUID_WICPixelFormat128bppRGBAFloat;
                    var hr = sourceTransform->CopyPixels(
                        &rect,
                        width,
                        height,
                        &requestedFormat,
                        WICBitmapTransformOptions.Rotate0,
                        srcStride,
                        (uint)sourceBytes64,
                        sourcePixels);

                    if (hr.Failure || requestedFormat != Apis.GUID_WICPixelFormat128bppRGBAFloat)
                    {
                        Interlocked.CompareExchange(ref failed, (int)IGStatus.DecodeFailed, 0);
                        return;
                    }

                    for (var row = 0; row < rows; row++)
                    {
                        if ((row & 31) == 0 && HostChannel.IsCanceled((void*)cancellationPtr))
                        {
                            Interlocked.Exchange(ref canceled, 1);
                            return;
                        }

                        var src = (float*)(sourcePixels + (nint)row * srcStride);
                        var dst = (Half*)(destBase + (nint)(y0 + row) * dstStride);
                        TensorPrimitives.ConvertToHalf(
                            new ReadOnlySpan<float>(src, floatsPerRow),
                            new Span<Half>(dst, floatsPerRow));
                    }
                }
                catch
                {
                    Interlocked.CompareExchange(ref failed, (int)IGStatus.Internal, 0);
                }
                finally
                {
                    NativeMemory.Free(sourcePixels);
                    ComInterop.Release(ref sourceTransform);
                    ComInterop.Release(ref workerFrame);
                    ComInterop.Release(ref workerDecoder);
                    ComInterop.Release(ref workerFactory);
                }
            });

            if (Volatile.Read(ref canceled) != 0) return IGStatus.Canceled;
            var failure = Volatile.Read(ref failed);
            if (failure != 0) return (IGStatus)failure;

            outBuf->Data = destPixels;
            outBuf->Width = (int)width;
            outBuf->Height = (int)height;
            outBuf->Stride = dstStride;
            outBuf->PixelFormat = (int)IGPixelFormat.RgbaFloat16;
            outBuf->ReleaseContext = destPixels;
            destPixels = null;
            return IGStatus.OK;
        }
        finally
        {
            NativeBuffers.Discard(destPixels);
        }
    }


    /// <summary>
    /// Conservative single-decoder full-resolution fallback. WIC emits native RGBA32F once and
    /// the narrowing pass is then vectorized and spread across all logical processors.
    /// </summary>
    private static IGStatus DecodeRgbaFloat32Sequential(IWICBitmapFrameDecode* frame,
        uint width, uint height, IGPixelBuffer* outBuf, void* cancellation)
    {
        var srcStride64 = (long)width * 16;
        var dstStride64 = (long)width * 8;
        var srcBytes64 = srcStride64 * height;
        var dstBytes64 = dstStride64 * height;
        if (srcStride64 > int.MaxValue || dstStride64 > int.MaxValue
            || srcBytes64 > int.MaxValue || dstBytes64 > int.MaxValue)
        {
            return IGStatus.OutOfMemory;
        }

        byte* sourcePixels = null;
        byte* destPixels = null;
        try
        {
            sourcePixels = (byte*)NativeMemory.Alloc((nuint)srcBytes64);
            if (sourcePixels == null) return IGStatus.OutOfMemory;

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (((IWICBitmapSource*)frame)->CopyPixels(null, (uint)srcStride64,
                (uint)srcBytes64, sourcePixels).Failure)
            {
                return IGStatus.DecodeFailed;
            }

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            destPixels = NativeBuffers.AllocPixels((nuint)dstBytes64);
            if (destPixels == null) return IGStatus.OutOfMemory;

            var srcBase = (nint)sourcePixels;
            var dstBase = (nint)destPixels;
            var srcStride = (int)srcStride64;
            var dstStride = (int)dstStride64;
            var floatsPerRow = checked((int)width * 4);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };

            Parallel.For(0, (int)height, options, y =>
            {
                var src = (float*)(srcBase + (nint)(y * srcStride));
                var dst = (Half*)(dstBase + (nint)(y * dstStride));
                TensorPrimitives.ConvertToHalf(
                    new ReadOnlySpan<float>(src, floatsPerRow),
                    new Span<Half>(dst, floatsPerRow));
            });

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            outBuf->Data = destPixels;
            outBuf->Width = (int)width;
            outBuf->Height = (int)height;
            outBuf->Stride = dstStride;
            outBuf->PixelFormat = (int)IGPixelFormat.RgbaFloat16;
            outBuf->ReleaseContext = destPixels;
            destPixels = null;
            return IGStatus.OK;
        }
        finally
        {
            NativeMemory.Free(sourcePixels);
            NativeBuffers.Discard(destPixels);
        }
    }


    private static int GetRegionWorkerCount(int height)
    {
        var tileRows = Math.Max(1, (height + 255) / 256);
        var defaultWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

        var configured = defaultWorkers;
        var text = Environment.GetEnvironmentVariable("FASTJXR_WORKERS");
        if (int.TryParse(text, out var parsed))
        {
            configured = Math.Clamp(parsed, 1, 16);
        }

        return Math.Min(configured, tileRows);
    }


    /// <summary>
    /// Creates a factory and opens <paramref name="path"/> with whichever installed decoder
    /// claims the content.
    /// </summary>
    private static IGStatus Open(string path, void* cancellation,
        ref IWICImagingFactory* factory, ref IWICBitmapDecoder* decoder)
    {
        if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

        factory = WicFactory.Create();
        if (factory == null) return IGStatus.Internal;

        // This fork only claims JPEG XR, so avoid WIC's filename/content sniff and registered
        // decoder search on the hot path. This matters especially for parallel ROI decoding,
        // where several decoder instances are opened for one image.
        IWICStream* stream = null;
        IWICBitmapDecoder* opened = null;
        try
        {
            var directHr = factory->CreateStream(&stream);
            if (directHr.Success)
            {
                fixed (char* pPath = path)
                {
                    directHr = stream->InitializeFromFilename(pPath, ComInterop.GENERIC_READ);
                }
            }

            if (directHr.Success)
            {
                var container = Apis.GUID_ContainerFormatWmp;
                directHr = factory->CreateDecoder(&container, null, &opened);
            }

            if (directHr.Success)
            {
                directHr = opened->Initialize(
                    (Vortice.Win32.Com.IStream*)stream,
                    Apis.WICDecodeMetadataCacheOnDemand);
            }

            if (directHr.Success)
            {
                decoder = opened;
                opened = null;
                return IGStatus.OK;
            }

            // Compatibility fallback: if a system's explicit WMP decoder cannot be initialized,
            // let WIC sniff the file and select any registered JPEG XR decoder.
            ComInterop.Release(ref opened);
            ComInterop.Release(ref stream);

            fixed (char* pPath = path)
            {
                directHr = factory->CreateDecoderFromFilename(
                    pPath,
                    null,
                    NativeFileAccess.GenericRead,
                    Apis.WICDecodeMetadataCacheOnDemand,
                    &opened);
            }

            if (directHr.Failure)
            {
                return directHr.Value == ComInterop.WINCODEC_ERR_COMPONENTNOTFOUND
                    ? IGStatus.Unsupported
                    : IGStatus.IoError;
            }

            decoder = opened;
            opened = null;
            return IGStatus.OK;
        }
        finally
        {
            ComInterop.Release(ref opened);
            ComInterop.Release(ref stream);
        }
    }


    /// <summary>
    /// Wraps <paramref name="frame"/> in a scaler that fits the box, or returns <c>null</c> when
    /// the frame already fits (or the scaler cannot be set up, which is not fatal).
    /// </summary>
    /// <remarks>
    /// The box applies to the UPRIGHT image, so a 90/270 degree orientation swaps the axes before
    /// the ratio is taken; the resulting scale then applies to the unrotated frame either way.
    /// </remarks>
    private static IWICBitmapSource* TryScale(IWICImagingFactory* factory,
        IWICBitmapFrameDecode* frame, int orientation, uint maxWidth, uint maxHeight,
        IWICBitmapScaler** outScaler)
    {
        uint srcWidth = 0, srcHeight = 0;
        if (((IWICBitmapSource*)frame)->GetSize(&srcWidth, &srcHeight).Failure) return null;
        if (srcWidth == 0 || srcHeight == 0) return null;

        var uprightWidth = SwapsAxes(orientation) ? srcHeight : srcWidth;
        var uprightHeight = SwapsAxes(orientation) ? srcWidth : srcHeight;

        var scale = Math.Min((double)maxWidth / uprightWidth, (double)maxHeight / uprightHeight);
        if (scale >= 1) return null;

        var targetWidth = Math.Max(1u, (uint)Math.Round(srcWidth * scale));
        var targetHeight = Math.Max(1u, (uint)Math.Round(srcHeight * scale));
        if (targetWidth >= srcWidth && targetHeight >= srcHeight) return null;

        if (factory->CreateBitmapScaler(outScaler).Failure) return null;

        // Fant is WIC's box/averaging filter: the right one for a big downscale.
        if ((*outScaler)->Initialize((IWICBitmapSource*)frame, targetWidth, targetHeight,
            WICBitmapInterpolationMode.ModeFant).Failure)
        {
            ComInterop.Release(ref *outScaler);
            return null;
        }

        return (IWICBitmapSource*)*outScaler;
    }


    /// <summary>
    /// Converts to <see cref="PixelPlan.TargetFormat"/>, stepping down the plan when this
    /// machine has no converter for the pair.
    /// </summary>
    private static bool TryConvert(IWICBitmapSource* source, ref PixelPlan plan, IWICBitmapSource** result)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = plan.TargetFormat;

            // WICConvertBitmapSource, not IWICImagingFactory.CreateFormatConverter: the helper
            // walks the registered converters, so it also finds the high-color and WMPhoto ones
            // that the default converter cannot stand in for.
            if (Apis.WICConvertBitmapSource(&target, source, result).Success) return true;

            var degraded = WicPixels.Degrade(in plan);
            if (degraded.TargetFormat == plan.TargetFormat) return false;
            plan = degraded;
        }

        return false;
    }


    /// <summary>
    /// Reads the EXIF orientation tag (1..8), defaulting to 1 when absent.
    /// </summary>
    private static int ReadOrientation(IWICBitmapFrameDecode* frame)
    {
        IWICMetadataQueryReader* reader = null;
        try
        {
            if (frame->GetMetadataQueryReader(&reader).Failure) return 1;

            foreach (var query in _orientationQueries)
            {
                ComInterop.PropVariant value = default;
                HResult hr;
                fixed (char* pQuery = query)
                {
                    hr = reader->GetMetadataByName(pQuery, (Variant**)&value);
                }
                if (hr.Failure) continue;

                var orientation = value.Vt switch
                {
                    ComInterop.VT_UI2 => (int)(ushort)value.Value0,
                    ComInterop.VT_UI1 => (int)(byte)value.Value0,
                    _ => 0,
                };
                ComInterop.PropVariantClear(&value);

                if (orientation is >= 1 and <= 8) return orientation;
            }

            return 1;
        }
        finally
        {
            ComInterop.Release(ref reader);
        }
    }


    /// <summary>
    /// Maps an EXIF orientation onto the equivalent WIC transform. WIC applies the flip after
    /// the rotation, which is why 5 and 7 pair a horizontal flip with the opposite rotation
    /// from the one their EXIF description names.
    /// </summary>
    private static WICBitmapTransformOptions ToTransform(int orientation) => orientation switch
    {
        2 => WICBitmapTransformOptions.FlipHorizontal,
        3 => WICBitmapTransformOptions.Rotate180,
        4 => WICBitmapTransformOptions.FlipVertical,
        5 => WICBitmapTransformOptions.Rotate90 | WICBitmapTransformOptions.FlipHorizontal,
        6 => WICBitmapTransformOptions.Rotate90,
        7 => WICBitmapTransformOptions.Rotate270 | WICBitmapTransformOptions.FlipHorizontal,
        8 => WICBitmapTransformOptions.Rotate270,
        _ => WICBitmapTransformOptions.Rotate0,
    };


    private static bool SwapsAxes(int orientation) => orientation is 5 or 6 or 7 or 8;


    /// <summary>
    /// Publishes the frame's embedded ICC profile, or falls back to its EXIF color-space tag.
    /// </summary>
    private static void ApplyColorProfile(IWICImagingFactory* factory, IWICBitmapFrameDecode* frame,
        IGImageInfo* outInfo, out byte[]? iccBytes)
    {
        iccBytes = null;
        uint count = 0;
        if (frame->GetColorContexts(0, null, &count).Failure || count == 0) return;

        // A frame can carry several contexts (e.g. a profile plus an EXIF hint); cap the read
        // so a malformed file cannot make us allocate an arbitrary array.
        count = Math.Min(count, 8);

        var contexts = stackalloc IWICColorContext*[(int)count];
        for (var i = 0u; i < count; i++) contexts[i] = null;

        try
        {
            // GetColorContexts fills objects the caller supplies rather than creating them.
            for (var i = 0u; i < count; i++)
            {
                if (factory->CreateColorContext(&contexts[i]).Failure) return;
            }

            uint actual = 0;
            if (frame->GetColorContexts(count, contexts, &actual).Failure) return;

            for (var i = 0u; i < actual; i++)
            {
                var context = contexts[i];
                if (context == null) continue;

                WICColorContextType type;
                if (context->GetType(&type).Failure) continue;

                if (type == Apis.WICColorContextProfile
                    && TryPublishProfile(context, outInfo, out iccBytes)) return;

                if (type == Apis.WICColorContextExifColorSpace)
                {
                    uint exifSpace = 0;
                    if (context->GetExifColorSpace(&exifSpace).Failure) continue;

                    // 1 = sRGB, 2 = Adobe RGB; anything else is "uncalibrated".
                    outInfo->ColorSpace = (int)(exifSpace switch
                    {
                        1 => IGColorSpace.Srgb,
                        2 => IGColorSpace.AdobeRgb,
                        _ => IGColorSpace.Unknown,
                    });
                }
            }
        }
        finally
        {
            for (var i = 0u; i < count; i++)
            {
                ComInterop.Release(ref contexts[i]);
            }
        }
    }


    private static bool TryPublishProfile(IWICColorContext* context, IGImageInfo* outInfo,
        out byte[]? profileBytes)
    {
        profileBytes = null;
        uint size = 0;
        if (context->GetProfileBytes(0, null, &size).Failure || size == 0) return false;

        var bytes = new byte[size];
        fixed (byte* pBytes = bytes)
        {
            if (context->GetProfileBytes(size, pBytes, &size).Failure) return false;
        }

        var block = NativeBuffers.PublishIccProfile(bytes.AsSpan(0, (int)Math.Min(size, bytes.Length)));
        if (block == null) return false;

        var actualSize = (int)Math.Min(size, bytes.Length);
        outInfo->IccProfileData = block;
        outInfo->IccProfileSize = actualSize;
        profileBytes = actualSize == bytes.Length ? bytes : bytes.AsSpan(0, actualSize).ToArray();
        return true;
    }


    private static long FileSizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }
}
