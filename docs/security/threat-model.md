# Threat model: optimiser library and playground

Scope: what this repository contains (handbook §23, §108). The product's API, database, identity and storage have
their own threat model in the host application. Last reviewed 17 Nov 2026.

## Data flow

```
caller (host API, CLI or playground page)
   │ snapshot JSON (pieces, roll offers, stock, policy)          untrusted
   ▼
InputValidator ──► Normalizer ──► SearchEngine ──► CutPlanValidator ──► result (plans, printouts)
                                   (bounded by time, work, limits)        independent re-check
```

Trust boundaries: the snapshot is untrusted until `InputValidator` accepts it; a plan is untrusted until
`CutPlanValidator` accepts it (also when it comes back from storage or a client); the playground page is untrusted.

## Threats

| ID | Threat | Prerequisite | Impact | Control | Test / evidence | Status |
|---|---|---|---|---|---|---|
| T1 | Pathological input exhausts CPU or memory (huge counts, sizes, budgets) | Any caller | Worker starved for all tenants | `OptimizerLimits` (pieces, instances, stock, offers, dimensions, area, budget, candidates); integer overflow guarded; exact solver work cap; playground 1 MB body and 30 s cap | `InputValidationTests.*Limits*`, `Aggregate_area_limits_prevent_integer_overflow` | Implemented; per-tenant quotas are the host's |
| T2 | Forged or tampered plan approved or printed | Client or storage can alter a plan | Wrong cuts, wasted fabric, wrong purchase | Approval and printing re-validate; plan bound to its snapshot hash; quality claims checked | `InvalidGuillotineCut_CannotBePublished`, `A_plan_is_bound_to_its_snapshot`, `Quality_labels_need_the_bound_they_rest_on` | Implemented |
| T3 | Plan for an old job revision or old stock approved | Edits or reservations after the run | Wrong pieces cut, remnant double use | `PlanSelection.Approve` checks live revision, stock versions and availability; no silent substitution | `StaleJobRevision_DoesNotApproveOldPlan`, `Changed_or_missing_stock_is_never_swapped_silently` | Implemented; atomic reservation is the host's |
| T4 | Stock of another organisation offered to a job | Host builds the snapshot from an unscoped query | Cross-tenant stock use | Every stock row must carry the job's organisation; validator rejects foreign stock | `Stock_of_another_organisation_is_rejected_by_input_check_and_validator` | Implemented as defence in depth; tenant scoping is the host's |
| T5 | Stored XSS through labels or ids in printouts | Attacker controls piece labels | Script in the playground or printed document | SVG text escaped (`SecurityElement.Escape`), document HTML-encoded, playground escapes all values; CSP `script-src 'self'` | Printouts render labels as text | Implemented |
| T6 | Playground exposed on the internet without a login | Hosted without configuration | Anyone runs the solver | Hosted playground refuses to run without `DREAMGLASS_PASSWORD` (12+ characters); Basic auth with constant-time compare and a delay on wrong passwords; HTTPS from the host | Manual run 17 Nov 2026 | Implemented; the playground is a test harness, not the product |
| T7 | Error responses leak internals | Malformed request | Information disclosure | Problem Details with a code, safe detail and correlation id; no stack traces | Manual check | Implemented |
| T8 | Supply chain: changed package or CI action | Upstream compromise | Malicious code in builds | Lock files with locked restore; actions pinned to commits; read-only CI permissions; no secrets in CI | `.github/workflows/ci.yml` | Implemented; dependency and secret scanning not yet configured |
| T9 | Generator defect produces an invalid plan | Bug in the search | Unusable cut plan | Independent validator on every returned plan; rejected plans recorded with codes, never shown | `PropertyTests.*`, `Random_corruptions_are_always_rejected` | Implemented |

## Not covered here

Authentication and authorisation of the product API, tenant isolation in the database, storage and downloads,
support access, audit trail, worker queue forgery, and backups: see handbook Parts II–III and VIII.
