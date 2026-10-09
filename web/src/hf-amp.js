// SPDX-License-Identifier: GPL-2.0-or-later
// HF LDMOS amplifier panel. Plain createElement (no JSX, no bundler dependencies).
// `react` is provided by the Zeus host; styles are inlined by build.mjs.
import * as React from 'react';

const h = React.createElement;
const STYLES = /*__STYLES__*/"";
const ROOT = 'com-rigeek-hfamp';
const POLL_MS = 250;

function ensureStyles() {
  if (document.getElementById(`${ROOT}-styles`)) return;
  const el = document.createElement('style');
  el.id = `${ROOT}-styles`;
  el.textContent = STYLES;
  document.head.appendChild(el);
}

const fmt = (v, d = 0) => (v == null || Number.isNaN(v) ? '—' : Number(v).toFixed(d));

function Meter({ label, value, unit, max, digits, live }) {
  const pct = value == null ? 0 : Math.max(0, Math.min(100, (value / max) * 100));
  return h('div', { className: 'hfa-meter' },
    h('span', { className: 'lbl' }, label),
    h('div', {
      className: `hfa-bar${live ? '' : ' stale'}`, role: 'meter', 'aria-label': label,
      'aria-valuemin': 0, 'aria-valuemax': max, 'aria-valuenow': value == null ? 0 : Math.round(value),
    }, h('i', { style: { width: `${pct}%` } })),
    h('span', { className: 'val' }, `${fmt(value, digits)} ${unit}`));
}

function Read({ k, v, warn }) {
  return h('div', { className: 'hfa-read' },
    h('span', { className: 'k' }, k),
    h('span', { className: `v${warn ? ' warn' : ''}` }, v));
}

