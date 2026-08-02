<!--
Every pull request must close an issue. The issue's milestone decides which version this ships
in. See docs/releasing.md. If your issue is still in the `Backlog` milestone, ask for it to be
scheduled before opening this PR.
-->

Closes #

## What changed

<!-- What a reviewer needs to know to review it, not a restatement of the diff. -->

## Breaking change?

<!--
If this breaks a consumer, label the PR `breaking-change` and fill this in: what broke, and the
before/after a caller needs. This text becomes the release notes, so write it for the person doing
the upgrade. Delete this section if nothing breaks.
-->

## Checklist

- [ ] `dotnet test` passes locally (Postgres-backed tests need a running Docker daemon)
- [ ] Public API additions or removals are reflected in `PublicAPI.Unshipped.txt` / `PublicAPI.Shipped.txt`
- [ ] `breaking-change` label applied if a consumer breaks
- [ ] `CHANGELOG.md` **not** hand-edited; the release workflow drafts it
