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

// callBackend resolves to a fetch Response (not parsed JSON); parse it and surface HTTP errors.
async function call(api, method, path, body) {
  const res = await api.callBackend(method, path, body);
  let data = null;
  try { data = await res.json(); } catch { /* empty or non-JSON body */ }
  return { ok: !!res.ok, status: res.status, data };
}

const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? '—' : Number(v).toFixed(d));

const HOLD_MS = 5000;
const pctOf = (v, min, max) => (v == null ? 0 : Math.max(0, Math.min(100, ((v - min) / (max - min)) * 100)));

// One meter row: label + value on top, tick-marked bar below, optional held-peak marker.
function Meter({ label, value, unit, min = 0, max, digits, live, warnAt, peak }) {
  const pct = pctOf(value, min, max);
  const warn = live && warnAt != null && pct >= warnAt;
  const peakPct = peak == null ? null : pctOf(peak, min, max);
  return h('div', { className: 'hfa-meter' },
    h('div', { className: 'hfa-meter-head' },
      h('span', { className: 'hfa-label' }, label),
      h('span', { className: 'val' }, fmt(value, digits), h('span', { className: 'unit' }, unit))),
    h('div', {
      className: `hfa-bar${live ? '' : ' stale'}${warn ? ' warn' : ''}`, role: 'meter', 'aria-label': label,
      'aria-valuemin': min, 'aria-valuemax': max, 'aria-valuenow': value == null ? min : Number(value.toFixed(2)),
      'aria-valuetext': value == null ? 'no reading' : `${fmt(value, digits)} ${unit}`,
    },
      h('i', { style: { width: `${pct}%` } }),
      peakPct != null && peakPct > pct + 0.5
        ? h('b', { className: 'hfa-peak', style: { left: `${peakPct}%` }, title: `Peak ${fmt(peak, digits)} ${unit}` })
        : null));
}

