/*
  The live chart gallery (Charts.astro). Moved here from the plain-HTML site
  on 2026-10-21; the drawing code is unchanged.

  Every chart is generated from a fixed seed: identical on every load, and
  nobody's usage. Colours, rates and rules (model shades, spike threshold,
  heat ramps, top-eight-plus-Other, top-nine-plus-Others) follow the app's
  own src/UsageCore/View/ModelColors.cs, Providers.cs, Pricing/*.json and
  src/UsageApp/Charts/*.cs.
*/

/* ------------------------------------------------------------------ theme */

/* The page's theme: the visitor's choice (data-theme, set by ThemeToggle.astro),
   else the system's. Chart colours are read from it on every draw. */
const isLight = () => {
  const chosen = document.documentElement.dataset.theme;
  return chosen ? chosen === 'light' : matchMedia('(prefers-color-scheme: light)').matches;
};

/* ------------------------------------------------------------------ format */

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const fmtDay = (d) => `${MONTHS[d.getUTCMonth()]} ${d.getUTCDate()}`;
const fmtDayLong = (d) => `${WEEKDAYS[d.getUTCDay()]}, ${fmtDay(d)}, ${d.getUTCFullYear()}`;

function usd(v) {
  if (v >= 1000) return '$' + v.toLocaleString('en-US', { maximumFractionDigits: 0 });
  return '$' + v.toFixed(2);
}
function tokens(v) {
  if (v >= 1e9) return (v / 1e9).toFixed(2) + 'B';
  if (v >= 1e6) return (v / 1e6).toFixed(2) + 'M';
  if (v >= 1e3) return (v / 1e3).toFixed(1) + 'K';
  return String(Math.round(v));
}
const count = (v) => Math.round(v).toLocaleString('en-US');
function duration(sec) {
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
  if (h === 0) return `${m}m`;
  return `${h}h ${m}m`;
}
function hour(h) {
  const am = h < 12 ? 'AM' : 'PM';
  const v = h % 12 === 0 ? 12 : h % 12;
  return `${v} ${am}`;
}
const pct = (x) => `${(x * 100).toFixed(1)}%`;
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const initials = (name) => name.split(/\s+/).map((w) => w[0]).join('').slice(0, 2).toUpperCase();

/* ------------------------------------------------------------------ data */

