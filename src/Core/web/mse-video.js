// CouchDesk hardware-video renderer (MediaSource / fragmented MP4).
//
// The CLIENT half of the UseHardwareVideo path. The server captures on the GPU, hardware-
// encodes H.264, and streams a fragmented MP4 over the WebSocket (binary messages tagged
// with a leading byte 3). Here we feed those bytes to a MediaSource-backed <video> and draw
// each frame onto the SAME <canvas id="screen"> the JPEG path uses — so all the existing
// zoom / pan / fit and pointer-mapping code keeps working unchanged.
//
// Live-latency discipline: a <video> playing MSE naturally drifts behind the newest data
// whenever playback stalls (which happens on full-motion content), and the gap never
// recovers on its own — that's how latency runs away to seconds. So we actively chase the
// live edge: nudge playbackRate up when slightly behind, hard-seek when far behind, trim
// the buffer so it can't accumulate, and jump to live when the tab is refocused.

(() => {
  'use strict';

  const CODEC = 'video/mp4; codecs="avc1.640034"'; // High profile, ~L5.2 — covers ultrawide
  const supported = 'MediaSource' in window
    && typeof MediaSource.isTypeSupported === 'function'
    && MediaSource.isTypeSupported(CODEC);

  // Target no more than this many seconds behind the live edge.
  const TARGET_LATENCY = 0.15;
  const CATCHUP_RATE = 1.15;   // gentle speed-up when a little behind
  const HARD_SEEK_GAP = 1.0;   // jump straight to live past this

  function create(canvas, { onResize } = {}) {
    const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });

    const video = document.createElement('video');
    video.muted = true;
    video.autoplay = true;
    video.playsInline = true;
    video.setAttribute('playsinline', '');
    // Keep it in the page (invisible) so browsers don't throttle a detached element.
    video.style.cssText = 'position:fixed;left:0;top:0;width:1px;height:1px;opacity:0;pointer-events:none;';
    document.body.appendChild(video);

    const mediaSource = new MediaSource();
    video.src = URL.createObjectURL(mediaSource);

    let sourceBuffer = null;
    const pending = [];
    let lastW = 0, lastH = 0, raf = 0, watchdog = 0, closed = false;

    mediaSource.addEventListener('sourceopen', () => {
      try {
        sourceBuffer = mediaSource.addSourceBuffer(CODEC);
        sourceBuffer.mode = 'sequence';
        sourceBuffer.addEventListener('updateend', flush);
        flush();
      } catch (e) {}
    });

    function liveEdge() {
      const b = video.buffered;
      return b.length ? b.end(b.length - 1) : null;
    }

    function flush() {
      if (!sourceBuffer || sourceBuffer.updating || pending.length === 0) return;
      let total = 0;
      for (const c of pending) total += c.length;
      const merged = new Uint8Array(total);
      let off = 0;
      for (const c of pending) { merged.set(c, off); off += c.length; }
      pending.length = 0;
      try { sourceBuffer.appendBuffer(merged); }
      catch (e) { if (e && e.name === 'QuotaExceededError') trim(true); }
    }

    // Drop buffered data well behind the play head so the SourceBuffer can't grow without
    // bound (which eventually stalls with QuotaExceeded).
    function trim(force) {
      try {
        if (!sourceBuffer || sourceBuffer.updating || !video.buffered.length) return;
        const start = video.buffered.start(0);
        const keepFrom = Math.max(start, video.currentTime - (force ? 0.5 : 2));
        if (keepFrom - start > 1) sourceBuffer.remove(start, keepFrom);
      } catch (e) {}
    }

    // Keep playback pinned near the live edge.
    function chase() {
      if (closed) return;
      const end = liveEdge();
      if (end != null) {
        const gap = end - video.currentTime;
        if (gap < 0 || gap > HARD_SEEK_GAP) {
          try { video.currentTime = end - TARGET_LATENCY; } catch (e) {}
          video.playbackRate = 1.0;
        } else if (gap > TARGET_LATENCY + 0.1) {
          video.playbackRate = CATCHUP_RATE;   // slightly behind → catch up smoothly
        } else {
          video.playbackRate = 1.0;
        }
      }
      if (video.paused) video.play().catch(() => {});
      trim(false);
    }

    function draw() {
      if (closed) return;
      if (video.videoWidth) {
        if (video.videoWidth !== lastW || video.videoHeight !== lastH) {
          canvas.width = video.videoWidth;
          canvas.height = video.videoHeight;
          lastW = video.videoWidth; lastH = video.videoHeight;
          onResize && onResize();
        }
        ctx.drawImage(video, 0, 0);
      }
      raf = requestAnimationFrame(draw);
    }

    // Returning to a backgrounded tab: rAF/timers were throttled and the video fell far
    // behind — snap straight back to the live edge instead of playing catch-up for seconds.
    function onVisible() {
      if (document.hidden) return;
      const end = liveEdge();
      if (end != null) { try { video.currentTime = end - TARGET_LATENCY; } catch (e) {} }
      if (!raf) draw();
    }
    document.addEventListener('visibilitychange', onVisible);

    video.addEventListener('loadeddata', () => { if (!raf) draw(); });
    video.play().catch(() => {});
    watchdog = setInterval(chase, 200);

    return {
      get supported() { return supported; },
      get pending() { return pending.length + (sourceBuffer && sourceBuffer.updating ? 1 : 0); },

      appendChunk(bytes) {
        if (closed) return;
        pending.push(bytes);
        flush();
      },

      close() {
        closed = true;
        clearInterval(watchdog);
        cancelAnimationFrame(raf);
        document.removeEventListener('visibilitychange', onVisible);
        try { video.pause(); } catch (e) {}
        try { if (mediaSource.readyState === 'open') mediaSource.endOfStream(); } catch (e) {}
        try { URL.revokeObjectURL(video.src); } catch (e) {}
        try { video.remove(); } catch (e) {}
      },
    };
  }

  window.RDMse = { supported, create };
})();
