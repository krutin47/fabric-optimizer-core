# Changelog

## 1.3.3 — 2026-11-12 (playground only; solver unchanged at `dreamglass-hybrid/1.3.0`, validator at `dreamglass-validator/1.2.0`)

For a hosted playground shared with a few testers, without Microsoft sign-in.

- Password: on a hosting service the playground asks for a shared password (HTTP Basic; the browser asks once and
  sends it with every request). Set the app setting `DREAMGLASS_PASSWORD` (at least 12 characters; any user name
  works). A wrong password is answered after a one-second wait. Without a password a hosted playground does not run
  open: every request gets a message saying how to set one (`DREAMGLASS_ALLOW_PUBLIC=true` opts out, for a login of
  your own in front). On your own computer nothing changes unless you set a password. The console shows the status.
- Fix: `Referrer-Policy` is now `same-origin` instead of `no-referrer`, so the page's own requests keep their Referer
  and Origin headers; a login layer in front (Azure App Service authentication) could refuse POSTs without them (403
  on Optimize and the form check while the page itself loaded).
- Page: a failed request shows the start of the server's answer (for example a hosting service's own 403 page)
  instead of only the status code.

## 1.3.2 — 2026-11-12 (playground only; solver unchanged at `dreamglass-hybrid/1.3.0`, validator at `dreamglass-validator/1.2.0`)

For hosting the playground for testers (Azure App Service or any host that assigns a port).

- Start-up: on a hosting service the playground listens on the port the host assigns (`PORT`, `WEBSITES_PORT`, or
  8080 on Azure App Service, detected by `WEBSITE_SITE_NAME`) and respects `ASPNETCORE_HTTP_PORTS`; it opens no
  browser and does not wait for Enter after a start-up error. On your own computer nothing changes.
