using BenchmarkDotNet.Running;
using GSAnalyzer.Benchmarks.Core;
using Microsoft.Extensions.Logging;

namespace GSAnalyzer.Benchmarks
{
    public class Program
    {
        public static int Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            var logger = loggerFactory.CreateLogger<Program>();
            var validatorLogger = loggerFactory.CreateLogger(typeof(ThresholdValidator));

            logger.LogInformation("Initializing GS Engine Benchmark Suite...");

            var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

            bool passed = ThresholdValidator.EnforceLimits(summaries, "thresholds.json", validatorLogger);

            if (!passed)
            {
                logger.LogCritical("CI Pipeline Blocked: One or more performance thresholds failed.");
                return 1;
            }

            logger.LogInformation("CI Pipeline Passed: All system metrics are within limits.");
            return 0;
        }
    }
}