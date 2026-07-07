// EncodeProbe — proves the GPU-capture + hardware-H.264 pipeline and measures the
// bandwidth win over the JPEG stream. Mirrors CaptureProbe: run it standalone, no tray
// app or browser needed.
//
//   dotnet run --project tools/EncodeProbe -- [seconds] [fps] [mbps] [outputIndex]
//   e.g.  dotnet run --project tools/EncodeProbe -- 8 30 8 0
//
// It captures the desktop for a few seconds, hardware-encodes H.264 to capture-test.mp4,
// then prints frames / bitrate / encode latency and compares against a JPEG frame at the
// app's current quality. INTERACT with the screen while it runs (scroll, drag a window)
// so the numbers reflect real motion rather than a frozen desktop.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Capture.Gpu;

int seconds = ArgInt(0, 8);
int fps = ArgInt(1, 30);
int mbps = ArgInt(2, 8);
int outputIndex = ArgInt(3, 0);
const int JpegQuality = 75; // matches AppConfig.JpegQuality default

string outPath = Path.Combine(Environment.CurrentDirectory, "capture-test.mp4");

Console.WriteLine($"EncodeProbe: output #{outputIndex}, {seconds}s @ {fps}fps, target {mbps} Mbps");
Console.WriteLine("Interact with the screen (scroll / drag a window) while this runs.\n");

using var source = new DesktopDuplicationSource(outputIndex);
Console.WriteLine($"Capturing {source.Width}x{source.Height} → {outPath}");

using var encoder = new MediaFoundationH264Encoder(outPath, source.Width, source.Height, fps, mbps * 1_000_000);

// Warm up: block a little longer for the first frame so we always have something to encode.
byte[]? last = source.TryAcquire(1000)?.Bgra;

var wall = Stopwatch.StartNew();
var encodeTimer = new Stopwatch();
int frames = 0, changed = 0;
double totalEncodeMs = 0, maxEncodeMs = 0;
long jpegTotal = 0; int jpegSamples = 0;
int frameIntervalMs = Math.Max(1, 1000 / fps);

while (wall.Elapsed.TotalSeconds < seconds)
{
    DesktopFrame? frame = source.TryAcquire(frameIntervalMs);

    byte[] bgra;
    bool isChanged;
    if (frame is not null) { bgra = frame.Bgra; last = frame.Bgra; isChanged = true; changed++; }
    else if (last is not null) { bgra = last; isChanged = false; } // static → reuse last, keeps fps steady
    else continue;                                                  // nothing captured yet

    encodeTimer.Restart();
    encoder.Encode(bgra, wall.Elapsed, forceKeyframe: frames == 0);
    encodeTimer.Stop();

    double ms = encodeTimer.Elapsed.TotalMilliseconds;
    totalEncodeMs += ms;
    if (ms > maxEncodeMs) maxEncodeMs = ms;
    frames++;

    // JPEG baseline: size a handful of CHANGED frames at the app's quality.
    if (isChanged && jpegSamples < 12)
    {
        jpegTotal += JpegSize(bgra, source.Width, source.Height, JpegQuality);
        jpegSamples++;
    }

    // Pace to the target frame rate.
    long targetMs = (long)(frames * 1000.0 / fps);
    long behind = targetMs - (long)wall.Elapsed.TotalMilliseconds;
    if (behind > 0) Thread.Sleep((int)behind);
}

encoder.Finish();

double duration = wall.Elapsed.TotalSeconds;
long fileBytes = new FileInfo(outPath).Length;
double h264Mbps = fileBytes * 8.0 / duration / 1_000_000.0;
double avgJpegKb = jpegSamples > 0 ? jpegTotal / (double)jpegSamples / 1024.0 : 0;
double jpegStreamMbps = avgJpegKb * 1024 * fps * 8 / 1_000_000.0; // if every frame were a full JPEG

Console.WriteLine();
Console.WriteLine("──────────── results ────────────");
Console.WriteLine($"Frames encoded : {frames}  ({changed} changed, {frames - changed} static-reuse)");
Console.WriteLine($"Duration       : {duration:F1} s");
Console.WriteLine($"Output size    : {fileBytes / 1024.0:F0} KB");
Console.WriteLine($"H.264 bitrate  : {h264Mbps:F2} Mbps average");
Console.WriteLine($"Encode latency : {totalEncodeMs / Math.Max(frames, 1):F2} ms avg, {maxEncodeMs:F2} ms max");
if (jpegSamples > 0)
{
    Console.WriteLine($"JPEG Q{JpegQuality} frame : {avgJpegKb:F0} KB avg");
    Console.WriteLine($"  → a full-frame JPEG stream at {fps}fps ≈ {jpegStreamMbps:F0} Mbps");
    Console.WriteLine($"  → H.264 here is ~{jpegStreamMbps / Math.Max(h264Mbps, 0.01):F0}× smaller");
}
Console.WriteLine($"\nPlay it:  {outPath}");
return 0;

static int ArgInt(int index, int fallback) =>
    index < Environment.GetCommandLineArgs().Length - 1
    && int.TryParse(Environment.GetCommandLineArgs()[index + 1], out int v) ? v : fallback;

// Size one BGRA frame as JPEG at the given quality — the apples-to-apples comparison
// against the current stream (each JPEG is a fresh intra-frame with no temporal coding).
static long JpegSize(byte[] bgra, int width, int height, long quality)
{
    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
    var rect = new Rectangle(0, 0, width, height);
    var bits = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
    try { Marshal.Copy(bgra, 0, bits.Scan0, Math.Min(bgra.Length, bits.Stride * height)); }
    finally { bitmap.UnlockBits(bits); }

    var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    using var stream = new MemoryStream();
    using var parameters = new EncoderParameters(1);
    parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
    bitmap.Save(stream, jpegEncoder, parameters);
    return stream.Length;
}
