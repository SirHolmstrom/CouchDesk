// CouchDesk browser viewer client (mouse + touch + on-screen keyboard).

const cv = document.getElementById('screen');
const ctx = cv.getContext('2d');
const stage = document.getElementById('stage');
const remoteCursorEl = document.getElementById('remoteCursor');
const statEl = document.getElementById('stat');
const latEl = document.getElementById('lat');
const kbdBtn = document.getElementById('kbdBtn');

// Mirrored by StreamBinaryMessageType on the host. JavaScript has no native enum,
// so freeze the mapping to keep packet discriminators explicit and immutable.
const BinaryMessageType = Object.freeze({
  JpegTiles: 1,
  FragmentedMp4: 3,
  H264AnnexB: 4,
  PointerMove: 5,
  CursorState: 6,
});

let ws, controlWs, pingTimer, networkPingTimer, reconnectTimer, controlReconnectTimer, idlePauseTimer;
let queue = [], processing = false;
let sessionInfo = null;
let videoRenderer = null; // set when the server streams hardware H.264 (MediaSource path)
let reconnectAttempts = 0;
let controlReconnectAttempts = 0;
let controlToken = '';
let manualDisconnect = false;
let resumeRunning = false;
let lastPongAt = 0;
let streamMode = 'jpeg';
let hostPerf = null;
let clientPerf = null;
let networkRtt = 0;
let networkRttAt = 0;
let networkPingRunning = false;
let previousGc = null;
let lastSpikeAt = 0;
let lastSpikeSummary = '';
let lastSpikeReportAt = 0;
let browserEventLoopPeak = 0;
let browserLongTaskPeak = 0;

// A WebSocket message can arrive promptly but be handled late if Chrome's main
// thread pauses. Keep browser scheduling separate from network latency.
const browserLoopProbeMs = 100;
let browserLoopExpectedAt = performance.now() + browserLoopProbeMs;
setInterval(() => {
  const now = performance.now();
  if (document.visibilityState === 'visible')
    browserEventLoopPeak = Math.max(browserEventLoopPeak, now - browserLoopExpectedAt);
  browserLoopExpectedAt = now + browserLoopProbeMs;
}, browserLoopProbeMs);

try {
  if (window.PerformanceObserver
      && PerformanceObserver.supportedEntryTypes?.includes('longtask')) {
    new PerformanceObserver(list => {
      for (const entry of list.getEntries())
        browserLongTaskPeak = Math.max(browserLongTaskPeak, entry.duration);
    }).observe({ type: 'longtask', buffered: false });
  }
} catch (e) {}

function takeBrowserResponsivenessStats() {
  const result = {
    eventLoopPeakMs: Math.max(0, browserEventLoopPeak),
    longTaskPeakMs: Math.max(0, browserLongTaskPeak),
  };
  browserEventLoopPeak = 0;
  browserLongTaskPeak = 0;
  return result;
}

// ---------------- adaptive stream quality ----------------
const adaptiveLevels = [
  { fps: 30, quality: 75, bitratePercent: 100, label: 'best' },
  { fps: 20, quality: 68, bitratePercent: 75, label: 'steady' },
  { fps: 15, quality: 58, bitratePercent: 55, label: 'saving bandwidth' },
  { fps: 10, quality: 48, bitratePercent: 35, label: 'recovering' },
];
let adaptiveEnabled = true;
let adaptiveLevel = 0;
let adaptiveLatency = 0;
let lastHealthRtt = 0;
let adaptiveGoodSince = 0;
let adaptiveLastChange = 0;
let streamFps = adaptiveLevels[0].fps;
let streamQuality = adaptiveLevels[0].quality;
let streamBitratePercent = adaptiveLevels[0].bitratePercent;

function applySessionInfo(info) {
  sessionInfo = info;
  const permissions = info.permissions || {};
  const allowed = {
    control: !!permissions.canControl,
    system: !!permissions.canUseSystemKeys,
    files: !!permissions.canTransferFiles,
  };
  document.querySelectorAll('[data-requires]').forEach(element => {
    element.classList.toggle('permission-hidden', !allowed[element.dataset.requires]);
  });
  if (!allowed.control) {
    document.getElementById('pad').classList.add('hidden');
    document.getElementById('vkbd').classList.remove('show');
    document.getElementById('arrows').classList.add('hidden');
  }
  if (!allowed.system) document.getElementById('syskeys').classList.add('hidden');
  const role = info.role === 'guest' ? `guest · ${info.accessLevel}` : 'owner';
  document.getElementById('sessionRole').textContent = role;
  if (allowed.control) setRemoteCursor(curX, curY, true);
}

// ---------------- zoom / pan ----------------
let scale = 1, minScale = 1, tx = 0, ty = 0;
const clamp01 = v => Math.min(1, Math.max(0, v));
function applyTransform() {
  cv.style.transform = `translate(${tx}px,${ty}px) scale(${scale})`;
  positionRemoteCursor();
}
function fitView() {
  const vw = stage.clientWidth, vh = stage.clientHeight;
  if (!cv.width || !cv.height || !vw || !vh) return;
  minScale = Math.min(vw / cv.width, vh / cv.height);
  scale = minScale;
  tx = (vw - cv.width * scale) / 2;
  ty = (vh - cv.height * scale) / 2;
  applyTransform();
}
function resetZoom() { fitView(); }
function clampView() {
  const vw = stage.clientWidth, vh = stage.clientHeight;
  const cw = cv.width * scale, ch = cv.height * scale;
  tx = cw <= vw ? (vw - cw) / 2 : Math.min(0, Math.max(vw - cw, tx));
  ty = ch <= vh ? (vh - ch) / 2 : Math.min(0, Math.max(vh - ch, ty));
}
window.addEventListener('resize', fitView);

// ---------------- remote cursor overlay ----------------
// Visible by default: if the server cursor feed breaks, the user still gets a
// local pointer marker instead of staring at an invisible cursor.
let remoteCursor = { visible: true, x: 0.5, y: 0.5 };
const REMOTE_INPUT_PRIORITY_MS = 4000;
let remoteInputPriorityUntil = 0;
let steeringBlockedUntil = 0;
let pendingServerCursor = null, lastServerCursor = null, serverCursorSnapTimer = 0;
function canControl() { return !!sessionInfo?.permissions?.canControl; }
function isSteeringBlocked() { return performance.now() < steeringBlockedUntil; }
function beginPointerAction() {
  if (!canControl() || isSteeringBlocked()) return false;
  markLocalCursorInput();
  return true;
}
function markLocalCursorInput() {
  remoteInputPriorityUntil = performance.now() + REMOTE_INPUT_PRIORITY_MS;
  scheduleServerCursorSnap();
}
function setRemoteCursor(x, y, visible = true) {
  remoteCursor = { visible: visible !== false || canControl(), x: clamp01(+x || 0), y: clamp01(+y || 0) };
  positionRemoteCursor();
}
function applyServerCursor(m) {
  lastServerCursor = m;
  pendingServerCursor = null;
  if (serverCursorSnapTimer) {
    clearTimeout(serverCursorSnapTimer);
    serverCursorSnapTimer = 0;
  }
  setRemoteCursor(m.x, m.y, m.visible);
  if (m.visible && canControl()) { curX = clamp01(m.x); curY = clamp01(m.y); }
}
function scheduleServerCursorSnap() {
  if (!pendingServerCursor) return;
  if (serverCursorSnapTimer) clearTimeout(serverCursorSnapTimer);
  const wait = Math.max(0, remoteInputPriorityUntil - performance.now());
  serverCursorSnapTimer = setTimeout(() => {
    serverCursorSnapTimer = 0;
    if (!pendingServerCursor) return;
    if (performance.now() < remoteInputPriorityUntil) {
      scheduleServerCursorSnap();
      return;
    }
    applyServerCursor(pendingServerCursor);
  }, wait);
}
function positionRemoteCursor() {
  if (!remoteCursorEl) return;
  if (!remoteCursor.visible) {
    remoteCursorEl.classList.add('hidden');
    return;
  }
  const width = cv.width || stage.clientWidth || 1;
  const height = cv.height || stage.clientHeight || 1;
  const x = tx + remoteCursor.x * width * scale;
  const y = ty + remoteCursor.y * height * scale;
  const zoomRatio = minScale > 0 ? scale / minScale : 1;
  const cursorScale = Math.max(0.3, Math.min(1, 0.3 + 0.7 * (1 - Math.exp(-0.35 * Math.max(0, zoomRatio - 1)))));
  remoteCursorEl.classList.toggle('smooth', !canControl() || performance.now() >= remoteInputPriorityUntil);
  remoteCursorEl.style.transform = `translate3d(${x - 3}px,${y - 3}px,0) scale(${cursorScale})`;
  remoteCursorEl.classList.remove('hidden');
}
positionRemoteCursor();

