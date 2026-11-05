using GSAnalyzer.Benchmarks.Core;
using GSSystemAnalyzer.Services;
using GSSystemAnalyzer.Engine;
using GSSystemAnalyzer.Hubs;
using GSSystemAnalyzer.Interfaces;
using GSSystemAnalyzer.Models;
using GSSystemAnalyzer.Models.SettingDtos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GSAnalyzer.Benchmarks.Tests;

public class TelemetryContractTests
{
    [Theory]
    [InlineData("ParseSnapshot", 10)]
    [InlineData("ParseSnapshot", 100)]
    [InlineData("ParseSnapshot", 1000)]
    [InlineData("ProjectSnapshot", 10)]
    [InlineData("ProjectSnapshot", 100)]
    [InlineData("ProjectSnapshot", 1000)]
    [InlineData("ProjectCpu", 1)]
    [InlineData("ProjectCpu", 8)]
    [InlineData("ProjectCpu", 64)]
    [InlineData("SampleNetwork", 1)]
    [InlineData("SampleNetwork", 8)]
    [InlineData("SampleNetwork", 32)]
    [InlineData("SampleDisk", 1)]
    [InlineData("SampleDisk", 4)]
    [InlineData("SampleDisk", 16)]
    public void MeasuredOutputsMatchIndependentReferences(string method, int count)
    {
        using var input = new EngineInput(method, count);
        var actual = EngineObservation.Capture(method, input.Run());
        var expected = EngineObservation.Expected(method, count, 0);
        var differences = EngineObservation.Compare(expected, actual);
        if (method == "SampleNetwork" && count == 32)
        {
            Assert.Equal(new[] { "adapter/13/utilisation", "adapter/19/utilisation", "adapter/29/utilisation" }, differences);
            Assert.Equal(new[] { "0.19", "0.29", "0.43" }, differences.Select(k => actual[k]));
            Assert.Equal(new[] { "0.2", "0.28", "0.44" }, differences.Select(k => expected[k]));
        }
        else Assert.Empty(differences);
        actual["count"] = "-1";
        Assert.Contains("count", EngineObservation.Compare(expected, actual));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(1800)]
    [InlineData(10000)]
    public void HistorySlicesAndWebSerializationHaveTheSameExactReference(int count)
    {
        foreach (int minutes in new[] { 5, 15, 30, 60 })
        foreach (string method in new[] { "QueryHistory", "SerializeHistory" })
        {
            using var input = new EngineInput(method, count, minutes);
            var actual = EngineObservation.Capture(method, input.Run());
            Assert.Empty(EngineObservation.Compare(EngineObservation.Expected(method, count, minutes), actual));
            if (method == "SerializeHistory") Assert.True(int.Parse(actual["entity_bytes"]) > 0);
        }
    }

    [Fact]
    public void CpuControlCoversSingleCoreRoundingNegativeDeltaAndUnavailableFrequency()
    {
        var dto = WindowsCpuProvider.Project(new[] { 12.34 }, 20, null, null, 7, 19, 101);
        Assert.Equal(12.3, dto.AverageLoad);
        Assert.Equal(-7.7, dto.Delta);
        Assert.Equal(0, dto.CurrentFrequencyGhz);
        Assert.Equal(new[] { 12.3 }, dto.CoreGroups["CORE 0-0"]);
        Assert.Equal((7, 19, 101), (dto.TotalProcesses, dto.TotalThreads, dto.TotalHandles));
        Assert.Equal(3.75, WindowsCpuProvider.Project(new[] { 50.0 }, 0, 3000, 125, 1, 2, 3).CurrentFrequencyGhz);
    }

    [Fact]
    public void NetworkResetRemovalUnavailableLinkAndFailureRetainTheirDistinctOutcomes()
    {
        using var input = new EngineInput("SampleNetwork", 8);
        var initial = input.Network!.SampleMetrics();
        Assert.Null(initial.Interfaces.Single(n => n.Id == "2").UtilisationPercent);
        input.Clock.Advance(TimeSpan.FromSeconds(2));
        input.Adapters = [new() { Id = "1", NetworkInterfaceType = System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            OperationalStatus = System.Net.NetworkInformation.OperationalStatus.Up, BytesReceived = 1, BytesSent = 99999 }];
        var reset = Assert.Single(input.Network.SampleMetrics().Interfaces);
        Assert.Equal((0.0, 0.0, 200L, 100L), (reset.RxBytesPerSec, reset.TxBytesPerSec, reset.SessionRxBytes, reset.SessionTxBytes));
        input.Adapters = [];
        Assert.Empty(input.Network.SampleMetrics().Interfaces);
        input.Adapters = EngineInput.NetworkFixture(1, true);
        Assert.Equal(0, Assert.Single(input.Network.SampleMetrics().Interfaces).SessionRxBytes);
        input.FailReader = true;
        Assert.Throws<IOException>(() => input.Network.SampleMetrics());
    }

