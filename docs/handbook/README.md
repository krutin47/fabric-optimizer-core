# Engineering handbook

The authority for how DreamGlass is built is the **DreamGlass Production SaaS Engineering, Security, Product &
Accessible Design Handbook, edition 1.1 (14 November 2026)**. `AGENTS.md` is its short form for this repository.

## Why the handbook itself is not in this folder

The handbook is classified *internal engineering standards*, and this repository is public. Its text is therefore
kept outside the repository. To add it here (handbook §80 asks for a `docs/handbook/` edition), first make the
repository private, or have the owner confirm that the handbook may be published.

## How this repository uses it

| Handbook part | Where it lands here |
|---|---|
| §29–38, §95–98 optimiser, physical correctness, warehouse | `src/DreamGlass.Optimizer` (solver, validator, approval), tests |
| §32, §96 objective and honest quality indicators | ADR-010, ADR-015; `QualityStatus`, `ObjectiveBound` |
| §36, §47, §102, §104 printed cut plan and its text equivalent | `Printing/CutPlanDocument.cs`, `Printing/CutPlanSvgRenderer.cs` |
| §30, §95 coordinate frames and transforms | `Printing/SheetTransform.cs`, `Normalization/OrientationRules.cs` |
| §12, §25, §26 API errors, headers, input limits | playground `Program.cs`, `Contracts/Options.cs` (`OptimizerLimits`) |
| §65, §110 CI quality gates | `.github/workflows/ci.yml` |
| §80 required repository artifacts | `AGENTS.md`, `CONTRIBUTING.md`, `docs/`, PR template, `.editorconfig` |
| §87 requirements traceability | `docs/requirements/ledger.csv` |

Parts II–III (tenancy, database, Auth0, authorisation), V–VI (product screens, full WCAG audit) and VIII
(Azure deployment) apply to the host application, which is not in this repository. The ledger marks those
requirements as out of scope here rather than as met.
