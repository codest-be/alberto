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
| Mutation score, changed code | `mutation.yml`, every PR | 90% | — | The diff only |
| Mutation score, aggregate | `mutation.yml`, nightly | 90% | 92.59% | Core packages |

A fourth number, `thresholds.break` in `stryker-config.json` (80%), is Stryker's own
per-package floor. It is not the gate — the script deliberately keeps going when a package
trips it, because one low package must not hide every score after it — but it makes a
single-package run exit non-zero, which is what you want while iterating on one package.

**The PR gate that matters is the middle one.** `--since` mutates only what the branch
touched, so it holds new code to a standard without asking anyone to fix the whole back
catalogue first. Fixing your own diff is a bounded job in a way that fixing the back
catalogue is not.

It sits a couple of points *under* the repo aggregate rather than above it, which is not an
oversight. A diff is a small sample, and a small sample makes the percentage jumpy: ten
mutants with two missed reads as 80% and fails. Setting the diff gate above the aggregate
would mostly generate false failures on ten-line PRs, and a gate people learn to re-run
until it passes is worse than no gate. 90% catches a diff that is genuinely untested while
leaving room for one awkward mutant in a small change.

The two whole-repo thresholds trail the achieved score rather than lead it. That is on
purpose: a gate set above where the repo currently sits blocks every PR, including the ones
trying to fix it. Raise them as the score climbs — a few points at a time, once the headroom
is real.

**The aggregate runs nightly, not on push.** It used to run on every push to `main` and
`release/**`, which on a single `public-ci` runner meant a five-hour job standing between
everyone else's pull request and the machine — the 0.2.0 release pull request waited about two
hours for its checks that way. `workflow_dispatch` is there for when a particular commit needs
the whole number sooner.

The sweep is also slower than it should be. Between a quarter and a third of tested mutants in
every core package come back `Timeout`, and a timed-out mutant costs a full timeout window
rather than the milliseconds a killed one costs. They are not confined to the polling code you
would expect: `TelemetryBuilderExtensions.cs`, which does nothing but register services,
produced eight of them. That points at tests which hang when wiring is perturbed instead of
failing fast, so the fix is in the suite rather than in the budget. Until that is done the
nightly is allowed 360 minutes; do not raise it further to make a red run green.

## Where the numbers stand

`Alberto.Postgres`, `Alberto.Messaging.Postgres` and `Alberto.EntityFramework` appear under
coverage only, for the reason given above. The baseline column is the first full run, kept so
later movement means something.

| package | mutation | was | line coverage | was | branch |
|---|---|---|---|---|---|
| `Alberto` | 91.93% | 88.69% | 95.41% | 91.25% | 87.50% |
| `Alberto.Commands` | 98.10% | 74.29% | 98.77% | 83.54% | 90.00% |
| `Alberto.InMemory` | 93.48% | 93.48% | 94.20% | 94.20% | 95.00% |
| `Alberto.Messaging` | 97.73% | 92.42% | 97.95% | 95.49% | 88.64% |
| `Alberto.Telemetry` | 88.57% | 87.62% | 96.35% | 96.35% | 64.29% |
| `Alberto.Testing` | 92.90% | 92.35% | 93.21% | 93.21% | 86.59% |
| `Alberto.EntityFramework` | not measured | — | 95.17% | 95.17% | 74.07% |
| `Alberto.Messaging.Postgres` | not measured | — | 88.57% | 88.57% | 50.00% |
| `Alberto.Postgres` | not measured | — | 94.28% | 94.28% | 78.03% |
| **total** | **92.59%** | 88.95% | **95.27%** | 92.33% | **85.20%** |

The shape matters more than the number. Of 2415 scored mutants, **none survive**: every
remaining miss is `NoCoverage`, code no test reaches. There is currently no place in the core
packages where a test runs a line and fails to assert on it.

That is also why the last two rows moved on mutation while sitting still on coverage.
`Alberto.Telemetry` and `Alberto.Testing` each gained a point from a single new assertion on a
line that was already executed — the trace link's `event.position` tag, and `TestEvents`
keeping caller-supplied metadata instead of silently swapping in an empty dictionary. Coverage
could not have found either, because coverage already counted both lines as covered.

The 179 remaining misses are spread across 49 files with no file holding more than 16, and
they concentrate in the async loop code — `ControlLoop`, `DeadLetterRetryLoop`,
`LeaseAwareControlLoopGroup`, `RebuildCoordinator`. That is the expensive end: each needs a
test that drives a real polling loop through a timing window. The cheap wins are spent.

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