function Read({ k, v }) {
  return h('div', { className: 'hfa-read' },
    h('span', { className: 'hfa-label' }, k),
    h('span', { className: 'v' }, v));
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

const STORE = `${ROOT}.sections`;
const PREFS = `${ROOT}.prefs`;
function loadJson(key) {
  try { return JSON.parse(localStorage.getItem(key) || '{}') || {}; } catch { return {}; }
}
function saveJson(key, v) {
  try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* storage unavailable */ }
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

function Panel({ api }) {
  const [status, setStatus] = React.useState(null);
  const [loadError, setLoadError] = React.useState(null);
  const [msg, setMsg] = React.useState({ text: '', err: false });
  const [hostDraft, setHostDraft] = React.useState('');
  const hostTouched = React.useRef(false);
  const [openMap, setOpenMap] = React.useState(() => loadJson(STORE));
  const [prefs, setPrefs] = React.useState(() => loadJson(PREFS));
  const [view, setView] = React.useState('main');            // 'main' | 'settings'
  const peakHold = !!prefs.peakHold;
  const peakHoldRef = React.useRef(peakHold);
  const peaks = React.useRef({});
  const gearRef = React.useRef(null);
  const hostRef = React.useRef(null);
  const wasSettings = React.useRef(false);

  peakHoldRef.current = peakHold;

  React.useEffect(() => {
    let stop = false;
    let timer = 0;
    const tick = async () => {
      try {
        const r = await call(api, 'GET', '/status');
        if (stop) return;
        if (!r.ok || !r.data?.connection) throw new Error(r.data?.message || `HTTP ${r.status}`);
        const s = r.data;
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
  const isOpen = (id, dflt) => (openMap[id] === undefined ? dflt : !!openMap[id]);
  const flip = (id, dflt) => () => setOpenMap((m) => {
    const next = { ...m, [id]: !(m[id] === undefined ? dflt : m[id]) };
    saveJson(STORE, next);
    return next;
  });
  const setPref = (k, v) => setPrefs((p) => {
    const next = { ...p, [k]: v };
    saveJson(PREFS, next);
    return next;
  });

  if (!status) {
    return h('div', { className: ROOT },
      h('div', { className: 'hfa-card' },
        h('h2', { className: 'hfa-title' }, 'HF Amplifier'),
        h('p', { className: 'hfa-empty', role: 'status' },
          loadError ? `Unavailable: ${loadError}` : 'Loading…')));
  }

  const { connection: c, amp, meters, controls, follow, limits } = status;
  const connected = c.state === 'connected';
  const tx = !!amp?.tx;
  const lockTx = tx; // band/antenna/supply changes are refused by the backend while transmitting
  const live = !!meters?.fresh;
  const idleLive = connected && !!amp;
  const unconfigured = !c.host;

  const pill = unconfigured ? ['Not configured', '']
    : connected ? (tx ? ['Transmitting', 'tx'] : ['Connected', 'ok'])
    : [c.state === 'connecting' ? 'Connecting…' : 'Disconnected', 'warn'];

  // ------------------------------------------------------------------ settings view
  if (view === 'settings') {
    const followLabel = follow.radioBand
      ? `Radio ${fmt(follow.radioHz / 1e6, 3)} MHz → ${controls.bands[follow.radioBand - 1]}`
      : follow.radioHz ? `Radio ${fmt(follow.radioHz / 1e6, 3)} MHz (outside amp bands)` : '';
    return h('div', { className: ROOT },
      h('section', { className: 'hfa-card', 'aria-label': 'HF amplifier settings' },
        h('div', { className: 'hfa-head' },
          h('h2', { className: 'hfa-title' }, 'Settings'),
          h('button', { type: 'button', onClick: () => setView('main') }, 'Done')),
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
            h('button', { type: 'submit' }, 'Connect')),
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
          h('label', { className: 'hfa-check' },
            h('input', { type: 'checkbox', checked: peakHold, onChange: (e) => setPref('peakHold', e.target.checked) }),
            'Peak hold (5 s)'),
          h('p', { className: 'hfa-note' }, 'Marks the highest output, reflected, input, current and SWR reading for five seconds.')),
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
    type: 'button', 'aria-pressed': pressed, disabled: !connected || opts.disabled,
    className: opts.warn ? 'hfa-warn' : undefined,
    onClick: () => cmd({ action, mode: pressed ? off : on }),
  }, label);

  const followNote = follow.enabled && follow.radioBand
    ? `Following radio: ${fmt(follow.radioHz / 1e6, 3)} MHz → ${controls.bands[follow.radioBand - 1]}` : '';

  return h('div', { className: ROOT },
    h('div', { className: 'hfa-card' },
      h('div', { className: 'hfa-head' },
        h('h2', { className: 'hfa-title' }, 'HF Amplifier'),
        h('div', { className: 'hfa-head-end' },
          h('span', { className: `hfa-pill ${pill[1]}`, role: 'status' }, pill[0]),
          h('button', {
            type: 'button', className: 'hfa-icon', ref: gearRef, 'aria-label': 'Settings', title: 'Settings',
            onClick: () => setView('settings'),
          }, h(Gear)))),
      c.error && !connected ? h('p', { className: 'hfa-msg err' }, c.error) : null,
      unconfigured
        ? h('p', { className: 'hfa-empty' }, 'No amplifier address yet. Open settings (the gear) to connect.')
        : null,
      h('div', { className: 'hfa-meters' },
        h(Meter, { label: 'Output', value: live ? meters.forwardW : null, unit: 'W', max: limits.forwardW, digits: 0, live, warnAt: 92, peak: pk('Output') }),
        h(Meter, { label: 'Reflected', value: live ? meters.reflectedW : null, unit: 'W', max: limits.reflectedW, digits: 0, live, warnAt: 25, peak: pk('Reflected') }),
        h(Meter, { label: 'Input', value: live ? meters.inputW : null, unit: 'W', max: limits.inputW, digits: 0, live, peak: pk('Input') }),
        h(Meter, { label: 'Current', value: live ? meters.currentA : null, unit: 'A', max: limits.currentA, digits: 1, live, warnAt: 90, peak: pk('Current') }),
        h(Meter, { label: 'SWR', value: live ? meters.swr : null, unit: ':1', min: 1, max: 3, digits: 2, live, warnAt: 50, peak: pk('SWR') }),
        h(Meter, { label: 'Efficiency', value: live ? meters.efficiencyPercent : null, unit: '%', max: 100, digits: 0, live }),
        h(Meter, { label: 'Supply', value: idleLive ? amp.volts : null, unit: 'V', min: 40, max: 60, digits: 1, live: idleLive }),
        h(Meter, { label: 'Temp', value: idleLive ? amp.tempC : null, unit: '°C', max: 100, digits: 0, live: idleLive, warnAt: 60 })),
      h('div', { className: 'hfa-readouts' },
        h(Read, { k: 'Band', v: amp ? amp.bandName : '—' }),
        h(Read, { k: 'Antenna', v: amp ? String(amp.antenna) : '—' })),
      msg.text || controls.message || controls.bandSelect
        ? h('div', { className: 'hfa-msg' + (msg.err ? ' err' : ''), role: 'status', 'aria-live': 'polite' },
          msg.text || controls.bandSelect || controls.message)
        : null,
      live ? h('p', { className: 'hfa-note' }, 'Efficiency is an estimate (output power ÷ supply V × current); power scaling is approximate.') : null),

    h(Section, {
      id: 'controls', title: 'Controls', open: isOpen('controls', true), onToggle: flip('controls', true),
      summary: amp ? [amp.bandName, `Ant ${amp.antenna}`, amp.highVoltage ? 'High' : 'Low',
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
          h('button', { type: 'button', 'aria-pressed': amp ? !amp.highVoltage : false, disabled: !connected || lockTx,
            onClick: () => cmd({ action: 'voltage', mode: 'low' }) }, 'Low'),
          h('button', { type: 'button', 'aria-pressed': amp ? amp.highVoltage : false, disabled: !connected || lockTx,
            onClick: () => cmd({ action: 'voltage', mode: 'high' }) }, 'High'))),
      h('div', { className: 'hfa-group' },
        h('h2', { className: 'hfa-label' }, 'Cooling and protection'),
        h('div', { className: 'hfa-row' },
          toggle('Fans 100%', !!controls.fansForced, 'fans', 'force', 'auto'),
          toggle('Bypass', !!amp?.bypass, 'bypass', 'on', 'off', { warn: true, disabled: false }),
          h('button', { type: 'button', disabled: !connected, onClick: () => cmd({ action: 'reset' }) }, 'Reset')),
        h('p', { className: 'hfa-note' }, 'Bypass cuts the LDMOS supply. Changes are locked while transmitting.'))));
}

// Maps /status onto the host's standard amplifier meter (the meters pane).
function toAmplifierMeter(status) {
  const c = status.connection || {};
  const connected = c.state === 'connected';
  const sampled = Date.parse(status.sampledUtc);
  return {
    enabled: !!c.host,                                  // an amplifier address is configured
    connected,                                          // control link is up
    operating: connected && !!status.amp && !status.amp.bypass,   // not bypassed (LDMOS supply on)
    outputWatts: status.meters?.fresh ? status.meters.forwardW : 0,
    maxWatts: status.limits?.forwardW ?? 1300,
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
          return toAmplifierMeter(await response.json());
        } catch {
          return null;
        }
      },
    });
  }
}
