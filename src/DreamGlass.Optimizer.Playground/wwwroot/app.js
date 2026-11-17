'use strict';

// If the page fails to start (for example a browser that kept an old cached page), say so instead of hanging.
window.addEventListener('error', (e) => {
  const box = document.getElementById('results');
  if (!box || box.dataset.startupError) return;
  box.dataset.startupError = '1';
  box.insertAdjacentHTML('afterbegin', `<div class="callout bad" style="background:#fbe5e3;color:#7a1610;padding:10px 12px;border-radius:8px">
    <b>The page did not load correctly.</b> Reload it without the cache: <b>Ctrl+F5</b> (Windows) or <b>Cmd+Shift+R</b> (Mac).
    <div style="font-size:12px;margin-top:4px">${String(e.message || e.error || '').replace(/[<>&]/g, '')}</div></div>`);
});

// Tolerate an older copy of index.html: add the few elements this script expects if they are missing.
(function ensureShell() {
  const preset = document.getElementById('preset');
  if (!document.getElementById('preset-label')) {
    const span = preset?.closest('label')?.querySelector('span');
    if (span) span.id = 'preset-label';
  }
  if (!document.getElementById('form-issues')) {
    const div = document.createElement('div');
    div.id = 'form-issues';
    div.className = 'form-issues';
    div.hidden = true;
    document.querySelector('.run-bar')?.before(div);
  }
})();

// ---------- helpers ------------------------------------------------------------------------------------------
const $ = (sel, root = document) => root.querySelector(sel);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const clone = (o) => JSON.parse(JSON.stringify(o));
const num = (n, d = 4) => (n == null ? '–' : Number(n).toLocaleString(undefined, { maximumFractionDigits: d }));
const mm = (n) => (n == null ? '–' : `${num(n, 1)} mm`);
const m2 = (n) => (n == null ? '–' : `${num(n, 2)} m²`);
const money = (n, cur) => (n == null ? '–' : `${Number(n).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}${cur ? ' ' + esc(cur) : ''}`);
const pct = (n) => (n == null ? '–' : `${(Number(n) * 100).toFixed(1)} %`);
const plural = (n, one, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;
/** A label with a hover/tap explanation (dotted underline). */
const tipped = (text, tip) => `<span class="tipped" tabindex="0" data-tip="${esc(tip)}">${text}</span>`;

const ORIENT = ['None', 'Vertical', 'Horizontal'];
const AXES = ['Width', 'Length'];
// Plain names for a roll's pattern axis (JSON keeps Width/Length).
const AXIS_LABEL = { Width: 'Across the roll', Length: 'Along the length' };

const pad = (t, r = t, b = t, l = r) => ({ top: t, right: r, bottom: b, left: l });
const piece = (id, w, l, q = 1, orientation = 'None', p = pad(0)) => ({ id, label: null, widthMm: w, lengthMm: l, quantity: q, orientation, paddingMm: p });
const roll = (offerId, widthMm, price, extra = {}) => ({
  offerId, widthMm, pricePerSquareMetre: price, currency: 'EUR', minOrderMetres: 1, maxOrderMetres: null,
  orderIncrementMetres: 1, patternAxis: 'Width', fixedLengthMm: null, ...extra,
});
const stock = (stockId, w, l, axis = 'Width', version = 1, bin = null) => ({
  stockId, widthMm: w, lengthMm: l, patternAxis: axis, version, materialId: null, available: true, binReference: bin,
});
const job = (o) => ({
  schemaVersion: 1, jobRevisionId: 'playground', materialId: 'fabric', materialDirectional: true,
  pieces: [], rollOffers: [], stock: [],
  minimumReusableRemnant: { minWidthMm: 200, minLengthMm: 300, minAreaM2: null, maxAspectRatio: null },
  precision: { unitsPerMm: 10 },
  search: { timeBudgetMs: 5000, seed: 12345, maxCandidates: 3 },
  ...o,
});

function mulberry32(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function randomJob(instances, seed) {
  const r = mulberry32(seed);
  const int = (n) => Math.floor(r() * n);
  const pieces = [];
  let left = instances, id = 0;
  while (left > 0) {
    const q = Math.min(left, 1 + int(4));
    left -= q;
    pieces.push(piece(`P${++id}`, 200 + 10 * int(100), 300 + 10 * int(150), q, ORIENT[int(3)], int(3) === 0 ? pad(5) : pad(0)));
  }
  const stockList = [1, 2, 3, 4].map((i) => stock(`R${i}`, 400 + 50 * int(40), 600 + 50 * int(60), int(2) ? 'Width' : 'Length', 1 + int(5), `A-${10 + i}`));
  return job({
    jobRevisionId: `mixed-${instances}-${seed}`,
    pieces,
    rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 }), roll('roll-1400', 1400, 13.1, { maxOrderMetres: 50 })],
    stock: stockList,
    search: { timeBudgetMs: 8000, seed, maxCandidates: 4 },
  });
}

// Two 7,225 mm wide × 2,600 mm high curtains, side by side along the roll: 14,450 mm of running length.
const curtains = () => [piece('Curtain', 7225, 2600, 2, 'Vertical')];

const PRESETS = [
  {
    name: 'Spec example',
    note: 'Two 600 × 900 panels whose pattern must run top to bottom, plus one panel with 5 mm padding. There is a 2,800 mm high supplier roll and one 1,400 × 800 remnant in the warehouse. Look at which panels go on the remnant and how much is ordered.',
    make: () => job({
      materialId: 'material-id',
      pieces: [piece('P1', 600, 900, 2, 'Vertical'), piece('P2', 450, 700, 1, 'None', pad(5))],
      rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 })],
      stock: [stock('R1', 800, 1400, 'Width', 4)],
    }),
  },
  {
    name: 'Order from a supplier roll (15 of 31 m)',
    note: 'Two curtains, each 7,225 mm wide and 2,600 mm high, side by side use 14,450 mm of a 2,800 mm high roll. The supplier\'s roll holds 31 m, but only 15 m is ordered (rounded up to whole metres). The other 16 m stay with the supplier, so they are not bought and not shown.',
    make: () => job({ pieces: curtains(), rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 31 })] }),
  },
  {
    name: 'Whole roll in the warehouse (31 m)',
    note: 'The same curtains, but a full 31 m roll is already in the warehouse. Nothing is ordered, and the 16,550 mm left on that roll is shown as a reusable remnant.',
    make: () => job({
      pieces: curtains(),
      rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 31 })],
      stock: [stock('ROLL-31', 2800, 31000, 'Width', 1, 'Rack 2')],
    }),
  },
  {
    name: 'Order with padding (17 m)',
    note: 'A 16,500 mm wide curtain with 90 mm padding on the left and on the right needs 16,500 + 180 = 16,680 mm of running length, so 17 m is ordered.',
    make: () => job({
      pieces: [piece('Curtain', 16500, 2400, 1, 'Vertical', { top: 0, right: 90, bottom: 0, left: 90 })],
      rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 })],
    }),
  },
  {
    name: 'Remnant with the pattern along its length',
    note: 'On this remnant the pattern runs along its length (left to right on the sheet), so the panel is cut lying on its side. That way its pattern still runs top to bottom on the finished panel.',
    make: () => job({
      pieces: [piece('P1', 600, 900, 1, 'Vertical', { top: 10, right: 0, bottom: 30, left: 20 })],
      stock: [stock('R1', 800, 1400, 'Length', 3, 'B-12')],
    }),
  },
  {
    name: 'Warehouse only',
    note: 'There are no supplier rolls, so every panel must come from remnants you own and nothing is ordered. Remnants that aren\'t cut are not counted.',
    make: () => job({
      pieces: [piece('Seat', 500, 400, 4, 'Vertical'), piece('Back', 450, 600, 2, 'Vertical')],
      stock: [stock('R1', 1200, 1000, 'Width', 2, 'A-03'), stock('R2', 1000, 700, 'Width', 5, 'A-07'), stock('R3', 3000, 3000, 'Width', 1, 'C-01')],
    }),
  },
  {
    name: 'Smart remnant use',
    note: 'Putting the small panel on remnant A first would leave no room for the large one and force an order. The optimiser puts each panel on the right remnant and orders nothing.',
    make: () => job({
      pieces: [piece('S', 500, 500, 1, 'Vertical'), piece('L', 1000, 1000, 1, 'Vertical')],
      rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 })],
      stock: [stock('A', 1000, 1000, 'Width', 1), stock('B', 600, 600, 'Width', 1)],
    }),
  },
  {
    name: 'Two roll heights',
    note: 'Two 1,000 × 1,400 panels fit one above the other in 1 m of a 2,800 mm high roll (€35), or side by side in 2 m of a 1,400 mm high roll (€25.20). The cheaper order wins and the other is shown as an alternative plan.',
    make: () => job({
      pieces: [piece('P', 1000, 1400, 2, 'Vertical')],
      rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 }), roll('roll-1400', 1400, 9, { maxOrderMetres: 50 })],
    }),
  },
  {
    name: 'Row of panels (workshop sketch)',
    note: 'On the 1,400 mm roll: two 1,000 × 1,400 panels, then three 800 × 500 panels (810 × 510 with padding) side by side over a 1,400 × 500 panel (1,440 × 540 with padding) with a 90 mm offcut beside it. 3,530 mm used, 4 m ordered (€50.40), and one 470 × 1,400 piece goes to the warehouse.',
    make: () => job({
      pieces: [piece('P', 1000, 1400, 2, 'Vertical'), piece('P2', 800, 500, 3, 'Horizontal', pad(5)), piece('P3', 1400, 500, 1, 'Vertical', pad(20))],
      rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 }), roll('roll-1400', 1400, 9, { maxOrderMetres: 50 })],
    }),
  },
  {
    name: 'Mixed job (60 pieces)',
    note: 'A random mix of 60 panels, some with grain and some with padding. There are two roll widths and four remnants, some with a sideways pattern. It shows a realistic workload.',
    make: () => randomJob(60, 7),
  },
  {
    name: 'Impossible job',
    note: 'Two 600 × 600 panels fit the 1,000 × 1,000 remnant by area but not by shape, and there is no roll to order from. The optimiser proves that no plan exists.',
    make: () => job({ pieces: [piece('P', 600, 600, 2)], stock: [stock('R', 1000, 1000)] }),
  },
  {
    name: 'Piece too large',
    note: 'With 5 mm padding, a 2,800 mm high panel whose pattern must run top to bottom needs 2,810 mm of roll height, but the roll is only 2,800 mm high. The form flags it before you run, and running shows the full error.',
    make: () => job({ pieces: [piece('P1', 1000, 2800, 1, 'Vertical', pad(5))], rollOffers: [roll('roll-2800', 2800, 12.5, { maxOrderMetres: 50 })] }),
  },
];

