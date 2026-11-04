using BenchmarkDotNet.Attributes;
using GSAnalyzer.Benchmarks.Core;

namespace GSAnalyzer.Benchmarks.Suites.Diagnostics;

[MemoryDiagnoser]
public class HarnessSmokeBenchmarks
{
    [GlobalSetup]
    public void Setup() => BenchmarkResults.WriteChildProvenance();

    [Benchmark]
    public byte[] Allocate() => new byte[128];
}

[MemoryDiagnoser]
public class HarnessThrowBenchmarks
{
    [GlobalSetup]
    public void Setup() => BenchmarkResults.WriteChildProvenance();

    [Benchmark]
    public byte[] Throw() => throw new InvalidOperationException("Intentional harness crash control.");
}

[MemoryDiagnoser]
public class HarnessWindowsSmokeBenchmarks
{
    [GlobalSetup]
    public void Setup() => BenchmarkResults.WriteChildProvenance();

    [Benchmark]
    public byte[] Allocate() => new byte[128];
}
