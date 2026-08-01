# Release commands

Worked commands for [SKILL.md](SKILL.md). Every version below is an example — substitute the real
one. `X.Y.Z` is whatever `<VersionPrefix>` says.

## 1. Check the starting state

```bash
grep -o '<VersionPrefix>[^<]*' Directory.Build.props
git fetch origin && git log --oneline origin/main -3
git tag -l 'v*'
gh run list --branch main --limit 3
```

The commit you intend to tag must already be on `origin/main` with a green CI run.

## 2. Bump the version

Edit `<VersionPrefix>` in `Directory.Build.props`, then confirm nothing else pinned the old one:

```bash
git grep -n "0\.1\.1" -- '*.yml' '*.props' '*.csproj' '*.json'
```

Anything that matches outside `Directory.Build.props` and `CHANGELOG.md` is a hardcoded version
that must be derived instead. The pattern both workflows use:

```bash
PREFIX=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props | head -1)
```

## 3. Update the CHANGELOG

Move the shipped items out of `[Unreleased]` into a new `## [X.Y.Z]` section. As of 0.1.2 the
CHANGELOG had no entries for 0.1.0, 0.1.1 or 0.1.2 — the step was missed on every release so far,
so check the file rather than assuming the previous release set a precedent.

## 4. Push to main

In a worktree, `git checkout main` fails when another worktree holds it. Push explicitly:

```bash
git push origin HEAD:main
```

Then wait for **both** workflows. This poll exits as soon as nothing is running:

```bash
for i in $(seq 1 40); do
  out=$(gh run list --branch main --limit 2 --json status,conclusion,name \
        --template '{{range .}}{{.name}}={{.status}}/{{.conclusion}} {{end}}')
  case "$out" in *in_progress*|*queued*) sleep 20;; *) echo "$out"; break;; esac
done
```

This push publishes a `X.Y.Z-beta.<run>` prerelease to GitHub Packages only. It exercises build,
test, pack, the allowlist and the GitHub Packages push — everything the tag will do except the
nuget.org step. If it is not green, the tag will not be either.

## 5. Tag

**Confirm with the user before this command.** It is the irreversible step.

```bash
git tag -a v0.1.2 <commit> -m "Alberto 0.1.2

<short summary>"
git push origin v0.1.2
```

The workflow fails the run if `v0.1.2` disagrees with `VersionPrefix`, before anything is pushed.

## 6. Watch the run, and confirm the nuget.org steps were not skipped

```bash
RUN=$(gh run list --workflow "Publish NuGet Packages" --limit 1 --json databaseId -q '.[0].databaseId')
gh run view "$RUN" --json jobs -q '.jobs[].steps[] | "\(.conclusion)\t\(.name)"'
```

`Log in to nuget.org` and `Push to nuget.org` must both be `success`. If they are `skipped`, the
run was not a tag build — the version gate saw `release=false` — and nothing reached nuget.org
despite the green tick.

## 7. Verify what shipped

### No warnings, and exactly the expected IDs

```bash
LOG=$(gh run view "$RUN" --log)
echo "$LOG" | grep "Push to nuget.org" | grep -i "warn"          # must print nothing
echo "$LOG" | grep "Push to nuget.org" | grep -oE "Pushing [A-Za-z.]+\.[0-9.]+\.nupkg" | sort -u
```

Ten IDs, none of them `Alberto.Admin*` or `Alberto.Cli`.

### Indexing

Use a JSON parser. A loose grep for a version string matches `xml version="1.0"` inside an error
page and reports false success — this has happened here.

```bash
python3 - <<'PY'
import json, urllib.request, time
IDS = ["alberto","alberto.commands","alberto.entityframework","alberto.inmemory",
       "alberto.messaging","alberto.messaging.postgres","alberto.postgres",
       "alberto.telemetry","alberto.testing","alberto.testing.xunit"]
VERSION = "0.1.2"
def versions(pid):
    url = f"https://api.nuget.org/v3-flatcontainer/{pid}/index.json"
    try:
        with urllib.request.urlopen(url, timeout=20) as r:
            return json.load(r)["versions"]
    except Exception as e:
        return ["ERR: %s" % e]
for attempt in range(12):
    missing = [p for p in IDS if VERSION not in versions(p)]
    if not missing:
        print("all 10 indexed at", VERSION); break
    print(f"attempt {attempt+1}: waiting on {len(missing)}")
    time.sleep(25)
else:
    print("STILL MISSING:", missing)
PY
```

Indexing lags the push by roughly three to five minutes.

### The published artifact

The only check that catches metadata the build never claimed to produce:

```bash
curl -sSL -o alberto.nupkg https://api.nuget.org/v3-flatcontainer/alberto/0.1.2/alberto.0.1.2.nupkg
unzip -o -q alberto.nupkg -d x
grep -oE "<(readme|icon)>[^<]*" x/Alberto.nuspec
grep -oE '<repository[^/]*/?>' x/Alberto.nuspec
ls x | grep -iE "readme|icon"
head -5 x/README.md
```

Expect `<readme>README.md`, `<icon>icon.png`, and a `<repository>` element carrying the tagged
commit SHA. If `<readme>` or `<icon>` is absent while the file itself is present in the package,
that is the `Directory.Build.props` evaluation-order bug — see SKILL.md.

Confirm the shipped icon is the current one rather than a stale render:

```bash
magick compare -metric AE x/icon.png icon.png null:   # 0 means identical
```

## 8. GitHub release

```bash
gh release create v0.1.2 --title "Alberto 0.1.2" --notes "$(cat <<'MD'
<notes>
MD
)"
```

Match the existing tone: state whether there are API or behaviour changes in the first line, then
explain *why* each change was needed, not only what changed. `gh release view v0.1.1 --json body`
is the reference.

## Regenerating the icon

`icon.svg` is the source; `icon.png` is what ships. Nothing in the build renders it.

```bash
rsvg-convert -w 256 -h 256 icon.svg -o icon.png
```

Check legibility at the smallest size nuget.org renders before committing:

```bash
rsvg-convert -w 32 -h 32 icon.svg -o /tmp/icon-32.png
```

Colours and styling come from CurioStack's tokens in
`apps/CurioStack.Web/client/src/index.css` of the `curiostack` repo: paper `#FAF8F3`, ink `#090706`,
`--logo-yellow: #F3CD00`, `border-2`, `rounded-base` (6px), and hard offset shadows with zero blur.

## Recovering from a bad release

There is no way to replace or delete a published version. Fix forward:

1. Correct the cause on `main` and let CI go green.
2. Bump the patch version in `Directory.Build.props`.
3. Release again from step 3.

Unlisting the bad version is optional — it hides the version from search and from the package page's
default view, but anyone who pinned it still restores it. It is done from the package's *Manage*
page on nuget.org and cannot be done from CI.
