# Decision register

Status of every policy the optimiser depends on. "Open" items are implemented as **configuration with a visible
default**, never silently decided in code (spec §12). The main decisions are recorded as ADRs in [`adr/`](adr/README.md)
(ADR-009 validator, ADR-010 quality claims, ADR-015 ranking, ADR-019 search); this page keeps the finer-grained ones.

## Approved and implemented (spec §12)

| Decision | Where |
|---|---|
| Accurate rectangles, padded footprints, exact coverage | `PlanAssembler`, `CutPlanValidator.CheckPlacements` |
| Guillotine cutting with replayable cut trees | `Node.CarvePiece`, `CutPlanValidator.CheckTrees` |
| Full/remnant orientation correctness, physical remnant rotation | `OrientationRules`, validator `Finished()` |
| Warehouse reuse with joint stock/new-roll search | `SearchEngine` configs, `ExactGuillotineSolver` assignment DP |
| Large usable leftovers preferred (tie-break tiers, residual merging) | `ObjectivePolicy.CheapestFirst`, `Node.Canonicalize` |
| Integer-metre orders with explicit rounding surplus | `NormalizedProblem.OrderedLength`, `Finalizer` |
| Ranked, employee-selectable plans; audited choice | `CandidatePool.SelectFinal`, `PlanSelection.Approve` |
| Hybrid time-bounded search | `SearchEngine` |
| Manual layout editing deferred | not implemented |

## Policies

### 1. Objective comparator (spec §12.1) — decided 12 Nov 2026, confirmed 17 Nov 2026 (ADR-015)
- **Decision (product owner):** price first, then lowest wastage, with big remnants that later jobs can use preferred.
  Implemented as `ObjectivePolicy.CheapestFirst` (`cheapest-first`, `Approved = true`, the default): purchase cost →
  unusable scrap (in 0.01 m² buckets, `ScrapTieToleranceM2`) → largest reusable remnant (max) → roll length used →
  cuts → purchased area → stock consumed. Warehouse stock costs nothing, so remnants are used before buying.
- **Why the scrap tolerance:** in the workshop sketch job, a layout with 0.0041 m² more scrap left one 470 × 1,400
  piece instead of 510 × 590 + 420 × 860. Exact scrap comparison ranked the worse layout first.
- **Kept for comparison:** `ObjectivePolicy.ProposedV1` (the spec §5 proposal, unapproved; purchased area → scrap →
  largest remnant → thin fragments → cost → cuts → stock) and `ObjectivePolicy.HistoricalWasteFirst` (total residual →
  cost → purchase → cuts). The playground offers "Cheapest first" and "Least leftover first"; the CLI and API also
  accept `proposed`.
- **Near-equivalence:** `PurchasedAreaTieToleranceM2` buckets purchases so near-equal ones fall through to the next
  tier. Bucketing keeps the ordering transitive.
- **Earlier review note (ProposedV1):** existing stock counted as free on tier 1. So when two plans buy the
  same metres, the optimiser may cut a remnant just to cut scrap (see `samples/spec-example.json`). The
  "Least warehouse stock committed at the best purchase" alternative surfaces this trade-off to the employee.

### 2. Reusable-remnant policy (spec §12.2)
- **Default:** none. If `minimumReusableRemnant` is absent, every residual is reported as not eligible and a warning is raised.
- **Semantics implemented:** `minWidthMm` applies to the short side, `minLengthMm` to the long side (orientation
  independent); optional `minAreaM2` and `maxAspectRatio`. "Thin fragment" means short side < `minWidthMm`.
- Still to decide: material-specific values and whether area or aspect ratio rules apply.

### 3. Supplier order limits (spec §12.3)
- **Implemented semantics (please confirm):** permitted lengths are `minOrderMetres + k·orderIncrementMetres`, capped by
  `maxOrderMetres`. One roll per plan. The price is snapshotted per offer, and cost is reported unrounded in the
  offer's currency. Mixed currencies produce a warning, and the cost comparison is then not meaningful.

### 4. Workload and budgets (spec §12.4)
- Defaults: 60 s time budget (snapshot `search.timeBudgetMs`), deterministic work budget 2,500 units, exact solver
  ≤ 40 instances / ≤ 4,096 piece multisets / ≤ 6 remnants / 30 % of the time budget / 40 million splits
  (`MaxWork`, machine-independent), beam search ≤ 400 instances, 2,000-instance and 200-remnant input limits, 32 kept
  plans, improvement stops after 8,000 steps without progress. All live in `OptimizationOptions`. The pool size and
  stopping rule were tuned on the synthetic benchmark (`testing/README.md`); recalibrate on real jobs and production
  hardware.

## Implementation assumptions to confirm

| Assumption | Rationale | Change point |
|---|---|---|
| Default precision 0.1 mm (`unitsPerMm = 10`); finer input is rejected | Spec: never silently round | `PrecisionPolicy` |
| Piece rotation is 90° clockwise; padding sides rotate with the panel | Needed so labels and nominal rectangles are unambiguous | `OrientationRules`, validator |
| Display: every source is drawn with its running length left to right from the start of the roll and its fixed height top to bottom (`displayRotationDegrees = 270`) | Matches how the workshop reads a roll and their reference cutting app; replaced the v1.0 "pattern top-to-bottom" rotation | `OrientationRules.SheetDisplayRotationDegrees` |
| Playground rolls and warehouse stock default to `patternAxis: Width` (pattern across the roll) | Wide (2,800 mm) fabric is used with the panel height across the roll; panels stand upright on the sheet | `wwwroot/app.js` defaults |
| Identical instances are numbered by position (source, y, x) | Workshop readability; instances are interchangeable | `Finalizer` |
| A plan's sources list only sources that hold a piece; listing untouched stock is a validation error | Spec §5 accounting warning | validator `UNUSED_SOURCE` |
| Approving a lower-ranked candidate requires a reason | Auditability | `PlanSelection.Approve` |
| Pareto alternatives consider purchased area, scrap, largest remnant, cost, cuts and stock consumed | Spec §5.4 | `PlanRanking.Dominates` |
| Fragmentation index = 1 − largest residual leaf ÷ total residual (6 dp) | Spec asks for a documented penalty | `PlanAssembler`, validator |
| Search is single-threaded | Simplest reproducibility story; parallelism needs a merge policy (fixed partitions with derived seeds, merged in partition order) | `SearchEngine` |
| Organisation id is opaque to the library and must match on every stock row | The host resolves it from the verified identity; the library only refuses mixtures (defence in depth) | `InputValidator`, validator `ORGANIZATION_MISMATCH` |
| A plan is bound to the canonical hash of its snapshot (decimals normalised, so 600 and 600.0 are the same) | Handbook §97, A5; stale plans must fail | `OptimizerJson.SnapshotHash`, validator `INPUT_VERSION_MISMATCH` |
| Purchase cost is exact for ranking; documents show it rounded half-up to 2 decimals | Handbook §15 rounding mode | `Money`, `PurchaseLine.PurchaseCostRounded` |
| Optimiser returns statuses rather than throwing `OperationCanceledException` | Spec: failures are explicit runs | `HybridCutOptimizer` |

## Deferred (spec §1, handbook §3)
Manual layout editing, re-optimisation after stock discrepancies, multi-job batching, non-guillotine/irregular
nesting, other materials, arbitrary rotations, multi-roll purchasing within one candidate. CP-SAT benchmarking is
not wired in: the in-house exact DP covers small-case proofs without a native dependency. Revisit if larger proofs are needed.
