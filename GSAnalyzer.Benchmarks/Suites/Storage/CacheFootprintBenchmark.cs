using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using GSSystemAnalyzer.Engine;
using GSSystemAnalyzer.Hubs;
using GSSystemAnalyzer.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
​
namespace GSAnalyzer.Benchmarks.Suites.Storage
{
    [MemoryDiagnoser]
    [SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 8, invocationCount: 1)]
    public class CacheFootprintBenchmark
    {
        private const long MaxResidualBytes = 8_000_000; // 8 MB
​
        private static readonly string[] Extensions =
        {
            ".txt", ".log", ".mp4", ".mov", ".jpg", ".png", ".pdf", ".docx",
            ".xlsx", ".zip", ".rar", ".cs", ".js", ".ts", ".json", ".xml",
            ".dll", ".exe", ".iso", ".bin"
        };
​
        private DiskScannerEngine _engine = null!;
        private FileTypeScanner _fileTypeScanner = null!;
        private AgeHeatmapEngine _ageHeatmap = null!;
        private string _testDirectory = null!;
        private List<FileSystemInfo> _testItems = null!;
        private List<string> _leafFolders = null!;
        private Guid _scanId;
        private long _idleManagedBytes;
​
        [GlobalSetup]
        public void Setup()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "GSAnalyzer_Issue141_CI");
​
            if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, true);
            Directory.CreateDirectory(_testDirectory);
​
            const int rootFolders = 5;
            const int subFoldersPerRoot = 200;   // 1,000 leaf folders
            const int filesPerExtension = 2;     // 20 exts * 2 = 40 files/folder => 40,000 files
​
            // 0-byte files keep runner disk low; the footprint we measure is the
            // retained extension map + path strings, not the file bytes.
            Parallel.For(0, rootFolders, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
            {
                string rootPath = Path.Combine(_testDirectory, $"Root_{r}");
                Directory.CreateDirectory(rootPath);
​
                for (int s = 0; s < subFoldersPerRoot; s++)
                {
                    string subPath = Path.Combine(rootPath, $"Sub_{s}");
                    Directory.CreateDirectory(subPath);
​
                    foreach (string ext in Extensions)
                    {
                        for (int f = 0; f < filesPerExtension; f++)
                        {
                            // Long, unique names so each retained LargestFilePath
                            // string carries real weight.
                            string fileName = $"verbose_report_document_{r}_{s}_{f}{ext}";
                            File.Create(Path.Combine(subPath, fileName)).Dispose();
                        }
                    }
                }
            });
​
            var dirInfo = new DirectoryInfo(_testDirectory);
            _testItems = dirInfo.GetFileSystemInfos().ToList();
            _leafFolders = Directory.GetDirectories(_testDirectory, "Sub_*", SearchOption.AllDirectories).ToList();
            _scanId = Guid.NewGuid();
​
            var mockClientProxy = new Mock<IClientProxy>();
            var mockClients = new Mock<IHubClients>();
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);
​
            var mockHub = new Mock<IHubContext<SystemHub>>();
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
​
            var mockSettings = new Mock<ISettingService>();
            mockSettings.Setup(s => s.Current).Returns(new GSSystemAnalyzer.Models.SettingDtos.AppSettingDto
            {
                Scan = new GSSystemAnalyzer.Models.SettingDtos.ScanSettingDto
                {
                    Depth = 10,
                    ExcludedPaths = new List<string>()
                }
            });
​
            _engine = new DiskScannerEngine(mockHub.Object, mockSettings.Object, NullLogger.Instance);
            _fileTypeScanner = new FileTypeScanner(_engine, new MemoryCache(new MemoryCacheOptions()));
            _ageHeatmap = new AgeHeatmapEngine(_engine, new MemoryCache(new MemoryCacheOptions()));
​
            // Idle baseline: tree exists, engine constructed, NO scan yet.
            _idleManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
        }
​
        // Fresh scan cache before every measured operation.
        [IterationSetup]
        public void ResetScanCache()
        {
            _engine.DirectorySizeCache.Clear();
        }
​
        [Benchmark]
        public async Task Footprint_FullStoragePipeline()
        {
            await _engine.CalculateMissingSizesAsync(_testItems, _scanId);
​
            // The three analyzers a Storage view triggers; each retains a snapshot
            // derived from the per-folder extension maps.
            _fileTypeScanner.Analyze(_testDirectory);
            _fileTypeScanner.GetExtensionBreakdown(_testDirectory);
            _ageHeatmap.Analyze(_testDirectory);
        }
​
        [Benchmark]
        public async Task ReturnToIdle_AfterClearCache()
        {
            // Populate everything the way a real session does: full scan plus a
            // per-folder snapshot in each analyzer cache.
            await _engine.CalculateMissingSizesAsync(_testItems, _scanId);
            foreach (string folder in _leafFolders)
            {
                _fileTypeScanner.Analyze(folder);
                _fileTypeScanner.GetExtensionBreakdown(folder);
                _ageHeatmap.Analyze(folder);
            }
​
            // The operation under test.
            _engine.ClearCache();
​
            long residual = GC.GetTotalMemory(forceFullCollection: true) - _idleManagedBytes;
            if (residual > MaxResidualBytes)
            {
                throw new InvalidOperationException(
                    $"Issue #141 Part 1 regression: ClearCache() did not return to idle. " +
                    $"Residual managed heap {residual:N0} bytes > allowed {MaxResidualBytes:N0} bytes. " +
                    $"Snapshot caches (filetypes:/extbreakdown:/ageheatmap:) are not being evicted.");
            }
        }
​
        [GlobalCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
    }