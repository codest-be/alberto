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
| Cut a release | Run **Release** with the milestone, `dry_run` on; read it; run it again with `dry_run` off |
| Publish it | Merge the release pull request, then tag the merge commit |
| Patch an older line | Cut `release/X.Y`, fix on `main`, label the pull request `backport/X.Y` |

## Deciding the version: milestones

**The milestone on the issue decides which version the change ships in.** Not the pull request,
not the merge order, not whatever `main` happens to be at.

That is the whole point. A change is scheduled into a version when it is triaged, which means a
fix can be aimed at `1.0.2` while `main` is already working on `1.1.0`. This is why the project
does not fix forward.

It is also literally true rather than a convention: the milestone is the input to the release, its
title *is* the version number, and its closed issues are the notes. Nothing derives a version from
anything else. Releasing several changes together is therefore not a separate act: putting them
on one milestone is the act.

Milestones are named for the exact version they ship: `1.1.0`, `1.0.2`, `2.0.0`. Not `v1.1.0`,
not `Q3`, not `Next`. `check-pr-policy.sh` enforces the shape.

`Backlog` is the one standing exception: a real milestone that is not a version, holding issues
nobody has scheduled yet. A pull request cannot merge against a `Backlog` issue: scheduling it
into a version is the act of deciding to ship it.

### Which slot

Standard semver, with the 0.y.z clause while the project is pre-1.0:

| | 1.0 and later | pre-1.0 (`0.y.z`) |
|---|---|---|
| Breaking change | major (`2.0.0`) | minor (`0.2.0`) |
| New API, backwards-compatible | minor (`1.1.0`) | minor (`0.2.0`) |
| Fix, no API change | patch (`1.0.1`) | patch (`0.1.2`) |

Pre-1.0 there is no major slot to increment, so breaks and additions share the minor slot. That
is not sloppiness, it is what `0.y.z` means: no stability guarantee yet. Going to `1.0.0` is a
deliberate act, and it looks like one: you make the `1.0.0` milestone and release it.

### `breaking-change`

One label, on the pull request. It means: **a consumer who upgrades has to change their code.**

Removing or renaming a public member, changing a signature, changing behaviour someone could
reasonably have depended on, tightening a database constraint, changing a migration's meaning.

When you apply it, the pull request description must say what breaks and what to do about it.
That text is what gets folded into the changelog and, for a major, into the migration guide:
nobody will reconstruct it later.

`check-pr-policy.sh` fails a `breaking-change` pull request aimed at a milestone whose number
does not warn anyone: a patch, or a minor at 1.0 and later.

### Public API