// ---------------- connection ----------------
function connect() {
  if (manualDisconnect) return;
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  closeControlChannel(true);
  clearTimeout(reconnectTimer);
  const socket = new WebSocket(`wss://${location.host}/ws`);
  ws = socket;
  socket.binaryType = 'arraybuffer';
  socket.onopen = () => {
    if (ws !== socket) {
      try { socket.close(); } catch (e) {}
      return;
    }
    reconnectAttempts = 0;
    lastPongAt = performance.now();
    setStatus('connected', 'ok');
    startPing();
    startNetworkPing();
    requestWakeLock();
    if (adaptiveEnabled) applyAdaptiveLevel(true);
  };
  socket.onclose = () => {
    if (ws !== socket) return;
    ws = null;
    closeControlChannel(true);
    clearInterval(pingTimer);
    stopNetworkPing();
    resetVideoRenderer();
    if (manualDisconnect) return;
    setStatus(document.visibilityState === 'visible' ? 'reconnecting…' : 'paused', 'wait');
    scheduleReconnect();
  };
  socket.onerror = () => {
    if (ws === socket) setStatus(navigator.onLine === false ? 'offline' : 'connection issue', 'bad');
  };
  socket.onmessage = event => {
    if (ws === socket) onMessage(event);
  };
}

function connectControl(token) {
  if (manualDisconnect || !token) return;
  if (token !== controlToken) {
    closeControlChannel(true);
    controlToken = token;
  }
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  if (controlWs && (controlWs.readyState === WebSocket.OPEN || controlWs.readyState === WebSocket.CONNECTING)) return;

  clearTimeout(controlReconnectTimer);
  const socket = new WebSocket(`wss://${location.host}/ws/control?token=${encodeURIComponent(controlToken)}`);
  controlWs = socket;
  socket.binaryType = 'arraybuffer';
  socket.onopen = () => {
    if (controlWs !== socket) {
      try { socket.close(); } catch (e) {}
      return;
    }
    controlReconnectAttempts = 0;
    lastPongAt = performance.now();
  };
  socket.onmessage = event => {
    if (controlWs !== socket) return;
    if (typeof event.data === 'string') {
      try { handleControl(JSON.parse(event.data)); } catch (e) {}
      return;
    }
    const bytes = new Uint8Array(event.data);
    if (bytes[0] === BinaryMessageType.CursorState) handleBinaryCursor(bytes);
  };
  socket.onclose = () => {
    if (controlWs !== socket) return;
    controlWs = null;
    if (!manualDisconnect && ws?.readyState === WebSocket.OPEN && document.visibilityState === 'visible')
      scheduleControlReconnect();
  };
  socket.onerror = () => {};
}

function scheduleControlReconnect() {
  if (manualDisconnect || !controlToken || ws?.readyState !== WebSocket.OPEN) return;
  clearTimeout(controlReconnectTimer);
  const delay = Math.min(5000, 250 * Math.pow(1.6, controlReconnectAttempts++));
  controlReconnectTimer = setTimeout(() => connectControl(controlToken), delay);
}

function closeControlChannel(clearToken) {
  clearTimeout(controlReconnectTimer);
  controlReconnectTimer = 0;
  controlReconnectAttempts = 0;
  const socket = controlWs;
  controlWs = null;
  if (clearToken) controlToken = '';
  if (socket && socket.readyState < WebSocket.CLOSING) {
    try { socket.close(1000, 'video channel closed'); } catch (e) {}
  }
}

function controlSocket() {
  return controlWs?.readyState === WebSocket.OPEN ? controlWs : ws;
}

function scheduleReconnect() {
  if (manualDisconnect) return;
  clearTimeout(reconnectTimer);
  if (document.visibilityState !== 'visible') return;
  if (navigator.onLine === false) {
    setStatus('offline', 'wait');
    return;
  }
  const delay = Math.min(10000, 500 * Math.pow(1.6, reconnectAttempts++));
  reconnectTimer = setTimeout(resumeViewer, delay);
}

async function resumeViewer() {
  if (manualDisconnect) return;
  if (document.visibilityState !== 'visible') return;
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  if (resumeRunning) return;

  resumeRunning = true;
  setStatus('resuming…', 'wait');
  try {
    const r = await fetch('/api/session', { cache: 'no-store' });
    if (r.status === 401 || r.status === 403) { location.href = '/login.html'; return; }
    if (!r.ok) throw new Error(`Session check failed (${r.status})`);
    applySessionInfo(await r.json());
    resetVideoRenderer();
    queue.length = 0;
    connect();
  } catch (e) {
    setStatus(navigator.onLine === false ? 'offline' : 'reconnecting…', 'wait');
    scheduleReconnect();
  } finally {
    resumeRunning = false;
  }
}

function resetVideoRenderer() {
  if (!videoRenderer) return;
  try { videoRenderer.close && videoRenderer.close(); } catch (e) {}
  videoRenderer = null;
}
function onMessage(ev) {
  if (typeof ev.data === 'string') { handleControl(JSON.parse(ev.data)); return; }
  const bytes = new Uint8Array(ev.data);
  const type = bytes[0];
  if (type === BinaryMessageType.CursorState) {
    handleBinaryCursor(bytes);
    return;
  }
  // Route by leading type byte: 4 = per-frame H.264 (WebCodecs), 3 = fragmented MP4 (MSE),
  // else JPEG tiles.
  if (videoRenderer) {
    if (type === BinaryMessageType.H264AnnexB) { // [type][flags(bit0=keyframe)][annexB]
      const flags = new Uint8Array(ev.data, 1, 1)[0];
      videoRenderer.decode(new Uint8Array(ev.data, 2), (flags & 1) === 1, performance.now());
      return;
    }
    if (type === BinaryMessageType.FragmentedMp4) { videoRenderer.appendChunk(new Uint8Array(ev.data, 1)); return; }
  }
  if (type !== BinaryMessageType.JpegTiles) return;
  // Binary tiles = a frame of changed regions. Process in order (deltas must not drop).
  queue.push(ev.data);
  if (!processing) processQueue();
}
async function processQueue() {
  processing = true;
  while (queue.length) { await renderFrame(queue.shift()); }
  processing = false;
}

// Binary frame (little-endian): u8 type=1, u16 w, u16 h, u16 tileCount,
// then per tile: u16 x, u16 y, u16 w, u16 h, i32 jpegLen, jpeg bytes.
async function renderFrame(buf) {
  try {
    const dv = new DataView(buf);
    if (dv.getUint8(0) !== 1) return;
    const W = dv.getUint16(1, true), H = dv.getUint16(3, true), count = dv.getUint16(5, true);
    // Size only changes on a keyframe, so resizing (which clears) is safe here.
    if (cv.width !== W || cv.height !== H) { cv.width = W; cv.height = H; fitView(); }

    let off = 7;
    const jobs = [];
    for (let i = 0; i < count; i++) {
      const x = dv.getUint16(off, true), y = dv.getUint16(off + 2, true);
      const w = dv.getUint16(off + 4, true), h = dv.getUint16(off + 6, true);
      const len = dv.getUint32(off + 8, true);
      off += 12;
      const bytes = new Uint8Array(buf, off, len);
      off += len;
      jobs.push(createImageBitmap(new Blob([bytes], { type: 'image/jpeg' }))
        .then(bmp => ({ bmp, x, y, w, h })));
    }
    const tiles = await Promise.all(jobs);
    for (const t of tiles) { ctx.drawImage(t.bmp, t.x, t.y); t.bmp.close && t.bmp.close(); }
  } catch (e) {}
}
function handleBinaryCursor(bytes) {
  if (bytes.byteLength !== 8) return;
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const flags = bytes[1];
  const sourceCode = (flags >> 1) & 0x03;
  handleCursorUpdate({
    visible: (flags & 0x01) !== 0,
    x: view.getUint16(2, true) / 65535,
    y: view.getUint16(4, true) / 65535,
    source: sourceCode === 1 ? 'self' : sourceCode === 2 ? 'remote' : 'host',
    hostBlockMs: view.getUint16(6, true),
  });
}

function handleCursorUpdate(m) {
  if (m.source === 'self' && canControl() && !isSteeringBlocked()) {
    pendingServerCursor = null;
    return;
  }
  const hostTakeover = m.source === 'host' && +m.hostBlockMs > 0;
  if (hostTakeover) {
    steeringBlockedUntil = Math.max(steeringBlockedUntil, performance.now() + +m.hostBlockMs);
  }
  if (hostTakeover || m.source === 'remote') {
    remoteInputPriorityUntil = 0;
  }
  if (!hostTakeover && m.source !== 'remote' && canControl() && performance.now() < remoteInputPriorityUntil) {
    pendingServerCursor = m;
    scheduleServerCursorSnap();
    return;
  }
  applyServerCursor(m);
}

