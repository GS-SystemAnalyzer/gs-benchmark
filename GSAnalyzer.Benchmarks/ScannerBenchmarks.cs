using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using GSSystemAnalyzer.Engine; 
using GSSystemAnalyzer.Hubs;       
using GSSystemAnalyzer.Interfaces; 
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GSAnalyzer.Benchmarks
{
    [MemoryDiagnoser] 
    public class ScannerBenchmarks
    {
        private DiskScannerEngine _scanner = null!;
        private string _testDirectory = null!;
        private List<FileSystemInfo> _testItems = null!;
        private Guid _scanId;

        [GlobalSetup]
        public void Setup()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "GSAnalyzer_Benchmark_CI");
            
            if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, true);
            Directory.CreateDirectory(_testDirectory);

            Console.WriteLine("Generating 50,000 physical files for CI runner sandbox...");

            int totalFolders = 500;
            int rootFolders = 5;
            int filesPerFolder = 100; // 500 folders * 100 files = 50,000 files
            int subFoldersPerRoot = totalFolders / rootFolders; // 100 subfolders per root

            // Parallel file creation for maximum CI runner speed
            Parallel.For(0, rootFolders, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, r =>
            {
                string rootPath = Path.Combine(_testDirectory, $"Root_{r}");
                Directory.CreateDirectory(rootPath);

                for (int s = 0; s < subFoldersPerRoot; s++)
                {
                    string subPath = Path.Combine(rootPath, $"Sub_{s}");
                    Directory.CreateDirectory(subPath);

                    for (int f = 0; f < filesPerFolder; f++)
                    {
                        // 0-byte file creation to conserve the runner's disk space
                        File.Create(Path.Combine(subPath, $"file_{f}.txt")).Dispose();
                    }
                }
            });

            var dirInfo = new DirectoryInfo(_testDirectory);
            _testItems = dirInfo.GetFileSystemInfos().ToList();
            _scanId = Guid.NewGuid();

            // --- Deep Mocking SignalR ---
            var mockClientProxy = new Mock<IClientProxy>(); 
            var mockClients = new Mock<IHubClients>();      
            mockClients.Setup(c => c.All).Returns(mockClientProxy.Object);

            var mockHub = new Mock<IHubContext<SystemHub>>(); 
            mockHub.Setup(h => h.Clients).Returns(mockClients.Object);

            // --- Mocking Settings ---
            var mockSettings = new Mock<ISettingService>();
            mockSettings.Setup(s => s.Current).Returns(new GSSystemAnalyzer.Models.SettingDtos.AppSettingDto 
            { 
                Scan = new GSSystemAnalyzer.Models.SettingDtos.ScanSettingDto 
                { 
                    Depth = 10, 
                    ExcludedPaths = new List<string>() 
                } 
            });

            var logger = NullLogger<DiskScannerEngine>.Instance;
            _scanner = new DiskScannerEngine(mockHub.Object, mockSettings.Object, logger);
        }

        [Benchmark]
        public async Task MeasureDiskScanner_CI()
        {
            await _scanner.CalculateMissingSizesAsync(_testItems, _scanId); 
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
    }
}