The packable projects use `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Adding a public member
fails the build until it is declared in that project's `PublicAPI.Unshipped.txt`, so `Unshipped`
cannot be quietly stale, which is what makes it usable as a signal.

- Non-empty `Unshipped` → the release is at least a minor. `resolve-version.sh` refuses a patch
  milestone while any packable project has unshipped entries, which is the only automated check on
  the additive half of semver. `check-pr-policy.sh` reconciles `breaking-change` against the
  milestone, but nothing else reads the analyzer.
- `Shipped` is only ever written by the release workflow. Do not promote entries by hand.

`Alberto.Admin` and `Alberto.Admin.Postgres` are parked and excluded. See `.github/packages.txt`
and the admin-surface note in `CLAUDE.md`.

### Adding a package

Two edits, and the second one is not redundant. Add the ID to `.github/packages.txt` (which the
release scripts and every build and pack loop read) **and** to the literal allowlist in the
"Verify packed set" step of `publish-packages.yml`. That step is the only gate between an
accidental `IsPackable=true` and a nuget.org listing that can be unlisted but never deleted or
reclaimed, and it only works because it states the shipping set independently of the thing that
produces it. Edit only `packages.txt` and the publish job fails on the diff rather than pushing.

## Cutting a release

### 1. Dry run

Run the **Release** workflow from `main` (or from a `release/**` branch, for a patch on an older
line), and give it the `milestone` to release. Leave `dry_run` checked. It is checked by default.

The milestone's title is the version. `1.0.0` needs no special handling: make the milestone, put
the issues on it, release it.

`version:` is the escape hatch for a release that has no milestone: a one-off with nothing
triaged into it. It skips every check below, so reach for it rarely.

Before doing anything, the run rejects a release that is wrong on its face:

| Refused | Why |
|---|---|
| Milestone does not exist, or is not an `X.Y.Z` | There is nothing to take a version or notes from |
| Milestone has no closed issues | Nothing to release |
| Milestone is not ahead of the current `VersionPrefix` | It would move the version backwards or republish one. Usually means the wrong branch |
| Unshipped public API aimed at a patch | Added API is at least a minor |

An open issue left on the milestone is a warning rather than a refusal: punting unfinished work
to the next milestone mid-release is normal, and only closed issues become notes.

The run then prints the version and the drafted section, and writes nothing. Read both.

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

### 5. Verify

The GitHub release writes itself. `github-release.yml` fires on the same tag and creates it from
that version's `CHANGELOG.md` section, with a **Full changelog** compare link. There is nothing to
paste. The notes you edited on the release pull request are the notes on the releases page, which
is what stops the two from drifting. It fails loudly if the section is missing rather than
publishing an empty release, and it never overwrites a release that already exists.

To backfill an older tag, or replace a release deleted by mistake, run it from
`workflow_dispatch` with the tag.

Verifying the packages is still yours. A green publish run means the commands exited zero, not that the package is correct. `0.1.0`
shipped with no README on any of its ten nuget.org pages and every check was green. Check the push
step for warnings, confirm the versions indexed, and read the `.nuspec` back out of a downloaded
`.nupkg`. The `release-packages` skill in `.claude/skills/` has the commands and the traps this
repository has actually hit.

## Patching an older line

### Cut the branch

Run **Cut release branch** with the line, e.g. `1.0`, to create `release/1.0` from `main`. Cut it
while `main` is still on that line. The workflow refuses otherwise, because a branch cut after
`main` has moved on would compute versions from the wrong line.

Cut a line when you intend to keep patching it: normally right after its first release, or just
before merging something that would break it.

### Fix on `main` first, then backport

**Nothing is fixed directly on a release branch.** A fix that lands only on `release/1.0` is a
regression scheduled for the next release, because `main` never learned about it.

So: open the fix against `main` as usual, with its issue and milestone. Add a `backport/1.0`
label. When it merges, **Backport** cherry-picks it onto `release/1.0` and opens a pull request
there. Adding the label to an already-merged pull request works too, which is the common case.
The decision to patch an older release is usually made after the fix is in.

If the cherry-pick conflicts, the workflow says so on the original pull request with the commands
to do it by hand. It does not guess.

### Release the line

Run **Release** from `release/1.0` with the `1.0.2` milestone. It opens the release pull request
against that branch, and you tag from there. The branch's own `VersionPrefix` is what the
milestone is checked against, which is what stops a `1.1.x` milestone being released from a `1.0`
branch by accident.

Positions, versions and history on a release branch are its own. Do not merge a release branch
back into `main`.

## What the automation will not do

- **Choose the version.** It reads the milestone you hand it. Nothing infers a version from
  labels, commits or history. There is one source, and you set it during triage.
- **Write your release notes.** It drafts from issue titles; that is a starting point.
- **Tag.** The last step before a permanent nuget.org push is a person.
- **Resolve a backport conflict.**
- **Promote public API on a branch that is not being released.**

## Repository configuration

The policy above is only real because the repository enforces it. What is set, and why:

**Merging.** Squash only: merge commits and rebase merges are off. One commit per change is what
makes `backport.yml` able to cherry-pick a merged pull request onto a release line as a single
`-x` pick. `squash_merge_commit_title` is pinned to `PR_TITLE` so every subject ends `(#123)`;
that is now a readability convention rather than something a script parses, since the release
tooling reads milestones and `backport.yml` resolves the commit through the API.

**`main` and `release/**`.** Both require a pull request with resolved review threads. Direct
pushes, force-pushes and deletion are blocked. `main` additionally requires branches to be up to
date before merging, and requires both the `build-test` and `pr-policy` checks.

Neither requires an approving review, which is a deliberate choice for a single-maintainer
repository rather than an oversight. GitHub does not let anyone approve their own pull request, so
a required approval would be unsatisfiable on the maintainer's own work, and since a ruleset
bypass is all-or-nothing, working around it would also skip `build-test` and `pr-policy`. A
requirement that has to be bypassed to merge anything protects nothing and costs the two checks
that do. Nobody without write access can merge regardless, so the gate that matters is still
there.

**Add the review requirement back the day a second maintainer joins**: set
`required_approving_review_count` to `1` and `require_code_owner_review` and
`require_last_push_approval` to `true` on both rulesets. `.github/CODEOWNERS` is already written
for it and, in the meantime, still auto-requests review on the paths it names.

**Actions.** Only GitHub-owned, verified-creator, and `NuGet/login@*` actions may run. The default
`GITHUB_TOKEN` is read-only; each workflow requests the writes it needs and nothing more. Workflow
runs on pull requests from forks require a maintainer to approve them first, so no fork can run CI
on this repository unprompted. Actions are not SHA-pinned; the allowlist above is what stands in
for it, and pinning would have to be weighed against the upgrade churn.

**Secrets.**

| Secret | Used by | Required? |
|---|---|---|
| `NUGET_USER` | `publish-packages.yml` | Yes, to publish. The nuget.org profile name, not an email |
| `RELEASE_TOKEN` | `release.yml`, `backport.yml` | Optional, but see below |

There is no nuget.org API key anywhere. Publishing uses trusted publishing (OIDC): the workflow
mints a token that nuget.org exchanges for a key valid for one hour. The policy on nuget.org is
bound to this repository **and to the workflow filename.** Renaming `publish-packages.yml` breaks
publishing until the policy is edited to match.

`RELEASE_TOKEN` exists because a pull request opened with the built-in `GITHUB_TOKEN` does not
trigger workflow runs. GitHub suppresses them to stop workflows recursing. Without it the
release and backport pull requests arrive with no CI on them and cannot satisfy the required
checks. The workaround, if you would rather not hold a token, is to close and reopen each
generated pull request by hand.

Use a **fine-grained** token scoped to this repository alone, not a classic `repo`-scope one,
which would carry write access to every repository the owner can reach:

| Permission | Access | Needed for |
|---|---|---|
| Contents | Read and write | Pushing `release-prep/**` and `backport/**` branches |
| Pull requests | Read and write | Opening the pull request, labelling it, commenting on a failed backport |
| Metadata | Read | Mandatory on every fine-grained token |

Nothing else. In particular it needs no `workflow` permission; neither workflow edits any file
under `.github/workflows`.

The repository is org-owned, so a fine-grained token has to be approved by a `codest-be` owner
before it works, and it expires. Put the expiry in the calendar; the failure mode is a release
that stops opening its pull request months from now.

Store it without the value passing through a shell history or a terminal buffer:

```bash
gh secret set RELEASE_TOKEN --repo codest-be/alberto
```

## Files

| | |
|---|---|
| `.github/packages.txt` | The ten packages that ship. Scripts and workflows read this, never a `src/*` glob |
| `.github/scripts/resolve-version.sh` | Turns the milestone into the version, and refuses a wrong one |
| `.github/scripts/draft-release-notes.sh` | Drafts a changelog section from a milestone |
| `.github/scripts/apply-release.py` | Version bump, changelog cut, public-API promotion |
| `.github/scripts/extract-changelog-section.py` | One version's section, as a release body |
| `.github/scripts/check-pr-policy.sh` | Issue link, milestone, `breaking-change` agreement |
| `.github/workflows/release.yml` | Opens the release pull request |
| `.github/workflows/github-release.yml` | Creates the GitHub release, on a tag |
| `.github/workflows/cut-release-branch.yml` | Creates `release/X.Y` |
| `.github/workflows/backport.yml` | Cherry-picks a merged change onto a release branch |
| `.github/workflows/publish-packages.yml` | Packs and pushes, on a tag |

`apply-release.py` and `check-pr-policy.sh` both run locally against a real pull request or a
dirty tree, which is the intended way to find out what a release would do:

```bash
.github/scripts/check-pr-policy.sh codest-be/alberto 123
```
