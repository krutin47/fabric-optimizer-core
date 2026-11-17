# ADR-015 — Ranking objective: cheapest first

**Status:** Accepted for the default ranking (product owner, 12 Nov 2026; confirmed 17 Nov 2026). Stock valuation
remains open (see below). · **Owner:** DreamGlass product owner

## Context

Handbook §32 and §96 list this decision as pending and propose, for review, a material-first order: least new
purchased area or material loss, then usable remnants, then price, then cuts. The product owner decided a different
order on 12 Nov 2026 and confirmed on 17 Nov 2026 that it stays the default: **price first, then least wastage, with
big leftovers that later jobs can use preferred.**

## Decision

The default is `ObjectivePolicy.CheapestFirst` (`cheapest-first`, `Approved = true`):

1. purchase cost (warehouse stock costs nothing, so stock is used before buying where that is cheaper);
2. unusable scrap, compared in 0.01 m² buckets (`ScrapTieToleranceM2`), so a few square centimetres cannot outrank a
   much bigger keepable leftover;
3. largest keepable leftover (bigger is better);
4. running length of the new roll used;
5. number of cuts;
6. purchased area, then warehouse area committed (final tie-breaks).

The handbook's proposed material-first order remains available as `ObjectivePolicy.ProposedV1` (purchased area first,
not approved), and the older "least leftover first" reading as `HistoricalWasteFirst`. Results say which policy
ranked them and whether it is approved.

All the independent metrics the handbook asks for are reported and validated, never folded into one "wastage %":
new ordered, new consumed, rounding surplus, warehouse consumed and untouched, usable leftovers, scrap, cost, cuts,
sources, and a stock-depletion proxy.

## Still open (needs the product owner)

- **Stock valuation.** Warehouse stock counts as free in tier 1, so a plan may cut into a remnant to save a little
  scrap at equal price. The handbook asks whether protecting useful historical stock should outrank a small purchase
  saving. `StockDepletionAreaM2` is reported as a proxy until this is decided; the selection also offers "least
  warehouse stock committed at the best purchase" as an alternative plan.
- Material-specific keep-size thresholds (remnant policy).

## Consequences

Changing the order or the tolerance is a product decision and needs an update to this record. Tests:
`ObjectiveTests` (order and tolerances), `HandbookTests.Exact_search_proves_the_cheapest_purchase_when_price_and_area_disagree`
(price and area pick different rolls).
