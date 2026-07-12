using System.Collections.Concurrent;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.MediaFoundation;

namespace Capture.Gpu;

/// <summary>
/// Low-latency streaming H.264 encoder. Drives the platform's HARDWARE encoder MFT
/// DIRECTLY (async model, pumped on a background thread) and emits one Annex-B access unit
/// PER FRAME — no MP4 container, no fragment buffering. That removes the ~0.5s fMP4 floor,
/// so end-to-end latency drops toward capture + network + decode (tens of ms on LAN).
///
/// Input is NV12 (converted from the capturer's BGRA on the CPU, parallelized). Output
/// frames are pushed via <see cref="FrameEncoded"/> and fed to the browser's WebCodecs
/// VideoDecoder. Key frames are infrequent (encoder GOP) since per-frame delivery doesn't
/// need them for latency — which also makes the "whole screen resend" spikes rare.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class H264LowLatencyEncoder : IVideoEncoder
{
    private static readonly Guid MF_MT_MAJOR_TYPE           = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE              = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMediaType_Video          = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264         = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12         = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_AVG_BITRATE          = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_FRAME_SIZE           = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE           = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO   = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE       = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE        = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_DEFAULT_STRIDE       = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK  = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid IID_IMFTransform           = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IID_ICodecAPI              = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid CODECAPI_AVLowLatencyMode              = new("9c27891a-ed7a-4e1b-8c3b-7d9dfc5e80c0");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode    = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate        = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount  = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize               = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    private const uint MFT_ENUM_FLAG_HARDWARE       = 0x4;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER  = 0x40;
    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_Main      = 77;

    private readonly int m_Width, m_Height, m_Fps, m_Stride;
    private readonly int m_OutputPrefixBytes;
    private readonly long m_FrameDurationTicks;
    private readonly byte[] m_Nv12;
    private IMFTransform m_Encoder = null!;
    private IMFAttributes m_EncoderAttributes = null!;
    private IMFMediaEventGenerator m_Events = null!;
    private IntPtr m_CodecApi;
    private readonly BlockingCollection<IMFSample> m_Input = new(boundedCapacity: 3);
    private readonly Thread m_Pump;
    private volatile bool m_Running = true;
    private int m_BitrateBitsPerSec;

    /// <summary>Raised on the pump thread for each encoded Annex-B access unit.</summary>
    public event Action<EncodedVideoFrame>? FrameEncoded;

    public H264LowLatencyEncoder(
        int width,
        int height,
        int fps,
        int bitrateBitsPerSec,
        int outputPrefixBytes = 0)
    {
        m_Width = width; m_Height = height; m_Fps = fps; m_Stride = width;
        m_OutputPrefixBytes = Math.Max(0, outputPrefixBytes);
        m_FrameDurationTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;
        m_Nv12 = new byte[width * height * 3 / 2];
        m_BitrateBitsPerSec = bitrateBitsPerSec;

        MediaFactory.MFStartup();
        m_Encoder = CreateHardwareEncoder();
        m_CodecApi = QueryCodecApi(m_Encoder);

        // Unlock the async interface so we can drive it with the event model.
        m_EncoderAttributes = m_Encoder.Attributes;
        m_EncoderAttributes.SetUInt32(MF_TRANSFORM_ASYNC_UNLOCK, 1);

        // Codec settings belong on ICodecAPI, not on the transform's general attribute
        // collection. Set static properties before the media types; runtime bitrate
        // changes use the same interface later.
        SetInitialCodecValue(CODECAPI_AVLowLatencyMode, 1);
        SetInitialCodecValue(CODECAPI_AVEncMPVDefaultBPictureCount, 0);
        SetInitialCodecValue(CODECAPI_AVEncCommonRateControlMode, 0); // CBR
        SetInitialCodecValue(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrateBitsPerSec);
        SetInitialCodecValue(CODECAPI_AVEncMPVGOPSize, (uint)(fps * 12));

        // Output type MUST be set before input on an encoder.
        IMFMediaType outType = MediaFactory.MFCreateMediaType();
        outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
        outType.SetUInt32(MF_MT_AVG_BITRATE, (uint)bitrateBitsPerSec);
        outType.SetUInt32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        outType.SetUInt32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
        outType.SetUInt64(MF_MT_FRAME_SIZE, Pack(width, height));
        outType.SetUInt64(MF_MT_FRAME_RATE, Pack(fps, 1));
        outType.SetUInt64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        m_Encoder.SetOutputType(0, outType, 0);

        IMFMediaType inType = MediaFactory.MFCreateMediaType();
        inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        inType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        inType.SetUInt32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        inType.SetUInt32(MF_MT_DEFAULT_STRIDE, (uint)m_Stride);
        inType.SetUInt64(MF_MT_FRAME_SIZE, Pack(width, height));
        inType.SetUInt64(MF_MT_FRAME_RATE, Pack(fps, 1));
        inType.SetUInt64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        m_Encoder.SetInputType(0, inType, 0);

        m_Events = m_Encoder.QueryInterface<IMFMediaEventGenerator>();
        m_Encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        m_Encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        m_Pump = new Thread(Pump) { IsBackground = true, Name = "h264-lowlat" };
        m_Pump.Start();
    }

    private IMFTransform CreateHardwareEncoder()
    {
        var outInfo = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
        MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, null, outInfo,
            out IntPtr array, out uint count);
        if (count == 0 || array == IntPtr.Zero)
            throw new NotSupportedException("No hardware H.264 encoder MFT is available.");

        var ptrs = new IntPtr[count];
        Marshal.Copy(array, ptrs, 0, (int)count);
        Marshal.FreeCoTaskMem(array);

        using var activate = new IMFActivate(ptrs[0]);
        for (int i = 1; i < count; i++) Marshal.Release(ptrs[i]); // keep only the first
        activate.ActivateObject(out IMFTransform? transform);
        return transform
            ?? throw new NotSupportedException("The hardware H.264 encoder could not be activated.");
    }

    private static ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;

    private static IntPtr QueryCodecApi(IMFTransform encoder)
    {
        Guid iid = IID_ICodecAPI;
        int result = Marshal.QueryInterface(encoder.NativePointer, ref iid, out IntPtr codecApi);
        return result >= 0 ? codecApi : IntPtr.Zero;
    }

    private void SetInitialCodecValue(Guid property, uint value)
    {
        if (TrySetCodecUInt32(property, value)) return;
        try { m_EncoderAttributes.SetUInt32(property, value); }
        catch { /* vendor does not expose this optional property */ }
    }

    private unsafe bool TrySetCodecUInt32(Guid property, uint value)
    {
        if (m_CodecApi == IntPtr.Zero) return false;

        // ICodecAPI::SetValue is vtable slot 9 after IUnknown and the six preceding
        // codec methods. CODECAPI_AVEncCommonMeanBitRate requires VARIANT VT_UI4.
        IntPtr vtable = Marshal.ReadIntPtr(m_CodecApi);
        IntPtr address = Marshal.ReadIntPtr(vtable, 9 * IntPtr.Size);
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, NativeVariant*, int>)address;
        NativeVariant variant = NativeVariant.FromUInt32(value);
        int result = setValue(m_CodecApi, &property, &variant);
        return result == 0; // S_OK; S_FALSE means the property is read-only
    }

    /// <summary>
    /// Best-effort live bitrate update through the hardware encoder's CodecAPI
    /// attributes. Most current Intel, AMD, and NVIDIA MFTs apply this without a restart.
    /// </summary>
    public bool TrySetBitrate(int bitrateBitsPerSec)
    {
        int target = Math.Clamp(bitrateBitsPerSec, 500_000, 100_000_000);
        if (Volatile.Read(ref m_BitrateBitsPerSec) == target) return true;
        try
        {
            if (!TrySetCodecUInt32(CODECAPI_AVEncCommonMeanBitRate, (uint)target))
                return false;
            Volatile.Write(ref m_BitrateBitsPerSec, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Encode(ReadOnlySpan<byte> bgra, TimeSpan timestamp, bool forceKeyframe)
    {
        BgraToNv12(bgra, m_Nv12, m_Width, m_Height);

        int size = m_Nv12.Length;
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(size);
        buffer.Lock(out IntPtr dst, out _, out _);
        try { Marshal.Copy(m_Nv12, 0, dst, size); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = size;

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp.Ticks;
        sample.SampleDuration = m_FrameDurationTicks;
        if (forceKeyframe) sample.SetUInt32(MFSampleExtension_CleanPoint, 1);
        buffer.Dispose();

        // Hand off to the pump; drop if the encoder is backed up so we never build a queue
        // of stale frames (latency > freshness).
        if (!m_Input.TryAdd(sample, 4)) sample.Dispose();
    }

    // ---- async MFT event pump (one input -> one output; no B-frames) ----
    private void Pump()
    {
        while (m_Running)
        {
            IMFMediaEvent evt;
            try { evt = m_Events.GetEvent(0); } // blocking wait for the next event
            catch { break; }

            MediaEventTypes type;
            try { type = evt.Type; } finally { evt.Dispose(); }

            if (type == MediaEventTypes.TransformNeedInput)
            {
                IMFSample input;
                try { input = m_Input.Take(); }
                catch { break; } // CompleteAdding on dispose
                try { m_Encoder.ProcessInput(0, input, 0); } catch { }
                input.Dispose();
            }
            else if (type == MediaEventTypes.TransformHaveOutput)
            {
                DrainOutput();
            }
        }
    }

    private void DrainOutput()
    {
        var outBuf = new OutputDataBuffer { StreamID = 0, Sample = null };
        try { m_Encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out _); }
        catch { return; }

        IMFSample? sample = outBuf.Sample;
        if (sample is null) return;

        try
        {
            bool key = false;
            try { key = sample.GetUInt32(MFSampleExtension_CleanPoint) != 0; } catch { }
            long ts = sample.SampleTime;

            IMFMediaBuffer contig = sample.ConvertToContiguousBuffer();
            contig.Lock(out IntPtr p, out _, out int len);
            byte[]? data = null;
            try
            {
                data = ArrayPool<byte>.Shared.Rent(m_OutputPrefixBytes + len);
                Marshal.Copy(p, data, m_OutputPrefixBytes, len);
            }
            catch
            {
                if (data is not null) ArrayPool<byte>.Shared.Return(data);
                throw;
            }
            finally
            {
                contig.Unlock();
                contig.Dispose();
            }

            var frame = new EncodedVideoFrame(
                data,
                m_OutputPrefixBytes,
                len,
                key,
                TimeSpan.FromTicks(ts));
            var handler = FrameEncoded;
            if (handler is null)
            {
                frame.Dispose();
            }
            else
            {
                try { handler(frame); }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }
        }
        finally { sample.Dispose(); }
    }

    // ---- BGRA (top-down) -> NV12, parallelized. Integer BT.601 coefficients. ----
    private static unsafe void BgraToNv12(ReadOnlySpan<byte> bgra, byte[] nv12, int w, int h)
    {
        fixed (byte* srcFixed = bgra)
        fixed (byte* dstFixed = nv12)
        {
            IntPtr src = (IntPtr)srcFixed, dst = (IntPtr)dstFixed;
            int frame = w * h;

            Parallel.For(0, h, y =>
            {
                byte* s = (byte*)src + (long)y * w * 4;
                byte* yr = (byte*)dst + (long)y * w;
                for (int x = 0; x < w; x++)
                {
                    int b = s[0], g = s[1], r = s[2];
                    s += 4;
                    yr[x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                }
            });

            Parallel.For(0, h / 2, j =>
            {
                byte* s = (byte*)src + (long)(j * 2) * w * 4;
                byte* uv = (byte*)dst + frame + (long)j * w;
                for (int x = 0; x < w; x += 2)
                {
                    int b = s[x * 4], g = s[x * 4 + 1], r = s[x * 4 + 2];
                    uv[x]     = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128); // U
                    uv[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);  // V
                }
            });
        }
    }

    public void Finish() { }

    public void Dispose()
    {
        m_Running = false;
        try { m_Input.CompleteAdding(); } catch { }
        try { m_Pump.Join(500); } catch { }
        try { m_Encoder?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        if (m_CodecApi != IntPtr.Zero)
        {
            try { Marshal.Release(m_CodecApi); } catch { }
            m_CodecApi = IntPtr.Zero;
        }
        try { m_EncoderAttributes?.Dispose(); } catch { }
        try { m_Encoder?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }
        try { m_Input.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct NativeVariant
    {
        private const ushort VariantTypeUInt32 = 19;

        [FieldOffset(0)] private ushort m_Type;
        [FieldOffset(8)] private uint m_UInt32;

        public static NativeVariant FromUInt32(uint value) => new()
        {
            m_Type = VariantTypeUInt32,
            m_UInt32 = value
        };
    }
}
