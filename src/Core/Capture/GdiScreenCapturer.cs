using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Core.Capture;

/// <summary>
/// GDI-based capture for the MVP: reuses one bitmap to avoid per-frame allocation
/// and uses a fast pixel hash for change detection. CPU-heavy at high resolutions
/// and cannot grab some hardware-accelerated/protected surfaces — that's what the
/// future WGC implementation fixes. One instance per session (GDI bitmaps are not
/// safe to share across threads).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapturer : IScreenCapturer
{
    private readonly ImageCodecInfo m_JpegEncoder =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // Reused JPEG-encode scratch so per-tile encoding doesn't allocate a MemoryStream
    // and a fresh EncoderParameters on every tile of every frame. One capturer per
    // session, driven by one send-loop thread, so no locking is needed.
    private readonly MemoryStream m_EncodeBuffer = new(256 * 1024);
    private EncoderParameters? m_EncoderParams;
    private long m_EncoderQuality = -1;

    // Reused capture target — kept across frames so we don't reallocate every grab.
    private Bitmap? m_FrameBitmap;
    private int m_FrameMonitor = -1, m_FrameWidth, m_FrameHeight;

    // Per-tile change-detection hashes (one capturer per session, so this is safe).
    private ulong[]? m_TileHashes;
    private int m_GridMonitor = -1, m_GridWidth, m_GridHeight, m_GridTileSize;

    // Short-lived monitor-list cache so we don't EnumDisplayMonitors on every frame.
    private IReadOnlyList<MonitorInfo>? m_MonitorCache;
    private DateTime m_MonitorCacheAt;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        // Short cache so we don't EnumDisplayMonitors on every frame; new displays
        // are picked up within ~2 seconds.
        if (m_MonitorCache is not null && (DateTime.UtcNow - m_MonitorCacheAt).TotalSeconds < 2)
            return m_MonitorCache;

        var monitors = new List<MonitorInfo>();
        int index = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitorHandle, IntPtr _, ref RECT bounds, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitorHandle, ref info);
            bool isPrimary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
            monitors.Add(new MonitorInfo(
                index++, bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top, isPrimary));
            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
            monitors.Add(new MonitorInfo(0, 0, 0, 1920, 1080, true)); // defensive fallback

        m_MonitorCache = monitors;
        m_MonitorCacheAt = DateTime.UtcNow;
        return monitors;
    }

    public DeltaFrame CaptureDelta(int monitorIndex, long quality, bool keyframe, int tileSize)
    {
        var monitors = GetMonitors();
        var monitor = monitors.FirstOrDefault(x => x.Index == monitorIndex) ?? monitors[0];

        EnsureFrameBitmap(monitor.Index, monitor.Width, monitor.Height);
        using (var graphics = Graphics.FromImage(m_FrameBitmap!))
            graphics.CopyFromScreen(monitor.X, monitor.Y, 0, 0, new Size(monitor.Width, monitor.Height));
        // The cursor is sent as a separate WebSocket overlay. Keeping it out of the
        // bitmap means cursor movement does not dirty JPEG tiles or video frames.

        int cols = (monitor.Width + tileSize - 1) / tileSize;
        int rows = (monitor.Height + tileSize - 1) / tileSize;

        // (Re)initialise the per-tile hash grid when the monitor/size/tile changes;
        // a grid reset implies a full repaint.
        if (m_TileHashes is null || m_GridMonitor != monitor.Index || m_GridWidth != monitor.Width
            || m_GridHeight != monitor.Height || m_GridTileSize != tileSize)
        {
            m_TileHashes = new ulong[cols * rows];
            m_GridMonitor = monitor.Index;
            m_GridWidth = monitor.Width;
            m_GridHeight = monitor.Height;
            m_GridTileSize = tileSize;
            keyframe = true;
        }

        var changedTiles = new List<(int x, int y, int w, int h)>();
        var fullArea = new Rectangle(0, 0, monitor.Width, monitor.Height);
        var bits = m_FrameBitmap!.LockBits(fullArea, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)bits.Scan0;
                int stride = bits.Stride;
                for (int ry = 0; ry < rows; ry++)
                {
                    int ty = ry * tileSize;
                    int th = Math.Min(tileSize, monitor.Height - ty);
                    for (int cx = 0; cx < cols; cx++)
                    {
                        int tx = cx * tileSize;
                        int tw = Math.Min(tileSize, monitor.Width - tx);
                        ulong hash = HashRegion(basePtr, stride, tx, ty, tw, th);
                        int idx = ry * cols + cx;
                        if (keyframe || m_TileHashes[idx] != hash)
                        {
                            m_TileHashes[idx] = hash;
                            changedTiles.Add((tx, ty, tw, th));
                        }
                    }
                }
            }
        }
        finally
        {
            m_FrameBitmap.UnlockBits(bits);
        }

        var tiles = new List<Tile>(changedTiles.Count);
        foreach (var region in changedTiles)
        {
            using var tileBitmap = m_FrameBitmap.Clone(
                new Rectangle(region.x, region.y, region.w, region.h), PixelFormat.Format24bppRgb);
            tiles.Add(new Tile(region.x, region.y, region.w, region.h, EncodeJpeg(tileBitmap, quality)));
        }

        return new DeltaFrame(monitor.Width, monitor.Height, tiles);
    }

    public CapturedFrame CaptureJpeg(int monitorIndex, long quality)
    {
        var monitors = GetMonitors();
        var monitor = monitors.FirstOrDefault(x => x.Index == monitorIndex) ?? monitors[0];

        using var bitmap = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(monitor.X, monitor.Y, 0, 0, new Size(monitor.Width, monitor.Height));

        return new CapturedFrame(EncodeJpeg(bitmap, quality), monitor.Width, monitor.Height);
    }

    /// <summary>
    /// FNV-1a hash over a tile, sampling every 4th byte. Cheap enough to run on every
    /// tile each frame; collisions only ever cost a missed repaint, never corruption.
    /// </summary>
    private static unsafe ulong HashRegion(byte* basePtr, int stride, int x, int y, int w, int h)
    {
        ulong hash = 1469598103934665603UL; // FNV offset basis
        int xByte = x * 3, wByte = w * 3;
        for (int row = 0; row < h; row++)
        {
            byte* p = basePtr + (y + row) * stride + xByte;
            for (int b = 0; b < wByte; b += 4) // sample every 4th byte for speed
            {
                hash ^= p[b];
                hash *= 1099511628211UL; // FNV prime
            }
        }
        return hash;
    }

    private byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        long clampedQuality = Math.Clamp(quality, 1, 100);

        // Rebuild the encoder params only when quality actually changes (a change also
        // forces a keyframe), so the steady state allocates nothing here.
        if (m_EncoderParams is null || m_EncoderQuality != clampedQuality)
        {
            m_EncoderParams?.Dispose();
            m_EncoderParams = new EncoderParameters(1);
            m_EncoderParams.Param[0] = new EncoderParameter(Encoder.Quality, clampedQuality);
            m_EncoderQuality = clampedQuality;
        }

        m_EncodeBuffer.SetLength(0);
        bitmap.Save(m_EncodeBuffer, m_JpegEncoder, m_EncoderParams);
        // ToArray is the one remaining allocation: each tile's bytes must outlive the
        // shared buffer (the next tile overwrites it before the frame is serialized).
        return m_EncodeBuffer.ToArray();
    }

    private void EnsureFrameBitmap(int monitor, int width, int height)
    {
        if (m_FrameBitmap is not null && m_FrameMonitor == monitor && m_FrameWidth == width && m_FrameHeight == height)
            return;

        m_FrameBitmap?.Dispose();
        m_FrameBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        m_FrameMonitor = monitor;
        m_FrameWidth = width;
        m_FrameHeight = height;
    }

    public void Dispose()
    {
        m_FrameBitmap?.Dispose();
        m_EncodeBuffer.Dispose();
        m_EncoderParams?.Dispose();
    }

    // ---------- Win32 interop ----------
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
}
