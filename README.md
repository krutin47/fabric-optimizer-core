# DreamGlass Optimizer

Hybrid, time-bounded **guillotine cutting optimiser** for DreamGlass fabric jobs, built to
`DreamGlass-Optimizer-Implementation-Spec.md` (v1.0 design baseline, 6 Nov 2026) and the **DreamGlass Production
Engineering Handbook v1.1** (14 Nov 2026). Rules for changing it: [`AGENTS.md`](AGENTS.md).

> Contract: given a material, roll offers, rectangular pieces with quantities, per-side padding, grain rules and
> optional remnants, return the best **verified** guillotine-cuttable plans found within the budget — exact
> coordinates, source assignments, ordered cuts, whole-metre purchase and residuals. It never claims an unproved
> global optimum.

## What is in this repository, and what is not

| In this repository | Not here: the host application (handbook Parts II–III, V–VI, VIII) |
|---|---|
| Optimiser library: input checks, search, exact small-case solver, bounds, ranking | Organisations, members, roles, Auth0 login, support access |
| Independent validator (stable failure codes) | PostgreSQL schema, tenant-safe keys, row-level security |
| Plan approval record with live-state checks (stale revision, stock version, organisation) | Atomic stock reservation, job lifecycle, remnant confirmation after cutting |
| Printouts: SVG sheets and the printable cut plan (HTML) | Worker queue, retries, audit trail, PDF storage and downloads |
| CLI, benchmark, local playground (a test harness with an optional password) | Product screens, design system, full WCAG 2.2 AA audit, Azure deployment |

The library takes an organisation id and stock versions in the snapshot and refuses mixtures, but the host is
responsible for authorisation and for scoping every query to the tenant. `docs/requirements/ledger.csv` lists each
handbook requirement with its status here.

## Quick start

```bash
dotnet restore --locked-mode
dotnet build -c Release                        # warnings are errors
dotnet test -c Release                         # 177 tests: fixtures, validator, exact solver, properties, handbook
dotnet run --project src/DreamGlass.Optimizer.Cli -- optimize samples/spec-example.json --out result.json --svg prints/ --html prints/
dotnet run --project src/DreamGlass.Optimizer.Cli -- validate samples/spec-example.json result.json
dotnet run --project src/DreamGlass.Optimizer.Cli -c Release -- bench --sizes 10,50,200,500 --time 5000
```

Requires the .NET 10 SDK (`global.json`). Add `--deterministic --work 2500` for reproducible runs. `--html` writes the
printable cut plan: what to order, the warehouse pieces to take, the material summary, and per sheet the drawing with
numbered cut steps, pieces and leftovers as tables (handbook §36, §47). Plans are ranked cheapest first (the
approved order: price, then least scrap, then the biggest keepable leftover); `--objective historical` ranks least
leftover first, and `--scrap-tolerance <m²>` changes how big a scrap difference must be to count (default 0.01).

### Playground UI

A small local web page for trying jobs by hand: edit pieces, supplier rolls and warehouse stock (or paste JSON), run
the real solver, then see what to order and which warehouse pieces to use. Each plan shows three drawings in order:
the supplier's whole roll with the ordered part and where the supplier cuts, the ordered fabric's cutting sheet, and
one sheet per warehouse remnant used.
Every label has an explanation on hover or tap. While you edit, the form runs the optimiser's own input checks
(`POST /api/check`), so a piece that fits no roll or remnant is flagged on its row before you run.

```bash
dotnet run --project src/DreamGlass.Optimizer.Playground -c Release
```

It opens your browser at <http://localhost:5080> (or the next free port). On a hosting service (Azure App Service or a
container platform) it uses the port the host assigns instead and asks for a shared password: set the app setting
`DREAMGLASS_PASSWORD` (12+ characters). Without one, a hosted playground shows how to set it instead of running open. The console window, the line under the page
title and every printout footer show the build number (e.g. "DreamGlass 1.4.0"), so you can check which build is running. It listens on this computer only; pass
`--urls http://0.0.0.0:5080` to reach it from other machines, and `--no-browser` to skip opening a browser.

