# Releasing

How a change gets from a pull request to a version on nuget.org, and how the version it lands in
is decided.

All ten packages share one `<VersionPrefix>` in `Directory.Build.props` and are released in
lockstep. There is no per-package versioning.

## The short version

| I want to… | Do this |
|---|---|
| Get a change into the next release | Open an issue, give it a milestone, link the pull request with `Closes #123` |
| Know which version my change ships in | Read the milestone on the issue |
| Ship a breaking change | Label the pull request `breaking-change`, target a major milestone |
| Cut a release | Run **Release** with `dry_run` on, read it, run it again with `dry_run` off |
| Publish it | Merge the release pull request, then tag the merge commit |
| Patch an older line | Cut `release/X.Y`, fix on `main`, label the pull request `backport/X.Y` |

## Deciding the version: milestones

**The milestone on the issue decides which version the change ships in.** Not the pull request,
not the merge order, not whatever `main` happens to be at.

That is the whole point. A change is scheduled into a version when it is triaged, which means a
fix can be aimed at `1.0.2` while `main` is already working on `1.1.0`. This is why the project
does not fix forward.

Milestones are named for the exact version they ship: `1.1.0`, `1.0.2`, `2.0.0`. Not `v1.1.0`,
not `Q3`, not `Next`. `check-pr-policy.sh` enforces the shape.

`Backlog` is the one standing exception: a real milestone that is not a version, holding issues
nobody has scheduled yet. A pull request cannot merge against a `Backlog` issue — scheduling it
into a version is the act of deciding to ship it.

### Which slot

Standard semver, with the 0.y.z clause while the project is pre-1.0:

| | 1.0 and later | pre-1.0 (`0.y.z`) |
|---|---|---|
| Breaking change | major — `2.0.0` | minor — `0.2.0` |
| New API, backwards-compatible | minor — `1.1.0` | minor — `0.2.0` |
| Fix, no API change | patch — `1.0.1` | patch — `0.1.2` |

Pre-1.0 there is no major slot to increment, so breaks and additions share the minor slot. That
is not sloppiness, it is what `0.y.z` means: no stability guarantee yet. Going to `1.0.0` is
therefore always a deliberate act — the automation will never choose it for you.

### `breaking-change`

One label, on the pull request. It means: **a consumer who upgrades has to change their code.**

Removing or renaming a public member, changing a signature, changing behaviour someone could
reasonably have depended on, tightening a database constraint, changing a migration's meaning.

When you apply it, the pull request description must say what breaks and what to do about it.
That text is what gets folded into the changelog and, for a major, into the migration guide —
nobody will reconstruct it later.

`check-pr-policy.sh` fails a `breaking-change` pull request aimed at a milestone whose number
does not warn anyone: a patch, or a minor at 1.0 and later.

### Public API

