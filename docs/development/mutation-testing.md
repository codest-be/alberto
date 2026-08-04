# Mutation testing and coverage

Alberto measures its test suite two ways, and the two answer different questions.

**Coverage** answers *was this line executed*. It is cheap, it runs on every PR, and it is
good at exactly one thing: finding code no test reaches at all.

**Mutation testing** answers *would a test have noticed if this line were wrong*. Stryker
rewrites the source — flips a `<` to `<=`, empties a method body, inverts a boolean — rebuilds,
and re-runs the suite. A mutant that survives is a change to production behaviour that every
test still passed through. That is the number worth defending, because a line can be covered
by a test that asserts nothing about it.

Neither number is a target to maximise. 100% of either is not the goal and is not worth the
tests it would take to get there. The goal is that the important behaviour is pinned down, and
that a regression in it fails the build.

## Running them

```bash
build/coverage.sh
```

```bash
build/mutation-test.sh
```

One package only, which is what you usually want while iterating:

```bash
build/mutation-test.sh Alberto.Commands.csproj
```

Only what your branch changed — this is what CI runs on a PR, and it takes minutes rather
than hours:

```bash
build/mutation-test.sh --since main
```

**`--since` does not work from a linked git worktree, and the script refuses to run there.**
Stryker resolves the repository through LibGit2Sharp, which follows a worktree's `.git` file
back to the main checkout and diffs *that* working directory. Every changed path it finds is
rooted somewhere else on disk, nothing matches a file under the worktree, and all mutants are
dropped by the since filter — the run prints `0 total mutants will be tested` and exits zero.
Combined with `--allow-empty` that is a green gate over an unmeasured diff, which is worse
than no gate, so the script exits 2 instead. CI is unaffected: `actions/checkout` produces a
plain clone. A full sweep in a worktree is fine — only the diff filter is broken.

Reports land in `artifacts/`. `artifacts/stryker/<package>/reports/mutation-report.html` is
the one to open: it shows each surviving mutant in place in the source, which is usually
enough to see what assertion is missing.

## What is measured, and what is not

Mutation testing re-runs the test suite once for every mutant. That constraint decides the
scope of everything below.

| Package | Mutation tested | Why |
|---|---|---|
| `Alberto` | yes | Core. In-memory tests cover it. |
| `Alberto.Commands` | yes | Same. |
| `Alberto.InMemory` | yes | Same. |
| `Alberto.Messaging` | yes | Outbox/transport logic is backend-free. |
| `Alberto.Telemetry` | yes | Same. |
| `Alberto.Testing` | yes | Ships to consumers; its own correctness matters. |
| `Alberto.Postgres` | **no** | Behaviour lives behind Testcontainers. |
| `Alberto.Messaging.Postgres` | **no** | Same. |
| `Alberto.EntityFramework` | **no** | Same. |
| `Alberto.Admin*` | no | Parked, `IsPackable=false`. |
| `Alberto.Commands.Analyzers` | no | Roslyn analyzer; tested by compiling snippets. |
| `Alberto.Testing.Xunit` | no | Conformance specs — it *is* test code. |

The three Postgres/EF packages are the interesting exclusion. Their tests each need a
database, and Stryker would pay that cost once per mutant. Running them against the in-memory
suite instead would not report a low score, it would report a **fabricated** one: nearly every
mutant would survive for want of a database, and the number would say nothing about the tests
that actually exist. They are covered by the coverage gate instead, which runs the whole suite
including the integration tests.

That split is why the two gates use different test selections:

- **Coverage** runs everything, integration included. It runs the suite once, and the
  Postgres tests cost about ten seconds.
- **Mutation** runs `Category!=Integration` only. Every Postgres-backed test class carries
  `[Trait("Category", "Integration")]`.

`PostgresCluster` starts its container lazily, on the first request for a database, precisely
so a filtered run never touches Docker. Do not move that back into `InitializeAsync`: it is an
assembly fixture, so it is constructed on every run of the assembly including the thousands
that mutation testing performs.

## String mutants are off

`ignore-mutations: ["string"]` in `stryker-config.json`. String mutants land almost entirely in
log messages, exception text and SQL fragments; killing them means asserting on wording, which
freezes prose into tests and buys nothing. Everything structural — conditionals, arithmetic,
booleans, LINQ, block removal — is on.

## The gates

Three numbers, enforced in two workflows.

| Gate | Where | Threshold | Achieved | Scope |
|---|---|---|---|---|
| Line coverage | `ci.yml`, every PR | 93% | 95.27% | All shipped packages |
| Mutation score, changed code | `mutation.yml`, every PR | 80% | — | The diff only |
| Mutation score, aggregate | `mutation.yml`, nightly | 68% | 70.01% | Core packages |

