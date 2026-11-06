using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Reports;
using GSAnalyzer.Benchmarks.Core;
using GSAnalyzer.Benchmarks.Models;
using GSSystemAnalyzer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace GSAnalyzer.Benchmarks.Tests;

public class ScenarioResultsTests
{
    private static readonly string RunId = new string('a', 32);
    private static readonly string LaunchId = new string('b', 32);
    private static CaseIdentity Identity(string method = "ProjectCpu", int count = 8) => new()
    {
        Type = EngineObservation.TypeFor(method)!, Method = method,
        Parameters = new() { ["parameter:Count"] = "System.Int32:" + count },
        Job = "engine-net10-windows-x64", Runtime = ".NET 10.0", Tfm = "net10.0-windows", Architecture = "X64",
        Scenario = "engine/" + method, CacheTopology = "engine-local", DatasetId = "engine-reference-v1"
    };
    private static Measurement[] Measurements() => new[] { new Measurement(1, IterationMode.Workload, IterationStage.Warmup, 1, 1, 100) }
        .Concat(Enumerable.Range(1, 3).Select(i => new Measurement(1, IterationMode.Workload, IterationStage.Actual, i, 1, 100)))
        .Concat(Enumerable.Range(1, 3).Select(i => new Measurement(1, IterationMode.Workload, IterationStage.Result, i, 1, 100))).ToArray();
    private static EngineInvocation[] Records(string method = "ProjectCpu", int count = 8) => Enumerable.Range(1, 5)
        .Select(i => new EngineInvocation(RunId, LaunchId, 42, i, method, count, 0, EngineObservation.Expected(method, count, 0))).ToArray();
    private static string[] Lines(EngineInvocation[] records) => records.Select(r => EngineIteration.Prefix + JsonSerializer.Serialize(r, ThresholdValidator.Json)).ToArray();
    private static CaseResult Result(CaseIdentity identity) => new()
    {
        Identity = identity, ExecutionStatus = "succeeded", ExecutionErrors = [], LaunchCount = 1,
        ContractChecks = new(), CorrectnessStatus = "not_checked", CorrectnessBasis = "pending reference comparison", Metrics = []
    };

    [Fact]
    public void FullOrdinalJoinIncludesWarmupAndDiagnosticCorrectness()
    {
        var records = EngineObservation.Correlate(Identity(), RunId, 42, Lines(Records()), Measurements(), 1);
        Assert.Equal("passed", EngineObservation.Apply(Result(Identity()), records).CorrectnessStatus);
        records[^1].Actual["average"] = "99";
        Assert.Equal("failed", EngineObservation.Apply(Result(Identity()), records).CorrectnessStatus);
        Assert.Equal("not_checked", EngineObservation.Apply(Result(Identity()), null).CorrectnessStatus);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("run")]
    [InlineData("process")]
    [InlineData("method")]
    [InlineData("count")]
    [InlineData("launch")]
    [InlineData("ordinal")]
    [InlineData("measurement")]
    [InlineData("gc")]
    [InlineData("parameter")]
    public void IncompleteOrCrossCaseEvidenceCannotJoin(string defect)
    {
        var records = Records(); var measurements = Measurements(); var identity = Identity();
        switch (defect)
        {
            case "missing": records = records[..^1]; break;
            case "duplicate": records = [.. records, records[0]]; break;
            case "run": records[0] = records[0] with { RunId = LaunchId }; break;
            case "process": records[0] = records[0] with { ProcessId = 43 }; break;
            case "method": records[0] = records[0] with { Method = "SampleDisk" }; break;
            case "count": records[0] = records[0] with { Count = 64 }; break;
            case "launch": records[0] = records[0] with { LaunchId = RunId }; break;
            case "ordinal": records[1] = records[1] with { Invocation = 1 }; break;
            case "measurement": measurements = measurements[1..]; break;
            case "parameter": identity = identity with { Parameters = new() { ["parameter:Count"] = "System.String:8" } }; break;
        }
        Assert.ThrowsAny<Exception>(() => EngineObservation.Correlate(identity, RunId, 42, Lines(records), measurements, defect == "gc" ? 2 : 1));
    }

    [Fact]
    public void ActualNetworkRoundingDiscrepancyRemainsFailedProductEvidence()
    {
        using var input = new EngineInput("SampleNetwork", 32);
        var actual = EngineObservation.Capture("SampleNetwork", input.Run());
        var records = Records("SampleNetwork", 32).Select(r => r with { Actual = actual }).ToArray();
        var result = EngineObservation.Apply(Result(Identity("SampleNetwork", 32)), records);
        Assert.Equal("failed", result.CorrectnessStatus);
        Assert.Equal("passed", result.ContractChecks["invocation_correlation"]);
        Assert.Equal("succeeded", result.ExecutionStatus);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("bytes")]
    [InlineData("body")]
    [InlineData("missing")]
    [InlineData("projection")]
    public void SerializedHistoryReplaysTheActualUtf8Body(string defect)
    {
        using var input = new EngineInput("SerializeHistory", 60, 5);
        var actual = EngineObservation.Capture("SerializeHistory", input.Run());
        var identity = Identity("SerializeHistory", 60) with
            { Parameters = new() { ["parameter:Count"] = "System.Int32:60", ["parameter:Minutes"] = "System.Int32:5" } };
        var records = Enumerable.Range(1, 5).Select(i => new EngineInvocation(RunId, LaunchId, 42, i,
            "SerializeHistory", 60, 5, new(actual))).ToArray();
        switch (defect)
        {
            case "bytes": records[^1].Actual["entity_bytes"] = "1"; break;
            case "body": records[^1].Actual["entity_body"] = JsonSerializer.Serialize("{}"); break;
            case "missing": records[^1].Actual.Remove("entity_body"); break;
            case "projection": records[^1].Actual["count"] = "59"; break;
        }
        if (defect != "valid")
            Assert.ThrowsAny<Exception>(() => EngineObservation.Correlate(identity, RunId, 42, Lines(records), Measurements(), 1));
        else
        {
            var joined = EngineObservation.Correlate(identity, RunId, 42, Lines(records), Measurements(), 1);
            Assert.Equal("passed", EngineObservation.Apply(Result(identity), joined).CorrectnessStatus);
        }
    }
}

