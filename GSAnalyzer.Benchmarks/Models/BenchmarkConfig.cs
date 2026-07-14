namespace GSAnalyzer.Benchmarks.Models
{
    public class BenchmarkThresholds
    {
        public double MaxMeanMs { get; set; }
        public double MaxErrorMs { get; set; }
        public long MaxAllocatedBytes { get; set; }
    }
}