// ---------- explanations ---------------------------------------------------------------------------------------
const TIP = {
  preset: 'Ready-made example jobs. Picking one replaces the form with that job; you can then change anything.',
  directional: 'Tick when the fabric has a pattern or nap that must run one way: panels with a grain setting are then never turned the wrong way. Untick for plain fabric, where any panel may be turned 90° to save fabric.',
  more: 'Rules for reusable leftovers and how long and how the optimiser searches. The defaults are fine for most jobs.',
  policyOn: 'Untick to report every leftover as scrap.',
  minWidth: 'A leftover is kept as a reusable remnant only if its short side is at least this long (mm)…',
  minLength: '…and its long side is at least this long (mm). Smaller leftovers count as scrap.',
  timeBudget: 'How long the optimiser may search, in milliseconds (at most 30,000 here). Small jobs usually finish sooner.',
  seed: 'Starting number for the randomised search. Change it to explore other plans; keep it to repeat a run.',
  maxCandidates: 'How many alternative plans to show.',
  objective: 'How plans are ranked. "Cheapest first" (the approved order): lowest price, then least scrap, then the biggest leftover you can keep for the next jobs, then the least roll length used, then fewest cuts. "Least leftover first" keeps all leftover (scrap and keepable) low first.',
  scrapTolerance: 'Scrap differences smaller than this count as equal, so a few square centimetres of extra scrap cannot beat a much bigger keepable leftover. 0.01 m² is a 100 × 100 mm square. Used by "Cheapest first" only.',
  deterministic: 'Search by a fixed amount of work instead of time, so the same job and seed always give exactly the same plans.',
  work: 'Amount of search work in repeatable mode. More work can find better plans for big jobs.',
  materialId: 'Material reference for the job. Shown on results; informational in the playground.',
  precision: 'Smallest size step the optimiser works with. Sizes finer than this are rejected, never rounded.',
};

const COLS = {
  pieces: [
    { k: 'id', label: 'ID', t: 'str', tip: 'Name of the panel. Each copy is labelled ID#1, ID#2 … on the cutting sheets.' },
    { k: 'widthMm', label: 'Width mm', t: 'num', tip: 'Finished width of the panel (left to right) in mm, without padding.' },
    { k: 'lengthMm', label: 'Height mm', t: 'num', tip: 'Finished height of the panel (top to bottom) in mm, without padding. Called lengthMm in the JSON.' },
    { k: 'quantity', label: 'Qty', t: 'int', tip: 'How many identical panels to cut.' },
    { k: 'orientation', label: 'Grain', t: 'sel', opts: ORIENT, tip: 'Which way the pattern must run on the finished panel. Vertical = top to bottom, Horizontal = left to right, None = either way (the panel may be turned 90° to save fabric). Only matters for directional fabric.' },
    { k: 'paddingMm', label: 'Padding mm', t: 'pad', tip: 'Extra fabric around the panel (seams, hems) in mm. One number for all four sides, or four numbers for Top, Right, Bottom, Left. Cut-out size = finished size + padding.' },
  ],
  rollOffers: [
    { k: 'offerId', label: 'ID', t: 'str', tip: 'Name of the supplier roll (product or offer).' },
    { k: 'widthMm', label: 'Height mm', t: 'num', tip: 'The roll\'s fixed height (also called its width), e.g. 2800. The supplier never cuts this; you only order running length. Called widthMm in the JSON.' },
    { k: 'pricePerSquareMetre', label: '€ / m²', t: 'num', tip: 'Price per square metre. Used to cost the order and to choose between rolls.' },
    { k: 'maxOrderMetres', label: 'Roll length m', t: 'optint', tip: 'How many metres the supplier has on this roll (empty = no limit). You only order what the layout needs, rounded up to whole metres. The rest stays with the supplier and is not shown.' },
    { k: 'minOrderMetres', label: 'Min order m', t: 'int', tip: 'Smallest length the supplier will cut, in whole metres.' },
    { k: 'orderIncrementMetres', label: 'Step m', t: 'int', tip: 'Order lengths go up in steps of this many metres. 1 = any whole metre.' },
    { k: 'patternAxis', label: 'Pattern runs', t: 'sel', opts: AXES, optLabels: AXIS_LABEL, tip: 'Which way the fabric\'s pattern runs. Across the roll: top to bottom when the roll is unrolled left to right, so panels stand upright across the roll\'s height (usual for wide fabric). Along the length: the pattern runs along the running length, so pattern-locked panels are cut lying on their side.' },
  ],
  stock: [
    { k: 'stockId', label: 'ID', t: 'str', tip: 'Your stock id for a remnant or a whole roll you already own.' },
    { k: 'lengthMm', label: 'Length mm', t: 'num', tip: 'Length of the piece along the running length, in mm. For a whole roll, its full running length (31 m = 31000).' },
    { k: 'widthMm', label: 'Height mm', t: 'num', tip: 'Height of the piece: the roll\'s fixed dimension it was cut from, in mm. Called widthMm in the JSON.' },
    { k: 'patternAxis', label: 'Pattern runs', t: 'sel', opts: AXES, optLabels: AXIS_LABEL, tip: 'Which way the pattern runs on this piece. Across the roll: top to bottom (usual). Along the length: left to right along its length.' },
    { k: 'binReference', label: 'Bin', t: 'optstr', tip: 'Where it is stored. Printed on the cutting sheet so it can be found.' },
    { k: 'excludeReason', label: 'Leave out because', t: 'optstr', tip: 'Leave empty to offer this piece to the optimiser. Type a reason (damaged, held for another job, wrong shade…) to leave it out: it is not used, and the reason is recorded with the job.' },
  ],
};
const SECTIONS = {
  pieces: { title: 'Pieces', hint: 'The panels to cut.' },
  rollOffers: { title: 'Supplier rolls', hint: 'Fabric you can order. Only the metres the layout needs are ordered, rounded up to whole metres.' },
  stock: { title: 'Warehouse stock', hint: 'Remnants and whole rolls you already own. Whatever is left after cutting is shown as a reusable remnant.' },
};
const NEW_ROW = {
  pieces: () => piece(`P${state.pieces.length + 1}`, 500, 500, 1, 'None'),
  rollOffers: () => roll(`roll-${state.rollOffers.length + 1}`, 1600, 12, { maxOrderMetres: 50 }),
  stock: () => stock(`R${state.stock.length + 1}`, 1000, 1000),
};