function mulberry32(seed) {
  return function () {
    seed |= 0; seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const DAY_MS = 86400000;
const END = new Date(Date.UTC(2026, 9, 14)); // 2026-10-14, the screenshots' last day
const DAYS = 300;

/** The fallback project palette (ProjectColors.cs), handed out in report order. */
const PROJECT_COLORS = ['#3B82F6', '#D946EF', '#0D9488', '#D97706', '#8B5CF6', '#DB2777', '#0891B2', '#65A30D', '#6366F1', '#E11D48', '#C38504', '#7C3AED'];

/*
  Two invented agents. Models are listed dearest first; each has its dark
  shade and the light theme's twin (ModelColors.LightPalettes). Rates are the
  built-in cards', USD per million tokens.
*/
const AGENTS = {
  claude: {
    label: 'Claude Code', costLabel: 'Total estimated spend', noun: 'messages', Noun: 'Messages', sub: 'subagent',
    seed: 20261014,
    models: [
      { id: 'fable-5', dark: '#A14324', light: '#712E19', w: 0.14, rate: { input: 10, w5: 12.5, w1: 20, read: 1, output: 50 } },
      { id: 'opus-5', dark: '#D0562F', light: '#963E21', w: 0.46, rate: { input: 5, w5: 6.25, w1: 10, read: 0.5, output: 25 } },
      { id: 'opus-4-8', dark: '#D97757', light: '#AD4727', w: 0.18, rate: { input: 5, w5: 6.25, w1: 10, read: 0.5, output: 25 } },
      { id: 'sonnet-5', dark: '#E7A893', light: '#D56845', w: 0.22, rate: { input: 2, w5: 2.5, w1: 4, read: 0.2, output: 10 } },
    ],
    projects: [['Recipe Box', 34], ['Weather Widget', 22], ['Invoice Tool', 18], ['Bird Log', 12], ['Chess Clock', 8], ['Tide Table', 6],
      ['Habit Grid', 4], ['Plant Water', 3], ['Sudoku Solver', 2], ['Dice Roller', 1.5], ['Color Picker', 1]],
    ramp: {
      dark: ['#161318', '#9D4224', '#BE4F2B', '#D3603B', '#D97757', '#E39981'],
      light: ['#D9DDE4', '#D97554', '#CF552E', '#AF4827', '#933C21', '#75301A'],
    },
    swatches: {
      dark: ['#A14324', '#AE4827', '#BA4D2A', '#C5512C', '#D0562F', '#D56743', '#D97757', '#DF8B70', '#E19278', '#E39981', '#E7A893', '#EDC0B1'],
      light: ['#712E19', '#79321B', '#82361D', '#8C3A1F', '#963E21', '#A14224', '#AD4727', '#B94C29', '#C6512C', '#D25A33', '#D56845', '#D97657'],
    },
    scale: 1,
  },
  codex: {
    label: 'Codex', costLabel: 'API-equivalent spend', noun: 'requests', Noun: 'Requests', sub: 'auto-review',
    seed: 20261015,
    models: [
      { id: 'gpt-5.6-sol', dark: '#0B6D55', light: '#074B3B', w: 0.58, rate: { input: 4, read: 0.4, output: 20 }, long: true },
      { id: 'codex-auto-review', dark: '#13C69A', light: '#0D8164', w: 0.12, rate: { input: 1.75, read: 0.175, output: 14 }, alias: 'gpt-5.3-codex' },
      { id: 'gpt-6.1-sol', dark: '#3FD2AC', light: '#0E8C6D', w: 0.30, rate: { input: 2, read: 0.1, output: 10 }, long: true },
    ],
    projects: [['Recipe Box', 48], ['Label Printer', 30], ['Bird Log', 14], ['Tide Table', 8]],
    ramp: {
      dark: ['#131817', '#0A6B53', '#0D8165', '#0E9372', '#10A37F', '#13C197'],
      light: ['#D7DEE0', '#10A37F', '#0E8D6D', '#0C765C', '#0A624D', '#084F3E'],
    },
    swatches: {
      dark: ['#0B6D55', '#0C7A5F', '#0E8A6C', '#0F9674', '#10A37F', '#12B48C', '#13C69A', '#3FD2AC', '#6CDDBF', '#98E8D3'],
      light: ['#074B3B', '#085340', '#095B47', '#0A624D', '#0B6C54', '#0C755C', '#0D8164', '#0E8C6D', '#0F9876', '#10A580'],
    },
    scale: 0.55,
  },
};

const modelColor = (m) => (isLight() ? m.light : m.dark);

function cell() {
  return { msgs: 0, input: 0, output: 0, read: 0, w5: 0, w1: 0, reasoning: 0, cost: 0, runtime: 0 };
}
const totalTokens = (c) => c.input + c.output + c.read + c.w5 + c.w1;

function add(a, b) {
  for (const k of Object.keys(a)) a[k] += b[k];
  return a;
}

function costOf(m, c) {
  const r = m.rate;
  return (c.input * r.input + c.output * r.output + c.read * r.read + c.w5 * (r.w5 ?? 0) + c.w1 * (r.w1 ?? 0)) / 1e6;
}

function build(key) {
  const cfg = AGENTS[key];
  const rnd = mulberry32(cfg.seed);
  const days = [];
  const hours = new Array(24).fill(0);
  const projectTotals = new Map(cfg.projects.map(([name]) => [name, { name, cost: 0, tokens: 0, runtime: 0, msgs: 0, days: 0, perModel: cfg.models.map(() => cell()) }]));
  const weightSum = cfg.projects.reduce((s, [, w]) => s + w, 0);
  const pickProject = () => {
    let r = rnd() * weightSum;
    for (const [name, w] of cfg.projects) { r -= w; if (r <= 0) return name; }
    return cfg.projects[0][0];
  };

  for (let i = 0; i < DAYS; i++) {
    const date = new Date(END.getTime() - (DAYS - 1 - i) * DAY_MS);
    const p = 0.12 + 0.55 * (i / DAYS);
    const day = { date, combined: cell(), perModel: cfg.models.map(() => cell()), projects: new Map() };
    // The last few days are always active, so today has a figure for the tray.
    if (rnd() < p || i >= DAYS - 3) {
      let msgs = (8 + rnd() * 55) * cfg.scale * (rnd() < 0.08 ? 2.6 : 1);
      // A few projects a day, the first taking most of it.
      const names = [pickProject()];
      if (rnd() < 0.55) names.push(pickProject());
      if (rnd() < 0.25) names.push(pickProject());
      const shares = names.map((_, k) => (k === 0 ? 1.6 : 0.4 + rnd() * 0.6));
      const shareSum = shares.reduce((s, v) => s + v, 0);
      names.forEach((name, k) => {
        const pm = msgs * (shares[k] / shareSum);
        const proj = projectTotals.get(name);
        let projCost = 0;
        cfg.models.forEach((m, mi) => {
          const n = Math.round(pm * m.w * (0.3 + rnd() * 1.4));
          if (n <= 0) return;
          const c = cell();
          c.msgs = n;
          if (key === 'claude') {
            c.input = n * (8 + rnd() * 20);
            c.output = n * (900 + rnd() * 2200);
            c.read = n * (35000 + rnd() * 60000);
            c.w5 = n * (2000 + rnd() * 3500);
            c.w1 = n * (rnd() < 0.3 ? 200 + rnd() * 900 : 0);
          } else {
            c.input = n * (1500 + rnd() * 4000);
            c.output = n * (800 + rnd() * 1800);
            c.reasoning = c.output * (0.3 + rnd() * 0.3);
            c.read = n * (40000 + rnd() * 70000);
          }
          for (const f of ['input', 'output', 'read', 'w5', 'w1', 'reasoning']) c[f] = Math.round(c[f]);
          c.cost = costOf(m, c);
          c.runtime = n * (25 + rnd() * 45);
          add(day.perModel[mi], c);
          add(proj.perModel[mi], c);
          add(day.combined, c);
          projCost += c.cost;
          proj.tokens += totalTokens(c);
          proj.runtime += c.runtime;
          proj.msgs += c.msgs;
        });
        proj.cost += projCost;
        proj.days += 1;
        day.projects.set(name, (day.projects.get(name) ?? 0) + projCost);
      });
      const peak = Math.floor(9 + rnd() * 14) % 24;
      hours[peak] += day.combined.msgs * 0.4;
      hours[(peak + 1) % 24] += day.combined.msgs * 0.3;
      hours[(peak + 23) % 24] += day.combined.msgs * 0.3;
    }
    days.push(day);
  }

  const firstActive = days.findIndex((d) => d.combined.msgs > 0);
  const recorded = days.slice(firstActive);
  const combined = recorded.reduce((a, d) => add(a, { ...d.combined }), cell());
  const perModel = cfg.models.map((_, mi) => recorded.reduce((a, d) => add(a, { ...d.perModel[mi] }), cell()));
  const projects = [...projectTotals.values()].filter((p) => p.cost > 0).sort((a, b) => b.cost - a.cost);
  projects.forEach((p, i) => { p.color = PROJECT_COLORS[i % PROJECT_COLORS.length]; });

  // Streaks, ending today.
  let current = 0, longest = 0, run = 0;
  recorded.forEach((d) => { run = d.combined.msgs > 0 ? run + 1 : 0; longest = Math.max(longest, run); });
  for (let i = days.length - 1; i >= 0 && days[i].combined.msgs > 0; i--) current++;

  const sessions = Math.round(combined.msgs / 16);
  return {
    cfg, key, days: recorded, all: days, combined, perModel, projects,
    activeDays: recorded.filter((d) => d.combined.msgs > 0).length,
    sessions, subagents: Math.round(sessions * 0.2),
    current, longest,
    peakHour: hours.indexOf(Math.max(...hours)),
    longestChat: 3600 + rnd() * 5400,
    peakTokens: Math.max(...recorded.map((d) => totalTokens(d.combined))) * 0.45,
  };
}

const DATA = { claude: build('claude'), codex: build('codex') };
let agent = 'claude';

/* ------------------------------------------------------------------ helpers */

/** Round axis ticks from zero to at least max, five of them, like the app's NiceTicks. */
function niceTicks(max) {
  if (max <= 0) return [0, 1, 2, 3, 4];
  const raw = max / 4;
  const p = 10 ** Math.floor(Math.log10(raw));
  let step = 10 * p;
  for (const m of [1, 2, 2.5, 3, 5, 10]) if (m * p >= raw) { step = m * p; break; }
  return [0, 1, 2, 3, 4].map((i) => i * step);
}

function yAxis(ticks, x0, x1, y, fmt) {
  return ticks.map((v) => `<line x1="${x0}" x2="${x1}" y1="${y(v)}" y2="${y(v)}" stroke="var(--chart-grid)"/>`
    + `<text x="${x0 - 8}" y="${y(v) + 4}" text-anchor="end">${esc(fmt(v))}</text>`).join('');
}

/** Monotone cubic (Fritsch-Carlson): a curve that never overshoots the data. */
function curve(pts, move = true) {
  const n = pts.length;
  if (n < 2) return n ? `${move ? 'M' : 'L'}${pts[0][0]},${pts[0][1]}` : '';
  const dx = [], m = [], t = [];
  for (let i = 0; i < n - 1; i++) { dx[i] = pts[i + 1][0] - pts[i][0]; m[i] = (pts[i + 1][1] - pts[i][1]) / dx[i]; }
  t[0] = m[0]; t[n - 1] = m[n - 2];
  for (let i = 1; i < n - 1; i++) t[i] = m[i - 1] * m[i] <= 0 ? 0 : (m[i - 1] + m[i]) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (m[i] === 0) { t[i] = 0; t[i + 1] = 0; continue; }
    const a = t[i] / m[i], b = t[i + 1] / m[i], h = a * a + b * b;
    if (h > 9) { const k = 3 / Math.sqrt(h); t[i] = k * a * m[i]; t[i + 1] = k * b * m[i]; }
  }
  const f = (v) => v.toFixed(1);
  let d = `${move ? 'M' : 'L'}${f(pts[0][0])},${f(pts[0][1])}`;
  for (let i = 0; i < n - 1; i++) {
    const h = dx[i] / 3;
    d += `C${f(pts[i][0] + h)},${f(pts[i][1] + h * t[i])} ${f(pts[i + 1][0] - h)},${f(pts[i + 1][1] - h * t[i + 1])} ${f(pts[i + 1][0])},${f(pts[i + 1][1])}`;
  }
  return d;
}

function xDates(days, x, y, every) {
  let s = '';
  const last = days.length - 1;
  days.forEach((d, i) => {
    if ((last - i) % every !== 0) return;
    // Edge labels are nudged inward, not dropped: the last day is the one worth naming.
    const anchor = i === last ? 'end' : i === 0 ? 'start' : 'middle';
    s += `<text x="${x(i)}" y="${y}" text-anchor="${anchor}">${fmtDay(d.date)}</text>`;
  });
  return s;
}

const tipRow = (color, label, value) =>
  `<div class="row"><i style="background:${color}"></i>${esc(label)} <span style="margin-left:auto;padding-left:12px">${esc(value)}</span></div>`;

function hitStrips(days, x, T, H, html) {
  const w = days.length > 1 ? x(1) - x(0) : 20;
  return days.map((d, i) => `<rect x="${x(i) - w / 2}" y="${T}" width="${w}" height="${H}" fill="transparent" data-tip="${esc(html(d, i))}"/>`).join('');
}

function countUp(nodes) {
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  nodes.forEach((el) => {
    const final = el.textContent;
    const m = final.match(/^([^\d]*)([\d,.]+)(.*)$/);
    if (!m) return;
    const to = parseFloat(m[2].replace(/,/g, ''));
    const dec = (m[2].split('.')[1] || '').length;
    const t0 = performance.now();
    const step = (t) => {
      const p = Math.min(1, (t - t0) / 900);
      const e = 1 - (1 - p) ** 3;
      const v = (to * e).toFixed(dec);
      el.textContent = p < 1 ? m[1] + (m[2].includes(',') ? Number(v).toLocaleString('en-US', { minimumFractionDigits: dec }) : v) + m[3] : final;
      if (p < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  });
}

/* ------------------------------------------------------------------ charts */

function drawHead(d) {
  const c = d.combined;
  const first = d.days[0].date, last = d.days[d.days.length - 1].date;
  document.getElementById('v-head').innerHTML = `<div class="headline">
    <div><div class="h-label">${esc(d.cfg.costLabel)}</div>
      <div class="h-big" data-count>${usd(c.cost)}</div>
      <div class="h-sub">across ${d.projects.length} projects · ${d.cfg.models.length} models · ${d.activeDays} active days</div>
      <div class="h-sub faint">${fmtDay(first)} to ${fmtDay(last)}</div></div>
    <div><div class="h-label">Total tokens</div>
      <div class="h-num" data-count>${tokens(totalTokens(c))}</div>
      <div class="h-sub">${count(c.msgs)} ${d.cfg.noun}</div></div>
    <div><div class="h-label">Total runtime <span class="info" data-tip="${esc('<b>About runtime</b>The sum of gaps between consecutive transcript lines, leaving out any gap over 30 minutes as time away. An approximation, and labelled as one.')}">i</span></div>
      <div class="h-num">${duration(c.runtime)}</div>
      <div class="h-sub">${usd(c.cost / d.activeDays)} avg / active day</div></div>
  </div>`;
  countUp(document.querySelectorAll('#v-head [data-count]'));
}

/** The app's MarkSpikes: above mean + 1.6 sd, and above 1.5x the mean. */
function spikes(values) {
  const mean = values.reduce((s, v) => s + v, 0) / values.length;
  const sd = Math.sqrt(values.reduce((s, v) => s + (v - mean) ** 2, 0) / values.length);
  return values.map((v) => v > mean + 1.6 * sd && v > mean * 1.5);
}

function drawSpend(d) {
  const W = 1000, H = 280, L = 52, R = 10, T = 10, B = 26;
  const days = d.days.slice(-90);
  const vals = days.map((x) => x.combined.cost);
  const marks = spikes(vals);
  const ticks = niceTicks(Math.max(...vals));
  const top = ticks[4];
  const x = (i) => L + ((W - L - R) * i) / (days.length - 1);
  const y = (v) => H - B - ((H - B - T) * v) / top;
  const line = curve(days.map((dd, i) => [x(i), y(dd.combined.cost)]));
  const area = `${line}L${x(days.length - 1)},${y(0)}L${x(0)},${y(0)}Z`;
  const dots = days.map((dd, i) => (marks[i] ? `<circle cx="${x(i)}" cy="${y(dd.combined.cost)}" r="4.5" fill="var(--spike)" class="fade"/>` : '')).join('');
  const largest = d.projects[0];
  document.getElementById('v-spend').innerHTML =
    `<div class="chart-top"><div class="callout"><span class="callout-label">Largest project</span><span class="callout-name">${esc(largest.name)}</span> <span class="callout-value">${usd(largest.cost)}</span></div></div>
    <svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Daily combined spend over the last ${days.length} days">
      <defs><linearGradient id="g-spend" x1="0" x2="0" y1="0" y2="1">
        <stop offset="0" stop-color="var(--accent)" stop-opacity="0.45"/><stop offset="1" stop-color="var(--accent)" stop-opacity="0.02"/>
      </linearGradient></defs>
      ${yAxis(ticks, L, W - R, y, (v) => '$' + v.toFixed(v < 10 && v % 1 ? 1 : 0))}
      <path d="${area}" fill="url(#g-spend)" class="fade"/><path d="${line}" fill="none" stroke="var(--accent)" stroke-width="2" class="fade"/>
      ${dots}${xDates(days, x, H - 6, 10)}
      ${hitStrips(days, x, T, H - B - T, (dd, i) => `<b>${fmtDayLong(dd.date)}</b><span style="color:${marks[i] ? 'var(--spike)' : 'var(--accent-bright)'};font-weight:650;font-size:15px">${usd(dd.combined.cost)}</span>${marks[i] ? ' <small>well above trend</small>' : ''}`)}
    </svg>`;
}

function drawCost(d) {
  const models = d.cfg.models.map((m, i) => ({ m, c: d.perModel[i] })).sort((a, b) => b.c.cost - a.c.cost);
  const W = 1000, L = 120, R = 14, rowH = 42, T = 4;
  const H = T + models.length * rowH + 26;
  const ticks = niceTicks(models[0].c.cost);
  const x = (v) => L + ((W - L - R) * v) / ticks[4];
  let s = '';
  ticks.forEach((v) => {
    s += `<line x1="${x(v)}" x2="${x(v)}" y1="${T}" y2="${H - 22}" stroke="var(--chart-grid)"/><text x="${x(v)}" y="${H - 6}" text-anchor="middle">$${v.toFixed(v % 1 ? 2 : 0)}</text>`;
  });
  models.forEach(({ m, c }, i) => {
    const by = T + i * rowH + 8, bh = rowH - 16;
    const tip = `<b>${esc(m.id)}</b>${tipRow(modelColor(m), 'Spend', usd(c.cost))}${tipRow(modelColor(m), 'Share', pct(c.cost / d.combined.cost))}${tipRow(modelColor(m), 'Tokens', tokens(totalTokens(c)))}`;
    s += `<text x="${L - 10}" y="${by + bh / 2 + 4}" text-anchor="end">${esc(m.id)}</text>`;
    s += `<rect class="draw-x" style="animation-delay:${i * 60}ms" x="${L}" y="${by}" width="${Math.max(2, x(c.cost) - L)}" height="${bh}" rx="4" fill="${modelColor(m)}" data-tip="${esc(tip)}"/>`;
  });
  document.getElementById('v-cost').innerHTML = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Estimated spend per model, dearest first">${s}</svg>`;
}

const ICONS = {
  sessions: '<path d="M4 6h16M4 12h16M4 18h10"/>',
  messages: '<path d="M4 5h16v11H9l-5 4z"/>',
  hourglass: '<path d="M7 3h10M7 21h10M8 3c0 5 8 6 8 9s-8 4-8 9M16 3c0 5-8 6-8 9"/>',
  model: '<rect x="5" y="5" width="14" height="14" rx="3"/><path d="M9 9h6v6H9z"/>',
  input: '<path d="M12 4v12M6 10l6 6 6-6M5 20h14"/>',
  output: '<path d="M12 20V8M6 14l6-6 6 6M5 4h14"/>',
  cache: '<ellipse cx="12" cy="6" rx="7" ry="3"/><path d="M5 6v12c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3"/>',
  peak: '<path d="M3 20l6-8 4 4 8-11"/>',
  calendar: '<rect x="4" y="5" width="16" height="15" rx="2"/><path d="M4 10h16M9 3v4M15 3v4"/>',
  flame: '<path d="M12 21c4 0 6-3 6-6 0-4-4-6-4-11-3 2-4 5-4 7-1-1-2-2-2-4-2 2-2 5-2 8 0 3 2 6 6 6z"/>',
  trophy: '<path d="M8 4h8v5a4 4 0 0 1-8 0zM8 6H5a3 3 0 0 0 3 4M16 6h3a3 3 0 0 1-3 4M12 13v4M8 20h8"/>',
  clock: '<circle cx="12" cy="12" r="8"/><path d="M12 8v4l3 2"/>',
};

function drawCards(d) {
  const c = d.combined;
  const top = d.cfg.models.map((m, i) => ({ m, t: totalTokens(d.perModel[i]) })).sort((a, b) => b.t - a.t)[0].m;
  const cached = c.read + c.w5 + c.w1;
  const codex = d.key === 'codex';
  const cards = [
    ['Sessions', 'sessions', count(d.sessions), `${count(d.sessions - d.subagents)} top-level · ${count(d.subagents)} ${d.cfg.sub}`, 'Each transcript file is a session; subagent files are counted apart.'],
    [d.cfg.Noun, 'messages', count(c.msgs), 'de-duplicated API calls', 'Each API call counted once, however many times it was written to disk.'],
    ['Longest chat', 'hourglass', duration(d.longestChat), `Sep 18 · ${d.projects[1].name}`, 'The most active runtime in one chat, measured like total runtime, not first-to-last wall clock.'],
    ['Top model', 'model', `<span class="mdot" style="background:${modelColor(top)}"></span><span class="accent-text">${esc(top.id)}</span>`, 'by total tokens', null],
    ['Input tokens', 'input', tokens(c.input), 'uncached prompt', 'Prompt tokens charged at the full input rate. Anything served from cache is counted under Cached tokens instead.'],
    ['Output tokens', 'output', tokens(c.output), codex ? `incl. ${tokens(c.reasoning)} reasoning` : 'generated', codex ? 'Reasoning tokens are part of output, so they are not added again.' : null],
    ['Cached tokens', 'cache', tokens(cached), codex ? 'read from cache' : `${tokens(c.read)} read · ${tokens(c.w5 + c.w1)} written`, null],
    ['Peak tokens', 'peak', tokens(d.peakTokens), `Oct 2 · ${d.projects[0].name}`, 'The heaviest single chat, with the subagent threads it spawned folded in.'],
    ['Active days', 'calendar', count(d.activeDays), 'days with any usage', null],
    ['Current streak', 'flame', `${d.current}d`, 'consecutive days', 'Consecutive active days ending today. Resets once a full day passes with no usage.'],
    ['Longest streak', 'trophy', `${d.longest}d`, 'consecutive days', null],
    ['Peak hour', 'clock', hour(d.peakHour), `most ${d.cfg.noun}`, 'Busiest hour of the day by message count, in your configured local offset.'],
  ];
  document.getElementById('v-cards').innerHTML = cards.map(([label, icon, value, sub, tip]) => `<div class="acard">
      <div class="h-label"><svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[icon]}</svg>${esc(label)}${tip ? ` <span class="info" data-tip="${esc(`<b>About ${label.toLowerCase()}</b>${tip}`)}">i</span>` : ''}</div>
      <div class="v">${value}</div><div class="s" title="${esc(sub)}">${esc(sub)}</div></div>`).join('');
}

let heatExpanded = false;
function drawHeat(d) {
  const ramp = d.cfg.ramp[isLight() ? 'light' : 'dark'];
  document.getElementById('heat-word').textContent = isLight() ? 'Darker' : 'Brighter';
  const byTime = new Map(d.all.map((x) => [x.date.getTime(), x.combined.cost]));
  const max = Math.max(...d.all.map((x) => x.combined.cost));
  const level = (v) => (v <= 0 ? 0 : Math.min(5, 1 + Math.floor((v / max) * 5)));
  const mondayOf = (t) => t - ((new Date(t).getUTCDay() + 6) % 7) * DAY_MS;

  function strip(startMon, label) {
    let cells = '<span></span>';
    let lastMonth = -1;
    for (let w = 0; w < 26; w++) {
      const m = new Date(startMon + w * 7 * DAY_MS).getUTCMonth();
      cells += `<span class="hm-month">${m !== lastMonth ? MONTHS[m] : ''}</span>`;
      lastMonth = m;
    }
    ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].forEach((name, dow) => {
      cells += `<span class="hm-day">${name}</span>`;
      for (let w = 0; w < 26; w++) {
        const t = startMon + (w * 7 + dow) * DAY_MS;
        if (t > END.getTime() || !byTime.has(t)) { cells += '<span></span>'; continue; }
        const v = byTime.get(t);
        const tip = `<b>${fmtDayLong(new Date(t))}</b>${v > 0 ? usd(v) : 'No usage'}`;
        cells += `<span class="hm-cell fade" style="background:${ramp[level(v)]}" data-tip="${esc(tip)}"></span>`;
      }
    });
    return (label ? `<div class="hm-block-label">${label}</div>` : '') + `<div class="hm" role="img" aria-label="Spend per day, ${label || 'the last 26 weeks'}">${cells}</div>`;
  }

  const lastMon = mondayOf(END.getTime());
  let html;
  const sum = (from, to) => d.all.slice(from, to).reduce((s, x) => s + x.combined.cost, 0);
  const n = d.all.length;
  const thisWeek = sum(n - 7, n), prevWeek = sum(n - 14, n - 7);
  const change = prevWeek > 0 ? (thisWeek - prevWeek) / prevWeek : 0;
  if (!heatExpanded) {
    html = `<div class="hm-top"><b class="${change >= 0 ? 'up' : 'down'}">${change >= 0 ? '↑' : '↓'} ${Math.abs(change * 100).toFixed(0)}%</b> vs previous 7 days</div>`
      + strip(lastMon - 25 * 7 * DAY_MS, '');
  } else {
    html = '';
    const firstMon = mondayOf(d.days[0].date.getTime());
    for (let s0 = firstMon; s0 <= END.getTime(); s0 += 26 * 7 * DAY_MS) {
      const a = new Date(s0), b = new Date(Math.min(s0 + (26 * 7 - 1) * DAY_MS, END.getTime()));
      html += strip(s0, `${MONTHS[a.getUTCMonth()]} ${a.getUTCFullYear()} to ${MONTHS[b.getUTCMonth()]} ${b.getUTCFullYear()}`);
    }
  }
  const shown = heatExpanded ? d.days : d.all.slice(-182);
  const total = shown.reduce((s, x) => s + x.combined.cost, 0);
  const active = shown.filter((x) => x.combined.cost > 0).length;
  html += `<div class="hm-foot"><span>${usd(total)} across ${active} active days ${heatExpanded ? `since ${fmtDay(d.days[0].date)}` : 'in the last 6 months'}</span>
    <button type="button" class="mini-btn" id="hm-toggle" aria-expanded="${heatExpanded}">${heatExpanded ? 'Back to 6 months' : 'Expand'}</button>
    <span class="hm-scale">Less ${ramp.map((c) => `<i style="background:${c}"></i>`).join('')} More</span></div>`;
  const el = document.getElementById('v-heat');
  el.innerHTML = html;
  el.querySelector('#hm-toggle').addEventListener('click', () => { heatExpanded = !heatExpanded; drawHeat(d); });
}

let projRange = '30';
function drawProjects(d) {
  const W = 1000, H = 280, L = 52, R = 10, T = 10, B = 26;
  const days = projRange === 'all' ? d.days : d.days.slice(-Number(projRange));
  // The top eight over the days shown get a band; the rest are summed into Other.
  const inRange = d.projects.map((p) => ({ p, cost: days.reduce((s, x) => s + (x.projects.get(p.name) ?? 0), 0) }))
    .filter((x) => x.cost > 0).sort((a, b) => b.cost - a.cost);
  const top = inRange.slice(0, 8);
  const rest = inRange.slice(8);
  const series = top.map(({ p }) => ({ name: p.name, color: p.color, vals: days.map((x) => x.projects.get(p.name) ?? 0) }));
  if (rest.length) series.push({ name: 'Other', color: isLight() ? '#8C909B' : '#5D5D68', vals: days.map((x) => rest.reduce((s, r) => s + (x.projects.get(r.p.name) ?? 0), 0)) });
  const totals = days.map((_, i) => series.reduce((s, se) => s + se.vals[i], 0));
  const ticks = niceTicks(Math.max(...totals));
  const x = (i) => L + ((W - L - R) * i) / Math.max(1, days.length - 1);
  const y = (v) => H - B - ((H - B - T) * v) / ticks[4];

  // Back to front: each band fills from its cumulative top down to the
  // baseline, topmost first, so no seams show between bands.
  const tops = [];
  let base = days.map(() => 0);
  series.forEach((se) => { base = base.map((b, i) => b + se.vals[i]); tops.push(base); });
  let layers = '';
  for (let k = series.length - 1; k >= 0; k--) {
    const line = curve(tops[k].map((v, i) => [x(i), y(v)]));
    const fill = `color-mix(in srgb, ${series[k].color} 62%, var(--surface))`;
    layers += `<path d="${line}L${x(days.length - 1)},${y(0)}L${x(0)},${y(0)}Z" style="fill:${fill}" class="fade"/>`
      + `<path d="${line}" fill="none" stroke="${series[k].color}" stroke-width="1.4" class="fade"/>`;
  }
  const every = days.length > 120 ? 30 : days.length > 40 ? 14 : 4;
  document.getElementById('v-proj').innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Daily spend stacked by project">
      ${yAxis(ticks, L, W - R, y, (v) => '$' + v.toFixed(v % 1 ? 1 : 0))}${layers}${xDates(days, x, H - 6, every)}
      ${hitStrips(days, x, T, H - B - T, (dd, i) => `<b>${fmtDayLong(dd.date)}</b>` + series.map((se) => [se, se.vals[i]]).filter(([, v]) => v > 0)
        .sort((a, b) => b[1] - a[1]).map(([se, v]) => tipRow(se.color, se.name, usd(v))).join('') || 'No usage')}
    </svg>`;
  document.getElementById('v-proj-legend').innerHTML = series.map((se) =>
    `<span><i style="background:${se.color}"></i>${se.name === 'Other' ? '' : `<span class="logo">${initials(se.name)}</span>`}${esc(se.name)}</span>`).join('');
}

let stackMode = 'tokens';
function drawStack(d) {
  const W = 1000, H = 280, L = 56, R = 10, T = 10, B = 26;
  const days = d.days.slice(-30);
  const spend = stackMode === 'spend';
  const val = (c) => (spend ? c.cost : totalTokens(c));
  const fmt = spend ? (v) => '$' + v.toFixed(v % 1 ? 1 : 0) : tokens;
  const totals = days.map((x) => val(x.combined));
  const ticks = niceTicks(Math.max(...totals));
  const slot = (W - L - R) / days.length;
  const bw = slot * 0.72;
  const y = (v) => H - B - ((H - B - T) * v) / ticks[4];
  let bars = '';
  days.forEach((dd, i) => {
    if (!dd.combined.msgs) return;
    const bx = L + slot * i + (slot - bw) / 2;
    let acc = 0, rects = '';
    // Dearest model at the base of each column.
    d.cfg.models.forEach((m, mi) => {
      const v = val(dd.perModel[mi]);
      if (v <= 0) return;
      rects += `<rect x="${bx}" y="${y(acc + v)}" width="${bw}" height="${y(acc) - y(acc + v)}" fill="${modelColor(m)}"/>`;
      acc += v;
    });
    const tip = `<b>${fmtDayLong(dd.date)}</b>` + d.cfg.models.map((m, mi) => [m, val(dd.perModel[mi])]).filter(([, v]) => v > 0)
      .map(([m, v]) => tipRow(modelColor(m), m.id, spend ? usd(v) : tokens(v))).join('') + `<div style="margin-top:4px">Total ${spend ? usd(acc) : tokens(acc)}</div>`;
    bars += `<g class="draw" style="animation-delay:${i * 15}ms" data-tip="${esc(tip)}">${rects}</g>`;
  });
  const x = (i) => L + slot * i + slot / 2;
  document.getElementById('v-stack').innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Daily ${spend ? 'spend' : 'tokens'} by model, stacked">${yAxis(ticks, L, W - R, y, fmt)}${bars}${xDates(days, x, H - 6, 4)}</svg>`;

  const sum = d.cfg.models.map((_, mi) => days.reduce((a, x) => add(a, { ...x.perModel[mi] }), cell()));
  const grand = sum.reduce((s, c) => s + val(c), 0);
  document.getElementById('v-stack-legend').innerHTML = d.cfg.models.map((m, mi) => {
    const c = sum[mi];
    const detail = spend ? '' : `${tokens(c.input)} in · ${tokens(c.output)} out · ${tokens(c.read + c.w5 + c.w1)} cached`;
    return `<div class="mrow"><i style="background:${modelColor(m)}"></i><span>${esc(m.id)}</span><span class="detail">${detail}</span>
      <span class="val">${spend ? usd(c.cost) : tokens(totalTokens(c))}</span><span class="pct">${pct(val(c) / grand)}</span></div>`;
  }).join('');
}

function arc(cx, cy, r0, r1, a0, a1) {
  const p = (r, a) => [cx + r * Math.sin(a), cy - r * Math.cos(a)];
  const large = a1 - a0 > Math.PI ? 1 : 0;
  const [x0, y0] = p(r1, a0), [x1, y1] = p(r1, a1), [x2, y2] = p(r0, a1), [x3, y3] = p(r0, a0);
  return `M${x0},${y0}A${r1},${r1} 0 ${large} 1 ${x1},${y1}L${x2},${y2}A${r0},${r0} 0 ${large} 0 ${x3},${y3}Z`;
}

function drawDonut(d) {
  const total = d.combined.cost;
  // Only the top nine get a slice; at exactly ten, all ten would.
  const max = d.projects.length === 10 ? 10 : 9;
  const slices = d.projects.slice(0, max).map((p) => ({ name: p.name, cost: p.cost, color: p.color, logo: true }));
  const rest = d.projects.slice(max);
  if (rest.length) slices.push({ name: 'Others', cost: rest.reduce((s, p) => s + p.cost, 0), color: isLight() ? '#8C909B' : '#5D5D68', n: rest.length });
  let a = 0;
  const gap = 0.012;
  const paths = slices.map((s, i) => {
    const sweep = (s.cost / total) * Math.PI * 2;
    const path = arc(130, 130, 82, 124, a + gap, a + sweep - gap);
    a += sweep;
    const tip = `<b>${esc(s.name)}</b>${s.n ? `${s.n} projects<br>` : ''}${usd(s.cost)} · ${pct(s.cost / total)}`;
    return `<path d="${path}" fill="${s.color}" data-i="${i}" data-tip="${esc(tip)}" class="fade"/>`;
  }).join('');
  const legend = slices.map((s, i) => `<div class="drow" data-i="${i}"><i style="background:${s.color}"></i>
      ${s.logo ? `<span class="logo">${initials(s.name)}</span>` : '<span></span>'}<span class="n">${esc(s.name)}${s.n ? ` <small style="color:var(--text-faint)">${s.n} projects</small>` : ''}</span>
      <span class="c">${usd(s.cost)}</span><span class="p">${pct(s.cost / total)}</span></div>`).join('');
  const el = document.getElementById('v-donut');
  el.innerHTML = `<div class="donut"><svg viewBox="0 0 260 260" role="img" aria-label="Share of spend by project">${paths}</svg>
      <div class="donut-hole"><b>${usd(total)}</b><span>across ${d.projects.length} projects</span></div></div>
    <div class="dlegend">${legend}</div>`;
  // Hovering a slice or a row fades every other one, in the ring and the legend together.
  const setActive = (i) => {
    el.querySelectorAll('[data-i]').forEach((n) => n.classList.toggle('faded', i !== null && n.dataset.i !== String(i)));
  };
  el.querySelectorAll('[data-i]').forEach((n) => {
    n.addEventListener('pointerenter', () => setActive(n.dataset.i));
    n.addEventListener('pointerleave', () => setActive(null));
  });
}

function drawProjectCards(d) {
  const total = d.combined.cost;
  const html = d.projects.slice(0, 4).map((p, i) => {
    const bar = d.cfg.models.map((m, mi) => {
      const v = p.perModel[mi].cost;
      return v > 0 ? `<span style="width:${(v / p.cost) * 100}%;background:${modelColor(m)};animation-delay:${mi * 80}ms" data-tip="${esc(`<b>${m.id}</b>${usd(v)} · ${pct(v / p.cost)} of this project`)}"></span>` : '';
    }).join('');
    const unmerge = i === 0 ? `<div>↶ Unmerge ${esc(p.name)} (old)</div>` : '';
    return `<div class="pcard">
      <div class="pc-top"><span class="logo logo-lg">${initials(p.name)}</span>
        <div><div class="pc-name">${esc(p.name)}</div><div class="pc-path">C:\\Users\\you\\Projects\\${esc(p.name)}</div></div>
        <div class="pc-cost"><b>${usd(p.cost)}</b><span>${pct(p.cost / total)} of total</span></div></div>
      <div class="pc-bar">${bar}</div>
      <div class="pc-foot"><span>${tokens(p.tokens)} tokens</span><span>${duration(p.runtime)}</span><span>${count(p.msgs / 16 + 1)} sessions</span><span>${p.days} active days</span>
        <span class="open">View breakdown →</span>
        <button type="button" class="dots-btn" aria-expanded="false" aria-label="Options for ${esc(p.name)}">⋯</button></div>
      <div class="menu" hidden>
        <div><i style="background:${p.color}"></i>Change colour…</div>
        <div>✎ Rename…</div>
        <div>⇲ Merge into…</div>${unmerge}
        <div>⊘ Hide from project list</div>
        <hr>
        <div>▣ Set logo…</div>
        <div>▤ Open logos folder</div>
      </div></div>`;
  }).join('');
  const el = document.getElementById('v-pcards');
  el.innerHTML = html;
  el.querySelectorAll('.dots-btn').forEach((b) => b.addEventListener('click', (e) => {
    e.stopPropagation();
    const menu = b.closest('.pcard').querySelector('.menu');
    const open = menu.hidden;
    closeMenus();
    menu.hidden = !open;
    b.setAttribute('aria-expanded', String(open));
  }));
}

function closeMenus() {
  document.querySelectorAll('.pcard .menu').forEach((m) => { m.hidden = true; });
  document.querySelectorAll('.dots-btn').forEach((b) => b.setAttribute('aria-expanded', 'false'));
}

function drawTokens(d) {
  const codex = d.key === 'codex';
  document.getElementById('v-tokens-note').innerHTML = codex
    ? 'Input is only the uncached part of the prompt, which is why it looks small next to Cached. Reasoning is part of Output, so the row does not sum across.'
    : 'Cache writes are split by lifetime (5 minutes and 1 hour) and priced apart; the column shows their sum.';
  const cols = codex
    ? ['Model', d.cfg.Noun, 'Input', 'Cached', 'Output', 'Reasoning', 'Total tokens', 'Runtime', 'Cost']
    : ['Model', d.cfg.Noun, 'Input', 'Output', 'Cache write', 'Cache read', 'Total tokens', 'Runtime', 'Cost'];
  const row = (c) => codex
    ? [count(c.msgs), tokens(c.input), tokens(c.read), tokens(c.output), tokens(c.reasoning), tokens(totalTokens(c)), duration(c.runtime)]
    : [count(c.msgs), tokens(c.input), tokens(c.output), tokens(c.w5 + c.w1), tokens(c.read), tokens(totalTokens(c)), duration(c.runtime)];
  const body = d.cfg.models.map((m, i) => ({ m, c: d.perModel[i] })).sort((a, b) => b.c.cost - a.c.cost).map(({ m, c }) =>
    `<tr><td><span class="mcell"><i style="background:${modelColor(m)}"></i>${esc(m.id)}</span></td>${row(c).map((v) => `<td class="num">${v}</td>`).join('')}<td class="num cost">${usd(c.cost)}</td></tr>`).join('');
  document.getElementById('v-tokens').innerHTML = `<div class="table-wrap"><table>
    <thead><tr>${cols.map((h, i) => `<th class="${i ? 'num' : ''}">${h}</th>`).join('')}</tr></thead>
    <tbody>${body}</tbody>
    <tfoot><tr><td>All models</td>${row(d.combined).map((v) => `<td class="num">${v}</td>`).join('')}<td class="num cost">${usd(d.combined.cost)}</td></tr></tfoot>
  </table></div>`;
}

/* Model prices: an invented unpriced model shows how the app flags one. */
const priceEdits = { claude: {}, codex: {} };
/* The table's dragged order, per agent; empty is the default order (ModelSettings.Arrange). */
const priceOrder = { claude: [], codex: [] };
let editing = null;
function arrange(rows, order) {
  const rank = new Map(order.map((id, i) => [id, i]));
  return rows.map((r, i) => [r, i])
    .sort((a, b) => (rank.get(a[0].id) ?? Infinity) - (rank.get(b[0].id) ?? Infinity) || a[1] - b[1])
    .map(([r]) => r);
}
function drawPrices(d) {
  const codex = d.key === 'codex';
  const fields = codex ? [['input', 'Input'], ['read', 'Cached input'], ['output', 'Output']]
    : [['input', 'Input'], ['w5', 'Write 5m'], ['w1', 'Write 1h'], ['read', 'Cache read'], ['output', 'Output']];
  const byDefault = d.cfg.models.map((m) => ({ id: m.id, color: modelColor(m), rate: priceEdits[d.key][m.id]?.rate ?? m.rate, long: m.long }));
  const preview = codex ? 'gpt-preview-1' : 'claude-preview-1';
  const pe = priceEdits[d.key][preview];
  byDefault.push({ id: preview, color: isLight() ? '#8C909B' : '#9C7A6C', rate: pe?.rate ?? null, unpriced: !pe?.rate });
  const rows = arrange(byDefault, priceOrder[d.key]);
  const name = (r) => priceEdits[d.key][r.id]?.name || r.id;
  const money = (v) => (v === undefined || v === null ? '-' : '$' + (+v).toString());
  const body = rows.map((r, i) => `<tr><td><span class="mcell"><button type="button" class="grip" data-grip="${i}" title="Drag to reorder" aria-label="Move ${esc(name(r))}: drag, or press the up and down arrow keys"></button><i style="background:${priceEdits[d.key][r.id]?.color ?? r.color}"></i>${esc(name(r))}${r.unpriced ? '<span class="flag">unpriced</span>' : ''}</span></td>
      ${fields.map(([k]) => `<td class="num">${r.rate ? money(r.rate[k]) : '-'}</td>`).join('')}
      <td class="num"><button type="button" class="tbl-btn${r.unpriced ? ' primary' : ''}" data-edit="${esc(r.id)}" aria-expanded="${editing === r.id}">${r.unpriced ? 'Set price' : 'Edit'}</button></td></tr>`).join('');
  const el = document.getElementById('v-prices');
  const editRow = rows.find((r) => r.id === editing);
  const swatches = d.cfg.swatches[isLight() ? 'light' : 'dark'];
  const reorder = priceOrder[d.key].length ? '<div class="note"><button type="button" class="tbl-btn" data-act="order">Default order</button></div>' : '';
  el.innerHTML = `<div class="table-wrap"><table>
      <thead><tr><th>Model</th>${fields.map(([, h]) => `<th class="num">${h}</th>`).join('')}<th></th></tr></thead>
      <tbody>${body}</tbody></table></div>
    ${codex ? `<div class="note">On GPT-5.6 Sol and GPT-6.1 Sol, a request whose prompt is over 272K tokens is billed at 2x input and cache and 1.5x output, for the whole request.</div>` : '<div class="note">USD per million tokens.</div>'}${reorder}
    <div class="editor" ${editRow ? '' : 'hidden'}>
      ${editRow ? `<label class="name-field">Name<input type="text" maxlength="60" data-name value="${esc(name(editRow))}" placeholder="${esc(editRow.id)}"></label>
      <h4>Price for ${esc(editRow.id)}, USD per million tokens</h4>
      <div class="fields">${fields.map(([k, h]) => `<label>${h}<input type="number" min="0" step="0.01" data-f="${k}" value="${editRow.rate?.[k] ?? ''}"></label>`).join('')}</div>
      <div class="swatches" role="group" aria-label="Colour">${swatches.map((c) => `<button type="button" style="background:${c}" data-c="${c}" aria-label="${c}" aria-pressed="${(priceEdits[d.key][editRow.id]?.color ?? editRow.color).toUpperCase() === c}"></button>`).join('')}</div>
      <div class="ed-actions">
        <button type="button" class="tbl-btn" data-act="fill"${codex ? ' hidden' : ''}>Cache rates from input</button>
        <button type="button" class="tbl-btn" data-act="reset">Reset</button>
        <button type="button" class="tbl-btn" data-act="cancel">Cancel</button>
        <button type="button" class="tbl-btn primary" data-act="save">Save</button>
      </div>` : ''}
    </div>`;
  el.querySelectorAll('[data-edit]').forEach((b) => b.addEventListener('click', () => {
    editing = editing === b.dataset.edit ? null : b.dataset.edit;
    drawPrices(d);
  }));
  el.querySelector('[data-act="order"]')?.addEventListener('click', () => { priceOrder[d.key] = []; drawPrices(d); });

  const move = (from, to, refocus) => {
    const ids = rows.map((r) => r.id);
    ids.splice(to, 0, ...ids.splice(from, 1));
    priceOrder[d.key] = ids;
    drawPrices(d);
    if (refocus) el.querySelector(`[data-grip="${to}"]`)?.focus();
  };
  el.querySelectorAll('[data-grip]').forEach((grip) => {
    const from = +grip.dataset.grip;
    grip.addEventListener('keydown', (e) => {
      const to = e.key === 'ArrowUp' ? from - 1 : e.key === 'ArrowDown' ? from + 1 : -1;
      if (to < 0 || to >= rows.length) return;
      e.preventDefault();
      move(from, to, true);
    });
    // As in the app: the row follows the pointer, an accent rule marks where it lands.
    grip.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      grip.setPointerCapture(e.pointerId);
      const trs = [...el.querySelectorAll('tbody tr')];
      const boxes = trs.map((t) => t.getBoundingClientRect());
      const row = trs[from];
      row.classList.add('lifted');
      el.classList.add('dragging');
      // Over the lifted row, which would otherwise hide the rule where it lands.
      const wrap = el.querySelector('.table-wrap');
      const wrapTop = wrap.getBoundingClientRect().top;
      const rule = document.createElement('div');
      rule.className = 'drop-rule';
      rule.hidden = true;
      wrap.append(rule);
      let to = from;
      const onMove = (ev) => {
        const dy = Math.max(boxes[0].top - boxes[from].top, Math.min(boxes.at(-1).bottom - boxes[from].bottom, ev.clientY - e.clientY));
        row.style.transform = `translateY(${dy}px)`;
        const middle = boxes[from].top + boxes[from].height / 2 + dy;
        to = boxes.filter((b, i) => i !== from && b.top + b.height / 2 < middle).length;
        rule.hidden = to === from;
        rule.style.top = `${(to < from ? boxes[to].top : boxes[to].bottom) - wrapTop - 1}px`;
      };
      const onEnd = (ev) => {
        grip.removeEventListener('pointermove', onMove);
        grip.removeEventListener('pointerup', onEnd);
        grip.removeEventListener('pointercancel', onEnd);
        el.classList.remove('dragging');
        if (ev.type === 'pointerup' && to !== from) move(from, to, false);
        else drawPrices(d);
      };
      grip.addEventListener('pointermove', onMove);
      grip.addEventListener('pointerup', onEnd);
      grip.addEventListener('pointercancel', onEnd);
    });
  });
  if (!editRow) return;
  let color = priceEdits[d.key][editRow.id]?.color ?? null;
  el.querySelectorAll('[data-c]').forEach((b) => b.addEventListener('click', () => {
    color = b.dataset.c;
    el.querySelectorAll('[data-c]').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
  }));
  const input = (k) => el.querySelector(`[data-f="${k}"]`);
  el.querySelector('[data-act="fill"]').addEventListener('click', () => {
    const base = parseFloat(input('input').value);
    if (!(base >= 0)) return;
    input('w5').value = +(base * 1.25).toFixed(4);
    input('w1').value = +(base * 2).toFixed(4);
    input('read').value = +(base * 0.1).toFixed(4);
  });
  el.querySelector('[data-act="reset"]').addEventListener('click', () => { delete priceEdits[d.key][editRow.id]; editing = null; drawPrices(d); });
  el.querySelector('[data-act="cancel"]').addEventListener('click', () => { editing = null; drawPrices(d); });
  el.querySelector('[data-act="save"]').addEventListener('click', () => {
    const rate = {};
    for (const [k] of fields) rate[k] = parseFloat(input(k).value) || 0;
    // Blank, or the model string typed back, is no name at all.
    const typed = el.querySelector('[data-name]').value.trim();
    priceEdits[d.key][editRow.id] = { rate, color, name: typed && typed !== editRow.id ? typed : null };
    editing = null;
    drawPrices(d);
  });
}

