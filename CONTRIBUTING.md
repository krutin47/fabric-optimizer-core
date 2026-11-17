# Contributing

Read `AGENTS.md` first: it lists the rules that apply to every change.

## Branches

Feature branches open pull requests into `dev`; `dev` is promoted to `uat`, and `uat` to `master` (handbook §62,
§110). No direct pushes to those branches. A pull request needs the CI check (`ci / build-test`) to pass.

## Before opening a pull request

```bash
dotnet restore --locked-mode   # fails if a package version changed without updating packages.lock.json
dotnet build -c Release        # warnings are errors
dotnet test -c Release
```

If you add or update a package, run `dotnet restore` (without `--locked-mode`) and commit the changed
`packages.lock.json` files. Say in the pull request why the package is needed, its licence, and that you checked it
for known vulnerabilities.

## Changing the optimiser

- Every plan must still pass the independent validator; the test suite checks every returned plan.
- A change to the search: run the benchmark comparison described in `docs/testing/README.md` against the previous
  build and put the table in the pull request, including the jobs that got worse.
- A change to results or checks: bump `HybridCutOptimizer.SolverVersion` or `CutPlanValidator.Version`, and the
  package version in `src/DreamGlass.Optimizer/DreamGlass.Optimizer.csproj`.
- A change to how plans are ranked: that is a product decision. Update ADR-015 with the product owner's sign-off.
- New validator check: give it a stable code and add it to `docs/testing/validator-codes.md`.

## Requirements

Requirements carry ids (`DG-PROD-###`, `DG-SEC-###`, `DG-ACC-###`, `DG-OPS-###`). When a change implements or affects
one, update its row in `docs/requirements/ledger.csv`, including the test that covers it.
