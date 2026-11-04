# GS Analyzer benchmark companion

The harness distinguishes **discovery**, **observations**, and **acceptance**.
An exit-zero contract run proves the harness controls; it supplies no product
performance, correctness, or demo-readiness certification.

## Build and run

Requires .NET SDK **10.0.302**, pinned by `global.json`, and the matching core
source checkout. BenchmarkDotNet is pinned to **0.15.8**. In PowerShell:

```powershell
$env:CoreRepoPath = (Resolve-Path ../core).Path
dotnet build GSAnalyzer.Benchmarks/GSAnalyzer.Benchmarks.csproj -c Release --no-incremental
dotnet build GSAnalyzer.Benchmarks.Tests/GSAnalyzer.Benchmarks.Tests.csproj -c Release --no-incremental
dotnet test GSAnalyzer.Benchmarks.Tests/GSAnalyzer.Benchmarks.Tests.csproj -c Release --no-build

dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0 --no-build -- --mode discover --profile legacy-storage
dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0 --no-build -- --mode gate --profile selftest-pass
dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0-windows --no-build -- --mode gate --profile selftest-windows-pass
```

`CoreRepoPath` must be exported, not merely supplied with a parent `-p:` switch:
BenchmarkDotNet launches independent restore/build processes. `--core-repo`
can supply the same absolute path at execution time. No engine is constructed
by the synthetic profiles.

Required CLI options: `--mode discover|report|gate` and `--profile <id>`.
Optional: `--config <path>`, `--output <parent-directory>`, `--filter <glob>`,
`--source-project <absolute-csproj>`, `--core-repo <absolute-directory>`.
All paths resolve before collection changes directory. Default configuration
is next to the executable, never implicitly in the caller's current directory.
Each invocation creates a fresh `run-<guid>` directory. Preserve that entire
directory, including logs, component results and the manifest.

- `discover`: compares independently declared expectations with expanded
  methods, typed parameters and named jobs; executes no fixture setup.
- `report`: records valid observations; absent budgets are provisional.
  Configuration, execution, coverage and correctness failures still exit 1.
- `gate`: additionally applies approved inclusive limits. Required
  performance/release cases need independent product correctness and approved
  required budgets. Contract checks cannot promote `not_checked` correctness.

Report-only metrics and cases stay provisional even when within approved limits;
their metric decision is `warn`, not an acceptance `pass`.

A filter must retain the complete selected profile. Intentional subsets need
their own checked-in profile. New or deleted methods/parameters/jobs cannot
silently change expected coverage. Discovery supports declarative literal
parameters; executable sources and custom configuration attributes are rejected
before converter invocation. Only the memory diagnoser is supported here.

## Current coverage and deliberate migration

| Profile | Purpose | State |
| --- | --- | --- |
| `selftest-pass`, `selftest-windows-pass` | contract | Allocates a returned 128-byte array; validates nonzero allocation and actual child provenance |
| `selftest-throw` | contract | Intentional exception; must exit 1 |
| `selftest-allocation-fail` | contract | Intentional 1 B/op limit; must exit 1 |
| `legacy-scanner`, `legacy-footprint`, `legacy-storage` | contract | Discovery only; execution refuses until source-registered owned fixtures are available |

`--selftest-build-failure` intentionally gives the generated child an absent
core project. It is restricted to synthetic profiles and must exit 1.

Legacy mean/allocation limits retain their exact numeric values in canonical
units, marked **historical/provisional**. The orphan RAM `MeasureUsage` policy
was removed; it has no corresponding measured case. `MaxErrorMs` was never
enforced and is not migrated to a different statistic. Uncertainty uses BDN's
actual **half-width of the 99.9% confidence interval**, with budgets TBD.
Managed allocation/op is not retained memory or process working set.

The BDN/runtime migration breaks unqualified comparisons with older runs. The
synthetic lane has three deliberately short timing iterations; its timing
warnings and provisional values must not be read as stable performance results.

## Evidence schema v1

JSON uses snake_case and strict required properties. Unknown fields/schema,
duplicate properties/identities, invalid units/statistics, nonfinite numbers,
negative absolute measurements, missing evidence and inverted limits fail.
Missing values are null with a status/reason, never invented numeric zero.

