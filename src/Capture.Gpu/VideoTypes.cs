namespace Capture.Gpu;

/// <summary>
/// A captured desktop frame. For this spike the pixels are copied to a managed BGRA
/// buffer (tight stride, top-down) so the encoder path is easy to reason about. The
/// PRODUCTION path keeps the D3D11 texture on the GPU and feeds it to the encoder
/// zero-copy (via an IMFDXGIDeviceManager bound to the Sink Writer) — see PROTOTYPE.md.
/// </summary>
public sealed class DesktopFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>BGRA8888, Width*Height*4 bytes, tight stride, top-down.</summary>
    public required byte[] Bgra { get; init; }

    /// <summary>
    /// False when Desktop Duplication reported no change since the last acquire (a
    /// timeout). The streaming path uses this to SKIP encoding idle frames entirely —
    /// the single biggest bandwidth win for a mostly-static desktop.
    /// </summary>
    public required bool Changed { get; init; }
}

/// <summary>Captures the desktop on the GPU. One instance per encode session.</summary>
public interface IGpuFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Grab the next frame, waiting up to <paramref name="timeoutMs"/>. Returns null on
    /// timeout (no screen change). Recreates the duplication automatically on access loss
    /// (resolution change, fullscreen transition, UAC secure desktop).
    /// </summary>
    DesktopFrame? TryAcquire(int timeoutMs);
}

/// <summary>
/// One encoded H.264 access unit in Annex B framing (start-code separated NAL units).
/// Used by the STREAMING variant of the encoder that emits NAL units for
/// WebSocket → WebCodecs. The probe encoder writes an .mp4 file instead, so it does not
/// produce these; see the "Productionizing" section of PROTOTYPE.md for wiring the Annex B
/// path into StreamSession as a new binary frame type.
/// </summary>
public sealed record EncodedVideoFrame(byte[] AnnexB, bool IsKeyframe, TimeSpan Timestamp);

/// <summary>Hardware H.264 encoder.</summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Encode one BGRA frame. <paramref name="timestamp"/> is presentation time measured
    /// from capture start. Set <paramref name="forceKeyframe"/> to request an IDR (first
    /// frame, a newly-joined viewer, or loss recovery).
    /// </summary>
    void Encode(ReadOnlySpan<byte> bgra, TimeSpan timestamp, bool forceKeyframe);

    /// <summary>Flush and finalize the output (writes the mp4 moov atom for the probe).</summary>
    void Finish();
}
