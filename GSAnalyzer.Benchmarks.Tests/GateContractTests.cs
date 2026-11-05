using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.Results;
using GSAnalyzer.Benchmarks.Core;
using GSAnalyzer.Benchmarks.Models;

namespace GSAnalyzer.Benchmarks.Tests;

public sealed class GateContractTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gs-gate-" + Guid.NewGuid().ToString("N"));

    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly string configHash = new('a', 64);
    private static readonly CaseIdentity Identity = new()
    {
        Type = "GSAnalyzer.Benchmarks.Tests.GateFixture", Method = "Allocate", Parameters = new(),
        Job = "contract-net10-x64", Runtime = ".NET 10.0", Tfm = "net10.0", Architecture = "X64",
        Scenario = "synthetic", CacheTopology = "none", DatasetId = "synthetic-v1"
    };

    public GateContractTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "raw.json"), "[128,128,128]");
    }

    private static BenchmarkProfile Policy() => new()
    {
        Id = "control", Purpose = "contract", Types = [Identity.Type],
        Cases = [new() { Identity = Identity, Required = true, ContractChecks = ["allocation_control"],
            Metrics = [new() { Metric = "allocated", Unit = "B/op", Direction = "lower", Statistic = "bytes_per_operation",
                Required = true, BudgetStatus = "approved", Owner = "synthetic control, not a product budget", Lower = 128, Upper = 256 }] }]
    };

    private RunEvidence Evidence() => new()
    {
        SchemaVersion = 1, RunId = runId, ProfileId = "control", ConfigSha256 = configHash, Discovered = [Identity], CollectionErrors = [],
        Cases = [new() { Identity = Identity, ExecutionStatus = "succeeded", ExecutionErrors = [], LaunchCount = 1,
            ContractChecks = new() { ["allocation_control"] = "passed" }, CorrectnessStatus = "not_checked", CorrectnessBasis = "Synthetic harness only.",
            Metrics = [new() { CaseId = Identity.Id, Scenario = Identity.Scenario, DatasetId = Identity.DatasetId,
                Metric = "allocated", Unit = "B/op", Direction = "lower", Statistic = "bytes_per_operation",
                MeasurementStatus = "measured", Value = 128, SampleCount = 3, CorrectnessStatus = "not_checked", Reason = "observed", RawArtifact = "raw.json" }] }]
    };

    private ValidationResult Evaluate(RunEvidence? run = null, BenchmarkProfile? policy = null, string mode = "gate") =>
        ThresholdValidator.Evaluate(policy ?? Policy(), run ?? Evidence(), mode, runId, configHash, root);

    [Fact]
    public void ValidContractPassDoesNotAssertProductCorrectness()
    {
        var result = Evaluate();
        Assert.True(result.Success, string.Join("\n", result.Failures));
        Assert.Equal("not_checked", result.Cases.Single().CorrectnessStatus);
    }

    [Fact]
    public void ConfiguredCrashIsRejected()
    {
        var run = Evidence();
        Assert.Contains(Evaluate(run with { Cases = [run.Cases[0] with { ExecutionStatus = "failed" }] }).Failures,
            f => f.StartsWith("Execution failed:"));
    }

    [Fact]
    public void MissingConfigurationIsRejected() =>
        Assert.Throws<FileNotFoundException>(() => ThresholdValidator.Read<BenchmarkPolicy>(Path.Combine(root, "absent.json")));

    [Fact]
    public void EmptyResultsAreRejected()
    {
        Assert.Contains("Empty execution results.", Evaluate(Evidence() with { Cases = [] }).Failures);
    }

    [Fact]
    public void UnmatchedCrashIsRejected()
    {
        var run = Evidence();
        var crash = run.Cases[0] with { Identity = Identity with { Type = "OtherFixture" }, ExecutionStatus = "failed" };
        Assert.Contains(Evaluate(run with { Cases = [run.Cases[0], crash] }).Failures, f => f.StartsWith("Execution failed:"));
    }

    [Theory]
    [InlineData("performance")]
    [InlineData("release")]
    public void ProductGateCannotUseContractOnlyEvidence(string purpose) =>
        Assert.Contains(Evaluate(policy: Policy() with { Purpose = purpose }).Failures, f => f.StartsWith("Independent product correctness"));

    [Theory]
    [InlineData(128, true)]
    [InlineData(256, true)]
    [InlineData(127.999, false)]
    [InlineData(256.001, false)]
    [InlineData(-1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void InclusiveFiniteBounds(double value, bool pass)
    {
        var run = Evidence();
        var item = run.Cases[0];
        Assert.Equal(pass, Evaluate(run with { Cases = [item with { Metrics = [item.Metrics[0] with { Value = value }] }] }).Success);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("count")]
    [InlineData("unit")]
    [InlineData("direction")]
    [InlineData("statistic")]
    [InlineData("status")]
    [InlineData("raw")]
    [InlineData("traversal")]
    [InlineData("case")]
    [InlineData("dataset")]
    public void MissingOrInvalidEvidenceFails(string field)
    {
        var run = Evidence();
        var c = run.Cases[0];
        var m = c.Metrics[0];
        m = field switch
        {
            "value" => m with { Value = null }, "count" => m with { SampleCount = 0 },
            "unit" => m with { Unit = "MiB" }, "direction" => m with { Direction = "higher" },
            "statistic" => m with { Statistic = "StandardError" }, "status" => m with { MeasurementStatus = "made_up" },
            "raw" => m with { RawArtifact = "missing.json" }, "traversal" => m with { RawArtifact = "../raw.json" },
            "case" => m with { CaseId = "wrong" }, _ => m with { DatasetId = "wrong" }
        };
        Assert.False(Evaluate(run with { Cases = [c with { Metrics = [m] }] }).Success);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("parameter")]
    [InlineData("job")]
    public void IndependentExpectationsDetectRemovedCoverage(string change)
    {
        var removed = change switch
        {
            "method" => Identity with { Method = "RemovedMethod" },
            "parameter" => Identity with { Parameters = new() { ["Count"] = "System.Int32:2" } },
            _ => Identity with { Job = "RemovedJob" }
        };
        var p = Policy();
        Assert.False(Evaluate(policy: p with { Cases = [p.Cases[0], p.Cases[0] with { Identity = removed }] }).Success);
    }

    [Fact]
    public void DuplicateAndEmptyDiscoveryCannotPass()
    {
        Assert.False(Evaluate(Evidence() with { Discovered = [] }).Success);
        Assert.False(Evaluate(Evidence() with { Discovered = [Identity, Identity] }).Success);
        var run = Evidence();
        Assert.False(Evaluate(run with { Cases = [run.Cases[0], run.Cases[0]] }).Success);
    }

    [Fact]
    public void OptionalUnsupportedRequiresAnExplicitReason()
    {
        var p = Policy();
        var optional = p.Cases[0] with { Identity = Identity with { Method = "Optional" }, Required = false, OptionalReason = "Unavailable on this host" };
        var run = Evidence();
        var result = run.Cases[0] with { Identity = optional.Identity, ExecutionStatus = "not_supported", LaunchCount = 0, Metrics = [] };
        var profile = ThresholdValidator.Profile(new() { SchemaVersion = 1,
            Profiles = [p with { Cases = [p.Cases[0], optional] }] }, p.Id);
        Assert.True(Evaluate(run with { Discovered = [Identity, optional.Identity], Cases = [run.Cases[0], result] },
            profile).Success);
        Assert.Throws<InvalidDataException>(() => ThresholdValidator.Profile(new() { SchemaVersion = 1,
            Profiles = [p with { Cases = [p.Cases[0], optional with { OptionalReason = null }] }] }, p.Id));
    }

    [Fact]
    public void StaleRunAndConfigAreRejected()
    {
        Assert.False(Evaluate(Evidence() with { RunId = Guid.NewGuid().ToString("N") }).Success);
        Assert.False(Evaluate(Evidence() with { ConfigSha256 = new string('b', 64) }).Success);
        Assert.False(Evaluate(Evidence() with { SchemaVersion = 2 }).Success);
    }

    [Fact]
    public void ReportModeAllowsMissingBudgetsButNeverExecutionFailure()
    {
        var p = Policy();
        p = p with { Cases = [p.Cases[0] with { Metrics = [p.Cases[0].Metrics[0] with { BudgetStatus = "tbd", Lower = null, Upper = null }] }] };
        Assert.True(Evaluate(policy: p, mode: "report").Success);
        var run = Evidence();
        Assert.False(Evaluate(run with { Cases = [run.Cases[0] with { ExecutionStatus = "failed" }] }, p, "report").Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InRangeReportOnlyObservationsRemainProvisional(bool optionalCase)
    {
        var p = Policy();
        var run = Evidence();
        var identity = Identity with { Method = "Observation" };
        var observed = run.Cases[0] with { Identity = identity,
            Metrics = [run.Cases[0].Metrics[0] with { CaseId = identity.Id }] };
        var declaration = p.Cases[0] with { Identity = identity, Required = !optionalCase, OptionalReason = "Diagnostic observation" };
        if (!optionalCase)
        {
            declaration = declaration with { Metrics = [declaration.Metrics[0], new() { Metric = "mean", Unit = "ns/op", Direction = "lower",
                Statistic = "mean", Required = false, BudgetStatus = "approved", Owner = "test control", Upper = 1000 }] };
            observed = observed with { Metrics = [.. observed.Metrics, observed.Metrics[0] with { Metric = "mean", Unit = "ns/op", Statistic = "mean" }] };
        }
        p = ThresholdValidator.Profile(new() { SchemaVersion = 1, Profiles = [p with { Cases = [p.Cases[0], declaration] }] }, p.Id);
        var evaluated = Evaluate(run with { Discovered = [Identity, identity], Cases = [run.Cases[0], observed] }, p);
        Assert.True(evaluated.Success, string.Join("\n", evaluated.Failures));
        Assert.Equal("warn", evaluated.Cases.Single(c => c.Identity.Id == identity.Id).Metrics.Single(m => m.Metric == (optionalCase ? "allocated" : "mean")).GateDecision);
        Assert.Contains(evaluated.Warnings, w => w.Contains("Report-only"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schema_version\":1,\"schema_version\":1,\"profiles\":[]}")]
    [InlineData("{\"schema_version\":1,\"profiles\":[null]}")]
    public void MalformedPolicyCannotBecomeAnEmptyPass(string json)
    {
        Assert.ThrowsAny<Exception>(() => ThresholdValidator.Profile(ThresholdValidator.Read<BenchmarkPolicy>(WriteConfig(json)), "control"));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    [InlineData("")]
    public void AggregateRejectsEveryNonSuccess(string state) => Assert.False(ThresholdValidator.Aggregate(["success", state]));

    [Fact]
    public void AggregateRequiresAtLeastOneResult()
    {
        Assert.False(ThresholdValidator.Aggregate([]));
        Assert.True(ThresholdValidator.Aggregate(["success", "success"]));
    }

    private string WriteConfig(string text)
    {
        string path = Path.Combine(root, "thresholds.json");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void StatisticsCannotHideAnEarlierFailedLaunchOrAbsentAllocation()
    {
        var config = ManualConfig.Create(DefaultConfig.Instance).AddJob(Job.Default.WithId("explicit")
            .WithRuntime(CoreRuntime.Core10_0).WithPlatform(Platform.X64));
        var benchmark = BenchmarkConverter.TypeToBenchmarks(typeof(GateFixture), config).BenchmarksCases.Single();
        var bad = new ExecuteResult(true, 17, 1, ["WorkloadResult   1: 1024 op, 102400.00 ns, 100.0000 ns/op"], [], [], 0);
        var good = new ExecuteResult(true, 0, 2, ["WorkloadResult   1: 1024 op, 102400.00 ns, 100.0000 ns/op"], [], [], 1);
        Assert.True(bad.IsSuccess);
        var report = new BenchmarkReport(true, benchmark, null!, null!, [bad, good], []);
        Assert.NotNull(report.ResultStatistics);
        var result = BenchmarkResults.Normalize(report, runId, root, BenchmarkResults.LoadedIdentity(), "not-used.csproj");
        Assert.Contains(result.ExecutionErrors, e => e.StartsWith("Launch failed:") && e.Contains("exit=17"));
        Assert.Null(result.Metrics.Single(m => m.Metric == "allocated").Value);
    }

    [Fact]
    public void DiscoveryDoesNotConstructFixturesAndRejectsSourcesBeforeInvocation()
    {
        BenchmarkResults.RequireSafeDiscovery(typeof(ConstructorTrap));
        Assert.Single(BenchmarkConverter.TypeToBenchmarks(typeof(ConstructorTrap)).BenchmarksCases);
        Assert.Throws<InvalidDataException>(() => BenchmarkResults.RequireSafeDiscovery(typeof(SourceTrap)));
    }

    [Fact]
    public void CheckedInProfilesMatchTheExplicitStorageMigration()
    {
        var policy = ThresholdValidator.Read<BenchmarkPolicy>(Path.Combine(AppContext.BaseDirectory, "thresholds.json"));
        foreach (var profile in policy.Profiles)
        {
            ThresholdValidator.Profile(policy, profile.Id);
            var runs = BenchmarkResults.Discover(profile, Path.Combine(root, "discovery"));
            Assert.All(runs.SelectMany(r => r.BenchmarksCases), c => Assert.True(c.Job.Environment.Runtime != null,
                profile.Id + ": " + c.DisplayInfo));
            var ids = runs.SelectMany(r => r.BenchmarksCases).Select(BenchmarkResults.Identity).ToArray();
            Assert.Equal(profile.Cases.Select(c => c.Identity.Id).Order(), ids.Select(c => c.Id).Order());
            Assert.All(runs, r => Assert.True(BenchmarkResults.CanExecute(r.Type)));
        }
        Assert.DoesNotContain(policy.Profiles, p => p.Id is "legacy-storage" or "legacy-scanner");
        Assert.Equal(8, policy.Profiles.Single(p => p.Id == "storage-tiny").Cases.Length);
        Assert.Equal(5, policy.Profiles.Single(p => p.Id == "storage-stages").Cases.Length);
        Assert.Equal(2, policy.Profiles.Single(p => p.Id == "legacy-footprint").Cases.Length);
        var tiny = policy.Profiles.Single(p => p.Id == "storage-tiny").Cases;
        Assert.Equal((from method in new[] { "AppEmpty", "AppHit", "ChangedRescan", "PersistedRestore" }
            from dataset in new[] { "tiny-v1", "tiny-unicode-v1" } select method + ":" + dataset).Order(),
            tiny.Select(c => c.Identity.Method + ":" + c.Identity.DatasetId).Order());
        Assert.Equal(new[] { "ClearAction", "ForcedGcRetention", "PassiveAfterProductGc", "PopulationRebuild", "WarmedAnalyzerHit" },
            policy.Profiles.Single(p => p.Id == "storage-stages").Cases.Select(c => c.Identity.Method).Order());
        Assert.Equal(new[] { "Footprint_FullStoragePipeline", "ReturnToIdle_AfterClearCache" },
            policy.Profiles.Single(p => p.Id == "legacy-footprint").Cases.Select(c => c.Identity.Method).Order());
        Assert.False(BenchmarkResults.CanExecute(typeof(ConstructorTrap)));
        Assert.False(BenchmarkResults.CanExecute(typeof(GateFixture)));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("inverted")]
    [InlineData("nonfinite")]
    [InlineData("unknown-unit")]
    [InlineData("unknown-schema")]
    [InlineData("empty")]
    public void InvalidPolicyIsRejected(string change)
    {
        var p = Policy();
        var c = p.Cases[0];
        var m = c.Metrics[0];
        p = change switch
        {
            "duplicate" => p with { Cases = [c, c] },
            "inverted" => p with { Cases = [c with { Metrics = [m with { Lower = 257 }] }] },
            "nonfinite" => p with { Cases = [c with { Metrics = [m with { Upper = double.PositiveInfinity }] }] },
            "unknown-unit" => p with { Cases = [c with { Metrics = [m with { Unit = "ms" }] }] },
            "empty" => p with { Cases = [] }, _ => p
        };
        Assert.Throws<InvalidDataException>(() => ThresholdValidator.Profile(new() { SchemaVersion = change == "unknown-schema" ? 2 : 1, Profiles = [p] }, p.Id));
    }

    [Fact]
    public void InventoryDetectsTamperingAndNeverInventoriesItself()
    {
        string config = WriteConfig(JsonSerializer.Serialize(new BenchmarkPolicy { SchemaVersion = 1, Profiles = [Policy()] }, ThresholdValidator.Json));
        var m = new RunManifest(1, runId, "control", "contract", "discover", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new("test", false), new("test", false), BenchmarkResults.Hash(config), BenchmarkResults.DatasetHash([Identity]), new(), []);
        m = BenchmarkResults.FinalizeRun(root, m, config, Evidence(), Evaluate());
        BenchmarkResults.VerifyInventory(root, m);
        Assert.DoesNotContain(m.Artifacts, a => a.Path == "manifest.json");
        File.AppendAllText(Path.Combine(root, "raw.json"), "tamper");
        Assert.Throws<InvalidDataException>(() => BenchmarkResults.VerifyInventory(root, m));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public class GateFixture
{
    [Benchmark]
    public byte[] Allocate() => new byte[128];
}

public class ConstructorTrap
{
    public ConstructorTrap() => throw new InvalidOperationException("Discovery constructed a fixture.");
    [Benchmark] public int Value() => 1;
}

public class SourceTrap
{
    public SourceTrap() => throw new InvalidOperationException("Discovery constructed a source.");
    [ParamsSource(nameof(Values))] public int Value { get; set; }
    public IEnumerable<int> Values => throw new InvalidOperationException("Discovery invoked a source.");
    [Benchmark] public int Read() => Value;
}

public sealed class CliContractTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory, Trait("Category", "Cli")]
    [InlineData("")]
    [InlineData("nested/output")]
    public async Task ReadOnlyValidationRejectsOutputInsideEvidenceWithoutChangingInput(string suffix)
    {
        string owned = Path.Combine(Path.GetTempPath(), "gs-cli-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        output.WriteLine("Retained read-only evidence: " + owned);
        async Task<(int Exit, string Text)> Invoke(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            return (process.ExitCode, await stdout + "\n" + await stderr);
        }
        var discovery = await Invoke("--mode", "discover", "--profile", "selftest-pass", "--output", owned);
        Assert.True(discovery.Exit == 0, discovery.Text);
        string input = Directory.GetDirectories(owned, "run-*").Single();
        string[] Snapshot() => Directory.GetFileSystemEntries(input, "*", SearchOption.AllDirectories).Order()
            .Select(p => Path.GetRelativePath(input, p) + (File.Exists(p) ? ":" + BenchmarkResults.Hash(p) + ":" + File.GetLastWriteTimeUtc(p).Ticks : ":directory")).ToArray();
        string[] before = Snapshot();
        var validation = await Invoke("--mode", "discover", "--profile", "selftest-pass", "--evidence", input,
            "--output", Path.Combine(input, suffix));
        Assert.Equal(1, validation.Exit);
        Assert.Equal(before, Snapshot());
        Assert.Contains("--output must be outside --evidence", validation.Text);
        var rootParent = await Invoke("--mode", "discover", "--profile", "selftest-pass", "--evidence", Path.GetPathRoot(owned)!,
            "--output", Path.Combine(owned, "must-not-exist"));
        Assert.Equal(1, rootParent.Exit);
        Assert.Contains("--output must be outside --evidence", rootParent.Text);
        Assert.False(Directory.Exists(Path.Combine(owned, "must-not-exist")));
        foreach (string outside in new[] { input + "-sibling", Path.Combine(input, "..", "outside") })
        {
            var sibling = await Invoke("--mode", "discover", "--profile", "selftest-pass", "--evidence", input, "--output", outside);
            Assert.True(sibling.Exit == 0, sibling.Text);
            Assert.Equal(before, Snapshot());
        }
    }

    [Fact, Trait("Category", "Cli")]
    public async Task ReleaseCommandAndPublishedChildMatrix()
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../GSAnalyzer.Benchmarks/GSAnalyzer.Benchmarks.csproj"));
        string project = Path.GetDirectoryName(source)!;
        string repo = Path.GetDirectoryName(project)!;
        string evidence = Path.Combine(Path.GetTempPath(), "gs-cli-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        output.WriteLine("Retained CLI evidence: " + evidence);
        string unrelated = Path.Combine(evidence, "unrelated");
        Directory.CreateDirectory(unrelated);
        string[] command = ["run", "--project", source, "-c", "Release", "-f", "net10.0", "--no-build", "--"];

        async Task<string> Run(string name, string cwd, int expected, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            string text = await stdout + "\n" + await stderr;
            File.WriteAllText(Path.Combine(evidence, name + ".log"), text);
            output.WriteLine($"{name}: exit {process.ExitCode}");
            Assert.True(process.ExitCode == expected, name + ": expected " + expected + ", actual " + process.ExitCode + "\n" + text);
            return text;
        }

        async Task<string> Gate(string name, string cwd, string profile, int expected, params string[] extra) =>
            await Run(name, cwd, expected, [.. command, "--mode", "gate", "--profile", profile, "--output", evidence, .. extra]);

        string first = await Gate("project-pass", project, "selftest-pass", 0);
        string firstRoot = first.Split('\n').First(l => l.StartsWith("Evidence: "))[10..].Trim();
        await Gate("repository-pass", repo, "selftest-pass", 0);
        await Gate("unrelated-pass", unrelated, "selftest-pass", 0);
        Assert.Contains("Intentional harness crash control", await Gate("throw", project, "selftest-throw", 1));
        Assert.Contains("Outside inclusive limits", await Gate("allocation", project, "selftest-allocation-fail", 1));
        Assert.Contains("Build failed", await Gate("child-build", project, "selftest-pass", 1, "--selftest-build-failure"));
        await Gate("missing-config", unrelated, "selftest-pass", 1, "--config", Path.Combine(evidence, "absent.json"));
        await Gate("empty-filter", unrelated, "selftest-pass", 1, "--filter", "*Absent*");
        Assert.Contains("Unknown profile", await Gate("retired-legacy", project, "legacy-storage", 1));
        Assert.Contains("different parent target framework", await Gate("storage-target", project, "storage-tiny", 1));
        await Gate("read-only", unrelated, "selftest-pass", 0, "--evidence", firstRoot,
            "--manifest-sha", BenchmarkResults.Hash(Path.Combine(firstRoot, "manifest.json")));

        var replayFailures = new List<string>();
        foreach (string defect in new[] { "raw-run", "raw-case", "raw-failure", "generation-failure", "build-failure", "launch-exit",
            "missing-launch", "child-identity", "child-output", "empty-environment", "empty-source", "wrong-sdk", "empty-source-hash" })
        {
            string copy = Path.Combine(evidence, "replay-" + defect);
            foreach (string file in Directory.EnumerateFiles(firstRoot, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(copy, Path.GetRelativePath(firstRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            string manifestPath = Path.Combine(copy, "manifest.json");
            var manifest = ThresholdValidator.Read<RunManifest>(manifestPath);
            if (defect == "empty-environment") manifest = manifest with { Environment = new() };
            else if (defect == "empty-source") manifest = manifest with { Core = new("", false), Harness = new("", false) };
            else if (defect == "wrong-sdk") manifest.Environment["sdk"] = "0.0.0";
            else if (defect == "empty-source-hash") manifest.Environment["core_source_sha256"] = "";
            else
            {
                var cases = ThresholdValidator.Read<RunEvidence>(Path.Combine(copy, "case-results.json"));
                string component = Path.Combine(copy, cases.Cases[0].Metrics[0].RawArtifact);
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(component))!;
                if (defect == "raw-run") json["run_id"] = Guid.NewGuid().ToString("N");
                else if (defect == "raw-case") json["case_id"] = "another-case";
                else if (defect == "raw-failure") json["success"] = false;
                else if (defect == "generation-failure") json["generated"] = false;
                else if (defect == "build-failure") json["built"] = false;
                else if (defect == "missing-launch") json["launches"]!.AsArray().Clear();
                else if (defect == "child-identity") json["children"]![0]!["run_id"] = Guid.NewGuid().ToString("N");
                else if (defect == "child-output") json["launches"]![0]!["standard_output"]!.AsArray().Clear();
                else json["launches"]![0]!["exit_code"] = 17;
                File.WriteAllText(component, json.ToJsonString());
                manifest = manifest with { Artifacts = manifest.Artifacts.Select(a => a.Path == cases.Cases[0].Metrics[0].RawArtifact
                    ? a with { Bytes = new FileInfo(component).Length, Sha256 = BenchmarkResults.Hash(component) } : a).ToArray() };
            }
            BenchmarkResults.Write(manifestPath, manifest);
            BenchmarkResults.VerifyInventory(copy, manifest);
            try { await Gate(defect, unrelated, "selftest-pass", 1, "--evidence", copy); }
            catch (Xunit.Sdk.XunitException error) { replayFailures.Add(error.Message); }
        }
        Assert.Empty(replayFailures);

        string publish = Path.Combine(evidence, "published");
        await Run("publish", repo, 0, "publish", source, "-c", "Release", "-f", "net10.0", "--no-build", "-o", publish);
        string binary = Path.Combine(publish, "GSAnalyzer.Benchmarks.dll");
        await Run("published-pass", unrelated, 0, binary, "--mode", "gate", "--profile", "selftest-pass", "--source-project", source, "--output", evidence);
        await Run("binary-only", unrelated, 1, binary, "--mode", "gate", "--profile", "selftest-pass", "--output", evidence);
        foreach (string state in new[] { "failure", "cancelled", "skipped", "" })
            await Run("aggregate-" + (state == "" ? "missing" : state), unrelated, 1, binary, "--aggregate", "success," + state);
        await Run("aggregate-pass", unrelated, 0, binary, "--aggregate", "success,success");

        if (OperatingSystem.IsWindows())
            await Run("windows-child", repo, 0, "run", "--project", source, "-c", "Release", "-f", "net10.0-windows", "--no-build", "--",
                "--mode", "gate", "--profile", "selftest-windows-pass", "--output", evidence);
        var manifests = Directory.EnumerateFiles(evidence, "manifest.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Manifest: ThresholdValidator.Read<RunManifest>(path))).ToArray();
        Assert.True(manifests.Length >= 8);
        foreach (var item in manifests) BenchmarkResults.VerifyInventory(Path.GetDirectoryName(item.Path)!, item.Manifest);
        Assert.All(manifests, m => Assert.Equal(BenchmarkResults.Hash(Path.Combine(project, "thresholds.json")), m.Manifest.ConfigSha256));
    }
}
