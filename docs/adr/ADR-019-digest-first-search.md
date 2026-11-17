# ADR-019 — Keep layouts by their figures; validate every plan that is returned

**Status:** Accepted · **Date:** 17 Nov 2026 · **Owner:** DreamGlass technical owner

## Context

In 1.3.3 every complete layout was turned into a full plan (strings, decimals, cut tree DTOs) and passed through the
independent validator before the search could compare it. On a 200-piece job that was about two thirds of the search
time, so far fewer layouts were explored. Handbook §61 asks to optimise only after profiling; §33 asks for a bounded,
reproducible search; ADR-009 requires that nothing unvalidated leaves the library.

## Decision

1. A finished layout is first reduced to a **digest**: its exact metrics (the same object the plan will carry) and a
   128-bit structural key. Both come from one integer pass over the cut trees.
2. Duplicates (same key) and layouts that could not earn a place among the kept ones are dropped there.
3. Kept layouts are built into full plans and validated **when they are selected for the result**. If the validator
   rejects one, it is dropped and the selection is repeated, so the result only ever holds validated plans.
4. `OptimizationOptions.ValidateEveryKeptPlan` validates each layout the moment it is kept. The test suite turns it on,
   so generator defects surface in the search, not only at the end.

## Alternatives considered

- Validate every complete layout (1.3.3): safest feedback loop, but it starved the search.
- Validate only the final best plan: would let alternatives through unvalidated. Rejected.

## Consequences

- Benchmark against 1.3.3 on 56 synthetic jobs, 3 s each: better on 49, equal on 5, worse on 2
  (`docs/testing/README.md`).
- A generator defect in a kept layout that is never selected goes unnoticed in production mode; the test suite's
  `ValidateEveryKeptPlan` and the property tests cover that.
- `RunDiagnostics` reports layouts skipped as duplicates or uncompetitive, plans validated, and any plan the validator
  rejected with its failure codes.
