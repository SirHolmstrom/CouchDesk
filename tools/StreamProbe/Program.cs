// StreamProbe — validates the low-latency per-frame encoder (H264LowLatencyEncoder):
// captures, hardware-encodes one Annex-B access unit per frame, writes them to out.h264,
// and prints the NAL types of the first frame. If out.h264 plays (ffplay/VLC) and the first
// frame carries SPS(7)+PPS(8)+IDR(5), the WebCodecs path is sound.
//
//   dotnet run --project tools/StreamProbe -- [seconds] [fps] [mbps] [outputIndex]

using System.Diagnostics;
using Capture.Gpu;

int seconds = ArgInt(0, 6), fps = ArgInt(1, 30), mbps = ArgInt(2, 8), outputIndex = ArgInt(3, 0);
string outPath = Path.Combine(Environment.CurrentDirectory, "out.h264");

Console.WriteLine($"StreamProbe (low-latency): output #{outputIndex}, {seconds}s @ {fps}fps, {mbps} Mbps");
Console.WriteLine("Interact with the screen while this runs.\n");

using var source = new DesktopDuplicationSource(outputIndex);
Console.WriteLine($"Capturing {source.Width}x{source.Height} -> {outPath}");

using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
object gate = new();
int received = 0, keyframes = 0;
long bytes = 0;
byte[]? firstFrame = null;

using var encoder = new H264LowLatencyEncoder(source.Width, source.Height, fps, mbps * 1_000_000);
encoder.FrameEncoded += f =>
{
    lock (gate)
    {
        fs.Write(f.AnnexB, 0, f.AnnexB.Length);
        received++;
        bytes += f.AnnexB.Length;
        if (f.IsKeyframe) keyframes++;
        firstFrame ??= f.AnnexB;
    }
};

byte[]? last = source.TryAcquire(1000)?.Bgra;
var wall = Stopwatch.StartNew();
int submitted = 0;
while (wall.Elapsed.TotalSeconds < seconds)
{
    DesktopFrame? frame = source.TryAcquire(Math.Max(1, 1000 / fps));
    byte[] bgra = frame?.Bgra ?? last ?? Array.Empty<byte>();
    if (bgra.Length == 0) continue;
    last = bgra;
    encoder.Encode(bgra, wall.Elapsed, forceKeyframe: submitted == 0);
    submitted++;

    long targetMs = (long)(submitted * 1000.0 / fps);
    long behind = targetMs - (long)wall.Elapsed.TotalMilliseconds;
    if (behind > 0) Thread.Sleep((int)behind);
}
Thread.Sleep(600); // let the pump drain trailing outputs
double dur = wall.Elapsed.TotalSeconds;
lock (gate) fs.Flush();

Console.WriteLine();
Console.WriteLine("──────────── results ────────────");
Console.WriteLine($"Frames submitted : {submitted}");
Console.WriteLine($"Frames encoded   : {received}  ({keyframes} keyframes)");
Console.WriteLine($"Annex-B output   : {bytes / 1024.0:F0} KB  ({(dur > 0 ? bytes * 8.0 / dur / 1e6 : 0):F2} Mbps)");
Console.WriteLine($"Avg frame size   : {(received > 0 ? bytes / (double)received : 0):F0} bytes");
if (firstFrame is not null)
    Console.WriteLine($"First-frame NALs : {NalTypes(firstFrame)}   (want 7=SPS, 8=PPS, 5=IDR)");
Console.WriteLine($"Wrote {outPath}");
return received > 0 ? 0 : 5;

static string NalTypes(byte[] d)
{
    var types = new List<int>();
    for (int i = 0; i + 4 < d.Length && types.Count < 12; i++)
        if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1) { types.Add(d[i + 3] & 0x1f); i += 3; }
    return string.Join(",", types);
}

static int ArgInt(int i, int fb) =>
    i < Environment.GetCommandLineArgs().Length - 1
    && int.TryParse(Environment.GetCommandLineArgs()[i + 1], out int v) ? v : fb;
