using BenchmarkDotNet.Running;

namespace GSAnalyzer.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            // This fires up the benchmarking engine
            var summary = BenchmarkRunner.Run<ScannerBenchmarks>();
        }
    }
}