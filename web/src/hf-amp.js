// SPDX-License-Identifier: GPL-2.0-or-later
// HF LDMOS amplifier panel. Plain createElement (no JSX, no bundler dependencies).
// `react` is provided by the Zeus host; styles are inlined by build.mjs.
import * as ReactNS from 'react';
// Works whether the host exposes React as named exports, a default export, or both.
const React = ReactNS.default ?? ReactNS;

const h = React.createElement;
const STYLES = /*__STYLES__*/"";
const ROOT = 'com-n1mjf-hfamp';
const POLL_MS = 250;

function ensureStyles() {
  if (document.getElementById(`${ROOT}-styles`)) return;
  const el = document.createElement('style');
  el.id = `${ROOT}-styles`;
  el.textContent = STYLES;
  document.head.appendChild(el);
}

// callBackend resolves to a Response object (not parsed JSON); parse it and surface HTTP errors.
async function call(api, method, path, body) {
  const res = await api.callBackend(method, path, body);
  let data = null;
  try { data = await res.json(); } catch { /* empty or non-JSON body */ }
  return { ok: !!res.ok, status: res.status, data };
}

const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? '—' : Number(v).toFixed(d));

const HOLD_MS = 5000;
const REMIND_MS = 30000;

// Meter ranges and colour zones. Every value can be changed in Settings (this is for any DIY LDMOS amp).
// Green is below `warn`, yellow from `warn`, red from `bad`. Defaults are the RZ1ZR C5MAX owner's limits;
// current and temperature are derived values (see README). `zones: false` meters have a range only.
const METER_DEFS = [
  { key: 'Output',     title: 'Output',     unit: 'W',  step: 1,   d: { min: 0, max: 1300, warn: 600,  bad: 1200 } },
  { key: 'Reflected',  title: 'Reflected',  unit: 'W',  step: 1,   d: { min: 0, max: 100 }, zones: false, note: 'Coloured by SWR' },
  { key: 'Input',      title: 'Input',      unit: 'W',  step: 0.1, d: { min: 0, max: 15,   warn: 3,    bad: 5 } },
  { key: 'Current',    title: 'Current',    unit: 'A',  step: 0.5, d: { min: 0, max: 40,   warn: 30,   bad: 38 } },
  { key: 'SWR',        title: 'SWR',        unit: ':1', step: 0.1, d: { min: 1, max: 3,    warn: 1.7,  bad: 2.5 }, strict: true },
  { key: 'Efficiency', title: 'Efficiency', unit: '%',  step: 1,   d: { min: 0, max: 100 }, zones: false },
  { key: 'Supply',     title: 'Supply',     unit: 'V',  step: 0.1, d: { min: 40, max: 60,  warn: 48.4, bad: 53 } },
  { key: 'Temp',       title: 'Temp',       unit: '°C', step: 1,   d: { min: 0, max: 100,  warn: 60,   bad: 75 } },
];
const DEFAULT_PREFS = {
  peakHold: false,
  tempF: false,
  chime: { on: true, volume: 0.5, repeat: false },
  meters: Object.fromEntries(METER_DEFS.map((m) => [m.key, { ...m.d }])),
};
// The firmware's top preset is 58.25 V; Helios DX and the R4875 front end call it 58.3.
const supplyLabel = (v) => (v === 58.25 ? '58.3' : String(v));
const isNum = (v) => typeof v === 'number' && Number.isFinite(v);

// Merge whatever was stored onto the defaults, dropping anything malformed.
function normalizePrefs(raw) {
  const r = raw && typeof raw === 'object' ? raw : {};
  const meters = {};
  for (const m of METER_DEFS) {
    const src = r.meters?.[m.key] ?? {};
    const out = { ...m.d };
    for (const f of Object.keys(m.d)) if (isNum(src[f])) out[f] = src[f];
    meters[m.key] = validateMeter(m, out) ? out : { ...m.d };
  }
  const c = r.chime ?? {};
  return {
    peakHold: !!r.peakHold,
    sections: Object.fromEntries(['controls'].filter((k) => typeof r.sections?.[k] === 'boolean').map((k) => [k, r.sections[k]])),
    tempF: !!r.tempF,
    chime: {
      on: c.on === undefined ? true : !!c.on,
      volume: isNum(c.volume) ? Math.max(0, Math.min(1, c.volume)) : 0.5,
      repeat: !!c.repeat,
    },
    meters,
  };
}

// Returns an error string, or null when the range and zones make sense.
// Temperature arrives in °C; Fahrenheit is a display choice (ranges stay stored in °C).
const toF = (c) => c * 9 / 5 + 32;
function tempMeter(m, fahrenheit) {
  if (!fahrenheit) return m;
  return { ...m, min: toF(m.min), max: toF(m.max), warn: toF(m.warn), bad: toF(m.bad) };
}

function validateMeter(m, v) {
  if (!(v.min < v.max)) return 'Minimum must be below maximum';
  if (m.zones !== false) {
    if (!(v.warn <= v.bad)) return 'Yellow must not be above red';
  }
  return null;
}