- Request bodies are limited to 1 MB; a larger one gets "The job is too large…" (413) instead of being read.
- Browser security headers on every response: a Content-Security-Policy (only the page's own script, styles and
  data; no framing), `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, and HSTS
  when hosted. The `Server` header is no longer sent.
- Page: when a host's login has expired, a run says "Your sign-in has expired. Reload the page to sign in again."
  instead of failing silently; an unreachable server says so.

## 1.3.1 — 2026-11-12 (printouts and playground only; solver unchanged at `dreamglass-hybrid/1.3.0`, validator at `dreamglass-validator/1.2.0`)

- Build number: `HybridCutOptimizer.BuildVersion` (the package version, now 1.3.1) goes up with every release, so you can
  tell which build is running. The solver version changes only when the search changes. It shows in the playground's
  console window ("DreamGlass playground 1.3.1 (solver …)"), under the page title, in `/api/info` (`buildVersion`)
  and in the CLI summary.
- Printouts: the footer of every cutting sheet and full-roll view reads "Plan … · DreamGlass 1.3.1 · solver … ·
  checker …". `CutPlanSvgRenderer.Render` and `RenderSupplierRollOverview` take the run's `solverVersion` (optional).
- 1 new test (151 total).

## 1.3.0 — 2026-11-12 (solver `dreamglass-hybrid/1.3.0`; validator unchanged at `dreamglass-validator/1.2.0`)

From the workshop sketch: three 510 mm cut-outs side by side over a 1,440 × 540 cut-out on a 1,400 mm roll.

- Search: row sections. A new section of a roll used to be exactly as long as the piece that opened it, so pieces
  stacked across the roll beside it had to fit within that length. Construction can now make a section as long as a row
  of pending pieces side by side (`Construction.ChooseSection`: candidate lengths from rows of 1..n copies of each
  pending piece type, each packed on a scratch section, best fill wins). Used by half the roll starts and random restarts
  (`PlacementPolicy.Rows`). The sketch job now uses 3,530 mm instead of 3,950 mm and leaves one 470 × 1,400 piece
  instead of 510 × 590 + 420 × 860, with 8 cuts instead of 9–10.
- Ranking: new default `ObjectivePolicy.CheapestFirst` (`cheapest-first`), approved by the product owner: price →
  least unusable scrap within `ScrapTieToleranceM2` (0.01 m²) → biggest keepable leftover → least roll length used
  (new `ObjectiveCriterion.RollLengthUsed`) → fewest cuts → purchased area → warehouse stock committed. Results no
  longer carry the "pending approval" warning. `ProposedV1` and `HistoricalWasteFirst` remain available.
- Playground: the ranking dropdown is "Cheapest first" (default) and "Least leftover first", with a "Scrap tolerance m²"
  field; the request takes `scrapToleranceM2`. CLI: `--objective cheapest|historical|proposed`, `--scrap-tolerance`.
  New example job "Row of panels (workshop sketch)"; the "Two roll heights" note now explains the choice by price.
- Printouts and playground: "The N mm left over goes to the warehouse" only when that piece is big enough to keep;
  otherwise "is scrap: too small to keep" with the reason. Leftover labels that do not fit on one line go on two lines
  or read upwards, so a narrow end-of-order piece shows its size.
- 7 new tests (150 total): the sketch layout, the cheapest-first order and scrap tolerance, the approved default, and
  the leftover wording.

## 1.2.1 — 2026-11-11 (playground and packaging only; solver and validator unchanged at 1.2.0)

- Download-and-run packages: `scripts/package-apps.sh` builds one zip per system (Windows x64, macOS Apple silicon,
  macOS Intel, Linux x64), each with a self-contained single-file program (no .NET install needed), the page files, a
  start script where the system needs one, and a plain-language README.txt including the one-time SmartScreen /
  Gatekeeper step. macOS programs are ad-hoc signed by the SDK so Apple silicon runs them.
- Playground start-up: finds its page files next to the program whatever folder it is started from, listens on this
  computer only (localhost) by default instead of every network interface, takes the first free port from 5080 so a
  second copy does not fail, opens the default browser (`--no-browser` to skip), prints the address with "keep this
  window open", and on a start-up error keeps the window open so the message can be read.
- The playground program is now named `DreamGlass.Playground` (project unchanged). Packaged builds use invariant
  globalization, so numbers print the same on every computer's language settings.

## 1.2.0 — 2026-11-06 (solver `dreamglass-hybrid/1.2.0`, validator `dreamglass-validator/1.2.0`)

Workshop feedback on the playground and printouts.

- New full-roll view (`CutPlanSvgRenderer.RenderSupplierRollOverview`): the supplier's whole roll to scale, left to
  right, with the ordered part (pieces, leftovers, cuts), a dashed line where the supplier cuts, the rest of the roll
  greyed out as staying with the supplier (never as a remnant), a metre ruler, and "ordered" vs "supplier roll" spans.
  Shown when the offer has a roll length. The CLI writes it as `rank{n}-supplier-roll.svg`.
- Each plan's drawings come in workshop order: 1 the supplier roll at full length, 2 the ordered fabric's cutting sheet
  (zoomed in), 3 every warehouse remnant used.
- Sheets are drawn the way the workshop reads a roll: running length left to right from the start of the roll, the
  roll's fixed height top to bottom (`displayRotationDegrees = 270` for every source; the validator also accepts 270).
  Order dimension lines run along the bottom. Leftover sizes, "turned 90°" labels and the playground's cut list follow
  the drawing.
- Playground: a roll's fixed dimension is labelled "Height", and rolls and warehouse stock default to a pattern running
  across the roll, so pattern-locked panels stand upright across the roll's height. With that, the spec example cuts both
  600 × 900 panels one above the other from 600 mm of running length (1 m ordered), as in the workshop's reference app.
  Example jobs were reworked to fit (curtains up to 2,600 mm high on a 2,800 mm roll, padding on the left and right for
  the 17 m order, a remnant with the pattern along its length).

- Playground: every input, column, setting and example job has a plain-language explanation (hover or tap). Advanced
  settings are folded under "More settings".
- Playground: the form checks the job while you edit (new `POST /api/check`, which runs only the optimiser's input
  validator). A piece that fits no roll or remnant is flagged on its own row with the reason and a fix; you can still run.
- Playground: supplier rolls take a "Roll length" (`maxOrderMetres`). Only the ordered metres are bought and drawn. The
  confusing "Fixed mm" test field is gone from the form, and a whole roll you own goes under warehouse stock, where its
  remainder is a reusable remnant. New examples: "Order from a supplier roll (15 of 31 m)" and "Whole roll in the
  warehouse (31 m)".
- Playground results are decluttered. Each plan opens with "Order from supplier" and "From the warehouse" boxes, then one
  full-width cutting sheet per source (fit to screen, click to zoom, open full size). Plan tags are short, and metrics,
  diagnostics and versions move into collapsible sections.
- Printouts: the header states what to do ("ORDER FROM SUPPLIER: 15 m of roll-2800 (2,800 mm wide)" or "FROM WAREHOUSE:
  R1 v4 · bin A-11"), with technical details reduced to a one-line footer. Unpadded pieces show just their size, padded
  pieces show the cut-out plus the finished size and padding.
- Fix: printout cut-out sizes are read from the cut geometry itself, so a result without `cutWidthMm`/`cutLengthMm`
  (from an older build or edited by hand) can no longer show "Cut-out 0 × 0 mm".
- The "fits no source" input error is written in plain language ("needs a 2810 × 1010 mm cut-out … as drawn it needs
  2810 mm of width").
- Fix: the playground no longer breaks after an update when the browser still holds the old page. Static files are
  served with `Cache-Control: no-cache` and versioned URLs, the script copes with an older page, and a startup failure
  now shows a "reload without the cache" message instead of hanging on "Loading solver info…".
- 5 new tests (143 total).

## 1.1.0 — 2026-11-06 (solver `dreamglass-hybrid/1.1.0`, validator `dreamglass-validator/1.1.0`)

Workshop feedback on printouts and ordering.

- Purchase line adds `lengthWithoutPaddingMm` and `paddingLengthMm` (consumed = without + padding), derived from the
  cut tree of the actual arrangement and independently re-checked by the validator (`PURCHASE_INVALID`).
- Placements add `cutWidthMm` / `cutLengthMm`: the full cut-out size including padding, in the finished-panel frame
  (validator: `CUTOUT_SIZE_MISMATCH`).
- New-roll printouts carry an order header ("ORDER 17 m × 2,800 mm — layout needs 16,680 mm (16,500 + 180 padding) →
  rounded up to whole metres") and "needed"/"ordered" dimension lines.
- Piece labels show cut-out size, finished size and padding; edge dimensions show the cut-out as laid on the table.
  Cut step numbers moved to a quarter of the way along each cut, and edge dimensions are inset past the marker radius,
  so labels never overlap.
- Playground: "Fabric to order" box with the breakdown, needed length in the comparison table, cut-out/finished/padding
  columns, and an "Order with padding (17 m)" preset. CLI summary shows the same breakdown.
- Fix: final candidate selection no longer adds a redundant "Next best by objective" tag to already-chosen plans.
- 7 new tests (138 total).

## 1.0.0 — 2026-11-06 (solver `dreamglass-hybrid/1.0.0`, validator `dreamglass-validator/1.0.0`, objective `proposed-v1`, pending approval)

Initial build to the v1.0 design baseline spec.

- Versioned input/options/output contracts; structured input validation with field paths and remediation.
- Scaled-integer geometry with checked arithmetic and aggregate-area limits.
- Grain/orientation model covering source × piece rotation; source rotation is persisted as a display transform.
- Guillotine free-region construction with six piece orderings, four assignment strategies, three remnant-opening
  preferences, four fit rules and six split rules across warehouse-only, joint and roll-only source configurations.
- Beam search over placements, then destroy-and-repair improvement (random, roll-tail, rebuild-source, spatial window,
  empty-roll, drop-remnant), with a tier-1 pre-filter.
- Exact guillotine DP for small jobs. It proves minimum purchased area over all remnant/roll assignments, or proves
  infeasibility.
- Roll finalisation: clip to consumed length, round to permitted whole metres, explicit full-width surplus residual,
  merge of piece-free subtrees.
- Independent validator (coverage, grain, footprint/padding transforms, bounds, overlap, cut-tree replay with
  cycle/orphan detection, cut-step order, purchase minimality, surplus, residual eligibility, full metric recompute).
- Lexicographic objective policy plus Pareto-diverse, tagged candidate selection; scoped bounds (`BoundedGap`).
- SVG cut printouts, audited plan approval, CLI (`optimize`, `validate`, `bench`).
- 131 xUnit tests covering every spec §10 fixture category, plus property-based and mutation tests.
