// Remote Desktop LAN — WebCodecs H.264 decode path (bandwidth upgrade over the JPEG-tile stream).
//
// This is the CLIENT half of the hardware-video pipeline. The server captures the
// desktop on the GPU (DXGI Desktop Duplication) and hardware-encodes H.264; the browser
// decodes it here with WebCodecs (hardware-accelerated) and paints it to the same
// <canvas id="screen"> the JPEG path already uses. Everything is drawn to that canvas so
// the existing zoom/pan/fit and pointer-mapping code keeps working unchanged.
//
// Why WebCodecs (not MSE/WebRTC): lowest latency (no container, no jitter buffer we don't
// control) and, as of 2026, VideoDecoder is supported everywhere that matters INCLUDING
// iOS/iPadOS Safari 16.4+ — which is the whole point of "check my PC from my phone".
//
// Wire-in is deliberately additive: including this file does nothing until app.js calls
// RDVideo.create(). If VideoDecoder is missing (very old browser), RDVideo.supported is
// false and the caller falls back to the existing JPEG-tile renderer.

(() => {
  'use strict';

  const supported = typeof window.VideoDecoder === 'function'
    && typeof window.EncodedVideoChunk === 'function';

  // ---- Annex B helpers -----------------------------------------------------
  // The server sends raw Annex B (NAL units separated by 00 00 01 / 00 00 00 01),
  // which WebCodecs accepts directly when we DON'T pass an avcC `description`.

  // Walk NAL start codes and yield [type, offsetOfPayload] pairs. Cheap; runs once
  // per keyframe (to read the SPS) — delta frames skip this entirely.
  function* iterateNals(bytes) {
    const n = bytes.length;
    let i = 0;
    while (i + 3 < n) {
      // find next start code
      if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1) {
        const type = bytes[i + 3] & 0x1f;
        yield { type, start: i + 3 };
        i += 3;
      } else {
        i++;
      }
    }
  }

  // Build the exact `avc1.PPCCLL` codec string from the SPS so the decoder is
  // configured for the profile/level the encoder actually produced. Guessing a
  // baseline string when the encoder emitted High profile makes configure() reject
  // the stream on some browsers, so we read it from the bitstream instead.
  function codecStringFromKeyframe(bytes) {
    for (const nal of iterateNals(bytes)) {
      if (nal.type === 7) { // SPS
        const p = nal.start + 1;            // skip the NAL header byte
        if (p + 2 < bytes.length) {
          const profile = bytes[p];         // profile_idc
          const constraints = bytes[p + 1]; // constraint_set flags + reserved
          const level = bytes[p + 2];       // level_idc
          const hex = v => v.toString(16).padStart(2, '0');
          return `avc1.${hex(profile)}${hex(constraints)}${hex(level)}`;
        }
      }
    }
    return null; // no SPS in this buffer
  }

  // ---- renderer ------------------------------------------------------------
  // Creates a decoder that paints decoded frames onto `canvas`. Returns an object
  // the caller drives with decode(bytes, isKeyframe, timestampMs).
  //
  //   onResize(w, h)   — first frame / resolution change, so the caller can refit.
  //   onNeedKeyframe() — decoder needs a fresh IDR (startup, or recovery after an
  //                      error). The caller should ask the server to force a keyframe.
  //   onError(err)     — fatal decode/config failure; caller may fall back to JPEG.
  function create(canvas, { onResize, onNeedKeyframe, onError } = {}) {
    const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    let decoder = null;
    let configured = false;
    let waitingForKeyframe = true; // can't decode deltas until an IDR configures us
    let lastW = 0, lastH = 0;

    function paint(frame) {
      try {
        const w = frame.displayWidth, h = frame.displayHeight;
        if (w !== lastW || h !== lastH) {
          canvas.width = w; canvas.height = h;
          lastW = w; lastH = h;
          onResize && onResize(w, h);
        }
        ctx.drawImage(frame, 0, 0);
      } finally {
        frame.close(); // VideoFrames hold GPU memory — release promptly or we stall
      }
    }

    function buildDecoder() {
      decoder = new VideoDecoder({
        output: paint,
        error: err => {
          // A decode error means our reference chain is broken; drop back to
          // "need keyframe" and let the caller request one.
          waitingForKeyframe = true;
          configured = false;
          try { decoder && decoder.state !== 'closed' && decoder.close(); } catch {}
          decoder = null;
          onNeedKeyframe && onNeedKeyframe();
          onError && onError(err);
        },
      });
    }

    async function configureFrom(keyframeBytes) {
      const codec = codecStringFromKeyframe(keyframeBytes);
      if (!codec) return false; // wait for a buffer that actually contains the SPS
      const config = { codec, optimizeForLatency: true, hardwareAcceleration: 'prefer-hardware' };
      try {
        const support = await VideoDecoder.isConfigSupported(config);
        if (!support.supported) {
          // Retry without the hardware hint before giving up (some builds only do software).
          const soft = { codec, optimizeForLatency: true };
          const s2 = await VideoDecoder.isConfigSupported(soft);
          if (!s2.supported) { onError && onError(new Error('H.264 config unsupported: ' + codec)); return false; }
          decoder.configure(soft);
        } else {
          decoder.configure(config);
        }
        configured = true;
        return true;
      } catch (err) {
        onError && onError(err);
        return false;
      }
    }

    return {
      get supported() { return supported; },

      // Feed one access unit. `isKeyframe` comes from the server (the encoder knows
      // which frames are IDRs) so we don't have to scan every delta for slice types.
      async decode(bytes, isKeyframe, timestampMs) {
        if (!supported) return;
        if (!decoder) buildDecoder();

        if (waitingForKeyframe) {
          if (!isKeyframe) return;         // ignore deltas until we can start clean
          if (!configured && !(await configureFrom(bytes))) return;
          waitingForKeyframe = false;
        }
        if (!configured) return;

        try {
          decoder.decode(new EncodedVideoChunk({
            type: isKeyframe ? 'key' : 'delta',
            timestamp: Math.round((timestampMs ?? performance.now()) * 1000), // µs
            data: bytes,
          }));
        } catch (err) {
          waitingForKeyframe = true;
          onNeedKeyframe && onNeedKeyframe();
          onError && onError(err);
        }
      },

      close() {
        try { decoder && decoder.state !== 'closed' && decoder.close(); } catch {}
        decoder = null; configured = false; waitingForKeyframe = true;
      },
    };
  }

  window.RDVideo = { supported, create };
})();
