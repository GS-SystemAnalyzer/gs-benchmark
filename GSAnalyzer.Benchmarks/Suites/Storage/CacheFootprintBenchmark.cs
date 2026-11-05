using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSAnalyzer.Benchmarks.Core;
using GSSystemAnalyzer.Engine;
using GSSystemAnalyzer.Hubs;
using GSSystemAnalyzer.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GSAnalyzer.Benchmarks.Suites.Storage
{
    [MemoryDiagnoser]
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 8, invocationCount: 1)]
    public class CacheFootprintBenchmark
    {
        // Raised from 8_000_000 for issue #141: 8M was below this benchmark's own floor and
        // its ~600 KB noise exceeded the effect, so it failed on correct code. Smoke test only.
        private const long MaxResidualBytes = 12_000_000;

        private DiskScannerEngine _engine = null!;
        private FileTypeScanner _fileTypeScanner = null!;
        private AgeHeatmapEngine _ageHeatmap = null!;
        private string _testDirectory = null!;
        private List<FileSystemInfo> _testItems = null!;
        private List<string> _leafFolders = null!;
        private Guid _scanId;
        private long _idleManagedBytes;
        private StorageFixture _fixture = null!;
        private readonly string _launch = Guid.NewGuid().ToString("N");
        private string _method = "";
        private int _invocation;
        private long _afterManagedBytes;
        private double _elapsedMs;
        private int _populatedDirectories, _populatedFiles;

        [GlobalSetup]
        public void Setup()
        {
            BenchmarkResults.WriteChildProvenance();
            _fixture = new StorageFixture(StorageManifest.Legacy(footprint: true));
            try { SetupOwned(); }
            catch (Exception error)
            {
                try { Cleanup(); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        private void SetupOwned()
        {
            _testDirectory = _fixture.Workload;

            var dirInfo = new DirectoryInfo(_testDirectory);
            _testItems = dirInfo.GetFileSystemInfos().ToList();
            _leafFolders = Directory.GetDirectories(_testDirectory, "Sub_*", SearchOption.AllDirectories).ToList();
            _scanId = Guid.NewGuid();

            var mockClientProxy = new Mock<IClientProxy>();
            var mockClients = new Mock<IHubClients>();
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockHub = new Mock<IHubContext<SystemHub>>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

            var mockSettings = new Mock<ISettingService>();
            mockSettings.Setup(s => s.Current).Returns(new GSSystemAnalyzer.Models.SettingDtos.AppSettingDto
            {
                Scan = new GSSystemAnalyzer.Models.SettingDtos.ScanSettingDto
                {
                    Depth = 10,
                    ExcludedPaths = new List<string>()
                }
            });

            _engine = _fixture.Own(new DiskScannerEngine(mockHub.Object, mockSettings.Object, NullLogger<DiskScannerEngine>.Instance,
                cacheFilePath: _fixture.CachePath));
            _fileTypeScanner = new FileTypeScanner(_engine, _fixture.Own(new MemoryCache(new MemoryCacheOptions())));
            _ageHeatmap = new AgeHeatmapEngine(_engine, _fixture.Own(new MemoryCache(new MemoryCacheOptions())));

            // Idle baseline: tree exists, engine constructed, NO scan yet.
            _idleManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
        }

        // Fresh scan cache before every measured operation.
        [IterationSetup(Target = nameof(Footprint_FullStoragePipeline))]
        public void PipelineSetup() => ResetScanCache(nameof(Footprint_FullStoragePipeline));
        [IterationSetup(Target = nameof(ReturnToIdle_AfterClearCache))]
        public void ResidualSetup() => ResetScanCache(nameof(ReturnToIdle_AfterClearCache));

        private void ResetScanCache(string method)
        {
            _method = method;
            _invocation++;
            _populatedDirectories = _populatedFiles = 0;
            _engine.DirectorySizeCache.Clear();
        }

        [Benchmark]
        public async Task Footprint_FullStoragePipeline()
        {
            long start = Stopwatch.GetTimestamp();
            await Populate();

            // The three analyzers a Storage view triggers; each retains a snapshot
            // derived from the per-folder extension maps.
            _fileTypeScanner.Analyze(_testDirectory);
            _fileTypeScanner.GetExtensionBreakdown(_testDirectory);
            _ageHeatmap.Analyze(_testDirectory);
            _elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        [Benchmark]
        public async Task ReturnToIdle_AfterClearCache()
        {
            long start = Stopwatch.GetTimestamp();
            // Populate everything the way a real session does: full scan plus a
            // per-folder snapshot in each analyzer cache.
            await Populate();
            foreach (string folder in _leafFolders)
            {
                _fileTypeScanner.Analyze(folder);
                _fileTypeScanner.GetExtensionBreakdown(folder);
                _ageHeatmap.Analyze(folder);
            }

            // The operation under test.
            _engine.ClearCache();

            _afterManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
            long residual = _afterManagedBytes - _idleManagedBytes;
            _elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (residual > MaxResidualBytes)
            {
                throw new InvalidOperationException(
                    $"Issue #141 Part 1 regression: ClearCache() did not return to idle. " +
                    $"Residual managed heap {residual:N0} bytes > allowed {MaxResidualBytes:N0} bytes. " +
                    $"Check that DiskScannerEngine.ClearCache() still cancels its snapshot reset " +
                    $"token, and that FileTypeScanner/AgeHeatmapEngine still pass " +
                    $"_engine.SnapshotResetToken to AddExpirationToken on every cache Set. " +
                    $"Note this figure also carries scan-path allocations, so a rise here is not " +
                    $"proof on its own that the snapshot caches are the cause.");
            }
        }

        private async Task Populate()
        {
            _scanId = _engine.BeginScanSession();
            try { await _fixture.Run(async () => { await _engine.CalculateMissingSizesAsync(_testItems, _scanId); return true; }); }
            finally { _engine.EndScanSession(_scanId); }
            _populatedDirectories = _engine.DirectorySizeCache.Count;
            _populatedFiles = _engine.DirectorySizeCache.Values.Sum(e => e.Extensions?.Values.Sum(x => x.Count) ?? 0);
        }

        [IterationCleanup]
        public void Record()
        {
            var expected = StorageWorkload.LegacyExpected();
            var actual = new Dictionary<string, string> { ["populated_directories"] = _populatedDirectories.ToString(),
                ["populated_files"] = _populatedFiles.ToString() };
            bool residual = _method == nameof(ReturnToIdle_AfterClearCache);
            StorageWorkload.Emit(new StorageInvocation { RunId = Environment.GetEnvironmentVariable("GS_BENCH_RUN_ID") ?? "standalone",
                LaunchId = _launch, ProcessId = Environment.ProcessId, Method = _method, Invocation = _invocation,
                Root = _fixture.Root, CachePath = _fixture.CachePath, Manifest = _fixture.Manifest,
                Scenario = residual ? "legacy-clear" : "legacy-pipeline", Expected = expected, Actual = actual,
                Differences = StorageOracle.Compare(expected, actual), Before = new() { ["os_cache"] = StorageWorkload.OsCache },
                After = new() { ["directory_entries"] = _engine.DirectorySizeCache.Count.ToString() },
                StagesMs = new() { [residual ? "legacy-clear" : "legacy-pipeline"] = _elapsedMs },
                BaselineBytes = residual ? _idleManagedBytes : null, AfterBytes = residual ? _afterManagedBytes : null, Cleaned = false });
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _fixture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (_fixture != null) Console.WriteLine(StorageWorkload.CleanupPrefix + System.Text.Json.JsonSerializer.Serialize(_fixture.Root));
        }
    }
}