// ---------- tooltip (one floating element, so table scroll areas never clip it) --------------------------------
const tipEl = document.createElement('div');
tipEl.className = 'tooltip';
tipEl.setAttribute('role', 'tooltip');
tipEl.hidden = true;
document.body.append(tipEl);
let tipFor = null;
function showTip(el) {
  if (tipFor === el) return;
  tipFor = el;
  tipEl.textContent = el.dataset.tip;
  tipEl.hidden = false;
  const r = el.getBoundingClientRect();
  const w = Math.min(320, window.innerWidth - 24);
  tipEl.style.maxWidth = `${w}px`;
  const tw = tipEl.offsetWidth, th = tipEl.offsetHeight;
  let x = Math.min(Math.max(12, r.left + r.width / 2 - tw / 2), window.innerWidth - tw - 12);
  let y = r.bottom + 8;
  if (y + th > window.innerHeight - 8) y = Math.max(8, r.top - th - 8);
  tipEl.style.left = `${x}px`;
  tipEl.style.top = `${y}px`;
}
function hideTip() { tipFor = null; tipEl.hidden = true; }
document.addEventListener('mouseover', (e) => { const t = e.target.closest?.('[data-tip]'); if (t) showTip(t); else hideTip(); });
document.addEventListener('focusin', (e) => { const t = e.target.closest?.('[data-tip]'); if (t) showTip(t); else hideTip(); });
document.addEventListener('scroll', hideTip, true);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideTip(); });

// ---------- state --------------------------------------------------------------------------------------------
let state = PRESETS[0].make();
const runOpts = { deterministic: false, workBudget: 2500, objective: 'cheapest', scrapToleranceM2: 0.01 };
let response = null;
let selected = 0;
let controller = null;
let mode = 'form';
let issues = { errors: [], warnings: [] };
let moreOpen = false;

// ---------- form -------------------------------------------------------------------------------------------------
const padText = (p) => {
  if (!p) return '0';
  const v = [p.top, p.right, p.bottom, p.left];
  return v.every((x) => x === v[0]) ? String(v[0]) : v.join(',');
};

function parseValue(t, raw) {
  const s = raw.trim();
  switch (t) {
    case 'str': return { ok: true, v: raw };
    case 'optstr': return { ok: true, v: s === '' ? null : raw };
    case 'sel': return { ok: true, v: raw };
    case 'num': case 'int': {
      if (s === '') return { ok: true, v: 0 };
      const n = Number(s);
      return { ok: Number.isFinite(n) && (t !== 'int' || Number.isInteger(n)), v: n };
    }
    case 'optnum': case 'optint': {
      if (s === '') return { ok: true, v: null };
      const n = Number(s);
      return { ok: Number.isFinite(n) && (t !== 'optint' || Number.isInteger(n)), v: n };
    }
    case 'pad': {
      const parts = s.split(/[\s,;]+/).filter(Boolean).map(Number);
      if (parts.length === 0) return { ok: true, v: pad(0) };
      if (!(parts.length === 1 || parts.length === 4) || parts.some((n) => !Number.isFinite(n))) return { ok: false };
      return { ok: true, v: parts.length === 1 ? pad(parts[0]) : pad(parts[0], parts[1], parts[2], parts[3]) };
    }
    default: return { ok: false };
  }
}

function cellHtml(list, i, col, row) {
  const attrs = `data-list="${list}" data-i="${i}" data-k="${col.k}" data-t="${col.t}" aria-label="${esc(col.label)} row ${i + 1}" title="${esc(col.tip)}"`;
  if (col.t === 'sel') {
    return `<select ${attrs}>${col.opts.map((o) => `<option value="${o}"${row[col.k] === o ? ' selected' : ''}>${esc(col.optLabels?.[o] ?? o)}</option>`).join('')}</select>`;
  }
  const value = col.t === 'pad' ? padText(row[col.k]) : row[col.k] ?? '';
  const numeric = ['num', 'int', 'optnum', 'optint'].includes(col.t);
  const placeholder = col.k === 'maxOrderMetres' ? ' placeholder="no limit"' : '';
  return `<input ${attrs} value="${esc(value)}"${numeric ? ' inputmode="decimal"' : ''}${placeholder}>`;
}

function tableHtml(list) {
  const cols = COLS[list];
  const { title, hint } = SECTIONS[list];
  const rows = state[list] || [];
  const fixed = list === 'rollOffers' ? rows.filter((r) => r.fixedLengthMm != null) : [];
  return `<fieldset>
    <div class="legend-row"><legend>${title} <span class="muted small">(${rows.length})</span></legend>
      <button type="button" class="btn small" data-add="${list}">+ Add</button></div>
    <p class="muted small hint">${hint}</p>
    ${fixed.length ? `<p class="small warn-text">⚠ ${fixed.map((r) => esc(r.offerId)).join(', ')} ${fixed.length > 1 ? 'have' : 'has'} a fixed length set in the JSON (test mode): the whole length is treated as bought. Remove <code>fixedLengthMm</code> to order only what is needed.</p>` : ''}
    ${rows.length === 0 ? '<p class="muted small">None.</p>' : `<div class="table-wrap"><table class="edit" data-table="${list}">
      <thead><tr>${cols.map((c) => `<th>${tipped(c.label, c.tip)}</th>`).join('')}<th></th></tr></thead>
      <tbody>${rows.map((row, i) => `<tr data-row="${list}-${i}">${cols.map((c) => `<td>${cellHtml(list, i, c, row)}</td>`).join('')}
        <td class="del"><button type="button" class="icon-btn" data-del="${list}" data-i="${i}" title="Remove row" aria-label="Remove row ${i + 1}">×</button></td></tr>`).join('')}</tbody>
    </table></div>`}
  </fieldset>`;
}

function moreSummary() {
  const pol = state.minimumReusableRemnant;
  const keep = pol ? `leftovers ≥ ${num(pol.minWidthMm, 1)} × ${num(pol.minLengthMm, 1)} mm are kept` : 'all leftovers are scrap';
  const search = runOpts.deterministic ? `repeatable, ${num(runOpts.workBudget, 0)} work units` : `${num((state.search?.timeBudgetMs ?? 0) / 1000, 1)} s search`;
  const rank = runOpts.objective === 'historical' ? 'least leftover first' : 'cheapest first';
  return `${keep} · ${search} · ${plural(state.search?.maxCandidates ?? 0, 'plan')} · ${rank}`;
}

