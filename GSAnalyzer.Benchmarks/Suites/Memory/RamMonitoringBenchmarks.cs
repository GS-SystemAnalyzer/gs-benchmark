using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSAnalyzer.Benchmarks.Core;

namespace GSAnalyzer.Benchmarks.Suites.Memory;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class RamMonitoringBenchmarks
{
    private readonly EngineIteration iteration = new();
    [Params(10, 100, 1000)] public int Count { get; set; }
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup(Target = nameof(ParseSnapshot))] public void ParserSetup() => iteration.Begin(nameof(ParseSnapshot), Count);
    [IterationSetup(Target = nameof(ProjectSnapshot))] public void ProjectionSetup() => iteration.Begin(nameof(ProjectSnapshot), Count);
    [Benchmark] public object ParseSnapshot() => iteration.Run();
    [Benchmark] public object ProjectSnapshot() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class NativeRamBenchmarks
{
    private readonly EngineIteration iteration = new();
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup] public void Prepare() => iteration.Begin(nameof(ReadNativeProcesses));
    [Benchmark] public object ReadNativeProcesses() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}