// Shared by the panel and the meters-pane hook, so both see the same ranges. Persisted by the backend
// (survives restarts, shared by every browser). No browser storage is used.
const prefsStore = { value: normalizePrefs({}), listeners: new Set(), loading: null, timer: 0 };
function setPrefsLocal(v) {
  prefsStore.value = v;
  prefsStore.listeners.forEach((f) => f());
}
function loadPrefs(api) {
  prefsStore.loading ??= (async () => {
    try {
      const r = await call(api, 'GET', '/prefs');
      if (r.ok && r.data && Object.keys(r.data).length) setPrefsLocal(normalizePrefs(r.data));
    } catch { prefsStore.loading = null; /* retry on the next call */ }
  })();
  return prefsStore.loading;
}
// Saves now, or after a short pause for sliders that fire continuously.
function savePrefs(api, v, { delay = 0 } = {}) {
  setPrefsLocal(v);
  clearTimeout(prefsStore.timer);
  const send = async () => {
    try {
      const r = await call(api, 'POST', '/prefs', v);
      return { ok: r.ok, message: r.data?.message || (r.ok ? 'Saved' : `Save failed (HTTP ${r.status})`) };
    } catch (e) {
      return { ok: false, message: e?.message || 'Save failed' };
    }
  };
  if (delay <= 0) return send();
  prefsStore.timer = setTimeout(send, delay);
  return Promise.resolve({ ok: true, message: '' });
}
function usePrefs() {
  const [, bump] = React.useState(0);
  React.useEffect(() => {
    const f = () => bump((n) => n + 1);
    prefsStore.listeners.add(f);
    return () => prefsStore.listeners.delete(f);
  }, []);
  return prefsStore.value;
}

function zoneOf(value, z, strict) {
  if (value == null || !z || z.warn == null) return null;
  const over = (limit) => (strict ? value > limit : value >= limit);
  return over(z.bad) ? 'bad' : over(z.warn) ? 'warn' : 'ok';
}

// ---- Fault chime: two soft sine notes with a gentle decay. Browsers only allow sound after the person has
// clicked or pressed a key in the page once; after that, resuming the audio context works at any time.
let audioCtx = null;
function audioContext() {
  const AC = window.AudioContext || window.webkitAudioContext;
  if (!AC) return null;
  try { audioCtx ??= new AC(); } catch { return null; }
  return audioCtx;
}
function audioState() {
  if (!(window.AudioContext || window.webkitAudioContext)) return 'unsupported';
  const active = navigator.userActivation?.hasBeenActive;
  return audioCtx?.state === 'running' || active ? 'ready' : 'locked';
}
async function playChime(volume) {
  const ctx = audioContext();
  if (!ctx) return false;
  if (ctx.state !== 'running') { try { await ctx.resume(); } catch { /* blocked */ } }
  if (ctx.state !== 'running') return false;
  const t0 = ctx.currentTime + 0.02;
  const peak = 0.22 * Math.max(0, Math.min(1, volume));   // never louder than a quarter of full scale
  if (peak <= 0) return true;
  [[784, 0], [587.33, 0.32]].forEach(([freq, at]) => {    // G5 then D5: falling, soft, not shrill
    for (const [mult, level] of [[1, 1], [2, 0.18]]) {    // a faint octave adds a bell-like warmth
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = freq * mult;
      const t = t0 + at;
      gain.gain.setValueAtTime(0.0001, t);
      gain.gain.exponentialRampToValueAtTime(peak * level, t + 0.03);
      gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.85);
      osc.connect(gain).connect(ctx.destination);
      osc.start(t);
      osc.stop(t + 0.9);
    }
  });
  return true;
}

// Edge-detects faults from any status feed (panel poll or meters-pane hook). One chime when a fault
// starts, optionally a reminder every 30 s while it lasts. A fault already present when we first look
// is shown but not announced.
const faultMonitor = { active: false, seen: false, last: 0 };
function feedFaults(status, chime) {
  const amp = status?.amp;
  if (status?.connection?.state !== 'connected' || !amp) return;
  const fault = !!amp.fault;
  const now = Date.now();
  if (fault && chime.on) {
    if (!faultMonitor.active && faultMonitor.seen) { playChime(chime.volume); faultMonitor.last = now; }
    else if (faultMonitor.active && chime.repeat && now - faultMonitor.last >= REMIND_MS) { playChime(chime.volume); faultMonitor.last = now; }
  }
  faultMonitor.active = fault;
  faultMonitor.seen = true;
}

const ZONE_WORD = { warn: 'Caution', bad: 'Danger' };
const pctOf = (v, min, max) => (v == null ? 0 : Math.max(0, Math.min(100, ((v - min) / (max - min)) * 100)));

