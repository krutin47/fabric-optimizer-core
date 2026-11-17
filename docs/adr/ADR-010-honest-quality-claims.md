# ADR-010 — Quality claims need a proof or a bound

**Status:** Accepted · **Date:** 6 Nov 2026, extended 17 Nov 2026 · **Owner:** DreamGlass technical owner

## Context

A heuristic's best plan is not proven optimal, and the business ranking has several tiers (ADR-015). Handbook §32,
§96 and law 11 forbid presenting a best-found plan as optimal.

## Decision

- `QualityStatus.OptimalProven` is never emitted; the validator rejects it.
- Each plan carries up to two bounds, each stating its metric, scope, method and whether it is **proven**:
  - `Bound`: newly purchased area (m²);
  - `CostBound`: purchase cost.
  A proven bound comes only from a completed exact search over all guillotine plans, source assignments and roll
  offers. Otherwise the bound is a relaxation (area beyond the warehouse, the longest piece, wide pieces that cannot
  share the roll width).
- `BoundedGap` is used only when the ranking's **first tier** has a bound that is proven or met with zero gap. With
  the default ranking that is the price.
- The gap is reported in the metric's unit and as a percentage **of the bound** (`GapPercentOfBound`).
- User-facing wording: "Price proven lowest" / "Price at the lower bound" / "Fabric ordered proven least", and
  otherwise "Best verified plan found within the search budget". Scrap, leftovers and cuts are never claimed proven.
- The validator checks that a label is backed by its bound and that the bound's arithmetic is right
  (`UNVERIFIABLE_CLAIM`). It cannot check that a bound is true; that is covered by tests against the exact solver
  (`ExactSolverTests`, `HandbookTests.Exact_search_proves_the_cheapest_purchase_when_price_and_area_disagree`).

## Consequences

The exact solver now proves the minimum cost as well as the minimum area, because price is the first tier.
