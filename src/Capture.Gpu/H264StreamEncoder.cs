using System.Runtime.Versioning;
using Vortice.MediaFoundation;

namespace Capture.Gpu;

/// <summary>
/// Streaming hardware H.264 encoder. Same proven path as MediaFoundationH264Encoder (the
/// Sink Writer drives the platform's HARDWARE encoder MFT), but targets a FRAGMENTED MP4
/// sink (<c>MFCreateFMPEG4MediaSink</c>) over a file opened with write-sharing. Fragmented
/// MP4 is streamable: an init segment (ftyp+moov) is written up front, then self-contained
/// moof+mdat fragments append. A reader tails <see cref="OutputPath"/> with a shared read
/// handle and forwards new bytes to the browser, which plays them via MediaSource (MSE) or
/// demuxes them for WebCodecs.
///
/// No async event pump and no custom COM callbacks — the Sink Writer manages the async
/// hardware MFT internally, exactly as the validated .mp4 probe did.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class H264StreamEncoder : IVideoEncoder
{
    private static readonly Guid MF_MT_MAJOR_TYPE           = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE              = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMediaType_Video          = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264         = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_RGB32        = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_AVG_BITRATE          = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_FRAME_SIZE           = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE           = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO   = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE       = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE        = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_DEFAULT_STRIDE       = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MFSampleExtension_CleanPoint  = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING       = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    // Codec-API keys for low-latency encoding, passed as encoding parameters.
    private static readonly Guid CODECAPI_AVLowLatencyMode              = new("9c27891a-ed7a-4e1b-8c3b-7d9dfc5e80c0");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode    = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate        = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount  = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_High      = 100;

    private readonly int m_Height, m_Fps, m_Stride;
    private readonly long m_FrameDurationTicks;
    private IMFSinkWriter m_Writer = null!;
    private IMFMediaSink? m_Sink;
    private IMFByteStream? m_ByteStream;
    private const int StreamIndex = 0;

    /// <summary>The fragmented-MP4 file the encoder writes; tail this to stream.</summary>
    public string OutputPath { get; }

    public H264StreamEncoder(string outputPath, int width, int height, int fps, int bitrateBitsPerSec)
    {
        OutputPath = outputPath;
        m_Height = height;
        m_Fps = fps;
        m_Stride = width * 4;
        m_FrameDurationTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;

        MediaFactory.MFStartup();

        // Encoded stream type (configures the encoder the Sink Writer inserts).
        IMFMediaType h264 = MediaFactory.MFCreateMediaType();
        h264.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        h264.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
        h264.SetUInt32(MF_MT_AVG_BITRATE, (uint)bitrateBitsPerSec);
        h264.SetUInt32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        h264.SetUInt32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);
        h264.SetUInt64(MF_MT_FRAME_SIZE, Pack(width, height));
        h264.SetUInt64(MF_MT_FRAME_RATE, Pack(fps, 1));
        h264.SetUInt64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));

        // Fragmented-MP4 sink over a write-shared file so a reader can tail it live.
        m_ByteStream = MediaFactory.MFCreateFile(
            FileAccessMode.MfAccessModeReadwrite,
            FileOpenMode.MfOpenModeDeleteIfExist,
            FileFlags.FlagsAllowWriteSharing,
            outputPath);
        MediaFactory.MFCreateFMPEG4MediaSink(m_ByteStream, h264, null, out IMFMediaSink sink);
        m_Sink = sink;

        IMFAttributes attrs = MediaFactory.MFCreateAttributes(2);
        attrs.SetUInt32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        attrs.SetUInt32(MF_SINK_WRITER_DISABLE_THROTTLING, 1);
        m_Writer = MediaFactory.MFCreateSinkWriterFromMediaSink(m_Sink, attrs);

        // Uncompressed input: RGB32 / BGRA, top-down.
        IMFMediaType rgb = MediaFactory.MFCreateMediaType();
        rgb.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        rgb.Set(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        rgb.SetUInt32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        rgb.SetUInt32(MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        rgb.SetUInt32(MF_MT_DEFAULT_STRIDE, (uint)m_Stride);
        rgb.SetUInt64(MF_MT_FRAME_SIZE, Pack(width, height));
        rgb.SetUInt64(MF_MT_FRAME_RATE, Pack(fps, 1));
        rgb.SetUInt64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        // Low-latency encoder config — the decisive fix for full-motion content. By
        // default the MF H.264 encoder uses B-frames (reorder delay) + VBR (bitrate spikes
        // that flood the socket), which together push latency to multiple seconds. Force
        // real-time mode, zero B-frames, and CBR capped at the target bitrate.
        IMFAttributes encodingParams = MediaFactory.MFCreateAttributes(4);
        encodingParams.SetUInt32(CODECAPI_AVLowLatencyMode, 1);
        encodingParams.SetUInt32(CODECAPI_AVEncMPVDefaultBPictureCount, 0);
        encodingParams.SetUInt32(CODECAPI_AVEncCommonRateControlMode, 0); // 0 = CBR
        encodingParams.SetUInt32(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrateBitsPerSec);
        try { m_Writer.SetInputMediaType(StreamIndex, rgb, encodingParams); }
        catch { m_Writer.SetInputMediaType(StreamIndex, rgb, null); } // fall back if the encoder rejects them

        m_Writer.BeginWriting();
    }

    private static ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;

    public void Encode(ReadOnlySpan<byte> bgra, TimeSpan timestamp, bool forceKeyframe)
    {
        int size = m_Stride * m_Height;
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(size);
        buffer.Lock(out IntPtr dst, out _, out _);
        try
        {
            unsafe
            {
                fixed (byte* src = bgra)
                    Buffer.MemoryCopy(src, dst.ToPointer(), size, size);
            }
        }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = size;

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp.Ticks;
        sample.SampleDuration = m_FrameDurationTicks;
        if (forceKeyframe) sample.SetUInt32(MFSampleExtension_CleanPoint, 1);

        m_Writer.WriteSample(StreamIndex, sample);
        sample.Dispose();
        buffer.Dispose();
    }

    public void Finish() { try { m_Writer.Finalize(); } catch { } }

    public void Dispose()
    {
        try { m_Writer?.Dispose(); } catch { }
        try { m_Sink?.Shutdown(); } catch { }
        try { m_Sink?.Dispose(); } catch { }
        try { m_ByteStream?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }
    }
}