function drawDaily(d) {
  const codex = d.key === 'codex';
  const p = d.projects[0];
  const rnd = mulberry32(7);
  const active = d.days.filter((x) => x.projects.has(p.name)).slice(-3).reverse();
  const cols = codex ? ['Date', 'Model', d.cfg.Noun, 'Input', 'Output', 'Cached', 'Runtime', 'Cost']
    : ['Date', 'Model', d.cfg.Noun, 'Input', 'Output', 'Cache write', 'Cache read', 'Runtime', 'Cost'];
  let body = '';
  active.forEach((day) => {
    const share = day.projects.get(p.name) / day.combined.cost;
    const rows = d.cfg.models.map((m, mi) => ({ m, c: day.perModel[mi] })).filter(({ c }) => c.msgs > 0).sort((a, b) => b.c.cost - a.c.cost);
    rows.forEach(({ m, c }, k) => {
      const f = share * (0.85 + rnd() * 0.3);
      const vals = codex ? [count(Math.max(1, c.msgs * f)), tokens(c.input * f), tokens(c.output * f), tokens(c.read * f), duration(c.runtime * f)]
        : [count(Math.max(1, c.msgs * f)), tokens(c.input * f), tokens(c.output * f), tokens((c.w5 + c.w1) * f), tokens(c.read * f), duration(c.runtime * f)];
      body += `<tr><td class="dim">${k === 0 ? fmtDayLong(day.date) : ''}</td><td><span class="mcell"><i style="background:${modelColor(m)}"></i>${esc(m.id)}</span></td>
        ${vals.map((v) => `<td class="num">${v}</td>`).join('')}<td class="num cost">${usd(c.cost * f)}</td></tr>`;
    });
  });
  document.getElementById('v-daily').innerHTML = `<div class="table-wrap"><table>
    <thead><tr>${cols.map((h, i) => `<th class="${i > 1 ? 'num' : ''}">${h}</th>`).join('')}</tr></thead><tbody>${body}</tbody></table></div>
    <div class="note">${esc(p.name)}, the three most recent active days.</div>`;
}

