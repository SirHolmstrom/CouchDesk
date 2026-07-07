using System.Runtime.Versioning;
using Vortice.MediaFoundation;

namespace Capture.Gpu;

/// <summary>
/// Hardware H.264 encoder via the Media Foundation Sink Writer. Input is system-memory
/// BGRA (RGB32); the Sink Writer color-converts to NV12 and drives the platform's
/// HARDWARE encoder MFT (NVENC / Intel Quick Sync / AMD AMF) because we enable hardware
/// transforms. Output is an .mp4 file — enough to prove the GPU-capture + hardware-encode
/// pipeline and measure the bitrate win over the JPEG stream.
///
/// For live streaming you want raw Annex B (see H264StreamEncoder), not a finalized .mp4.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    // ---- Media Foundation attribute GUIDs (mfapi.h / codecapi.h) ----
    private static readonly Guid MF_MT_MAJOR_TYPE           = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE              = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMediaType_Video          = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264         = new("34363248-0000-0010-8000-00aa00389b71"); // 'H264'
    private static readonly Guid MFVideoFormat_RGB32        = new("00000016-0000-0010-8000-00aa00389b71"); // D3DFMT 22
    private static readonly Guid MF_MT_AVG_BITRATE          = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_FRAME_SIZE           = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE           = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO   = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE       = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE        = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_DEFAULT_STRIDE       = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MFSampleExtension_CleanPoint  = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    // Sink Writer attributes
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING       = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    private const uint MFSTARTUP_FULL             = 0;
    private const uint MF_VERSION                 = 0x00020070;
    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_High    = 100; // Main = 77, Base = 66

    private readonly int m_Width, m_Height, m_Fps, m_Stride;
    private readonly long m_FrameDurationTicks;
    private IMFSinkWriter m_Writer = null!;
    private int m_StreamIndex;

    public MediaFoundationH264Encoder(string outputPath, int width, int height, int fps, int bitrateBitsPerSec)
    {
        m_Width = width;
        m_Height = height;
        m_Fps = fps;
        m_Stride = width * 4;                                  // BGRA
        m_FrameDurationTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;

        MediaFactory.MFStartup();

        // Enable hardware encoder MFTs and disable the sink's real-time throttling so we
        // push frames as fast as we capture them.
        IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.SetUInt32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        attributes.SetUInt32(MF_SINK_WRITER_DISABLE_THROTTLING, 1);

        m_Writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);

        // ---- output (encoded) media type: H.264 ----
        IMFMediaType outType = MediaFactory.MFCreateMediaType();
        outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
        outType.SetUInt32(MF_MT_AVG_BITRATE, (uint)bitrateBitsPerSec);
        outType.SetUInt32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        outType.SetUInt32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);
        outType.SetUInt64(MF_MT_FRAME_SIZE, Pack(width, height));
        outType.SetUInt64(MF_MT_FRAME_RATE, Pack(fps, 1));
        outType.SetUInt64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        m_StreamIndex = m_Writer.AddStream(outType);

        // ---- input (uncompressed) media type: RGB32 / BGRA, top-down ----
        IMFMediaType inType = MediaFactory.MFCreateMediaType();
        inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        inType.Set(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        inType.SetUInt32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        inType.SetUInt32(MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        // Positive stride = top-down (matches Desktop Duplication). Negate if the encoded
        // video comes out vertically flipped.
        inType.SetUInt32(MF_MT_DEFAULT_STRIDE, (uint)m_Stride);
        inType.SetUInt64(MF_MT_FRAME_SIZE, Pack(width, height));
        inType.SetUInt64(MF_MT_FRAME_RATE, Pack(fps, 1));
        inType.SetUInt64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        m_Writer.SetInputMediaType(m_StreamIndex, inType, null);

        m_Writer.BeginWriting();
    }

    /// <summary>Pack two 32-bit values into the MF 64-bit attribute layout (hi=a, lo=b).</summary>
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
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = size;

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp.Ticks;
        sample.SampleDuration = m_FrameDurationTicks;
        if (forceKeyframe)
            sample.SetUInt32(MFSampleExtension_CleanPoint, 1);

        m_Writer.WriteSample(m_StreamIndex, sample);

        sample.Dispose();
        buffer.Dispose();
    }

    public void Finish() => m_Writer.Finalize();

    public void Dispose()
    {
        try { m_Writer?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }
    }
}
