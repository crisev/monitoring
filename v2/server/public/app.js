// Monitor v2 — parent web app. Plain JavaScript, no build step.
// All data comes from /api/parent/*, which Cloudflare Access protects.

const view = document.getElementById('view');
const REFRESH_MS = 15_000;
let refreshTimer = null;
let timeZone = 'Europe/Bucharest';

// ---------- helpers ----------

/** Creates an element. Children that are strings become text nodes (never HTML). */
function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

async function api(path, { method = 'GET', body } = {}) {
  const res = await fetch(`/api/parent${path}`, {
    method,
    headers: body !== undefined ? { 'content-type': 'application/json' } : {},
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: 'same-origin',
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const msg =
      res.status === 401 ? 'Not signed in — reload the page to log in.'
      : res.status === 403 ? 'This account is not allowed.'
      : data.issues ? data.issues.map((i) => `${i.path.join('.')}: ${i.message}`).join('; ')
      : data.error || `Request failed (${res.status})`;
    throw new Error(msg);
  }
  return data;
}

/** 4500 -> "1h 15m", 300 -> "5m". */
function dur(seconds) {
  const neg = seconds < 0;
  const m = Math.round(Math.abs(seconds) / 60);
  const txt = m >= 60 ? `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, '0')}m` : `${m}m`;
  return neg ? `−${txt}` : txt;
}

function when(ms, withDate = true) {
  const opts = withDate
    ? { timeZone, day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false }
    : { timeZone, hour: '2-digit', minute: '2-digit', hour12: false };
  return new Intl.DateTimeFormat('en-GB', opts).format(new Date(ms));
}

function ago(ms) {
  const s = Math.max(0, Math.round((Date.now() - ms) / 1000));
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  return when(ms);
}

let toastTimer;
function toast(msg, isError = false) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.className = `show${isError ? ' error' : ''}`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.className = ''), isError ? 6000 : 3000);
}

function stamp() {
  document.getElementById('updated').textContent = `Updated ${when(Date.now(), false)}`;
}

/** Runs an async action from a button, disabling it meanwhile. */
async function act(button, fn) {
  button.disabled = true;
  try {
    await fn();
  } catch (e) {
    toast(e.message, true);
  } finally {
    button.disabled = false;
  }
}

// ---------- Today ----------