// One meter row: label + value on top, an LED-style bar below, optional held-peak marker.
// With `zones` the whole scale shows its green/yellow/red bands (dim) and the lit part is coloured by where it
// reaches, like the host's own meters. Without them the bar is one colour (zone class, grey when stale, else accent).
function Meter({ label, value, unit, min = 0, max, digits, live, zone, zones, peak }) {
  const pct = pctOf(value, min, max);
  const z = live ? zone : null;
  const peakPct = peak == null ? null : pctOf(peak, min, max);
  const word = ZONE_WORD[z];
  const banded = !!zones && live;
  const style = banded ? { '--wp': `${pctOf(zones.warn, min, max)}%`, '--bp': `${pctOf(zones.bad, min, max)}%` } : undefined;
  const badPct = banded ? pctOf(zones.bad, min, max) : null;
  return h('div', { className: 'hfa-meter' },
    h('div', { className: 'hfa-meter-head' },
      h('span', { className: 'hfa-label' }, label),
      word ? h('span', { className: `hfa-zone ${z}` }, word) : null,
      h('span', { className: 'val' }, fmt(value, digits), h('span', { className: 'unit' }, unit))),
    h('div', {
      className: `hfa-bar${live ? '' : ' stale'}${banded ? ' banded' : z ? ` z-${z}` : ''}`, style, role: 'meter', 'aria-label': label,
      'aria-valuemin': min, 'aria-valuemax': max, 'aria-valuenow': value == null ? min : Number(value.toFixed(2)),
      'aria-valuetext': value == null ? 'no reading' : `${fmt(value, digits)} ${unit}${word ? `, ${word.toLowerCase()}` : ''}`,
    },
      h('i', { style: { clipPath: `inset(0 ${100 - pct}% 0 0)` } }),
      badPct != null && badPct > 0 && badPct < 100 ? h('em', { className: 'hfa-notch', style: { left: `${badPct}%` } }) : null,
      pct > 0 ? h('s', { className: 'hfa-tip', style: { left: `${pct}%` } }) : null,
      peakPct != null && peakPct > pct + 0.5
        ? h('b', { className: 'hfa-peak', style: { left: `${peakPct}%` }, title: `Peak ${fmt(peak, digits)} ${unit}` })
        : null));
}

function Read({ k, v, zone, big }) {
  return h('div', { className: `hfa-read${big ? ' big' : ''}${zone ? ` z-${zone}` : ''}` },
    h('span', { className: 'hfa-label' }, k, ZONE_WORD[zone] ? h('span', { className: `hfa-zone ${zone}` }, ZONE_WORD[zone]) : null),
    h('span', { className: 'v' }, v));
}

// What to look at for each fault code (code 3 is the high-SWR fault; names come from the backend).
const FAULT_HINTS = {
  1: 'the drive level from the radio', 2: 'the drive level and output power', 3: 'the antenna and SWR',
  4: 'the low-pass filter', 5: 'the drain current and drive level', 6: 'the supply voltage', 7: 'the cooling and heatsink',
};

function Warn() {
  return h('svg', {
    width: 20, height: 20, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2.2,
    strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': 'true', focusable: 'false', className: 'hfa-fault-icon',
  }, h('path', { d: 'M12 3 2.5 20h19L12 3z' }), h('path', { d: 'M12 10v4.5' }), h('path', { d: 'M12 17.6v.1' }));
}

function Gear() {
  const spokes = Array.from({ length: 8 }, (_, i) => {
    const a = (i * Math.PI) / 4;
    return h('line', {
      key: i, x1: 12 + 7.5 * Math.cos(a), y1: 12 + 7.5 * Math.sin(a),
      x2: 12 + 10 * Math.cos(a), y2: 12 + 10 * Math.sin(a),
    });
  });
  return h('svg', {
    width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
    strokeWidth: 2.4, strokeLinecap: 'round', 'aria-hidden': 'true', focusable: 'false',
  }, h('circle', { cx: 12, cy: 12, r: 6 }), h('circle', { cx: 12, cy: 12, r: 2 }), spokes);
}

// A card with a button header that collapses its body.
function Section({ id, title, summary, open, onToggle, children }) {
  const expanded = !!open;
  const bodyId = `${ROOT}-${id}`;
  return h('section', { className: `hfa-card hfa-section${expanded ? '' : ' collapsed'}` },
    h('button', {
      type: 'button', className: 'hfa-toggle', 'aria-expanded': expanded, 'aria-controls': bodyId, onClick: onToggle,
    },
      h('span', { className: 'hfa-chev', 'aria-hidden': 'true' }),
      h('span', { className: 'hfa-label' }, title),
      !expanded && summary ? h('span', { className: 'hfa-summary' }, summary) : null),
    expanded ? h('div', { id: bodyId, className: 'hfa-body' }, children) : null);
}

// Values whose peaks are held (those that spike during TX).
function peakInputs(s) {
  const m = s?.meters;
  if (!m?.fresh) return {};
  return { Output: m.forwardW, Reflected: m.reflectedW, Input: m.inputW, Current: m.currentA, SWR: m.swr };
}