A fourth number, `thresholds.break` in `stryker-config.json` (55%), is Stryker's own
per-package floor. It is not the gate — the script deliberately keeps going when a package
trips it, because one low package must not hide every score after it — but it makes a
single-package run exit non-zero, which is what you want while iterating on one package.

**Both mutation thresholds moved down sharply, and no test got worse.** They were calibrated
against an aggregate of 92.59% that counted false timeouts as kills. The same suite, measured
with a window long enough to tell a slow mutant from a hanging one, scores 70.01%. Nothing
regressed; the old number was wrong. See the next section for the measurement.

**The PR gate that matters is the middle one.** `--since` mutates only what the branch
touched, so it holds new code to a standard without asking anyone to fix the whole back
catalogue first. Fixing your own diff is a bounded job in a way that fixing the back
catalogue is not.

It sits *above* the repo aggregate rather than below it, which is the opposite of how this
pair used to be set. When the aggregate read 92.59% the diff gate sat a couple of points under
it, because a diff is a small sample and a small sample makes the percentage jumpy — ten
mutants with two missed reads as 80%. Against a real back catalogue of 70% that reasoning
inverts: the whole point of gating the diff is that new code should be better than what is
already there, and 80% does that while leaving room for one awkward mutant in a small change.
Setting it back to 90 would fail honest pull requests, and a gate people learn to re-run until
it passes is worse than no gate.

The two whole-repo thresholds trail the achieved score rather than lead it. That is on
purpose: a gate set above where the repo currently sits blocks every PR, including the ones
trying to fix it. Raise them as the score climbs — a few points at a time, once the headroom
is real.

**The aggregate runs nightly, not on push.** It used to run on every push to `main` and
`release/**`, which on a single `public-ci` runner meant a five-hour job standing between
everyone else's pull request and the machine — the 0.2.0 release pull request waited about two
hours for its checks that way. `workflow_dispatch` is there for when a particular commit needs
the whole number sooner.

## The timeout window, and why it decides the score

**Stryker counts a `Timeout` as a kill.** So does `build/mutation-summary.py` — the score is
`(killed + timeout) / (killed + timeout + survived + no-coverage)`. That is the right call for
a mutant that genuinely hangs: an infinite loop is a defect the tests noticed. It is the wrong
answer for a mutant that merely ran slowly, and the two are indistinguishable in the report.

For a long time this repo could not tell them apart, and the score paid for it. Measured on
`Alberto.Telemetry`, same code, same tests, only the per-mutant window changed:

| `additional-timeout` | Timeout | Killed | Survived | reported score |
|---|---|---|---|---|
| 5000 (the default) | 62 | 29 | 2 | **86.67%** |
| 120000 | **0** | 57 | 36 | **54.29%** |

Not one of those 62 was a hang. Thirty-four surviving mutants were hiding behind the window,
and the reported score was thirty-two points too high.

Two things make the default window too tight here:

- **The preview MTP runner does not attribute coverage per test.** It reports every covered
  mutant as covered by all 1890 tests — `coveredBy` in the report takes exactly two values, 0
  and 1890, never anything between. So a mutant no test kills has to run the entire suite
  before it can be called a survivor. Setting `coverage-analysis` does not help: `perTest` is
  already the default and produces an identical report. `test-runner: vstest` is worse — it
  captures no coverage at all, marking 103 of 195 mutants `Survived` for a 1.90% score.
- **Stryker runs mutants concurrently.** The suite takes about 15 seconds alone; seven copies
  of it sharing a machine take considerably longer, and the window is derived from the solo
  baseline.

`additional-timeout: 120000` in `stryker-config.json` is the fix. **Do not shrink it to make
the sweep faster** — that buys a fast number by making it a false one.

