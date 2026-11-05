using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Reports;
using GSAnalyzer.Benchmarks.Core;
using GSAnalyzer.Benchmarks.Models;
using GSAnalyzer.Benchmarks.Suites.Storage;

namespace GSAnalyzer.Benchmarks.Tests;

public sealed class StorageWorkloadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void ContainmentHandlesFilesystemRootsWithoutTouchingThem()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string parent = Path.Combine(root, "parent");
        Assert.True(StorageFixture.IsChild(root, parent));
        Assert.True(StorageFixture.IsChild(parent, Path.Combine(parent, "child")));
        Assert.False(StorageFixture.IsChild(parent, parent));
        Assert.False(StorageFixture.IsChild(parent, parent + "-sibling"));
        Assert.False(StorageFixture.IsChild(parent, Path.Combine(parent, "..", "escape")));
    }

    [Fact]
    public async Task TinyUnicodeInventoryAndPeerCleanupAreOwned()
    {
        await using var first = new StorageFixture(StorageManifest.Tiny(unicode: true));
        await using var peer = new StorageFixture(StorageManifest.Tiny());
        Assert.NotEqual(first.Root, peer.Root);
        first.VerifyInventory();
        Assert.Equal(7, first.Manifest.Files.Length);
        Assert.Equal(90, first.Manifest.Files.Sum(f => f.Bytes));
        Assert.False(StorageFixture.IsChild(first.Workload, first.State));
        Assert.False(StorageFixture.IsChild(first.Workload, first.Output));
        Assert.True(StorageFixture.IsChild(first.State, first.CachePath));
        await first.DisposeAsync();
        Assert.False(Directory.Exists(first.Root));
        peer.VerifyInventory();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongMarkerRefusesDeletion(bool missing)
    {
        var fixture = new StorageFixture(StorageManifest.Tiny());
        string token = File.ReadAllText(fixture.Marker);
        if (missing) File.Delete(fixture.Marker); else File.WriteAllText(fixture.Marker, "wrong");
        await Assert.ThrowsAsync<IOException>(() => fixture.DisposeAsync().AsTask());
        Assert.True(fixture.Preserved);
        Assert.True(File.Exists(fixture.Resolve("root.txt")));
        File.WriteAllText(fixture.Marker, token);
        fixture.ValidateOwnership();
        Directory.Delete(fixture.Root, true);
    }

    [Fact]
    public async Task CleanupAwaitsTheActualOperationBeforeReleasingOwners()
    {
        var fixture = new StorageFixture(StorageManifest.Tiny());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        fixture.Own(new CallbackDisposable(() => disposed = true));
        var operation = fixture.Run(async () => { await release.Task; File.WriteAllText(Path.Combine(fixture.State, "settled"), "yes"); return true; });
        Task cleanup = fixture.DisposeAsync().AsTask();
        try
        {
            Assert.False(cleanup.IsCompleted);
            Assert.False(disposed);
            Assert.True(Directory.Exists(fixture.Root));
        }
        finally { release.TrySetResult(true); await operation; await cleanup; }
        Assert.True(disposed);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task InvalidManifestIsRejectedBeforeCreatingAnyRoot()
    {
        await using var parent = new StorageFixture(StorageManifest.Tiny());
        string[] before = Directory.GetDirectories(parent.State).Order().ToArray();
        Assert.Throws<InvalidDataException>(() => new StorageFixture(new("escape", [""], [new("../outside", 1, 0)]), parent.State));
        Assert.Equal(before, Directory.GetDirectories(parent.State).Order());
    }

    [Fact]
    public async Task LinkIsRejectedWithoutTraversingOrDeletingItsTarget()
    {
        await using var peer = new StorageFixture(StorageManifest.Tiny());
        var fixture = new StorageFixture(StorageManifest.Tiny());
        string link = Path.Combine(fixture.Workload, "outside-link");
        try { Directory.CreateSymbolicLink(link, peer.Workload); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            output.WriteLine("Link capability not_supported: " + e.Message);
            await fixture.DisposeAsync();
            return;
        }
        try
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.DisposeAsync().AsTask());
            peer.VerifyInventory();
            Assert.True(fixture.Preserved);
        }
        finally
        {
            Directory.Delete(link);
            fixture.ValidateOwnership();
            Directory.Delete(fixture.Root, true);
        }
    }

    [Fact, Trait("Category", "StorageHost")]
    public async Task OwnedFixtureHost()
    {
        var fixture = new StorageFixture(StorageManifest.Tiny(unicode: true));
        output.WriteLine("Host start: " + DateTimeOffset.UtcNow.ToString("O"));
        output.WriteLine("Owned root: " + fixture.Root);
        try
        {
            var session = fixture.Own(new StorageSession(fixture));
            string target = (string)typeof(GSSystemAnalyzer.Engine.DiskScannerEngine)
                .GetField("_cacheFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Engine)!;
            Assert.Equal(fixture.CachePath, target);
            Assert.False(File.Exists(target));
            await session.Scan();
            Assert.True(File.Exists(target));
            fixture.VerifyInventory();
            await Task.Delay(2000); // Keep independent hosts overlapping for the ownership exercise.
        }
        finally { await fixture.DisposeAsync(); }
        Assert.False(Directory.Exists(fixture.Root));
        output.WriteLine("Host end: " + DateTimeOffset.UtcNow.ToString("O"));
    }

    private sealed class CallbackDisposable(Action action) : IDisposable { public void Dispose() => action(); }

    [Fact]
    public async Task ReentrantDisposalCannotMissTheStartingOperation()
    {
        var fixture = new StorageFixture(StorageManifest.Tiny());
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cleanup = null;
        var operation = fixture.Run(() => { cleanup = fixture.DisposeAsync().AsTask(); return release.Task; });
        try
        {
            Assert.NotNull(cleanup);
            Assert.False(cleanup.IsCompleted);
            Assert.True(Directory.Exists(fixture.Root));
        }
        finally { release.TrySetResult(true); await operation; await cleanup!; }
    }

    [Fact]
    public async Task ResourceDisposalCannotRegisterWorkAfterTheDrain()
    {
        var fixture = new StorageFixture(StorageManifest.Tiny());
        Exception? rejection = null;
        fixture.Own(new CallbackDisposable(() => rejection = Record.Exception(() => { _ = fixture.Run(() => Task.FromResult(true)); })));
        await fixture.DisposeAsync();
        Assert.IsType<ObjectDisposedException>(rejection);
    }

    [Fact]
    public async Task PartialGenerationFailureCleansOnlyItsOwnedRoot()
    {
        await using var parent = new StorageFixture(StorageManifest.Tiny());
        Assert.Throws<ArgumentException>(() => new StorageFixture(new("partial", [""], [new("good.txt", 1, 0), new("bad\0name", 1, 0)]), parent.State));
        Assert.Empty(Directory.GetFileSystemEntries(parent.State));
        parent.VerifyInventory();
    }

    [Theory]
    [InlineData("stream/recursive_bytes", "0")]
    [InlineData("node/A/own_bytes", "0")]
    [InlineData("node/A/files", "1")]
    [InlineData("progress/visited", "0")]
    [InlineData("heatmap/node/A/bytes", "5")]
    [InlineData("file/A/deep/deep.bin", null)]
    public void IndependentOracleRejectsNoWorkMissingSubtreesAndStaleOutput(string key, string? wrong)
    {
        var manifest = StorageManifest.Tiny();
        var expected = StorageOracle.Expected(manifest, "app_empty");
        Assert.Equal(["90"], expected["stream/recursive_bytes"]);
        Assert.Equal(["16"], expected["node/A/own_bytes"]);
        Assert.Equal(["33"], expected["node/A/recursive_bytes"]);
        Assert.Equal(["3"], expected["extension/.txt/count"]);
        Assert.Equal(["8"], expected["extension/.txt/bytes"]);
        var actual = expected.ToDictionary(p => p.Key, p => p.Value[0]);
        Assert.Empty(StorageOracle.Compare(manifest, "app_empty", actual));
        if (wrong == null) actual.Remove(key); else actual[key] = wrong;
        Assert.Contains(StorageOracle.Compare(manifest, "app_empty", actual), difference => difference.Key == key);
    }

    [Theory]
    [InlineData("app_empty")]
    [InlineData("app_hit")]
    [InlineData("changed_rescan")]
    [InlineData("persisted_restore")]
    public async Task TinyStateMatrixRetainsActualProductFailures(string state)
    {
        await using var fixture = new StorageFixture(StorageManifest.Tiny());
        var session = fixture.Own(new StorageSession(fixture));
        Assert.Empty(session.Engine.DirectorySizeCache);
        if (state != "app_empty")
        {
            await session.Scan();
            _ = StorageOracle.Observe(fixture, session, "app_empty");
            Assert.Equal(4, session.Engine.DirectorySizeCache.Count);
            Assert.True(File.Exists(fixture.CachePath));
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, GSSystemAnalyzer.Engine.CacheEntry>>(File.ReadAllText(fixture.CachePath))!;
            Assert.Equal(90, saved[fixture.Workload].Size);
            Assert.Equal(33, saved[fixture.Resolve("A")].Size);
            if (state == "changed_rescan")
            {
                fixture.ChangeFile("A/a.txt", 13);
                session.Engine.InvalidatePaths([fixture.Resolve("A")]);
                Assert.False(session.Engine.DirectorySizeCache.ContainsKey(fixture.Resolve("A")));
                Assert.True(session.Engine.DirectorySizeCache.ContainsKey(fixture.Resolve("B")));
            }
            else if (state == "persisted_restore")
            {
                fixture.Release(session);
                session = fixture.Own(new StorageSession(fixture));
                output.WriteLine("Restored directory entries: " + session.Engine.DirectorySizeCache.Count);
            }
        }
        if (state != "persisted_restore") await session.Scan();
        var actual = StorageOracle.Observe(fixture, session, state);
        var differences = StorageOracle.Compare(fixture.Manifest, state, actual);
        output.WriteLine(state + ": " + System.Text.Json.JsonSerializer.Serialize(new { actual, differences }, ThresholdValidator.Json));
        Assert.Contains(differences, d => d.Key == "extension/.txt/count" && d.Expected == "3" && d.Actual == "1");
        Assert.Contains(differences, d => d.Key == "types/total_bytes" && d.Expected == (state == "changed_rescan" ? "98" : "90") &&
            d.Actual == (state == "changed_rescan" ? "95" : "87"));
        if (state == "persisted_restore")
        {
            Assert.Contains(differences, d => d.Key == "size/" && d.Expected == "90" && d.Actual == null);
            Assert.Equal(194, actual.Where(p => p.Key.StartsWith("heatmap/bucket/") && p.Key.EndsWith("/bytes")).Sum(p => int.Parse(p.Value)));
            Assert.Equal(0, actual.Where(p => p.Key.StartsWith("heatmap/bucket/") && p.Key.EndsWith("/count")).Sum(p => int.Parse(p.Value)));
            Assert.Contains(differences, d => d.Key == "node//own_bytes" && d.Expected == "3" && d.Actual == "90");
        }
        else
        {
            Assert.Equal(state == "app_hit" ? "unavailable" : state == "changed_rescan" ? "3" : "5", actual["progress/visited"]);
            Assert.DoesNotContain(differences, d => d.Key.StartsWith("stream/") || d.Key.StartsWith("progress/"));
        }
    }

    [Fact]
    public async Task MarkerCaseRenameStillDeletesMarkerLast()
    {
        var fixture = new StorageFixture(StorageManifest.Tiny());
        if (OperatingSystem.IsWindows())
        {
            string intermediate = fixture.Marker + "-rename";
            File.Move(fixture.Marker, intermediate);
            File.Move(intermediate, Path.Combine(fixture.Root, ".OWNER"));
        }
        await fixture.DisposeAsync();
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData(typeof(ScannerBenchmarks))]
    [InlineData(typeof(CacheFootprintBenchmark))]
    public void StorageSuitesRetainAnOwnedFixtureWithoutInvokingUnsafeSetup(Type suite)
    {
        Assert.Contains(suite.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType.FullName == "GSAnalyzer.Benchmarks.Core.StorageFixture");
    }

    [Theory]
    [InlineData("stage_duration", "ms", "mean_elapsed_ms", 0, 100)]
    [InlineData("managed_residual", "B", "max_signed_delta", -100, 12_000_000)]
    public void PhaseAndSignedResidualPolicyDomainsAreRegistered(string name, string unit, string statistic, double lower, double upper)
    {
        var policy = Policy(name, unit, statistic, lower, upper);
        Assert.Equal(policy.Profiles[0], ThresholdValidator.Profile(policy, "storage-control"));
    }

    [Theory]
    [InlineData(-100, true)]
    [InlineData(0, true)]
    [InlineData(12_000_000, true)]
    [InlineData(12_000_001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void SignedResidualEvaluationAndCoverageAgree(double value, bool pass)
    {
        string root = Path.Combine(Path.GetTempPath(), "gs-metric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "raw.json"), "[1]");
            var p = Policy("managed_residual", "B", "max_signed_delta", -100, 12_000_000).Profiles[0];
            var c = p.Cases[0];
            string id = Guid.NewGuid().ToString("N"), hash = new('a', 64);
            var result = new CaseResult { Identity = c.Identity, ExecutionStatus = "succeeded", ExecutionErrors = [], LaunchCount = 1,
                ContractChecks = new() { ["oracle"] = "passed" }, CorrectnessStatus = "passed", CorrectnessBasis = "Controlled output",
                Metrics = [new() { CaseId = c.Identity.Id, DatasetId = c.Identity.DatasetId, Scenario = c.Identity.Scenario,
                    Metric = "managed_residual", Unit = "B", Direction = "lower", Statistic = "max_signed_delta", Value = value,
                    MeasurementStatus = "measured", SampleCount = 1, CorrectnessStatus = "passed", Reason = "control", RawArtifact = "raw.json" }] };
            var run = new RunEvidence { SchemaVersion = 1, RunId = id, ProfileId = p.Id, ConfigSha256 = hash,
                Discovered = [c.Identity], Cases = [result], CollectionErrors = [] };
            var evaluated = ThresholdValidator.Evaluate(p, run, "gate", id, hash, root);
            Assert.Equal(pass, evaluated.Success);
            Assert.Equal(double.IsFinite(value), evaluated.Coverage.Measured.Contains(c.Identity.Id));
            Assert.False(ThresholdValidator.Evaluate(p, run with { Cases = [result with { CorrectnessStatus = "failed" }] },
                "report", id, hash, root).Success);
        }
        finally { Directory.Delete(root, true); }
    }

    private static BenchmarkPolicy Policy(string name, string unit, string statistic, double lower, double upper) => new()
    {
        SchemaVersion = 1, Profiles = [new() { Id = "storage-control", Purpose = "contract", Types = ["StorageControl"], Cases = [new()
        {
            Identity = new() { Type = "StorageControl", Method = "Measure", Parameters = new(), Job = "control", Runtime = ".NET 10.0",
                Tfm = "net10.0", Architecture = "X64", Scenario = "control", CacheTopology = "none", DatasetId = "control" },
            Required = true, ContractChecks = ["oracle"], Metrics = [new() { Metric = name, Unit = unit, Direction = "lower",
                Statistic = statistic, Required = true, BudgetStatus = "approved", Owner = "test control", Lower = lower, Upper = upper }]
        }] }]
    };

    [Theory]
    [InlineData("AppEmpty")]
    [InlineData("AppHit")]
    [InlineData("ChangedRescan")]
    [InlineData("PersistedRestore")]
    [InlineData("WarmedAnalyzerHit")]
    [InlineData("ClearAction")]
    [InlineData("PassiveAfterProductGc")]
    [InlineData("ForcedGcRetention")]
    public async Task BdnLifecycleRetainsSeedsPreconditionsAndActualOutputs(string method)
    {
        var workload = new StorageWorkload();
        string state = BenchmarkResults.StorageScenario(method);
        var fixture = workload.Begin(method, state, "tiny-v1");
        try
        {
            await workload.Run();
            var record = workload.Finish(false);
            Assert.True(record.Cleaned);
            Assert.False(Directory.Exists(fixture.Root));
            Assert.DoesNotContain(record.Differences, d => d.Key.StartsWith("precondition/") || d.Key.StartsWith("workload/") ||
                d.Key.StartsWith("analyzer_hit/") || d.Key.StartsWith("clear/"));
            if (state != "app_empty") Assert.Contains(record.Differences, d => d.Key == "seed/types/total_bytes" && d.Actual == "87");
            Assert.NotEmpty(record.Differences);
            output.WriteLine(JsonSerializer.Serialize(record, ThresholdValidator.Json));
        }
        finally { workload.Cleanup(); }
    }

    [Fact]
    public void AnalyzerNoOpCannotValidateReplacementReads()
    {
        var workload = new StorageWorkload();
        workload.Begin("WarmedAnalyzerHit", "analyzer_hit", "tiny-v1");
        try
        {
            var record = workload.Finish(false);
            Assert.Contains(record.Differences, d => d.Key == "workload/operations" && d.Actual == "0");
            Assert.Contains(record.Differences, d => d.Key == "types/total_bytes" && d.Actual == null);
            Assert.Contains(record.Differences, d => d.Key == "analyzer_hit/types" && d.Actual == "False");
        }
        finally { workload.Cleanup(); }
    }

    [Fact]
    public async Task ClearDetectorSeesRetainedAnalyzerSnapshots()
    {
        var workload = new StorageWorkload();
        workload.Begin("ClearAction", "clear_action", "tiny-v1");
        try
        {
            var session = (StorageSession)typeof(StorageWorkload).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workload)!;
            var cache = (Microsoft.Extensions.Caching.Memory.MemoryCache)typeof(StorageSession).GetField("analyzerCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var retained = cache.Keys.ToDictionary(k => k, k => { cache.TryGetValue(k, out object? value); return value; });
            Assert.Equal(3, retained.Count);
            await workload.Run();
            foreach (var p in retained) { using var entry = cache.CreateEntry(p.Key); entry.Value = p.Value; }
            var record = workload.Finish(false);
            Assert.Contains(record.Differences, d => d.Key == "clear/analyzer_snapshots" && d.Actual == "3");
        }
        finally { workload.Cleanup(); }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-root")]
    [InlineData("damaged")]
    [InlineData("forged-inline")]
    public void LegacyCleanupRequiresOneLosslessFinalRecord(string defect)
    {
        var policy = ThresholdValidator.Read<BenchmarkPolicy>(Path.Combine(AppContext.BaseDirectory, "thresholds.json"));
        var identity = policy.Profiles.Single(p => p.Id == "legacy-footprint").Cases
            .Single(c => c.Identity.Method == "Footprint_FullStoragePipeline").Identity;
        string run = Guid.NewGuid().ToString("N"), launch = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "profile-🤡", "gs-storage-" + Guid.NewGuid().ToString("N"));
        var expected = StorageWorkload.LegacyExpected();
        var records = Enumerable.Range(1, 10).Select(i => new StorageInvocation
        {
            RunId = run, LaunchId = launch, ProcessId = 123, Method = identity.Method, Invocation = i,
            Root = root, CachePath = Path.Combine(root, "state", "scanner_memory.json"), Manifest = StorageManifest.Legacy(true),
            Scenario = "legacy-pipeline", Expected = expected, Actual = expected.ToDictionary(p => p.Key, p => p.Value[0]),
            Differences = [], Before = new() { ["os_cache"] = StorageWorkload.OsCache }, After = [],
            StagesMs = new() { ["legacy-pipeline"] = 1 }, Cleaned = defect == "forged-inline"
        }).ToArray();
        var measurements = new[] { "WorkloadWarmup 1: 1 op, 100.00 ns, 100.00 ns/op" }
            .Concat(new[] { "Actual", "Result" }.SelectMany(stage => Enumerable.Range(1, 8)
                .Select(i => $"Workload{stage} {i}: 1 op, 100.00 ns, 100.00 ns/op")))
            .Select(s => Measurement.Parse(s, 1)).ToArray();
        var lines = records.Select(r => StorageWorkload.Prefix + JsonSerializer.Serialize(r,
            new JsonSerializerOptions(ThresholdValidator.Json) { WriteIndented = false })).ToList();
        string cleanup = StorageWorkload.CleanupPrefix + JsonSerializer.Serialize(defect == "wrong-root" ? root + "-other" : root);
        if (defect == "damaged") cleanup = StorageWorkload.CleanupPrefix + root;
        if (defect is not ("missing" or "forged-inline")) lines.Add(cleanup);
        if (defect == "duplicate") lines.Add(cleanup);
        var wire = lines.Select(s => System.Text.Encoding.ASCII.GetString(System.Text.Encoding.ASCII.GetBytes(s))).ToArray();
        if (defect == "valid")
            Assert.Equal(10, BenchmarkResults.CorrelateStorage(identity, run, 123, wire, measurements, 1).Length);
        else
            Assert.Equal("Storage cleanup evidence.", Assert.Throws<InvalidDataException>(() =>
                BenchmarkResults.CorrelateStorage(identity, run, 123, wire, measurements, 1)).Message);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("case")]
    [InlineData("launch")]
    [InlineData("process")]
    [InlineData("ordinal")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("manifest")]
    [InlineData("expected")]
    [InlineData("forged-correctness")]
    [InlineData("stage")]
    [InlineData("stage-key")]
    [InlineData("snapshot")]
    [InlineData("cleaned")]
    [InlineData("root")]
    [InlineData("operation-count")]
    [InlineData("result-count")]
    [InlineData("gc-operations")]
    public void ChildCorrelationRejectsCorruptEvidence(string defect)
    {
        var policy = ThresholdValidator.Read<BenchmarkPolicy>(Path.Combine(AppContext.BaseDirectory, "thresholds.json"));
        var identity = policy.Profiles.Single(p => p.Id == "storage-stages").Cases.Single(c => c.Identity.Method == "ForcedGcRetention").Identity;
        string run = Guid.NewGuid().ToString("N"), launch = Guid.NewGuid().ToString("N");
        var manifest = StorageManifest.Tiny();
        var expected = StorageWorkload.Expected(manifest, "forced_gc_retention");
        var records = Enumerable.Range(1, 5).Select(i =>
        {
            string root = Path.Combine(Path.GetTempPath(), "gs-storage-" + Guid.NewGuid().ToString("N"));
            return new StorageInvocation { RunId = run, LaunchId = launch, ProcessId = 123, Method = identity.Method,
                Invocation = i, Root = root, CachePath = Path.Combine(root, "state", "scanner_memory.json"), Manifest = manifest,
                Scenario = "forced_gc_retention", Expected = new(expected), Actual = expected.ToDictionary(p => p.Key, p => p.Value[0]),
                Differences = [], Before = new() { ["os_cache"] = StorageWorkload.OsCache }, After = [],
                StagesMs = new() { ["forced_gc_retention"] = i, ["product_clear"] = i + 1 },
                BaselineBytes = 1000, AfterBytes = 900, Cleaned = true };
        }).ToArray();
        var measurements = new[] { "WorkloadWarmup 1: 1 op, 100.00 ns, 100.00 ns/op", "WorkloadActual 1: 1 op, 100.00 ns, 100.00 ns/op",
            "WorkloadActual 2: 1 op, 100.00 ns, 100.00 ns/op", "WorkloadActual 3: 1 op, 100.00 ns, 100.00 ns/op",
            "WorkloadResult 1: 1 op, 100.00 ns, 100.00 ns/op", "WorkloadResult 2: 1 op, 100.00 ns, 100.00 ns/op",
            "WorkloadResult 3: 1 op, 100.00 ns, 100.00 ns/op" }.Select(s => Measurement.Parse(s, 1)).ToArray();
        string[] Lines() => records.Select(r => StorageWorkload.Prefix + JsonSerializer.Serialize(r,
            new JsonSerializerOptions(ThresholdValidator.Json) { WriteIndented = false })).ToArray();
        Assert.Equal(5, BenchmarkResults.CorrelateStorage(identity, run, 123, Lines(), measurements, 1).Length);
        var first = records[0];
        switch (defect)
        {
            case "run": records[0] = first with { RunId = Guid.NewGuid().ToString("N") }; break;
            case "case": records[0] = first with { Method = "ClearAction" }; break;
            case "launch": records[1] = records[1] with { LaunchId = Guid.NewGuid().ToString("N") }; break;
            case "process": records[0] = first with { ProcessId = 124 }; break;
            case "ordinal": records[0] = first with { Invocation = 2 }; break;
            case "duplicate": records = [.. records, first]; break;
            case "missing": records = records.Skip(1).ToArray(); break;
            case "manifest": records[0] = first with { Manifest = manifest with { Id = "other" } }; break;
            case "expected": first.Expected.Remove("workload/operations"); break;
            case "forged-correctness": first.Actual["seed/types/total_bytes"] = "0"; break;
            case "stage": first.StagesMs.Remove("forced_gc_retention"); break;
            case "stage-key": first.StagesMs.Remove("product_clear"); first.StagesMs["wrong"] = 1; break;
            case "snapshot": records[0] = first with { BaselineBytes = -1 }; break;
            case "cleaned": records[0] = first with { Cleaned = false }; break;
            case "root": records[0] = first with { Root = first.Root + "/../" + Path.GetFileName(first.Root), CachePath = Path.Combine(first.Root + "/../" + Path.GetFileName(first.Root), "state", "scanner_memory.json") }; break;
            case "operation-count": measurements[1] = Measurement.Parse("WorkloadActual 1: 2 op, 100.00 ns, 50.00 ns/op", 1); break;
            case "result-count": measurements = measurements[..^1]; break;
        }
        Assert.Throws<InvalidDataException>(() => BenchmarkResults.CorrelateStorage(identity, run, 123, Lines(), measurements, defect == "gc-operations" ? 2 : 1));
    }
}