- `manifest.json`: run/profile/purpose, UTC interval, core/harness Git SHA and
  dirty state, config/dataset hashes, actual toolchain/environment and inventory.
- `policy.json`: byte-for-byte selected configuration, including owners and
  approved/historical/TBD budgets.
- `coverage.json`: independent expected, discovered, launched, executed and
  measured sets, plus missing/extra cases and optional capability reasons.
- `case-results.json`: execution outcomes, named contract checks, independent
  correctness status/basis, and normalized metrics with relative raw links.
- `validation.json`, `scorecard.md`: gate outcome, failures and provisional
  observations.
- `raw/`: original full BDN JSON, measurements CSV and build/execution logs.
- `component-results/`: all retained launch results, child identity payloads,
  generated project/source/build scripts, SDK proof and available build binlogs.

Metric units are `ns/op`, `B/op`, and `ci999_half_width_ns_per_op`. Allocation
has one diagnostic observation, not one allocation sample per timing iteration.
The child records its physical loaded core path/hash, framework/platform,
runtime, architecture and PID. Generated assemblies can differ from the parent
build; both identities are retained and the child's actual files are checked.
Source inputs are fingerprinted before and after collection.

The inventory excludes the manifest itself. Store the printed manifest SHA256
in an external CI/baseline reference. Checksums establish consistency, not trust
in whoever supplied the evidence.

## Published runner and read-only revalidation

```powershell
dotnet publish GSAnalyzer.Benchmarks -c Release -f net10.0 --no-build -o <published-directory>
dotnet <published-directory>/GSAnalyzer.Benchmarks.dll --mode gate --profile selftest-pass --source-project <matching-source-csproj>
dotnet <published-directory>/GSAnalyzer.Benchmarks.dll --mode gate --profile selftest-pass --evidence <run-directory> --manifest-sha <external-sha256> --output <separate-directory>
```

Published collection needs the matching source checkout and Release assemblies.
Binary-only collection fails before setup. Read-only validation requires no
source checkout and writes a new validation result in a separate run directory.
Original evidence is never rewritten to make its checksums match.
An output equal to or beneath the evidence directory is rejected before writing.
Replay checks successful normalized cases against component run/case IDs,
generation/build outcomes, every retained launch and child identity, and the
recorded source/toolchain fingerprints. It checks copied build evidence without
requiring the original source or generated binaries to remain on disk.

## CI contracts

`.github/workflows/benchmark-contracts.yml` builds both target frameworks,
runs policy tests and the real executable/child matrix, retains artifacts on
failure, and has an always-running aggregate requiring every declared job to
succeed. Workflow-dispatch `aggregate_probe=failure|skipped` deliberately makes
that aggregate red. The negative rehearsal checks failure/cancelled/skipped/
missing states through the executable. A green synthetic contract lane supplies
no product coverage. The core repository's pinned benchmark caller is adopted
separately; merely adding this workflow does not update that caller or branch
protection.

### Discord completion summaries

`.github/workflows/benchmark-report.yml` sends one summary per completed
`Benchmark harness contracts` attempt from pull requests, pushes, or manual
dispatches. Set the repository Actions secret **`CI_BUILD`** to the Discord
webhook URL. Repository secrets in `core` are not shared with this repository.
The reporter must exist on the default branch (`main`) before GitHub activates
its `workflow_run` trigger; adding it to a PR alone does not send notifications.

Messages show the exact upstream conclusion, source repository/branch/commit,
actor, run/attempt, and run/artifact-page link. Manual run labels include
`aggregate probe=none|failure|skipped`, so intentional failures are identifiable.
Timeouts and other non-success conclusions retain their exact label and use
failure styling. These messages describe synthetic harness checks only.

The reporter uses event metadata and does not check out PR code or download
artifacts, including for fork PRs. A missing `CI_BUILD` produces a visible warning
and failed reporter job; transport errors also fail that separate job. The
benchmark aggregate and its acceptance result remain independent. Confirm the
message in the intended Discord channel after activation; local payload checks
and a successful action invocation alone do not establish channel delivery.

Local CLI tests retain their owned `gs-cli-contract-<guid>` directory under the
system temp directory and print its exact path in test output. All invoked
workloads are synthetic; no legacy storage fixture is executed.
