# Testing

## Running

```bash
dotnet restore --locked-mode
dotnet test -c Release                       # 177 tests, about 15 s
dotnet test -c Release --filter HandbookTests
```

Every test that runs the optimiser re-validates every returned plan with the independent validator (`T.RunOk`) and
checks the material ledger. Test runs use deterministic, work-bounded search with `ValidateEveryKeptPlan` on, so a
generator defect in any kept layout fails the test. All fixtures are synthetic.

## Handbook §38 test matrix

| Family | Covered by | Not here (host application) |
|---|---|---|
| Identity of pieces | `Quantity_expands_into_distinct_instances_without_duplicates_or_omissions`, `ValidatorTests.Missing_duplicate_and_unknown_instances_fail_exact_coverage` | |
| Physical fit | `Exact_width_fits_and_one_unit_more_is_rejected_naming_the_binding_width`, `InputValidationTests.Padding_that_pushes_a_locked_piece_past_the_roll_width_is_a_structured_error`, `Exact_fit_has_zero_residual_and_a_single_leaf_tree` | |
| Orientation | `OrientationTests.*`, `UnconstrainedPieceRotation_StillPreservesActualPatternRules`, `Sheet_transforms_round_trip`, `Padding_footprint_and_pattern_transforms_agree_with_the_solver_and_round_trip` | |
| Packing | `ValidatorTests.Overlap_and_out_of_bounds_are_detected`, `Pinwheel_arrangement_admits_no_guillotine_tree_and_is_rejected`, `InvalidGuillotineCut_CannotBePublished` | |
| Costs | `PurchaseRoundUp_PreservesFullWidthRemainder`, `Order_reports_length_without_padding_plus_padding_and_rounds_up`, `Supplier_increment_and_minimum_are_respected`, `Exact_search_proves_the_cheapest_purchase_when_price_and_area_disagree` | |
| Warehouse | `Warehouse_only_plan_has_no_purchase_order`, `Joint_search_beats_greedy_remnants_first_assignment`, `Excluded_stock_needs_a_reason_and_is_never_used`, `Stock_must_be_available_unique_and_of_the_job_material` | Reservation and stock states |
| Concurrency | | `TwoConfirmations_OnlyOneReservesSameRemnant` (needs PostgreSQL) |
| Revisions | `StaleJobRevision_DoesNotApproveOldPlan`, `A_plan_is_bound_to_its_snapshot`, `Changed_or_missing_stock_is_never_swapped_silently` | Worker re-checks |
| Robustness | `Precancelled_run_returns_cancelled_without_partial_plans`, `Cancellation_mid_search_preserves_the_valid_incumbent`, `Tiny_time_budget_never_returns_an_unvalidated_plan`, `Overflowing_coordinates_fail_with_arithmetic_overflow_not_an_exception` | Worker crash and retry |
| Quality | `ExactSolverTests.Exact_minimum_length_matches_an_independent_grid_brute_force` (exhaustive oracle), `Determinism_same_input_seed_and_version_give_identical_results`, `PropertyTests.*`, `Quality_labels_need_the_bound_they_rest_on` | |
| Printing | `Printed_cut_plan_has_one_section_per_source_and_every_cut_step`, `Warehouse_only_plan_says_no_new_fabric_to_order`, `Roll_printout_shows_the_order_and_cut_out_sizes` | PDF tagging |
| Accessibility | Printed plan and drawings carry a text equivalent (tables, `role="img"` + `<title>`) | Keyboard and screen-reader journeys of the product UI |
| Isolation | `Stock_of_another_organisation_is_rejected_by_input_check_and_validator` | Cross-tenant API, storage and download tests |

Counterexamples the handbook asks for (§96): greedy remnants-first is beaten
(`Joint_search_beats_greedy_remnants_first_assignment`), a single roll width is beaten
(`Alternative_roll_widths_are_compared_and_the_tradeoff_is_retained`), and free non-guillotine nesting is rejected
(`Pinwheel_arrangement_admits_no_guillotine_tree_and_is_rejected`).

## Benchmark: comparing two builds

The CLI generates seeded synthetic jobs (mixed sizes and grain rules, 2–6 remnants, two roll widths at different
prices). To compare two builds on the same jobs:

```bash
dotnet run --project src/DreamGlass.Optimizer.Cli -c Release -- bench --sizes 10,20,30,60,120,200,500 --seeds 8 --time 3000 --csv > new.csv
# same command with the previous build → base.csv
python3 scripts/compare-bench.py base.csv new.csv
```

`compare-bench.py` ranks each job's best plan by the approved cheapest-first order and reports how often the new build
is better, equal or worse, and on which tier it was decided. Run both builds on the same machine, one after the other.

### 1.4.0 against 1.3.3 (17 Nov 2026, 4-vCPU cloud container, 3 s per job)

| Pieces | Jobs | Better | Equal | Worse | 1.3.3 ms | 1.4.0 ms | Decided by |
|---:|---:|---:|---:|---:|---:|---:|---|
| 10 | 8 | 4 | 4 | 0 | 716 | 469 | larger keepable leftover ×3, fewer cuts ×1 |
| 20 | 8 | 7 | 1 | 0 | 836 | 476 | less scrap ×6, lower price ×1 |
| 30 | 8 | 7 | 0 | 1 | 1,193 | 842 | lower price ×3, less scrap ×4; worse on price ×1 |
| 60 | 8 | 8 | 0 | 0 | 2,575 | 2,536 | lower price ×4, less scrap ×4 |
| 120 | 8 | 8 | 0 | 0 | 3,025 | 3,008 | lower price ×1, less scrap ×7 |
| 200 | 8 | 8 | 0 | 0 | 3,020 | 3,013 | lower price ×4, less scrap ×4 |
| 500 | 8 | 7 | 0 | 1 | 3,024 | 3,043 | lower price ×4, less scrap ×3; worse on price ×1 |

Total: better on 49 of 56 jobs, equal on 5, worse on 2. Jobs of 60 pieces or more use the whole 3 s budget in both
builds; smaller jobs finish when the search stops improving. These are synthetic jobs on one machine: they show the
direction of the change, not a product commitment (handbook §59). Calibrate budgets on real DreamGlass jobs.

### Exact solver range

The exact solver proves the minimum purchased area and the minimum price for small jobs. Its limit is the number of
distinct piece combinations, not the piece count: 24 pieces of 3 types or 40 pieces of 2 types complete in under
0.3 s, while 16 all-different pieces may not finish within its share of the budget. It stops at a
machine-independent work cap (`ExactSolverOptions.MaxWork`) and then makes no claim. Measured on the same machine:
mixed jobs of 12–14 pieces were proven in 0.05–1.0 s; at 16 pieces, 3 of 5 attempts finished.