async function renderToday() {
  const t = await api('/today');
  timeZone = t.timeZone;
  const gaming = t.gaming.length > 0;
  const pc = t.devices[0];

  const mode = h('div', { class: `mode${gaming ? ' gaming' : ''}` },
    h('span', { class: 'dot', 'aria-hidden': 'true' }),
    gaming
      ? h('div', {}, h('strong', {}, 'Gaming'), ` on ${t.gaming.map((g) => g.deviceName).join(', ')} since ${when(t.gaming[0].startedAt, false)}`)
      : h('div', {}, h('strong', {}, 'School mode'), ' — only allowed apps and sites'),
  );

  const s = t.screen;
  let meter = null;
  if (s.limitSeconds !== null) {
    const pct = s.limitSeconds > 0 ? Math.min(100, (s.usedSeconds / s.limitSeconds) * 100) : 100;
    const cls = s.remainingSeconds <= 0 ? ' crit' : s.remainingSeconds < 15 * 60 ? ' warn' : '';
    meter = h('div', { class: `meter${cls}`, role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': String(s.limitSeconds), 'aria-valuenow': String(s.usedSeconds), 'aria-label': 'Screen time used' },
      h('span', { style: `width:${pct}%` }));
  }

  const pcTile = pc
    ? h('div', { class: 'card' },
        h('div', { class: 'tile-label' }, pc.name),
        h('div', { class: 'tile-value' }, h('span', { class: `status ${pc.online ? 'good' : ''}` }, pc.online ? 'Online' : 'Offline')),
        h('div', { class: 'muted small' }, pc.lastHeartbeatAt ? `Last contact ${ago(pc.lastHeartbeatAt)}` : 'Never connected'))
    : h('div', { class: 'card' },
        h('div', { class: 'tile-label' }, 'PC'),
        h('div', { class: 'tile-value' }, 'None yet'),
        h('a', { href: '#devices', class: 'small' }, 'Add the PC'));

  const note = h('input', { type: 'text', placeholder: 'Note (optional), e.g. “homework done”', maxlength: '200', 'aria-label': 'Note' });
  const grant = (bucket, minutes) => async (ev) =>
    act(ev.currentTarget, async () => {
      await api('/grant', { method: 'POST', body: { bucket, minutes, note: note.value || undefined } });
      note.value = '';
      toast(`${minutes > 0 ? '+' : ''}${minutes} min ${bucket === 'game' ? 'game time' : 'school time today'}`);
      await render();
    });
  const custom = (bucket) => {
    const input = h('input', { type: 'number', step: '5', min: '-1440', max: '1440', placeholder: '± min', 'aria-label': `Custom ${bucket} minutes` });
    const btn = h('button', {
      onclick: (ev) => {
        const m = Number(input.value);
        if (!Number.isInteger(m) || m === 0) return toast('Enter whole minutes, e.g. 20 or -10', true);
        return grant(bucket, m)(ev);
      },
    }, 'Apply');
    return h('div', { class: 'row', style: 'margin-top:8px' }, input, btn);
  };

  const actions = h('div', { class: 'card' },
    h('div', { class: 'action-group' },
      h('div', { class: 'action-title' }, 'Game time', h('span', { class: 'muted small' }, ' · carries over to the next days')),
      h('div', { class: 'row' },
        h('button', { class: 'primary', onclick: grant('game', 15) }, '+15'),
        h('button', { class: 'primary', onclick: grant('game', 30) }, '+30'),
        h('button', { class: 'primary', onclick: grant('game', 60) }, '+60')),
      custom('game')),
    h('div', { class: 'action-group' },
      h('div', { class: 'action-title' }, 'School time', h('span', { class: 'muted small' }, ' · today only')),
      h('div', { class: 'row' },
        h('button', { onclick: grant('screen', 30) }, '+30'),
        h('button', { onclick: grant('screen', 60) }, '+60')),
      custom('screen')),
    h('div', { class: 'action-group' }, note),
    gaming && h('div', { class: 'action-group' },
      h('button', {
        class: 'danger', style: 'width:100%',
        onclick: (ev) => act(ev.currentTarget, async () => {
          if (!confirm('End gaming now? The PC switches to School mode within 30 seconds.')) return;
          await api('/end-gaming', { method: 'POST', body: { note: note.value || undefined } });
          toast('Gaming ended');
          await render();
        }),
      }, 'End gaming now')),
  );

  view.replaceChildren(
    mode,
    h('div', { class: 'card', style: 'margin-top:12px' },
      h('div', { class: 'hero-label' }, 'Game time left'),
      h('div', { class: 'hero' }, dur(t.game.balanceSeconds)),
      h('div', { class: 'muted small' },
        `Used today ${dur(t.game.usedTodaySeconds)} · daily allowance ${dur(t.game.dailyAllowanceSeconds)} · carry-over cap ${dur(t.game.maxBalanceSeconds)}`)),
    h('div', { class: 'tiles', style: 'margin-top:12px' },
      h('div', { class: 'card' },
        h('div', { class: 'tile-label' }, 'Screen time left today'),
        h('div', { class: 'tile-value' }, s.remainingSeconds === null ? 'No limit' : dur(s.remainingSeconds)),
        meter,
        h('div', { class: 'muted small', style: 'margin-top:6px' },
          s.limitSeconds === null ? `Used ${dur(s.usedSeconds)}` : `Used ${dur(s.usedSeconds)} of ${dur(s.limitSeconds)}${s.extraSeconds ? ` (incl. +${dur(s.extraSeconds)} extra)` : ''}`)),
      pcTile),
    h('h2', {}, 'Give or take time'),
    actions,
  );
  stamp();
}

// ---------- History ----------

const KIND_LABEL = {
  daily_allowance: 'Daily allowance',
  cap: 'Carry-over cap',
  grant: 'Granted',
  adjustment: 'Correction',
  usage: 'Played',
};

const END_REASON = {
  stopped: 'stopped on the PC',
  parent: 'ended by a parent',
  lease_expired: 'time ran out or the PC went offline',
  abandoned: 'the PC lost the session',
};

function parseDetail(detail) {
  if (!detail) return {};
  try { return JSON.parse(detail); } catch { return { text: detail }; }
}

/** Human-readable line for an event row. Device-provided text is only ever shown as text. */
function describeEvent(e) {
  const d = parseDetail(e.detail);
  switch (e.type) {
    case 'game_start': return `Gaming started (balance ${dur(d.balanceSeconds ?? 0)})`;
    case 'game_end': return `Gaming ended: ${END_REASON[d.reason] ?? d.reason}, played ${dur(d.usedSeconds ?? 0)}`;
    case 'grant': return `${d.minutes > 0 ? '+' : ''}${d.minutes}m ${d.bucket === 'game' ? 'game time' : 'school time'}${d.note ? ` — “${d.note}”` : ''}`;
    case 'end_gaming': return `Ended gaming${d.note ? ` — “${d.note}”` : ''}`;
    case 'settings': return `Settings changed (v${d.version})`;
    case 'device_added': return 'PC added';
    case 'device_reset': return 'New enrollment code issued';
    case 'device_revoked': return 'PC removed';
    case 'enrolled': return 'PC enrolled';
    case 'enroll_failed': return 'Wrong enrollment code entered';
    case 'pc_silent': return 'PC went silent while in use';
    default: return `${e.type}${e.detail ? `: ${e.detail}` : ''}`;
  }
}

function listItem(left, right, sub) {
  return h('li', { class: 'item' },
    h('div', { class: 'item-main' }, h('span', {}, left), right),
    sub && h('div', { class: 'item-sub' }, sub));
}

async function renderHistory() {
  const [ledger, events] = await Promise.all([api('/ledger?limit=100'), api('/events?limit=100')]);
  const ledgerItems = ledger.items.map((r) =>
    listItem(
      `${KIND_LABEL[r.kind] ?? r.kind}${r.bucket === 'screen' ? ' · school time, today only' : ''}`,
      h('span', { class: `num ${r.seconds >= 0 ? 'plus' : 'minus'}` }, `${r.seconds > 0 ? '+' : ''}${dur(r.seconds)}`),
      [when(r.createdAt), r.by.startsWith('device:') ? 'PC' : r.by === 'system' ? null : r.by, r.note && `“${r.note}”`].filter(Boolean).join(' · ')));
  const eventItems = events.items.map((e) =>
    listItem(describeEvent(e), null, [when(e.at), e.deviceName, e.by].filter(Boolean).join(' · ')));

  view.replaceChildren(
    h('h2', {}, 'Time ledger'),
    h('div', { class: 'card' }, ledgerItems.length ? h('ul', { class: 'list' }, ledgerItems) : h('p', { class: 'muted' }, 'Nothing yet.')),
    h('h2', {}, 'Events'),
    h('div', { class: 'card' }, eventItems.length ? h('ul', { class: 'list' }, eventItems) : h('p', { class: 'muted' }, 'Nothing yet.')),
  );
  stamp();
}

// ---------- Activity ----------

const SERIES = [['school', 'School'], ['gaming', 'Gaming']];

function shiftDay(day, n) {
  const d = new Date(`${day}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}
function dayLabel(day, opts = { weekday: 'short', day: 'numeric', month: 'short' }) {
  return new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', ...opts }).format(new Date(`${day}T00:00:00Z`));
}
function hashParams() {
  return new URLSearchParams(location.hash.split('?')[1] ?? '');
}
/** Like dur(), but shows seconds for very short times. */
function dur2(seconds) {
  return seconds > 0 && seconds < 60 ? `${seconds}s` : dur(seconds);
}
function splitTip(r) {
  const parts = [`School ${dur2(r.school)}`, `Gaming ${dur2(r.gaming)}`];
  if (r.audio) parts.push(`sound ${dur2(r.audio)}`);
  if (r.blocked) parts.push(`blocked ${r.blocked}×`);
  return parts.join(' · ');
}

function legend() {
  return h('div', { class: 'legend' }, SERIES.map(([k, label]) => h('span', {}, h('i', { class: `swatch ${k}` }), label)));
}

/** Horizontal bars, school + gaming stacked, longest first. */
function barList(rows, nameOf, subOf) {
  const max = Math.max(1, ...rows.map((r) => r.seconds));
  return h('div', { class: 'bars' }, rows.map((r) =>
    h('div', { class: 'bar-row', 'data-tip': `${nameOf(r)} — ${dur2(r.seconds)} (${splitTip(r)})`, tabindex: '0' },
      h('div', { class: 'bar-label' },
        h('span', { class: 'bar-name' }, nameOf(r), subOf && subOf(r) ? h('span', { class: 'muted small' }, ` ${subOf(r)}`) : null),
        h('span', { class: 'num' }, r.seconds ? dur2(r.seconds) : `blocked ${r.blocked}×`)),
      h('div', { class: 'bar-track' },
        SERIES.filter(([k]) => r[k] > 0).map(([k]) => h('span', { class: `seg ${k}`, style: `width:${(r[k] / max) * 100}%` }))))));
}

/** One day as a strip of 5-minute slots, coloured by mode. */
function timeline(report) {
  const slots = report.timeline;
  const HOUR = 3_600_000;
  const slotMs = report.slotMinutes * 60_000;
  let start = Math.floor(slots[0].slot / HOUR) * HOUR;
  let end = Math.ceil((slots.at(-1).slot + slotMs) / HOUR) * HOUR;
  if (end - start < 6 * HOUR) end = start + 6 * HOUR;
  const span = end - start;
  const hours = span / HOUR;
  // Keep hour labels at least ~56px apart on narrow screens.
  const width = Math.max(200, view.clientWidth - 64);
  const every = [1, 2, 3, 4, 6, 12].find((n) => width / (hours / n) >= 56) ?? 12;
  const ticks = [];
  for (let t = start; t <= end; t += every * HOUR) {
    ticks.push(h('span', { class: 'tick', style: `left:${((t - start) / span) * 100}%` }, when(t, false)));
  }
  return h('div', { class: 'card' },
    h('div', { class: 'strip', role: 'img', 'aria-label': 'When the PC was used' },
      slots.map((s) => h('span', {
        class: `slot ${s.mode}`,
        style: `left:${((s.slot - start) / span) * 100}%;width:${(slotMs / span) * 100}%`,
        'data-tip': `${when(s.slot, false)}–${when(s.slot + slotMs, false)} · ${s.mode === 'gaming' ? 'Gaming' : 'School'} · mostly ${s.topApp} · ${dur2(s.seconds)} active`,
      }))),
    h('div', { class: 'ticks' }, ticks));
}

/** Several days as stacked columns. */
function perDay(report) {
  const byDay = new Map(report.perDay.map((d) => [d.day, d]));
  const days = [];
  for (let d = report.from; d <= report.to; d = shiftDay(d, 1)) days.push(byDay.get(d) ?? { day: d, seconds: 0, school: 0, gaming: 0 });
  const max = Math.max(1, ...days.map((d) => d.seconds));
  const labelEvery = days.length > 14 ? 5 : 1;
  return h('div', { class: 'card' },
    h('div', { class: 'cols' }, days.map((d, i) =>
      h('div', { class: 'col', 'data-tip': `${dayLabel(d.day)} — ${dur2(d.seconds)} (${splitTip(d)})`, tabindex: '0' },
        h('div', { class: 'col-bar' },
          SERIES.filter(([k]) => d[k] > 0).map(([k]) => h('span', { class: `seg ${k}`, style: `height:${(d[k] / max) * 100}%` }))),
        h('div', { class: 'col-label' }, i % labelEvery === 0 ? dayLabel(d.day, { day: 'numeric', month: days.length > 7 ? 'short' : undefined, weekday: days.length <= 7 ? 'short' : undefined }) : '')))));
}

async function renderActivity() {
  const p = hashParams();
  const qs = new URLSearchParams();
  if (p.get('from')) qs.set('from', p.get('from'));
  if (p.get('to')) qs.set('to', p.get('to'));
  const r = await api(`/activity?${qs}`);
  timeZone = r.timeZone;

  const go = (from, to) => (location.hash = `#activity?from=${from}&to=${to}`);
  const presets = [
    ['Today', r.today, r.today],
    ['Yesterday', shiftDay(r.today, -1), shiftDay(r.today, -1)],
    ['7 days', shiftDay(r.today, -6), r.today],
    ['30 days', shiftDay(r.today, -29), r.today],
  ];
  const picker = h('input', { type: 'date', value: r.from === r.to ? r.from : '', max: r.today, 'aria-label': 'Pick a day',
    onchange: (e) => e.target.value && go(e.target.value, e.target.value) });
  const filters = h('div', { class: 'filters' },
    presets.map(([label, from, to]) =>
      h('button', { class: `chip${r.from === from && r.to === to ? ' on' : ''}`, onclick: () => go(from, to) }, label)),
    picker);

  const single = r.from === r.to;
  const title = single ? (r.from === r.today ? `Today, ${dayLabel(r.from)}` : dayLabel(r.from, { weekday: 'long', day: 'numeric', month: 'long' }))
    : `${dayLabel(r.from)} – ${dayLabel(r.to)}`;
  const t = r.totals;
  const blockedApps = r.apps.filter((a) => a.blocked > 0);

  const content = t.seconds === 0 && t.blocked === 0
    ? [h('div', { class: 'card muted' }, 'No activity recorded for this period.')]
    : [
        h('div', { class: 'tiles tiles3' },
          h('div', { class: 'card' }, h('div', { class: 'tile-label' }, 'Active on the PC'), h('div', { class: 'tile-value' }, dur(t.seconds))),
          h('div', { class: 'card' }, h('div', { class: 'tile-label' }, h('i', { class: 'swatch school' }), 'School'), h('div', { class: 'tile-value' }, dur(t.school))),
          h('div', { class: 'card' }, h('div', { class: 'tile-label' }, h('i', { class: 'swatch gaming' }), 'Gaming'), h('div', { class: 'tile-value' }, dur(t.gaming)))),
        h('div', { class: 'section-head' }, h('h2', {}, single ? 'When' : 'Per day'), legend()),
        single ? (r.timeline.length ? timeline(r) : h('div', { class: 'card muted' }, 'No foreground activity.')) : perDay(r),
        h('h2', {}, 'Apps'),
        h('div', { class: 'card' }, barList(r.apps.filter((a) => a.seconds > 0), (a) => a.app)),
        h('h2', {}, 'Websites'),
        h('div', { class: 'card' }, r.sites.length ? barList(r.sites, (s) => s.site) : h('span', { class: 'muted' }, 'No websites recorded.')),
        h('h2', {}, 'Window titles'),
        h('div', { class: 'card' }, r.titles.length
          ? h('ul', { class: 'list' }, r.titles.map((x) => listItem(x.title, h('span', { class: 'num' }, dur2(x.seconds)), [x.app, x.site].filter(Boolean).join(' · '))))
          : h('span', { class: 'muted' }, 'No titles recorded.')),
        blockedApps.length && h('h2', {}, 'Blocked in School mode'),
        blockedApps.length && h('div', { class: 'card' },
          h('ul', { class: 'list' }, blockedApps.map((a) => listItem(a.app, h('span', { class: 'num' }, `${a.blocked}×`), null)))),
      ];

  view.replaceChildren(filters, h('div', { class: 'range-title' }, title), ...content);
  stamp();
}

// ---------- Settings ----------

const NUMBER_FIELDS = [
  ['dailyGameMinutes', 'Daily game allowance (min)', 'Added every day. 0 = game time only when you grant it.'],
  ['maxGameBalanceMinutes', 'Carry-over cap (min)', 'Each morning the game balance is limited to this.'],
  ['dailyScreenMinutes', 'Daily screen-time limit (min)', 'School + gaming. The PC shuts down when it runs out. 0 = no limit.'],
  ['offlineBudgetMinutes', 'Offline budget (min)', 'How long the PC may be used (School mode only) without internet.'],
  ['screenshotIntervalMinutes', 'Screenshot interval (min)', 'Screenshots go to Discord. 0 = off.'],
];
const LIST_FIELDS = [
  ['allowedApps', 'Allowed apps in School mode', 'Program names without .exe, one per line (e.g. Code, WINWORD).'],
  ['allowedSites', 'Allowed websites in School mode', 'One domain per line; subdomains are included.'],
  ['blockedTitles', 'Blocked window titles', 'Blocked even on allowed sites, e.g. “Google Doodles”. One per line.'],
];

async function renderSettings() {
  const { settings, version, updatedAt, updatedBy } = await api('/settings');
  timeZone = settings.timeZone;
  const inputs = {};

  const numberFields = NUMBER_FIELDS.map(([key, label, help]) => {
    inputs[key] = h('input', { type: 'number', min: '0', step: '1', value: String(settings[key]), id: `f-${key}` });
    return h('label', { class: 'field', for: `f-${key}` }, h('span', {}, label), inputs[key], h('small', {}, help));
  });
  const listFields = LIST_FIELDS.map(([key, label, help]) => {
    inputs[key] = h('textarea', { id: `f-${key}`, spellcheck: 'false' });
    inputs[key].value = settings[key].join('\n');
    return h('label', { class: 'field', for: `f-${key}` }, h('span', {}, label), inputs[key], h('small', {}, help));
  });

  const save = h('button', {
    class: 'primary',
    onclick: (ev) => act(ev.currentTarget, async () => {
      const next = { ...settings };
      for (const [key] of NUMBER_FIELDS) next[key] = Number(inputs[key].value);
      for (const [key] of LIST_FIELDS) next[key] = inputs[key].value.split('\n').map((s) => s.trim()).filter(Boolean);
      await api('/settings', { method: 'PUT', body: next });
      toast('Settings saved — the PC picks them up within a minute');
      await render();
    }),
  }, 'Save settings');

  view.replaceChildren(
    h('div', { class: 'card stack' },
      h('div', { class: 'grid2' }, numberFields),
      ...listFields,
      h('div', { class: 'row' }, save,
        h('span', { class: 'muted small' }, version ? `Version ${version}, changed ${when(updatedAt)} by ${updatedBy}` : 'Using defaults (never saved)'))),
  );
  stamp();
}

// ---------- PCs ----------

function codeCard(res, title) {
  return h('div', { class: 'card stack', style: 'margin-top:12px' },
    h('div', { class: 'action-title' }, title),
    h('div', { class: 'code' }, res.code.replace(/(.{5})/, '$1-')),
    h('div', { class: 'muted small' }, `Enter this code in the installer on the PC. Valid until ${when(res.expiresAt, false)}, one use.`));
}

async function renderDevices() {
  const { items } = await api('/devices');
  const result = h('div');

  const rows = items.map((d) =>
    h('div', { class: 'card stack' },
      h('div', { class: 'row', style: 'justify-content:space-between' },
        h('strong', {}, d.name),
        h('span', { class: `status ${d.online ? 'good' : ''}` }, d.online ? 'Online' : 'Offline')),
      h('div', { class: 'muted small' },
        [
          d.enrolled ? 'Enrolled' : d.enrollPending ? 'Waiting for enrollment' : 'Not enrolled',
          d.clientVersion && `version ${d.clientVersion}`,
          d.lastHeartbeatAt && `last contact ${ago(d.lastHeartbeatAt)}`,
        ].filter(Boolean).join(' · ')),
      h('div', { class: 'row' },
        h('button', {
          onclick: (ev) => act(ev.currentTarget, async () => {
            if (d.enrolled && !confirm(`Re-enroll ${d.name}? Its current login stops working until the new code is entered.`)) return;
            result.replaceChildren(codeCard(await api(`/devices/${d.id}/reset`, { method: 'POST', body: {} }), `New code for ${d.name}`));
          }),
        }, 'New enrollment code'),
        h('button', {
          onclick: (ev) => act(ev.currentTarget, async () => {
            if (!confirm(`Remove ${d.name}? It will no longer be able to connect.`)) return;
            await api(`/devices/${d.id}/revoke`, { method: 'POST', body: {} });
            toast('PC removed');
            await render();
          }),
        }, 'Remove'))));

  const name = h('input', { type: 'text', placeholder: 'PC name, e.g. Desktop', maxlength: '60', 'aria-label': 'PC name' });
  const add = h('button', {
    class: 'primary',
    onclick: (ev) => act(ev.currentTarget, async () => {
      if (!name.value.trim()) return toast('Give the PC a name', true);
      const res = await api('/devices', { method: 'POST', body: { name: name.value.trim() } });
      name.value = '';
      await renderDevices();
      document.getElementById('enroll-result').replaceChildren(codeCard(res, 'Enrollment code'));
    }),
  }, 'Add PC');

  result.id = 'enroll-result';
  view.replaceChildren(
    h('div', { class: 'stack' }, rows.length ? rows : h('p', { class: 'muted' }, 'No PCs yet.')),
    h('h2', {}, 'Add a PC'),
    h('div', { class: 'card' }, h('div', { class: 'row' }, name, add)),
    result,
  );
  stamp();
}

// ---------- router ----------

/** True while the parent is typing, so auto-refresh does not wipe the inputs. */
function isEditing() {
  const a = document.activeElement;
  if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA')) return true;
  return [...view.querySelectorAll('input')].some((i) => i.value);
}

const ROUTES = { today: renderToday, activity: renderActivity, history: renderHistory, settings: renderSettings, devices: renderDevices };

async function render() {
  const route = (location.hash.slice(1) || 'today').split('?')[0];
  const fn = ROUTES[route] ?? renderToday;
  for (const a of document.querySelectorAll('#nav a')) {
    a.toggleAttribute('aria-current', a.getAttribute('href') === `#${route}`);
    if (a.getAttribute('href') === `#${route}`) a.setAttribute('aria-current', 'page');
  }
  clearInterval(refreshTimer);
  try {
    await fn();
  } catch (e) {
    view.replaceChildren(h('div', { class: 'card' }, h('strong', {}, 'Could not load. '), e.message));
  }
  if (fn === renderToday) {
    refreshTimer = setInterval(() => {
      if (!document.hidden && !isEditing()) renderToday().catch(() => {});
    }, REFRESH_MS);
  }
}

window.addEventListener('hashchange', render);

// Hover / tap tooltips for anything with data-tip (text only).
const tip = document.getElementById('tip');
function placeTip(e) {
  const pad = 12;
  const x = Math.min(e.clientX + pad, window.innerWidth - tip.offsetWidth - 8);
  const y = e.clientY - tip.offsetHeight - pad < 8 ? e.clientY + pad : e.clientY - tip.offsetHeight - pad;
  tip.style.transform = `translate(${Math.max(8, x)}px, ${y}px)`;
}
document.addEventListener('pointerover', (e) => {
  const t = e.target.closest?.('[data-tip]');
  if (!t) return;
  tip.textContent = t.dataset.tip;
  tip.classList.add('show');
  placeTip(e);
});
document.addEventListener('pointermove', (e) => tip.classList.contains('show') && placeTip(e));
document.addEventListener('pointerout', (e) => {
  const t = e.target.closest?.('[data-tip]');
  if (t && !t.contains(e.relatedTarget)) tip.classList.remove('show');
});
window.addEventListener('scroll', () => tip.classList.remove('show'), { passive: true });
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && !isEditing() && location.hash.slice(1) !== 'settings') render();
});
api('/me').then((me) => (document.getElementById('who').textContent = `Signed in as ${me.email}`)).catch(() => {});
render();
