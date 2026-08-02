---
name: release-packages
description: Cut a release of the Alberto NuGet packages — bump VersionPrefix, tag, publish to nuget.org via trusted publishing, and verify what actually shipped. Use when asked to release, cut a release, ship a version, bump the version, publish to NuGet, or when a push to nuget.org needs checking or repairing afterwards.
---

# Releasing the Alberto packages

## The core rule

**A push to nuget.org is permanent.** A version can be unlisted but never deleted, its content
never replaced, and the package ID never reclaimed by anyone. There is no undo, so every check
belongs *before* the tag. After the tag, the only remedy is another version.

The tag is the only irreversible step. Everything up to it — commits, main pushes, prerelease
builds — is cheap and reversible, so do all the work there and hold the tag until the release is
actually ready. **Pushing to nuget.org is publishing: get explicit confirmation before tagging.**

## What is being released

Ten packages, and only these ten:

```
Alberto  Alberto.Commands  Alberto.EntityFramework  Alberto.InMemory  Alberto.Messaging
Alberto.Messaging.Postgres  Alberto.Postgres  Alberto.Telemetry  Alberto.Testing  Alberto.Testing.Xunit
```

**`Alberto.Admin`, `Alberto.Admin.Postgres` and `Alberto.Cli` must never reach nuget.org.** The
admin projects are `IsPackable=false` on purpose: shipping `IAdminReader`/`IAdminOperator` at 1.0
would freeze the abstraction under semver before the things that consume it exist (see CLAUDE.md,
"Admin surface"). The workflow's *Verify packed set* step is the enforcement — it diffs the packed
IDs against a literal allowlist and fails on a difference in **either** direction, so a package
that appears unexpectedly and one that silently stops building are both caught.

Never widen that allowlist to make a build pass. If it fires, the build is right.

## How the pipeline decides what to do

`Directory.Build.props`'s `<VersionPrefix>` is the single source of truth.

| Trigger | Result |
|---|---|
| Push to `main` | Prerelease `X.Y.Z-beta.<run>` → GitHub Packages only |
| Tag `v*` | Release `X.Y.Z` → GitHub Packages **and** nuget.org |
| `workflow_dispatch` | Prerelease with a suffix you choose |

The nuget.org steps are gated on `steps.version.outputs.release == 'true'`, which only a tag sets.
A tag whose version disagrees with `VersionPrefix` fails the run before anything is pushed — that
check is a feature, not an obstacle to route around.

## Workflow

**The version bump, the changelog section and the public-API promotion are not done by hand.**
The **Release** workflow does all three and opens a pull request with them. Which version it
picks is decided by the milestones on the issues — [docs/releasing.md](../../../docs/releasing.md)
is the reference for that half, and this skill is the reference for everything from the tag
onward.

1. **Land the work on `main` first and let it go green.** Never tag a commit CI has not passed.
2. **Run **Release** with `dry_run` on.** It prints the version it computed and the changelog
   section it drafted, and writes nothing. If the version is wrong, the milestones are wrong.
3. **Run it again with `dry_run` off,** then edit the drafted notes on the pull request it opens.
   That editing pass is the only one a release gets. Do not hand-edit `CHANGELOG.md` or
   `<VersionPrefix>` outside that pull request.
4. **Merge it, and wait for CI *and* the publish workflow to finish green.** The prerelease that
   merge produces is a free rehearsal of the exact steps the tag will run.
5. **Confirm with the user, then tag.** Annotated, on the verified commit, `v` + the exact
   `VersionPrefix`.
6. **Watch the publish run and check the nuget.org steps ran rather than being skipped.** A green
   run proves nothing on its own — if `release` was `false`, the push steps are skipped and the run
   is still green.
7. **Verify what shipped** (below). Then write the GitHub release.

Exact commands for each step: [REFERENCE.md](REFERENCE.md)

## Verify, do not assume

A green run means the commands exited zero. It does not mean the package is correct — 0.1.0 shipped
with no README on any of its ten pages and every run was green.

Check three things after any release:

- **Zero warnings in the push step.** `warn : Readme missing` is how the 0.1.0 bug announced
  itself. Treat any warning there as a defect to fix in the next version.
- **The packages indexed.** Parse the flat-container `index.json` with a real JSON parser. Indexing
  lags the push by roughly three to five minutes, so poll. Do **not** grep the response for a
  version string: an error page contains `xml version="1.0"`, which a loose pattern matches, and
  that has already produced a false success report here.
- **The published artifact.** Download the `.nupkg` back from nuget.org and look inside the
  `.nuspec` for `<readme>`, `<icon>` and a `<repository>` commit. This is the only check that
  catches metadata the build never claimed to produce.

## Traps

**Packable-only properties belong in `Directory.Build.targets`, never in `Directory.Build.props`.**
MSBuild evaluates every property in import order before it evaluates any item. A
`PropertyGroup Condition="'$(IsPackable)' == 'true'"` in `Directory.Build.props` is tested while
`IsPackable` is still the repo-wide `false` — the csproj body that flips it has not been read yet —
so the group is **silently dead**. Items are different: an `ItemGroup` with the identical condition
in the same file works, because items are evaluated in a later pass. This shipped 0.1.0 with no
README and degraded SourceLink. Both files carry comments saying so; do not undo them.

**Hardcoded versions outside `Directory.Build.props`.** `ci.yml`'s pack smoke references a package
version, and a literal there turns the check into a guaranteed restore failure the moment the
version is bumped. It now derives `$PREFIX` by `sed`. Before any bump, grep the repo for the old
version and confirm nothing else pins it.

**Trusted publishing is bound to the workflow *filename*.** The nuget.org policy names this
repository and `publish-packages.yml`. Renaming or moving that file breaks publishing until the
policy is edited on nuget.org to match. No long-lived API key exists to fall back on — the OIDC
token is exchanged for a one-hour key at run time.

**`git checkout main` fails in a worktree** when another worktree holds it. Stay on the current
branch and push explicitly with `HEAD:main`.

**Regenerate `icon.png` after editing `icon.svg`.** The PNG is what ships; NuGet accepts only PNG
and JPEG. Nothing in the build renders it, so an edited SVG with a stale PNG publishes the old
icon silently. Also: an XML comment may not contain a double hyphen, so CSS custom-property names
like `--background` cannot be written literally in `icon.svg`.

**The README is the landing page for all ten packages,** so its links must be absolute GitHub
URLs. nuget.org has no repository to resolve a relative path against.

## After a bad release

You cannot replace the version. The only remedy is another version: correct the cause, release a
patch. Unlisting the bad version is optional and hides it from search but does not remove it —
anyone who pinned it still restores it.

That is the one place a release goes forward rather than back, and it is forced by nuget.org, not
chosen. It does not mean the project fixes forward: if the bad version is on an older line, the
fix still lands on `main` first and reaches the line through a `backport/X.Y` label. See
[docs/releasing.md](../../../docs/releasing.md).