function renderForm() {
  const pol = state.minimumReusableRemnant;
  $('#form-view').innerHTML = `
    <label class="check"><input type="checkbox" data-path="materialDirectional" data-t="bool"${state.materialDirectional ? ' checked' : ''}>
      ${tipped('Directional fabric (pattern must run one way)', TIP.directional)}</label>
    ${tableHtml('pieces')}
    ${tableHtml('rollOffers')}
    ${tableHtml('stock')}
    <details class="more"${moreOpen ? ' open' : ''}>
      <summary>${tipped('More settings', TIP.more)} <span class="muted small" id="more-summary">${esc(moreSummary())}</span></summary>
      <h4>Reusable leftovers</h4>
      <label class="check"><input type="checkbox" id="policy-on"${pol ? ' checked' : ''}> ${tipped('Keep big enough leftovers as reusable remnants', TIP.policyOn)}</label>
      ${pol ? `<div class="fields">
        <label class="field">${tipped('Min short side mm', TIP.minWidth)}<input data-path="minimumReusableRemnant.minWidthMm" data-t="num" value="${esc(pol.minWidthMm)}" title="${esc(TIP.minWidth)}"></label>
        <label class="field">${tipped('Min long side mm', TIP.minLength)}<input data-path="minimumReusableRemnant.minLengthMm" data-t="num" value="${esc(pol.minLengthMm)}" title="${esc(TIP.minLength)}"></label>
      </div>` : ''}
      <h4>Search</h4>
      <div class="fields">
        <label class="field">${tipped('Time limit ms', TIP.timeBudget)}<input data-path="search.timeBudgetMs" data-t="int" value="${esc(state.search.timeBudgetMs)}" title="${esc(TIP.timeBudget)}"></label>
        <label class="field">${tipped('Plans to show', TIP.maxCandidates)}<input data-path="search.maxCandidates" data-t="int" value="${esc(state.search.maxCandidates)}" title="${esc(TIP.maxCandidates)}"></label>
        <label class="field">${tipped('Seed', TIP.seed)}<input data-path="search.seed" data-t="int" value="${esc(state.search.seed)}" title="${esc(TIP.seed)}"></label>
        <label class="field">${tipped('Ranking', TIP.objective)}<select id="objective" title="${esc(TIP.objective)}">
          <option value="cheapest"${runOpts.objective === 'cheapest' ? ' selected' : ''}>Cheapest first</option>
          <option value="historical"${runOpts.objective === 'historical' ? ' selected' : ''}>Least leftover first</option>
        </select></label>
        ${runOpts.objective === 'cheapest' ? `<label class="field">${tipped('Scrap tolerance m²', TIP.scrapTolerance)}<input id="scrap-tol" inputmode="decimal" value="${runOpts.scrapToleranceM2}" title="${esc(TIP.scrapTolerance)}"></label>` : ''}
      </div>
      <label class="check"><input type="checkbox" id="deterministic"${runOpts.deterministic ? ' checked' : ''}> ${tipped('Repeatable run (same job + seed → same plans)', TIP.deterministic)}</label>
      ${runOpts.deterministic ? `<label class="field" style="max-width:200px">${tipped('Work units', TIP.work)}<input id="work" inputmode="numeric" value="${runOpts.workBudget}" title="${esc(TIP.work)}"></label>` : ''}
      <h4>Material</h4>
      <div class="fields">
        <label class="field">${tipped('Material id', TIP.materialId)}<input data-path="materialId" data-t="str" value="${esc(state.materialId)}" title="${esc(TIP.materialId)}"></label>
        <label class="field">${tipped('Precision', TIP.precision)}<select data-path="precision.unitsPerMm" data-t="int" title="${esc(TIP.precision)}">
          ${[1, 10, 100, 1000].map((u) => `<option value="${u}"${state.precision?.unitsPerMm === u ? ' selected' : ''}>${u === 1 ? '1 mm' : `1/${u} mm`}</option>`).join('')}</select></label>
      </div>
    </details>`;
  paintIssues();
}

function setPath(obj, path, value) {
  const keys = path.split('.');
  let o = obj;
  for (let i = 0; i < keys.length - 1; i++) o = o[keys[i]];
  o[keys[keys.length - 1]] = value;
}

function refreshSummary() { const el = $('#more-summary'); if (el) el.textContent = moreSummary(); }

$('#form-view').addEventListener('input', (e) => {
  const el = e.target;
  if (el.id === 'work') { runOpts.workBudget = parseInt(el.value, 10) || 1; refreshSummary(); return; }
  if (el.id === 'scrap-tol') { const v = parseFloat(el.value); runOpts.scrapToleranceM2 = Number.isFinite(v) && v >= 0 ? v : 0; return; }
  const t = el.dataset.t;
  if (!t) return;
  if (t === 'bool') { setPath(state, el.dataset.path, el.checked); scheduleCheck(); return; }
  const parsed = parseValue(t, el.value);
  el.classList.toggle('bad', !parsed.ok);
  if (!parsed.ok) return;
  if (el.dataset.list) state[el.dataset.list][+el.dataset.i][el.dataset.k] = parsed.v;
  else setPath(state, el.dataset.path, parsed.v);
  refreshSummary();
  scheduleCheck();
});

$('#form-view').addEventListener('change', (e) => {
  const el = e.target;
  if (el.id === 'policy-on') {
    state.minimumReusableRemnant = el.checked ? { minWidthMm: 200, minLengthMm: 300, minAreaM2: null, maxAspectRatio: null } : null;
    renderForm();
    scheduleCheck();
  } else if (el.id === 'deterministic') {
    runOpts.deterministic = el.checked;
    renderForm();
  } else if (el.id === 'objective') {
    runOpts.objective = el.value;
    renderForm();
  } else if (el.dataset.t === 'bool') {
    setPath(state, el.dataset.path, el.checked);
    scheduleCheck();
  }
});

$('#form-view').addEventListener('toggle', (e) => { if (e.target.matches('details.more')) moreOpen = e.target.open; }, true);

$('#form-view').addEventListener('click', (e) => {
  const add = e.target.closest('[data-add]');
  const del = e.target.closest('[data-del]');
  if (add) { state[add.dataset.add].push(NEW_ROW[add.dataset.add]()); renderForm(); scheduleCheck(); }
  if (del) { state[del.dataset.del].splice(+del.dataset.i, 1); renderForm(); scheduleCheck(); }
});

// ---------- pre-check: the optimiser's own input rules, before running ----------------------------------------------
// X-Requested-With: a host's login (Azure App Service) then answers an expired sign-in with 401 instead of a redirect.
const JSON_HEADERS = { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' };
const SIGNED_OUT = 'Your sign-in has expired. Reload the page to sign in again.';
// An error answer as one line: the server's JSON message, else the start of its text or page (e.g. a hosting
// service's own 403 page), so the cause is visible on screen.
async function errorText(res) {
  const text = await res.text().catch(() => '');
  try {
    const data = JSON.parse(text);
    const msg = data?.detail || data?.error;
    if (msg) return data.correlationId ? `${msg} (reference ${data.correlationId})` : msg;
  } catch { /* not JSON */ }
  const plain = text.replace(/<(style|script)[\s\S]*?<\/\1>/gi, ' ').replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  return `Server returned ${res.status}${plain ? `: ${plain.slice(0, 200)}` : ''}`;
}
let checkTimer = null, checkSeq = 0;
function scheduleCheck() { clearTimeout(checkTimer); checkTimer = setTimeout(runCheck, 250); }
async function runCheck() {
  const seq = ++checkSeq;
  try {
    const res = await fetch('api/check', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(snapshotOf(state)) });
    const data = res.ok ? await res.json() : null;
    const message = res.ok ? '' : res.status === 401 ? SIGNED_OUT : await errorText(res);
    if (seq !== checkSeq) return;
    issues = res.ok ? { errors: data.errors || [], warnings: data.warnings || [] }
      : { errors: [{ code: 'JSON', path: '$', message, remediation: '' }], warnings: [] };
  } catch {
    return; // server unreachable: the run itself will report it
  }
  paintIssues();
}

const ISSUE_TITLE = {
  PIECE_FITS_NO_SOURCE: 'Won\'t fit any roll or remnant',
  PRECISION_EXCEEDED: 'Too precise',
  DIMENSION_LIMIT: 'Size out of range',
  NO_SOURCES: 'Nothing to cut from',
  STOCK_UNAVAILABLE: 'Stock not available',
};
const issueTitle = (e) => ISSUE_TITLE[e.code] || 'Needs fixing';

// Warehouse rows with a "leave out" reason are sent as excludedStock (with that reason), the rest as stock. The maps
// turn the server's $.stock[i] / $.excludedStock[i] paths back into form rows.
let stockRowOf = [];
let excludedRowOf = [];
function snapshotOf(s) {
  const offered = [];
  const excluded = [...(s.excludedStock || [])];
  stockRowOf = [];
  excludedRowOf = excluded.map(() => -1);
  (s.stock || []).forEach((r, i) => {
    const { excludeReason, ...item } = r;
    if (excludeReason && String(excludeReason).trim()) {
      excluded.push({ stockId: r.stockId, version: r.version, reason: String(excludeReason).trim() });
      excludedRowOf.push(i);
    } else {
      offered.push(item);
      stockRowOf.push(i);
    }
  });
  return { ...s, stock: offered, excludedStock: excluded };
}

const rowOf = (path) => {
  const m = /^\$\.(pieces|rollOffers|stock|excludedStock)\[(\d+)\](?:\.(\w+))?/.exec(path || '');
  if (!m) return null;
  if (m[1] === 'stock') return { list: 'stock', i: stockRowOf[+m[2]] ?? +m[2], k: m[3] };
  if (m[1] === 'excludedStock') return excludedRowOf[+m[2]] >= 0 ? { list: 'stock', i: excludedRowOf[+m[2]], k: 'excludeReason' } : null;
  return { list: m[1], i: +m[2], k: m[3] };
};
const ROW_NAME = { pieces: 'piece', rollOffers: 'supplier roll', stock: 'warehouse item' };
const rowLabel = (r) => {
  const row = state[r.list]?.[r.i];
  const id = row?.id ?? row?.offerId ?? row?.stockId;
  return `${ROW_NAME[r.list]} ${id ? `'${id}'` : `row ${r.i + 1}`}`;
};