**Download-and-run packages** (no .NET needed on the target computer): `scripts/package-apps.sh` writes one zip per
system to `dist/` (Windows x64, macOS Apple silicon, macOS Intel, Linux x64). Each holds a single self-contained
program, the page files and a README.txt with start steps; on Windows you double-click `DreamGlass.Playground.exe`, on
macOS `Start DreamGlass.command`. The programs are not code-signed by a publisher, so Windows SmartScreen and macOS
Gatekeeper ask once (the README.txt says how). Building them needs the .NET 10 SDK and `zip`, and downloads each
target's runtime from NuGet on the first run.

Each example job has a one-line explanation. They cover the spec example; ordering
15 m from a 31 m supplier roll vs. the same job cut from a whole 31 m roll already in the warehouse; an order with padding
(16,500 + 180 mm → 17 m); a remnant with a sideways pattern; warehouse-only; smart remnant assignment; two roll widths; a
row of panels setting the section length (the workshop sketch); a 60-piece mixed job; a proven-impossible job; and a
piece too large for any roll. Warehouse rows have a "Leave out because" column: a reason sends the piece as excluded
stock, recorded with the job and never used. Each plan has a "Printable cut plan" button, and pointing at or tabbing
to a piece or cut in the tables highlights it on the drawing. The playground saves nothing, its search budget is
capped at 30 s, and it is a test harness, not the production API.

## Pipeline

```
OptimizationSnapshot ─► InputValidator ─► Normalizer ─► SearchEngine ──────────────────────────────┐
  (decimal mm, org,     structured errors  scaled ints   0 exact DP (small jobs: proves the least    │
   excluded stock)      + remediation      legal 0°/90°    purchase area and the lowest price)      │
                                           orientations  1 multi-start construction                 │
                                                         2 beam search over placements              │
                                                         3 destroy-and-repair, two walks            │
                                                           (ranking, and shortest roll at best price)│
                                                                                                     ▼
 ranked candidates ◄─ CutPlanValidator ◄─ PlanAssembler ◄── CandidatePool ◄── digest ◄── Finalizer
 (tags, quality,      (independent; every  (full plan for   (kept by figures; (metrics +  (clip roll, round to
  bounds, snapshot     returned plan)       selected only)   duplicates and    128-bit     metres, surplus,
  hash)                                                      weak ones skipped) key)        leftover re-split)
```

| Component | File | Notes |
|---|---|---|
| Contracts | `src/DreamGlass.Optimizer/Contracts/` | Versioned input/options/output records (spec §3, §8); material ledger metrics (handbook §96). |
| Input validation | `Normalization/InputValidator.cs` | Field paths, remediation, limits, organisation and excluded-stock checks, per-piece "fits no source" explanations. |
| Orientation rules | `Normalization/OrientationRules.cs`, `Printing/SheetTransform.cs` | Grain semantics; canonical/display transforms with inverses (handbook §30). |
| Guillotine construction | `Solver/Construction.cs`, `Geometry.cs` | Free-region splitter: every placement is made by full-region cuts, so trees are guillotine by construction. Row sections. |
| Search orchestration | `Solver/SearchEngine.cs` | Source configs (skipped when they cannot hold the job or cannot rank first), orderings × strategies × fit/split rules, seeded restarts, beam, destroy-and-repair. |
| Exact solver | `Exact/ExactGuillotineSolver.cs` | Memoised DP over piece multisets with bound pruning; proves minimum purchase area and minimum price for small jobs. |
| Bounds | `Solver/Bounds.cs` | Area, longest-piece and wide-piece relaxation per roll offer, in m² and in money. |
| Independent validator | `Validation/CutPlanValidator.cs` | **Release blocker.** Shares only DTOs with the generator; re-derives everything, checks the snapshot hash and the ledger. Codes: `docs/testing/validator-codes.md`. |
| Ranking | `Solver/PlanRanking.cs`, `CandidatePool.cs` | Configurable lexicographic tiers (ADR-015), tagged diverse selection; validates each selected plan (ADR-019). |
| Printouts | `Printing/CutPlanSvgRenderer.cs`, `Printing/CutPlanDocument.cs` | SVG per source and the printable cut plan with tables; refuse unvalidated plans. |
| Approval | `Approval/PlanSelection.cs` | Audited choice; re-validates and checks live job revision, stock versions and organisation state. |