Granting a window that large is only safe because the suite no longer hangs. Every wait in it
is bounded, so a mutant that breaks wiring fails in about five seconds instead of sitting in
the window until it expires. Those two changes belong together: unbounded waits made a big
window ruinously expensive, and a small window made unbounded waits invisible. See
[#133](https://github.com/codest-be/alberto/issues/133).

**If you add a test that waits on a signal, bound the wait.** The bound is a failure detector,
not a budget — it should be far above what correct code needs and far below "forever". Five
seconds is the convention here; `Patience` and `StopBudget` in the test suite are the existing
names for it.

## Where the numbers stand

`Alberto.Postgres`, `Alberto.Messaging.Postgres` and `Alberto.EntityFramework` appear under
coverage only, for the reason given above.

The mutation column is the first sweep measured with a truthful timeout window. The column
beside it is what the same suite reported before that window was fixed, kept because it is the
number the gates and every earlier revision of this document were calibrated against — not
because anything regressed between the two. No test changed except to bound its own waits.

| package | mutation | as reported before | survivors | line coverage | branch |
|---|---|---|---|---|---|
| `Alberto` | 69.75% | 91.93% | 364 | 95.41% | 87.50% |
| `Alberto.Commands` | 79.78% | 98.10% | 16 | 98.77% | 90.00% |
| `Alberto.InMemory` | 75.37% | 93.48% | 51 | 94.20% | 95.00% |
| `Alberto.Messaging` | 76.52% | 97.73% | 28 | 97.95% | 88.64% |
| `Alberto.Telemetry` | 56.19% | 88.57% | 34 | 96.35% | 64.29% |
| `Alberto.Testing` | 64.98% | 92.90% | 78 | 93.21% | 86.59% |
| `Alberto.EntityFramework` | not measured | — | — | 95.17% | 74.07% |
| `Alberto.Messaging.Postgres` | not measured | — | — | 88.57% | 50.00% |
| `Alberto.Postgres` | not measured | — | — | 94.28% | 78.03% |
| **total** | **70.01%** | 92.59% | **571** | **95.27%** | **85.20%** |

2531 scored mutants: 1748 killed, 24 timed out, 571 survived, 188 never reached by a test.
This document used to claim that **none** survive and that every remaining miss was
`NoCoverage`. That claim was an artefact of the measurement — a mutant that survived and made
the suite slow was scored `Timeout`, and `Timeout` counts as a kill. 571 of them do survive:
places where a test runs the line and does not assert on what it did.

Timeouts are now 24 of 2531, 0.95%, down from between a quarter and a third. The one in
`Alberto.Telemetry` was checked by hand — `TelemetryBatchConsumeMiddleware.cs:57`, an equality
mutation in code that only sets tags and records metrics and has no loop to hang in. That is a
contention straggler on a machine running seven mutants at once, not a wait somebody forgot to
bound.

Where the misses are has not changed, only how many of them there are. They concentrate in the
async loop code — `ControlLoop`, `DeadLetterRetryLoop`, `LeaseAwareControlLoopGroup`,
`RebuildCoordinator` — which is the expensive end, because each needs a test that drives a real
polling loop through a timing window. `Alberto.Telemetry` scoring lowest of the six is the same
story read from the other side: it has the repo's highest line coverage at 96.35% and its
lowest branch coverage at 64.29%, which is the signature of tests that execute instrumentation
without asserting on what it emitted.

## A known Stryker flake

Stryker 4.16.0 has a race in its MTP runner. It throws a `NullReferenceException` out of
`MicrosoftTestPlatformRunnerPool.CaptureCoverage` before a single mutant is tested, then a
second one out of `Dispose()`, and takes that package's report with it — so the package
vanishes from the aggregate rather than scoring badly. It has hit a different package on each
sweep so far (`Alberto.Telemetry`, then `Alberto.Commands`), and re-running the same package
alone has succeeded every time with no change to anything.

`build/mutation-test.sh` therefore retries a package once when Stryker exits non-zero **and**
wrote no report. Those two conditions together are what distinguishes a crash from a genuine
below-threshold score — the score case writes a report, so it is never retried. If both
attempts crash, the script says so and exits non-zero, because an aggregate computed without
a package reads higher than the truth and must not pass for a clean run.

A PR that touches no mutated package produces no mutants at all. That is a pass, not a 0%,
which is what `--allow-empty` on the summary script is for.

## Reading a surviving mutant

Not every survivor is worth killing. Ask what the mutant proves:

- **A real gap** — the mutant changes behaviour a caller depends on and nothing failed. Add
  the assertion.
- **Equivalent** — the mutant produces a program that behaves identically (a redundant bounds
  check, a defensive branch that cannot be reached). No test can kill it. Leave it, or delete
  the dead code.
- **Not worth it** — killing it would mean asserting on a log string or a private
  implementation detail. Leave it.

The score is a prompt to look, not a quota. A package sitting at 75% with every survivor
triaged is in better shape than one at 90% padded with assertions on internals.

## Adding a package to the mutation set

Add it to `PACKAGES` in `build/mutation-test.sh`, run it once to see where it lands, and only
then decide the threshold. If the package needs a database to mean anything, it does not
belong in the set — cover it with integration tests and let the coverage gate hold it.