function handleControl(m) {
  switch (m.t) {
    case 'control-channel':
      connectControl(m.token);
      break;
    case 'pong':
      lastPongAt = performance.now();
      const rtt = lastPongAt - m.ts;
      if (m.perf) {
        const gcDelta = previousGc
          ? {
              gc0: Math.max(0, m.perf.gc0 - previousGc.gc0),
              gc1: Math.max(0, m.perf.gc1 - previousGc.gc1),
              gc2: Math.max(0, m.perf.gc2 - previousGc.gc2),
            }
          : { gc0: 0, gc1: 0, gc2: 0 };
        previousGc = { gc0: m.perf.gc0, gc1: m.perf.gc1, gc2: m.perf.gc2 };
        hostPerf = { ...m.perf, gcDelta };
      }
      const responsiveness = takeBrowserResponsivenessStats();
      clientPerf = videoRenderer && typeof videoRenderer.takeStats === 'function'
        ? { ...videoRenderer.takeStats(), ...responsiveness }
        : responsiveness;
      if (rtt >= 80 && hostPerf) {
        lastSpikeAt = performance.now();
        lastSpikeSummary = `Spike ${Math.round(rtt)} · net ${networkRtt ? Math.round(networkRtt) : '--'} · map ${Math.round(+hostPerf.captureMapPeakMs || 0)} · loop ${Math.round(+clientPerf.eventLoopPeakMs || 0)} ms`;
        reportStreamSpike(rtt);
      }
      latEl.textContent = Math.round(rtt) + ' ms';
      noteAdaptiveSample(rtt);
      break;
    case 'monitors': fillMonitors(m.list, m.active); break;
    case 'status': {
      if (m.state === 'video-h264') { // low-latency per-frame H.264 via WebCodecs
        streamMode = 'h264';
        updateAdaptiveUi();
        if (!videoRenderer && window.RDVideo && RDVideo.supported) {
          videoRenderer = RDVideo.create(cv, { onResize: fitView, onNeedKeyframe: () => send({ t: 'keyframe' }) });
          setStatus('connected · H.264 (low latency)', 'ok');
        } else if (!window.RDVideo || !RDVideo.supported) {
          setStatus('WebCodecs not supported here', 'bad');
        }
        break;
      }
      if (m.state === 'video') { // fragmented MP4 via MediaSource
        streamMode = 'h264';
        updateAdaptiveUi();
        if (!videoRenderer && window.RDMse && RDMse.supported) {
          videoRenderer = RDMse.create(cv, { onResize: fitView });
          setStatus('connected · H.264', 'ok');
        } else if (!window.RDMse || !RDMse.supported) {
          setStatus('H.264 not supported here', 'bad');
        }
        break;
      }
      const label = m.state === 'disabled' ? 'disabled by host'
        : m.state === 'access-revoked' ? 'guest access ended'
        : m.state;
      if (m.state === 'connected') {
        streamMode = 'jpeg';
        updateAdaptiveUi();
      }
      setStatus(label, m.state === 'connected' ? 'ok' : 'bad');
      break;
    }
    // real text of the PC's focused field (UI Automation) → seed the echo so it matches reality
    case 'cursor': handleCursorUpdate(m); break;
    case 'steering':
      if (m.state === 'blocked') {
        steeringBlockedUntil = performance.now() + Math.max(250, +m.ms || 1000);
        remoteInputPriorityUntil = 0;
        if (pendingServerCursor) applyServerCursor(pendingServerCursor);
        else if (lastServerCursor) applyServerCursor(lastServerCursor);
      }
      break;
    case 'focusText': echoBuf = (m.text || '').slice(-5000); echoRender(); break;
    case 'kicked':
      manualDisconnect = true;
      clearTimeout(reconnectTimer);
      clearTimeout(idlePauseTimer);
      clearInterval(pingTimer);
      stopNetworkPing();
      releaseWakeLock();
      setStatus('disconnected by host', 'bad');
      closeControlChannel(true);
      try { ws && ws.close(1000, 'kicked'); } catch (e) {}
      setTimeout(() => { location.href = '/login.html'; }, 900);
      break;
  }
}
function fillMonitors(list, active) {
  const sel = document.getElementById('mon');
  sel.innerHTML = '';
  list.forEach(mn => {
    const o = document.createElement('option');
    o.value = mn.index;
    o.textContent = `#${mn.index} ${mn.w}×${mn.h}${mn.primary ? ' ★' : ''}`;
    if (mn.index === active) o.selected = true;
    sel.appendChild(o);
  });
}
function setStatus(s, level) { // level: 'ok' | 'wait' | 'bad'
  statEl.textContent = s;
  const cls = 'dot ' + (level || 'wait');
  const d1 = document.getElementById('dot'), d2 = document.getElementById('dot2');
  if (d1) d1.className = cls;
  if (d2) d2.className = cls;
}

function getStreamBacklog() {
  if (videoRenderer && typeof videoRenderer.pending === 'number') return videoRenderer.pending;
  return queue.length + (processing ? 1 : 0);
}

function updateFpsButtons(value) {
  document.querySelectorAll('#panel .seg button')
    .forEach(b => b.classList.toggle('on', +b.dataset.fps === value));
}

function updateAdaptiveUi() {
  const btn = document.getElementById('autoBtn');
  const stat = document.getElementById('autoStat');
  if (btn) {
    btn.classList.toggle('on', adaptiveEnabled);
    btn.textContent = adaptiveEnabled ? 'Auto' : 'Manual';
  }
  if (stat) {
    const level = adaptiveLevels[adaptiveLevel];
    const mode = streamMode === 'jpeg' ? 'JPEG' : 'H.264';
    stat.textContent = adaptiveEnabled
      ? `${level.label} · ${streamFps} fps · ${mode}`
      : 'manual';
  }
  const q = document.getElementById('q');
  if (q && +q.value !== streamQuality) q.value = streamQuality;
  updateFpsButtons(streamFps);
  updateHealthUi();
}

function updateHealthUi() {
  const stateEl = document.getElementById('healthState');
  const latencyEl = document.getElementById('healthLatency');
  const queueEl = document.getElementById('healthQueue');
  const codecEl = document.getElementById('healthCodec');
  const perfEl = document.getElementById('perfStat');
  if (!stateEl || !latencyEl || !queueEl || !codecEl) return;
  const backlog = getStreamBacklog();
  const avg = adaptiveLatency || lastHealthRtt;
  const mode = streamMode === 'jpeg' ? 'JPEG' : 'H.264';
  const state = backlog > 4 || avg > 150
    ? 'busy'
    : backlog > 1 || avg > 90
      ? 'ok'
      : 'good';
  stateEl.textContent = lastHealthRtt ? state : 'waiting';
  stateEl.className = `metric-value ${lastHealthRtt ? state : ''}`;
  latencyEl.textContent = lastHealthRtt ? `${Math.round(lastHealthRtt)} ms` : '-- ms';
  queueEl.textContent = String(backlog);
  codecEl.textContent = mode;

  if (perfEl && hostPerf) {
    const capturePeak = +hostPerf.capturePeakMs || 0;
    const captureAcquirePeak = +hostPerf.captureAcquirePeakMs || 0;
    const captureCopyPeak = +hostPerf.captureCopyPeakMs || 0;
    const captureMapPeak = +hostPerf.captureMapPeakMs || 0;
    const captureCpuCopyPeak = +hostPerf.captureCpuCopyPeakMs || 0;
    const encodePeak = +hostPerf.encodePeakMs || 0;
    const controlSendWaitPeak = +hostPerf.controlSendWaitPeakMs || 0;
    const controlSendPeak = +hostPerf.controlSendPeakMs || 0;
    const sendWaitPeak = +hostPerf.sendWaitPeakMs || 0;
    const sendPeak = +hostPerf.sendPeakMs || 0;
    const sendTotalPeak = sendWaitPeak + sendPeak;
    const paintPeak = +clientPerf?.paintPeakMs || 0;
    const paintGapPeak = +clientPerf?.paintGapPeakMs || 0;
    const gc = hostPerf.gcDelta || { gc0: 0, gc1: 0, gc2: 0 };
    const showMs = value => value < 10 ? value.toFixed(1) : Math.round(value).toString();
    const netLabel = networkRtt ? `Net ${Math.round(networkRtt)} · ` : '';
    perfEl.textContent = lastSpikeSummary && performance.now() - lastSpikeAt < 10000
      ? lastSpikeSummary
      : `${netLabel}peak cap ${showMs(capturePeak)} · enc ${showMs(encodePeak)} · send ${showMs(sendTotalPeak)} ms`;
    perfEl.classList.toggle(
      'warn',
      performance.now() - lastSpikeAt < 10000
        || Math.max(capturePeak, encodePeak, sendTotalPeak, paintPeak) > 50);
    perfEl.title = [
      `Control channel: ${controlWs?.readyState === WebSocket.OPEN ? 'separate WebSocket' : 'video-socket fallback'}`,
      `Independent network RTT: ${networkRtt ? Math.round(networkRtt) + ' ms' : 'waiting'}`,
      `Control RTT: ${lastHealthRtt ? Math.round(lastHealthRtt) + ' ms' : 'waiting'}`,
      `Host capture last/peak: ${showMs(+hostPerf.captureMs || 0)} / ${showMs(capturePeak)} ms`,
      `  DXGI acquire peak: ${showMs(captureAcquirePeak)} ms (includes waiting for a desktop update)`,
      `  GPU copy/map peak: ${showMs(captureCopyPeak)} / ${showMs(captureMapPeak)} ms`,
      `  CPU pixel copy peak: ${showMs(captureCpuCopyPeak)} ms`,
      `Host encode last/peak: ${showMs(+hostPerf.encodeMs || 0)} / ${showMs(encodePeak)} ms`,
      `Control send wait/send peak: ${showMs(controlSendWaitPeak)} / ${showMs(controlSendPeak)} ms`,
      `Send-lock wait last/peak: ${showMs(+hostPerf.sendWaitMs || 0)} / ${showMs(sendWaitPeak)} ms`,
      `WebSocket send last/peak: ${showMs(+hostPerf.sendMs || 0)} / ${showMs(sendPeak)} ms`,
      `Host queue current/peak: ${hostPerf.hostQueue || 0} / ${hostPerf.hostQueuePeak || 0}`,
      `Encoded frame last/peak: ${Math.round((hostPerf.frameBytes || 0) / 1024)} / ${Math.round((hostPerf.framePeakBytes || 0) / 1024)} KB`,
      `Video rate/target/applied: ${Math.round(hostPerf.videoKbps || 0)} / ${hostPerf.targetVideoBitrateKbps || 0} / ${hostPerf.appliedVideoBitrateKbps || 0} Kbps`,
      `Video frames in sample: ${hostPerf.videoFrames || 0}`,
      `Large frame paced in sample: ${hostPerf.pacedFrameSeen ? 'yes' : 'no'}`,
      `Browser paint last/peak: ${showMs(+clientPerf?.paintMs || 0)} / ${showMs(paintPeak)} ms`,
      `Browser frame gap last/peak: ${showMs(+clientPerf?.paintGapMs || 0)} / ${showMs(paintGapPeak)} ms`,
      `Browser event-loop/long-task peak: ${showMs(+clientPerf?.eventLoopPeakMs || 0)} / ${showMs(+clientPerf?.longTaskPeakMs || 0)} ms`,
      `Host timer drift peak: ${showMs(+hostPerf.hostTimerPeakMs || 0)} ms`,
      `CPU CouchDesk/system: ${showMs(+hostPerf.processCpuPercent || 0)}% / ${showMs(+hostPerf.systemCpuPercent || 0)}%`,
      `ThreadPool queue/threads: ${hostPerf.threadPoolQueue || 0} / ${hostPerf.threadPoolThreads || 0}`,
      `Keyframe sent in sample: ${hostPerf.keyframeSeen ? 'yes' : 'no'}`,
      `Host GC delta: gen0 ${gc.gc0}, gen1 ${gc.gc1}, gen2 ${gc.gc2}`,
    ].join('\n');
  }
}