function paintIssues() {
  document.querySelectorAll('#form-view .issue-row').forEach((tr) => tr.remove());
  document.querySelectorAll('#form-view tr.has-issue').forEach((tr) => tr.classList.remove('has-issue'));
  document.querySelectorAll('#form-view [data-issue]').forEach((el) => { el.classList.remove('bad'); el.removeAttribute('data-issue'); });
  const general = [];
  for (const e of issues.errors) {
    const r = rowOf(e.path);
    const tr = r && $(`#form-view tr[data-row="${r.list}-${r.i}"]`);
    if (!tr) { general.push(e); continue; }
    tr.classList.add('has-issue');
    if (r.k) {
      const cell = tr.querySelector(`[data-k="${r.k}"]`);
      if (cell) { cell.classList.add('bad'); cell.dataset.issue = '1'; }
    }
    const note = document.createElement('tr');
    note.className = 'issue-row';
    note.innerHTML = `<td colspan="${tr.children.length}"><div class="issue"><b>⚠ ${esc(issueTitle(e))}.</b> ${esc(e.message)}
      ${e.remediation ? `<div class="muted">Fix: ${esc(e.remediation)}</div>` : ''}</div></td>`;
    tr.after(note);
  }
  const box = $('#form-issues');
  box.innerHTML = general.length
    ? `<div class="issue"><b>⚠ ${plural(general.length, 'problem')} with the job.</b><ul>${general.map((e) =>
      `<li>${esc(e.message)}${e.remediation ? ` <span class="muted">Fix: ${esc(e.remediation)}</span>` : ''}</li>`).join('')}</ul></div>`
    : '';
  box.hidden = general.length === 0;

  const n = issues.errors.length;
  const hint = $('#run-hint');
  if (n === 0) {
    hint.className = 'muted small';
    hint.textContent = 'Runs the real optimiser and its independent checker on the server.';
  } else {
    const first = issues.errors[0];
    const r = rowOf(first.path);
    const what = r ? `${rowLabel(r)}: ${issueTitle(first).toLowerCase()}` : issueTitle(first).toLowerCase();
    hint.className = 'small warn-text';
    hint.textContent = `⚠ ${n === 1 ? what : `${n} problems (first: ${what})`}. You can still run it to see the full report.`;
  }
}

// ---------- presets & mode ---------------------------------------------------------------------------------------
$('#preset').innerHTML = PRESETS.map((p, i) => `<option value="${i}" title="${esc(p.note)}">${esc(p.name)}</option>`).join('');
$('#preset-label').innerHTML = tipped('Example job', TIP.preset);
function loadPreset(i) {
  state = PRESETS[i].make();
  $('#preset-note').innerHTML = `<b>${esc(PRESETS[i].name)}:</b> ${esc(PRESETS[i].note)}`;
  if (mode === 'json') $('#json-text').value = JSON.stringify(state, null, 2);
  renderForm();
  scheduleCheck();
}
$('#preset').addEventListener('change', (e) => loadPreset(+e.target.value));

document.querySelectorAll('.seg-btn').forEach((b) => b.addEventListener('click', () => setMode(b.dataset.mode)));
function setMode(m) {
  if (m === mode) return;
  if (m === 'form' && !applyJson()) return;
  mode = m;
  document.querySelectorAll('.seg-btn').forEach((b) => b.classList.toggle('active', b.dataset.mode === m));
  $('#form-view').hidden = m !== 'form';
  $('#json-view').hidden = m !== 'json';
  if (m === 'json') $('#json-text').value = JSON.stringify(state, null, 2);
  else renderForm();
}
function applyJson() {
  try {
    const parsed = JSON.parse($('#json-text').value);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) throw new Error('Expected a JSON object.');
    state = job(parsed);
    $('#json-error').textContent = '';
    scheduleCheck();
    return true;
  } catch (err) {
    $('#json-error').textContent = `Invalid JSON: ${err.message}`;
    return false;
  }
}
$('#json-apply').addEventListener('click', () => { if (applyJson()) $('#json-error').textContent = 'Applied.'; });

// ---------- run ------------------------------------------------------------------------------------------------
$('#run').addEventListener('click', run);
$('#cancel').addEventListener('click', () => controller?.abort());

async function run() {
  if (mode === 'json' && !applyJson()) return;
  hideTip();
  controller = new AbortController();
  const started = performance.now();
  const cap = Math.min(state.search?.timeBudgetMs ?? 0, 30000) / 1000;
  $('#run').disabled = true;
  $('#cancel').hidden = false;
  $('#results').innerHTML = `<div class="loading"><div class="spinner"></div><div><b>Searching for valid layouts…</b>
    <div class="muted small" id="elapsed">0.0 s of up to ${cap.toFixed(1)} s</div>
    <div class="muted small">Every plan is checked by an independent checker before it is shown. Cancel stops the search; nothing partial is kept.</div></div></div>`;
  announce('Optimising. This can take up to ' + cap.toFixed(0) + ' seconds.');
  const tick = setInterval(() => {
    const el = $('#elapsed');
    if (el) el.textContent = `${((performance.now() - started) / 1000).toFixed(1)} s of up to ${cap.toFixed(1)} s`;
  }, 100);
  try {
    const res = await fetch('api/optimize', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({ snapshot: snapshotOf(state), deterministic: runOpts.deterministic, workBudget: runOpts.workBudget, objective: runOpts.objective, scrapToleranceM2: runOpts.scrapToleranceM2 }),
      signal: controller.signal,
    }).catch((e) => {
      if (e.name === 'AbortError') throw e;
      throw new Error('Could not reach the playground. Check the connection, or reload the page (you may need to sign in again).');
    });
    if (res.status === 401) throw new Error(SIGNED_OUT);
    if (!res.ok) throw new Error(await errorText(res));
    response = await res.json();
    selected = 0;
    renderResults();
    announce(resultSummary(response.result));
  } catch (err) {
    $('#results').innerHTML = err.name === 'AbortError'
      ? '<div class="callout warn"><b>Cancelled.</b> The search stopped and nothing partial was kept.</div>'
      : `<div class="callout bad"><b>Request failed.</b> ${esc(err.message)}</div>`;
    announce(err.name === 'AbortError' ? 'Cancelled. Nothing partial was kept.' : `Request failed. ${err.message}`);
  } finally {
    clearInterval(tick);
    $('#run').disabled = false;
    $('#cancel').hidden = true;
    controller = null;
  }
}

// Status messages for screen readers (WCAG 4.1.3): announced without moving focus.
function announce(text) {
  const el = $('#run-status');
  if (!el) return;
  el.textContent = '';
  setTimeout(() => { el.textContent = text; }, 50);
}

function resultSummary(r) {
  const c = r.candidates?.[0];
  if (!c) return `${STATUS_TEXT[r.status] || r.status}.`;
  const order = c.purchase ? `order ${num(c.purchase.orderedLengthMetres, 3)} ${Number(c.purchase.orderedLengthMetres) === 1 ? 'metre' : 'metres'} of ${c.purchase.rollOfferId}, ${money(c.purchase.purchaseCost, c.purchase.currency)}` : 'no new fabric to order';
  return `${plural(r.candidates.length, 'plan')} found. Best plan: ${order}, ${c.metrics.totalCuts} cuts.`;
}

