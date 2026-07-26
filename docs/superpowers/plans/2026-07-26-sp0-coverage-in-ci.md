# SP0: Coverage collection in CI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make CI emit a code coverage report for every push and pull request, so the other sub-projects have a baseline to move.

**Architecture:** Add `coverlet.collector` to the one test project, switch the CI test step to `--collect:"XPlat Code Coverage"`, and upload the resulting Cobertura file as a build artifact. No thresholds are enforced — this sub-project measures, it does not gate.

**Tech Stack:** .NET 10.0, xUnit v3 3.2.2, coverlet.collector, GitHub Actions.

## Global Constraints

- Target framework is `net10.0`. Do not add a second TFM.
- All NuGet versions are centrally managed in `Directory.Packages.props`. A `PackageReference` in a `.csproj` carries **no** `Version` attribute; the version goes in `Directory.Packages.props` as a `PackageVersion`.
- The suite must stay green. Run `dotnet test` before every commit and never commit red.
- PostgreSQL-backed tests use Testcontainers and require a running Docker daemon locally.
- Branch for this sub-project: `sp0-coverage-in-ci`, off `main`.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `Directory.Packages.props` | Central version for `coverlet.collector` | Modify |
| `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj` | Reference the collector | Modify |
| `.github/workflows/ci.yml` | Collect and upload coverage | Modify |

Three files, one task. The change is not independently testable in pieces — a collector reference with no CI change measures nothing, and a CI change with no collector reference fails the build. They ship together.

---

### Task 1: Collect coverage in CI

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`
- Modify: `.github/workflows/ci.yml:32-33`

**Interfaces:**
- Consumes: nothing.
- Produces: a Cobertura XML file at `tests/Alberto.Dcb.Tests/TestResults/<guid>/coverage.cobertura.xml`, uploaded as the CI artifact named `coverage`. Later sub-projects read the line-coverage figure from this artifact to show movement.

- [ ] **Step 1: Create the branch**

```bash
git switch -c sp0-coverage-in-ci
```

- [ ] **Step 2: Add the central package version**

In `Directory.Packages.props`, add this line immediately after the `Testcontainers.PostgreSql` entry (currently line 59), keeping the existing alignment:

```xml
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
```

- [ ] **Step 3: Reference the collector from the test project**

In `tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj`, add this to the same `<ItemGroup>` that already holds `xunit.runner.visualstudio`:

```xml
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
```

The `PrivateAssets`/`IncludeAssets` pair is what stops the collector leaking into anything that references the test project. Note there is no `Version` attribute — central package management supplies it.

- [ ] **Step 4: Verify coverage is produced locally**

Run:

```bash
dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj --collect:"XPlat Code Coverage"
```

Expected: the run passes with `Passed: 909, Skipped: 2`, and the output ends with a line naming an `.xml` file, e.g.
`Attachments: /Users/.../tests/Alberto.Dcb.Tests/TestResults/<guid>/coverage.cobertura.xml`

If no attachment line appears, the collector reference did not take — recheck Step 3 before continuing.

- [ ] **Step 5: Record the baseline coverage figure**

Run:

```bash
find tests/Alberto.Dcb.Tests/TestResults -name 'coverage.cobertura.xml' -exec grep -o 'line-rate="[0-9.]*"' {} \; | head -1
```

Expected: a single line such as `line-rate="0.61"`. Write the value down — it goes into the commit message in Step 8, and it is the number SP1b, SP3 and SP5 are trying to move.

- [ ] **Step 6: Update the CI test step**

In `.github/workflows/ci.yml`, replace lines 30-33 with:

```yaml
      # Runs unit tests plus the Testcontainers-backed PostgreSQL integration
      # tests (Docker is available on ubuntu-latest runners). Coverage is
      # collected but not gated: the figure exists to show movement across the
      # test-suite remediation sub-projects, not to fail builds.
      - name: Test
        run: dotnet test tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj -c Release --no-build --logger "console;verbosity=normal" --collect:"XPlat Code Coverage"

      - name: Upload coverage
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: coverage
          path: tests/Alberto.Dcb.Tests/TestResults/**/coverage.cobertura.xml
          if-no-files-found: error
```

`if: always()` uploads coverage even when tests fail, which is when you most want to see what ran. `if-no-files-found: error` means a silently broken collector fails the build rather than uploading an empty artifact.

- [ ] **Step 7: Verify the workflow file parses**

Run:

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ci.yml parses')"
```

Expected: `ci.yml parses`

- [ ] **Step 8: Run the full suite and commit**

Run:

```bash
dotnet test
```

Expected: `Passed: 909, Skipped: 2`, exit code 0.

Then commit, substituting the figure from Step 5:

```bash
git add Directory.Packages.props tests/Alberto.Dcb.Tests/Alberto.Dcb.Tests.csproj .github/workflows/ci.yml
git commit -m "ci: collect code coverage

Adds coverlet.collector to the test project and uploads a Cobertura
report as a CI artifact. Not gated on a threshold -- the figure exists
so the test-suite remediation sub-projects can show movement.

Baseline line-rate at this commit: <VALUE FROM STEP 5>"
```

- [ ] **Step 9: Clean the local coverage output**

`TestResults/` accumulates a directory per run and should not be committed. Confirm it is ignored:

```bash
git status --porcelain tests/Alberto.Dcb.Tests/TestResults
```

Expected: no output. If paths are listed, add `TestResults/` to `.gitignore`, then commit:

```bash
git add .gitignore && git commit -m "chore: ignore TestResults output"
```

- [ ] **Step 10: Push and open the PR**

```bash
git push -u origin sp0-coverage-in-ci
```

Then open a PR titled `SP0: collect code coverage in CI`. Confirm on the PR that the `coverage` artifact appears in the run summary before merging.

---

## Self-Review

**Spec coverage.** The spec's SP0 entry asks for `coverlet.collector` plus `--collect:"XPlat Code Coverage"` in `.github/workflows/ci.yml`, and states it exists to give the other sub-projects a baseline. Task 1 does all three, and Step 5 captures the baseline figure explicitly rather than leaving it implied.

**Placeholder scan.** One deliberate substitution marker, `<VALUE FROM STEP 5>`, in the Step 8 commit message. It is filled from the command in Step 5, which is given in full. No other gaps.

**Type consistency.** No types are introduced. The artifact name `coverage` and the path glob `tests/Alberto.Dcb.Tests/TestResults/**/coverage.cobertura.xml` are used identically in Steps 5, 6 and 9.