function reportStreamSpike(streamRtt) {
  const now = performance.now();
  if (!hostPerf || now - lastSpikeReportAt < 5000) return;
  lastSpikeReportAt = now;
  const gc = hostPerf.gcDelta || { gc0: 0, gc1: 0, gc2: 0 };
  const report = {
    controlRttMs: streamRtt,
    networkRttMs: networkRtt,
    networkSampleAgeMs: networkRttAt ? now - networkRttAt : 0,
    capturePeakMs: +hostPerf.capturePeakMs || 0,
    captureAcquirePeakMs: +hostPerf.captureAcquirePeakMs || 0,
    captureCopyPeakMs: +hostPerf.captureCopyPeakMs || 0,
    captureMapPeakMs: +hostPerf.captureMapPeakMs || 0,
    captureCpuCopyPeakMs: +hostPerf.captureCpuCopyPeakMs || 0,
    encodePeakMs: +hostPerf.encodePeakMs || 0,
    controlSendWaitPeakMs: +hostPerf.controlSendWaitPeakMs || 0,
    controlSendPeakMs: +hostPerf.controlSendPeakMs || 0,
    sendWaitPeakMs: +hostPerf.sendWaitPeakMs || 0,
    sendPeakMs: +hostPerf.sendPeakMs || 0,
    hostQueuePeak: +hostPerf.hostQueuePeak || 0,
    framePeakBytes: +hostPerf.framePeakBytes || 0,
    videoKbps: +hostPerf.videoKbps || 0,
    videoFrames: +hostPerf.videoFrames || 0,
    targetVideoBitrateKbps: +hostPerf.targetVideoBitrateKbps || 0,
    appliedVideoBitrateKbps: +hostPerf.appliedVideoBitrateKbps || 0,
    pacedFrameSeen: !!hostPerf.pacedFrameSeen,
    decoderQueue: getStreamBacklog(),
    paintPeakMs: +clientPerf?.paintPeakMs || 0,
    paintGapPeakMs: +clientPerf?.paintGapPeakMs || 0,
    browserEventLoopPeakMs: +clientPerf?.eventLoopPeakMs || 0,
    browserLongTaskPeakMs: +clientPerf?.longTaskPeakMs || 0,
    hostTimerPeakMs: +hostPerf.hostTimerPeakMs || 0,
    processCpuPercent: +hostPerf.processCpuPercent || 0,
    systemCpuPercent: +hostPerf.systemCpuPercent || 0,
    threadPoolQueue: +hostPerf.threadPoolQueue || 0,
    threadPoolThreads: +hostPerf.threadPoolThreads || 0,
    keyframeSeen: !!hostPerf.keyframeSeen,
    gc0: gc.gc0 || 0,
    gc1: gc.gc1 || 0,
    gc2: gc.gc2 || 0,
    codec: streamMode,
    fps: streamFps,
  };
  fetch('/api/diagnostics/spike', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(report),
    keepalive: true,
  }).catch(() => {});
}

function applyStreamSettings(fps, quality, force = false) {
  fps = Math.max(1, Math.min(60, +fps || streamFps));
  quality = Math.max(10, Math.min(95, +quality || streamQuality));
  if (force || fps !== streamFps) send({ t: 'fps', v: fps });
  // JPEG quality is live. H.264 quality is controlled separately as a percentage
  // of the bitrate configured by the host.
  if (streamMode === 'jpeg' && (force || quality !== streamQuality))
    send({ t: 'quality', v: quality });
  streamFps = fps;
  streamQuality = quality;
  updateAdaptiveUi();
}

function applyVideoBitratePercent(percent, force = false) {
  percent = Math.max(25, Math.min(100, Math.round(+percent || 100)));
  if (force || percent !== streamBitratePercent) send({ t: 'bitrate', v: percent });
  streamBitratePercent = percent;
}

function applyAdaptiveLevel(force = false) {
  if (!adaptiveEnabled) return;
  const level = adaptiveLevels[adaptiveLevel];
  applyStreamSettings(level.fps, level.quality, force);
  applyVideoBitratePercent(level.bitratePercent, force);
}

function setAdaptiveEnabled(enabled) {
  adaptiveEnabled = !!enabled;
  adaptiveGoodSince = 0;
  adaptiveLastChange = performance.now();
  updateAdaptiveUi();
  if (adaptiveEnabled) applyAdaptiveLevel(true);
  else applyVideoBitratePercent(100, true);
}

function toggleAdaptive() {
  setAdaptiveEnabled(!adaptiveEnabled);
}

function noteAdaptiveSample(rtt) {
  lastHealthRtt = rtt;
  if (!adaptiveEnabled || document.visibilityState !== 'visible') {
    updateHealthUi();
    return;
  }

  const now = performance.now();
  adaptiveLatency = adaptiveLatency ? adaptiveLatency * 0.75 + rtt * 0.25 : rtt;
  const backlog = getStreamBacklog();
  const hostBacklog = +hostPerf?.hostQueuePeak || 0;
  const bad = rtt > 120 || adaptiveLatency > 90 || backlog > 2 || hostBacklog > 1;
  const good = rtt < 60 && adaptiveLatency < 50 && backlog <= 1 && hostBacklog <= 1;

  if (bad && adaptiveLevel < adaptiveLevels.length - 1 && now - adaptiveLastChange > 2500) {
    adaptiveLevel++;
    adaptiveLastChange = now;
    adaptiveGoodSince = 0;
    applyAdaptiveLevel();
    return;
  }

  if (good) {
    if (!adaptiveGoodSince) adaptiveGoodSince = now;
    if (adaptiveLevel > 0 && now - adaptiveGoodSince > 30000 && now - adaptiveLastChange > 15000) {
      adaptiveLevel--;
      adaptiveLastChange = now;
      adaptiveGoodSince = 0;
      applyAdaptiveLevel();
      return;
    }
  } else {
    adaptiveGoodSince = 0;
  }

  updateAdaptiveUi();
}