public sealed class TransportImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gs-scenario-import-" + Guid.NewGuid().ToString("N"));
    private const string Run = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Backend = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        Client = "cccccccccccccccccccccccccccccccc", Attempt = "dddddddddddddddddddddddddddddddd";
    private static readonly string Hash = new('e', 64);
    private readonly List<JsonObject> backend = [], client = [];
    private ScenarioCapture capture;
    private BenchmarkProfile Profile => ThresholdValidator.Profile(ThresholdValidator.Read<BenchmarkPolicy>(
        Path.Combine(AppContext.BaseDirectory, "thresholds.json")), "transport-mock");
    private string Input => Path.Combine(root, "input");
    private string Output => Path.Combine(root, "output-" + Guid.NewGuid().ToString("N"));

    public TransportImportTests()
    {
        Directory.CreateDirectory(Input);
        AddBackend("trace.start", 0, "observed", counts: new() { ["rich"] = 0 });
        var source = Metadata(2);
        AddBackend("send.RamUpdate.start", 10, "started", "send-1", source);
        AddBackend("send.RamUpdate.end", 11, "succeeded", "send-1", source);
        AddClient("trace.start", 0, counts: new() { ["rich"] = 0 });
        AddClient("scenario.Idle.start", 100, "started", Attempt);
        AddClient("transport.negotiate", 110);
        AddClient("transport.http_connected", 120);
        AddClient("receive.RamUpdate", 130, "received", source: source);
        AddClient("state.ram.applied", 140, source: source, counts: new() { ["processes"] = 3 });
        AddClient("scenario.Idle.end", 150, "succeeded", Attempt);
        capture = new(1, Run, "transport-mock", "minimal", DateTimeOffset.Parse("2026-11-05T12:00:00Z"),
            DateTimeOffset.Parse("2026-11-05T12:00:01Z"), new(new('1', 40), false), new(new('2', 40), true),
            new() { ["backend_sha256"] = Hash, ["client_sha256"] = Hash, ["core_source_sha256"] = Hash,
                ["harness_source_sha256"] = Hash, ["sdk"] = "10.0.302", ["flutter"] = "controlled", ["dart"] = "controlled",
                ["os"] = "Windows", ["architecture"] = "X64", ["tfm"] = "net10.0-windows", ["environment_id"] = "isolated-contract" },
            [new(Backend, "backend", 42, Backend, Hash, "backend.jsonl", "exited", 0),
                new(Client, "client", 43, Client, Hash, "client.jsonl", "exited", 0)],
            [new(Attempt, "Idle", 1, Client, [Backend], [], [], "succeeded")], []);
    }

    private static JsonObject Metadata(int sequence) => new() { ["schemaVersion"] = 1, ["runId"] = Run, ["streamId"] = "ram",
        ["producerEpoch"] = Backend, ["sequence"] = sequence.ToString(), ["clockId"] = "dotnet-" + Backend, ["frequencyHz"] = "1000" };
    private void AddBackend(string stage, long ticks, string outcome, string? operation = null, JsonObject? source = null, JsonObject? counts = null) =>
        backend.Add(new() { ["eventId"] = (backend.Count + 1).ToString(), ["processId"] = 42, ["stage"] = stage,
            ["localTicks"] = ticks.ToString(), ["outcome"] = outcome, ["operationId"] = operation,
            ["benchmarkTrace"] = (source ?? Metadata(backend.Count + 1)).DeepClone(), ["counts"] = counts });
    private void AddClient(string stage, long ticks, string outcome = "observed", string? operation = null, JsonObject? source = null, JsonObject? counts = null)
    {
        var record = new JsonObject { ["schemaVersion"] = 1, ["runId"] = Run, ["producerEpoch"] = Client,
            ["clockId"] = "dart-" + Client, ["frequencyHz"] = "1000", ["eventId"] = (client.Count + 1).ToString(),
            ["processId"] = 43, ["stage"] = stage, ["localTicks"] = ticks.ToString(), ["outcome"] = outcome };
        if (operation != null) record["operationId"] = operation;
        if (source != null) record["benchmarkTrace"] = source.DeepClone();
        if (counts != null) record["counts"] = counts;
        client.Add(record);
    }
    private void Write(bool footer = true, long dropped = 0)
    {
        if (footer)
        {
            AddBackend("trace.end", 1000, "drained", counts: new() { ["written"] = backend.Count, ["dropped"] = 0,
                ["outstanding"] = 0, ["eviction_queue_unknown"] = 0 });
            AddClient("trace.end", 1000, dropped == 0 ? "drained" : "incomplete", counts: new() { ["written"] = client.Count,
                ["dropped"] = dropped, ["outstanding"] = 0, ["owned_tasks"] = 0 });
        }
        File.WriteAllLines(Path.Combine(Input, "backend.jsonl"), backend.Select(r => r.ToJsonString()));
        File.WriteAllLines(Path.Combine(Input, "client.jsonl"), client.Select(r => r.ToJsonString()));
        SaveInventory();
    }
    private void SaveInventory()
    {
        capture = capture with { Artifacts = Directory.GetFiles(Input, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "collection.json").Select(path => new ArtifactEntry(Path.GetRelativePath(Input, path).Replace('\\', '/'),
            new FileInfo(path).Length, BenchmarkResults.Hash(path))).ToArray() };
        BenchmarkResults.Write(Path.Combine(Input, "collection.json"), capture);
    }
    private (RunManifest Manifest, RunEvidence Evidence) Import(string? output = null) => ScenarioResults.Import(Profile, Input, output ?? Output, Hash, "report");

    [Theory]
    [InlineData("missing")]
    [InlineData("transport-unknown")]
    [InlineData("resources-unsettled")]
    [InlineData("duplicate")]
    [InlineData("qualified")]
    public void ClientLifetimeRequiresExplicitOwnedAndPackageSettlement(string state)
    {
        AddClient("transport.dispose", 800, "disconnected");
        if (state != "missing")
            AddClient("client.lifetime", 900, state == "transport-unknown" ? "unknown" : "settled", counts: new()
                { ["owned_resources_settled"] = state == "resources-unsettled" ? 0 : 1,
                  ["transport_internal_unknown"] = state == "transport-unknown" ? 1 : 0 });
        if (state == "duplicate") AddClient("client.lifetime", 901, "settled", counts: new()
            { ["owned_resources_settled"] = 1, ["transport_internal_unknown"] = 0 });
        Write();
        Assert.Equal(state == "qualified" ? "passed" : "not_checked", Import().Evidence.Cases.Single().ContractChecks["lifetime"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientOwnedTaskFooterCannotClaimDrainWithoutZero(bool missing)
    {
        Write();
        if (missing) client[^1]["counts"]!.AsObject().Remove("owned_tasks");
        else client[^1]["counts"]!["owned_tasks"] = 1;
        Write(footer: false);
        var result = Import().Evidence.Cases.Single();
        Assert.Equal("failed", result.ContractChecks["observer_integrity"]);
        Assert.Equal("failed", result.ExecutionStatus);
    }

    [Fact]
    public void AsyncErrorAfterScenarioIntervalRemainsFailedProductEvidence()
    {
        AddClient("scenario.async_error", 900, "failed");
        Write();
        var result = Import().Evidence.Cases.Single();
        Assert.Equal("failed", result.ContractChecks["scenario_outcome"]);
        Assert.Equal("failed", result.CorrectnessStatus);
        Assert.Equal("failed", result.ExecutionStatus);
    }

    [Fact]
    public void ImportKeepsLocalDurationAndMockCorrectnessDistinctFromCoverage()
    {
        Write();
        string output = Output;
        var (manifest, evidence) = Import(output);
        var result = Assert.Single(evidence.Cases);
        Assert.Equal(("succeeded", "not_checked", 2), (result.ExecutionStatus, result.CorrectnessStatus, result.LaunchCount));
        Assert.Equal("passed", result.ContractChecks["correlation"]);
        Assert.Equal((50.0, 1L), (result.Metrics.Single().Value, result.Metrics.Single().SampleCount));
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
        var validation = ThresholdValidator.Evaluate(Profile, evidence, "report", Run, Hash, output);
        Assert.False(validation.Success);
        Assert.Equal(9, validation.Coverage.Missing.Length);
    }

    [Theory]
    [InlineData("dropped")]
    [InlineData("footer")]
    [InlineData("exit")]
    [InlineData("launch")]
    [InlineData("interval")]
    [InlineData("partial")]
    public void IncompleteAttemptsRemainFailuresInsteadOfDisappearing(string defect)
    {
        if (defect == "exit") capture = capture with { Launches = [capture.Launches[0], capture.Launches[1] with { ExitCode = 17 }] };
        if (defect == "launch") capture = capture with { Attempts = [capture.Attempts[0] with { ClientLaunch = new('f', 32) }] };
        if (defect == "interval") client.RemoveAt(client.Count - 1);
        Write(footer: defect != "footer", dropped: defect == "dropped" ? 1 : 0);
        if (defect == "partial")
        {
            File.Move(Path.Combine(Input, "client.jsonl"), Path.Combine(Input, "client.jsonl.partial"));
            capture = capture with { Launches = [capture.Launches[0], capture.Launches[1] with { Trace = "client.jsonl.partial" }],
                Artifacts = capture.Artifacts.Select(a => a.Path == "client.jsonl" ? a with { Path = "client.jsonl.partial" } : a).ToArray() };
            BenchmarkResults.Write(Path.Combine(Input, "collection.json"), capture);
        }
        var result = Assert.Single(Import().Evidence.Cases);
        Assert.Equal("failed", result.ExecutionStatus);
        Assert.NotEmpty(result.ExecutionErrors);
        Assert.Equal(defect == "exit" ? "passed" : "failed", result.ContractChecks["observer_integrity"]);
        if (defect is "launch" or "interval") Assert.Null(result.Metrics.Single().Value);
    }

    [Fact]
    public void FailedAndTimedOutCompletedIntervalsStayInTheMean()
    {
        string second = new('e', 32), third = new('f', 32);
        AddClient("scenario.Idle.start", 200, "started", second);
        AddClient("scenario.Idle.end", 300, "failed", second);
        AddClient("scenario.Idle.start", 400, "started", third);
        AddClient("scenario.Idle.end", 700, "timed_out", third);
        capture = capture with { Attempts = [capture.Attempts[0],
            capture.Attempts[0] with { Id = second, Ordinal = 2, Outcome = "failed" },
            capture.Attempts[0] with { Id = third, Ordinal = 3, Outcome = "timed_out" }] };
        Write();
        var result = Assert.Single(Import().Evidence.Cases);
        Assert.Equal("failed", result.ExecutionStatus);
        Assert.Equal((150.0, 3L), (result.Metrics.Single().Value, result.Metrics.Single().SampleCount));
        Assert.Contains(result.ExecutionErrors, e => e.Contains("timed_out"));
        Assert.Contains(result.ExecutionErrors, e => e.Contains("failed"));
    }

    [Fact]
    public void FailedProcessWithCompleteTraceKeepsObserverIntegrityIndependent()
    {
        capture = capture with { Launches = [capture.Launches[0], capture.Launches[1] with { ExitCode = 1 }] };
        client[6]["outcome"] = "timed_out";
        capture = capture with { Attempts = [capture.Attempts[0] with { Outcome = "timed_out" }] };
        Write();
        var result = Import().Evidence.Cases.Single();
        Assert.Equal("failed", result.ExecutionStatus);
        Assert.Contains(result.ExecutionErrors, e => e.Contains("unsuccessful process exit"));
        Assert.Equal("failed", result.ContractChecks["scenario_outcome"]);
        Assert.Equal("passed", result.ContractChecks["observer_integrity"]);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("process")]
    [InlineData("epoch")]
    [InlineData("clock")]
    [InlineData("frequency")]
    [InlineData("duplicate-event")]
    [InlineData("reverse-span")]
    [InlineData("ordinal")]
    [InlineData("artifact-hash")]
    [InlineData("source")]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("truncated-json")]
    public void InvalidWireAndProvenanceAreRejected(string defect)
    {
        switch (defect)
        {
            case "run": client[2]["runId"] = new string('f', 32); break;
            case "process": client[2]["processId"] = 42; break;
            case "epoch": client[2]["producerEpoch"] = Backend; break;
            case "clock": client[2]["clockId"] = "dotnet-" + Client; break;
            case "frequency": client[2]["frequencyHz"] = "0"; break;
            case "duplicate-event": client[2]["eventId"] = "1"; break;
            case "reverse-span": backend[2]["localTicks"] = "9"; break;
            case "ordinal": capture = capture with { Attempts = [capture.Attempts[0] with { Ordinal = 2 }] }; break;
            case "source": capture = capture with { Core = new("", false) }; break;
            case "unknown-property": client[2]["unregistered"] = true; break;
        }
        Write();
        if (defect == "artifact-hash") File.AppendAllText(Path.Combine(Input, "client.jsonl"), "\n");
        if (defect is "duplicate-property" or "truncated-json")
        {
            string path = Path.Combine(Input, "client.jsonl");
            File.AppendAllText(path, defect == "duplicate-property" ? "{\"stage\":\"one\",\"stage\":\"two\"}\n" : "{\"stage\":");
            SaveInventory();
        }
        Assert.ThrowsAny<Exception>(() => Import());
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("correctness")]
    [InlineData("launches")]
    [InlineData("correlation")]
    [InlineData("source-kind")]
    [InlineData("manifest-run")]
    [InlineData("manifest-end")]
    public void ReplayRederivesSemanticFields(string defect)
    {
        Write();
        string output = Output;
        var (manifest, evidence) = Import(output);
        var result = evidence.Cases.Single();
        switch (defect)
        {
            case "measurement": result = result with { Metrics = [result.Metrics.Single() with { Value = 1 }] }; break;
            case "correctness": result = result with { CorrectnessStatus = "passed" }; break;
            case "launches": result = result with { LaunchCount = 1 }; break;
            case "correlation": result.ContractChecks["correlation"] = "not_checked"; break;
            case "source-kind": manifest = manifest with { CollectionKind = "bdn" }; break;
            case "manifest-run": manifest = manifest with { RunId = new('f', 32) }; break;
            case "manifest-end": manifest = manifest with { EndedUtc = manifest.EndedUtc.AddSeconds(1) }; break;
        }
        Assert.Throws<InvalidDataException>(() => ScenarioResults.VerifyExecution(output, manifest, evidence with { Cases = [result] }, Profile));
    }

    private const string Scan = "11111111111111111111111111111111", Request = "22222222222222222222222222222222";
    private static JsonObject Source(string stream, int sequence)
    {
        var metadata = Metadata(sequence);
        metadata["streamId"] = stream;
        return metadata;
    }
    private void Method(string method)
    {
        client[1]["stage"] = "scenario." + method + ".start";
        client[6]["stage"] = "scenario." + method + ".end";
        client[6]["localTicks"] = "900";
        capture = capture with { Attempts = [capture.Attempts[0] with { Method = method }] };
    }
    private void Ordered()
    {
        foreach (var records in new[] { backend, client })
        {
            records.Sort((a, b) => long.Parse(a["localTicks"]!.GetValue<string>()).CompareTo(long.Parse(b["localTicks"]!.GetValue<string>())));
            for (int i = 0; i < records.Count; i++) records[i]["eventId"] = (i + 1).ToString();
        }
    }
    private void Http(string id = Request, int minutes = 0)
    {
        var source = Source("http-" + id, 1);
        AddBackend("http.handler.start", 20, "started", id, source);
        AddBackend("http.route", 21, "observed", source: source);
        backend[^1]["key"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Backend + "\n" + (minutes == 0 ? "POST /api/storage/stream-sector" : "GET /api/telemetry/history"))));
        AddBackend("http.handler.end", 24, "succeeded", id, source);
        AddBackend("http.response", 25, "observed", source: source, counts: new() { ["status"] = 200 });
        AddClient("http.round_trip.start", 200, "started", id);
        AddClient("http.headers", 210, operation: id, counts: new() { ["status"] = 200 });
        AddClient("http.round_trip.end", 220, "succeeded", id, counts: new() { ["status"] = 200, ["entity_bytes"] = 42 });
        if (minutes != 0)
        {
            AddClient("http.decode.start", 221, "started", id);
            AddClient("http.decode.end", 222, "succeeded", id, counts: new() { ["entity_bytes"] = 42 });
        }
    }
    private void ScanCase(string method = "AppEmpty")
    {
        Method(method);
        capture = capture with { Attempts = [capture.Attempts[0] with { ScanIds = [Scan], RequestIds = [Request] }] };
        Http();
        AddClient("state.directory.selected", 198); client[^1]["label"] = Scan;
        AddClient("state.directory.request", 199, operation: Request);
        client[^1]["label"] = Guid.ParseExact(Scan, "N").ToString();
        AddBackend("scan.session.start", 22, "started", source: Source(Scan, 1));
        foreach (var (stage, first, last, outcome) in new[] {
            ("stream.controller_task", 23L, 96L, "succeeded"), ("stream.controller_queue", 23L, 29L, "settled"),
            ("stream.service", 30L, 89L, "succeeded"), ("scan.lock_queue", 31L, 35L, "acquired"),
            ("scan.active", 36L, 60L, "succeeded"), ("coalescer.consumer", 30L, 70L, "settled"),
            ("scan.save", 62L, 65L, "succeeded"), ("stream.final_save", 61L, 66L, "returned") })
        {
            var source = Source(Scan, backend.Count + 1);
            AddBackend(stage + ".start", first, "started", stage, source);
            AddBackend(stage + ".end", last, outcome, stage, source);
            if (stage == "stream.service") backend[^1]["counts"] = new JsonObject { ["own_bytes"] = 3, ["recursive_bytes"] = 90 };
        }
        AddBackend("coalescer.summary", 71, "observed", source: Source(Scan, 50), counts: new() {
            ["accepted"] = 4, ["drained"] = 4, ["delivered"] = 4, ["abandoned"] = 0, ["rejected"] = 0 });
        AddBackend("scan.session.end", 88, "removed", source: Source(Scan, 60));
        AddBackend("stream.terminal", 90, "succeeded", source: Source(Scan, 61), counts: new() { ["has_summary"] = 1, ["recursive_bytes"] = 90 });
        var chunk = Source(Scan, 40); var complete = Source(Scan, 62);
        AddBackend("stream.chunk", 49, "observed", source: chunk, counts: new() { ["nodes"] = 4, ["bytes"] = 90 });
        AddBackend("send.DirectoryChunk.start", 50, "started", "chunk", chunk);
        AddBackend("send.DirectoryChunk.end", 51, "succeeded", "chunk", chunk);
        AddBackend("send.DirectoryStreamComplete.start", 91, "started", "complete", complete);
        AddBackend("send.DirectoryStreamComplete.end", 92, "succeeded", "complete", complete);
        AddClient("receive.DirectoryChunk", 300, "received", source: chunk);
        AddClient("state.directory.applied", 301, source: chunk, counts: new() { ["nodes"] = 4 });
        AddClient("receive.DirectoryStreamComplete", 310, "received", source: complete, counts: new() { ["sequence_jump"] = 22 });
        AddClient("state.directory.terminal", 311, "applied", source: complete, counts: new() { ["nodes"] = 4, ["own_bytes"] = 3, ["recursive_bytes"] = 90 });
        AddClient("scenario.snapshot", 320, operation: Attempt, counts: new() {
            ["nodes"] = 4, ["a_bytes"] = 33, ["b_bytes"] = 54, ["root_bytes"] = 3, ["zero_bytes"] = 0, ["unknown"] = 0 });
        client[^1]["label"] = Scan;
    }

    private void BindTiny()
    {
        ColdWalks(Scan);
        string artifact = "cases/" + Attempt + "/fixtures.json";
        string path = "C:/owned/tiny-a";
        var manifest = StorageManifest.Tiny();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Input, artifact))!);
        var fixture = new { id = manifest.Id, root = path, directories = manifest.Directories, observedDirectories = manifest.Directories,
            files = manifest.Files.Select(f => new { path = f.Path, bytes = f.Bytes, ageDays = f.AgeDays }),
            inventory = manifest.Files.Select(f => new { path = f.Path, bytes = f.Bytes,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[f.Bytes])) }) };
        File.WriteAllText(Path.Combine(Input, artifact), JsonSerializer.Serialize(new { primary = fixture }));
        string key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Backend + "\n" + path.ToUpperInvariant())));
        AddBackend("stream.request", 22, "observed", Request, Source(Scan, 100)); backend[^1]["key"] = key;
        capture = capture with { Attempts = [capture.Attempts[0] with { Scans =
            [new(Scan, Request, Backend, key, artifact, "primary", "current", "app_empty", [])] }] };
    }

    private void ColdWalks(string scan, bool large = false)
    {
        int count = large ? 505 : 3;
        for (int i = 0; i < count; i++)
        {
            var source = Source(scan, 200 + i);
            string operation = "walk-" + i;
            AddBackend("scan.walk.start", 40, "started", operation, source);
            AddBackend("scan.walk.end", 48, "succeeded", operation, source, new() {
                ["observed_files"] = large ? i < 5 ? 0 : 100 : new[] { 2, 1, 2 }[i],
                ["own_bytes"] = large ? 0 : new[] { 16, 17, 54 }[i] });
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong-root")]
    [InlineData("wrong-request")]
    [InlineData("wrong-launch")]
    [InlineData("missing-file")]
    [InlineData("missing-directory")]
    [InlineData("false-manifest")]
    [InlineData("missing-observed-directory")]
    [InlineData("before-session")]
    [InlineData("duplicate-binding")]
    [InlineData("wrong-content")]
    public void ScanBindingUsesBackendRequestAndIndependentFixtureMembership(string defect)
    {
        ScanCase(); BindTiny();
        if (defect == "wrong-root") backend[^1]["key"] = new string('f', 64);
        if (defect == "wrong-request") backend[^1]["operationId"] = new string('f', 32);
        if (defect == "before-session") backend[^1]["localTicks"] = "21";
        if (defect == "duplicate-binding") capture = capture with { Attempts = [capture.Attempts[0] with
            { Scans = [capture.Attempts[0].Scans[0], capture.Attempts[0].Scans[0]] }] };
        if (defect == "wrong-launch") capture = capture with { Attempts = [capture.Attempts[0] with
            { Scans = [capture.Attempts[0].Scans[0] with { BackendLaunch = new('f', 32) }] }] };
        string path = Path.Combine(Input, capture.Attempts[0].Scans[0].FixtureArtifact);
        var fixture = JsonNode.Parse(File.ReadAllText(path))!;
        if (defect == "missing-file") fixture["primary"]!["inventory"]!.AsArray().RemoveAt(0);
        if (defect == "missing-directory") fixture["primary"]!["directories"]!.AsArray().RemoveAt(1);
        if (defect == "false-manifest") fixture["primary"]!["id"] = "scanner-50k-v1";
        if (defect == "missing-observed-directory") fixture["primary"]!["observedDirectories"]!.AsArray().RemoveAt(1);
        if (defect == "wrong-content") fixture["primary"]!["inventory"]![0]!["sha256"] = new string('f', 64);
        File.WriteAllText(path, fixture.ToJsonString());
        Ordered(); Write(); string output = Output; var (manifest, evidence) = Import(output);
        Assert.Equal(defect == "none" ? "passed" : "failed", evidence.Cases.Single().ContractChecks["scan_binding"]);
        if (defect != "none") Assert.Equal("failed", evidence.Cases.Single().CorrectnessStatus);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Fact]
    public void ScanBindingAcceptsLauncherOriginTimestamp()
    {
        ScanCase(); BindTiny();
        string path = Path.Combine(Input, capture.Attempts[0].Scans[0].FixtureArtifact);
        var fixture = JsonNode.Parse(File.ReadAllText(path))!;
        fixture["primary"]!["originUtc"] = "2026-11-05T12:00:00.0000000Z";
        File.WriteAllText(path, fixture.ToJsonString());
        Ordered(); Write();
        Assert.Equal("passed", Import().Evidence.Cases.Single().ContractChecks["scan_binding"]);
    }

    [Fact]
    public async Task LauncherSerializedFixtureAndEnrichedBindingImportAndReplay()
    {
        if (!OperatingSystem.IsWindows()) return;
        ScanCase(); BindTiny();
        string artifact = capture.Attempts[0].Scans[0].FixtureArtifact;
        string fixture = Path.Combine(Input, artifact);
        string owned = Path.Combine(root, "fixture-root");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "scripts", "run-scenarios.ps1"))) repository = repository.Parent;
        Assert.NotNull(repository);
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string command = $". {Quote(Path.Combine(repository.FullName, "scripts", "run-scenarios.ps1"))} -LibraryOnly; " +
            $"$f = New-Fixture {Quote(owned)} $false; $f | ConvertTo-Json -Depth 32 | Set-Content -Encoding UTF8 {Quote(Path.Combine(root, "fixture.json"))}";
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("PSModulePath");
        foreach (string arg in new[] { "-NoProfile", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        var actual = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "fixture.json")))!;
        Assert.Equal(4, actual["observedDirectories"]!.AsArray().Count);
        File.WriteAllText(fixture, new JsonObject { ["primary"] = actual }.ToJsonString());
        string key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Backend + "\n" + owned.Replace('\\', '/').ToUpperInvariant())));
        backend.Single(e => e["stage"]!.GetValue<string>() == "stream.request")["key"] = key;
        capture = capture with { Attempts = [capture.Attempts[0] with { Scans = [capture.Attempts[0].Scans[0] with { RootKey = key }] }] };
        Ordered(); Write(); string output = Output; var (manifest, evidence) = Import(output);
        Assert.Equal("passed", evidence.Cases.Single().ContractChecks["scan_binding"]);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("owned")]
    [InlineData("foreign")]
    [InlineData("mixed")]
    [InlineData("query-error")]
    [InlineData("descendants")]
    [InlineData("force-error")]
    [InlineData("signal-error")]
    public async Task LauncherOwnershipAndFailureEvidence(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunLauncherControl("$case = '" + scenario + "'; " + """
            function Expect-Failure([scriptblock]$Action, [string]$Message) {
                try { & $Action } catch { Assert-Condition ($_.Exception.Message -like ('*' + $Message + '*')) 'Wrong failure'; return }
                throw 'Expected failure was not raised'
            }
            $script:rows = @()
            function Get-Listeners {
                if ($case -eq 'query-error') { throw 'native-query-failed' }
                return $script:rows
            }
            if ($case -eq 'empty') {
                Assert-Port
                Expect-Failure { Assert-Port 42 } 'unavailable'
            } elseif ($case -eq 'owned') {
                $script:rows = @(@{OwningProcess=42}, @{OwningProcess=42})
                Assert-Port 42
                Expect-Failure { Assert-Port } 'foreign'
            } elseif ($case -in @('foreign','mixed','query-error')) {
                $script:rows = @(@{OwningProcess=43})
                if ($case -eq 'mixed') { $script:rows += @{OwningProcess=42} }
                $expected = if ($case -eq 'query-error') { 'native-query-failed' } else { 'foreign' }
                Expect-Failure { Assert-Port 42 } $expected
                $entry = @{ process = [pscustomobject]@{ Pid = 42 } }
                $entry.process | Add-Member ScriptMethod Wait { param($ms) return $false }
                Expect-Failure { Wait-Backend $entry } $expected
            } else {
                Add-Type -TypeDefinition @'
            using System;
            using System.ComponentModel;
            public sealed class ControlledOwner : IDisposable {
                public bool Exited, FailForce, FailSignal, Disposed;
                public int Forces, Signals;
                public uint ActiveProcesses { get; set; }
                public int ExitCode { get { return 1; } }
                public bool Wait(int milliseconds) { return Exited; }
                public void Break() {
                    Signals++;
                    if(FailSignal) throw new AggregateException(new Win32Exception(6,"secret-canary"), new Win32Exception(5,"secret-canary"));
                }
                public void Force() {
                    Forces++; if(FailForce) throw new Win32Exception(5);
                    Exited=true; ActiveProcesses=0;
                }
                public void Dispose() { Disposed=true; }
            }
            '@
                $script:RunId = 'control'
                $script:Ledger = New-Object IO.StreamWriter((Join-Path $ownedRoot 'ledger.jsonl'), $false, $script:Utf8)
                $owner = New-Object ControlledOwner
                $owner.Exited = $case -ne 'signal-error'; $owner.ActiveProcesses = 1
                $owner.FailForce = $case -eq 'force-error'; $owner.FailSignal = $case -eq 'signal-error'
                $entry = @{ id='owned'; role='backend'; process=$owner; settled=$false; outcome='failed'; exitCode=$null }
                try {
                    if ($case -eq 'force-error') {
                        $later = New-Object ControlledOwner; $later.Exited = $true
                        Close-OwnedLaunches @($entry, @{ id='later'; role='client'; process=$later; settled=$false })
                        Assert-Condition ($owner.Disposed -and $later.Disposed -and -not $entry.settled -and $script:Failures.Count -gt 0) 'Failed owner was declared settled or later owner was abandoned'
                    } else {
                        Stop-Launch $entry $true
                        Stop-Launch $entry $true
                        Assert-Condition ($entry.settled -and $owner.Forces -eq 1 -and $owner.ActiveProcesses -eq 0 -and $entry.outcome -eq 'forced') 'Job settlement was not enforced'
                        if ($case -eq 'signal-error') { Assert-Condition ($owner.Signals -eq 1) 'Signal repeated after settlement' }
                    }
                } finally { $script:Ledger.Dispose() }
                $text = [IO.File]::ReadAllText((Join-Path $ownedRoot 'ledger.jsonl'))
                $events = @(Get-Content (Join-Path $ownedRoot 'ledger.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
                if ($case -eq 'force-error') {
                    Assert-Condition (@($events | Where-Object { $_.stage -eq 'launch.exited' -and $_.fields.id -eq 'owned' }).Count -eq 0) 'False settled ledger record'
                }
                if ($case -eq 'signal-error') {
                    $signal = @($events | Where-Object stage -eq 'launch.signal_failed')
                    Assert-Condition ($signal.Count -eq 1) 'Missing signal failure'
                    $native = @($signal[0].fields.errors | Where-Object type -eq 'System.ComponentModel.Win32Exception')
                    Assert-Condition ($native.Count -eq 2 -and $native.nativeErrorCode -contains 5 -and $native.nativeErrorCode -contains 6) 'Native failure branches lost'
                    Assert-Condition (-not $text.Contains('secret-canary')) 'Exception message leaked'
                }
            }
            """);
    }

    [Fact]
    public async Task LauncherNativeTableSeparatesListenersFromAcceptedConnections()
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunLauncherControl("""
            Initialize-NativeLauncher
            foreach ($address in @([Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback)) {
                $listener = New-Object Net.Sockets.TcpListener($address, 0)
                $connection = $null; $accepted = $null
                try {
                    $listener.Start()
                    $port = $listener.LocalEndpoint.Port
                    $connection = New-Object Net.Sockets.TcpClient($address.AddressFamily)
                    $connection.Connect($address, $port)
                    $accepted = $listener.AcceptTcpClient()
                    $rows = @([GsScenarioProcess]::Listeners() | Where-Object LocalPort -eq $port)
                    Assert-Condition ($rows.Count -eq 1 -and $rows[0].OwningProcess -eq $PID -and $rows[0].LocalAddress -eq $address.ToString()) 'Owned native listener missing or established connection misclassified'
                    $listener.Stop()
                    $rows = @([GsScenarioProcess]::Listeners() | Where-Object LocalPort -eq $port)
                    Assert-Condition ($rows.Count -eq 0) 'Closed listener or remaining accepted connection misclassified'
                } finally {
                    if ($null -ne $accepted) { $accepted.Dispose() }
                    if ($null -ne $connection) { $connection.Dispose() }
                    $listener.Stop()
                }
            }
            """);
    }

    [Fact]
    public async Task LauncherOverheadOffOmitsBothTraceVariablesWithoutChangingDefault()
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunLauncherControl("""
            $script:RunId = 'owned-run'
            $env:GS_BENCHMARK_RUN_ID = 'foreign-run'
            $env:GS_BENCHMARK_OUTPUT = 'foreign-output'
            $on = New-ChildEnvironment 'owned-output' 'owned-driver'
            $off = New-ChildEnvironment 'owned-output' 'owned-driver' $false
            Assert-Condition ($on.Contains("GS_BENCHMARK_RUN_ID=owned-run`0") -and $on.Contains("GS_BENCHMARK_OUTPUT=owned-output`0")) 'Default trace activation changed'
            Assert-Condition (-not $off.Contains('GS_BENCHMARK_RUN_ID=') -and -not $off.Contains('GS_BENCHMARK_OUTPUT=')) 'Off inherited trace activation'
            Assert-Condition ($off.Contains("GS_BENCHMARK_SCENARIO=owned-driver`0")) 'Off lost driver ownership configuration'
            """);
    }

    private async Task RunLauncherControl(string control)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "scripts", "run-scenarios.ps1"))) repository = repository.Parent;
        Assert.NotNull(repository);
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        string command = $". {Quote(Path.Combine(repository.FullName, "scripts", "run-scenarios.ps1"))} -LibraryOnly; $ownedRoot = {Quote(root)}; " + control;
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("PSModulePath");
        foreach (string arg in new[] { "-NoProfile", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    private const string NextScan = "55555555555555555555555555555555", NextRequest = "66666666666666666666666666666666";
    private void TwoBoundScans(string method)
    {
        ScanCase(method); BindTiny();
        bool concurrent = method == "ConcurrentRoots";
        long localOffset = concurrent ? 40 : 350;
        foreach (var source in backend.Where(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() is Scan or "http-" + Request).ToArray())
        {
            var copy = JsonNode.Parse(source.ToJsonString().Replace(Scan, NextScan).Replace(Request, NextRequest))!.AsObject();
            copy["localTicks"] = (long.Parse(copy["localTicks"]!.GetValue<string>()) + 100).ToString(); backend.Add(copy);
        }
        foreach (var source in client.Where(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() == Scan ||
            e["operationId"]?.GetValue<string>() == Request || e["label"]?.GetValue<string>()?.Replace("-", "") == Scan).ToArray())
        {
            var copy = JsonNode.Parse(source.ToJsonString().Replace(Scan, NextScan).Replace(Guid.Parse(Scan).ToString(), Guid.Parse(NextScan).ToString()).Replace(Request, NextRequest))!.AsObject();
            copy["localTicks"] = (long.Parse(copy["localTicks"]!.GetValue<string>()) + localOffset).ToString(); client.Add(copy);
        }
        var first = capture.Attempts[0].Scans[0] with { Role = concurrent ? "superseded" : "seed", SupersededByScanId = concurrent ? NextScan : null };
        var second = first with { ScanId = NextScan, RequestId = NextRequest, Role = "current", SupersededByScanId = null,
            FixtureName = concurrent ? "secondary" : "primary", SeedScanId = concurrent ? null : Scan,
            SetupKind = method == "AppHit" ? "app_hit" : concurrent ? "app_empty" : "changed_rescan" };
        if (concurrent)
        {
            string path = Path.Combine(Input, first.FixtureArtifact);
            var fixtures = JsonNode.Parse(File.ReadAllText(path))!;
            fixtures["secondary"] = fixtures["primary"]!.DeepClone(); fixtures["secondary"]!["root"] = "C:/owned/tiny-b";
            File.WriteAllText(path, fixtures.ToJsonString());
            second = second with { RootKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Backend + "\nC:/OWNED/TINY-B"))) };
            backend.Single(e => e["stage"]!.GetValue<string>() == "stream.request" && e["benchmarkTrace"]!["streamId"]!.GetValue<string>() == NextScan)["key"] = second.RootKey;
            client.RemoveAll(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() == Scan && e["stage"]!.GetValue<string>() == "state.directory.applied" ||
                e["stage"]!.GetValue<string>() == "scenario.snapshot" && e["label"]!.GetValue<string>() == Scan);
            client.Single(e => e["stage"]!.GetValue<string>() == "state.directory.terminal" && e["benchmarkTrace"]!["streamId"]!.GetValue<string>() == Scan)["outcome"] = "old_scan";
        }
        if (method == "ChangedRescan")
        {
            foreach (var e in backend.Concat(client).Where(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() == NextScan))
            {
                if (e["counts"]?["recursive_bytes"] != null) e["counts"]!["recursive_bytes"] = 98;
                if (e["stage"]!.GetValue<string>() == "stream.chunk") e["counts"]!["bytes"] = 98;
            }
            client.Single(e => e["stage"]!.GetValue<string>() == "scenario.snapshot" && e["label"]!.GetValue<string>() == NextScan)["counts"]!["a_bytes"] = 41;
        }
        if (method == "AppHit") backend.RemoveAll(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() == NextScan && e["stage"]!.GetValue<string>().StartsWith("scan.walk."));
        capture = capture with { Attempts = [capture.Attempts[0] with { ScanIds = [Scan, NextScan], RequestIds = [Request, NextRequest], Scans = [first, second] }] };
    }

    [Theory]
    [InlineData("AppHit", "none")]
    [InlineData("ChangedRescan", "none")]
    [InlineData("ChangedRescan", "bad-seed")]
    [InlineData("ChangedRescan", "bad-current")]
    [InlineData("ConcurrentRoots", "none")]
    [InlineData("ConcurrentRoots", "late-application")]
    public void BoundScanRolesKeepSeedAndSupersededOutputsTruthful(string method, string defect)
    {
        TwoBoundScans(method);
        if (defect.StartsWith("bad-")) client.Single(e => e["stage"]!.GetValue<string>() == "scenario.snapshot" &&
            e["label"]!.GetValue<string>() == (defect == "bad-seed" ? Scan : NextScan))["counts"]!["a_bytes"] = 0;
        if (defect == "late-application") AddClient("state.directory.applied", 302, source: Source(Scan, 40), counts: new() { ["nodes"] = 4 });
        Ordered(); Write(); string output = Output; var (manifest, evidence) = Import(output);
        var result = evidence.Cases.Single();
        Assert.Equal(defect == "none" ? "passed" : "failed", result.ContractChecks["scenario_outcome"]);
        Assert.Equal("not_checked", result.ContractChecks["scan_setup"]);
        Assert.Equal(defect == "none" ? "not_checked" : "failed", result.CorrectnessStatus);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unknown-role")]
    [InlineData("self-seed")]
    [InlineData("wrong-fixture")]
    [InlineData("seed-after-request")]
    public void BoundScanRolesRejectUnsupportedClaims(string defect)
    {
        TwoBoundScans("AppHit");
        var scans = capture.Attempts[0].Scans;
        scans[1] = defect switch { "unknown-role" => scans[1] with { Role = "complete" }, "self-seed" => scans[1] with { SeedScanId = NextScan },
            "wrong-fixture" => scans[1] with { FixtureName = "secondary" }, _ => scans[1] };
        if (defect == "seed-after-request") client.Single(e => e["stage"]!.GetValue<string>() == "state.directory.terminal" && e["benchmarkTrace"]!["streamId"]!.GetValue<string>() == Scan)["localTicks"] = "700";
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["scan_roles"]);
    }

    [Theory]
    [InlineData("AppEmpty", "none")]
    [InlineData("AppEmpty", "missing-walk")]
    [InlineData("AppEmpty", "missing-file")]
    [InlineData("AppEmpty", "cache-shortcut")]
    [InlineData("AppHit", "none")]
    [InlineData("AppHit", "fresh-walk")]
    public void BoundScanWorkRequiresColdTraversalAndWarmReuse(string method, string defect)
    {
        if (method == "AppHit") TwoBoundScans(method); else { ScanCase(); BindTiny(); }
        if (defect == "missing-walk") backend.RemoveAll(e => e["operationId"]?.GetValue<string>() == "walk-0");
        if (defect == "missing-file") backend.First(e => e["stage"]!.GetValue<string>() == "scan.walk.end")["counts"]!["observed_files"] = 1;
        if (defect == "cache-shortcut") backend.First(e => e["stage"]!.GetValue<string>() == "scan.walk.end")["outcome"] = "cache_hit";
        if (defect == "fresh-walk") ColdWalks(NextScan);
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["scan_work"]);
    }

    private void ColdSetup()
    {
        string prefix = "cases/" + Attempt + "/";
        string state = Path.Combine(Input, prefix, "state-after"); Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "benchmark-owner.json"), JsonSerializer.Serialize(new { schemaVersion = 1, runId = Run, attemptId = Attempt }));
        File.WriteAllText(Path.Combine(state, "automation_rules.json"), "[]");
        File.WriteAllText(Path.Combine(state, "appsettings.user.json"), JsonSerializer.Serialize(new {
            scan = new { depth = 50, excludedPaths = Array.Empty<string>(), followSymlinks = false, skipHiddenFiles = true, skipSystemFiles = true },
            monitoring = new { enableScheduledScans = false, ramPollIntervalMs = 1000 },
            cache = new { scanCacheTtlMinutes = 15, maxCacheScans = 5, maxCachedNodes = 50000 },
            advanced = new { backendPort = 5200, signalrReconnectDelaysMs = 1000, maxSignalrRetries = 10 } }));
        var inventory = Directory.GetFiles(state).Select(p => new { path = Path.GetFileName(p), bytes = new FileInfo(p).Length, sha256 = BenchmarkResults.Hash(p) });
        string before = prefix + "state-before.json";
        File.WriteAllText(Path.Combine(Input, before), JsonSerializer.Serialize(inventory));
        capture = capture with { Attempts = [capture.Attempts[0] with { Scans = capture.Attempts[0].Scans.Select(s => s with { SetupArtifacts = [before] }).ToArray() }] };
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("cache-present")]
    [InlineData("wrong-owner")]
    [InlineData("changed-settings")]
    public void BoundScanSetupChecksOwnedColdStateIndependently(string defect)
    {
        ScanCase(); BindTiny();
        if (defect != "missing") ColdSetup();
        string state = Path.Combine(Input, "cases", Attempt, "state-after");
        if (defect == "wrong-owner") File.WriteAllText(Path.Combine(state, "benchmark-owner.json"), "{}");
        if (defect == "changed-settings") File.WriteAllText(Path.Combine(state, "appsettings.user.json"), "{}");
        if (defect == "cache-present")
        {
            string path = Path.Combine(Input, "cases", Attempt, "state-before.json");
            var inventory = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            inventory.Add(new JsonObject { ["path"] = "scanner_memory.json", ["bytes"] = 2, ["sha256"] = Hash });
            File.WriteAllText(path, inventory.ToJsonString());
        }
        Ordered(); Write(); string output = Output; var (manifest, evidence) = Import(output);
        Assert.Equal(defect == "none" ? "passed" : defect == "missing" ? "not_checked" : "failed", evidence.Cases.Single().ContractChecks["scan_setup"]);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-launch")]
    [InlineData("unchanged")]
    [InlineData("wrong-file")]
    [InlineData("selection-before-control")]
    [InlineData("seed-after-control")]
    [InlineData("missing-request-marker")]
    [InlineData("missing-ack-marker")]
    [InlineData("wrong-action")]
    [InlineData("partial-artifacts")]
    [InlineData("control-before-interval")]
    public void ChangedSetupBindsTheMutationArtifactsToSeedAndLaterSelection(string defect)
    {
        TwoBoundScans("ChangedRescan"); ColdSetup();
        if (defect != "missing")
        {
            string command = new('7', 32), prefix = "cases/" + Attempt + "/control/" + command;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Input, prefix))!);
            File.WriteAllText(Path.Combine(Input, prefix + ".request.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, runId = defect == "wrong-owner" ? new('f', 32) : Run, attemptId = Attempt, commandId = command, action = "mutate" }));
            File.WriteAllText(Path.Combine(Input, prefix + ".ack.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, runId = Run, attemptId = Attempt, commandId = command, outcome = "ready",
                backendLaunchId = defect == "wrong-launch" ? new('f', 32) : Backend }));
            File.WriteAllText(Path.Combine(Input, prefix + ".fixture.json"), JsonSerializer.Serialize(StorageManifest.Tiny().Files.Select(f => {
                long bytes = f.Bytes + (f.Path == (defect == "wrong-file" ? "B/old.log" : "A/a.txt") && defect != "unchanged" ? 8 : 0);
                return new { path = f.Path, bytes, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[bytes])) };
            })));
            var scans = capture.Attempts[0].Scans;
            scans[1] = scans[1] with { SetupArtifacts = [..scans[1].SetupArtifacts, prefix + ".request.json", prefix + ".ack.json", prefix + ".fixture.json"] };
            AddClient("scenario.control.requested", defect == "selection-before-control" ? 560 : 400, operation: command); client[^1]["label"] = "mutate";
            AddClient("scenario.control.acknowledged", 580, operation: command); client[^1]["label"] = "mutate";
            if (defect == "wrong-action") client[^1]["label"] = "restart";
            if (defect == "missing-request-marker") client.RemoveAll(e => e["stage"]!.GetValue<string>() == "scenario.control.requested");
            if (defect == "missing-ack-marker") client.RemoveAll(e => e["stage"]!.GetValue<string>() == "scenario.control.acknowledged");
            if (defect == "seed-after-control") client.Single(e => e["stage"]!.GetValue<string>() == "state.directory.terminal" && e["benchmarkTrace"]!["streamId"]!.GetValue<string>() == Scan)["localTicks"] = "450";
            if (defect == "partial-artifacts") scans[1] = scans[1] with { SetupArtifacts = scans[1].SetupArtifacts.Where(p => !p.EndsWith(".ack.json")).ToArray() };
            if (defect == "control-before-interval") client.Single(e => e["stage"]!.GetValue<string>() == "scenario.ChangedRescan.start")["localTicks"] = "500";
        }
        Ordered(); Write(); string output = Output; var (manifest, evidence) = Import(output);
        Assert.Equal(defect is "none" or "control-before-interval" ? "passed" : defect == "missing" ? "not_checked" : "failed", evidence.Cases.Single().ContractChecks["scan_setup"]);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing-walk")]
    [InlineData("missing-files")]
    public void LargeZeroByteWorkRequiresIndependentTraversalCounts(string defect)
    {
        ScanCase(); BindTiny();
        backend.RemoveAll(e => e["stage"]!.GetValue<string>().StartsWith("scan.walk."));
        ColdWalks(Scan, large: true);
        capture = capture with { Attempts = [capture.Attempts[0] with { Scans = [capture.Attempts[0].Scans[0] with { FixtureName = "large" }] }] };
        if (defect == "missing-walk") backend.RemoveAll(e => e["operationId"]?.GetValue<string>() == "walk-0");
        if (defect == "missing-files") backend.Last(e => e["stage"]!.GetValue<string>() == "scan.walk.end")["counts"]!["observed_files"] = 99;
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["scan_work"]);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("replaced")]
    [InlineData("unsettled")]
    public void ProducerSessionTransitionsAreNotOperationSpans(string ending)
    {
        AddBackend("scan.session.start", 20, "started", source: Source(Scan, 1));
        if (ending != "unsettled") AddBackend("scan.session.end", 30, ending, source: Source(Scan, 2));
        if (ending == "replaced")
        {
            AddBackend("scan.session.start", 31, "started", source: Source(Scan, 3));
            AddBackend("scan.session.end", 40, "removed", source: Source(Scan, 4));
        }
        Write();
        var result = Assert.Single(Import().Evidence.Cases);
        Assert.Equal(ending == "unsettled" ? "failed" : "succeeded", result.ExecutionStatus);
        if (ending == "unsettled") Assert.Contains(result.ExecutionErrors, e => e.Contains("session"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("duplicate-receipt")]
    [InlineData("duplicate-send")]
    [InlineData("wrong-type")]
    public void CorrelationIndependentlyChecksSequenceAndTypedUniqueSources(string defect)
    {
        if (defect == "duplicate-receipt") AddClient("receive.RamUpdate", 135, "received", source: Metadata(2));
        if (defect == "duplicate-send")
        {
            AddBackend("send.RamUpdate.start", 12, "started", "second", Metadata(2));
            AddBackend("send.RamUpdate.end", 13, "succeeded", "second", Metadata(2));
        }
        if (defect == "wrong-type")
        {
            backend[1]["stage"] = "send.DirectoryChunk.start"; backend[2]["stage"] = "send.DirectoryChunk.end";
            client[4]["stage"] = "receive.DirectoryChunk";
        }
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["correlation"]);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("false-label")]
    [InlineData("duplicate")]
    [InlineData("missing-send")]
    public void AllocationOrderIsNotWireOrderButIdentityMustRemainUnique(string defect)
    {
        ScanCase(); BindTiny();
        var metadata = Source(Scan, 39);
        if (defect != "missing-send")
        {
            AddBackend("send.ScanProgress.start", 52, "started", "progress", metadata);
            AddBackend("send.ScanProgress.end", 53, "succeeded", "progress", metadata);
        }
        AddClient("receive.ScanProgress", 305, defect == "false-label" ? "received" : "out_of_order", source: metadata);
        if (defect == "duplicate") AddClient("receive.ScanProgress", 306, "out_of_order", source: metadata);
        Ordered(); Write();
        var result = Import().Evidence.Cases.Single();
        Assert.Equal(defect == "none" ? "passed" : "failed", result.ContractChecks["correlation"]);
        Assert.Equal("passed", result.ContractChecks["scenario_outcome"]);
        Assert.Equal("not_checked", result.CorrectnessStatus);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("omitted-membership")]
    [InlineData("status-disagreement")]
    public void HttpCorrelationRequiresActualMembershipAndMatchingStatuses(string defect)
    {
        client[6]["localTicks"] = "300";
        Http();
        if (defect != "omitted-membership") capture = capture with { Attempts = [capture.Attempts[0] with { RequestIds = [Request] }] };
        if (defect == "status-disagreement") client.Last(e => e["stage"]!.GetValue<string>() == "http.round_trip.end")["counts"]!["status"] = 503;
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["correlation"]);
    }

    [Theory]
    [InlineData("ScanIds")]
    [InlineData("RequestIds")]
    public void DuplicateAttemptMembershipIsRejected(string field)
    {
        capture = capture with { Attempts = [field == "ScanIds" ? capture.Attempts[0] with { ScanIds = [Scan, Scan] }
            : capture.Attempts[0] with { RequestIds = [Request, Request] }] };
        Write();
        Assert.Throws<InvalidDataException>(() => Import());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("error-terminal")]
    [InlineData("old-terminal")]
    [InlineData("failed-save")]
    [InlineData("abandoned")]
    public void ScanOutcomesDistinguishObservedFailuresFromUnavailableSetup(string defect)
    {
        ScanCase();
        if (defect == "error-terminal")
        {
            var terminal = backend.Single(e => e["stage"]!.GetValue<string>() == "stream.terminal");
            terminal["outcome"] = "failed"; terminal["counts"]!["has_summary"] = 0;
        }
        if (defect == "old-terminal") client.Single(e => e["stage"]!.GetValue<string>() == "state.directory.terminal")["outcome"] = "old_scan";
        if (defect == "failed-save") backend.Single(e => e["stage"]!.GetValue<string>() == "scan.save.end")["outcome"] = "failed";
        if (defect == "abandoned")
        {
            var counts = backend.Single(e => e["stage"]!.GetValue<string>() == "coalescer.summary")["counts"]!;
            counts["delivered"] = 3; counts["abandoned"] = 1;
        }
        Ordered(); Write();
        var result = Import().Evidence.Cases.Single();
        Assert.Equal(defect == "none" ? "not_checked" : "failed", result.ContractChecks["scenario_outcome"]);
        if (defect != "none") Assert.Equal("failed", result.CorrectnessStatus);
    }

    [Fact]
    public void UnknownEvictionLifetimeCannotPassOnNonCancellationAttempts()
    {
        Write(); backend[^1]["counts"]!["eviction_queue_unknown"] = 1;
        File.WriteAllLines(Path.Combine(Input, "backend.jsonl"), backend.Select(e => e.ToJsonString())); SaveInventory();
        Assert.Equal("not_checked", Import().Evidence.Cases.Single().ContractChecks["lifetime"]);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unavailable")]
    [InlineData("late-post-dispose")]
    public void HistoryAvailabilityAndPublicationAreCheckedThroughOwnedSettlement(string defect)
    {
        Method("HistoryRanges");
        var requests = new List<string>();
        int offset = 0;
        foreach (int minutes in new[] { 5, 15, 30, 60 })
        {
            string id = minutes.ToString("x32"); requests.Add(id); Http(id, minutes);
            AddClient("history.request.start", 180, "started", id);
            AddClient("history.requested", 190, operation: id, counts: new() { ["minutes"] = minutes, ["generation"] = minutes, ["in_flight"] = 1 });
            AddClient("history.publication_attempt", 230, "current", id, counts: new() { ["minutes"] = minutes, ["generation"] = minutes });
            AddClient("history.applied", 231, operation: id, counts: new() { ["minutes"] = minutes, ["generation"] = minutes });
            AddClient("history.request.end", 240, defect == "unavailable" && minutes == 60 ? "unavailable" : "returned", id);
            AddClient("history.settled", 250, operation: id, counts: new() { ["in_flight"] = 0 });
            foreach (var e in client.Where(e => e["operationId"]?.GetValue<string>() == id))
                e["localTicks"] = (long.Parse(e["localTicks"]!.GetValue<string>()) + offset).ToString();
            offset += 120;
        }
        if (defect == "late-post-dispose")
        {
            string late = new('9', 32); requests.Add(late); Http(late, 60);
            AddClient("history.request.start", 180, "started", late);
            AddClient("history.requested", 190, operation: late, counts: new() { ["minutes"] = 60, ["generation"] = 61, ["in_flight"] = 1 });
            AddClient("history.publication_attempt", 950, "post_dispose", late, counts: new() { ["minutes"] = 60, ["generation"] = 61 });
            AddClient("history.request.end", 951, "failed", late);
            AddClient("history.settled", 952, operation: late, counts: new() { ["in_flight"] = 0 });
            foreach (var e in client.Where(e => e["operationId"]?.GetValue<string>() == late && long.Parse(e["localTicks"]!.GetValue<string>()) < 900))
                e["localTicks"] = (long.Parse(e["localTicks"]!.GetValue<string>()) + 600).ToString();
        }
        capture = capture with { Attempts = [capture.Attempts[0] with { RequestIds = requests.ToArray() }] };
        Ordered(); Write();
        var result = Import().Evidence.Cases.Single();
        Assert.Equal(defect == "none" ? "not_checked" : "failed", result.CorrectnessStatus);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("empty-new-sample")]
    [InlineData("array-order")]
    [InlineData("rescue-start")]
    public void ReconnectRequiresTheSameFreshNonemptySampleAndNoRescue(string defect)
    {
        Method("Reconnect");
        string restart = new('f', 32);
        var source = Metadata(2); source["producerEpoch"] = restart; source["clockId"] = "dotnet-" + restart;
        AddClient("transport.reconnecting", 200, "interrupted");
        AddClient("scenario.backend_restart", 290, operation: Attempt); client[^1]["label"] = restart;
        AddClient("transport.reconnected", 300, "connected");
        AddClient("receive.RamUpdate", 310, "received", source: source);
        AddClient("state.ram.applied", 320, source: source, counts: new() { ["processes"] = defect == "empty-new-sample" ? 0 : 3 });
        capture = capture with { Attempts = [capture.Attempts[0] with { BackendLaunches = defect == "array-order" ? [restart, Backend] : [Backend, restart] }] };
        Ordered(); Write();
        var restarted = backend.Select(e => JsonNode.Parse(e.ToJsonString().Replace(Backend, restart))!.AsObject()).ToList();
        foreach (var e in restarted) e["processId"] = 44;
        if (defect == "rescue-start")
        {
            var route = restarted[1].DeepClone().AsObject(); route["stage"] = "http.route"; route["operationId"] = null;
            route["key"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(restart + "\nPOST /api/Telemetry/ram/start")));
            restarted.Insert(1, route);
            for (int i = 0; i < restarted.Count; i++) restarted[i]["eventId"] = (i + 1).ToString();
            restarted[^1]["counts"]!["written"] = restarted.Count - 1;
        }
        string path = Path.Combine(Input, "restarted.jsonl"); File.WriteAllLines(path, restarted.Select(e => e.ToJsonString()));
        capture = capture with { Launches = [..capture.Launches, new(restart, "backend", 44, restart, Hash, "restarted.jsonl", "exited", 0)],
            Artifacts = [..capture.Artifacts, new("restarted.jsonl", new FileInfo(path).Length, BenchmarkResults.Hash(path))] };
        BenchmarkResults.Write(Path.Combine(Input, "collection.json"), capture);
        Assert.Equal(defect is "none" or "array-order" ? "not_checked" : "failed", Import().Evidence.Cases.Single().CorrectnessStatus);
    }

    [Theory]
    [InlineData("ActiveCancelRestart", "none")]
    [InlineData("QueuedCancelRestart", "none")]
    [InlineData("ActiveCancelRestart", "inactive")]
    [InlineData("QueuedCancelRestart", "wrong-scan")]
    public void CancellationUsesReceiptPhaseAndAllowsSynchronousSettlementBeforeSignal(string method, string defect)
    {
        CancellationCase(method, defect);
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["cancellation"]);
    }

    private void CancellationCase(string method, string defect = "none")
    {
        ScanCase(method);
        foreach (var e in backend.Where(e => e["benchmarkTrace"]!["streamId"]!.GetValue<string>() is Scan or "http-" + Request))
            e["localTicks"] = (long.Parse(e["localTicks"]!.GetValue<string>()) + 200).ToString();
        string old = new('3', 32), request = new('4', 32);
        capture = capture with { Attempts = [capture.Attempts[0] with { ScanIds = [old, Scan], RequestIds = [request, Request] }] };
        Http(request);
        AddClient("state.directory.request", 151, operation: request); client[^1]["label"] = old;
        AddBackend("scan.session.start", 22, "started", source: Source(old, 1));
        long activeStart = method == "QueuedCancelRestart" ? 61 : 35;
        var queue = Source(old, 2); var active = Source(old, 3);
        AddBackend("scan.lock_queue.start", 25, "started", "old-queue", queue);
        AddBackend("scan.lock_queue.end", activeStart - 1, "acquired", "old-queue", queue);
        AddBackend("scan.active.start", activeStart, "started", "old-active", active);
        AddBackend("scan.active.end", 70, "cancelled", "old-active", active);
        long receipt = defect == "inactive" ? 80 : 50;
        AddBackend("scan.abort.controller_received", receipt, "observed", source: Source(old, 4));
        AddBackend("scan.abort.received", receipt + 1, "observed", source: Source(old, 5));
        AddBackend("scan.abort.signalled", receipt + 30, "observed", source: Source(defect == "wrong-scan" ? Scan : old, 6));
        AddBackend("scan.abort.controller_returned", receipt + 31, "observed", source: Source(old, 7));
        AddBackend("scan.session.end", 72, "removed", source: Source(old, 8));
        AddBackend("stream.terminal", 73, "cancelled", source: Source(old, 9), counts: new() { ["has_summary"] = 0 });
    }

    [Theory]
    [InlineData("ActiveCancelRestart", "none")]
    [InlineData("QueuedCancelRestart", "none")]
    [InlineData("QueuedCancelRestart", "missing-blocker")]
    [InlineData("QueuedCancelRestart", "inactive-blocker")]
    [InlineData("QueuedCancelRestart", "wrong-blocker-launch")]
    [InlineData("ActiveCancelRestart", "wrong-replacement")]
    [InlineData("ActiveCancelRestart", "early-replacement")]
    [InlineData("ActiveCancelRestart", "wrong-abort-request")]
    [InlineData("ActiveCancelRestart", "wrong-abort-route")]
    [InlineData("ActiveCancelRestart", "missing-service-cancelled")]
    [InlineData("ActiveCancelRestart", "slow-client-ack")]
    public void BoundCancellationRequiresExactRequestBlockerAndReplacement(string method, string defect)
    {
        CancellationCase(method);
        string target = new('3', 32), targetRequest = new('4', 32), abort = new('5', 32), blocker = new('6', 32);
        string fixture = "cases/" + Attempt + "/fixtures.json";
        var scans = new List<ScenarioScan> {
            new(target, targetRequest, Backend, Hash, fixture, method == "QueuedCancelRestart" ? "secondary" : "large", "cancel_target", "app_empty", []),
            new(Scan, Request, Backend, Hash, fixture, "primary", "current", "app_empty", []) };
        AddClient("state.directory.selected", 149); client[^1]["label"] = target;
        Http(abort);
        foreach (var e in client.Where(e => e["operationId"]?.GetValue<string>() == abort))
            e["localTicks"] = (long.Parse(e["localTicks"]!.GetValue<string>()) - 35).ToString();
        foreach (var e in backend.Where(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() == "http-" + abort))
        {
            string stage = e["stage"]!.GetValue<string>();
            e["localTicks"] = (stage == "http.handler.start" ? 48 : stage == "http.route" ? 49 : stage == "http.handler.end" ? 82 : 83).ToString();
            if (stage == "http.route") e["key"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Backend + "\nPOST /api/storage/abort-scan")));
        }
        backend.Single(e => e["stage"]!.GetValue<string>() == "scan.abort.controller_received")["operationId"] = abort;
        AddBackend("stream.service.start", 24, "started", "cancel-service", Source(target, 10));
        AddBackend("stream.service.end", 71, "cancelled", "cancel-service", Source(target, 10));
        if (method == "QueuedCancelRestart" && defect != "missing-blocker")
        {
            scans.Insert(0, new(blocker, new('7', 32), defect == "wrong-blocker-launch" ? new('f', 32) : Backend,
                Hash, fixture, "large", "blocker", "app_empty", []));
            AddBackend("scan.active.start", 10, "started", "blocker-active", Source(blocker, 1));
            AddBackend("scan.active.end", defect == "inactive-blocker" ? 40 : 60, "succeeded", "blocker-active", Source(blocker, 1));
        }
        if (defect == "wrong-replacement") scans[1] = scans[1] with { Role = "superseded" };
        if (defect == "early-replacement") client.Single(e => e["stage"]!.GetValue<string>() == "state.directory.selected" && e["label"]!.GetValue<string>() == Scan)["localTicks"] = "160";
        if (defect == "wrong-abort-request") backend.Single(e => e["stage"]!.GetValue<string>() == "scan.abort.controller_received")["operationId"] = targetRequest;
        if (defect == "wrong-abort-route") backend.Single(e => e["stage"]!.GetValue<string>() == "http.route" && e["benchmarkTrace"]!["streamId"]!.GetValue<string>() == "http-" + abort)["key"] = Hash;
        if (defect == "missing-service-cancelled") backend.Single(e => e["operationId"]?.GetValue<string>() == "cancel-service" && e["stage"]!.GetValue<string>() == "stream.service.end")["outcome"] = "succeeded";
        if (defect == "slow-client-ack") client.Single(e => e["stage"]!.GetValue<string>() == "http.round_trip.end" && e["operationId"]!.GetValue<string>() == abort)["localTicks"] = "800";
        capture = capture with { Attempts = [capture.Attempts[0] with { Scans = scans.ToArray(),
            ScanIds = scans.Select(s => s.ScanId).ToArray(), RequestIds = [..scans.Select(s => s.RequestId), abort] }] };
        Ordered(); Write(); string output = Output; var (manifest, evidence) = Import(output);
        Assert.Equal(defect == "none" ? "passed" : "failed", evidence.Cases.Single().ContractChecks["cancellation"]);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing-chunk")]
    [InlineData("missing-terminal")]
    [InlineData("missing-error")]
    public void BoundScanSendCoverageCannotHideAnUnreceivedEvent(string defect)
    {
        ScanCase(); BindTiny();
        if (defect == "missing-chunk") client.RemoveAll(e => e["stage"]!.GetValue<string>() is "receive.DirectoryChunk" or "state.directory.applied");
        if (defect == "missing-terminal") client.RemoveAll(e => e["stage"]!.GetValue<string>() is "receive.DirectoryStreamComplete" or "state.directory.terminal" or "scenario.snapshot");
        if (defect == "missing-error")
        {
            AddBackend("send.DirectoryStreamError.start", 70, "started", "error", Source(Scan, 50));
            AddBackend("send.DirectoryStreamError.end", 71, "succeeded", "error", Source(Scan, 50));
        }
        Ordered(); Write();
        Assert.Equal(defect == "none" ? "passed" : "failed", Import().Evidence.Cases.Single().ContractChecks["correlation"]);
    }

    [Fact]
    public void StageSummariesKeepNamedClockAndOutcomePopulationsAndReplayThem()
    {
        string failed = new('e', 32), timeout = new('f', 32);
        AddClient("scenario.Idle.start", 200, "started", failed); AddClient("scenario.Idle.end", 250, "failed", failed);
        AddClient("scenario.Idle.start", 300, "started", timeout); AddClient("scenario.Idle.end", 600, "timed_out", timeout);
        capture = capture with { Attempts = [capture.Attempts[0], capture.Attempts[0] with { Id = failed, Ordinal = 2, Outcome = "failed" },
            capture.Attempts[0] with { Id = timeout, Ordinal = 3, Outcome = "timed_out" }] };
        Write(); string output = Output; var (manifest, evidence) = Import(output);
        var path = Path.Combine(output, "stage-summaries.json");
        Assert.True(File.Exists(path), "Missing named stage summaries");
        var summaries = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        Assert.Contains(summaries, s => s!["stage"]!.GetValue<string>() == "send.RamUpdate" && s["clock_id"]!.GetValue<string>() == "dotnet-" + Backend);
        Assert.Equal(new[] { "failed", "succeeded", "timed_out" }, summaries.Where(s => s!["stage"]!.GetValue<string>() == "scenario.Idle")
            .Select(s => s!["outcome"]!.GetValue<string>()).Order());
        summaries[0]!["mean_elapsed_ms"] = 999;
        File.WriteAllText(path, summaries.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ScenarioResults.VerifyExecution(output, manifest, evidence, Profile));
    }

    [Theory]
    [InlineData("AppHit", false)]
    [InlineData("AppHit", true)]
    [InlineData("ChangedRescan", false)]
    [InlineData("ChangedRescan", true)]
    [InlineData("PersistedRestore", false)]
    [InlineData("PersistedRestore", true)]
    [InlineData("ConcurrentRoots", false)]
    [InlineData("ConcurrentRoots", true)]
    public void RemainingScanMethodsKeepMissingSetupDistinctFromWrongOutput(string method, bool corrupt)
    {
        ScanCase(method);
        if (method == "ChangedRescan")
        {
            backend.Single(e => e["stage"]!.GetValue<string>() == "stream.terminal")["counts"]!["recursive_bytes"] = 98;
            client.Single(e => e["stage"]!.GetValue<string>() == "state.directory.terminal")["counts"]!["recursive_bytes"] = 98;
            client.Single(e => e["stage"]!.GetValue<string>() == "scenario.snapshot")["counts"]!["a_bytes"] = 41;
        }
        if (method == "ConcurrentRoots")
        {
            string second = new('3', 32), request = new('4', 32);
            var remote = backend.Where(e => e["benchmarkTrace"]!["streamId"]!.GetValue<string>() is Scan or "http-" + Request).ToArray();
            var local = client.Where(e => e["benchmarkTrace"]?["streamId"]?.GetValue<string>() == Scan ||
                e["operationId"]?.GetValue<string>() == Request || e["stage"]!.GetValue<string>() == "scenario.snapshot").ToArray();
            foreach (var (records, values) in new[] { (backend, remote), (client, local) })
                foreach (var e in values)
                    records.Add(JsonNode.Parse(e.ToJsonString().Replace(Scan, second).Replace(Request, request)
                        .Replace(Guid.ParseExact(Scan, "N").ToString(), Guid.ParseExact(second, "N").ToString()))!.AsObject());
            capture = capture with { Attempts = [capture.Attempts[0] with { ScanIds = [Scan, second], RequestIds = [Request, request] }] };
        }
        if (corrupt) client.First(e => e["stage"]!.GetValue<string>() == "scenario.snapshot")["counts"]!["a_bytes"] = 0;
        Ordered(); Write();
        string output = Output; var (manifest, evidence) = Import(output);
        var result = Assert.Single(evidence.Cases);
        Assert.Equal("passed", result.ContractChecks["correlation"]);
        Assert.Equal(corrupt ? "failed" : "not_checked", result.ContractChecks["scenario_outcome"]);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualBackendRecorderJsonImportsAndReplays(bool rich)
    {
        var writer = new StringWriter();
        var trace = new BenchmarkTrace(Run, writer, rich: rich);
        BenchmarkMetadata source;
        using (BenchmarkTrace.Use(trace, "ram"))
        {
            source = trace.Metadata("ram", BenchmarkTrace.Ticks, BenchmarkTrace.Ticks);
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync("RamUpdate", It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            await BenchmarkTrace.Send(proxy.Object, "RamUpdate", new { processes = new[] { new { pid = 7 } } }, metadata: source);
            trace.Emit("scan.session.start", "started", trace.Metadata(Scan));
            trace.Emit("scan.session.end", "removed", trace.Metadata(Scan));
        }
        await trace.DisposeAsync();
        Assert.False(trace.WriteFailed);
        Write();
        File.WriteAllText(Path.Combine(Input, "backend.jsonl"), writer.ToString());
        foreach (var e in client.Where(e => e["benchmarkTrace"] != null))
            e["benchmarkTrace"] = JsonSerializer.SerializeToNode(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        client[0]["counts"]!["rich"] = rich ? 1 : 0;
        File.WriteAllLines(Path.Combine(Input, "client.jsonl"), client.Select(e => e.ToJsonString()));
        capture = capture with { Detail = rich ? "rich" : "minimal", Launches = [capture.Launches[0] with
            { Epoch = trace.Epoch, ProcessId = Environment.ProcessId }, capture.Launches[1]] };
        SaveInventory();
        string output = Output; var (manifest, evidence) = Import(output);
        Assert.Equal("succeeded", Assert.Single(evidence.Cases).ExecutionStatus);
        Assert.Equal("passed", evidence.Cases[0].ContractChecks["correlation"]);
        ScenarioResults.VerifyExecution(output, manifest, evidence, Profile);
        Assert.Contains(ThresholdValidator.Read<ScenarioStageSummary[]>(Path.Combine(output, "stage-summaries.json")),
            s => s.Stage == "send.RamUpdate" && s.FrequencyHz == Stopwatch.Frequency && s.SampleCount == 1);
    }

    [Fact, Trait("Category", "Cli")]
    public async Task RawImportAndReplayPreserveInputsAndRejectAlteredStageSummaries()
    {
        Write();
        string[] Snapshot(string path) => Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories).Order()
            .Select(p => Path.GetRelativePath(path, p) + (File.Exists(p) ? ":" + BenchmarkResults.Hash(p) + ":" + File.GetLastWriteTimeUtc(p).Ticks : ":directory")).ToArray();
        async Task<(string Root, string Text)> Invoke(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            start.Environment["CoreRepoPath"] = Path.Combine(root, "absent-core");
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (string arg in new[] { "--mode", "report", "--profile", "transport-mock", "--output", Output }.Concat(arguments)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            string text = await stdout + "\n" + await stderr;
            Assert.True(process.ExitCode == 1, text);
            return (text.Split('\n').First(l => l.StartsWith("Evidence: "))[10..].Trim(), text);
        }
        string[] before = Snapshot(Input);
        var imported = await Invoke("--import", Input);
        Assert.False(File.Exists(Path.Combine(imported.Root, "failure.json")), imported.Text);
        Assert.Equal(before, Snapshot(Input));
        var manifest = ThresholdValidator.Read<RunManifest>(Path.Combine(imported.Root, "manifest.json"));
        Assert.Equal(capture.EndedUtc, manifest.EndedUtc);
        BenchmarkResults.VerifyInventory(imported.Root, manifest);
        var result = ThresholdValidator.Read<RunEvidence>(Path.Combine(imported.Root, "case-results.json"));
        Assert.Equal("succeeded", Assert.Single(result.Cases).ExecutionStatus);
        before = Snapshot(imported.Root);
        var replay = await Invoke("--evidence", imported.Root);
        Assert.Contains("Read-only evidence validation", replay.Text);
        Assert.False(File.Exists(Path.Combine(replay.Root, "failure.json")), replay.Text);
        Assert.Equal(before, Snapshot(imported.Root));
        string stages = Path.Combine(imported.Root, "stage-summaries.json");
        File.WriteAllText(stages, "[]");
        manifest = manifest with { Artifacts = manifest.Artifacts.Select(a => a.Path == "stage-summaries.json"
            ? a with { Bytes = new FileInfo(stages).Length, Sha256 = BenchmarkResults.Hash(stages) } : a).ToArray() };
        BenchmarkResults.Write(Path.Combine(imported.Root, "manifest.json"), manifest);
        BenchmarkResults.VerifyInventory(imported.Root, manifest);
        Assert.Contains("stage summary replay mismatch", (await Invoke("--evidence", imported.Root)).Text);
        File.Delete(stages);
        Assert.Contains("Benchmark invocation failed", (await Invoke("--evidence", imported.Root)).Text);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
