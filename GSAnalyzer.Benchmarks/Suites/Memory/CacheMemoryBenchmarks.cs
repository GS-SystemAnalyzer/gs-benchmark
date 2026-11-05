using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSAnalyzer.Benchmarks.Core;

namespace GSAnalyzer.Benchmarks.Suites.Memory
{
    [MemoryDiagnoser]
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
    public class CacheMemoryBenchmarks
    {
        private readonly StorageWorkload workload = new();
        [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
        [IterationSetup(Target = nameof(PopulationRebuild))]
        public void PopulateSetup() => workload.Begin(nameof(PopulationRebuild), "app_empty", "tiny-v1");
        [IterationSetup(Target = nameof(WarmedAnalyzerHit))]
        public void HitSetup() => workload.Begin(nameof(WarmedAnalyzerHit), "analyzer_hit", "tiny-v1");
        [IterationSetup(Target = nameof(ClearAction))]
        public void ClearSetup() => workload.Begin(nameof(ClearAction), "clear_action", "tiny-v1");
        [IterationSetup(Target = nameof(PassiveAfterProductGc))]
        public void PassiveSetup() => workload.Begin(nameof(PassiveAfterProductGc), "passive_after_product_gc", "tiny-v1");
        [IterationSetup(Target = nameof(ForcedGcRetention))]
        public void ForcedSetup() => workload.Begin(nameof(ForcedGcRetention), "forced_gc_retention", "tiny-v1");
        [Benchmark] public Task PopulationRebuild() => workload.Run();
        [Benchmark] public Task WarmedAnalyzerHit() => workload.Run();
        [Benchmark] public Task ClearAction() => workload.Run();
        [Benchmark] public Task PassiveAfterProductGc() => workload.Run();
        [Benchmark] public Task ForcedGcRetention() => workload.Run();
        [IterationCleanup] public void Record() => workload.Finish();
        [GlobalCleanup] public void Cleanup() => workload.Cleanup();
    }
}
