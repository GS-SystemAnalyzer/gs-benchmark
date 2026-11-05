using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSAnalyzer.Benchmarks.Core;

namespace GSAnalyzer.Benchmarks.Suites.Processing;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class CpuLoadBenchmarks
{
    private readonly EngineIteration iteration = new();
    [Params(1, 8, 64)] public int Count { get; set; }
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup] public void Prepare() => iteration.Begin(nameof(ProjectCpu), Count);
    [Benchmark] public object ProjectCpu() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class NativeCpuBenchmarks
{
    private readonly EngineIteration iteration = new();
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup] public void Prepare() => iteration.Begin(nameof(ReadNativeCpu));
    [Benchmark] public object ReadNativeCpu() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}
