using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSAnalyzer.Benchmarks.Core;

namespace GSAnalyzer.Benchmarks.Suites.Processing;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class NetworkContractBenchmarks
{
    private readonly EngineIteration iteration = new();
    [Params(1, 8, 32)] public int Count { get; set; }
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup] public void Prepare() => iteration.Begin(nameof(SampleNetwork), Count);
    [Benchmark] public object SampleNetwork() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class DiskContractBenchmarks
{
    private readonly EngineIteration iteration = new();
    [Params(1, 4, 16)] public int Count { get; set; }
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup] public void Prepare() => iteration.Begin(nameof(SampleDisk), Count);
    [Benchmark] public object SampleDisk() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class HistoryContractBenchmarks
{
    private readonly EngineIteration iteration = new();
    [Params(0, 60, 1800, 10000)] public int Count { get; set; }
    [Params(5, 15, 30, 60)] public int Minutes { get; set; }
    [GlobalSetup] public void Setup() => BenchmarkResults.WriteChildProvenance();
    [IterationSetup(Target = nameof(QueryHistory))] public void QuerySetup() => iteration.Begin(nameof(QueryHistory), Count, Minutes);
    [IterationSetup(Target = nameof(SerializeHistory))] public void SerializationSetup() => iteration.Begin(nameof(SerializeHistory), Count, Minutes);
    [Benchmark] public object QueryHistory() => iteration.Run();
    [Benchmark] public object SerializeHistory() => iteration.Run();
    [IterationCleanup] public void Record() => iteration.Finish();
    [GlobalCleanup] public void Cleanup() => iteration.Cleanup();
}