// ---------- results ---------------------------------------------------------------------------------------------
const STATUS_TONE = {
  Succeeded: 'good', InvalidInput: 'warn', NoFeasiblePlan: 'bad', NoPlanFound: 'bad',
  TimedOutWithoutPlan: 'bad', Cancelled: 'warn', Failed: 'bad',
};
const STATUS_TEXT = {
  Succeeded: 'Plans found', InvalidInput: 'Job needs fixing', NoFeasiblePlan: 'No plan is possible',
  NoPlanFound: 'No plan found (not proven impossible)', TimedOutWithoutPlan: 'Ran out of time without a plan',
  Cancelled: 'Cancelled', Failed: 'Run failed',
};
// Short, plain names for the optimiser's selection tags; the full reason is in the tooltip.
const TAGS = {
  'Best by objective': ['Best overall', 'The top plan under the chosen ranking.'],
  'No purchase (warehouse only)': ['Nothing to order', 'Cuts everything from warehouse stock.'],
  'Least warehouse stock committed at the best purchase': ['Saves warehouse stock', 'Orders the same as the best plan but cuts into less of your warehouse stock.'],
  'Fewest cuts': ['Fewest cuts', 'Needs the fewest cuts of all plans found.'],
  'Lowest purchase cost': ['Cheapest order', 'The cheapest order of all plans found.'],
  'Largest reusable remnant': ['Biggest leftover', 'Leaves the biggest reusable leftover.'],
  'Least unusable scrap': ['Least scrap', 'Leaves the least unusable scrap.'],
  'Next best by objective': ['Runner-up', 'The next best plan under the chosen ranking.'],
};
function tagsHtml(tags) {
  const shown = tags.slice(0, 2).map(tagChip).join('');
  if (tags.length <= 2) return shown;
  const rest = tags.slice(2).map((t) => (TAGS[t] || [t])[0]).join(', ');
  return `${shown}<span class="chip more-chip" tabindex="0" data-tip="Also: ${esc(rest)}">+${tags.length - 2}</span>`;
}
function tagChip(t) {
  const m = /^Best using roll offer (.+)$/.exec(t);
  const [label, tip] = TAGS[t] || (m ? [`Best with ${m[1]}`, `The best plan that orders from ${m[1]}.`] : [t, t]);
  return `<span class="chip" tabindex="0" data-tip="${esc(tip)}">${esc(label)}</span>`;
}

const offerOf = (id) => response?.effectiveSnapshot?.rollOffers?.find((o) => o.offerId === id);
const stockUsed = (c) => c.sources.filter((s) => s.kind === 'WarehouseRemnant');

// Honest quality labels (handbook §32, §45): never "optimal"; "proven" only with an exhaustive-search proof.
function qualityBadge(c) {
  const cost = c.costBound, area = c.bound;
  if (cost && Number(cost.gapM2) === 0) {
    return `<span class="pill good" tabindex="0" data-tip="${esc(cost.note)} Scrap, leftovers and cuts are the best found, not proven.">${cost.proven ? 'Price proven lowest' : 'Price at the lower bound'}</span>`;
  }
  if (area && area.proven && Number(area.gapM2) === 0) {
    return `<span class="pill good" tabindex="0" data-tip="${esc(area.note)}">Fabric ordered proven least</span>`;
  }
  const gap = cost && cost.gapPercentOfBound != null ? ` The price is at most ${num(cost.gapPercentOfBound, 1)} % above a lower bound (${esc(cost.method)}).` : '';
  return `<span class="pill neutral" tabindex="0" data-tip="Best verified plan found within the search budget. It passed the independent check; it is not proven to be the best possible.${gap}">Best verified plan found</span>`;
}

function orderCell(c) {
  const p = c.purchase;
  if (!p) return '<b class="nowrap">Nothing</b>';
  return `<span class="nowrap"><b>${num(p.orderedLengthMetres, 3)} m</b> ${esc(p.rollOfferId)}</span><br><span class="muted small nowrap">needs ${mm(p.consumedLengthMm)}</span>`;
}

function orderHtml(c) {
  const p = c.purchase;
  if (!p) {
    return `<div class="box order none"><div class="box-k">Order from supplier</div><div class="box-v">No new fabric to order</div>
      <div class="box-sub">The selected warehouse pieces cover this job.</div></div>`;
  }
  const padding = Number(p.paddingLengthMm);
  const needed = padding > 0
    ? `${mm(p.lengthWithoutPaddingMm)} + ${mm(p.paddingLengthMm)} padding = <b>${mm(p.consumedLengthMm)}</b>`
    : `<b>${mm(p.consumedLengthMm)}</b>`;
  const surplus = Number(p.roundingSurplusMm);
  const offer = offerOf(p.rollOfferId);
  const rollLen = offer?.maxOrderMetres;
  const lines = p.fixedLength
    ? [`The layout needs ${needed}.`, 'Fixed-length test stock: the whole length is treated as bought.']
    : [
      `The layout needs ${needed} → rounded <b>up</b> to whole metres.`,
      surplus <= 0 ? 'Nothing is left over.'
        : c.predictedResiduals.some((r) => r.roundingSurplus && !r.eligible) ? `The ${mm(surplus)} left over is scrap: too small to keep.`
        : `The ${mm(surplus)} left over goes to the warehouse.`,
      rollLen ? `<span class="muted">The supplier's roll is ${num(rollLen, 0)} m. Only the ${num(p.orderedLengthMetres, 3)} m ordered is shown; the rest stays with the supplier.</span>` : '',
    ];
  return `<div class="box order">
    <div class="box-k">Order from supplier</div>
    <div class="box-v">${num(p.orderedLengthMetres, 3)} m</div>
    <div class="box-sub">${num(p.orderedLengthMetres, 3)} m × ${num(p.orderedWidthMm, 0)} mm high from ${esc(p.rollOfferId)} · ${m2(p.purchasedAreaM2)} · ${money(p.purchaseCost, p.currency)}</div>
    <div class="box-calc">${lines.filter(Boolean).map((l) => `<div>${l}</div>`).join('')}</div>
  </div>`;
}

function stockHtml(c) {
  const used = stockUsed(c);
  if (!used.length) {
    return `<div class="box stock"><div class="box-k">From the warehouse</div><div class="box-v small-v">Nothing</div>
      <div class="box-sub">No remnants or rolls you own are cut.</div></div>`;
  }
  const kept = (id) => c.predictedResiduals.filter((r) => r.sourceId === id && r.eligible).reduce((a, r) => a + Number(r.areaM2), 0);
  return `<div class="box stock"><div class="box-k">From the warehouse</div>
    <ul class="stock-list">${used.map((s) => `<li><b>${esc(s.stockId)}</b> <span class="muted">v${s.stockVersion}</span>
      · ${num(s.lengthMm, 1)} long × ${num(s.widthMm, 1)} mm high${s.binReference ? ` · bin ${esc(s.binReference)}` : ''}
      ${kept(s.sourceId) > 0 ? `<div class="muted small">${m2(kept(s.sourceId))} of it stays reusable</div>` : ''}</li>`).join('')}</ul></div>`;
}

function renderResults() {
  const r = response.result;
  const d = r.diagnostics || {};
  const tone = STATUS_TONE[r.status] || 'neutral';
  let html = `<div class="banner">
      <span class="pill ${tone}">${esc(STATUS_TEXT[r.status] || r.status)}</span>
      <span class="muted small">in ${num(r.elapsedMs / 1000, 1)} s</span>
    </div>`;

  if (r.inputErrors?.length) {
    html += `<div class="callout warn"><b>The job was not run. Fix ${r.inputErrors.length === 1 ? 'this' : 'these'} first:</b><ul>${r.inputErrors.map((e) => {
      const row = rowOf(e.path);
      return `<li><b>${esc(issueTitle(e))}${row ? ` — ${esc(rowLabel(row))}` : ''}.</b> ${esc(e.message)}<br><span class="muted">Fix: ${esc(e.remediation)}</span></li>`;
    }).join('')}</ul></div>`;
  }
  if (d.infeasibilityReasons?.length) {
    html += `<div class="callout bad"><b>Why there is no plan</b><ul>${d.infeasibilityReasons.map((s) => `<li>${esc(s)}</li>`).join('')}</ul></div>`;
  }
  if (d.failureMessage) html += `<div class="callout bad"><b>Failure:</b> ${esc(d.failureMessage)}</div>`;
  const warnings = (d.warnings || []).filter((w) => !w.includes('pending product-owner approval'));
  if (warnings.length) html += `<div class="callout warn"><b>Warnings</b><ul>${warnings.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></div>`;

  if (r.candidates?.length) {
    const th = (label, tip, cls = '') => `<th${cls ? ` class="${cls}"` : ''}>${tipped(label, tip)}</th>`;
    html += `<h3>${r.candidates.length > 1 ? 'Choose a plan' : 'Plan'}</h3>
      <div class="table-wrap"><table class="compare">
      <thead><tr>${th('Plan', 'Plans are ranked best first. Click a row to see its cutting sheets.')}
        ${th('Why shown', 'Why this plan is in the list: the best overall, or the best at something specific.')}
        ${th('Order', 'Metres to order from the supplier (rounded up to whole metres) and the length the layout actually needs.')}
        ${th('Warehouse stock', 'Remnants or whole rolls you own that this plan cuts from.')}
        ${th('Scrap', 'Leftover area too small to keep as a reusable remnant.', 'num')}
        ${th('Cuts', 'Number of straight edge-to-edge cuts.', 'num')}
        ${th('Cost', 'Price of the fabric ordered from the supplier.', 'num')}</tr></thead>
      <tbody>${r.candidates.map((c, i) => {
        const used = stockUsed(c);
        return `<tr data-cand="${i}" class="${i === selected ? 'selected' : ''}" tabindex="0">
        <td><b>#${c.rank}</b></td>
        <td>${tagsHtml(c.tags || [])}</td>
        <td>${orderCell(c)}</td>
        <td>${used.length ? used.map((s) => esc(s.stockId)).join(', ') : '<span class="muted">–</span>'}</td>
        <td class="num">${m2(c.metrics.unusableScrapAreaM2)}</td>
        <td class="num">${c.metrics.totalCuts}</td>
        <td class="num">${money(c.metrics.purchaseCost, c.metrics.currency)}</td>
      </tr>`;
      }).join('')}</tbody></table></div>`;
    html += candidateHtml(r.candidates[selected]);
  }

  html += technicalHtml(r, d);
  $('#results').innerHTML = html;
  $('#download')?.addEventListener('click', downloadJson);
  $('#results').querySelectorAll('tr[data-cand]').forEach((tr) => {
    const pick = () => { selected = +tr.dataset.cand; renderResults(); };
    tr.addEventListener('click', pick);
    tr.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(); } });
  });
  $('#results').querySelectorAll('[data-open-print]').forEach((b) => b.addEventListener('click', (e) => { e.stopPropagation(); openPrint(b.dataset.openPrint); }));
  $('#results').querySelector('[data-open-doc]')?.addEventListener('click', openDocument);
  $('#results').querySelectorAll('.print').forEach((f) => f.addEventListener('click', () => f.classList.toggle('zoomed')));
}