// ---------------- controls ----------------
function send(o) {
  const socket = controlSocket();
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(o));
}
function setMonitor() { send({ t: 'monitor', v: +document.getElementById('mon').value }); }
function setQuality(manual = true) {
  const quality = +document.getElementById('q').value;
  if (manual) adaptiveEnabled = false;
  applyStreamSettings(streamFps, quality);
  if (manual && streamMode === 'h264')
    applyVideoBitratePercent(25 + (quality - 10) * 75 / 85);
}
function setFps(manual = true) {
  if (manual) {
    adaptiveEnabled = false;
    applyVideoBitratePercent(100);
  }
  applyStreamSettings(+document.getElementById('fps').value, streamQuality);
}
function startPing()  {
  clearInterval(pingTimer);
  pingTimer = setInterval(() => {
    const now = performance.now();
    if (document.visibilityState === 'visible'
        && ws?.readyState === WebSocket.OPEN
        && now - lastPongAt > 6000) {
      try { ws.close(); } catch (e) {}
      scheduleReconnect();
      return;
    }
    send({ t: 'ping', ts: now });
  }, 1000);
}

function startNetworkPing() {
  stopNetworkPing();
  sampleNetworkRtt();
  networkPingTimer = setInterval(sampleNetworkRtt, 1000);
}

function stopNetworkPing() {
  clearInterval(networkPingTimer);
  networkPingTimer = 0;
  networkPingRunning = false;
}

async function sampleNetworkRtt() {
  if (networkPingRunning
      || manualDisconnect
      || document.visibilityState !== 'visible'
      || navigator.onLine === false)
    return;

  networkPingRunning = true;
  const startedAt = performance.now();
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 3000);
  try {
    const response = await fetch('/api/ping', { cache: 'no-store', signal: controller.signal });
    if (response.ok) {
      networkRttAt = performance.now();
      networkRtt = networkRttAt - startedAt;
    }
  } catch (e) {
    // The WebSocket reconnect state remains authoritative for connection failures.
  } finally {
    clearTimeout(timeout);
    networkPingRunning = false;
    updateHealthUi();
  }
}

// ---------------- keep phone awake ----------------
// Supported browsers can keep the screen awake while the viewer is active. The
// lock is automatically released by the browser when the page is hidden, so we
// ask again when the tab becomes visible.
let wakeLock = null;
let wakeLockWanted = false;
async function requestWakeLock() {
  wakeLockWanted = true;
  if (wakeLock || document.visibilityState !== 'visible' || !navigator.wakeLock) return;
  try {
    wakeLock = await navigator.wakeLock.request('screen');
    wakeLock.addEventListener('release', () => { wakeLock = null; });
  } catch (e) {
    wakeLock = null;
  }
}
function releaseWakeLock() {
  wakeLockWanted = false;
  if (!wakeLock) return;
  const lock = wakeLock;
  wakeLock = null;
  lock.release().catch(() => {});
}
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') {
    clearTimeout(idlePauseTimer);
    if (wakeLockWanted) requestWakeLock();
    if (controlToken && ws?.readyState === WebSocket.OPEN) connectControl(controlToken);
    resumeViewer();
  } else {
    clearTimeout(idlePauseTimer);
    setStatus('paused', 'wait');
    idlePauseTimer = setTimeout(() => {
      if (document.visibilityState !== 'visible' && ws?.readyState === WebSocket.OPEN) {
        try { ws.close(1000, 'paused'); } catch (e) {}
      }
    }, 3000);
  }
});
window.addEventListener('online', () => {
  if (manualDisconnect) return;
  clearTimeout(reconnectTimer);
  setStatus('resuming…', 'wait');
  if (controlToken && ws?.readyState === WebSocket.OPEN) connectControl(controlToken);
  resumeViewer();
});
window.addEventListener('offline', () => {
  if (manualDisconnect) return;
  clearTimeout(reconnectTimer);
  setStatus('offline', 'wait');
  if (ws?.readyState === WebSocket.OPEN) {
    try { ws.close(1000, 'offline'); } catch (e) {}
  }
});
window.addEventListener('pageshow', event => {
  if (event.persisted && !manualDisconnect) resumeViewer();
});
window.addEventListener('pagehide', event => {
  if (!event.persisted || manualDisconnect) return;
  clearTimeout(reconnectTimer);
  clearTimeout(idlePauseTimer);
  if (ws?.readyState === WebSocket.OPEN) {
    try { ws.close(1000, 'page cached'); } catch (e) {}
  }
});
['pointerdown', 'keydown', 'touchstart'].forEach(eventName => {
  window.addEventListener(eventName, () => {
    if (wakeLockWanted && !wakeLock) requestWakeLock();
  }, { passive: true });
});

// ---------------- coordinate mapping ----------------
function normXY(clientX, clientY) {
  const r = cv.getBoundingClientRect();
  return {
    x: Math.min(1, Math.max(0, (clientX - r.left) / r.width)),
    y: Math.min(1, Math.max(0, (clientY - r.top) / r.height)),
  };
}
const norm = e => normXY(e.clientX, e.clientY);
// Cursor position in normalised screen coords (0..1). The phone drives this like a
// trackpad: DRAG moves it relatively (re-grippable), HOLD-still jumps it to that spot.
let curX = 0.5, curY = 0.5;
const HOLD_MS = 1400;
const MOVE_SEND_MS = 33;
let pendingMove = null, moveFlushTimer = 0, lastMoveSentAt = 0;

function queueMove(x, y) {
  if (!beginPointerAction()) return false;
  curX = clamp01(x);
  curY = clamp01(y);
  setRemoteCursor(curX, curY, true);
  pendingMove = { x: curX, y: curY };
  scheduleMoveFlush();
  return true;
}

function scheduleMoveFlush() {
  if (moveFlushTimer) return;
  const wait = Math.max(0, MOVE_SEND_MS - (performance.now() - lastMoveSentAt));
  moveFlushTimer = setTimeout(flushPendingMove, wait);
}

function flushPendingMove() {
  if (moveFlushTimer) {
    clearTimeout(moveFlushTimer);
    moveFlushTimer = 0;
  }
  if (!pendingMove) return;
  const move = pendingMove;
  pendingMove = null;
  lastMoveSentAt = performance.now();
  sendPointerMove(move.x, move.y);
}

function sendPointerMove(x, y) {
  const socket = controlSocket();
  if (!socket || socket.readyState !== WebSocket.OPEN) return;
  const packet = new ArrayBuffer(5);
  const view = new DataView(packet);
  view.setUint8(0, BinaryMessageType.PointerMove);
  view.setUint16(1, Math.round(clamp01(x) * 65535), true);
  view.setUint16(3, Math.round(clamp01(y) * 65535), true);
  socket.send(packet);
}

// Block the browser's own pinch/zoom of the page (iOS ignores user-scalable=no),
// so two-finger gestures drive only our canvas zoom.
['gesturestart', 'gesturechange', 'gestureend'].forEach(ev =>
  document.addEventListener(ev, e => e.preventDefault(), { passive: false }));

// ---------------- mouse input (desktop) ----------------
const BTN = ['left', 'middle', 'right'];
cv.addEventListener('mousemove', e => {
  const n = norm(e);
  queueMove(n.x, n.y);
});
cv.addEventListener('mousedown', e => {
  const n = norm(e);
  if (!queueMove(n.x, n.y)) return;
  flushPendingMove();
  send({ t: 'btn', b: BTN[e.button] || 'left', d: true });
});
cv.addEventListener('mouseup', e => {
  const n = norm(e);
  queueMove(n.x, n.y);
  flushPendingMove();
  send({ t: 'btn', b: BTN[e.button] || 'left', d: false });
});
cv.addEventListener('contextmenu', e => e.preventDefault());
cv.addEventListener('wheel', e => {
  e.preventDefault();
  if (!beginPointerAction()) return;
  flushPendingMove();
  send({ t: 'scroll', delta: e.deltaY > 0 ? -1 : 1 });
}, { passive: false });

// ---------------- touch input (trackpad model) ----------------
// 1 finger: DRAG moves the cursor relatively — lift and re-place your finger anywhere
// to keep going, like a laptop trackpad, so you can reach the whole screen. HOLD still
// ~1.4s jumps the cursor to that spot. Clicks come from the pad. 2 fingers = pinch-zoom
// + pan. Double-tap = fit.
let twoFinger = null, lastTapT = 0, lastTapX = 0, lastTapY = 0;
let drag = null, holdTimer = 0;
const dist = (a, b) => Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
const touchById = (touches, id) => {
  for (let i = 0; i < touches.length; i++)
    if (touches[i].identifier === id) return touches[i];
  return null;
};

function anchorDrag(touch, moved) {
  drag = {
    id: touch.identifier,
    x: touch.clientX,
    y: touch.clientY,
    sx: touch.clientX,
    sy: touch.clientY,
    moved,
  };
}