const toDraft = (meters) => Object.fromEntries(Object.entries(meters).map(([k, v]) =>
  [k, Object.fromEntries(Object.entries(v).map(([f, x]) => [f, String(x)]))]));
const fromDraft = (d) => Object.fromEntries(Object.entries(d).map(([f, x]) => [f, x === '' ? NaN : Number(x)]));

// Row in Settings: one meter's range and colour thresholds, edited as text and applied on Save.
function RangeRow({ def, draft, error, onChange }) {
  const zoned = def.zones !== false;
  const n = (v) => (v === '' || v == null ? NaN : Number(v));
  const lo = n(draft.min), hi = n(draft.max), w = n(draft.warn), b = n(draft.bad);
  const ok = Number.isFinite(lo) && Number.isFinite(hi) && hi > lo;
  const p = (v) => (ok ? pctOf(v, lo, hi) : 0);
  const field = (f, label) => h('label', { className: 'hfa-num' },
    h('span', { className: 'hfa-label' }, label),
    h('input', {
      type: 'number', inputMode: 'decimal', step: def.step, value: draft[f] ?? '',
      'aria-label': `${def.title} ${label.toLowerCase()} (${def.unit})`, 'aria-invalid': !!error,
      onChange: (e) => onChange(f, e.target.value),
    }));
  return h('div', { className: 'hfa-range' },
    h('div', { className: 'hfa-range-head' },
      h('span', { className: 'hfa-range-name' }, def.title, h('span', { className: 'unit' }, ` ${def.unit}`)),
      def.note ? h('span', { className: 'hfa-note' }, def.note) : null),
    h('div', { className: 'hfa-range-fields' },
      field('min', 'Min'), field('max', 'Max'),
      zoned ? field('warn', 'Yellow from') : null,
      zoned ? field('bad', 'Red from') : null),
    zoned && ok && Number.isFinite(w) && Number.isFinite(b)
      ? h('div', {
        className: 'hfa-strip', 'aria-hidden': 'true',
        style: { background: `linear-gradient(90deg, var(--ok) 0 ${p(w)}%, var(--amber) ${p(w)}% ${p(b)}%, var(--tx) ${p(b)}% 100%)` },
      })
      : null,
    error ? h('p', { className: 'hfa-msg err', role: 'alert' }, `${def.title}: ${error}`) : null);
}