function technicalHtml(r, d) {
  const ex = d.exact;
  const exactText = !ex ? '–' : !ex.attempted ? 'not attempted' : ex.completed ? (ex.provenInfeasible ? 'proved no plan exists' : 'proved the minimum order') : 'stopped early';
  const row = (k, v) => `<tr><th>${k}</th><td>${v}</td></tr>`;
  return `<details class="tech"><summary>Technical details</summary>
    <table class="data kv"><tbody>
      ${row('Stopped because', esc(r.stopReason))}
      ${row('Mode · seed', `${esc(r.mode)} · ${esc(r.seed)}`)}
      ${row('Ranking', `${esc(r.objectiveVersion)}${r.objectiveApproved ? '' : ' (pending product-owner approval)'}`)}
      ${row('Versions', `solver ${esc(r.solverVersion)} · checker ${esc(r.validatorVersion)}`)}
      ${r.status !== 'InvalidInput' ? `
      ${row('Search', `${num(d.constructionsAttempted, 0)} constructions · ${num(d.beamRuns, 0)} beam runs · ${num(d.improvementIterations, 0)} improvement steps`)}
      ${row('Checked', `${num(d.plansValidated, 0)} plans passed the independent checker · ${num(d.plansRejectedByValidator, 0)} rejected · ${num((d.duplicateLayoutsSkipped || 0) + (d.uncompetitiveLayoutsSkipped || 0), 0)} layouts skipped as duplicates or not competitive`)}
      ${(d.rejectedCandidates || []).length ? row('Rejected by the checker', d.rejectedCandidates.map((x) => esc(x.failures.map((f) => f.code).join(', '))).join('<br>')) : ''}
      ${d.purchaseCostLowerBound != null ? row('Price lower bound', `${money(d.purchaseCostLowerBound, r.candidates?.[0]?.metrics.currency)} (${esc(d.purchaseCostLowerBoundMethod)})`) : ''}
      ${row('Exact solver', `<span title="${esc(ex?.reason)}">${esc(exactText)}</span>`)}
      ${d.purchasedAreaLowerBoundM2 != null ? row('Order lower bound', `${m2(d.purchasedAreaLowerBoundM2)} (${esc(d.purchasedAreaLowerBoundMethod)})`) : ''}` : ''}
    </tbody></table>
    <button type="button" class="btn small" id="download">Download result JSON</button>
  </details>`;
}

function candidateHtml(c) {
  const m = c.metrics;
  const prints = response.printouts?.[c.candidateId] || [];
  const metric = (k, v, tip) => `<div class="metric"><div class="k">${tipped(k, tip)}</div><div class="v">${v}</div></div>`;
  // Cut-out size in the finished-panel frame, read off the footprint actually cut (works for any result version).
  const cut = (p) => (p.pieceRotated ? [p.footprint.length, p.footprint.width] : [p.footprint.width, p.footprint.length]);
  const padded = (p) => [p.paddingMm.top, p.paddingMm.right, p.paddingMm.bottom, p.paddingMm.left].some((v) => Number(v) > 0);
  return `<div class="card">
    <div class="card-head">
      <h2>Plan #${c.rank}</h2>
      <div class="badges"><button type="button" class="btn small" data-open-doc>Printable cut plan</button>${qualityBadge(c)}
        <span class="pill ${c.validation?.passed ? 'good' : 'bad'}" tabindex="0" data-tip="An independent checker re-measured every piece, cut and leftover of this plan.">${c.validation?.passed ? '✓ Checked' : '✗ Failed check'}</span></div>
    </div>
    <div class="summary">${orderHtml(c)}${stockHtml(c)}</div>
    ${planWarnings(c).map((w) => `<p class="muted small" style="margin:4px 0">⚠ ${esc(w)}</p>`).join('')}
    ${sheetsHtml(c, prints)}
    <details><summary>Pieces (${c.placements.length})</summary><div class="table-wrap"><table class="data">
      <thead><tr><th>Piece</th><th>Cut from</th><th>${tipped('Cut-out W × H', 'Full size to cut, including padding: width × height of the finished panel.')}</th><th>Finished W × H</th>
        <th>${tipped('Padding', 'Top / Right / Bottom / Left, in mm.')}</th><th>${tipped('Turned', 'The panel lies on its side on the cutting sheet: its height runs left to right.')}</th><th>Grain</th></tr></thead>
      <tbody>${c.placements.map((p) => {
        const [cw, cl] = cut(p);
        return `<tr data-piece="${esc(p.pieceInstanceId)}" tabindex="0"><td>${esc(p.pieceInstanceId)}</td><td>${esc(p.sourceId.replace(/^new-roll:/, 'order: '))}</td>
        <td><b>${num(cw, 1)} × ${num(cl, 1)}</b></td>
        <td>${num(p.nominalWidthMm, 1)} × ${num(p.nominalLengthMm, 1)}</td>
        <td>${padded(p) ? [p.paddingMm.top, p.paddingMm.right, p.paddingMm.bottom, p.paddingMm.left].map((v) => num(v, 1)).join(' / ') : '<span class="muted">none</span>'}</td>
        <td>${turnedOnSheet(p) ? '90°' : '–'}</td><td>${esc(p.finishedPatternDirection === 'None' ? '–' : p.finishedPatternDirection)}</td></tr>`;
      }).join('')}</tbody></table></div></details>
    <details><summary>Cut sequence (${c.cutSteps.length})</summary><div class="table-wrap"><table class="data">
      <thead><tr><th>#</th><th>On</th><th>${tipped('Cut', 'Direction and position as drawn on the cutting sheet, in mm from its left or top edge.')}</th><th>${tipped('Length', 'How long the cut is, in mm.')}</th></tr></thead>
      <tbody>${c.cutSteps.map((s) => cutRow(c, s)).join('')}</tbody></table></div></details>
    <details><summary>Leftovers (${c.predictedResiduals.length})</summary><div class="table-wrap"><table class="data">
      <thead><tr><th>On</th><th>${tipped('Size', 'As drawn on the cutting sheet: left to right × top to bottom, in mm.')}</th><th>Area</th><th>Kept?</th><th>Why</th></tr></thead>
      <tbody>${c.predictedResiduals.map((r) => {
        const s = c.sources.find((q) => q.sourceId === r.sourceId);
        const turned = [90, 270].includes(s?.displayRotationDegrees);
        const [a, d] = turned ? [r.region.length, r.region.width] : [r.region.width, r.region.length];
        return `<tr><td>${esc(sheetName(r.sourceId))}</td>
        <td>${num(a, 1)} × ${num(d, 1)}</td><td>${m2(r.areaM2)}</td>
        <td>${r.eligible ? 'reusable' : 'scrap'}${r.roundingSurplus ? ' (rounding surplus)' : ''}</td><td class="muted">${esc(r.reason)}</td></tr>`;
      }).join('')}</tbody></table></div>
      <p class="muted small">Leftovers are predictions: they become warehouse stock once they are cut and confirmed.</p></details>
    <details><summary>All numbers</summary>
      <div class="metrics">
        ${metric('Fabric needed (with padding)', m2(m.requiredCutAreaM2), 'Total area of all cut-outs, including padding.')}
        ${metric('Newly ordered', m2(m.purchasedAreaM2), 'Area ordered from the supplier (full width × ordered metres).')}
        ${metric('…of which the layout uses', m2(m.newConsumedAreaM2), 'Full width × the running length the layout needs.')}
        ${metric('…left over from rounding up', m2(m.roundingSurplusAreaM2), 'Full width × the part of the last metre the layout does not need.')}
        ${metric('Warehouse stock used', m2(m.existingStockAreaConsumedM2), 'Area of the warehouse pieces this plan cuts into.')}
        ${metric('Warehouse stock untouched', m2(m.existingStockUntouchedAreaM2), 'Warehouse pieces offered but not cut. They stay in the warehouse; this is not waste.')}
        ${metric('Scrap', m2(m.unusableScrapAreaM2), 'Leftover area too small to keep.')}
        ${metric('Reusable leftovers', `${m2(m.reusableResidualAreaM2)} <span class="muted small">(${m.reusableOffcutCount})</span>`, 'Leftover area big enough to keep, and how many pieces.')}
        ${metric('Largest reusable leftover', m2(m.largestReusableAreaM2), 'Area of the biggest leftover that is kept.')}
        ${metric('Cuts', m.totalCuts, 'Straight edge-to-edge cuts.')}
        ${metric('Fabric used', pct(m.materialUtilization), 'Share of the fabric cut into that ends up in pieces.')}
        ${metric('Thin strips', m.thinFragmentCount, 'Narrow leftover strips that are awkward to handle.')}
        ${metric('Order cost', money(m.purchaseCost, m.currency), 'Price of the fabric ordered.')}
      </div>
      ${c.costBound ? `<p class="muted small">Price: ${esc(c.costBound.note)}</p>` : ''}
      ${c.bound ? `<p class="muted small">Fabric ordered: ${esc(c.bound.note)}</p>` : ''}
      <p class="muted small">Plan id ${esc(c.candidateId)}</p>
    </details>
  </div>`;
}

