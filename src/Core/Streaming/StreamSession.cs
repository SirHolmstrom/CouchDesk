using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text.Json;
using Core.Capture;
using Core.Config;
using Core.Input;
using Core.Logging;
using Core.Security;
using Capture.Gpu;

namespace Core.Streaming;

/// <summary>
/// One connected client. Runs a receive loop (input + control messages) concurrently
/// with a send loop (capture -> encode -> push) at a target FPS. Honors
/// RemoteAccessEnabled live and supports quality/FPS/monitor changes mid-session.
///
/// Video and control use separate WebSockets when supported by the browser. Each socket
/// still permits only one outstanding send, so each has its own send semaphore. Cached
/// clients and a reconnecting control channel safely fall back to the video socket.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamSession : IDisposable
{
    private const int LargeVideoFrameThresholdBytes = 64 * 1024;
    private const int VideoFragmentBytes = 16 * 1024;

    // Control state: written by the receive loop, read by the send loop. volatile
    // int/bool reads & writes are atomic and visible across threads — enough here.
    private volatile int m_Quality;
    private volatile int m_Fps;
    private volatile int m_Monitor = 0;
    private volatile int m_VideoBitratePercent = 100;
    private volatile int m_AppliedVideoBitrateKbps;
    private volatile bool m_ForceKeyframe = true; // resend a full frame after a control change
    private volatile bool m_UsesH264;

    private readonly WebSocket m_Socket;
    private readonly AppConfig m_Config;
    private readonly IScreenCapturer m_Capturer;
    private readonly bool m_IsLanClient;
    private readonly SessionPermissions m_Permissions;
    private readonly SessionInfo m_LoginSession;
    private readonly PointerInputArbiter m_PointerInput;
    private readonly Func<bool> m_IsAccessStillValid;
    private readonly Action m_TouchLoginSession;
    private readonly Action m_RevokeLoginSession;
    private readonly CancellationTokenSource m_Cancellation;
    private readonly SemaphoreSlim m_SendLock = new(1, 1); // serialize ALL sends
    private readonly SemaphoreSlim m_ControlSendLock = new(1, 1);
    private readonly object m_ControlGate = new();
    private readonly object m_HeldInputLock = new();
    private readonly HashSet<string> m_HeldMouseButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ushort> m_HeldKeys = new();
    private long m_LastPointerBlockedNoticeAtMs;
    private string m_LastPointerBlockedBy = "";
    private int m_Kicked;
    private long m_LastCaptureTicks;
    private long m_PeakCaptureTicks;
    private long m_LastCaptureAcquireTicks;
    private long m_PeakCaptureAcquireTicks;
    private long m_LastCaptureCopyTicks;
    private long m_PeakCaptureCopyTicks;
    private long m_LastCaptureMapTicks;
    private long m_PeakCaptureMapTicks;
    private long m_LastCaptureCpuCopyTicks;
    private long m_PeakCaptureCpuCopyTicks;
    private long m_LastEncodeTicks;
    private long m_PeakEncodeTicks;
    private long m_LastControlSendWaitTicks;
    private long m_PeakControlSendWaitTicks;
    private long m_LastControlSendTicks;
    private long m_PeakControlSendTicks;
    private long m_LastVideoSendWaitTicks;
    private long m_PeakVideoSendWaitTicks;
    private long m_LastVideoSendTicks;
    private long m_PeakVideoSendTicks;
    private int m_VideoQueueDepth;
    private int m_PeakVideoQueueDepth;
    private int m_LastVideoFrameBytes;
    private int m_PeakVideoFrameBytes;
    private long m_VideoBytesSinceSnapshot;
    private int m_VideoFramesSinceSnapshot;
    private int m_PacedVideoFrameSeen;
    private int m_LargeVideoFrameInFlight;
    private long m_LastPerformanceSnapshotAt;
    private int m_KeyframeSeen;
    private long m_LastHostTimerTicks;
    private long m_PeakHostTimerTicks;
    private long m_LastCpuSampleAt;
    private long m_LastProcessCpuTicks;
    private ulong m_LastSystemIdleTicks;
    private ulong m_LastSystemKernelTicks;
    private ulong m_LastSystemUserTicks;
    private WebSocket? m_ControlSocket;
    private Task? m_ControlTask;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string ControlToken { get; } = Guid.NewGuid().ToString("N");
    public string ClientIp { get; }
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    public int Quality => m_Quality;
    public int Fps => m_Fps;
    public int Monitor => m_Monitor;
    public SessionRole Role { get; }
    public GuestAccessLevel? GuestAccessLevel { get; }
    public Guid? GuestInviteId { get; }

    public StreamSession(
        WebSocket socket,
        AppConfig config,
        string clientIp,
        bool isLanClient,
        SessionInfo loginSession,
        PointerInputArbiter pointerInput,
        Func<bool> isAccessStillValid,
        Action touchLoginSession,
        Action revokeLoginSession,
        CancellationToken outerToken)
    {
        m_Socket = socket;
        m_Config = config;
        m_Quality = Math.Clamp(config.JpegQuality, 10, 95);
        m_Fps = Math.Clamp(config.FpsLimit, 1, 60);
        ClientIp = clientIp;
        m_IsLanClient = isLanClient;
        Role = loginSession.Role;
        GuestAccessLevel = loginSession.GuestAccessLevel;
        GuestInviteId = loginSession.GuestInviteId;
        m_LoginSession = loginSession;
        m_Permissions = loginSession.Permissions;
        m_PointerInput = pointerInput;
        m_IsAccessStillValid = isAccessStillValid;
        m_TouchLoginSession = touchLoginSession;
        m_RevokeLoginSession = revokeLoginSession;
        m_Capturer = new GdiScreenCapturer(); // per-session: avoids cross-thread bitmap races
        m_Cancellation = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        m_LastPerformanceSnapshotAt = Stopwatch.GetTimestamp();
        InitializeCpuSampler();
    }

    /// <summary>Drives the session until either loop ends, then tears down cleanly.</summary>
    public async Task RunAsync()
    {
        AuditLogger.Log("STREAM_CONNECT", ClientIp, $"id={Id}");
        await SendVideoTextAsync(new { t = "control-channel", token = ControlToken });
        await SendMonitorListAsync();
        await SendVideoStatusAsync("connected");

        var receive = ReceiveLoopAsync(m_Socket, m_Cancellation.Token);
        var send = m_Config.UseHardwareVideo
            ? (m_Config.VideoLowLatency
                ? WebCodecsSendLoopAsync(m_Cancellation.Token)   // per-frame H.264 → WebCodecs
                : VideoSendLoopAsync(m_Cancellation.Token))      // fragmented MP4 → MSE
            : SendLoopAsync(m_Cancellation.Token);               // JPEG tiles
        var cursor = CursorLoopAsync(m_Cancellation.Token);
        var scheduler = HostSchedulerProbeAsync(m_Cancellation.Token);

        await Task.WhenAny(receive, send); // whichever ends first…
        m_Cancellation.Cancel();           // …tears down the other
        Task? controlTask;
        WebSocket? controlSocket;
        lock (m_ControlGate)
        {
            controlTask = m_ControlTask;
            controlSocket = m_ControlSocket;
        }
        try { controlSocket?.Abort(); } catch { }
        try { await Task.WhenAll(receive, send, cursor, scheduler); } catch { /* expected on cancel */ }
        if (controlTask is not null)
        {
            try { await controlTask; } catch { }
        }

        ReleaseHeldInputs();
        await CloseAsync();
        AuditLogger.Log("STREAM_DISCONNECT", ClientIp, $"id={Id}");
    }

    public bool MatchesLoginSession(SessionInfo loginSession) =>
        ReferenceEquals(m_LoginSession, loginSession);

    public Task RunControlSocketAsync(WebSocket socket, CancellationToken requestAborted)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (m_ControlGate)
        {
            if (m_Cancellation.IsCancellationRequested
                || m_ControlSocket is not null)
                return RejectControlSocketAsync(socket);

            m_ControlSocket = socket;
            m_ControlTask = completion.Task;
        }

        _ = CompleteControlSocketAsync(socket, requestAborted, completion);
        return completion.Task;
    }

    private async Task CompleteControlSocketAsync(
        WebSocket socket,
        CancellationToken requestAborted,
        TaskCompletionSource completion)
    {
        try
        {
            await RunControlSocketCoreAsync(socket, requestAborted);
            completion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task RunControlSocketCoreAsync(WebSocket socket, CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            m_Cancellation.Token,
            requestAborted);
        try
        {
            await ReceiveLoopAsync(socket, linked.Token);
        }
        finally
        {
            ReleaseHeldInputs();
            lock (m_ControlGate)
            {
                if (ReferenceEquals(m_ControlSocket, socket)) m_ControlSocket = null;
            }
            await CloseSocketAsync(socket, "control closed");
        }
    }

    private static async Task RejectControlSocketAsync(WebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "control channel already attached",
                    CancellationToken.None);
        }
        catch { }
    }

    // ---------- cursor overlay ----------
    // The desktop image/video capture paths do not carry the OS cursor. Sending it
    // separately keeps hardware video simple and prevents cursor-only movement from
    // dirtying JPEG tiles.
    private async Task CursorLoopAsync(CancellationToken ct)
    {
        const int AuthorityPollIntervalMs = 100;
        const int ObserverIntervalMs = 140; // ~7 Hz; observers interpolate between fresh host positions
        bool? lastVisible = null;
        int lastX = int.MinValue, lastY = int.MinValue, lastMonitor = int.MinValue;
        string lastSource = "";
        int lastHostBlockBucket = -1;
        bool errorLogged = false;

        while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
        {
            int nextIntervalMs = ObserverIntervalMs;
            try
            {
                var monitors = m_Capturer.GetMonitors();
                var monitor = monitors.FirstOrDefault(x => x.Index == m_Monitor) ?? monitors[0];
                var cursor = CursorTracker.Get();
                bool visible = cursor.HasPosition;
                if (cursor.HasPosition)
                {
                    m_PointerInput.ObserveCursor(cursor.X, cursor.Y);
                    int hostBlockMs = m_PointerInput.HostBlockRemainingMs();
                    if (hostBlockMs > 0 && m_Permissions.CanControl)
                        await SendPointerBlockedNoticeAsync("host", hostBlockMs, ct);
                }

                int localX = visible ? Math.Clamp(cursor.X - monitor.X, 0, monitor.Width - 1) : -1;
                int localY = visible ? Math.Clamp(cursor.Y - monitor.Y, 0, monitor.Height - 1) : -1;
                var cursorState = m_PointerInput.CursorStateFor(Id);
                nextIntervalMs = cursorState.Source == "self"
                    ? AuthorityPollIntervalMs
                    : ObserverIntervalMs;
                int hostBlockBucket = cursorState.HostBlockMs > 0 ? cursorState.HostBlockMs / 500 : 0;
                bool stateChanged = lastVisible != visible
                    || lastMonitor != monitor.Index
                    || lastSource != cursorState.Source
                    || lastHostBlockBucket != hostBlockBucket;
                bool positionChanged = localX != lastX || localY != lastY;

                // The authoritative browser already moves its own pointer immediately
                // and is the source of truth while it owns steering. Do not echo every
                // position back to it; only send ownership/host-takeover state changes.
                if (stateChanged || (cursorState.Source != "self" && positionChanged))
                {
                    lastVisible = visible;
                    lastMonitor = monitor.Index;
                    lastX = localX;
                    lastY = localY;
                    lastSource = cursorState.Source;
                    lastHostBlockBucket = hostBlockBucket;
                    await SendCursorStateAsync(
                        visible,
                        localX,
                        localY,
                        monitor.Width,
                        monitor.Height,
                        cursorState.Source,
                        cursorState.HostBlockMs,
                        ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Cursor overlay is a nice-to-have; never tear down a stream for it.
                if (!errorLogged)
                {
                    errorLogged = true;
                    AuditLogger.Log("CURSOR_ERROR", ClientIp, ex.Message);
                }
            }

            try { await Task.Delay(nextIntervalMs, ct); }
            catch { break; }
        }
    }

    /// <summary>Used by the registry / tray "disconnect" action.</summary>
    public void Cancel() => m_Cancellation.Cancel();

    /// <summary>Host-requested disconnect: tell the browser not to auto-reconnect.</summary>
    public void Kick()
    {
        if (Interlocked.Exchange(ref m_Kicked, 1) != 0) return;
        try { m_RevokeLoginSession(); } catch { }
        _ = KickAsync();
    }

    private async Task KickAsync()
    {
        try
        {
            await SendTextAsync(new { t = "kicked" }, CancellationToken.None);
            WebSocket? control;
            lock (m_ControlGate) control = m_ControlSocket;
            if (control is not null) await CloseSocketAsync(control, "kicked");
            await CloseSocketAsync(m_Socket, "kicked");
        }
        catch { }
        finally
        {
            m_Cancellation.Cancel();
        }
    }

    // ---------- hardware-video send loop (GPU capture + hardware H.264, fragmented MP4) ----------
    // Captures on the GPU and hardware-encodes to a fragmented-MP4 temp file, then tails
    // that file and pushes new bytes to the client (which plays them via MediaSource).
    // Binary video messages carry a 1-byte type prefix (3) so the client routes them apart
    // from the JPEG-tile frames (type 1). Monitor/fps/bitrate are taken at connect time;
    // changing them mid-session applies on reconnect (the tile path stays fully live).
    private async Task VideoSendLoopAsync(CancellationToken ct)
    {
        await SendVideoStatusAsync("video"); // ordered before the first MSE bytes

        string tempPath = Path.Combine(Path.GetTempPath(), $"rdl_{Id}.mp4");
        DesktopDuplicationSource? source = null;
        H264StreamEncoder? encoder = null;
        Task? captureTask = null;
        try
        {
            int fps = Math.Clamp(m_Fps, 1, 60);
            int bitrate = TargetVideoBitrateBitsPerSecond();
            source = new DesktopDuplicationSource(m_Monitor);
            encoder = new H264StreamEncoder(tempPath, source.Width, source.Height, fps, bitrate);
            m_AppliedVideoBitrateKbps = bitrate / 1000;
            m_UsesH264 = true;

            var capSource = source;
            var capEncoder = encoder;
            captureTask = Task.Run(() => CaptureLoop(capSource, capEncoder, fps, ct), ct);

            // Tail the fragmented-MP4 file: FlagsAllowWriteSharing on the MF side + this
            // shared read handle let us read what MF is still writing.
            using var reader = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[64 * 1024];
            var framed = new byte[buffer.Length + 1];
            framed[0] = (byte)StreamBinaryMessageType.FragmentedMp4;
            long position = 0;

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                if (!m_IsAccessStillValid()) { await SendStatusAsync("access-revoked"); break; }
                if (!m_IsLanClient && !m_Config.RemoteAccessEnabled) { await SendStatusAsync("disabled"); break; }

                long length = reader.Length;
                if (length > position)
                {
                    reader.Seek(position, SeekOrigin.Begin);
                    int toRead = (int)Math.Min(buffer.Length, length - position);
                    int read = await reader.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read > 0)
                    {
                        Buffer.BlockCopy(buffer, 0, framed, 1, read);
                        RecordVideoFrame(read + 1);
                        try { await SendRawAsync(framed.AsMemory(0, read + 1), WebSocketMessageType.Binary, ct, trackVideo: true); }
                        catch { break; }
                        position += read;
                    }
                }
                else
                {
                    try { await Task.Delay(8, ct); } catch { break; }
                }
            }
        }
        catch (Exception ex)
        {
            AuditLogger.Log("VIDEO_ERROR", ClientIp, ex.Message);
        }
        finally
        {
            m_Cancellation.Cancel();
            if (captureTask is not null) { try { await captureTask; } catch { } }
            try { encoder?.Finish(); } catch { }
            encoder?.Dispose();
            source?.Dispose();
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    // Feeds captured BGRA frames to the encoder at the target FPS on a worker thread.
    private void CaptureLoop(DesktopDuplicationSource source, H264StreamEncoder encoder, int fps, CancellationToken ct)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            byte[]? last = source.TryAcquire(500)?.Bgra;
            int submitted = 0;
            long nextFrameAt = stopwatch.ElapsedMilliseconds;

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                int targetFps = Math.Clamp(m_Fps, 1, Math.Clamp(fps, 1, 60));
                int interval = 1000 / targetFps;
                DesktopFrame? frame;
                long captureStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    frame = source.TryAcquire(interval);
                    RecordCapture(captureStartedAt);
                    RecordCaptureTiming(source.LastTiming);
                }
                catch (Exception ex)
                {
                    RecordCapture(captureStartedAt);
                    AuditLogger.Log("CAPTURE_ERROR", ClientIp, ex.Message);
                    break;
                }

                byte[] bgra = frame?.Bgra ?? last ?? Array.Empty<byte>();
                if (bgra.Length == 0) continue;
                last = bgra;

                // Force an IDR ~twice a second: the fragmented-MP4 sink starts a new
                // streamable fragment at each key frame, so shorter intervals mean smaller
                // fragments and lower end-to-end latency (matters most for video content).
                int keyEvery = Math.Max(1, fps / 2);
                bool key = submitted == 0 || m_ForceKeyframe || submitted % keyEvery == 0;
                if (m_ForceKeyframe) m_ForceKeyframe = false;

                long encodeStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    encoder.Encode(bgra, stopwatch.Elapsed, key);
                    RecordEncode(encodeStartedAt);
                }
                catch (Exception ex)
                {
                    RecordEncode(encodeStartedAt);
                    AuditLogger.Log("VIDEO_ENCODE_ERROR", ClientIp, ex.Message);
                    break;
                }
                submitted++;

                nextFrameAt += interval;
                int behind = (int)(nextFrameAt - stopwatch.ElapsedMilliseconds);
                if (behind > 0) { try { Thread.Sleep(behind); } catch { } }
                else nextFrameAt = stopwatch.ElapsedMilliseconds;
            }
        }
        finally
        {
            m_Cancellation.Cancel(); // ensure the tailing loop tears down if capture ends
        }
    }

    // ---------- low-latency WebCodecs send loop (per-frame H.264, no container) ----------
    // Encodes one Annex-B access unit per frame and pushes it immediately as a binary
    // message [u8 type=4][u8 flags(bit0=keyframe)][annexB...]. No fragment buffering, so
    // latency drops toward capture + network + decode. The browser decodes with WebCodecs.
    private async Task WebCodecsSendLoopAsync(CancellationToken ct)
    {
        DesktopDuplicationSource? source = null;
        H264LowLatencyEncoder? encoder = null;
        int fps = Math.Clamp(m_Fps, 1, 60);
        try
        {
            int bitrate = TargetVideoBitrateBitsPerSecond();
            source = new DesktopDuplicationSource(m_Monitor);
            encoder = new H264LowLatencyEncoder(
                source.Width,
                source.Height,
                fps,
                bitrate,
                outputPrefixBytes: 2);
            m_AppliedVideoBitrateKbps = bitrate / 1000;
            m_UsesH264 = true;
        }
        catch (Exception ex)
        {
            // No hardware H.264 encoder / capture unavailable — fall back to the JPEG tile
            // path so the session still works with hardware video defaulted on.
            AuditLogger.Log("VIDEO_FALLBACK", ClientIp, ex.Message);
            source?.Dispose();
            await SendLoopAsync(ct);
            return;
        }

        await SendVideoStatusAsync("video-h264"); // ordered before the first Annex B keyframe

        Task? captureTask = null;
        // Remote desktop must prefer a fresh image over a complete history. A large
        // encoded queue turns brief Wi-Fi congestion into seconds of visible catch-up.
        // Keep at most two frames and resynchronise with an IDR if that budget is hit.
        var queue = new BlockingCollection<EncodedVideoFrame>(2);
        int recoveryKeyframePending = 0;
        try
        {
            encoder.FrameEncoded += f =>
            {
                try
                {
                    if (queue.IsAddingCompleted)
                    {
                        f.Dispose();
                        return;
                    }

                    if (Volatile.Read(ref recoveryKeyframePending) != 0)
                    {
                        // Deltas after a dropped reference frame are not decodable.
                        // Ignore them until the forced IDR arrives.
                        if (!f.IsKeyframe)
                        {
                            f.Dispose();
                            return;
                        }
                        while (queue.TryTake(out var stale)) stale.Dispose();
                        if (!queue.TryAdd(f)) f.Dispose();
                        RecordVideoQueue(queue.Count);
                        return;
                    }

                    if (queue.TryAdd(f))
                    {
                        RecordVideoQueue(queue.Count);
                        return;
                    }

                    // The sender is behind. Remove stale frames immediately instead
                    // of preserving them and making the viewer play catch-up.
                    while (queue.TryTake(out var stale)) stale.Dispose();
                    if (f.IsKeyframe)
                    {
                        if (!queue.TryAdd(f)) f.Dispose();
                        RecordVideoQueue(queue.Count);
                        return;
                    }

                    Volatile.Write(ref recoveryKeyframePending, 1);
                    m_ForceKeyframe = true;
                    f.Dispose();
                    RecordVideoQueue(queue.Count);
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding raced with the encoder callback during teardown.
                    f.Dispose();
                }
            };

            var capSource = source;
            var capEncoder = encoder;
            captureTask = Task.Run(() => CaptureLoopLowLatency(capSource, capEncoder, fps, ct), ct);

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                if (!m_IsAccessStillValid()) { await SendStatusAsync("access-revoked"); break; }
                if (!m_IsLanClient && !m_Config.RemoteAccessEnabled) { await SendStatusAsync("disabled"); break; }

                EncodedVideoFrame? frame;
                try { if (!queue.TryTake(out frame, 100, ct) || frame is null) continue; }
                catch { break; }
                RecordVideoQueue(queue.Count);
                RecordVideoFrame(frame.MessageLength, frame.IsKeyframe);

                if (frame.IsKeyframe && Volatile.Read(ref recoveryKeyframePending) != 0)
                    Volatile.Write(ref recoveryKeyframePending, 0);

                try
                {
                    byte[] buffer = frame.Buffer;
                    buffer[0] = (byte)StreamBinaryMessageType.H264AnnexB;
                    buffer[1] = (byte)(frame.IsKeyframe ? 1 : 0);
                    await SendRawAsync(
                        buffer.AsMemory(0, frame.MessageLength),
                        WebSocketMessageType.Binary,
                        ct,
                        trackVideo: true,
                        paceLargeVideo: true);
                }
                catch { break; }
                finally { frame.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            AuditLogger.Log("VIDEO_ERROR", ClientIp, ex.Message);
        }
        finally
        {
            m_Cancellation.Cancel();
            try { queue.CompleteAdding(); } catch { }
            if (captureTask is not null) { try { await captureTask; } catch { } }
            encoder?.Dispose();
            source?.Dispose();
            while (queue.TryTake(out var remaining)) remaining.Dispose();
            RecordVideoQueue(0);
            try { queue.Dispose(); } catch { }
        }
    }

    private void CaptureLoopLowLatency(DesktopDuplicationSource source, H264LowLatencyEncoder encoder, int fps, CancellationToken ct)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            byte[]? last = source.TryAcquire(500)?.Bgra;
            int submitted = 0;
            int appliedBitrate = TargetVideoBitrateBitsPerSecond();
            long nextFrameAt = stopwatch.ElapsedMilliseconds;

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                int targetFps = Math.Clamp(m_Fps, 1, Math.Clamp(fps, 1, 60));
                int interval = 1000 / targetFps;
                int targetBitrate = TargetVideoBitrateBitsPerSecond();
                if (targetBitrate != appliedBitrate)
                {
                    if (!encoder.TrySetBitrate(targetBitrate))
                        AuditLogger.Log("BITRATE_UPDATE_UNSUPPORTED", ClientIp, $"targetKbps={targetBitrate / 1000}");
                    else
                        m_AppliedVideoBitrateKbps = targetBitrate / 1000;
                    appliedBitrate = targetBitrate;
                }
                DesktopFrame? frame;
                long captureStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    frame = source.TryAcquire(interval);
                    RecordCapture(captureStartedAt);
                    RecordCaptureTiming(source.LastTiming);
                }
                catch (Exception ex)
                {
                    RecordCapture(captureStartedAt);
                    AuditLogger.Log("CAPTURE_ERROR", ClientIp, ex.Message);
                    break;
                }

                byte[] bgra = frame?.Bgra ?? last ?? Array.Empty<byte>();
                if (bgra.Length == 0) continue;
                last = bgra;

                // Do not feed dependent delta frames into the encoder while a large
                // access unit is being paced. Otherwise the two-frame send queue can
                // fill, drop a reference, and force a cascade of replacement IDRs.
                if (Volatile.Read(ref m_LargeVideoFrameInFlight) != 0) continue;

                // Per-frame delivery needs a key frame only at start / on a control change;
                // the encoder's own GOP handles periodic IDRs, so full-frame resends are rare.
                bool key = submitted == 0 || m_ForceKeyframe;
                if (m_ForceKeyframe) m_ForceKeyframe = false;

                long encodeStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    encoder.Encode(bgra, stopwatch.Elapsed, key);
                    RecordEncode(encodeStartedAt);
                }
                catch (Exception ex)
                {
                    RecordEncode(encodeStartedAt);
                    AuditLogger.Log("VIDEO_ENCODE_ERROR", ClientIp, ex.Message);
                    break;
                }
                submitted++;

                nextFrameAt += interval;
                int behind = (int)(nextFrameAt - stopwatch.ElapsedMilliseconds);
                if (behind > 0) { try { Thread.Sleep(behind); } catch { } }
                else nextFrameAt = stopwatch.ElapsedMilliseconds;
            }
        }
        finally { m_Cancellation.Cancel(); }
    }

    // ---------- send loop ----------
    private async Task SendLoopAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        long lastKeyframeMs = long.MinValue;
        const int TileSize = 128;
        const long KeyframeIntervalMs = 7000; // periodic full refresh (cheap insurance)

        while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
        {
            if (!m_IsAccessStillValid())
            {
                await SendStatusAsync("access-revoked");
                break;
            }

            // The tray switch closes Internet sessions only. LAN streaming remains
            // available whenever the local host is running.
            if (!m_IsLanClient && !m_Config.RemoteAccessEnabled)
            {
                await SendStatusAsync("disabled");
                break;
            }

            long frameStart = stopwatch.ElapsedMilliseconds;
            bool keyframe = m_ForceKeyframe || (frameStart - lastKeyframeMs) >= KeyframeIntervalMs;
            if (m_ForceKeyframe) m_ForceKeyframe = false;

            DeltaFrame frame;
            try
            {
                frame = m_Capturer.CaptureDelta(m_Monitor, m_Quality, keyframe, TileSize);
            }
            catch (Exception ex)
            {
                AuditLogger.Log("CAPTURE_ERROR", ClientIp, ex.Message);
                break;
            }

            if (keyframe) lastKeyframeMs = frameStart;

            // Empty tile list => nothing changed; skip the send (bandwidth saver).
            if (frame.Tiles.Count > 0)
            {
                try { await SendRawAsync(SerializeFrame(frame), WebSocketMessageType.Binary, ct, trackVideo: true); }
                catch { break; } // client gone
            }

            int interval = 1000 / Math.Clamp(m_Fps, 1, 60);
            int elapsed = (int)(stopwatch.ElapsedMilliseconds - frameStart);
            int delay = interval - elapsed;
            if (delay > 0)
            {
                try { await Task.Delay(delay, ct); }
                catch { break; }
            }
        }
    }

    /// <summary>
    /// Binary frame format (little-endian): byte type=1, u16 width, u16 height,
    /// u16 tileCount, then per tile: u16 x, u16 y, u16 w, u16 h, i32 jpegLen, jpeg bytes.
    /// </summary>
    private static byte[] SerializeFrame(DeltaFrame frame)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)StreamBinaryMessageType.JpegTiles);
        writer.Write((ushort)frame.Width);
        writer.Write((ushort)frame.Height);
        writer.Write((ushort)frame.Tiles.Count);
        foreach (var tile in frame.Tiles)
        {
            writer.Write((ushort)tile.X);
            writer.Write((ushort)tile.Y);
            writer.Write((ushort)tile.W);
            writer.Write((ushort)tile.H);
            writer.Write(tile.Jpeg.Length); // int32
            writer.Write(tile.Jpeg);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private async Task<bool> TryUsePointerInputAsync(CancellationToken ct)
    {
        if (!m_Permissions.CanControl) return false;

        int priority = PointerInputArbiter.PriorityFor(Role, GuestAccessLevel);
        var decision = m_PointerInput.TryBeginRemoteInput(Id, priority);
        if (decision.Allowed) return true;

        await SendPointerBlockedNoticeAsync(decision.BlockedBy, decision.RetryAfterMs, ct);

        return false;
    }

    private async Task SendPointerBlockedNoticeAsync(string blockedBy, int retryAfterMs, CancellationToken ct)
    {
        long now = Environment.TickCount64;
        if (blockedBy == m_LastPointerBlockedBy
            && now - m_LastPointerBlockedNoticeAtMs <= 500)
            return;

        m_LastPointerBlockedBy = blockedBy;
        m_LastPointerBlockedNoticeAtMs = now;
        await SendTextAsync(new
        {
            t = "steering",
            state = "blocked",
            by = blockedBy,
            ms = retryAfterMs
        }, ct);
    }

    // ---------- receive loop ----------
    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer, ct); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (result.EndOfMessage)
                    await HandleBinaryInputAsync(buffer.AsMemory(0, result.Count), ct);
                continue;
            }
            if (result.MessageType != WebSocketMessageType.Text) continue;

            Dictionary<string, JsonElement>? message;
            try { message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(buffer.AsSpan(0, result.Count)); }
            catch { continue; }
            if (message is null || !message.TryGetValue("t", out var typeElement)) continue;
            try { m_TouchLoginSession(); } catch { }

            switch (typeElement.GetString())
            {
                // ----- input -----
                case "move":
                {
                    if (!await TryUsePointerInputAsync(ct)) break;
                    var monitor = m_Capturer.GetMonitors().FirstOrDefault(x => x.Index == m_Monitor)
                                  ?? m_Capturer.GetMonitors()[0];
                    int pixelX = monitor.X + (int)(message["x"].GetDouble() * monitor.Width);
                    int pixelY = monitor.Y + (int)(message["y"].GetDouble() * monitor.Height);
                    InputInjector.MoveMouseAbsolute(pixelX, pixelY);
                    m_PointerInput.NoteRemoteMove(pixelX, pixelY);
                    break;
                }
                case "btn":
                {
                    if (!m_Permissions.CanControl) break;
                    bool down = message["d"].GetBoolean();
                    string button = message["b"].GetString()!;
                    if (!down || await TryUsePointerInputAsync(ct))
                    {
                        InputInjector.MouseButton(button, down);
                        TrackMouseButton(button, down);
                    }
                    break;
                }
                case "scroll":
                    if (await TryUsePointerInputAsync(ct)) InputInjector.Scroll(message["delta"].GetInt32());
                    break;
                case "key":
                {
                    ushort virtualKey = (ushort)message["vk"].GetInt32();
                    if (m_Permissions.CanControl
                        && (m_Permissions.CanUseSystemKeys || !IsSystemKey(virtualKey)))
                    {
                        InputInjector.Key(virtualKey, message["d"].GetBoolean());
                        TrackKey(virtualKey, message["d"].GetBoolean());
                    }
                    break;
                }
                case "text":
                    if (m_Permissions.CanControl)
                    {
                        string text = message["s"].GetString() ?? "";
                        InputInjector.TypeUnicode(text[..Math.Min(text.Length, 4096)]);
                    }
                    break;
                case "combo":
                {
                    if (!m_Permissions.CanUseSystemKeys) break;
                    var modifiers = message["mods"].EnumerateArray().Select(e => (ushort)e.GetInt32()).ToArray();
                    InputInjector.KeyCombo(modifiers, (ushort)message["key"].GetInt32());
                    break;
                }

                // ----- live controls -----
                case "quality":
                    m_Quality = Math.Clamp(message["v"].GetInt32(), 10, 95);
                    // JPEG quality changes require a clean refresh. The active H.264
                    // encoder has a fixed bitrate, so forcing an IDR here only creates
                    // a large unnecessary packet and can amplify a latency spike.
                    if (!m_UsesH264) m_ForceKeyframe = true;
                    break;
                case "bitrate":
                    m_VideoBitratePercent = Math.Clamp(message["v"].GetInt32(), 25, 100);
                    break;
                case "fps": m_Fps = Math.Clamp(message["v"].GetInt32(), 1, 60); break;
                case "monitor":
                {
                    int index = message["v"].GetInt32();
                    if (m_Capturer.GetMonitors().Any(x => x.Index == index))
                    {
                        m_Monitor = index;
                        m_ForceKeyframe = true;
                        await SendMonitorListAsync();
                    }
                    break;
                }

                // ----- latency -----
                case "ping":
                    await SendTextAsync(new
                    {
                        t = "pong",
                        ts = message["ts"].GetDouble(),
                        perf = TakePerformanceSnapshot()
                    }, ct);
                    break;

                // ----- WebCodecs decoder asks for a fresh IDR (startup / loss recovery) -----
                case "keyframe": m_ForceKeyframe = true; break;

                // ----- read focused field text (UI Automation), for the keyboard echo -----
                case "getFocusText":
                {
                    if (!m_Permissions.CanControl) break;
                    var focusText = FocusedText.TryRead();
                    if (focusText != null) await SendTextAsync(new { t = "focusText", text = focusText }, ct);
                    break;
                }
            }
        }
    }

    private async Task HandleBinaryInputAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        if (packet.Length != 5
            || packet.Span[0] != (byte)StreamBinaryMessageType.PointerMove)
            return;

        try { m_TouchLoginSession(); } catch { }
        if (!await TryUsePointerInputAsync(ct)) return;

        int unitX = BinaryPrimitives.ReadUInt16LittleEndian(packet.Span[1..3]);
        int unitY = BinaryPrimitives.ReadUInt16LittleEndian(packet.Span[3..5]);
        var monitors = m_Capturer.GetMonitors();
        var monitor = monitors.FirstOrDefault(x => x.Index == m_Monitor) ?? monitors[0];
        int pixelX = monitor.X + (int)Math.Round(unitX / 65535.0 * Math.Max(0, monitor.Width - 1));
        int pixelY = monitor.Y + (int)Math.Round(unitY / 65535.0 * Math.Max(0, monitor.Height - 1));
        InputInjector.MoveMouseAbsolute(pixelX, pixelY);
        m_PointerInput.NoteRemoteMove(pixelX, pixelY);
    }

    private static bool IsSystemKey(ushort virtualKey) => virtualKey is
        0x11 or // Ctrl
        0x12 or // Alt
        0x2C or // Print Screen
        0x5B or // Left Windows
        0x5C or // Right Windows
        0x5D    // Application/Menu
        || virtualKey is >= 0x70 and <= 0x87; // F1..F24

    private Task SendCursorStateAsync(
        bool visible,
        int x,
        int y,
        int width,
        int height,
        string source,
        int hostBlockMs,
        CancellationToken ct)
    {
        // [type:u8][flags:u8][x:u16][y:u16][hostBlockMs:u16]
        // flags bit 0 = visible, bits 1..2 = host/self/remote source.
        byte sourceCode = source switch
        {
            "self" => 1,
            "remote" => 2,
            _ => 0
        };
        byte[] packet = new byte[8];
        packet[0] = (byte)StreamBinaryMessageType.CursorState;
        packet[1] = (byte)((visible ? 1 : 0) | (sourceCode << 1));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            visible ? ToUnitUInt16(x, width) : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(4, 2),
            visible ? ToUnitUInt16(y, height) : (ushort)0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(6, 2),
            (ushort)Math.Clamp(hostBlockMs, 0, ushort.MaxValue));
        return SendControlRawAsync(packet, WebSocketMessageType.Binary, ct);
    }

    private static ushort ToUnitUInt16(int value, int extent) =>
        (ushort)Math.Clamp(
            (int)Math.Round(Math.Clamp(value, 0, Math.Max(0, extent - 1)) * 65535.0 / Math.Max(1, extent - 1)),
            0,
            ushort.MaxValue);

    private void TrackMouseButton(string button, bool down)
    {
        lock (m_HeldInputLock)
        {
            if (down) m_HeldMouseButtons.Add(button);
            else m_HeldMouseButtons.Remove(button);
        }
    }

    private void TrackKey(ushort virtualKey, bool down)
    {
        lock (m_HeldInputLock)
        {
            if (down) m_HeldKeys.Add(virtualKey);
            else m_HeldKeys.Remove(virtualKey);
        }
    }

    private void ReleaseHeldInputs()
    {
        string[] buttons;
        ushort[] keys;
        lock (m_HeldInputLock)
        {
            buttons = m_HeldMouseButtons.ToArray();
            keys = m_HeldKeys.ToArray();
            m_HeldMouseButtons.Clear();
            m_HeldKeys.Clear();
        }

        foreach (string button in buttons)
        {
            try { InputInjector.MouseButton(button, false); } catch { }
        }
        foreach (ushort virtualKey in keys)
        {
            try { InputInjector.Key(virtualKey, false); } catch { }
        }
    }

    private void RecordCapture(long startedAt) =>
        RecordDuration(startedAt, ref m_LastCaptureTicks, ref m_PeakCaptureTicks);

    private void RecordCaptureTiming(DesktopCaptureTiming timing)
    {
        RecordTicks(timing.AcquireTicks, ref m_LastCaptureAcquireTicks, ref m_PeakCaptureAcquireTicks);
        RecordTicks(timing.CopyTicks, ref m_LastCaptureCopyTicks, ref m_PeakCaptureCopyTicks);
        RecordTicks(timing.MapTicks, ref m_LastCaptureMapTicks, ref m_PeakCaptureMapTicks);
        RecordTicks(timing.CpuCopyTicks, ref m_LastCaptureCpuCopyTicks, ref m_PeakCaptureCpuCopyTicks);
    }

    private void RecordEncode(long startedAt) =>
        RecordDuration(startedAt, ref m_LastEncodeTicks, ref m_PeakEncodeTicks);

    private static void RecordDuration(long startedAt, ref long last, ref long peak)
    {
        long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - startedAt);
        RecordTicks(elapsed, ref last, ref peak);
    }

    private static void RecordTicks(long elapsed, ref long last, ref long peak)
    {
        Interlocked.Exchange(ref last, elapsed);
        UpdatePeak(ref peak, elapsed);
    }

    private static void UpdatePeak(ref long location, long value)
    {
        long current = Volatile.Read(ref location);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static void UpdatePeak(ref int location, int value)
    {
        int current = Volatile.Read(ref location);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private void RecordVideoQueue(int depth)
    {
        Volatile.Write(ref m_VideoQueueDepth, depth);
        UpdatePeak(ref m_PeakVideoQueueDepth, depth);
    }

    private void RecordVideoFrame(int bytes, bool isKeyframe = false)
    {
        Volatile.Write(ref m_LastVideoFrameBytes, bytes);
        UpdatePeak(ref m_PeakVideoFrameBytes, bytes);
        Interlocked.Add(ref m_VideoBytesSinceSnapshot, bytes);
        Interlocked.Increment(ref m_VideoFramesSinceSnapshot);
        if (isKeyframe) Interlocked.Exchange(ref m_KeyframeSeen, 1);
    }

    private int TargetVideoBitrateBitsPerSecond()
    {
        int baseKbps = Math.Clamp(m_Config.VideoBitrateKbps, 500, 100_000);
        int percent = Math.Clamp(m_VideoBitratePercent, 25, 100);
        return (int)Math.Max(500_000L, baseKbps * 1000L * percent / 100);
    }

    private async Task HostSchedulerProbeAsync(CancellationToken ct)
    {
        long expectedTicks = Stopwatch.Frequency / 10;
        while (!ct.IsCancellationRequested)
        {
            long startedAt = Stopwatch.GetTimestamp();
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
            long delayTicks = Math.Max(0, Stopwatch.GetTimestamp() - startedAt - expectedTicks);
            RecordTicks(delayTicks, ref m_LastHostTimerTicks, ref m_PeakHostTimerTicks);
        }
    }

    private void InitializeCpuSampler()
    {
        m_LastCpuSampleAt = Stopwatch.GetTimestamp();
        using Process process = Process.GetCurrentProcess();
        m_LastProcessCpuTicks = process.TotalProcessorTime.Ticks;
        if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            m_LastSystemIdleTicks = FileTimeTicks(idle);
            m_LastSystemKernelTicks = FileTimeTicks(kernel);
            m_LastSystemUserTicks = FileTimeTicks(user);
        }
    }

    private (double Process, double System) SampleCpu()
    {
        long sampledAt = Stopwatch.GetTimestamp();
        using Process process = Process.GetCurrentProcess();
        long processTicks = process.TotalProcessorTime.Ticks;
        double wallSeconds = Math.Max(
            0.001,
            (sampledAt - m_LastCpuSampleAt) / (double)Stopwatch.Frequency);
        double processPercent = Math.Clamp(
            (processTicks - m_LastProcessCpuTicks) * 100.0
                / TimeSpan.TicksPerSecond
                / wallSeconds
                / Math.Max(1, Environment.ProcessorCount),
            0,
            100);
        m_LastCpuSampleAt = sampledAt;
        m_LastProcessCpuTicks = processTicks;

        double systemPercent = 0;
        if (GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            ulong idleTicks = FileTimeTicks(idle);
            ulong kernelTicks = FileTimeTicks(kernel);
            ulong userTicks = FileTimeTicks(user);
            ulong idleDelta = idleTicks - m_LastSystemIdleTicks;
            ulong totalDelta = kernelTicks - m_LastSystemKernelTicks + userTicks - m_LastSystemUserTicks;
            if (totalDelta > 0)
                systemPercent = Math.Clamp((1.0 - idleDelta / (double)totalDelta) * 100.0, 0, 100);
            m_LastSystemIdleTicks = idleTicks;
            m_LastSystemKernelTicks = kernelTicks;
            m_LastSystemUserTicks = userTicks;
        }

        return (Math.Round(processPercent, 1), Math.Round(systemPercent, 1));
    }

    private StreamPerformanceSnapshot TakePerformanceSnapshot()
    {
        int queue = Volatile.Read(ref m_VideoQueueDepth);
        int queuePeak = Math.Max(queue, Interlocked.Exchange(ref m_PeakVideoQueueDepth, 0));
        int frameBytes = Volatile.Read(ref m_LastVideoFrameBytes);
        int framePeakBytes = Math.Max(frameBytes, Interlocked.Exchange(ref m_PeakVideoFrameBytes, 0));
        long sampledAt = Stopwatch.GetTimestamp();
        double sampleSeconds = Math.Max(
            0.001,
            (sampledAt - Interlocked.Exchange(ref m_LastPerformanceSnapshotAt, sampledAt))
                / (double)Stopwatch.Frequency);
        double videoKbps = Math.Round(
            Interlocked.Exchange(ref m_VideoBytesSinceSnapshot, 0) * 8.0 / 1000.0 / sampleSeconds,
            1);
        int videoFrames = Interlocked.Exchange(ref m_VideoFramesSinceSnapshot, 0);
        var cpu = SampleCpu();
        return new StreamPerformanceSnapshot(
            TicksToMilliseconds(Volatile.Read(ref m_LastCaptureTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakCaptureTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastCaptureAcquireTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakCaptureAcquireTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastCaptureCopyTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakCaptureCopyTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastCaptureMapTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakCaptureMapTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastCaptureCpuCopyTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakCaptureCpuCopyTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastEncodeTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakEncodeTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastControlSendWaitTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakControlSendWaitTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastControlSendTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakControlSendTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastVideoSendWaitTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakVideoSendWaitTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastVideoSendTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakVideoSendTicks, 0)),
            TicksToMilliseconds(Volatile.Read(ref m_LastHostTimerTicks)),
            TicksToMilliseconds(Interlocked.Exchange(ref m_PeakHostTimerTicks, 0)),
            queue,
            queuePeak,
            frameBytes,
            framePeakBytes,
            videoKbps,
            videoFrames,
            TargetVideoBitrateBitsPerSecond() / 1000,
            m_AppliedVideoBitrateKbps,
            Interlocked.Exchange(ref m_PacedVideoFrameSeen, 0) != 0,
            Interlocked.Exchange(ref m_KeyframeSeen, 0) != 0,
            cpu.Process,
            cpu.System,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.ThreadCount,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    private static double TicksToMilliseconds(long ticks) =>
        Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 2);

    private sealed record StreamPerformanceSnapshot(
        double CaptureMs,
        double CapturePeakMs,
        double CaptureAcquireMs,
        double CaptureAcquirePeakMs,
        double CaptureCopyMs,
        double CaptureCopyPeakMs,
        double CaptureMapMs,
        double CaptureMapPeakMs,
        double CaptureCpuCopyMs,
        double CaptureCpuCopyPeakMs,
        double EncodeMs,
        double EncodePeakMs,
        double ControlSendWaitMs,
        double ControlSendWaitPeakMs,
        double ControlSendMs,
        double ControlSendPeakMs,
        double SendWaitMs,
        double SendWaitPeakMs,
        double SendMs,
        double SendPeakMs,
        double HostTimerMs,
        double HostTimerPeakMs,
        int HostQueue,
        int HostQueuePeak,
        int FrameBytes,
        int FramePeakBytes,
        double VideoKbps,
        int VideoFrames,
        int TargetVideoBitrateKbps,
        int AppliedVideoBitrateKbps,
        bool PacedFrameSeen,
        bool KeyframeSeen,
        double ProcessCpuPercent,
        double SystemCpuPercent,
        long ThreadPoolQueue,
        int ThreadPoolThreads,
        int Gc0,
        int Gc1,
        int Gc2);

    private static ulong FileTimeTicks(FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    // ---------- send helpers (video and control serialize independently) ----------
    private async Task SendRawAsync(
        ReadOnlyMemory<byte> data,
        WebSocketMessageType type,
        CancellationToken ct,
        bool trackVideo = false,
        bool paceLargeVideo = false)
    {
        long waitStartedAt = trackVideo ? Stopwatch.GetTimestamp() : 0;
        await m_SendLock.WaitAsync(ct);
        if (trackVideo)
            RecordDuration(waitStartedAt, ref m_LastVideoSendWaitTicks, ref m_PeakVideoSendWaitTicks);

        long sendStartedAt = 0;
        try
        {
            if (m_Socket.State == WebSocketState.Open)
            {
                if (trackVideo) sendStartedAt = Stopwatch.GetTimestamp();
                if (paceLargeVideo && data.Length >= LargeVideoFrameThresholdBytes)
                {
                    Interlocked.Exchange(ref m_PacedVideoFrameSeen, 1);
                    Volatile.Write(ref m_LargeVideoFrameInFlight, 1);
                    try
                    {
                        int offset = 0;
                        while (offset < data.Length)
                        {
                            int length = Math.Min(VideoFragmentBytes, data.Length - offset);
                            bool endOfMessage = offset + length >= data.Length;
                            await m_Socket.SendAsync(data.Slice(offset, length), type, endOfMessage, ct);
                            offset += length;
                            if (!endOfMessage) PaceVideoFragment();
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref m_LargeVideoFrameInFlight, 0);
                    }
                }
                else
                {
                    await m_Socket.SendAsync(data, type, true, ct);
                }
            }
        }
        finally
        {
            if (trackVideo && sendStartedAt != 0)
                RecordDuration(sendStartedAt, ref m_LastVideoSendTicks, ref m_PeakVideoSendTicks);
            m_SendLock.Release();
        }
    }

    private static void PaceVideoFragment()
    {
        // Task.Delay(1) commonly becomes a 10-15 ms wait on Windows due to timer
        // coalescing. A one-millisecond spin is rare (large frames only), precise,
        // allocation-free, and keeps total keyframe pacing below one frame interval.
        long until = Stopwatch.GetTimestamp() + Math.Max(1, Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(64);
    }

    private Task SendTextAsync(object payload, CancellationToken ct = default) =>
        SendControlRawAsync(
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
            WebSocketMessageType.Text,
            ct);

    private Task SendVideoTextAsync(object payload, CancellationToken ct = default) =>
        SendRawAsync(
            JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
            WebSocketMessageType.Text,
            ct);

    private async Task SendControlRawAsync(
        ReadOnlyMemory<byte> data,
        WebSocketMessageType type,
        CancellationToken ct)
    {
        WebSocket? control;
        lock (m_ControlGate) control = m_ControlSocket;
        if (control is { State: WebSocketState.Open })
        {
            try
            {
                await SendSocketAsync(control, m_ControlSendLock, data, type, ct);
                return;
            }
            catch when (!ct.IsCancellationRequested)
            {
                try { control.Abort(); } catch { }
            }
        }

        await SendRawAsync(data, type, ct);
    }

    private async Task SendSocketAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        ReadOnlyMemory<byte> data,
        WebSocketMessageType type,
        CancellationToken ct)
    {
        long waitStartedAt = Stopwatch.GetTimestamp();
        await sendLock.WaitAsync(ct);
        RecordDuration(waitStartedAt, ref m_LastControlSendWaitTicks, ref m_PeakControlSendWaitTicks);
        long sendStartedAt = 0;
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                sendStartedAt = Stopwatch.GetTimestamp();
                await socket.SendAsync(data, type, true, ct);
            }
        }
        finally
        {
            if (sendStartedAt != 0)
                RecordDuration(sendStartedAt, ref m_LastControlSendTicks, ref m_PeakControlSendTicks);
            sendLock.Release();
        }
    }

    private Task SendStatusAsync(string state) => SendTextAsync(new { t = "status", state });

    private Task SendVideoStatusAsync(string state) =>
        SendVideoTextAsync(new { t = "status", state });

    private Task SendMonitorListAsync()
    {
        var monitors = m_Capturer.GetMonitors()
            .Select(x => new { index = x.Index, w = x.Width, h = x.Height, primary = x.IsPrimary })
            .ToArray();
        return SendTextAsync(new { t = "monitors", list = monitors, active = m_Monitor });
    }

    private async Task CloseAsync()
    {
        WebSocket? control;
        lock (m_ControlGate) control = m_ControlSocket;
        if (control is not null) await CloseSocketAsync(control, "bye");
        await CloseSocketAsync(m_Socket, "bye");
    }

    private static async Task CloseSocketAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { }
    }

    public void Dispose()
    {
        ReleaseHeldInputs();
        m_Cancellation.Dispose();
        m_Capturer.Dispose();
        m_SendLock.Dispose();
        m_ControlSendLock.Dispose();
    }
}