The packable projects use `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Adding a public member
fails the build until it is declared in that project's `PublicAPI.Unshipped.txt`, so `Unshipped`
cannot be quietly stale — which is what makes it usable as a signal.

- Non-empty `Unshipped` → the release is at least a minor.
- `Shipped` is only ever written by the release workflow. Do not promote entries by hand.

`Alberto.Admin` and `Alberto.Admin.Postgres` are parked and excluded — see `.github/packages.txt`
and the admin-surface note in `CLAUDE.md`.

## Cutting a release

### 1. Dry run

Run the **Release** workflow from `main` (or from a `release/**` branch, for a patch on an older
line). Leave `dry_run` checked — it is checked by default.

- `bump: auto` derives the version: any `breaking-change` pull request since the last tag → major,
  else non-empty `Unshipped` → minor, else patch.
- `bump: major|minor|patch` forces the slot.
- `version:` sets it outright and overrides `bump`. **This is how you cut `1.0.0`** — `auto` will
  not leave the 0.x line on its own.

The run prints the version it computed and the changelog section it drafted, and writes nothing.
Read both. If the version is wrong, the milestones are wrong; fix them and run again.

### 2. Real run

Same workflow, `dry_run` off. It opens a pull request on `release-prep/vX.Y.Z` that:

- bumps `<VersionPrefix>`;
- renames `[Unreleased]` in `CHANGELOG.md` to the new version, inserts a fresh empty
  `[Unreleased]`, and fixes the compare links;
- promotes each packable project's `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`.

### 3. Edit the notes

**This is the one editing pass a release gets, and it is not optional.** The drafted entries are
issue titles, which are written to be found, not to be read. Rewrite them into something a
consumer can act on, and fold in the migration steps from every `breaking-change` pull request.

### 4. Merge, then tag

Merge the release pull request. Then tag the merge commit:

```bash
git switch main && git pull && git tag v1.1.0 && git push origin v1.1.0
```

The tag is what `publish-packages.yml` watches. It verifies the tag against `<VersionPrefix>` and
fails if they disagree, packs the ten packages, checks the packed set against its allowlist, and
pushes to nuget.org via trusted publishing.

Tagging is deliberately a human step and deliberately last. A push to nuget.org can be unlisted
but never deleted, and the package ID is never reclaimable.

### 5. GitHub release

Create the GitHub release from the tag and paste in the changelog section.

## Patching an older line

### Cut the branch

Run **Cut release branch** with the line, e.g. `1.0`, to create `release/1.0` from `main`. Cut it
while `main` is still on that line — the workflow refuses otherwise, because a branch cut after
`main` has moved on would compute versions from the wrong line.

Cut a line when you intend to keep patching it: normally right after its first release, or just
before merging something that would break it.

### Fix on `main` first, then backport

**Nothing is fixed directly on a release branch.** A fix that lands only on `release/1.0` is a
regression scheduled for the next release, because `main` never learned about it.

So: open the fix against `main` as usual, with its issue and milestone. Add a `backport/1.0`
label. When it merges, **Backport** cherry-picks it onto `release/1.0` and opens a pull request
there. Adding the label to an already-merged pull request works too, which is the common case —
the decision to patch an older release is usually made after the fix is in.

If the cherry-pick conflicts, the workflow says so on the original pull request with the commands
to do it by hand. It does not guess.

### Release the line

Run **Release** from `release/1.0`. It computes `1.0.2` from that branch's `VersionPrefix`,
opens the release pull request against that branch, and you tag from there.

Positions, versions and history on a release branch are its own. Do not merge a release branch
back into `main`.

## What the automation will not do

- **Choose `1.0.0`.** Leaving the 0.x line is a decision, not a computation.
- **Write your release notes.** It drafts from issue titles; that is a starting point.
- **Tag.** The last step before a permanent nuget.org push is a person.
- **Resolve a backport conflict.**
- **Promote public API on a branch that is not being released.**

## Repository configuration

The policy above is only real because the repository enforces it. What is set, and why:

**Merging.** Squash only — merge commits and rebase merges are off. Every change is one commit on
`main` whose subject ends `(#123)`, which is what `compute-version.sh` reads to find the pull
requests in a release and what `backport.yml` cherry-picks. `squash_merge_commit_title` is pinned
to `PR_TITLE` for the same reason: the default would use the commit subject instead whenever a
pull request had exactly one commit, dropping the `(#123)`.

**`main` and `release/**`.** Both require a pull request, one approving review, code-owner
approval, resolved review threads, and re-approval after a push. Force-pushes and deletion are
blocked. `main` additionally requires branches to be up to date before merging, and requires both
the `build-test` and `pr-policy` checks.

**Actions.** Only GitHub-owned, verified-creator, and `NuGet/login@*` actions may run. The default
`GITHUB_TOKEN` is read-only; each workflow requests the writes it needs and nothing more. Workflow
runs on pull requests from forks require a maintainer to approve them first — no fork can run CI
on this repository unprompted. Actions are not SHA-pinned; the allowlist above is what stands in
for it, and pinning would have to be weighed against the upgrade churn.

**Secrets.**

| Secret | Used by | Required? |
|---|---|---|
| `NUGET_USER` | `publish-packages.yml` | Yes, to publish. The nuget.org profile name, not an email |
| `RELEASE_TOKEN` | `release.yml`, `backport.yml` | Optional, but see below |

There is no nuget.org API key anywhere. Publishing uses trusted publishing (OIDC): the workflow
mints a token that nuget.org exchanges for a key valid for one hour. The policy on nuget.org is
bound to this repository **and to the workflow filename** — renaming `publish-packages.yml` breaks
publishing until the policy is edited to match.

`RELEASE_TOKEN` is a personal access token or GitHub App token with `repo` scope. It is optional
but strongly wanted: a pull request opened with the built-in `GITHUB_TOKEN` does not trigger
workflow runs, so without it the release and backport pull requests arrive with no CI on them and
cannot satisfy the required checks. The workaround, if you would rather not hold a token, is to
close and reopen each generated pull request by hand.

## Files

| | |
|---|---|
| `.github/packages.txt` | The ten packages that ship. Scripts read this, never a `src/*` glob |
| `.github/scripts/compute-version.sh` | Works out the next version |
| `.github/scripts/draft-release-notes.sh` | Drafts a changelog section from a milestone |
| `.github/scripts/apply-release.py` | Version bump, changelog cut, public-API promotion |
| `.github/scripts/check-pr-policy.sh` | Issue link, milestone, `breaking-change` agreement |
| `.github/workflows/release.yml` | Opens the release pull request |
| `.github/workflows/cut-release-branch.yml` | Creates `release/X.Y` |
| `.github/workflows/backport.yml` | Cherry-picks a merged change onto a release branch |
| `.github/workflows/publish-packages.yml` | Packs and pushes, on a tag |

`apply-release.py` and `check-pr-policy.sh` both run locally against a real pull request or a
dirty tree, which is the intended way to find out what a release would do:

```bash
.github/scripts/check-pr-policy.sh codest-be/alberto 123
```