// The "leftovers are predictions" note is shown once under Leftovers instead of on every plan.
const planWarnings = (c) => (c.warnings || []).filter((w) => !/not inventory until physically confirmed/i.test(w));

// Sheet coordinates as drawn: mm from the top-left corner, running length left to right (see the printout).
function sheetPoint(c, sourceId, x, y) {
  const s = c.sources.find((q) => q.sourceId === sourceId);
  const W = Number(s?.widthMm ?? 0), L = Number(s?.lengthMm ?? 0);
  switch (s?.displayRotationDegrees) {
    case 270: return [Number(y), W - Number(x)];
    case 90: return [L - Number(y), Number(x)];
    default: return [Number(x), Number(y)];
  }
}
const turnedOnSheet = (p) => ([90, 270].includes(p.sourceDisplayRotationDegrees) ? !p.pieceRotated : p.pieceRotated);
const sheetName = (id) => id.replace(/^new-roll:/, 'order: ');

function cutRow(c, s) {
  const [fx, fy] = sheetPoint(c, s.sourceId, s.fromXMm, s.fromYMm);
  const [tx, ty] = sheetPoint(c, s.sourceId, s.toXMm, s.toYMm);
  const vertical = fx === tx;
  const what = vertical ? `Top to bottom at ${num(fx, 1)} from the left` : `Left to right at ${num(fy, 1)} from the top`;
  return `<tr data-cut="${s.sequence}" tabindex="0"><td>${s.sequence}</td><td>${esc(sheetName(s.sourceId))}</td><td>${what}</td>
    <td>${num(vertical ? Math.abs(ty - fy) : Math.abs(tx - fx), 1)}</td></tr>`;
}

// The plan's drawings in workshop order: 1 the whole supplier roll, 2 the ordered fabric's cutting sheet (zoomed in),
// 3 every warehouse remnant used.
function sheetsHtml(c, prints) {
  const fig = (key, svg, title) => `<figure class="print" title="${esc(title)}">
    <button type="button" class="btn small open" data-open-print="${esc(key)}">Open full size</button>${svg}</figure>`;
  const head = (n, title, sub) => `<h3 class="sheets-h"><span class="step">${n}</span>${title} <span class="muted small">${sub}</span></h3>`;
  const note = (text) => `<p class="muted small sheet-note">${text}</p>`;
  const roll = prints.filter((p) => p.sourceId.startsWith('new-roll:'));
  const remnants = prints.filter((p) => !p.sourceId.startsWith('new-roll:'));
  const overview = response.overviews?.[c.candidateId];
  const offerId = c.purchase?.rollOfferId;

  const one = !c.purchase ? note('Nothing is ordered from a supplier for this plan.')
    : overview ? `<div class="prints">${fig('overview', overview, `${offerId} full roll`)}</div>`
      : note(`Enter a roll length for ${esc(offerId)} under Supplier rolls to see the whole roll.`);
  const two = roll.length ? `<div class="prints">${roll.map((p) => fig(`sheet:${p.sourceId}`, p.svg, p.sourceId)).join('')}</div>`
    : note('Nothing is ordered, so there is no cutting sheet for new fabric.');
  const three = remnants.length ? `<div class="prints">${remnants.map((p) => fig(`sheet:${p.sourceId}`, p.svg, p.sourceId)).join('')}</div>`
    : note('No warehouse remnants are used.');

  return `<section class="sheets">
      ${head(1, 'Supplier roll: full length', 'the whole roll, what you order and where the supplier cuts')}${one}
    </section>
    <section class="sheets">
      ${head(2, 'Ordered fabric: cutting sheet', 'zoomed in · cut in numbered order · click a sheet to zoom')}${two}
    </section>
    <section class="sheets">
      ${head(3, `Warehouse remnants used${remnants.length ? ` (${remnants.length})` : ''}`, 'one sheet per remnant · cut in numbered order')}${three}
    </section>`;
}

function openPrint(key) {
  const c = response.result.candidates[selected];
  const svg = key === 'overview'
    ? response.overviews?.[c.candidateId]
    : (response.printouts?.[c.candidateId] || []).find((p) => `sheet:${p.sourceId}` === key)?.svg;
  if (!svg) return;
  const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
  window.open(url, '_blank', 'noopener');
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

// The printable cut plan: the server builds it from the checked plan (tables per sheet plus the drawings).
function openDocument() {
  const c = response.result.candidates[selected];
  const html = response.documents?.[c.candidateId];
  if (!html) return;
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
  window.open(url, '_blank', 'noopener');
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

// Pointing at or focusing a piece or a cut in the tables highlights it on the drawings (and the other way round).
function wireHighlights() {
  const root = $('#results');
  const mark = (attr, value, on) => root.querySelectorAll(`[${attr}="${CSS.escape(value)}"]`).forEach((el) => el.classList.toggle('hl', on));
  const handler = (on) => (e) => {
    const t = e.target.closest?.('[data-piece],[data-cut]');
    if (!t || !root.contains(t)) return;
    if (t.dataset.piece) mark('data-piece', t.dataset.piece, on);
    if (t.dataset.cut) mark('data-cut', t.dataset.cut, on);
  };
  root.addEventListener('mouseover', handler(true));
  root.addEventListener('mouseout', handler(false));
  root.addEventListener('focusin', handler(true));
  root.addEventListener('focusout', handler(false));
}

function downloadJson() {
  const blob = new Blob([JSON.stringify(response.result, null, 2)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `dreamglass-result-${response.result.runId || 'run'}.json`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

// ---------- boot -------------------------------------------------------------------------------------------------
wireHighlights();
loadPreset(0);
fetch('api/info').then((r) => r.json()).then((info) => {
  $('#versions').textContent = `${info.buildVersion ? `DreamGlass ${info.buildVersion} · ` : ''}solver ${info.solverVersion} · checker ${info.validatorVersion}`;
}).catch(() => { $('#versions').textContent = 'Solver info unavailable'; });
