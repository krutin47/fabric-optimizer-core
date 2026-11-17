# ADR-009 — Guillotine-constrained optimiser with an independent validator

**Status:** Accepted · **Date:** 6 Nov 2026 (recorded 17 Nov 2026) · **Owner:** DreamGlass technical owner

## Context

The workshop cuts fabric with straight edge-to-edge cuts. A layout that looks efficient but cannot be cut that way is
worse than no layout (handbook §29: "one valid plan beats ten beautiful but infeasible renderings"). Free rectangle
packers (MaxRects, Skyline) do not guarantee guillotine cuts (§33, §95).

## Decision

1. Every layout is built by splitting free regions with full-region cuts, so it is a guillotine tree by construction.
2. A separate validator (`Validation/CutPlanValidator.cs`) re-derives everything from the snapshot and the plan's
   public contract: piece coverage, sizes and padding, grain after rotation, bounds, overlap, cut-tree replay from the
   whole source, cut order, purchase rounding, leftover eligibility, every metric and the material ledger, and that
   the plan belongs to the snapshot it is checked against. It shares no code with the solver.
3. A plan is returned, approved or printed only after the validator passed it. A rejected plan is a generator defect:
   it is recorded with its stable failure codes and never shown.

## Consequences

- Validation costs time. ADR-019 keeps that cost off the search's critical path without letting any unvalidated plan out.
- Non-guillotine nesting is out of scope until the workshop can cut it and the validator can prove it.
- Failure codes are a contract (`docs/testing/validator-codes.md`).

## Evidence

`ValidatorTests`, `PropertyTests.Random_corruptions_are_always_rejected`, `HandbookTests.InvalidGuillotineCut_CannotBePublished`,
and every test that runs the optimiser (`T.RunOk` re-validates every returned plan).