function drawTray() {
  const today = (k) => DATA[k].all[DATA[k].all.length - 1].combined.cost;
  document.getElementById('v-tray').innerHTML = `
    <div class="tray-col"><span class="tray-cap">Hover</span>
      <div class="tooltip-mock">AI Usage<br>Claude Code today: ${usd(today('claude'))}<br>Codex today: ${usd(today('codex'))} (API-equiv.)</div>
      <div class="taskbar"><span>⌃</span><span class="sel"><img src="favicon.svg" alt="AI Usage tray icon"></span><span>ENG</span><span>9:41 PM</span></div></div>
    <div class="tray-col"><span class="tray-cap">Right-click</span>
      <div class="menu menu-static">
        <div>Open AI Usage</div><div>Refresh now</div><hr>
        <div>✓ Start at login</div><div>✓ Refresh when transcripts change</div><hr>
        <div>Exit</div>
      </div></div>`;
}

function drawAll() {
  const d = DATA[agent];
  drawHead(d); drawSpend(d); drawCost(d); drawCards(d); drawHeat(d); drawProjects(d);
  drawStack(d); drawDonut(d); drawProjectCards(d); drawTokens(d); drawPrices(d); drawDaily(d); drawTray();
}

/* ------------------------------------------------------------------ wiring */