    [Fact]
    public void DiskElapsedFallbackAndFailedReaderDoNotMasqueradeAsCumulativeNetworkCounters()
    {
        using var input = new EngineInput("SampleDisk", 1);
        Assert.Equal(200, Assert.Single(input.Disk!.SampleMetrics().Disks).SessionReadBytes);
        input.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(300, Assert.Single(input.Disk.SampleMetrics().Disks).SessionReadBytes);
        input.FailReader = true;
        Assert.Empty(input.Disk.SampleMetrics().Disks);
        input.FailReader = false;
        input.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(200, Assert.Single(input.Disk.SampleMetrics().Disks).SessionReadBytes);
    }

    [Fact]
    public async Task RamPidReuseRemovalResetAndFailedReaderPreserveTheBaselineContract()
    {
        var clock = new EngineClock();
        ProcessMemorySample[] samples = [new(42, "reference", 1, 0, 123, null, 456)];
        bool fail = false;
        var settings = new Mock<ISettingService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettingDto());
        await using var ram = new RamMonitoringEngine(Mock.Of<IHubContext<SystemHub>>(), settings.Object,
            Mock.Of<IProcessOwnerResolver>(), Mock.Of<ITelemetryHistoryBuffer>(), NullLogger<RamMonitoringEngine>.Instance,
            () => fail ? throw new IOException("controlled") : samples, () => null, clock);
        Assert.Equal(0, Assert.Single(ram.CaptureProcessSnapshot()).CpuPercent);
        clock.Advance(TimeSpan.FromSeconds(2));
        samples = [samples[0] with { CpuTimeTicks = Environment.ProcessorCount * TimeSpan.TicksPerSecond / 2 }];
        Assert.Equal(25, Assert.Single(ram.CaptureProcessSnapshot()).CpuPercent);
        clock.Advance(TimeSpan.FromSeconds(2));
        samples = [samples[0] with { CreationTimeTicks = 9999, CpuTimeTicks = long.MaxValue / 2 }];
        Assert.Equal(0, Assert.Single(ram.CaptureProcessSnapshot()).CpuPercent);
        samples = [samples[0] with { CpuTimeTicks = 0 }];
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Assert.Single(ram.CaptureProcessSnapshot()).CpuPercent);
        fail = true;
        Assert.Throws<IOException>(() => ram.CaptureProcessSnapshot());
        fail = false;
        samples = [];
        Assert.Empty(ram.CaptureProcessSnapshot());
        samples = [new(42, "returned", 9999, 100000000, 123, null, 456)];
        Assert.Equal(0, Assert.Single(ram.CaptureProcessSnapshot()).CpuPercent);
    }

    [Fact]
    public async Task HistoryInclusiveCutoffPruningCapUnknownAndConcurrentAppendAreBounded()
    {
        var clock = new EngineClock();
        var history = new TelemetryHistoryBuffer(clock);
        history.Record("cpu", 1);
        clock.Advance(TimeSpan.FromMinutes(60));
        Assert.Single(history.GetHistory("cpu", 60)!.Points);
        clock.Advance(TimeSpan.FromTicks(1));
        history.Record("cpu", 2);
        Assert.Equal(2, Assert.Single(history.GetHistory("cpu", 60)!.Points).Value);
        Assert.Null(history.GetHistory("unknown", 60));
        Assert.Empty(history.GetHistory("ram", 60)!.Points);
        for (int i = 0; i < 10001; i++) history.Record("cpu", i);
        var capped = history.GetHistory("cpu", 60)!;
        Assert.Equal(10000, capped.Points.Count);
        Assert.Equal(1, capped.Points[0].Value);
        Assert.Equal(10000, capped.Points[^1].Value);
        var writer = Task.Run(() => { for (int i = 0; i < 200; i++) history.Record("ram", i); });
        var reader = Task.Run(() => { for (int i = 0; i < 200; i++) Assert.InRange(history.GetHistory("ram", 60)!.Points.Count, 0, 200); });
        await Task.WhenAll(writer, reader);
        var final = history.GetHistory("ram", 60)!;
        Assert.Equal(200, final.Points.Count);
        Assert.Equal(99.5, final.Stats.Avg);
    }
}