function Panel({ api }) {
  const [status, setStatus] = React.useState(null);
  const [loadError, setLoadError] = React.useState(null);
  const [msg, setMsg] = React.useState({ text: '', err: false });
  const [hostDraft, setHostDraft] = React.useState('');
  const hostTouched = React.useRef(false);
  const prefs = usePrefs();
  const [draft, setDraft] = React.useState(null);              // unsaved range edits, keyed by meter
  const [rangeMsg, setRangeMsg] = React.useState({ text: '', err: false });
  const [view, setView] = React.useState('main');            // 'main' | 'settings'
  const peakHold = prefs.peakHold;
  const peakHoldRef = React.useRef(peakHold);
  const peaks = React.useRef({});
  const gearRef = React.useRef(null);
  const hostRef = React.useRef(null);
  const wasSettings = React.useRef(false);

  peakHoldRef.current = peakHold;

  React.useEffect(() => { loadPrefs(api); }, [api]);

  React.useEffect(() => {
    let stop = false;
    let timer = 0;
    const tick = async () => {
      try {
        const r = await call(api, 'GET', '/status');
        if (stop) return;
        if (!r.ok || !r.data?.connection) throw new Error(r.data?.message || `HTTP ${r.status}`);
        const s = r.data;
        feedFaults(s, prefsStore.value.chime);
        // Peak hold: a peak stays for HOLD_MS, then falls to the current value.
        const now = Date.now();
        if (peakHoldRef.current) {
          const vals = peakInputs(s);
          for (const [k, v] of Object.entries(vals)) {
            if (v == null) continue;
            const p = peaks.current[k];
            if (!p || v >= p.v || now - p.t >= HOLD_MS) peaks.current[k] = { v, t: now };
          }
          for (const k of Object.keys(peaks.current)) {
            if (now - peaks.current[k].t >= HOLD_MS && !(k in vals)) delete peaks.current[k];
          }
        } else {
          peaks.current = {};
        }
        setStatus(s);
        setLoadError(null);
        if (!hostTouched.current && s?.connection?.host) setHostDraft(s.connection.host);
      } catch (e) {
        if (!stop) setLoadError(e?.message || 'Cannot reach the amplifier feature');
      }
      if (!stop) timer = setTimeout(tick, POLL_MS);
    };
    tick();
    return () => { stop = true; clearTimeout(timer); };
  }, [api]);

  // Keyboard focus: into the address box when settings open, back to the gear when they close.
  React.useEffect(() => {
    if (view === 'settings') hostRef.current?.focus();
    else if (wasSettings.current) gearRef.current?.focus();
    wasSettings.current = view === 'settings';
  }, [view]);

  const post = async (path, body) => {
    try {
      const r = await call(api, 'POST', path, body);
      setMsg({ text: r.data?.message || (r.ok ? 'Done' : `Request failed (HTTP ${r.status})`), err: !r.ok || r.data?.ok === false });
    } catch (e) {
      setMsg({ text: e?.message || 'Request failed', err: true });
    }
  };
  const cmd = (body) => post('/command', body);
  // Whether the Controls card is expanded is kept with the other display settings.
  const isOpen = (id, dflt) => (prefs.sections[id] === undefined ? dflt : !!prefs.sections[id]);
  const flip = (id, dflt) => () => updatePrefs({ sections: { ...prefs.sections, [id]: !isOpen(id, dflt) } });
  // Toggles and the volume slider save straight away; ranges wait for the Save button.
  const updatePrefs = (patch, opts) => {
    savePrefs(api, { ...prefsStore.value, ...patch }, opts).then((r) => {
      if (!r.ok) setMsg({ text: r.message, err: true });
    });
  };
  const setChime = (patch, opts) => updatePrefs({ chime: { ...prefsStore.value.chime, ...patch } }, opts);

  if (!status) {
    return h('div', { className: ROOT },
      h('div', { className: 'hfa-card' },
        h('h2', { className: 'hfa-title' }, 'HF Amplifier'),
        h('p', { className: 'hfa-empty', role: 'status' },
          loadError ? `Unavailable: ${loadError}` : 'Loading…')));
  }

  const { connection: c, amp, meters, controls, follow } = status;
  const connected = c.state === 'connected';
  const tx = !!amp?.tx;
  const lockTx = tx; // every control is refused by the backend while transmitting, as in Helios DX
  const live = !!meters?.fresh;
  const idleLive = connected && !!amp;
  const unconfigured = !c.host;
  const M = prefs.meters;
  const T = tempMeter(M.Temp, prefs.tempF);
  const swrZone = live ? zoneOf(meters.swr, M.SWR, true) : null;
  const fault = !!amp?.fault;
  const faultText = amp?.faultText || 'Fault';

  const pill = unconfigured ? ['Not configured', '']
    : connected ? (fault ? ['Fault', 'tx'] : tx ? ['Transmitting', 'tx'] : ['Connected', 'ok'])
    : [c.state === 'connecting' ? 'Connecting…' : 'Disconnected', 'warn'];

  // ------------------------------------------------------------------ settings view
  if (view === 'settings') {
    const followLabel = follow.radioBand
      ? `Radio ${fmt(follow.radioHz / 1e6, 3)} MHz → ${controls.bands[follow.radioBand - 1]}`
      : follow.radioHz ? `Radio ${fmt(follow.radioHz / 1e6, 3)} MHz (outside amp bands)` : '';
    const draftNow = draft ?? toDraft(prefs.meters);
    const rangeErrors = {};
    for (const def of METER_DEFS) {
      const parsed = fromDraft(draftNow[def.key]);
      const err = Object.values(parsed).some((v) => !Number.isFinite(v)) ? 'Enter a number in every box' : validateMeter(def, parsed);
      if (err) rangeErrors[def.key] = err;
    }
    const saveRanges = async () => {
      const meters = {};
      for (const def of METER_DEFS) meters[def.key] = fromDraft(draftNow[def.key]);
      const r = await savePrefs(api, { ...prefsStore.value, meters });
      setRangeMsg({ text: r.ok ? 'Ranges saved' : r.message, err: !r.ok });
      if (r.ok) setDraft(null);
    };
    return h('div', { className: ROOT },
      h('section', { className: 'hfa-card', 'aria-label': 'HF amplifier settings' },
        h('div', { className: 'hfa-head' },
          h('h2', { className: 'hfa-title' }, 'Settings'),
          h('button', { type: 'button', onClick: () => { setDraft(null); setRangeMsg({ text: '', err: false }); setView('main'); } }, 'Done')),
        h('form', {
          className: 'hfa-group',
          onSubmit: (e) => { e.preventDefault(); hostTouched.current = false; post('/settings', { host: hostDraft.trim() }); },
        },
          h('label', { className: 'hfa-label', htmlFor: `${ROOT}-host` }, 'Amplifier address'),
          h('div', { className: 'hfa-row' },
            h('input', {
              id: `${ROOT}-host`, ref: hostRef, type: 'text', value: hostDraft, placeholder: 'e.g. 192.168.1.50',
              autoComplete: 'off', spellCheck: false,
              onChange: (e) => { hostTouched.current = true; setHostDraft(e.target.value); },
            }),
            h('button', { type: 'submit', disabled: tx, title: tx ? 'Not while transmitting' : undefined }, 'Connect')),
          h('p', { className: 'hfa-note' }, 'Zeus takes control of the amplifier while connected and hands it back to its front panel when Zeus closes.')),
        h('div', { className: 'hfa-group' },
          h('label', { className: 'hfa-check' },
            h('input', {
              type: 'checkbox', checked: !!follow.enabled, disabled: !follow.available,
              onChange: (e) => post('/settings', { followBand: e.target.checked }),
            }),
            'Follow radio band'),
          follow.available
            ? h('p', { className: 'hfa-note' }, [followLabel, follow.enabled ? follow.state : ''].filter(Boolean).join(' · ') || 'Waiting for the radio')
            : h('p', { className: 'hfa-note' }, 'Radio state is not available, so band following is off.')),
        h('div', { className: 'hfa-group' },
          h('label', { className: 'hfa-label', htmlFor: `${ROOT}-class` }, 'Controller power class'),
          h('select', {
            id: `${ROOT}-class`, value: controls.powerClass,
            onChange: (e) => post('/settings', { powerClass: Number(e.target.value) }),
          }, controls.powerClasses.map((w) => h('option', { key: w, value: w }, `${w} W`))),
          h('p', { className: 'hfa-note' }, 'The controller version. It scales the output, reflected and current readings (input is not affected). Meter ranges below are separate; adjust them to suit.')),
        h('div', { className: 'hfa-group' },
          h('label', { className: 'hfa-check' },
            h('input', { type: 'checkbox', checked: prefs.tempF, onChange: (e) => updatePrefs({ tempF: e.target.checked }) }),
            'Show temperature in °F'),
          h('p', { className: 'hfa-note' }, 'Temperature ranges below are still entered in °C.')),
        h('div', { className: 'hfa-group' },
          h('label', { className: 'hfa-check' },
            h('input', { type: 'checkbox', checked: peakHold, onChange: (e) => updatePrefs({ peakHold: e.target.checked }) }),
            'Peak hold (5 s)'),
          h('p', { className: 'hfa-note' }, 'Marks the highest output, reflected, input, current and SWR reading for five seconds.')),
        h('div', { className: 'hfa-group' },
          h('label', { className: 'hfa-check' },
            h('input', { type: 'checkbox', checked: prefs.chime.on, onChange: (e) => setChime({ on: e.target.checked }) }),
            'Chime when the amplifier faults'),
          h('div', { className: 'hfa-row hfa-chime-row' },
            h('label', { className: 'hfa-num hfa-grow' },
              h('span', { className: 'hfa-label' }, 'Volume'),
              h('input', {
                type: 'range', min: 0, max: 1, step: 0.05, value: prefs.chime.volume, disabled: !prefs.chime.on,
                'aria-label': 'Chime volume', 'aria-valuetext': `${Math.round(prefs.chime.volume * 100)} percent`,
                onChange: (e) => setChime({ volume: Number(e.target.value) }, { delay: 500 }),
              })),
            h('button', {
              type: 'button',
              onClick: async () => { if (!(await playChime(prefs.chime.volume))) setMsg({ text: 'The browser is blocking sound. Click anywhere in Zeus and try again.', err: true }); },
            }, 'Test chime')),
          h('label', { className: 'hfa-check' },
            h('input', { type: 'checkbox', checked: prefs.chime.repeat, disabled: !prefs.chime.on, onChange: (e) => setChime({ repeat: e.target.checked }) }),
            'Remind every 30 s until the fault clears'),
          h('p', { className: 'hfa-note' }, audioState() === 'unsupported'
            ? 'This browser cannot play sound; the fault banner still appears.'
            : 'One soft two-note chime when a fault starts. Browsers only allow sound after you have clicked in Zeus once.')),
        h('div', { className: 'hfa-group' },
          h('h2', { className: 'hfa-label' }, 'Meter ranges and colours'),
          h('p', { className: 'hfa-note' }, 'Set these to suit your amplifier. A bar is green below “Yellow from”, yellow from there, and red from “Red from”.'),
          METER_DEFS.map((def) => h(RangeRow, {
            key: def.key, def, draft: draftNow[def.key], error: rangeErrors[def.key],
            onChange: (f, v) => setDraft({ ...draftNow, [def.key]: { ...draftNow[def.key], [f]: v } }),
          })),
          h('div', { className: 'hfa-row' },
            h('button', { type: 'button', disabled: !draft || Object.keys(rangeErrors).length > 0, onClick: saveRanges }, 'Save ranges'),
            h('button', { type: 'button', onClick: () => { setDraft(toDraft(DEFAULT_PREFS.meters)); setRangeMsg({ text: 'Defaults loaded. Press Save ranges to keep them.', err: false }); } }, 'Reset to defaults'),
            draft ? h('button', { type: 'button', onClick: () => { setDraft(null); setRangeMsg({ text: '', err: false }); } }, 'Discard') : null),
          h('div', { className: 'hfa-msg' + (rangeMsg.err ? ' err' : ''), role: 'status', 'aria-live': 'polite' }, rangeMsg.text)),
        h('div', { className: 'hfa-msg' + (msg.err ? ' err' : ''), role: 'status', 'aria-live': 'polite' }, msg.text)));
  }

  // ------------------------------------------------------------------ main view
  const pk = (k) => (peakHold ? peaks.current[k]?.v ?? null : null);

  const sel = (n) => h('button', {
    key: n, type: 'button', 'aria-pressed': amp?.band === n, disabled: !connected || lockTx,
    onClick: () => cmd({ action: 'band', band: n }),
  }, controls.bands[n - 1]);

  const ant = (n) => h('button', {
    key: n, type: 'button', 'aria-pressed': amp?.antenna === n, disabled: !connected || lockTx,
    onClick: () => cmd({ action: 'antenna', antenna: n }),
  }, `Ant ${n}`);

  const toggle = (label, pressed, action, on, off, opts = {}) => h('button', {
    type: 'button', 'aria-pressed': pressed, disabled: !connected || lockTx,
    title: lockTx ? 'Locked while transmitting' : undefined,
    className: opts.warn ? 'hfa-warn' : undefined,
    onClick: () => cmd({ action, mode: pressed ? off : on }),
  }, label);

  const followNote = follow.enabled && follow.radioBand
    ? `Following radio: ${fmt(follow.radioHz / 1e6, 3)} MHz → ${controls.bands[follow.radioBand - 1]}` : '';

  return h('div', { className: ROOT },
    h('div', { className: `hfa-card${fault ? ' faulted' : ''}` },
      h('div', { className: 'hfa-head' },
        h('h2', { className: 'hfa-title' }, 'HF Amplifier'),
        h('div', { className: 'hfa-head-end' },
          h('span', { className: `hfa-pill ${pill[1]}`, role: 'status' }, pill[0]),
          h('button', {
            type: 'button', className: 'hfa-icon', ref: gearRef, 'aria-label': 'Settings', title: 'Settings',
            onClick: () => setView('settings'),
          }, h(Gear)))),
      fault ? h('div', { className: 'hfa-fault', role: 'alert' },
        h(Warn),
        h('div', { className: 'hfa-fault-text' },
          h('strong', null, `Fault: ${faultText}`),
          h('span', null, `${tx ? 'Stop transmitting, then c' : 'C'}heck ${FAULT_HINTS[amp.flag4] || 'the amplifier'}, then press Reset.`)),
        h('button', { type: 'button', disabled: !connected || lockTx, title: lockTx ? 'Reset is not accepted while transmitting' : undefined, onClick: () => cmd({ action: 'reset' }) }, 'Reset')) : null,
      c.error && !connected ? h('p', { className: 'hfa-msg err' }, c.error) : null,
      unconfigured
        ? h('p', { className: 'hfa-empty' }, 'No amplifier address yet. Open settings (the gear) to connect.')
        : null,
      h('div', { className: 'hfa-meters' },
        h(Meter, { label: 'Output', value: live ? meters.forwardW : null, unit: 'W', min: M.Output.min, max: M.Output.max, zones: M.Output, digits: 0, live, zone: zoneOf(meters?.forwardW, M.Output), peak: pk('Output') }),
        h(Meter, { label: 'Reflected', value: live ? meters.reflectedW : null, unit: 'W', min: M.Reflected.min, max: M.Reflected.max, digits: 0, live, zone: swrZone || 'ok', peak: pk('Reflected') }),
        h(Meter, { label: 'Input', value: live ? meters.inputW : null, unit: 'W', min: M.Input.min, max: M.Input.max, zones: M.Input, digits: 0, live, zone: zoneOf(meters?.inputW, M.Input), peak: pk('Input') }),
        h(Meter, { label: 'Current', value: live ? meters.currentA : null, unit: 'A', min: M.Current.min, max: M.Current.max, zones: M.Current, digits: 1, live, zone: zoneOf(meters?.currentA, M.Current), peak: pk('Current') }),
        h(Meter, { label: 'SWR', value: live ? meters.swr : null, unit: ':1', min: M.SWR.min, max: M.SWR.max, zones: M.SWR, digits: 2, live, zone: swrZone, peak: pk('SWR') }),
        h(Meter, { label: 'Efficiency', value: live ? meters.efficiencyPercent : null, unit: '%', min: M.Efficiency.min, max: M.Efficiency.max, digits: 0, live }),
        h(Meter, { label: 'Supply', value: idleLive ? amp.volts : null, unit: 'V', min: M.Supply.min, max: M.Supply.max, zones: M.Supply, digits: 1, live: idleLive, zone: zoneOf(amp?.volts, M.Supply) }),
        h(Meter, { label: 'Temp', value: idleLive ? (prefs.tempF ? toF(amp.tempC) : amp.tempC) : null, unit: prefs.tempF ? '°F' : '°C', min: T.min, max: T.max, zones: T, digits: 0, live: idleLive, zone: zoneOf(amp?.tempC, M.Temp) })),
      h('div', { className: 'hfa-readouts' },
        h(Read, { k: 'Band', v: amp ? amp.bandName : '—' }),
        h(Read, { k: 'Output', big: true, zone: live ? zoneOf(meters.forwardW, M.Output) : null,
          v: idleLive ? `${fmt(live ? meters.forwardW : 0, 0)} W` : '—' }),
        h(Read, { k: 'Antenna', v: amp && amp.antenna > 0 ? String(amp.antenna) : '—' })),
      msg.text || controls.message || controls.bandSelect
        ? h('div', { className: 'hfa-msg' + (msg.err ? ' err' : ''), role: 'status', 'aria-live': 'polite' },
          msg.text || controls.bandSelect || controls.message)
        : null,
      live ? h('p', { className: 'hfa-note' }, 'Efficiency is an estimate (output power ÷ supply V × current); power scaling is approximate.') : null),

    h(Section, {
      id: 'controls', title: 'Controls', open: isOpen('controls', true), onToggle: flip('controls', true),
      summary: amp ? [fault ? `FAULT ${faultText}` : '', amp.bandName, `Ant ${amp.antenna}`, amp.supplyPreset ? `${supplyLabel(amp.supplyPreset)} V` : (amp.highVoltage ? 'High' : 'Low'),
        follow.enabled ? 'Following' : '', controls.fansForced ? 'Fans 100%' : '', amp.bypass ? 'BYPASS' : '']
        .filter(Boolean).join(' · ') : '',
    },
      h('div', { className: 'hfa-group' },
        h('h2', { className: 'hfa-label' }, 'Band'),
        h('div', { className: 'hfa-row', role: 'group', 'aria-label': 'Band' },
          Array.from({ length: controls.bands.length }, (_, i) => sel(i + 1))),
        followNote ? h('p', { className: 'hfa-note' }, followNote) : null),
      h('div', { className: 'hfa-group' },
        h('h2', { className: 'hfa-label' }, 'Antenna'),
        h('div', { className: 'hfa-row', role: 'group', 'aria-label': 'Antenna' },
          Array.from({ length: controls.antennas }, (_, i) => ant(i + 1)))),
      h('div', { className: 'hfa-group' },
        h('h2', { className: 'hfa-label' }, 'Supply'),
        h('div', { className: 'hfa-row', role: 'group', 'aria-label': 'Supply voltage' },
          controls.supplyPresets.map((v) => h('button', {
            key: v, type: 'button', 'aria-pressed': amp?.supplyPreset === v, disabled: !connected || lockTx,
            title: lockTx ? 'Locked while transmitting' : undefined,
            onClick: () => cmd({ action: 'supply', volts: v }),
          }, `${supplyLabel(v)} V`))),
        h('p', { className: 'hfa-note' }, 'Sets the Huawei rectifier through the amplifier. 42 V is the low setting; the others become the stored high setting. The lit button is the one matching the measured voltage at idle.')),
      h('div', { className: 'hfa-group' },
        h('h2', { className: 'hfa-label' }, 'Cooling and protection'),
        h('div', { className: 'hfa-row' },
          toggle('Fans 100%', !!controls.fansForced, 'fans', 'force', 'auto'),
          toggle('Bypass', !!amp?.bypass, 'bypass', 'on', 'off', { warn: true }),
          h('button', { type: 'button', disabled: !connected || lockTx, title: lockTx ? 'Reset is not accepted while transmitting' : undefined, onClick: () => cmd({ action: 'reset' }) }, 'Reset')),
        h('p', { className: 'hfa-note' }, 'Bypass cuts the LDMOS supply. All controls, including Reset, are locked while transmitting.'))));
}