function Panel({ api }) {
  const [status, setStatus] = React.useState(null);
  const [loadError, setLoadError] = React.useState(null);
  const [msg, setMsg] = React.useState({ text: '', err: false });
  const [hostDraft, setHostDraft] = React.useState('');
  const hostTouched = React.useRef(false);

  React.useEffect(() => {
    let stop = false;
    let timer = 0;
    const tick = async () => {
      try {
        const s = await api.callBackend('GET', '/status');
        if (stop) return;
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

  const post = async (path, body) => {
    try {
      const r = await api.callBackend('POST', path, body);
      setMsg({ text: r?.message || 'Done', err: r?.ok === false });
    } catch (e) {
      setMsg({ text: e?.message || 'Request failed', err: true });
    }
  };
  const cmd = (body) => post('/command', body);

  if (!status) {
    return h('div', { className: ROOT },
      h('div', { className: 'hfa-card' },
        h('h2', null, 'HF Amplifier'),
        h('p', { className: 'hfa-empty', role: 'status' },
          loadError ? `Unavailable: ${loadError}` : 'Loading…')));
  }

  const { connection: c, amp, meters, controls, follow, limits } = status;
  const connected = c.state === 'connected';
  const tx = !!amp?.tx;
  const lockTx = tx; // band/antenna/supply changes are refused by the backend while transmitting
  const live = !!meters?.fresh;
  const unconfigured = !c.host;

  const pill = unconfigured ? ['Not configured', '']
    : connected ? (tx ? ['Transmitting', 'tx'] : ['Connected', 'ok'])
    : [c.state === 'connecting' ? 'Connecting…' : 'Disconnected', 'warn'];

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

  const followLabel = follow.radioBand
    ? `Radio ${fmt(follow.radioHz / 1e6, 3)} MHz → ${controls.bands[follow.radioBand - 1]}`
    : follow.radioHz ? `Radio ${fmt(follow.radioHz / 1e6, 3)} MHz (outside amp bands)` : '';

  return h('div', { className: ROOT },
    h('div', { className: 'hfa-card' },
      h('div', { className: 'hfa-head' },
        h('h2', null, 'HF Amplifier'),
        h('span', { className: `hfa-pill ${pill[1]}`, role: 'status' }, pill[0])),
      c.error && !connected ? h('p', { className: 'hfa-msg err' }, c.error) : null,
      unconfigured
        ? h('p', { className: 'hfa-empty' }, 'Enter the amplifier address below to connect.')
        : null,
      h('div', { className: 'hfa-meters' },
        h(Meter, { label: 'Output', value: live ? meters.forwardW : null, unit: 'W', max: limits.forwardW, digits: 0, live }),
        h(Meter, { label: 'Reflected', value: live ? meters.reflectedW : null, unit: 'W', max: limits.reflectedW, digits: 0, live }),
        h(Meter, { label: 'Input', value: live ? meters.inputW : null, unit: 'W', max: limits.inputW, digits: 0, live }),
        h(Meter, { label: 'Current', value: live ? meters.currentA : null, unit: 'A', max: limits.currentA, digits: 1, live })),
      h('div', { className: 'hfa-readouts' },
        h(Read, { k: 'SWR', v: live && meters.swr != null ? `${fmt(meters.swr, 2)}:1` : '—', warn: live && meters.swr > 2 }),
        h(Read, { k: 'Efficiency', v: live && meters.efficiencyPercent != null ? `${fmt(meters.efficiencyPercent, 0)} %` : '—' }),
        h(Read, { k: 'Supply', v: amp ? `${fmt(amp.volts, 1)} V` : '—' }),
        h(Read, { k: 'Temp', v: amp ? `${fmt(amp.tempC)} °C` : '—', warn: amp?.tempC >= 60 }),
        h(Read, { k: 'Band', v: amp ? amp.bandName : '—' }),
        h(Read, { k: 'Antenna', v: amp ? String(amp.antenna) : '—' })),
      live ? h('p', { className: 'hfa-note' }, 'Efficiency is an estimate (output power ÷ supply V × current); power scaling is approximate.') : null),

    h('div', { className: 'hfa-card' },
      h('div', { className: 'hfa-group' },
        h('h2', null, 'Band'),
        h('div', { className: 'hfa-row', role: 'group', 'aria-label': 'Band' },
          Array.from({ length: controls.bands.length }, (_, i) => sel(i + 1))),
        controls.bandSelect ? h('p', { className: 'hfa-note', role: 'status' }, controls.bandSelect) : null,
        h('label', { className: 'hfa-check' },
          h('input', {
            type: 'checkbox', checked: !!follow.enabled, disabled: !follow.available,
            onChange: (e) => post('/settings', { followBand: e.target.checked }),
          }),
          'Follow radio band'),
        follow.available
          ? h('p', { className: 'hfa-note' }, [followLabel, follow.enabled ? follow.state : ''].filter(Boolean).join(' · '))
          : h('p', { className: 'hfa-note' }, 'Radio state is not available, so band following is off.')),
      h('div', { className: 'hfa-group' },
        h('h2', null, 'Antenna'),
        h('div', { className: 'hfa-row', role: 'group', 'aria-label': 'Antenna' },
          Array.from({ length: controls.antennas }, (_, i) => ant(i + 1)))),
      h('div', { className: 'hfa-group' },
        h('h2', null, 'Supply'),
        h('div', { className: 'hfa-row', role: 'group', 'aria-label': 'Supply voltage' },
          h('button', { type: 'button', 'aria-pressed': amp ? !amp.highVoltage : false, disabled: !connected || lockTx,
            onClick: () => cmd({ action: 'voltage', mode: 'low' }) }, 'Low'),
          h('button', { type: 'button', 'aria-pressed': amp ? amp.highVoltage : false, disabled: !connected || lockTx,
            onClick: () => cmd({ action: 'voltage', mode: 'high' }) }, 'High'))),
      h('div', { className: 'hfa-group' },
        h('h2', null, 'Cooling and protection'),
        h('div', { className: 'hfa-row' },
          toggle('Fans 100%', !!controls.fansForced, 'fans', 'force', 'auto'),
          toggle('Bypass', !!amp?.bypass, 'bypass', 'on', 'off', { warn: true, disabled: false }),
          h('button', { type: 'button', disabled: !connected, onClick: () => cmd({ action: 'reset' }) }, 'Reset')),
        h('p', { className: 'hfa-note' }, 'Bypass cuts the LDMOS supply. Changes are locked while transmitting.')),
      h('div', { className: 'hfa-msg' + (msg.err ? ' err' : ''), role: 'status', 'aria-live': 'polite' },
        msg.text || controls.message || '')),

    h('form', {
      className: 'hfa-card',
      onSubmit: (e) => { e.preventDefault(); hostTouched.current = false; post('/settings', { host: hostDraft.trim() }); },
    },
      h('h2', null, 'Connection'),
      h('div', { className: 'hfa-row' },
        h('input', {
          type: 'text', value: hostDraft, 'aria-label': 'Amplifier address', placeholder: 'e.g. 192.168.1.50',
          autoComplete: 'off', spellCheck: false,
          onChange: (e) => { hostTouched.current = true; setHostDraft(e.target.value); },
        }),
        h('button', { type: 'submit' }, 'Connect')),
      h('p', { className: 'hfa-note' }, 'Zeus takes control of the amplifier while connected and hands it back to its front panel when Zeus closes.')));
}

export default function register(api) {
  ensureStyles();
  api.registerPanel({
    id: 'hf-amp',
    component: () => h(Panel, { api }),
  });
}
