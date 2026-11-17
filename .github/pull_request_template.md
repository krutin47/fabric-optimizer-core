## What this changes

<!-- One outcome. Link the requirement ids (DG-PROD-### …) it implements or affects. -->

## Evidence

<!-- Commands you ran and their real results (PASS / FAIL / NOT RUN, with counts). Optimiser search changes: the
     benchmark comparison table from docs/testing/README.md, including jobs that got worse. -->

## Checklist (handbook Appendix A4)

- [ ] One outcome, no unrelated changes.
- [ ] Every returned plan still passes the independent validator; the validator was not weakened.
- [ ] Quality wording stays honest: nothing says "optimal"; "proven" only with a completed exact search.
- [ ] Tests added or updated; they would fail without this change.
- [ ] Contract or result changes: version bumped and `CHANGELOG.md` updated.
- [ ] Ranking changes: ADR-015 updated with the product owner's sign-off.
- [ ] Packages: `packages.lock.json` updated; licence and known vulnerabilities checked.
- [ ] No secrets, customer data or real job data in code, fixtures, logs or screenshots.
- [ ] CI permissions and workflows unchanged, or the change is explained and approved.
- [ ] Playground UI changes: keyboard path, labels, contrast and status messages checked.
- [ ] Open risks and assumptions listed below.

## Open risks and assumptions