function wireTooltip() {
  const tip = document.getElementById('tip');
  const hide = () => { tip.hidden = true; };
  document.addEventListener('pointermove', (e) => {
    const t = e.target.closest && e.target.closest('[data-tip]');
    if (!t) return hide();
    tip.innerHTML = t.getAttribute('data-tip');
    tip.hidden = false;
    const r = tip.getBoundingClientRect();
    let left = e.clientX + 14, top = e.clientY + 14;
    if (left + r.width > window.innerWidth - 8) left = e.clientX - r.width - 14;
    if (top + r.height > window.innerHeight - 8) top = e.clientY - r.height - 14;
    tip.style.left = `${Math.max(8, left)}px`;
    tip.style.top = `${Math.max(8, top)}px`;
  });
  document.addEventListener('scroll', hide, { passive: true });
}

function pickRadio(group, btn) {
  group.querySelectorAll('[role="radio"]').forEach((x) => x.setAttribute('aria-checked', String(x === btn)));
}

/* Module scripts run once the document is parsed, so the gallery exists. */
const agentSeg = document.getElementById('agent-seg');
const pickAgent = (b) => {
  agent = b.dataset.agent;
  pickRadio(agentSeg, b);
  document.getElementById('charts').dataset.agent = agent;
  editing = null;
};
agentSeg.querySelectorAll('[role="radio"]').forEach((b) => b.addEventListener('click', () => { pickAgent(b); drawAll(); }));
// ?agent=codex opens the gallery on Codex, so either view can be linked.
if (new URLSearchParams(location.search).get('agent') === 'codex') pickAgent(agentSeg.querySelector('[data-agent="codex"]'));

const range = document.getElementById('proj-range');
range.querySelectorAll('[role="radio"]').forEach((b) => b.addEventListener('click', () => { projRange = b.dataset.range; pickRadio(range, b); drawProjects(DATA[agent]); }));
const mode = document.getElementById('stack-mode');
mode.querySelectorAll('[role="radio"]').forEach((b) => b.addEventListener('click', () => { stackMode = b.dataset.mode; pickRadio(mode, b); drawStack(DATA[agent]); }));

document.addEventListener('click', (e) => { if (!e.target.closest('.menu')) closeMenus(); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeMenus(); });
// ThemeToggle.astro, and the system theme while no choice is pinned.
document.addEventListener('themechange', drawAll);

wireTooltip();
drawAll();
