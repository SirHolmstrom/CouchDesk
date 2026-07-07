# GPU capture + hardware H.264 — prototype

A spike to replace the CPU JPEG-tile stream with **GPU capture (DXGI Desktop Duplication) →
hardware H.264 (Media Foundation) → browser decode**. Goal: cut bandwidth and CPU, and
lower latency.

## Built & validated on this machine (2026-07-07)

Compiled and **run on the actual hardware** (3440×1440 ultrawide, .NET 10 SDK, Vortice 3.5.0):

- **Capture + encode validated:** DXGI Desktop Duplication → hardware H.264 produced a valid
  playable file at **6.2 Mbps** vs a full-frame JPEG stream at **~90 Mbps** — **~14× less
  bandwidth** — at **3.2 ms/frame** encode (hardware, not CPU).
- **Streaming encoder validated:** `H264StreamEncoder` emits a **fragmented MP4** (init
  segment + `moof`/`mdat` fragments over a write-shared file), confirmed streamable.
- **Wired into the tray, behind a default-off flag,** and the app builds + runs.

**Shipped decode path:** fragmented MP4 over the WebSocket → **MediaSource (MSE)** in the
browser, drawn onto the existing `#screen` canvas so all zoom/pan/pointer code is unchanged.
This reuses the proven hardware Sink Writer (the raw-Annex-B/WebCodecs async-MFT path was
scoped and set aside as too fragile to land blind; `webcodecs-decoder.js` remains for that
future latency optimization).

### Turn it on
Set `"UseHardwareVideo": true` (and optionally `"VideoBitrateKbps": 8000`) in
`%LOCALAPPDATA%\RemoteDesktopLAN\config.json`, then reconnect. Default is `false`, so the
dependable JPEG-tile path stays the baseline until you opt in. A tray toggle is the natural
next step.

### New / changed files
`src/Capture.Gpu/` (DesktopDuplicationSource, MediaFoundationH264Encoder, H264StreamEncoder,
VideoTypes), `tools/EncodeProbe` + `tools/StreamProbe`, `web/mse-video.js`, and — in Core —
`AppConfig` (two fields), `Core.csproj` (project ref), `StreamSession` (video send loop),
`web/app.js` + `index.html` (route + include).

---

_Original spike notes below (the isolated-probe workflow that got us here)._

This is deliberately **additive and isolated**. The new `Capture.Gpu` project is *not*
referenced by `Core`, so the shipping app keeps building and running exactly as before. You
validate the GPU pipeline with a standalone probe first, then wire it into `StreamSession`.