## Conventions (shared contract with the validator)

- **Coordinates:** origin top-left of each source's canonical (unrotated) frame; `x` across width, `y` along length;
  half-open rectangles. Output in decimal mm, solver in integers at `precision.unitsPerMm` (default 10 = 0.1 mm).
  Input finer than the precision is **rejected**, never rounded.
- **Piece rotation:** 90° clockwise in the source frame. Rotated footprint = (length + top + bottom) × (width + left + right).
  Rotated padding: source-top = panel-left, source-right = panel-top, source-bottom = panel-right, source-left = panel-bottom.
- **Grain:** finished direction is `Vertical` when (source axis is `Length`) XOR (piece rotated) holds as "pattern along
  the panel's length". Legality depends only on rotation relative to the material. A roll's `widthMm` is its fixed
  height (the supplier never cuts it); `patternAxis: Width` means the pattern runs across the roll, so upright panels
  stand across the roll's height (usual for wide fabric, and the playground default).
- **Sheets as drawn:** every source is drawn as unrolled on the cutting table: its length (running length) left to
  right from the start of the roll, its width (the roll's fixed height) top to bottom. That is `displayRotationDegrees
  = 270`, a quarter turn counter-clockwise of the canonical frame, (x, y) ↦ (y, W − x). Printouts, leftover sizes and the
  playground's cut list use this view; JSON coordinates stay canonical.
- **Ids:** instances are `{pieceId}#{n}`; new rolls are `new-roll:{offerId}`; remnants use their `stockId`.
- **Cuts:** `Vertical` = line `x = position` (left/right children); `Horizontal` = line `y = position` (top/bottom).
  `cutSteps` is a pre-order (parent-before-child) execution order.
- **Roll purchase:** allowed lengths are `minOrderMetres + k·orderIncrementMetres` (≤ `maxOrderMetres`); the plan
  orders the smallest allowed length covering the consumed length. The first roll cut is the full-width cut at the
  consumed length; the remainder is a residual flagged `roundingSurplus`.
- **Supplier roll vs. own roll:** `maxOrderMetres` is how much is on the supplier's roll. Only the ordered metres
  belong to the plan, so a 31 m supplier roll with a 14,450 mm layout orders 15 m, and the other 16 m are neither bought
  nor drawn. A whole roll you already own is warehouse stock (a `stock` item 31,000 mm long); then whatever is left after
  cutting is reported as a reusable remnant. `fixedLengthMm` is a test mode that treats the whole length as bought, so it
  is not offered in the playground form.
- **Order quantity:** the purchase line reports the length the layout needs (`consumedLengthMm`, incl. padding) split
  into `lengthWithoutPaddingMm` + `paddingLengthMm`, and the order rounded **up** to whole metres
  (e.g. 16,500 + 180 padding = 16,680 mm → 17 m). The padding share is defined on the actual arrangement: compact the cut
  tree along the roll (cuts across the width stack, cuts along the length take the longer side, leftovers take no
  length) once with padded and once with finished sizes, and subtract. The validator re-derives it.
- **Cut-out size:** every placement reports `cutWidthMm × cutLengthMm`, the full piece to cut including padding, in the
  finished-panel frame (width + left + right, length + top + bottom), next to the finished size. Printouts show both,
  plus the cut-out's edge dimensions as laid on the table.

## Statuses and honesty

- `RunStatus`: `Succeeded`, `InvalidInput`, `NoFeasiblePlan` (proven by area or exhaustive search), `NoPlanFound`
  (schedule exhausted, not proven), `TimedOutWithoutPlan`, `Cancelled`, `Failed` — all distinct. The optimiser never
  throws for bad input, time-outs or internal faults.
- `QualityStatus`: `BestFound` by default; `BoundedGap` only when the ranking's first tier (the price, by default)
  has a bound that is proven or met with zero gap. Each plan carries `bound` (purchased area) and `costBound` (price),
  each with its scope, method, `proven` flag and gap as a percentage of the bound. `OptimalProven` is never emitted
  and the validator rejects it (ADR-010). Wording shown to people: "Price proven lowest", "Price at the lower bound",
  or "Best verified plan found".
- Every candidate carries `validation` from the independent validator and the hash of the snapshot it belongs to;
  plans that fail are never returned. Plans the validator rejected are listed under `diagnostics.rejectedCandidates`.

## Determinism

`ExecutionMode.Deterministic` bounds search by work units (default 2,500), with the time budget as a safety cap.
Identical input, seed and versions give an identical `resultHash`; `diagnostics.reproducible` turns false if the
safety cap fired. Time-budgeted mode is not bit-reproducible. The search is single-threaded in v1. Parallel runs
need the documented merge policy in `docs/DECISIONS.md` first.

## Measured performance (1.4.0, 4-vCPU cloud container, Release, 5 s budget, synthetic data, 17 Nov 2026)

| Pieces | Wall time | Exact solver | Price above its lower bound | Purchase area above its lower bound | Validator rejections |
|---:|---:|---|---|---|---:|
| 10 | 0.3–1.1 s (converged) | proved | 0 % (proven) | 0 % (proven) | 0 |
| 50 | 1.3–2.6 s (converged) | – | 7.7 % / 27.3 % / 20.0 % | 7.7 % / 21.4 % / 20.0 % | 0 |
| 200 | 5 s (budget) | – | 7.4 % / 8.3 % / 7.8 % | 8.4 % / 8.3 % / 8.9 % | 0 |
| 500 | 5 s (budget) | – | 5.6 % / 6.5 % / 5.3 % | 6.0 % / 6.9 % / 5.3 % | 0 |

Above the exact solver's range the bounds are relaxations (area, longest piece, wide pieces). They are weak, so these
gaps overstate the distance to the best possible plan; the real gap is unknown. Against 1.3.3 on the same 56 jobs at
3 s each, 1.4.0 finds a better plan by the approved ranking on 49, an equal one on 5 and a worse one on 2
(`docs/testing/README.md`). None of these figures is a product commitment (handbook §59): set budgets from benchmarks
on real DreamGlass jobs and the target hardware.

## Documents

| | |
|---|---|
| [`AGENTS.md`](AGENTS.md), [`CONTRIBUTING.md`](CONTRIBUTING.md) | Rules for changing this repository |
| [`docs/handbook/`](docs/handbook/README.md) | Which handbook parts apply here, and why its text is not in this public repository |
| [`docs/adr/`](docs/adr/README.md) | Decisions: validator, quality claims, ranking, search |
| [`docs/requirements/ledger.csv`](docs/requirements/ledger.csv) | Handbook requirements with status, tests and evidence |
| [`docs/testing/`](docs/testing/README.md) | Test matrix, benchmark method and results, validator codes |
| [`docs/security/threat-model.md`](docs/security/threat-model.md) | Threats for the library and playground |

## Open decisions

The optimiser runs, but several business rules are still **configurable placeholders**. See
[`docs/DECISIONS.md`](docs/DECISIONS.md) for the comparator (spec §12.1), remnant policy (§12.2), supplier limits (§12.3),
workload budgets (§12.4), and the implementation assumptions that need confirmation. The ranking itself is decided
(ADR-015); how to value warehouse stock against a small purchase saving is still open.