// Maps /status onto the host's standard amplifier meter (the meters pane).
function toAmplifierMeter(status) {
  const c = status.connection || {};
  const connected = c.state === 'connected';
  const sampled = Date.parse(status.sampledUtc);
  return {
    enabled: !!c.host,                                  // an amplifier address is configured
    connected,                                          // control link is up
    operating: connected && !!status.amp && !status.amp.bypass && !status.amp.fault,   // supply on and not faulted
    outputWatts: status.meters?.fresh ? status.meters.forwardW : 0,
    maxWatts: prefsStore.value.meters.Output.max,
    sampledAt: Number.isFinite(sampled) ? sampled : 0,  // old/zero while telemetry is stale
  };
}

export default function register(api) {
  ensureStyles();
  api.registerPanel({
    id: 'hf-amp',
    component: () => h(Panel, { api }),
  });

  // Older Zeus hosts do not have the meters-pane hook; the workspace panel still works without it.
  if (typeof api.registerAmplifierMeter === 'function') {
    api.registerAmplifierMeter({
      id: 'main',
      async read(signal) {
        try {
          const response = await api.callBackend('GET', '/status', undefined, { signal });
          if (!response.ok) return null;
          loadPrefs(api);
          const status = await response.json();
          feedFaults(status, prefsStore.value.chime);    // chime even when the panel is not open
          return toAmplifierMeter(status);
        } catch {
          return null;
        }
      },
    });
  }
}