function beginPinch(touches) {
  if (touches.length < 2) return;
  const a = touches[0], b = touches[1];
  const r = stage.getBoundingClientRect();
  twoFinger = {
    a: a.identifier,
    b: b.identifier,
    d: dist(a, b),
    cx: (a.clientX + b.clientX) / 2 - r.left,
    cy: (a.clientY + b.clientY) / 2 - r.top,
  };
  drag = null;
  lastTapT = 0;
  clearTimeout(holdTimer);
}

function trackedPinchTouches(touches) {
  if (!twoFinger) return null;
  const a = touchById(touches, twoFinger.a);
  const b = touchById(touches, twoFinger.b);
  return a && b ? { a, b } : null;
}

cv.addEventListener('touchstart', e => {
  e.preventDefault();
  if (e.touches.length === 1) {
    const t = e.touches[0], now = performance.now();
    if (now - lastTapT < 300 && Math.hypot(t.clientX - lastTapX, t.clientY - lastTapY) < 30) {
      fitView(); lastTapT = 0; drag = null; clearTimeout(holdTimer); return;   // double-tap → fit
    }
    lastTapT = now; lastTapX = t.clientX; lastTapY = t.clientY;
    // Begin a possible drag/hold WITHOUT moving the cursor (so re-gripping doesn't jump it).
    anchorDrag(t, false);
    const dragId = t.identifier;
    clearTimeout(holdTimer);
    holdTimer = setTimeout(() => {
      if (drag && drag.id === dragId && !drag.moved) { // held still → jump cursor to the poke
        const r = cv.getBoundingClientRect();
        if (!queueMove((drag.sx - r.left) / r.width, (drag.sy - r.top) / r.height)) return;
        flushPendingMove();
        drag.moved = true;                         // further motion continues relatively
      }
    }, HOLD_MS);
  } else if (e.touches.length > 1) {
    // Commit the last one-finger position before switching gesture modes. The
    // remaining finger can become a fresh relative-drag anchor when zoom ends.
    if (!twoFinger) flushPendingMove();
    if (!trackedPinchTouches(e.touches)) beginPinch(e.touches);
  }
}, { passive: false });

cv.addEventListener('touchmove', e => {
  e.preventDefault();
  if (e.touches.length === 1) {
    let t = drag ? touchById(e.touches, drag.id) : null;
    if (!t) {
      // A browser may replace a touch identifier during a gesture interruption.
      // Re-anchor rather than applying a large delta from the previous finger.
      t = e.touches[0];
      anchorDrag(t, true);
      return;
    }
    if (!drag.moved && Math.hypot(t.clientX - drag.sx, t.clientY - drag.sy) > 8) {
      drag.moved = true; clearTimeout(holdTimer);  // moved = relative drag; cancel teleport
    }
    if (drag.moved) {
      const r = cv.getBoundingClientRect();        // r.width tracks zoom, so motion scales right
      queueMove(curX + (t.clientX - drag.x) / r.width, curY + (t.clientY - drag.y) / r.height);
    }
    drag.x = t.clientX; drag.y = t.clientY;
  } else if (e.touches.length > 1) {
    const pair = trackedPinchTouches(e.touches);
    if (!pair) {
      // One tracked pinch finger disappeared while at least two touches remain.
      // Select a new stable pair and use this event only as its baseline.
      beginPinch(e.touches);
      return;
    }
    const { a, b } = pair;
    const r = stage.getBoundingClientRect();
    const nd = dist(a, b);
    const cx = (a.clientX + b.clientX) / 2 - r.left;
    const cy = (a.clientY + b.clientY) / 2 - r.top;
    // dead-zone: ignore tiny distance changes so a two-finger PAN doesn't drift-zoom
    const ratio = Math.abs(nd - twoFinger.d) > 3 ? nd / twoFinger.d : 1;
    const newScale = Math.max(minScale, Math.min(minScale * 8, scale * ratio));
    const eff = newScale / scale;
    // keep the content point under the centroid fixed (zoom) + follow centroid (pan)
    tx = cx - eff * (twoFinger.cx - tx);
    ty = cy - eff * (twoFinger.cy - ty);
    scale = newScale;
    twoFinger.d = nd; twoFinger.cx = cx; twoFinger.cy = cy;
    clampView(); applyTransform();
  }
}, { passive: false });

function touchSetChanged(e) {
  e.preventDefault();
  if (e.touches.length > 1) {
    if (!trackedPinchTouches(e.touches)) beginPinch(e.touches);
    return;
  }

  if (e.touches.length === 1) {
    // Any 2+ -> 1 transition resumes relative control with the actual remaining
    // touch, regardless of which finger or TouchList index survived.
    twoFinger = null;
    clearTimeout(holdTimer);
    anchorDrag(e.touches[0], true);
    return;
  }

  flushPendingMove();
  twoFinger = null;
  drag = null;
  clearTimeout(holdTimer);
}

cv.addEventListener('touchend', touchSetChanged, { passive: false });
cv.addEventListener('touchcancel', touchSetChanged, { passive: false });

// ---------------- floating control pad ----------------
const pad = document.getElementById('pad');
const padBtn = document.getElementById('padBtn');

// Hold-to-press buttons: down on press, up on release → tap = click, hold = button held
// (hold L, then drag the screen with another finger = click-drag).
function bindButton(el, button) {
  const down = e => {
    e.preventDefault();
    if (!beginPointerAction()) return;
    flushPendingMove();
    send({ t: 'btn', b: button, d: true });
  };
  const up = e => {
    e.preventDefault();
    beginPointerAction();
    flushPendingMove();
    send({ t: 'btn', b: button, d: false });
  };
  el.addEventListener('touchstart', down, { passive: false });
  el.addEventListener('touchend', up, { passive: false });
  el.addEventListener('touchcancel', () => send({ t: 'btn', b: button, d: false }));
  el.addEventListener('mousedown', down);
  el.addEventListener('mouseup', up);
}
bindButton(document.getElementById('btnL'), 'left');
bindButton(document.getElementById('btnR'), 'right');

// Latched left-button "Hold": tap to pin the left button down at the cursor, then
// drag one finger to move/select/drag-a-file, tap again to drop. Survives zoom
// gestures (the button stays down until you tap Drop).
const holdBtn = document.getElementById('btnHold');
let leftLatched = false;
function toggleHold() {
  if (!leftLatched && !beginPointerAction()) return;
  if (leftLatched) beginPointerAction();
  flushPendingMove();
  leftLatched = !leftLatched;
  send({ t: 'btn', b: 'left', d: leftLatched });
  holdBtn.classList.toggle('on', leftLatched);
  holdBtn.textContent = leftLatched ? 'Drop' : 'Hold';
}
holdBtn.addEventListener('touchstart', e => { e.preventDefault(); toggleHold(); }, { passive: false });
holdBtn.addEventListener('mousedown', e => { e.preventDefault(); toggleHold(); });

// Scroll strip: drag up/down to send wheel notches.
const scrollPad = document.getElementById('scrollPad');
let scrollLastY = null;
scrollPad.addEventListener('touchstart', e => { e.preventDefault(); scrollLastY = e.touches[0].clientY; }, { passive: false });
scrollPad.addEventListener('touchmove', e => {
  e.preventDefault();
  const y = e.touches[0].clientY, dY = y - scrollLastY;
  if (Math.abs(dY) > 8) {
    if (beginPointerAction()) {
      flushPendingMove();
      send({ t: 'scroll', delta: dY > 0 ? -1 : 1 });
    }
    scrollLastY = y;
  }
}, { passive: false });
scrollPad.addEventListener('touchend', e => { e.preventDefault(); scrollLastY = null; }, { passive: false });

// Drag the pad by its grip (touch + mouse), clamped to the viewport.
const padGrip = document.getElementById('padGrip');
let padDrag = null;
function gripStart(x, y) { const r = pad.getBoundingClientRect(); padDrag = { dx: x - r.left, dy: y - r.top }; }
function gripMove(x, y) {
  if (!padDrag) return;
  let nx = Math.max(0, Math.min(window.innerWidth - pad.offsetWidth, x - padDrag.dx));
  let ny = Math.max(0, Math.min(window.innerHeight - pad.offsetHeight, y - padDrag.dy));
  pad.style.left = nx + 'px'; pad.style.top = ny + 'px'; pad.style.right = 'auto';
}
padGrip.addEventListener('touchstart', e => { e.preventDefault(); const t = e.touches[0]; gripStart(t.clientX, t.clientY); }, { passive: false });
padGrip.addEventListener('touchmove', e => { e.preventDefault(); const t = e.touches[0]; gripMove(t.clientX, t.clientY); }, { passive: false });
padGrip.addEventListener('touchend', e => { e.preventDefault(); padDrag = null; }, { passive: false });
padGrip.addEventListener('mousedown', e => {
  e.preventDefault(); gripStart(e.clientX, e.clientY);
  const mm = ev => gripMove(ev.clientX, ev.clientY);
  const mu = () => { padDrag = null; document.removeEventListener('mousemove', mm); document.removeEventListener('mouseup', mu); };
  document.addEventListener('mousemove', mm); document.addEventListener('mouseup', mu);
});

