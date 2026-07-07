using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
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
/// Concurrency note: a WebSocket permits only ONE outstanding send at a time, and we
/// send from both loops (frames vs pong/status). All sends are funneled through a
/// single SemaphoreSlim — without it you get intermittent InvalidOperationException
/// under load.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StreamSession : IDisposable
{
    // Control state: written by the receive loop, read by the send loop. volatile
    // int/bool reads & writes are atomic and visible across threads — enough here.
    private volatile int m_Quality;
    private volatile int m_Fps;
    private volatile int m_Monitor = 0;
    private volatile bool m_ForceKeyframe = true; // resend a full frame after a control change

    private readonly WebSocket m_Socket;
    private readonly AppConfig m_Config;
    private readonly IScreenCapturer m_Capturer;
    private readonly bool m_IsLanClient;
    private readonly SessionPermissions m_Permissions;
    private readonly Func<bool> m_IsAccessStillValid;
    private readonly CancellationTokenSource m_Cancellation;
    private readonly SemaphoreSlim m_SendLock = new(1, 1); // serialize ALL sends

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
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
        Func<bool> isAccessStillValid,
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
        m_Permissions = loginSession.Permissions;
        m_IsAccessStillValid = isAccessStillValid;
        m_Capturer = new GdiScreenCapturer(); // per-session: avoids cross-thread bitmap races
        m_Cancellation = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
    }

    /// <summary>Drives the session until either loop ends, then tears down cleanly.</summary>
    public async Task RunAsync()
    {
        AuditLogger.Log("STREAM_CONNECT", ClientIp, $"id={Id}");
        await SendMonitorListAsync();
        await SendStatusAsync("connected");

        var receive = ReceiveLoopAsync(m_Cancellation.Token);
        var send = m_Config.UseHardwareVideo
            ? (m_Config.VideoLowLatency
                ? WebCodecsSendLoopAsync(m_Cancellation.Token)   // per-frame H.264 → WebCodecs
                : VideoSendLoopAsync(m_Cancellation.Token))      // fragmented MP4 → MSE
            : SendLoopAsync(m_Cancellation.Token);               // JPEG tiles

        await Task.WhenAny(receive, send); // whichever ends first…
        m_Cancellation.Cancel();           // …tears down the other
        try { await Task.WhenAll(receive, send); } catch { /* expected on cancel */ }

        await CloseAsync();
        AuditLogger.Log("STREAM_DISCONNECT", ClientIp, $"id={Id}");
    }

    /// <summary>Used by the registry / tray "disconnect" action.</summary>
    public void Cancel() => m_Cancellation.Cancel();

    // ---------- hardware-video send loop (GPU capture + hardware H.264, fragmented MP4) ----------
    // Captures on the GPU and hardware-encodes to a fragmented-MP4 temp file, then tails
    // that file and pushes new bytes to the client (which plays them via MediaSource).
    // Binary video messages carry a 1-byte type prefix (3) so the client routes them apart
    // from the JPEG-tile frames (type 1). Monitor/fps/bitrate are taken at connect time;
    // changing them mid-session applies on reconnect (the tile path stays fully live).
    private async Task VideoSendLoopAsync(CancellationToken ct)
    {
        await SendStatusAsync("video"); // tell the client to switch to the MSE renderer

        string tempPath = Path.Combine(Path.GetTempPath(), $"rdl_{Id}.mp4");
        DesktopDuplicationSource? source = null;
        H264StreamEncoder? encoder = null;
        Task? captureTask = null;
        try
        {
            int fps = Math.Clamp(m_Fps, 1, 60);
            int bitrate = Math.Clamp(m_Config.VideoBitrateKbps, 500, 100_000) * 1000;
            source = new DesktopDuplicationSource(m_Monitor);
            encoder = new H264StreamEncoder(tempPath, source.Width, source.Height, fps, bitrate);

            var capSource = source;
            var capEncoder = encoder;
            captureTask = Task.Run(() => CaptureLoop(capSource, capEncoder, fps, ct), ct);

            // Tail the fragmented-MP4 file: FlagsAllowWriteSharing on the MF side + this
            // shared read handle let us read what MF is still writing.
            using var reader = new FileStream(
                tempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[64 * 1024];
            var framed = new byte[buffer.Length + 1];
            framed[0] = 3; // video-chunk marker
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
                        try { await SendRawAsync(framed.AsMemory(0, read + 1), WebSocketMessageType.Binary, ct); }
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
            int interval = 1000 / Math.Clamp(fps, 1, 60);

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                DesktopFrame? frame;
                try { frame = source.TryAcquire(interval); }
                catch (Exception ex) { AuditLogger.Log("CAPTURE_ERROR", ClientIp, ex.Message); break; }

                byte[] bgra = frame?.Bgra ?? last ?? Array.Empty<byte>();
                if (bgra.Length == 0) continue;
                last = bgra;

                // Force an IDR ~twice a second: the fragmented-MP4 sink starts a new
                // streamable fragment at each key frame, so shorter intervals mean smaller
                // fragments and lower end-to-end latency (matters most for video content).
                int keyEvery = Math.Max(1, fps / 2);
                bool key = submitted == 0 || m_ForceKeyframe || submitted % keyEvery == 0;
                if (m_ForceKeyframe) m_ForceKeyframe = false;

                try { encoder.Encode(bgra, stopwatch.Elapsed, key); }
                catch (Exception ex) { AuditLogger.Log("VIDEO_ENCODE_ERROR", ClientIp, ex.Message); break; }
                submitted++;

                long target = (long)(submitted * 1000.0 / fps);
                int behind = (int)(target - stopwatch.ElapsedMilliseconds);
                if (behind > 0) { try { Thread.Sleep(behind); } catch { } }
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
            int bitrate = Math.Clamp(m_Config.VideoBitrateKbps, 500, 100_000) * 1000;
            source = new DesktopDuplicationSource(m_Monitor);
            encoder = new H264LowLatencyEncoder(source.Width, source.Height, fps, bitrate);
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

        await SendStatusAsync("video-h264");

        Task? captureTask = null;
        var queue = new BlockingCollection<EncodedVideoFrame>(120);
        try
        {
            encoder.FrameEncoded += f =>
            {
                if (!queue.IsAddingCompleted && !queue.TryAdd(f))
                    m_ForceKeyframe = true; // had to drop a frame → resync with a fresh IDR
            };

            var capSource = source;
            var capEncoder = encoder;
            captureTask = Task.Run(() => CaptureLoopLowLatency(capSource, capEncoder, fps, ct), ct);

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                if (!m_IsAccessStillValid()) { await SendStatusAsync("access-revoked"); break; }
                if (!m_IsLanClient && !m_Config.RemoteAccessEnabled) { await SendStatusAsync("disabled"); break; }

                EncodedVideoFrame frame;
                try { if (!queue.TryTake(out frame, 100, ct)) continue; }
                catch { break; }

                var msg = new byte[2 + frame.AnnexB.Length];
                msg[0] = 4;
                msg[1] = (byte)(frame.IsKeyframe ? 1 : 0);
                Buffer.BlockCopy(frame.AnnexB, 0, msg, 2, frame.AnnexB.Length);
                try { await SendRawAsync(msg, WebSocketMessageType.Binary, ct); }
                catch { break; }
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
            int interval = 1000 / Math.Clamp(fps, 1, 60);

            while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
            {
                DesktopFrame? frame;
                try { frame = source.TryAcquire(interval); }
                catch (Exception ex) { AuditLogger.Log("CAPTURE_ERROR", ClientIp, ex.Message); break; }

                byte[] bgra = frame?.Bgra ?? last ?? Array.Empty<byte>();
                if (bgra.Length == 0) continue;
                last = bgra;

                // Per-frame delivery needs a key frame only at start / on a control change;
                // the encoder's own GOP handles periodic IDRs, so full-frame resends are rare.
                bool key = submitted == 0 || m_ForceKeyframe;
                if (m_ForceKeyframe) m_ForceKeyframe = false;

                try { encoder.Encode(bgra, stopwatch.Elapsed, key); }
                catch (Exception ex) { AuditLogger.Log("VIDEO_ENCODE_ERROR", ClientIp, ex.Message); break; }
                submitted++;

                long target = (long)(submitted * 1000.0 / fps);
                int behind = (int)(target - stopwatch.ElapsedMilliseconds);
                if (behind > 0) { try { Thread.Sleep(behind); } catch { } }
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
                try { await SendRawAsync(SerializeFrame(frame), WebSocketMessageType.Binary, ct); }
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
        writer.Write((byte)1);
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

    // ---------- receive loop ----------
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];

        while (!ct.IsCancellationRequested && m_Socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try { result = await m_Socket.ReceiveAsync(buffer, ct); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            Dictionary<string, JsonElement>? message;
            try { message = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(buffer.AsSpan(0, result.Count)); }
            catch { continue; }
            if (message is null || !message.TryGetValue("t", out var typeElement)) continue;

            switch (typeElement.GetString())
            {
                // ----- input -----
                case "move":
                {
                    if (!m_Permissions.CanControl) break;
                    var monitor = m_Capturer.GetMonitors().FirstOrDefault(x => x.Index == m_Monitor)
                                  ?? m_Capturer.GetMonitors()[0];
                    int pixelX = monitor.X + (int)(message["x"].GetDouble() * monitor.Width);
                    int pixelY = monitor.Y + (int)(message["y"].GetDouble() * monitor.Height);
                    InputInjector.MoveMouseAbsolute(pixelX, pixelY);
                    break;
                }
                case "btn":
                    if (m_Permissions.CanControl)
                        InputInjector.MouseButton(message["b"].GetString()!, message["d"].GetBoolean());
                    break;
                case "scroll":
                    if (m_Permissions.CanControl) InputInjector.Scroll(message["delta"].GetInt32());
                    break;
                case "key":
                {
                    ushort virtualKey = (ushort)message["vk"].GetInt32();
                    if (m_Permissions.CanControl
                        && (m_Permissions.CanUseSystemKeys || !IsSystemKey(virtualKey)))
                        InputInjector.Key(virtualKey, message["d"].GetBoolean());
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
                case "quality": m_Quality = Math.Clamp(message["v"].GetInt32(), 10, 95); m_ForceKeyframe = true; break;
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
                case "ping": await SendTextAsync(new { t = "pong", ts = message["ts"].GetDouble() }, ct); break;

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

    private static bool IsSystemKey(ushort virtualKey) => virtualKey is
        0x11 or // Ctrl
        0x12 or // Alt
        0x2C or // Print Screen
        0x5B or // Left Windows
        0x5C or // Right Windows
        0x5D    // Application/Menu
        || virtualKey is >= 0x70 and <= 0x87; // F1..F24

    // ---------- send helpers (all sends serialized through m_SendLock) ----------
    private async Task SendRawAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await m_SendLock.WaitAsync(ct);
        try
        {
            if (m_Socket.State == WebSocketState.Open)
                await m_Socket.SendAsync(data, type, true, ct);
        }
        finally
        {
            m_SendLock.Release();
        }
    }

    private Task SendTextAsync(object payload, CancellationToken ct = default) =>
        SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), WebSocketMessageType.Text, ct);

    private Task SendStatusAsync(string state) => SendTextAsync(new { t = "status", state });

    private Task SendMonitorListAsync()
    {
        var monitors = m_Capturer.GetMonitors()
            .Select(x => new { index = x.Index, w = x.Width, h = x.Height, primary = x.IsPrimary })
            .ToArray();
        return SendTextAsync(new { t = "monitors", list = monitors, active = m_Monitor });
    }

    private async Task CloseAsync()
    {
        try
        {
            if (m_Socket.State == WebSocketState.Open)
                await m_Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }

    public void Dispose()
    {
        m_Cancellation.Dispose();
        m_Capturer.Dispose();
        m_SendLock.Dispose();
    }
}
