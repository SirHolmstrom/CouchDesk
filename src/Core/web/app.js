// Remote Desktop LAN — browser viewer client (mouse + touch + on-screen keyboard).

const cv = document.getElementById('screen');
const ctx = cv.getContext('2d');
const stage = document.getElementById('stage');
const statEl = document.getElementById('stat');
const latEl = document.getElementById('lat');
const kbdBtn = document.getElementById('kbdBtn');

let ws, pingTimer;
let queue = [], processing = false;
let sessionInfo = null;
let videoRenderer = null; // set when the server streams hardware H.264 (MediaSource path)

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
}

// ---------------- zoom / pan ----------------
let scale = 1, minScale = 1, tx = 0, ty = 0;
function applyTransform() { cv.style.transform = `translate(${tx}px,${ty}px) scale(${scale})`; }
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

// ---------------- connection ----------------
function connect() {
  ws = new WebSocket(`wss://${location.host}/ws`);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => { setStatus('connected', 'ok'); startPing(); };
  ws.onclose = () => { setStatus('disconnected', 'bad'); clearInterval(pingTimer); };
  ws.onerror = () => { setStatus('error', 'bad'); };
  ws.onmessage = onMessage;
}
function onMessage(ev) {
  if (typeof ev.data === 'string') { handleControl(JSON.parse(ev.data)); return; }
  // Route by leading type byte: 4 = per-frame H.264 (WebCodecs), 3 = fragmented MP4 (MSE),
  // else JPEG tiles.
  if (videoRenderer) {
    const t = new Uint8Array(ev.data, 0, 1)[0];
    if (t === 4) { // [type][flags(bit0=keyframe)][annexB]
      const flags = new Uint8Array(ev.data, 1, 1)[0];
      videoRenderer.decode(new Uint8Array(ev.data, 2), (flags & 1) === 1, performance.now());
      return;
    }
    if (t === 3) { videoRenderer.appendChunk(new Uint8Array(ev.data, 1)); return; }
  }
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
function handleControl(m) {
  switch (m.t) {
    case 'pong': latEl.textContent = Math.round(performance.now() - m.ts) + ' ms'; break;
    case 'monitors': fillMonitors(m.list, m.active); break;
    case 'status': {
      if (m.state === 'video-h264') { // low-latency per-frame H.264 via WebCodecs
        if (!videoRenderer && window.RDVideo && RDVideo.supported) {
          videoRenderer = RDVideo.create(cv, { onResize: fitView, onNeedKeyframe: () => send({ t: 'keyframe' }) });
          setStatus('connected · H.264 (low latency)', 'ok');
        } else if (!window.RDVideo || !RDVideo.supported) {
          setStatus('WebCodecs not supported here', 'bad');
        }
        break;
      }
      if (m.state === 'video') { // fragmented MP4 via MediaSource
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
      setStatus(label, m.state === 'connected' ? 'ok' : 'bad');
      break;
    }
    // real text of the PC's focused field (UI Automation) → seed the echo so it matches reality
    case 'focusText': echoBuf = (m.text || '').slice(-5000); echoRender(); break;
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

// ---------------- controls ----------------
function send(o) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }
function setMonitor() { send({ t: 'monitor', v: +document.getElementById('mon').value }); }
function setQuality() { send({ t: 'quality', v: +document.getElementById('q').value }); }
function setFps()     { send({ t: 'fps',     v: +document.getElementById('fps').value }); }
function startPing()  { clearInterval(pingTimer); pingTimer = setInterval(() => send({ t: 'ping', ts: performance.now() }), 1000); }

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

// Block the browser's own pinch/zoom of the page (iOS ignores user-scalable=no),
// so two-finger gestures drive only our canvas zoom.
['gesturestart', 'gesturechange', 'gestureend'].forEach(ev =>
  document.addEventListener(ev, e => e.preventDefault(), { passive: false }));

// ---------------- mouse input (desktop) ----------------
const BTN = ['left', 'middle', 'right'];
cv.addEventListener('mousemove', e => { const n = norm(e); curX = n.x; curY = n.y; send({ t: 'move', x: n.x, y: n.y }); });
cv.addEventListener('mousedown', e => send({ t: 'btn', b: BTN[e.button] || 'left', d: true }));
cv.addEventListener('mouseup',   e => send({ t: 'btn', b: BTN[e.button] || 'left', d: false }));
cv.addEventListener('contextmenu', e => e.preventDefault());
cv.addEventListener('wheel', e => { e.preventDefault(); send({ t: 'scroll', delta: e.deltaY > 0 ? -1 : 1 }); }, { passive: false });

// ---------------- touch input (trackpad model) ----------------
// 1 finger: DRAG moves the cursor relatively — lift and re-place your finger anywhere
// to keep going, like a laptop trackpad, so you can reach the whole screen. HOLD still
// ~1.4s jumps the cursor to that spot. Clicks come from the pad. 2 fingers = pinch-zoom
// + pan. Double-tap = fit.
let twoFinger = null, multiTouch = false, lastTapT = 0, lastTapX = 0, lastTapY = 0;
let drag = null, holdTimer = 0;
const dist = (a, b) => Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
const clamp01 = v => Math.min(1, Math.max(0, v));

cv.addEventListener('touchstart', e => {
  e.preventDefault();
  if (e.touches.length === 1 && !multiTouch) {
    const t = e.touches[0], now = performance.now();
    if (now - lastTapT < 300 && Math.hypot(t.clientX - lastTapX, t.clientY - lastTapY) < 30) {
      fitView(); lastTapT = 0; drag = null; clearTimeout(holdTimer); return;   // double-tap → fit
    }
    lastTapT = now; lastTapX = t.clientX; lastTapY = t.clientY;
    // Begin a possible drag/hold WITHOUT moving the cursor (so re-gripping doesn't jump it).
    drag = { x: t.clientX, y: t.clientY, sx: t.clientX, sy: t.clientY, moved: false };
    clearTimeout(holdTimer);
    holdTimer = setTimeout(() => {
      if (drag && !drag.moved) {                  // held still → jump cursor to the poke
        const r = cv.getBoundingClientRect();
        curX = clamp01((drag.sx - r.left) / r.width);
        curY = clamp01((drag.sy - r.top) / r.height);
        send({ t: 'move', x: curX, y: curY });
        drag.moved = true;                         // further motion continues relatively
      }
    }, HOLD_MS);
  } else if (e.touches.length === 2) {
    multiTouch = true; drag = null; clearTimeout(holdTimer);
    const r = stage.getBoundingClientRect();
    twoFinger = {
      d: dist(e.touches[0], e.touches[1]),
      cx: (e.touches[0].clientX + e.touches[1].clientX) / 2 - r.left,
      cy: (e.touches[0].clientY + e.touches[1].clientY) / 2 - r.top,
    };
  }
}, { passive: false });

cv.addEventListener('touchmove', e => {
  e.preventDefault();
  if (e.touches.length === 1 && !twoFinger && !multiTouch && drag) {
    const t = e.touches[0];
    if (!drag.moved && Math.hypot(t.clientX - drag.sx, t.clientY - drag.sy) > 8) {
      drag.moved = true; clearTimeout(holdTimer);  // moved = relative drag; cancel teleport
    }
    if (drag.moved) {
      const r = cv.getBoundingClientRect();        // r.width tracks zoom, so motion scales right
      curX = clamp01(curX + (t.clientX - drag.x) / r.width);
      curY = clamp01(curY + (t.clientY - drag.y) / r.height);
      send({ t: 'move', x: curX, y: curY });
    }
    drag.x = t.clientX; drag.y = t.clientY;
  } else if (e.touches.length === 2 && twoFinger) {
    const r = stage.getBoundingClientRect();
    const nd = dist(e.touches[0], e.touches[1]);
    const cx = (e.touches[0].clientX + e.touches[1].clientX) / 2 - r.left;
    const cy = (e.touches[0].clientY + e.touches[1].clientY) / 2 - r.top;
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

// Stop zoom/pan when a finger lifts, but keep state until ALL fingers are up.
cv.addEventListener('touchend', e => {
  e.preventDefault();
  if (e.touches.length < 2) twoFinger = null;
  if (e.touches.length === 0) { multiTouch = false; drag = null; clearTimeout(holdTimer); }
}, { passive: false });
cv.addEventListener('touchcancel', () => { twoFinger = null; multiTouch = false; drag = null; clearTimeout(holdTimer); });

// ---------------- floating control pad ----------------
const pad = document.getElementById('pad');
const padBtn = document.getElementById('padBtn');

// Hold-to-press buttons: down on press, up on release → tap = click, hold = button held
// (hold L, then drag the screen with another finger = click-drag).
function bindButton(el, button) {
  const down = e => { e.preventDefault(); send({ t: 'btn', b: button, d: true }); };
  const up   = e => { e.preventDefault(); send({ t: 'btn', b: button, d: false }); };
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
  if (Math.abs(dY) > 8) { send({ t: 'scroll', delta: dY > 0 ? -1 : 1 }); scrollLastY = y; }
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
function setFpsBtn(v) {
  send({ t: 'fps', v });
  document.querySelectorAll('#panel .seg button').forEach(b => b.classList.toggle('on', +b.dataset.fps === v));
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

function logout() { try { ws && ws.close(); } catch (e) {} fetch('/api/logout', { method: 'POST' }).finally(() => location.href = '/login.html'); }
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
  try {
    const r = await fetch('/api/session', { cache: 'no-store' });
    if (r.status !== 200) { location.href = '/login.html'; return; }
    applySessionInfo(await r.json());
  } catch (e) { location.href = '/login.html'; return; }
  connect();
}
start();