> **Heads-up:** the two interop files were written without a Windows compiler in the loop.
> The Media Foundation GUIDs/values are correct per MSDN; a few **Vortice method names** may
> need a small, mechanical fix on first build. See [Fixup checklist](#fixup-checklist).

## Files

| File | Role |
|---|---|
| `src/Capture.Gpu/VideoTypes.cs` | `IGpuFrameSource`, `IVideoEncoder`, `DesktopFrame`, `EncodedVideoFrame` — the seam. |
| `src/Capture.Gpu/DesktopDuplicationSource.cs` | DXGI Desktop Duplication capture (GPU), with access-loss recovery. |
| `src/Capture.Gpu/MediaFoundationH264Encoder.cs` | Hardware H.264 via the MF Sink Writer → `.mp4`. |
| `tools/EncodeProbe/Program.cs` | Runnable probe: capture → encode → stats + bandwidth comparison. |
| `src/Core/web/webcodecs-decoder.js` | Browser H.264 decode (WebCodecs) → canvas, with JPEG fallback. |

## Build & run the probe

Prereqs: the same **.NET 8 SDK** you already use, plus a GPU with a hardware H.264 encoder
(any recent NVIDIA / Intel / AMD — essentially all of them).

```bat
dotnet run --project tools/EncodeProbe -- 8 30 8 0
```

Args: `[seconds] [fps] [target-Mbps] [outputIndex]`. **Interact with the screen while it
runs** (scroll, drag a window) so the numbers reflect real motion. It writes
`capture-test.mp4` to the current directory and prints, e.g.:

```
Frames encoded : 240  (176 changed, 64 static-reuse)
H.264 bitrate  : 3.10 Mbps average
Encode latency : 1.8 ms avg, 6.4 ms max
JPEG Q75 frame : 210 KB avg
  → a full-frame JPEG stream at 30fps ≈ 50 Mbps
  → H.264 here is ~16× smaller
```

**Verify:** open `capture-test.mp4` — it should play your captured desktop, right-side-up.
If it does, GPU capture + hardware encode work end-to-end on your machine and the bitrate
number is your real streaming budget.

## Fixup checklist

If the probe doesn't compile first try, it's almost certainly one of these Vortice naming
details. The *logic* and the MF GUIDs are correct — these are mechanical swaps.

1. **Attribute setters** (most likely). `MediaFoundationH264Encoder` uses
   `x.Set(Guid, value)` on `IMFAttributes` / `IMFMediaType` / `IMFSample`. If Vortice only
   exposes typed setters, map by value type: `int → SetUInt32`, `long → SetUInt64`,
   `Guid → SetGuid`. Same GUIDs, same values.
2. **Sample time/duration.** Uses properties `sample.SampleTime` / `sample.SampleDuration`.
   If those don't exist, use `sample.SetSampleTime(ticks)` / `sample.SetSampleDuration(ticks)`.
3. **Platform init.** Uses `MediaManager.Startup()` / `.Shutdown()`. Fallback:
   `MediaFactory.MFStartup(0x00020070, 0)` / `MediaFactory.MFShutdown()`.
4. **Factory return shape.** `MFCreateAttributes/MFCreateMediaType/MFCreateMemoryBuffer/`
   `MFCreateSample` are called with `out` params. If your Vortice version returns the object
   instead, switch to `var x = MediaFactory.MFCreateX(...)`.
5. **`Finalize_()`.** Vortice renames `IMFSinkWriter::Finalize` to avoid `object.Finalize`.
   If it's named differently, it's the method that writes the moov atom and closes the file.
6. **`buffer.Lock(out _, out _)`** should return the base `IntPtr`. Adjust if the signature
   differs.
7. **`D3D11CreateDevice` overload.** If the `(… out device, out context)` overload isn't
   found, use the out-device-only overload and read `device.ImmediateContext`.
8. **Enum namespaces.** `ResourceOptionFlags`, `Vortice.Direct3D11.MapFlags`,
   `Vortice.DXGI.ResultCode.WaitTimeout/AccessLost` — quick to confirm via IntelliSense.

If it fights back harder than you want, use the [FFmpeg fallback](#ffmpeg-fallback) to get
the numbers immediately, then return to the native path.

## Productionizing → streaming into StreamSession

The probe writes a **finalized .mp4**. Live streaming needs **Annex B NAL units** pushed as
they're produced. Two ways to get them:

- **(Recommended) Async hardware MFT.** Drive `CLSID_MSH264EncoderMFT` directly (input a
  D3D texture via `MF_SINK_WRITER_D3D_MANAGER` for true zero-copy) and read `IMFSample`s
  from `ProcessOutput`. Configure low-latency: `CODECAPI_AVLowLatencyMode=true`, zero
  B-frames, CBR, infinite GOP + on-demand IDR, intra-refresh. Emit `EncodedVideoFrame`.
- **(Simpler) Sink Writer → custom `IMFByteStream`** with fragmented MP4, then feed the
  browser via MSE instead of WebCodecs. Higher latency; fine as a stepping stone.

### Wire-up (once the streaming encoder emits `EncodedVideoFrame`)

**`AppConfig.cs`** — two fields, default off so nothing changes until you opt in:

```csharp
public bool UseHardwareVideo { get; set; } = false;
public int VideoBitrateKbps { get; set; } = 8000;
```

**New WS frame type** (alongside the existing `type=1` tile frame). Little-endian:

```
u8  type = 2
u16 width
u16 height
u8  flags        // bit0 = keyframe
i32 length
u8  annexB[length]
```

**`StreamSession.SendLoopAsync`** — when `m_Config.UseHardwareVideo`, capture with
`DesktopDuplicationSource`, and on each `TryAcquire`: `null` → skip the send (idle frame,
the big bandwidth saver); otherwise `encoder.Encode(...)` and, for each `EncodedVideoFrame`,
send a `type=2` frame. Map the existing controls: quality slider → `VideoBitrateKbps`,
`m_ForceKeyframe` → `forceKeyframe: true` on the next encode.

**`web/index.html`** — load the decoder before `app.js`:

```html
<script src="webcodecs-decoder.js"></script>
```

**`web/app.js`** — branch by frame type, and fall back to JPEG when WebCodecs is absent:

```js
let video = RDVideo.supported
  ? RDVideo.create(cv, { onResize: fitView, onNeedKeyframe: () => send({ t: 'keyframe' }) })
  : null;

async function renderFrame(buf) {
  const dv = new DataView(buf);
  const type = dv.getUint8(0);
  if (type === 2 && video) {                       // H.264 access unit
    const w = dv.getUint16(1, true), h = dv.getUint16(3, true);
    const isKey = (dv.getUint8(5) & 1) === 1;
    const len = dv.getUint32(6, true);
    return video.decode(new Uint8Array(buf, 10, len), isKey, performance.now());
  }
  // ...existing type === 1 tile path unchanged...
}
```

Server-side, honor a `{ t: 'keyframe' }` control message (the decoder asks for one after an
error) by setting `m_ForceKeyframe = true`.

### Then, the real wins to layer on

- **Client-side cursor.** Desktop Duplication exposes the pointer shape+position separately
  (`OutduplFrameInfo.PointerPosition`, `GetFramePointerShape`). Send those as a tiny JSON
  message and draw the cursor in the browser → sub-frame pointer response, decoupled from
  video latency.
- **Zero-copy.** Bind the D3D device manager to the encoder and feed the DDA texture
  directly — drop the staging copy this spike uses.
- **Shared encoder.** Encode once per `(monitor, bitrate)` and fan the NAL stream out to all
  viewers, instead of the current per-session capturer (consumer NVENC caps concurrent
  sessions).
- **One honest regression:** H.264 4:2:0 softens colored text vs JPEG. Mitigate with a
  higher bitrate, or keep the JPEG-tile mode as an optional "sharp text" toggle.

## FFmpeg fallback

If you want the bandwidth/latency numbers *right now* without touching the native interop,
FFmpeg's `ddagrab` is the exact same DXGI pipeline into your GPU encoder:

```bat
ffmpeg -f lavfi -i ddagrab=output_idx=0:framerate=30 -c:v h264_nvenc ^
  -preset p1 -tune ll -b:v 8M -g 9999 capture-test.mp4
```

Swap `h264_nvenc` for `h264_qsv` (Intel) or `h264_amf` (AMD). `-tune ll` = low latency,
`-g 9999` = infinite GOP. This is a measurement tool, not the shipped design — the point of
the native path is to avoid bundling ffmpeg.exe.
