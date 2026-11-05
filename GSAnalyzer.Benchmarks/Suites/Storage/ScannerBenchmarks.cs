using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSAnalyzer.Benchmarks.Core;

namespace GSAnalyzer.Benchmarks.Suites.Storage;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class ScannerBenchmarks
{
    private StorageFixture _fixture = null!;
    private readonly StorageWorkload workload = new();

    [Params("tiny-v1", "tiny-unicode-v1")]
    public string Dataset { get; set; } = "tiny-v1";

    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup(Target = nameof(AppEmpty))]
    public void EmptySetup() => _fixture = workload.Begin(nameof(AppEmpty), "app_empty", Dataset);
    [IterationSetup(Target = nameof(AppHit))]
    public void HitSetup() => _fixture = workload.Begin(nameof(AppHit), "app_hit", Dataset);
    [IterationSetup(Target = nameof(ChangedRescan))]
    public void ChangedSetup() => _fixture = workload.Begin(nameof(ChangedRescan), "changed_rescan", Dataset);
    [IterationSetup(Target = nameof(PersistedRestore))]
    public void RestoreSetup() => _fixture = workload.Begin(nameof(PersistedRestore), "persisted_restore", Dataset);

    [Benchmark] public Task AppEmpty() => workload.Run();
    [Benchmark] public Task AppHit() => workload.Run();
    [Benchmark] public Task ChangedRescan() => workload.Run();
    [Benchmark] public Task PersistedRestore() => workload.Run();
    [IterationCleanup] public void Record() => workload.Finish();
    [GlobalCleanup] public void Cleanup() => workload.Cleanup();
}
