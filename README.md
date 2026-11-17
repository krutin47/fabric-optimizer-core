# DreamGlass Optimizer

Hybrid, time-bounded **guillotine cutting optimiser** for DreamGlass fabric jobs, built to
`DreamGlass-Optimizer-Implementation-Spec.md` (v1.0 design baseline, 6 Nov 2026).

> Contract: given a material, roll offers, rectangular pieces with quantities, per-side padding, grain rules and
> optional remnants, return the best **verified** guillotine-cuttable plans found within the budget — exact
> coordinates, source assignments, ordered cuts, whole-metre purchase and residuals. It never claims an unproved
> global optimum.

This repository contains the **pure optimiser core** (spec §11: no database or HTTP dependencies), a CLI and the
test suite. The ASP.NET Core API, worker/queue, PostgreSQL persistence, React UI and Auth0 integration are the next
layers and are not included here.

## Quick start

```bash
dotnet build
dotnet test                                    # 151 tests: §10 fixtures, validator, exact solver, properties
dotnet run --project src/DreamGlass.Optimizer.Cli -- optimize samples/spec-example.json --out result.json --svg prints/
dotnet run --project src/DreamGlass.Optimizer.Cli -- validate samples/spec-example.json result.json
dotnet run --project src/DreamGlass.Optimizer.Cli -c Release -- bench --sizes 10,50,200,500 --time 5000
```

Requires the .NET 10 SDK. Add `--deterministic --work 2500` for reproducible runs. Plans are ranked cheapest first (the
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
title and every printout footer show the build number (e.g. "DreamGlass 1.3.1"), so you can check which build is running. It listens on this computer only; pass
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
piece too large for any roll. The playground has no authentication
and saves nothing, and its search budget is capped at 30 s. It is a test harness, not the production API.

## Pipeline

```
OptimizationSnapshot ─► InputValidator ─► Normalizer ─► SearchEngine ─────────────────────────────┐
  (decimal mm)          structured errors   scaled ints   0 exact DP (small jobs, proves tier 1)  │
                        + remediation       legal 0°/90°  1 multi-start construction              │
                                            orientations  2 beam search over placements           │
                                                          3 destroy-and-repair improvement        │
                                                                                                   ▼
            ranked, diverse candidates ◄─ CandidatePool ◄─ CutPlanValidator ◄─ PlanAssembler ◄─ Finalizer
            (tags, quality, bound)        (verified only)   (independent)       (metrics from    (clip roll, round
                                                                                 final trees)     to metres, surplus)
```

| Component | File | Notes |
|---|---|---|
| Contracts | `src/DreamGlass.Optimizer/Contracts/` | Versioned input/options/output records (spec §3, §8). |
| Input validation | `Normalization/InputValidator.cs` | Field paths, remediation, limits, per-piece "fits no source" explanations. |
| Orientation rules | `Normalization/OrientationRules.cs` | Grain semantics; source rotation reduced to a display transform. |
| Guillotine construction | `Solver/Construction.cs`, `Geometry.cs` | Free-region splitter: every placement is made by full-region cuts, so trees are guillotine by construction. |
| Search orchestration | `Solver/SearchEngine.cs` | Source configs (warehouse-only, joint stock+roll per offer, roll-only), 6 orderings × assignment strategies × fit/split rules, seeded restarts, beam, LNS. |
| Exact solver | `Exact/ExactGuillotineSolver.cs` | Memoised DP over piece multisets and normal-pattern widths + all remnant assignments. Proves minimum purchased area for small jobs. |
| Independent validator | `Validation/CutPlanValidator.cs` | **Release blocker.** Shares only DTOs with the generator; re-derives units, grain, order rules, eligibility and every metric. |
| Ranking | `Solver/PlanRanking.cs`, `CandidatePool.cs` | Configurable lexicographic tiers, Pareto dominance, tagged diverse selection. |
| Printouts | `Printing/CutPlanSvgRenderer.cs` | One SVG per source; refuses unvalidated plans. |
| Approval | `Approval/PlanSelection.cs` | Audited employee choice (re-validates; reason required below rank 1). |

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
- `QualityStatus`: `BestFound` by default; `BoundedGap` only with an explicit bound whose **scope is tier 1
  (purchased area)** — from the exact DP, or when the relaxation bound is met. `OptimalProven` is reserved and the
  validator rejects it in v1, because no method here proves the full lexicographic objective.
- Every candidate carries `validation` from the independent validator; plans that fail are never returned.

## Determinism

`ExecutionMode.Deterministic` bounds search by work units (default 2,500), with the time budget as a safety cap.
Identical input, seed and versions give an identical `resultHash`; `diagnostics.reproducible` turns false if the
safety cap fired. Time-budgeted mode is not bit-reproducible. The search is single-threaded in v1. Parallel runs
need the documented merge policy in `docs/DECISIONS.md` first.

## Measured performance (this build, 4-vCPU cloud container, Release, 5 s budget, synthetic data)

| Instances | Wall time | Exact | Best purchased vs tier-1 lower bound | Utilisation | Validator rejections |
|---:|---:|---|---|---:|---:|
| 10 | 0.9–1.3 s (converged) | proved | gap 0 | 55–63 % | 0 |
| 50 | 2.0–3.0 s (converged) | – | +7.7 % / +21 % vs a weak area bound | 88–91 % | 0 |
| 200 | 5 s (budget) | – | +8.4 % / +8.3 % | 92 % | 0 |
| 500 | 5 s (budget) | – | +6.8 % / +6.9 % | 94 % | 0 |

The lower bound on purchased area for jobs above the exact-solver limit comes from a simple area relaxation. It is
weak, so these gaps overstate the true distance to the optimum. The real gap is unknown, and none of these figures
is a product commitment (spec §9). Set service limits from benchmarks on DreamGlass job data and the target hardware.

## Open decisions

The optimiser runs, but several business rules are still **configurable placeholders**. See
[`docs/DECISIONS.md`](docs/DECISIONS.md) for the comparator (spec §12.1), remnant policy (§12.2), supplier limits (§12.3),
workload budgets (§12.4), and the implementation assumptions that need confirmation.