function toggleControls() {
  pad.classList.toggle('hidden');
  const visible = !pad.classList.contains('hidden');
  if (visible) clampPanelIntoView(pad);
  padBtn.classList.toggle('on', visible);
}
// Hidden by default on desktop (real mouse); shown on touch.
if (window.matchMedia('(pointer: fine)').matches) pad.classList.add('hidden');
else padBtn.classList.add('on');

// ---------------- keyboard ----------------
const isUi = t => t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA');
function sendKey(vk) { send({ t: 'key', vk, d: true }); send({ t: 'key', vk, d: false }); }

// Physical keyboard (desktop). Skips when a form field (incl. the type-bar) is focused.
window.addEventListener('keydown', e => { if (isUi(e.target)) return; e.preventDefault(); send({ t: 'key', vk: e.keyCode, d: true }); });
window.addEventListener('keyup',   e => { if (isUi(e.target)) return; e.preventDefault(); send({ t: 'key', vk: e.keyCode, d: false }); });

// Custom on-screen keyboard. Keys send to the PC live (like a real keyboard), so
// there's no compose/Send step. Plain characters go as Unicode text; modifier
// combos (Ctrl/Alt/Win + key) go as key combos. Semi-transparent so the desktop
// shows through.
const vkbd = document.getElementById('vkbd');
const vkeys = document.getElementById('vkeys');
const kbdEcho = document.getElementById('kbdEcho');
let kbLayer = 'letters', shiftArmed = false, shiftLock = false;
const mods = { ctrl: false, alt: false, win: false };

// Echo: a local mirror of what the keyboard types, shown above the keys.
let echoBuf = '';
function echoRender() { kbdEcho.textContent = echoBuf; kbdEcho.scrollTop = kbdEcho.scrollHeight; }
function echoAdd(s) { echoBuf = (echoBuf + s).slice(-5000); echoRender(); }
function echoBack() { echoBuf = echoBuf.slice(0, -1); echoRender(); }
function clearEcho() { echoBuf = ''; echoRender(); }

// Keys that auto-repeat while held (hold to delete/move continuously).
const REPEAT = new Set(['back', 'left', 'right', 'up', 'down', 'space']);
let kbRptDelay = 0, kbRptInt = 0;
function stopKbRepeat() { clearTimeout(kbRptDelay); clearInterval(kbRptInt); kbRptDelay = kbRptInt = 0; }

// Echo sync: while the keyboard is open, periodically re-read the PC's focused field
// (when you're not mid-typing) so PC-side edits don't leave stale text on screen.
let lastKeyT = 0, echoSync = 0;

const SPECIAL_VK = { back: 8, enter: 13, space: 32, tab: 9, esc: 27, left: 37, right: 39, up: 38, down: 40 };
const KEY_LABEL = { back: '⌫', enter: '⏎', space: 'space', sym: '?123', abc: 'ABC', ctrl: 'Ctrl', alt: 'Alt', win: 'Win', left: '◀', right: '▶', esc: 'Esc', tab: 'Tab' };
const WIDE = new Set(['shift', 'back', 'sym', 'abc', 'ctrl', 'alt', 'win', 'enter', 'esc', 'tab']);
const LAYERS = {
  letters: [
    ['1','2','3','4','5','6','7','8','9','0'],
    ['q','w','e','r','t','y','u','i','o','p'],
    ['a','s','d','f','g','h','j','k','l'],
    ['shift','z','x','c','v','b','n','m','back'],
    ['sym','ctrl','alt','space','left','right','enter'],
  ],
  symbols: [
    ['1','2','3','4','5','6','7','8','9','0'],
    ['!','@','#','$','%','^','&','*','(',')'],
    ['-','_','=','+','[',']','{','}',';'],
    ['esc',':','\'','"',',','.','/','?','back'],
    ['abc','ctrl','alt','space','tab','left','right','enter'],
  ],
};

function buildKeyboard() {
  stopKbRepeat();
  vkeys.innerHTML = '';
  for (const row of LAYERS[kbLayer]) {
    const r = document.createElement('div'); r.className = 'krow';
    for (const k of row) {
      if (!sessionInfo?.permissions?.canUseSystemKeys && (k === 'ctrl' || k === 'alt' || k === 'win')) continue;
      const b = document.createElement('div');
      let cls = 'key';
      if (WIDE.has(k)) cls += ' wide';
      if (k === 'space') cls += ' space';
      if (((k === 'ctrl' || k === 'alt' || k === 'win') && mods[k]) || (k === 'shift' && (shiftArmed || shiftLock))) cls += ' armed';
      b.className = cls;
      b.textContent = k === 'shift' ? (shiftLock ? '⇪' : '⇧')
        : (k in KEY_LABEL) ? KEY_LABEL[k]
        : (shiftArmed || shiftLock) && /^[a-z]$/.test(k) ? k.toUpperCase() : k;
      const press = () => {
        b.classList.add('down'); if (navigator.vibrate) navigator.vibrate(8); vkPress(k);
        if (REPEAT.has(k) && !anyMod()) {               // hold to repeat (e.g. backspace)
          stopKbRepeat();
          kbRptDelay = setTimeout(() => { kbRptInt = setInterval(() => vkPress(k), 90); }, 400);
        }
      };
      const lift = () => { b.classList.remove('down'); stopKbRepeat(); };
      b.addEventListener('touchstart', e => { e.preventDefault(); press(); }, { passive: false });
      b.addEventListener('touchend', lift);
      b.addEventListener('touchcancel', lift);
      b.addEventListener('mousedown', e => { e.preventDefault(); press(); });
      b.addEventListener('mouseup', lift);
      b.addEventListener('mouseleave', lift);
      r.appendChild(b);
    }
    vkeys.appendChild(r);
  }
}

const charToVk = k => /^[a-z]$/.test(k) ? 65 + k.charCodeAt(0) - 97 : /^[0-9]$/.test(k) ? 48 + (+k) : 0;
const armedModVks = () => { const a = []; if (mods.ctrl) a.push(17); if (mods.alt) a.push(18); if (mods.win) a.push(91); return a; };
const anyMod = () => mods.ctrl || mods.alt || mods.win;
function clearMods() { mods.ctrl = mods.alt = mods.win = false; }

function vkPress(k) {
  lastKeyT = performance.now();   // marks active typing so interval-sync waits its turn
  if (k === 'ctrl' || k === 'alt' || k === 'win') { mods[k] = !mods[k]; buildKeyboard(); return; }
  if (k === 'shift') { if (shiftLock) { shiftLock = false; shiftArmed = false; } else if (shiftArmed) { shiftLock = true; } else { shiftArmed = true; } buildKeyboard(); return; }
  if (k === 'sym') { kbLayer = 'symbols'; buildKeyboard(); return; }
  if (k === 'abc') { kbLayer = 'letters'; buildKeyboard(); return; }

  if (k in SPECIAL_VK) {
    const vk = SPECIAL_VK[k];
    if (anyMod()) { send({ t: 'combo', mods: armedModVks(), key: vk }); clearMods(); buildKeyboard(); }
    else {
      sendKey(vk);
      if (k === 'space') echoAdd(' ');
      else if (k === 'back') echoBack();
      else if (k === 'enter') echoAdd('\n');
      else if (k === 'tab') echoAdd('\t');
    }
    return;
  }
  // character key
  if (anyMod()) {                                  // Ctrl/Alt/Win + key → shortcut
    const vk = charToVk(k);
    if (vk) send({ t: 'combo', mods: armedModVks(), key: vk });
    clearMods();
    if (shiftArmed && !shiftLock) shiftArmed = false;
    buildKeyboard();
    return;
  }
  let ch = k;
  if ((shiftArmed || shiftLock) && /^[a-z]$/.test(k)) ch = k.toUpperCase();
  send({ t: 'text', s: ch });
  echoAdd(ch);
  if (shiftArmed && !shiftLock) { shiftArmed = false; buildKeyboard(); }
}

function toggleKeyboard() {
  if (vkbd.classList.contains('show')) {
    vkbd.classList.remove('show'); kbdBtn.classList.remove('on');
    document.body.classList.remove('kbd-open');
    clearInterval(echoSync); echoSync = 0;
  } else {
    document.getElementById('panel').classList.remove('open');
    buildKeyboard(); vkbd.classList.add('show'); kbdBtn.classList.add('on');
    document.body.classList.add('kbd-open');     // hide gear so it can't overlap the keys
    send({ t: 'getFocusText' });                 // seed echo from the PC's actual focused field
    clearInterval(echoSync);                     // then keep it synced while idle (not mid-typing)
    echoSync = setInterval(() => {
      if (performance.now() - lastKeyT > 500) send({ t: 'getFocusText' });
    }, 900);
  }
}

