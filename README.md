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

dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0-windows --no-build -- --mode discover --profile storage-tiny
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
| `storage-tiny` | performance | 8 owned cases: four application-cache states × ASCII/non-BMP datasets; numeric budgets TBD |
| `storage-stages` | performance | 5 owned production-topology cases: population, analyzer hit, clear, passive observation, forced-GC retention; numeric budgets TBD |
| `legacy-footprint` | contract | Preserved full pipeline and return-to-idle diagnostic; isolated 40,000-file fixture |

`legacy-scanner` and the `legacy-storage` union are retired. Their mixed-state
scanner measurement is replaced by the eight explicit `storage-tiny` identities.
Storage profiles require the `net10.0-windows` parent and a core revision with
the constructor's `cacheFilePath` argument (core PR263 or a compatible descendant).

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

## Owned storage workloads

Start with the tiny profiles before running the large legacy diagnostic:

```powershell
dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0-windows --no-build -- --mode report --profile storage-tiny --output <evidence-parent>
dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0-windows --no-build -- --mode report --profile storage-stages --output <evidence-parent>
dotnet run --project GSAnalyzer.Benchmarks -c Release -f net10.0-windows --no-build -- --mode report --profile legacy-footprint --output <evidence-parent>
```

Each fixture owns `gs-storage-<guid>` beneath the system temp directory. Its
random marker, workload, state/cache, output and prefix-sibling decoy are isolated.
The cache path is supplied before engine construction. Cleanup joins tracked
operations, disposes owners and revalidates marker/containment/link checks before
deleting. Failed or interrupted owners are preserved; there is no abandoned-root
sweep. `GS_STORAGE_OWNER` records creation/generation/verification phases;
invocation records and `GS_STORAGE_CLEANED` record successful cleanup. After an
interruption, retain the exact journaled root and logs until its workers have
settled. Fixture creation and inventory checks can take several minutes for
40,000 files; an outer timeout can interrupt setup before any measurement.

The manifest generator caps each tree at 50,000 files, 2,000 directories and
16,000,000 logical bytes. It generates metadata fixtures, not content-throughput
workloads. Every generated file, length and age is checked before timing.

The tiny manifest has **7 files / 4 directories / 90 B**. Its zero-length,
extensionless, root-direct and nested files exercise independent totals, file
types, extension breakdown and directory-age aggregation. The Unicode variant
includes `文档-🤡.txt`. Expected output comes from the manifest, independently of
production caches; persistence and fresh restore results are checked too.

| State | Timed work and preparation |
| --- | --- |
| `app_empty` | Service scan; fresh engine/cache/analyzers prepared outside timing |
| `app_hit` | Service rescan after verified seed; unavailable visited counts stay unavailable |
| `changed_rescan` | Change `A/a.txt` from 5 to 13 B and invalidate A before timing; expected total 98 B, B remains cached |
| `persisted_restore` | Fresh engine constructor reads/hydrates the real saved cache; collaborators prepared beforehand |

All identities explicitly say **`os_cache_uncontrolled`**. Fixture creation
warms filesystem metadata; an empty application cache is not a cold OS cache.
Production cases share a real `ScanCacheService` and one analyzer `MemoryCache`.
Seed discrepancies survive later phases. Analyzer-hit checks captured timed
returns and primed object identities; clear cases check directory/node/snapshot
eviction and persistence deletion after sampling.

Staged cases separate population, warmed analyzer hit and ClearCache wall time.
ClearCache itself forces GC. `PassiveAfterProductGc` waits 100 ms after that
action and samples without requesting another collection; it is **not natural
GC-free recovery**. `ForcedGcRetention` requests an additional collection.
Both signed residuals subtract the populated baseline; they are not app RAM,
working set or the legacy idle-baseline diagnostic.

The legacy return-to-idle case retains the 5×200×40 zero-byte/20-extension tree,
null node-cache service, separate analyzer caches, idle baseline and
populate→clear→forced-GC sequence. Its limit remains **12,000,000 B**. Random
owned path lengths qualify comparisons with historical measurements. The legacy
full-pipeline case retains mixed analyzer state and cannot certify rebuild speed.

The current tiny/staged runs detect product failures: omitted root-direct File
Types bytes (87/90 B), stale changed-subtree heatmap, and malformed restored
counts/totals (including 194 B/0 files in heatmap). They exit 1 even in report
mode. Detector-test passes establish rejection, not product acceptance; affected
cases cannot qualify a performance baseline until their correctness passes.

The retained corrected legacy run also exits 1: return-to-idle throws during
warmup at **14,749,360 B > 12,000,000 B**. No completed residual sample exists;
the raw exception is evidence, not a normalized measurement. The pipeline passes
its population/ownership contract only, not independent product correctness.
Earlier interrupted runs and their exact owned roots remain preserved. Local
BDN environment dumps/build binlogs can contain inherited credentials; use a
restricted child environment and review bundles before sharing them.

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

Metric units are `ns/op`, `B/op`, `ci999_half_width_ns_per_op`, `ms` for
`stage_duration/mean_elapsed_ms`, and `B` for `managed_residual/max_signed_delta`.
Only managed residual values/bounds may be negative. Allocation
has one diagnostic observation, not one allocation sample per timing iteration.
The child records its physical loaded core path/hash, framework/platform,
runtime, architecture and PID. Generated assemblies can differ from the parent
build; both identities are retained and the child's actual files are checked.
Source inputs are fingerprinted before and after collection.

Storage children emit `GS_STORAGE` records carrying run/method/launch/process/
invocation identities, the manifest, expected/actual differences, cache snapshots,
phase times and managed snapshots. Pinned Monitoring jobs use one launch,
one warmup, invocation/unroll 1, and 3 actual iterations (legacy: 8).
MemoryDiagnoser contributes a final extra-stats invocation without a timing line:
5 records per new case, 10 per legacy case. The adapter validates the entire
ordinal join. Stage means use actual samples only; residual maxima and correctness
checks include warmup and diagnostic records. Replay derives these values again
and rejects altered mappings, sample counts, output or normalized correctness.

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
