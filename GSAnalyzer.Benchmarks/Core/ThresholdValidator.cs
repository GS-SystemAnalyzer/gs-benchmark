using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BenchmarkDotNet.Reports;
using GSAnalyzer.Benchmarks.Models;
using Microsoft.Extensions.Logging;

namespace GSAnalyzer.Benchmarks.Core
{
    public static class ThresholdValidator
    {
        public static bool EnforceLimits(IEnumerable<Summary> summaries, string configFilePath, ILogger logger)
        {
            if (!File.Exists(configFilePath))
            {
                logger.LogWarning("Config file '{ConfigFilePath}' not found. Skipping threshold validation.", configFilePath);
                return true;
            }

            string json = File.ReadAllText(configFilePath);
            var allThresholds = JsonSerializer.Deserialize<Dictionary<string, BenchmarkThresholds>>(json);

            bool isGlobalPass = true;

            foreach (var summary in summaries)
            {
                foreach (var report in summary.Reports)
                {
                    string testName = $"{summary.Title}.{report.BenchmarkCase.Descriptor.WorkloadMethod.Name}";

                    if (allThresholds != null && allThresholds.TryGetValue(testName, out var limits))
                    {
                        if (report.ResultStatistics == null)
                        {
                            logger.LogError("[CRASH] {TestName} failed to generate ResultStatistics. Did the benchmark crash?", testName);
                            isGlobalPass = false;
                            continue;
                        }

                        double meanMs = report.ResultStatistics.Mean / 1_000_000.0;
                        double errorMs = report.ResultStatistics.StandardError / 1_000_000.0;

                        long allocatedBytes = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0;

                        logger.LogInformation("Validating Thresholds for: {TestName}", testName);

                        if (meanMs > limits.MaxMeanMs)
                        {
                            logger.LogError(" [SPEED FAIL] Mean: {MeanMs:F3} ms > Limit: {LimitMs} ms", meanMs, limits.MaxMeanMs);
                            isGlobalPass = false;
                        }
                        else logger.LogInformation("  ✅ [SPEED PASS] Mean: {MeanMs:F3} ms", meanMs);

                        if (allocatedBytes > limits.MaxAllocatedBytes)
                        {
                            logger.LogError(" [MEMORY FAIL] Bytes: {AllocatedBytes} > Limit: {LimitBytes}", allocatedBytes, limits.MaxAllocatedBytes);
                            isGlobalPass = false;
                        }
                        else logger.LogInformation("  ✅ [MEMORY PASS] Bytes: {AllocatedBytes}", allocatedBytes);
                    }
                }
            }

            return isGlobalPass;
        }
    }
}