// ---------------- misc ----------------
function toggleSettings() { document.getElementById('panel').classList.toggle('open'); }
function setFpsBtn(v, manual = true) {
  if (manual) adaptiveEnabled = false;
  applyStreamSettings(v, streamQuality);
}

// ---------------- send file to PC ----------------
const fileInput = document.getElementById('fileInput');
const uploadBox = document.getElementById('upload');
let pendingFile = null;
function pickFile() { document.getElementById('panel').classList.remove('open'); fileInput.value = ''; fileInput.click(); }
function fmtSize(n) { return n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(1) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; }
fileInput.addEventListener('change', () => {
  if (fileInput.files && fileInput.files[0]) {
    pendingFile = fileInput.files[0];
    document.getElementById('uploadName').textContent = `${pendingFile.name} · ${fmtSize(pendingFile.size)}`;
    document.getElementById('uploadStatus').textContent = '';
    uploadBox.classList.add('show');
  }
});
function cancelUpload() { uploadBox.classList.remove('show'); pendingFile = null; fileInput.value = ''; }
async function doUpload(action) {
  if (!pendingFile) return;
  const status = document.getElementById('uploadStatus');
  status.textContent = 'Uploading…';
  try {
    const r = await fetch(`/api/upload?action=${action}&name=${encodeURIComponent(pendingFile.name)}`, {
      method: 'POST', body: pendingFile,
      headers: { 'Content-Type': pendingFile.type || 'application/octet-stream' },
    });
    if (r.ok) { const j = await r.json(); status.textContent = '✓ Sent as ' + j.savedAs; setTimeout(cancelUpload, 1200); }
    else status.textContent = 'Failed (' + r.status + ')';
  } catch (e) { status.textContent = 'Error: ' + e.message; }
  pendingFile = null; fileInput.value = '';
}

function logout() {
  manualDisconnect = true;
  clearTimeout(reconnectTimer);
  clearTimeout(idlePauseTimer);
  stopNetworkPing();
  releaseWakeLock();
  resetVideoRenderer();
  closeControlChannel(true);
  try { ws && ws.close(); } catch (e) {}
  fetch('/api/logout', { method: 'POST' }).finally(() => location.href = '/login.html');
}
function fs() { const el = document.documentElement; (el.requestFullscreen || el.webkitRequestFullscreen || (()=>{})).call(el); }
function sendCAD() { send({ t: 'combo', mods: [17, 18], key: 46 }); }

// ---------------- draggable sys-keys panel (individual keys) ----------------
const syskeys = document.getElementById('syskeys');
const sysBtn = document.getElementById('sysBtn');
// Modifiers LATCH: tap to hold the key down, tap again to release — so you can hold
// Shift/Ctrl and then click files on the pad to multi-select. The rest are one-shot.
const SYS_MODS = { Ctrl: 17, Alt: 18, Shift: 160, Win: 91 };  // Shift = VK_LSHIFT (160): the generic 16 doesn't hold for combining
const SYS_KEYS = { Del: 46, PrtSc: 44, Esc: 27, Tab: 9 };
const sysHeld = {};
function sysMod(label) {
  sysHeld[label] = !sysHeld[label];
  send({ t: 'key', vk: SYS_MODS[label], d: sysHeld[label] });   // keydown to latch, keyup to release
  if (navigator.vibrate) navigator.vibrate(8);
  syskeys.querySelectorAll(`button[data-mod="${label}"]`).forEach(b => b.classList.toggle('held', sysHeld[label]));
}
function sysKey(label) { if (navigator.vibrate) navigator.vibrate(8); sendKey(SYS_KEYS[label]); }
function releaseSysMods() {
  for (const label in SYS_MODS) if (sysHeld[label]) {
    sysHeld[label] = false; send({ t: 'key', vk: SYS_MODS[label], d: false });
    syskeys.querySelectorAll(`button[data-mod="${label}"]`).forEach(b => b.classList.remove('held'));
  }
}
function toggleSysKeys() {
  syskeys.classList.toggle('hidden');
  const hidden = syskeys.classList.contains('hidden');
  if (hidden) releaseSysMods();          // release latched modifiers so none get stuck down
  else clampPanelIntoView(syskeys);      // snap into view (e.g. after a rotation)
  sysBtn.classList.toggle('on', !hidden);
}
function makeDraggable(panel, grip) {
  let d = null;
  const startD = (x, y) => { const r = panel.getBoundingClientRect(); d = { dx: x - r.left, dy: y - r.top }; };
  const moveD = (x, y) => {
    if (!d) return;
    panel.style.left = Math.max(0, Math.min(window.innerWidth - panel.offsetWidth, x - d.dx)) + 'px';
    panel.style.top = Math.max(0, Math.min(window.innerHeight - panel.offsetHeight, y - d.dy)) + 'px';
    panel.style.right = 'auto';
  };
  grip.addEventListener('touchstart', e => { e.preventDefault(); const t = e.touches[0]; startD(t.clientX, t.clientY); }, { passive: false });
  grip.addEventListener('touchmove', e => { e.preventDefault(); const t = e.touches[0]; moveD(t.clientX, t.clientY); }, { passive: false });
  grip.addEventListener('touchend', e => { e.preventDefault(); d = null; }, { passive: false });
  grip.addEventListener('mousedown', e => {
    e.preventDefault(); startD(e.clientX, e.clientY);
    const mm = ev => moveD(ev.clientX, ev.clientY);
    const mu = () => { d = null; document.removeEventListener('mousemove', mm); document.removeEventListener('mouseup', mu); };
    document.addEventListener('mousemove', mm); document.addEventListener('mouseup', mu);
  });
}
makeDraggable(syskeys, document.getElementById('sysGrip'));
// sys-keys starts hidden — opt in from settings

// ---------------- draggable arrow-keys panel (optional) ----------------
const arrows = document.getElementById('arrows');
const arrBtn = document.getElementById('arrBtn');
function toggleArrows() {
  arrows.classList.toggle('hidden');
  const visible = !arrows.classList.contains('hidden');
  if (visible) clampPanelIntoView(arrows);
  arrBtn.classList.toggle('on', visible);
}
// wire a button to a key with press feedback + hold-to-repeat (and combines with any
// latched sys modifier, e.g. Shift held + ↓ = extend selection down).
function wireRepeatKey(btn, vk) {
  let dly = 0, intv = 0;
  const stop = () => { clearTimeout(dly); clearInterval(intv); dly = intv = 0; };
  const press = () => {
    btn.classList.add('down'); if (navigator.vibrate) navigator.vibrate(8); sendKey(vk);
    stop(); dly = setTimeout(() => { intv = setInterval(() => sendKey(vk), 90); }, 400);
  };
  const lift = () => { btn.classList.remove('down'); stop(); };
  btn.addEventListener('touchstart', e => { e.preventDefault(); press(); }, { passive: false });
  btn.addEventListener('touchend', lift);
  btn.addEventListener('touchcancel', lift);
  btn.addEventListener('mousedown', e => { e.preventDefault(); press(); });
  btn.addEventListener('mouseup', lift);
  btn.addEventListener('mouseleave', lift);
}
arrows.querySelectorAll('button[data-vk]').forEach(b => wireRepeatKey(b, +b.dataset.vk));
makeDraggable(arrows, document.getElementById('arrGrip'));

// Keep the floating panels inside the viewport. After a rotation, a panel positioned
// for the old orientation can land off-screen, so re-clamp the visible ones.
function clampPanelIntoView(panel) {
  if (!panel || panel.classList.contains('hidden')) return;
  const r = panel.getBoundingClientRect();
  panel.style.left = Math.max(0, Math.min(window.innerWidth - panel.offsetWidth, r.left)) + 'px';
  panel.style.top = Math.max(0, Math.min(window.innerHeight - panel.offsetHeight, r.top)) + 'px';
  panel.style.right = 'auto';
}
let panelResizeTimer = 0;
window.addEventListener('resize', () => {
  clearTimeout(panelResizeTimer);
  // let the orientation reflow settle (sys-keys re-columns) before clamping
  panelResizeTimer = setTimeout(() => [pad, syskeys, arrows].forEach(clampPanelIntoView), 120);
});

// ---------------- start ----------------
async function start() {
  setStatus('connecting…', 'wait');
  manualDisconnect = false;
  try {
    const r = await fetch('/api/session', { cache: 'no-store' });
    if (r.status === 401 || r.status === 403) { location.href = '/login.html'; return; }
    if (!r.ok) throw new Error(`Session check failed (${r.status})`);
    applySessionInfo(await r.json());
  } catch (e) {
    setStatus(navigator.onLine === false ? 'offline' : 'reconnecting…', 'wait');
    scheduleReconnect();
    return;
  }
  connect();
}
start();
