using System.Buffers.Binary;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Reports;
using GSAnalyzer.Benchmarks.Models;
using GSSystemAnalyzer.Engine;
using GSSystemAnalyzer.Hubs;
using GSSystemAnalyzer.Interfaces;
using GSSystemAnalyzer.Models;
using GSSystemAnalyzer.Models.SettingDtos;
using GSSystemAnalyzer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GSAnalyzer.Benchmarks.Core;

public sealed record ScenarioLaunch(string Id, string Role, int ProcessId, string Epoch,
    string ArtifactSha256, string Trace, string Outcome, int? ExitCode);
public sealed record ScenarioAttempt(string Id, string Method, int Ordinal, string ClientLaunch,
    string[] BackendLaunches, string[] ScanIds, string[] RequestIds, string Outcome)
{
    public ScenarioScan[] Scans { get; init; } = [];
}
public sealed record ScenarioScan(string ScanId, string RequestId, string BackendLaunch, string RootKey,
    string FixtureArtifact, string FixtureName, string Role, string SetupKind, string[] SetupArtifacts,
    string? SeedScanId = null, string? SupersededByScanId = null);
public sealed record ScenarioCapture(int SchemaVersion, string RunId, string ProfileId, string Detail,
    DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, SourceIdentity Core, SourceIdentity Harness,
    Dictionary<string, string> Environment, ScenarioLaunch[] Launches, ScenarioAttempt[] Attempts,
    ArtifactEntry[] Artifacts);
public sealed record ScenarioStageSummary(string Launch, string Role, string ClockId, long FrequencyHz,
    string Stage, string Outcome, string Unit, string Statistic, long SampleCount, double MeanElapsedMs, string Basis);

public static class ScenarioResults
{
    public static readonly string[] Methods = ["Idle", "AppEmpty", "AppHit", "ChangedRescan", "PersistedRestore",
        "ConcurrentRoots", "ActiveCancelRestart", "QueuedCancelRestart", "HistoryRanges", "Reconnect"];
    private static readonly JsonSerializerOptions WireJson = new(ThresholdValidator.Json)
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };
    private sealed record Wire(string EventId, int ProcessId, string Stage, string LocalTicks, string Outcome)
    {
        public int? SchemaVersion { get; init; }
        public string? RunId { get; init; }
        public string? ProducerEpoch { get; init; }
        public string? ClockId { get; init; }
        public string? FrequencyHz { get; init; }
        public BenchmarkMetadata? BenchmarkTrace { get; init; }
        public string? OperationId { get; init; }
        public Dictionary<string, long>? Counts { get; init; }
        public string? Key { get; init; }
        public string? Label { get; init; }
    }
    private sealed record Event(Wire Wire, string Launch, string Role, string Epoch, string Clock, long Frequency, long Ticks)
    {
        public string Stage => Wire.Stage;
        public string Outcome => Wire.Outcome;
        public string? Stream => Wire.BenchmarkTrace?.StreamId;
        public long Count(string key) => Wire.Counts?.GetValueOrDefault(key, -1) ?? -1;
        public string Pair => string.Join("/", Launch, Epoch, Stream, Wire.OperationId,
            Stage.EndsWith(".start", StringComparison.Ordinal) ? Stage[..^6] : Stage[..^4]);
    }
    private sealed record Span(Event Start, Event End)
    {
        public string Stage => Start.Stage[..^6];
        public double Milliseconds => (End.Ticks - Start.Ticks) * 1000.0 / Start.Frequency;
    }
    private sealed record Trace(ScenarioLaunch Launch, Event[] Events, Span[] Spans, string[] Errors);
    private sealed record Fixture(string Id, string Root, string[] Directories, StorageFile[] Files, ArtifactEntry[] Inventory,
        string[] ObservedDirectories, DateTimeOffset? OriginUtc = null);

    public static CaseIdentity[] Discover(BenchmarkProfile profile)
    {
        Require(profile.CollectionKind == "scenario" && profile.Id is "transport-mock" or "transport-real", "Unregistered scenario profile.");
        bool real = profile.Id == "transport-real";
        string type = "GSAnalyzer.Benchmarks.Scenarios." + (real ? "RealTransport" : "MockTransport");
        Require(profile.Types.SequenceEqual([type]), "Unregistered scenario type.");
        Require(profile.Purpose == (real ? "performance" : "contract"), "Scenario purpose mismatch.");
        return Methods.Select(method => new CaseIdentity { Type = type, Method = method, Parameters = new(),
            Job = "scenario-windows-x64", Runtime = "dotnet-flutter", Tfm = "net10.0-windows+flutter", Architecture = "X64",
            Scenario = "transport/" + method, CacheTopology = real ? "loopback-kestrel" : "mock",
            DatasetId = "tiny-v1+scanner-50k-v1" }).ToArray();
    }

    public static (RunManifest Manifest, RunEvidence Evidence) Import(BenchmarkProfile profile, string input,
        string output, string configHash, string mode)
    {
        Require(Outside(input, output), "Scenario output must be outside input.");
        var capture = ReadCapture(input);
        ValidateCapture(capture, profile);
        string raw = Path.Combine(output, "raw");
        Directory.CreateDirectory(raw);
        File.Copy(SafeFile(input, "collection.json"), Path.Combine(raw, "collection.json"));
        foreach (var artifact in capture.Artifacts)
        {
            string source = SafeFile(input, artifact.Path);
            Require(new FileInfo(source).Length == artifact.Bytes && BenchmarkResults.Hash(source) == artifact.Sha256,
                "Scenario artifact changed: " + artifact.Path);
            string target = Path.Combine(raw, artifact.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        var evidence = Derive(profile, raw, configHash, out var stages);
        BenchmarkResults.Write(Path.Combine(output, "stage-summaries.json"), stages);
        var manifest = new RunManifest(1, capture.RunId, profile.Id, profile.Purpose, mode, capture.StartedUtc,
            capture.EndedUtc, capture.Core, capture.Harness, configHash,
            BenchmarkResults.DatasetHash(profile.Cases.Select(c => c.Identity)), capture.Environment, []) { CollectionKind = "scenario" };
        return (manifest, evidence);
    }

    public static void VerifyExecution(string root, RunManifest manifest, RunEvidence evidence, BenchmarkProfile profile)
    {
        Require(manifest.CollectionKind == "scenario" && evidence.CollectionKind == "scenario" &&
            profile.CollectionKind == "scenario", "Scenario source mismatch.");
        if (manifest.Mode == "discover")
        {
            Require(evidence.Cases.Length == 0 && evidence.Discovered.Select(c => c.Id).SequenceEqual(Discover(profile).Select(c => c.Id)),
                "Scenario discovery mismatch.");
            return;
        }
        string raw = Path.Combine(root, "raw");
        var capture = ReadCapture(raw);
        Require(capture.RunId == manifest.RunId && capture.Core == manifest.Core && capture.Harness == manifest.Harness &&
            capture.StartedUtc == manifest.StartedUtc && capture.EndedUtc == manifest.EndedUtc &&
            capture.Environment.OrderBy(p => p.Key).SequenceEqual(manifest.Environment.OrderBy(p => p.Key)),
            "Scenario launch provenance mismatch.");
        var derived = Derive(profile, raw, manifest.ConfigSha256, out var stages);
        Require(JsonSerializer.Serialize(ThresholdValidator.Read<ScenarioStageSummary[]>(Path.Combine(root, "stage-summaries.json")), ThresholdValidator.Json) ==
            JsonSerializer.Serialize(stages, ThresholdValidator.Json), "Scenario stage summary replay mismatch.");
        // Evaluation owns budget reasons/decisions; replay owns every measurement and correctness field.
        RunEvidence MeasurementFields(RunEvidence value) => value with { Cases = value.Cases.Select(c => c with
            { Metrics = c.Metrics.Select(m => m with { Reason = "", GateDecision = "" }).ToArray() }).ToArray() };
        Require(JsonSerializer.Serialize(MeasurementFields(evidence), ThresholdValidator.Json) ==
            JsonSerializer.Serialize(MeasurementFields(derived), ThresholdValidator.Json), "Scenario semantic replay mismatch.");
    }

    public static bool Outside(string input, string output)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(input), Path.GetFullPath(output));
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string SafeFile(string root, string relative)
    {
        Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) && !relative.Contains('\\') &&
            relative.Split('/').All(p => p.Length > 0 && p is not ("." or "..") && !p.Contains(':')), "Invalid scenario artifact path.");
        string fullRoot = Path.GetFullPath(root);
        Require(Directory.Exists(fullRoot), "Missing scenario directory.");
        for (var parent = new DirectoryInfo(fullRoot); parent != null; parent = parent.Parent)
            Require((parent.Attributes & FileAttributes.ReparsePoint) == 0, "Linked scenario directory.");
        string path = fullRoot;
        foreach (string part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Linked scenario artifact.");
        }
        Require(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 268435456, "Empty/oversized scenario artifact.");
        return path;
    }

    private static ScenarioCapture ReadCapture(string root) => ThresholdValidator.Read<ScenarioCapture>(SafeFile(root, "collection.json"));
    private static bool Hex(string value, int size) => value.Length == size && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static long Number(string text, bool positive = false)
    {
        Require(text.Length is > 0 and <= 20 && text.All(char.IsAsciiDigit) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long _), "Invalid decimal-string clock/sequence.");
        long value = long.Parse(text, CultureInfo.InvariantCulture);
        Require(value >= (positive ? 1 : 0), "Invalid clock/sequence domain.");
        return value;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void ValidateCapture(ScenarioCapture capture, BenchmarkProfile profile)
    {
        Discover(profile);
        Require(capture.SchemaVersion == 1 && Hex(capture.RunId, 32) && capture.ProfileId == profile.Id &&
            capture.Detail is "minimal" or "rich" && capture.StartedUtc <= capture.EndedUtc, "Invalid scenario capture.");
        Require(Hex(capture.Core.Sha, 40) && Hex(capture.Harness.Sha, 40), "Missing collection source identities.");
        foreach (string key in new[] { "backend_sha256", "client_sha256", "core_source_sha256", "harness_source_sha256" })
            Require(capture.Environment.TryGetValue(key, out string? value) && Hex(value, 64), "Missing collection artifact/source hash: " + key);
        foreach (string key in new[] { "sdk", "flutter", "dart", "os", "architecture", "tfm", "environment_id" })
            Require(capture.Environment.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value), "Missing collection environment: " + key);
        Require(capture.Environment["architecture"] == "X64" && capture.Environment["tfm"] == "net10.0-windows" &&
            capture.Environment["os"] == "Windows", "Unsupported scenario platform.");
        Require(capture.Artifacts.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == capture.Artifacts.Length &&
            capture.Artifacts.All(a => a.Path != "collection.json" && a.Bytes > 0 && Hex(a.Sha256, 64)), "Invalid scenario inventory.");
        Require(capture.Launches.Select(l => l.Id).Distinct().Count() == capture.Launches.Length &&
            capture.Launches.Select(l => l.Epoch).Distinct().Count() == capture.Launches.Length &&
            capture.Launches.Select(l => l.Trace).Distinct(StringComparer.OrdinalIgnoreCase).Count() == capture.Launches.Length, "Duplicate scenario launch.");
        foreach (var launch in capture.Launches)
            Require(Hex(launch.Id, 32) && Hex(launch.Epoch, 32) && launch.ProcessId > 0 && launch.Role is "backend" or "client" &&
                launch.ArtifactSha256 == capture.Environment[launch.Role + "_sha256"] &&
                launch.Outcome is "exited" or "forced" or "failed" &&
                capture.Artifacts.Any(a => a.Path == launch.Trace), "Invalid scenario launch provenance.");
        Require(capture.Attempts.Select(a => a.Id).Distinct().Count() == capture.Attempts.Length, "Duplicate scenario attempt.");
        foreach (var attempt in capture.Attempts)
        {
            Require(Hex(attempt.Id, 32) && Methods.Contains(attempt.Method) && attempt.Ordinal > 0 &&
                attempt.Outcome is "succeeded" or "failed" or "timed_out" &&
                attempt.ScanIds.All(s => Hex(s, 32)) && attempt.RequestIds.All(s => Hex(s, 32)) &&
                attempt.ScanIds.Distinct().Count() == attempt.ScanIds.Length && attempt.RequestIds.Distinct().Count() == attempt.RequestIds.Length &&
                attempt.BackendLaunches.Distinct().Count() == attempt.BackendLaunches.Length, "Invalid scenario attempt.");
        }
        foreach (var group in capture.Attempts.GroupBy(a => a.Method))
            Require(group.Select(a => a.Ordinal).Order().SequenceEqual(Enumerable.Range(1, group.Count())), "Missing/duplicate attempt ordinal.");
    }

    private static Trace ReadTrace(string root, ScenarioCapture capture, ScenarioLaunch launch)
    {
        string path = SafeFile(root, launch.Trace);
        var artifact = capture.Artifacts.Single(a => a.Path == launch.Trace);
        Require(new FileInfo(path).Length == artifact.Bytes && BenchmarkResults.Hash(path) == artifact.Sha256, "Trace hash mismatch.");
        var events = new List<Event>();
        var errors = new List<string>();
        var ids = new HashSet<string>();
        foreach (string line in File.ReadLines(path))
        {
            Require(line.Length is > 0 and <= 131072 && events.Count < 1000000, "Invalid trace record size/count.");
            using var json = JsonDocument.Parse(line);
            ThresholdValidator.RejectDuplicates(json.RootElement);
            var wire = json.RootElement.Deserialize<Wire>(WireJson) ?? throw new InvalidDataException("Null trace record.");
            Require(wire.ProcessId == launch.ProcessId && ids.Add(wire.EventId) && Number(wire.EventId, true) > 0 &&
                wire.Stage.Length is > 0 and <= 128 && wire.Outcome.Length is > 0 and <= 128 &&
                (wire.OperationId == null || wire.OperationId.Length is > 0 and <= 128) &&
                (wire.Label == null || wire.Label.Length <= 128), "Invalid trace identity.");
            var metadata = wire.BenchmarkTrace;
            if (metadata != null)
            {
                Require(metadata.SchemaVersion == 1 && metadata.RunId == capture.RunId && Hex(metadata.ProducerEpoch, 32) &&
                    metadata.StreamId.Length is > 0 and <= 128 && metadata.ClockId == "dotnet-" + metadata.ProducerEpoch,
                    "Invalid source metadata.");
                Number(metadata.Sequence, true); Number(metadata.FrequencyHz, true);
                Require((metadata.AcquisitionStartTicks == null) == (metadata.AcquisitionEndTicks == null), "Incomplete acquisition pair.");
                if (metadata.AcquisitionStartTicks != null)
                    Require(Number(metadata.AcquisitionEndTicks!) >= Number(metadata.AcquisitionStartTicks), "Reversed acquisition pair.");
            }
            bool backend = launch.Role == "backend";
            string epoch = backend ? metadata?.ProducerEpoch ?? "" : wire.ProducerEpoch ?? "";
            string clock = backend ? metadata!.ClockId : wire.ClockId ?? "";
            long frequency = Number(backend ? metadata!.FrequencyHz : wire.FrequencyHz ?? "", true);
            Require(epoch == launch.Epoch && clock == (backend ? "dotnet-" : "dart-") + epoch &&
                (backend ? wire.RunId == null && wire.SchemaVersion == null : wire.RunId == capture.RunId && wire.SchemaVersion == 1),
                "Mixed trace run/epoch/clock.");
            events.Add(new(wire, launch.Id, launch.Role, epoch, clock, frequency, Number(wire.LocalTicks)));
        }
        Require(events.Count > 0 && events[0].Stage == "trace.start", "Missing trace start.");
        Require(events.All(e => e.Frequency == events[0].Frequency), "Mixed local clock frequency.");
        if (!launch.Trace.EndsWith(".jsonl", StringComparison.Ordinal)) errors.Add("uncommitted trace");
        if (events[^1].Stage != "trace.end") errors.Add("missing trace footer");
        else
        {
            var footer = events[^1];
            if (footer.Outcome != "drained" || footer.Count("dropped") != 0 || footer.Count("outstanding") != 0 ||
                launch.Role == "client" && footer.Count("owned_tasks") != 0 ||
                footer.Count("written") != events.Count - 1) errors.Add("incomplete trace/footer counts");
            Require(events.Count(e => e.Stage == "trace.end") == 1, "Duplicate trace footer.");
        }
        var starts = new Dictionary<string, Event>();
        var spans = new List<Span>();
        var sessions = new Dictionary<string, Event>();
        foreach (var e in events.Where(e => e.Stage is "scan.session.start" or "scan.session.end"))
        {
            Require(launch.Role == "backend" && e.Stream != null && Hex(e.Stream, 32) && e.Wire.OperationId == null,
                "Invalid session transition identity.");
            if (e.Stage == "scan.session.start")
                Require(e.Outcome == "started" && sessions.TryAdd(e.Stream!, e), "Duplicate session start.");
            else
                Require(e.Outcome is "removed" or "replaced" && sessions.Remove(e.Stream!, out var start) && start.Ticks <= e.Ticks,
                    "Unmatched session end.");
        }
        if (sessions.Count > 0) errors.Add("unsettled sessions");
        foreach (var e in events.Where(e => e.Stage is not ("trace.start" or "trace.end" or "scan.session.start" or "scan.session.end") &&
            (e.Stage.EndsWith(".start") || e.Stage.EndsWith(".end"))))
        {
            Require(e.Wire.OperationId != null, "Span operation missing.");
            string key = e.Pair;
            if (e.Stage.EndsWith(".start")) Require(starts.TryAdd(key, e), "Duplicate span start.");
            else
            {
                Require(starts.Remove(key, out var start) && start.Ticks <= e.Ticks && start.Clock == e.Clock &&
                    start.Frequency == e.Frequency && start.Wire.BenchmarkTrace == e.Wire.BenchmarkTrace, "Unmatched span end/clock/metadata.");
                spans.Add(new(start!, e));
            }
        }
        if (starts.Count > 0) errors.Add("unsettled spans");
        return new(launch, events.ToArray(), spans.ToArray(), errors.ToArray());
    }

    private static RunEvidence Derive(BenchmarkProfile profile, string raw, string configHash, out ScenarioStageSummary[] stages)
    {
        var capture = ReadCapture(raw);
        ValidateCapture(capture, profile);
        var discovered = Discover(profile);
        var traces = capture.Launches.ToDictionary(l => l.Id, l => ReadTrace(raw, capture, l));
        stages = traces.Values.SelectMany(t => t.Spans).GroupBy(s => new { s.Start.Launch, s.Start.Role, s.Start.Clock,
            s.Start.Frequency, s.Stage, s.End.Outcome }).OrderBy(g => g.Key.Launch, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Stage, StringComparer.Ordinal).ThenBy(g => g.Key.Outcome, StringComparer.Ordinal)
            .Select(g => new ScenarioStageSummary(g.Key.Launch, g.Key.Role, g.Key.Clock, g.Key.Frequency, g.Key.Stage, g.Key.Outcome,
                "ms", "mean_elapsed_ms", g.LongCount(), g.Average(s => s.Milliseconds),
                g.Key.Outcome == "timed_out" ? "Complete local deadline intervals; not successful completion latency."
                    : "Complete same-clock span pairs across this launch, grouped by outcome; missing pairs are excluded and invalidate evidence.")).ToArray();
        var results = new List<CaseResult>();
        foreach (var identity in discovered)
        {
            var attempts = capture.Attempts.Where(a => a.Method == identity.Method).OrderBy(a => a.Ordinal).ToArray();
            if (attempts.Length == 0) continue;
            var errors = new List<string>();
            var durations = new List<double>();
            var launches = new HashSet<string>();
            bool correlated = true, integrity = true, lifetime = true;
            var outcomes = new List<string>();
            var cancellations = new List<string>();
            var bindings = new List<string>();
            var roles = new List<string>();
            var setups = new List<string>();
            var work = new List<string>();
            foreach (var attempt in attempts)
            {
                string[] selected = [attempt.ClientLaunch, .. attempt.BackendLaunches];
                foreach (string id in selected.Where(traces.ContainsKey)) launches.Add(id);
                if (!traces.TryGetValue(attempt.ClientLaunch, out var client) || client.Launch.Role != "client" ||
                    attempt.BackendLaunches.Length == 0 || attempt.BackendLaunches.Any(id => !traces.TryGetValue(id, out var t) || t.Launch.Role != "backend"))
                { errors.Add(attempt.Id + ": missing owned launch"); integrity = false; lifetime = false; outcomes.Add("not_checked"); continue; }
                var owned = selected.Select(id => traces[id]).ToArray();
                errors.AddRange(owned.Where(t => t.Launch.Outcome != "exited" || t.Launch.ExitCode != 0)
                    .Select(t => attempt.Id + ": " + t.Launch.Role + ": unsuccessful process exit"));
                if (owned.Any(t => t.Errors.Length > 0))
                {
                    integrity = false;
                    errors.AddRange(owned.SelectMany(t => t.Errors.Select(e => attempt.Id + ": " + t.Launch.Role + ": " + e)));
                }
                var clientLifetime = client.Events.Where(e => e.Stage == "client.lifetime").ToArray();
                lifetime &= owned.All(t => t.Errors.Length == 0) && owned.Where(t => t.Launch.Role == "backend")
                    .All(t => t.Events[^1].Stage == "trace.end" && t.Events[^1].Count("eviction_queue_unknown") == 0) &&
                    clientLifetime.Length == 1 && clientLifetime[0].Outcome == "settled" &&
                    clientLifetime[0].Count("owned_resources_settled") == 1 && clientLifetime[0].Count("transport_internal_unknown") == 0 &&
                    client.Events.Any(e => e.Stage == "transport.dispose" && e.Outcome == "disconnected" && e.Ticks <= clientLifetime[0].Ticks) &&
                    !client.Events.Any(e => e.Stage != "trace.end" && e.Ticks > clientLifetime[0].Ticks) &&
                    !client.Events.Any(e => e.Stage == "transport.dispose" && e.Outcome != "disconnected");
                bool asynchronousFailure = client.Events.Any(e => e.Stage == "scenario.async_error");
                if (asynchronousFailure) errors.Add(attempt.Id + ": client recorded failure (scenario or cleanup)");
                var starts = client.Events.Where(e => e.Stage == "scenario." + attempt.Method + ".start" && e.Wire.OperationId == attempt.Id).ToArray();
                var ends = client.Events.Where(e => e.Stage == "scenario." + attempt.Method + ".end" && e.Wire.OperationId == attempt.Id).ToArray();
                if (starts.Length != 1 || ends.Length != 1)
                { errors.Add(attempt.Id + ": missing scenario interval"); integrity = false; outcomes.Add("not_checked"); continue; }
                var start = starts.Single(); var end = ends.Single();
                Require(end.Ticks >= start.Ticks && end.Outcome == attempt.Outcome, "Scenario attempt outcome/clock mismatch.");
                durations.Add((end.Ticks - start.Ticks) * 1000.0 / start.Frequency);
                if (attempt.Outcome != "succeeded") errors.Add(attempt.Id + ": " + attempt.Outcome);
                var interval = client.Events.Where(e => e.Ticks >= start.Ticks && e.Ticks <= end.Ticks).ToArray();
                var local = client.Events.Where(e => interval.Contains(e) || attempt.RequestIds.Contains(e.Wire.OperationId) ||
                    attempt.ScanIds.Contains(e.Stream) || attempt.ScanIds.Any(id => ScanLabel(e, id)) ||
                    attempt.Method == "HistoryRanges" && e.Stage == "history.dispose").ToArray();
                var backend = owned.Where(t => t.Launch.Role == "backend").SelectMany(t => t.Events).ToArray();
                bool joined = Correlate(local, client.Events, backend, attempt);
                correlated &= joined;
                bindings.Add(ScanBinding(attempt, local, backend, capture, raw));
                roles.Add(ScanRoles(attempt, local));
                setups.Add(ScanSetup(attempt, client.Events, capture, raw));
                work.Add(ScanWork(attempt, backend));
                string cancellation = attempt.Method.Contains("Cancel", StringComparison.Ordinal)
                    ? Cancellation(attempt, local, backend, owned.SelectMany(t => t.Spans).ToArray()) : "not_checked";
                cancellations.Add(cancellation);
                outcomes.Add(asynchronousFailure ? "failed" : CheckOutcome(attempt, local, backend, capture, cancellation));
            }
            string outcome = Combine(outcomes);
            string binding = Combine(bindings);
            string role = Combine(roles);
            string setup = Combine(setups), scanWork = Combine(work);
            bool storage = identity.Method is not ("Idle" or "Reconnect" or "HistoryRanges");
            string correctness = outcome == "failed" || new[] { binding, role, setup, scanWork }.Contains("failed") ? "failed" : !correlated || !integrity || !lifetime || outcome == "not_checked" ||
                storage && new[] { binding, role, setup, scanWork }.Any(s => s != "passed") ||
                identity.Method == "HistoryRanges" || profile.Id == "transport-mock" ? "not_checked" : "passed";
            results.Add(new() { Identity = identity, ExecutionStatus = errors.Count == 0 ? "succeeded" : "failed",
                ExecutionErrors = errors.Distinct().ToArray(), LaunchCount = launches.Count, CorrectnessStatus = correctness,
                CorrectnessBasis = "Typed correlated observations and per-scan outcomes; unbound cache/root setup and history response content remain not_checked. Mock traces do not establish product acceptance.",
                ContractChecks = new() { ["observer_integrity"] = integrity ? "passed" : "failed",
                    ["correlation"] = correlated ? "passed" : "failed", ["scenario_outcome"] = outcome,
                    ["scan_binding"] = binding, ["scan_roles"] = role, ["scan_setup"] = setup, ["scan_work"] = scanWork,
                    ["cancellation"] = Combine(cancellations), ["lifetime"] = lifetime ? "passed" : "not_checked" },
                Metrics = [new() { CaseId = identity.Id, Scenario = identity.Scenario, DatasetId = identity.DatasetId,
                    Metric = "stage_duration", Unit = "ms", Direction = "lower", Statistic = "mean_elapsed_ms",
                    MeasurementStatus = durations.Count > 0 ? "measured" : "not_measured", Value = durations.Count > 0 ? durations.Average() : null,
                    SampleCount = durations.Count, CorrectnessStatus = correctness, RawArtifact = "raw/collection.json",
                    Reason = "All-outcome client-local attempt intervals, including timeout deadlines; not success latency. Named stage/outcome populations are in stage-summaries.json; absent intervals stay unmeasured." }] });
        }
        return new() { CollectionKind = "scenario", SchemaVersion = 1, RunId = capture.RunId, ProfileId = profile.Id,
            ConfigSha256 = configHash, Discovered = discovered, Cases = results.ToArray(), CollectionErrors = [] };
    }

    private static string Combine(IEnumerable<string> values)
    {
        var all = values.ToArray();
        return all.Contains("failed") ? "failed" : all.Length == 0 || all.Contains("not_checked") ? "not_checked" : "passed";
    }

    private static bool Correlate(Event[] client, Event[] allClient, Event[] backend, ScenarioAttempt attempt)
    {
        bool valid = true;
        var last = new Dictionary<string, long>();
        var identities = new HashSet<string>();
        foreach (var receipt in allClient.Where(e => e.Stage.StartsWith("receive.")).OrderBy(e => Number(e.Wire.EventId)))
        {
            var metadata = receipt.Wire.BenchmarkTrace;
            if (metadata == null) { if (client.Contains(receipt)) valid = false; continue; }
            string key = metadata.ProducerEpoch + "/" + metadata.StreamId;
            long sequence = Number(metadata.Sequence, true);
            bool seen = last.TryGetValue(key, out long previous);
            string expected = !seen || sequence > previous ? "received" : sequence == previous ? "duplicate" : "out_of_order";
            last[key] = Math.Max(sequence, previous);
            bool unique = identities.Add(key + "/" + sequence);
            if (!client.Contains(receipt)) continue;
            valid &= unique && expected != "duplicate" && receipt.Outcome == expected &&
                (receipt.Count("sequence_jump") == -1 || seen && receipt.Count("sequence_jump") == sequence - previous);
            string send = "send." + receipt.Stage[8..];
            var sends = backend.Where(e => e.Stage == send + ".start" && e.Wire.BenchmarkTrace == metadata).ToArray();
            valid &= sends.Length == 1 && backend.Count(e => e.Stage == send + ".end" && e.Wire.BenchmarkTrace == metadata &&
                e.Wire.OperationId == sends[0].Wire.OperationId && e.Outcome == "succeeded") == 1;
            if (receipt.Stage == "receive.RamUpdate") valid &= metadata.StreamId == "ram";
            if (receipt.Stage is "receive.DirectoryChunk" or "receive.DirectoryStreamComplete" or "receive.DirectoryStreamError")
                valid &= attempt.ScanIds.Contains(metadata.StreamId);
        }
        foreach (var applied in client.Where(e => e.Stage is "state.ram.applied" or "state.directory.applied" or "state.directory.terminal"))
        {
            string type = applied.Stage == "state.ram.applied" ? "RamUpdate" : applied.Stage == "state.directory.applied" ? "DirectoryChunk" : "DirectoryStreamComplete";
            valid &= applied.Wire.BenchmarkTrace != null && client.Count(e => e.Stage == "receive." + type &&
                e.Wire.BenchmarkTrace == applied.Wire.BenchmarkTrace && e.Ticks <= applied.Ticks && e.Outcome is "received" or "out_of_order") == 1;
        }
        foreach (var send in backend.Where(e => e.Stage.StartsWith("send.") && e.Stage.EndsWith(".start") &&
            attempt.Scans.Any(s => s.ScanId == e.Stream && s.BackendLaunch == e.Launch)))
        {
            string receive = "receive." + send.Stage[5..^6];
            valid &= client.Count(e => e.Stage == receive && e.Wire.BenchmarkTrace == send.Wire.BenchmarkTrace && e.Outcome is "received" or "out_of_order") == 1;
        }
        valid &= client.Where(e => e.Stage is "http.round_trip.start" or "state.directory.request" or "history.requested")
            .All(e => attempt.RequestIds.Contains(e.Wire.OperationId));
        foreach (string request in attempt.RequestIds)
        {
            var local = client.Where(e => e.Wire.OperationId == request).ToArray();
            var remote = backend.Where(e => e.Stream == "http-" + request).ToArray();
            var start = local.Where(e => e.Stage == "http.round_trip.start").ToArray();
            var end = local.Where(e => e.Stage == "http.round_trip.end").ToArray();
            var headers = local.Where(e => e.Stage == "http.headers").ToArray();
            var response = remote.Where(e => e.Stage == "http.response").ToArray();
            valid &= start.Length == 1 && end.Length == 1 && headers.Length == 1 && response.Length == 1 &&
                start[0].Ticks <= headers[0].Ticks && headers[0].Ticks <= end[0].Ticks && end[0].Outcome == "succeeded" &&
                end[0].Count("status") is >= 200 and < 300 && end[0].Count("entity_bytes") >= 0 &&
                end[0].Count("status") == headers[0].Count("status") && end[0].Count("status") == response[0].Count("status") &&
                remote.Count(e => e.Stage == "http.handler.start" && e.Wire.BenchmarkTrace == response[0].Wire.BenchmarkTrace) == 1 &&
                remote.Count(e => e.Stage == "http.handler.end" && e.Wire.BenchmarkTrace == response[0].Wire.BenchmarkTrace && e.Outcome == "succeeded") == 1;
        }
        if (attempt.Method is not ("Idle" or "Reconnect" or "HistoryRanges"))
        {
            var requests = client.Where(e => e.Stage == "state.directory.request").ToArray();
            valid &= attempt.ScanIds.Length > 0 && requests.Length == attempt.ScanIds.Length;
            foreach (string scan in attempt.ScanIds)
            {
                var request = requests.Where(e => Guid.TryParse(e.Wire.Label, out var id) && id.ToString("N") == scan).ToArray();
                if (request.Length != 1) { valid = false; continue; }
                var remote = backend.Where(e => e.Stream == "http-" + request[0].Wire.OperationId).ToArray();
                valid &= remote.Count(e => IsRoute(e, "POST", "/api/storage/stream-sector")) == 1;
                var session = backend.Where(e => e.Stage == "scan.session.start" && e.Stream == scan).ToArray();
                valid &= session.Length == 1 && remote.Any(e => e.Stage == "http.handler.start" && e.Launch == session[0].Launch && e.Ticks <= session[0].Ticks) &&
                    remote.Any(e => e.Stage == "http.handler.end" && e.Launch == session[0].Launch && e.Ticks >= session[0].Ticks);
            }
        }
        return valid;
    }

    private static bool IsRoute(Event e, string method, string path) => e.Stage == "http.route" && e.Wire.Key ==
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(e.Epoch + "\n" + method + " " + path)));

    private static T Artifact<T>(string raw, ScenarioCapture capture, string relative)
    {
        var entry = capture.Artifacts.SingleOrDefault(a => a.Path == relative);
        Require(entry != null, "Unlisted setup/fixture artifact.");
        string path = SafeFile(raw, relative);
        Require(new FileInfo(path).Length == entry!.Bytes && BenchmarkResults.Hash(path) == entry.Sha256, "Setup/fixture hash mismatch.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        ThresholdValidator.RejectDuplicates(json.RootElement);
        return json.RootElement.Deserialize<T>(WireJson) ?? throw new InvalidDataException("Null setup/fixture artifact.");
    }

    private static string RootKey(string epoch, string root)
    {
        string normalized = root.Replace('\\', '/').TrimEnd('/');
        Require(normalized.Length is > 3 and <= 32768 && char.IsAsciiLetter(normalized[0]) && normalized[1..3] == ":/" &&
            normalized[3..].Split('/').All(s => s.Length > 0 && s is not ("." or "..") && !s.Contains(':')), "Invalid owned fixture root.");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(epoch + "\n" + normalized.ToUpperInvariant())));
    }

    private static string ScanBinding(ScenarioAttempt attempt, Event[] client, Event[] backend, ScenarioCapture capture, string raw)
    {
        if (attempt.Scans.Length == 0) return "not_checked";
        if (attempt.Scans.Length != attempt.ScanIds.Length || attempt.Scans.Select(s => s.ScanId).Distinct().Count() != attempt.ScanIds.Length ||
            attempt.Scans.Any(s => !attempt.ScanIds.Contains(s.ScanId))) return "failed";
        try
        {
            var fixtures = new Dictionary<string, Dictionary<string, Fixture>>();
            foreach (var scan in attempt.Scans)
            {
                if (!attempt.BackendLaunches.Contains(scan.BackendLaunch) || !attempt.RequestIds.Contains(scan.RequestId) ||
                    scan.FixtureArtifact != "cases/" + attempt.Id + "/fixtures.json" || scan.FixtureName is not ("primary" or "secondary" or "large")) return "failed";
                if (!fixtures.TryGetValue(scan.FixtureArtifact, out var set))
                    fixtures[scan.FixtureArtifact] = set = Artifact<Dictionary<string, Fixture>>(raw, capture, scan.FixtureArtifact);
                if (!set.TryGetValue(scan.FixtureName, out var fixture)) return "failed";
                var expected = scan.FixtureName == "large" ? StorageManifest.Legacy(false) : StorageManifest.Tiny();
                if (fixture.Id != expected.Id || fixture.Directories == null || fixture.ObservedDirectories == null || fixture.Files == null || fixture.Inventory == null ||
                    !fixture.Directories.Order(StringComparer.Ordinal).SequenceEqual(expected.Directories.Order(StringComparer.Ordinal)) ||
                    !fixture.ObservedDirectories.Order(StringComparer.Ordinal).SequenceEqual(expected.Directories.Order(StringComparer.Ordinal)) ||
                    !fixture.Files.OrderBy(f => f.Path, StringComparer.Ordinal).SequenceEqual(expected.Files.OrderBy(f => f.Path, StringComparer.Ordinal)) ||
                    fixture.Inventory.Length != expected.Files.Length) return "failed";
                var inventory = fixture.Inventory.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                var files = expected.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                for (int i = 0; i < files.Length; i++)
                    if (inventory[i].Path != files[i].Path || inventory[i].Bytes != files[i].Bytes || inventory[i].Sha256 !=
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[files[i].Bytes]))) return "failed";
                var launch = capture.Launches.Single(l => l.Id == scan.BackendLaunch);
                string key = RootKey(launch.Epoch, fixture.Root);
                var request = backend.Where(e => e.Stage == "stream.request" && e.Stream == scan.ScanId).ToArray();
                var local = client.Where(e => e.Stage == "state.directory.request" && e.Wire.OperationId == scan.RequestId &&
                    Guid.TryParse(e.Wire.Label, out var id) && id.ToString("N") == scan.ScanId).ToArray();
                if (scan.RootKey != key || request.Length != 1 || local.Length != 1 || request[0].Wire.Key != key ||
                    request[0].Wire.OperationId != scan.RequestId || request[0].Launch != scan.BackendLaunch || request[0].Outcome != "observed") return "failed";
                var remote = backend.Where(e => e.Launch == launch.Id && e.Stream == "http-" + scan.RequestId).ToArray();
                if (remote.Count(e => IsRoute(e, "POST", "/api/storage/stream-sector")) != 1 ||
                    !remote.Any(e => e.Stage == "http.handler.start" && e.Ticks <= request[0].Ticks) ||
                    !remote.Any(e => e.Stage == "http.handler.end" && e.Ticks >= request[0].Ticks) ||
                    backend.Count(e => e.Stage == "scan.session.start" && e.Launch == launch.Id && e.Stream == scan.ScanId &&
                        e.Ticks <= request[0].Ticks) != 1) return "failed";
            }
            return "passed";
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException) { return "failed"; }
    }

    private static string Cancellation(ScenarioAttempt attempt, Event[] client, Event[] backend, Span[] spans)
    {
        var receipts = backend.Where(e => e.Stage == "scan.abort.controller_received" && attempt.ScanIds.Contains(e.Stream)).ToArray();
        if (receipts.Length != 1 || attempt.ScanIds.Length < 2) return "failed";
        var receipt = receipts[0];
        var scan = backend.Where(e => e.Launch == receipt.Launch && e.Stream == receipt.Stream).ToArray();
        var received = scan.Where(e => e.Stage == "scan.abort.received" && e.Ticks >= receipt.Ticks).ToArray();
        var signal = scan.Where(e => e.Stage == "scan.abort.signalled").ToArray();
        var returned = scan.Where(e => e.Stage == "scan.abort.controller_returned").ToArray();
        if (received.Length != 1 || signal.Length != 1 || returned.Length != 1 || signal[0].Ticks < received[0].Ticks ||
            returned[0].Ticks < signal[0].Ticks || (returned[0].Ticks - receipt.Ticks) * 1000.0 / receipt.Frequency > 500) return "failed";
        string stage = attempt.Method == "QueuedCancelRestart" ? "scan.lock_queue" : "scan.active";
        bool phase = spans.Any(s => s.Stage == stage && s.Start.Launch == receipt.Launch && s.Start.Stream == receipt.Stream &&
            s.Start.Ticks <= receipt.Ticks && s.End.Ticks >= received[0].Ticks);
        bool restart = backend.Any(e => e.Stage == "scan.session.start" && e.Launch == receipt.Launch &&
            attempt.ScanIds.Contains(e.Stream) && e.Stream != receipt.Stream && e.Ticks > receipt.Ticks);
        if (attempt.Scans.Length > 0)
        {
            var targets = attempt.Scans.Where(s => s.Role == "cancel_target").ToArray();
            var replacements = attempt.Scans.Where(s => s.Role == "current").ToArray();
            if (targets.Length != 1 || replacements.Length != 1 || targets[0].ScanId != receipt.Stream ||
                targets[0].BackendLaunch != receipt.Launch || replacements[0].BackendLaunch != receipt.Launch ||
                replacements[0].ScanId == receipt.Stream || replacements[0].FixtureName != "primary") return "failed";
            string? request = receipt.Wire.OperationId;
            if (request == null || !attempt.RequestIds.Contains(request) || attempt.Scans.Any(s => s.RequestId == request)) return "failed";
            var http = backend.Where(e => e.Launch == receipt.Launch && e.Stream == "http-" + request).ToArray();
            var started = client.Where(e => e.Stage == "http.round_trip.start" && e.Wire.OperationId == request).ToArray();
            var ended = client.Where(e => e.Stage == "http.round_trip.end" && e.Wire.OperationId == request).ToArray();
            var selected = client.Where(e => e.Stage == "state.directory.selected" && ScanLabel(e, replacements[0].ScanId)).ToArray();
            var targetSelected = client.Where(e => e.Stage == "state.directory.selected" && ScanLabel(e, targets[0].ScanId)).ToArray();
            if (http.Count(e => IsRoute(e, "POST", "/api/storage/abort-scan")) != 1 ||
                http.Count(e => e.Stage == "http.handler.start" && e.Ticks <= receipt.Ticks) != 1 ||
                http.Count(e => e.Stage == "http.handler.end" && e.Ticks >= returned[0].Ticks && e.Outcome == "succeeded") != 1 ||
                http.Count(e => e.Stage == "http.response" && e.Count("status") == 200) != 1 ||
                started.Length != 1 || ended.Length != 1 || ended[0].Outcome != "succeeded" || ended[0].Count("status") != 200 ||
                (ended[0].Ticks - started[0].Ticks) * 1000.0 / started[0].Frequency is < 0 or > 500 ||
                selected.Length != 1 || selected[0].Ticks < ended[0].Ticks || targetSelected.Length != 1 || targetSelected[0].Ticks > started[0].Ticks ||
                backend.Count(e => e.Stage == "scan.session.start" && e.Launch == receipt.Launch && e.Stream == replacements[0].ScanId && e.Ticks > returned[0].Ticks) != 1 ||
                scan.Count(e => e.Stage == "stream.service.end" && e.Outcome == "cancelled" && e.Ticks >= received[0].Ticks) != 1) return "failed";
            if (attempt.Method == "QueuedCancelRestart")
            {
                var blockers = attempt.Scans.Where(s => s.Role == "blocker").ToArray();
                if (blockers.Length != 1 || blockers[0].FixtureName != "large" || blockers[0].BackendLaunch != receipt.Launch ||
                    blockers[0].ScanId == receipt.Stream || !spans.Any(s => s.Stage == "scan.active" && s.Start.Launch == receipt.Launch &&
                        s.Start.Stream == blockers[0].ScanId && s.Start.Ticks <= receipt.Ticks && s.End.Ticks >= received[0].Ticks)) return "failed";
            }
        }
        return phase && restart && scan.Any(e => e.Stage == "scan.active.end" && e.Outcome == "cancelled" && e.Ticks >= received[0].Ticks)
            ? "passed" : "failed";
    }

    private static string ScanWork(ScenarioAttempt attempt, Event[] backend)
    {
        if (attempt.Scans.Length == 0) return "not_checked";
        bool unknown = false;
        foreach (var scan in attempt.Scans)
        {
            if (scan.Role == "cancel_target") continue;
            var events = backend.Where(e => e.Launch == scan.BackendLaunch && e.Stream == scan.ScanId).ToArray();
            var active = events.Where(e => e.Stage == "scan.active.end").ToArray();
            var walks = events.Where(e => e.Stage == "scan.walk.end").ToArray();
            if (active.Length != 1 || active[0].Outcome != "succeeded") return "failed";
            if (scan.SetupKind == "app_empty")
            {
                bool large = scan.FixtureName == "large";
                if (walks.Length != (large ? 505 : 3) || walks.Any(e => e.Outcome != "succeeded" || e.Count("observed_files") < 0 || e.Count("own_bytes") < 0) ||
                    walks.Sum(e => e.Count("observed_files")) != (large ? 50000 : 5) || walks.Sum(e => e.Count("own_bytes")) != (large ? 0 : 87)) return "failed";
            }
            else if (scan.SetupKind == "app_hit")
            {
                if (walks.Any(e => e.Outcome != "cache_hit") || walks.Length == 0 && events.Any(e => e.Stage == "scan.worker.start")) return "failed";
            }
            else if (scan.SetupKind == "changed_rescan")
            {
                if (!walks.Any(e => e.Outcome == "succeeded") || walks.Any(e => e.Outcome is not ("succeeded" or "cache_hit"))) return "failed";
            }
            else unknown = true;
        }
        return unknown ? "not_checked" : "passed";
    }

    private static string ScanSetup(ScenarioAttempt attempt, Event[] client, ScenarioCapture capture, string raw)
    {
        if (attempt.Scans.Length == 0) return "not_checked";
        string prefix = "cases/" + attempt.Id + "/", before = prefix + "state-before.json";
        if (attempt.Scans.Any(s => s.SetupArtifacts.Length == 0)) return "not_checked";
        try
        {
            if (attempt.Scans.Any(s => !s.SetupArtifacts.Contains(before) || s.SetupArtifacts.Distinct().Count() != s.SetupArtifacts.Length)) return "failed";
            var inventory = Artifact<ArtifactEntry[]>(raw, capture, before);
            string[] names = ["appsettings.user.json", "automation_rules.json", "benchmark-owner.json"];
            if (!inventory.Select(a => a.Path).Order().SequenceEqual(names.Order())) return "failed";
            foreach (var item in inventory)
            {
                var retained = capture.Artifacts.SingleOrDefault(a => a.Path == prefix + "state-after/" + item.Path);
                if (retained == null || retained.Bytes != item.Bytes || retained.Sha256 != item.Sha256) return "failed";
            }
            var owner = Artifact<JsonElement>(raw, capture, prefix + "state-after/benchmark-owner.json");
            var rules = Artifact<JsonElement>(raw, capture, prefix + "state-after/automation_rules.json");
            var settings = Artifact<JsonElement>(raw, capture, prefix + "state-after/appsettings.user.json");
            if (owner.GetProperty("schemaVersion").GetInt32() != 1 || owner.GetProperty("runId").GetString() != capture.RunId ||
                owner.GetProperty("attemptId").GetString() != attempt.Id || rules.GetArrayLength() != 0 ||
                settings.GetProperty("scan").GetProperty("depth").GetInt32() != 50 || settings.GetProperty("scan").GetProperty("excludedPaths").GetArrayLength() != 0 ||
                settings.GetProperty("scan").GetProperty("followSymlinks").GetBoolean() ||
                settings.GetProperty("monitoring").GetProperty("enableScheduledScans").GetBoolean() ||
                settings.GetProperty("monitoring").GetProperty("ramPollIntervalMs").GetInt32() != 1000 ||
                settings.GetProperty("advanced").GetProperty("backendPort").GetInt32() != 5200) return "failed";
            var checks = new List<string>();
            foreach (var scan in attempt.Scans)
            {
                if (scan.SetupKind == "persisted_restore") checks.Add("not_checked");
                else if (scan.SetupKind == "changed_rescan") checks.Add(ChangedSetup(scan, attempt, client, capture, raw, before));
                else checks.Add(scan.SetupArtifacts.SequenceEqual([before]) ? "passed" : "failed");
            }
            return Combine(checks);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException) { return "failed"; }
    }

    private static string ChangedSetup(ScenarioScan scan, ScenarioAttempt attempt, Event[] client, ScenarioCapture capture, string raw, string before)
    {
        if (scan.SetupArtifacts.SequenceEqual([before])) return "not_checked";
        var requests = scan.SetupArtifacts.Where(p => p.EndsWith(".request.json", StringComparison.Ordinal)).ToArray();
        if (requests.Length != 1) return "failed";
        var request = Artifact<JsonElement>(raw, capture, requests[0]);
        string? command = request.GetProperty("commandId").GetString();
        if (command == null || !Guid.TryParseExact(command, "N", out var id) || id.ToString("N") != command) return "failed";
        string prefix = "cases/" + attempt.Id + "/control/" + command;
        string[] expectedArtifacts = [before, prefix + ".request.json", prefix + ".ack.json", prefix + ".fixture.json"];
        if (!scan.SetupArtifacts.Order(StringComparer.Ordinal).SequenceEqual(expectedArtifacts.Order(StringComparer.Ordinal))) return "failed";
        var ack = Artifact<JsonElement>(raw, capture, prefix + ".ack.json");
        foreach (var control in new[] { request, ack })
            if (control.GetProperty("schemaVersion").GetInt32() != 1 || control.GetProperty("runId").GetString() != capture.RunId ||
                control.GetProperty("attemptId").GetString() != attempt.Id || control.GetProperty("commandId").GetString() != command) return "failed";
        if (request.GetProperty("action").GetString() != "mutate" || ack.GetProperty("outcome").GetString() != "ready" ||
            ack.GetProperty("backendLaunchId").GetString() != scan.BackendLaunch) return "failed";
        var seed = attempt.Scans.SingleOrDefault(s => s.ScanId == scan.SeedScanId && s.Role == "seed");
        if (seed == null || seed.BackendLaunch != scan.BackendLaunch || seed.FixtureArtifact != scan.FixtureArtifact ||
            seed.FixtureName != "primary" || scan.FixtureName != "primary") return "failed";
        var requested = client.Where(e => e.Stage == "scenario.control.requested" && e.Wire.OperationId == command).ToArray();
        var acknowledged = client.Where(e => e.Stage == "scenario.control.acknowledged" && e.Wire.OperationId == command).ToArray();
        var selected = client.Where(e => e.Stage == "state.directory.selected" && ScanLabel(e, scan.ScanId)).ToArray();
        if (requested.Length != 1 || acknowledged.Length != 1 || selected.Length != 1 ||
            requested[0].Wire.Label != "mutate" || acknowledged[0].Wire.Label != "mutate" ||
            Number(requested[0].Wire.EventId) >= Number(acknowledged[0].Wire.EventId) ||
            Number(requested[0].Wire.EventId) >= Number(selected[0].Wire.EventId) ||
            client.Count(e => e.Stage == "state.directory.terminal" && e.Stream == seed.ScanId && e.Outcome == "applied" &&
                Number(e.Wire.EventId) < Number(requested[0].Wire.EventId)) != 1) return "failed";
        var inventory = Artifact<ArtifactEntry[]>(raw, capture, prefix + ".fixture.json").OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var files = StorageManifest.Tiny().Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (inventory.Length != files.Length) return "failed";
        for (int i = 0; i < files.Length; i++)
        {
            long bytes = files[i].Bytes + (files[i].Path == "A/a.txt" ? 8 : 0);
            if (inventory[i].Path != files[i].Path || inventory[i].Bytes != bytes || inventory[i].Sha256 !=
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[bytes]))) return "failed";
        }
        return "passed";
    }

    private static bool ScanLabel(Event e, string scan) => Guid.TryParse(e.Wire.Label, out var id) && id.ToString("N") == scan;

    private static string ScanRoles(ScenarioAttempt attempt, Event[] local)
    {
        var scans = attempt.Scans;
        if (scans.Length == 0) return "not_checked";
        if (scans.Select(s => s.ScanId).Distinct().Count() != scans.Length) return "failed";
        foreach (var scan in scans)
        {
            if (scan.Role is not ("seed" or "current" or "superseded" or "cancel_target" or "blocker")) return "failed";
            var selected = local.Where(e => e.Stage == "state.directory.selected" && ScanLabel(e, scan.ScanId)).ToArray();
            var request = local.Where(e => e.Stage == "state.directory.request" && e.Wire.OperationId == scan.RequestId).ToArray();
            if (selected.Length != 1 || request.Length != 1 || selected[0].Ticks > request[0].Ticks) return "failed";
            if (scan.Role == "superseded")
            {
                var replacement = scans.SingleOrDefault(s => s.ScanId == scan.SupersededByScanId);
                var next = local.Where(e => e.Stage == "state.directory.selected" && Number(e.Wire.EventId) > Number(selected[0].Wire.EventId))
                    .OrderBy(e => Number(e.Wire.EventId)).FirstOrDefault();
                if (replacement == null || replacement == scan || next == null || !ScanLabel(next, replacement.ScanId)) return "failed";
            }
            else if (scan.SupersededByScanId != null) return "failed";
            if (scan.Role == "seed")
            {
                if (scan.SeedScanId != null || scan.SetupKind != "app_empty" || scan.FixtureName != "primary") return "failed";
            }
            else if (attempt.Method is "AppHit" or "ChangedRescan" or "PersistedRestore")
            {
                var seed = scans.SingleOrDefault(s => s.ScanId == scan.SeedScanId && s.Role == "seed");
                if (seed == null || seed.ScanId == scan.ScanId || seed.FixtureName != scan.FixtureName || seed.FixtureArtifact != scan.FixtureArtifact ||
                    (attempt.Method == "PersistedRestore" ? seed.BackendLaunch == scan.BackendLaunch : seed.BackendLaunch != scan.BackendLaunch) ||
                    scan.SetupKind != (attempt.Method == "AppHit" ? "app_hit" : attempt.Method == "ChangedRescan" ? "changed_rescan" : "persisted_restore") ||
                    local.Count(e => e.Stage == "state.directory.terminal" && e.Stream == seed.ScanId && e.Outcome == "applied" && e.Ticks < selected[0].Ticks) != 1) return "failed";
            }
            else if (scan.SeedScanId != null || scan.SetupKind != "app_empty") return "failed";
        }
        int Count(string role, string fixture) => scans.Count(s => s.Role == role && s.FixtureName == fixture);
        bool valid = attempt.Method switch
        {
            "AppEmpty" => scans.Length == 1 && Count("current", "primary") == 1,
            "AppHit" or "PersistedRestore" => scans.Length == 2 && Count("seed", "primary") == 1 && Count("current", "primary") == 1,
            "ChangedRescan" => scans.Length >= 2 && Count("seed", "primary") == 1 && Count("current", "primary") >= 1 &&
                scans.All(s => s.FixtureName == "primary" && s.Role is "seed" or "current" or "superseded"),
            "ConcurrentRoots" => scans.Length == 2 && Count("superseded", "primary") == 1 && Count("current", "secondary") == 1,
            "ActiveCancelRestart" => scans.Length == 2 && Count("cancel_target", "large") == 1 && Count("current", "primary") == 1,
            "QueuedCancelRestart" => scans.Length == 3 && Count("blocker", "large") == 1 && Count("cancel_target", "secondary") == 1 && Count("current", "primary") == 1,
            _ => false
        };
        return valid ? "passed" : "failed";
    }

    private static string CheckOutcome(ScenarioAttempt attempt, Event[] local, Event[] backend, ScenarioCapture capture, string cancellation)
    {
        bool Has(string stage) => local.Any(e => e.Stage == stage);
        bool ram = local.Any(e => e.Stage == "state.ram.applied" && e.Count("processes") > 0 && e.Stream == "ram");
        if (attempt.Outcome != "succeeded") return "failed";
        if (attempt.Method == "Idle") return ram && Has("transport.negotiate") && Has("transport.http_connected") ? "passed" : "failed";
        if (attempt.Method == "Reconnect")
        {
            var restarts = local.Where(e => e.Stage == "scenario.backend_restart" && e.Wire.OperationId == attempt.Id).ToArray();
            if (restarts.Length != 1 || !attempt.BackendLaunches.Contains(restarts[0].Wire.Label)) return "failed";
            var launch = capture.Launches.Single(l => l.Id == restarts[0].Wire.Label);
            bool rescued = local.Any(e => e.Stage == "transport.start.start") || backend.Where(e => e.Launch == launch.Id)
                .Any(e => new[] { "/api/Telemetry/ram/start", "/api/telemetry/ram/start", "/api/Telemetry/cpu/start", "/api/telemetry/cpu/start",
                    "/api/Telemetry/network/start", "/api/telemetry/network/start" }.Any(path => IsRoute(e, "POST", path)));
            bool fresh = local.Any(reconnected => reconnected.Stage == "transport.reconnected" && reconnected.Ticks >= restarts[0].Ticks &&
                local.Any(interrupted => interrupted.Stage == "transport.reconnecting" && interrupted.Ticks < reconnected.Ticks) &&
                local.Any(sample => sample.Stage == "state.ram.applied" && sample.Ticks > reconnected.Ticks && sample.Count("processes") > 0 &&
                    sample.Wire.BenchmarkTrace?.ProducerEpoch == launch.Epoch));
            bool original = local.Any(e => e.Stage == "state.ram.applied" && e.Count("processes") > 0 && e.Ticks < restarts[0].Ticks &&
                e.Wire.BenchmarkTrace?.ProducerEpoch != launch.Epoch);
            return !rescued && fresh && original && attempt.BackendLaunches.Length >= 2 ? "passed" : "failed";
        }
        if (attempt.Method == "HistoryRanges") return HistoryOutcome(attempt, local, backend);
        if (attempt.ScanIds.Length == 0 || attempt.Method.Contains("Cancel", StringComparison.Ordinal) && cancellation != "passed") return "failed";
        var cancelled = backend.Where(e => e.Stage == "scan.abort.controller_received" && attempt.ScanIds.Contains(e.Stream))
            .Select(e => e.Stream).ToHashSet();
        foreach (string scan in attempt.ScanIds)
        {
            var bound = attempt.Scans.FirstOrDefault(s => s.ScanId == scan);
            var remote = backend.Where(e => e.Stream == scan).ToArray();
            var terminal = remote.Where(e => e.Stage == "stream.terminal").ToArray();
            if (terminal.Length != 1 || remote.Any(e => e.Outcome is "failed" or "unobserved")) return "failed";
            if (cancelled.Contains(scan))
            {
                var receipt = remote.Single(e => e.Stage == "scan.abort.controller_received");
                if (terminal[0].Outcome != "cancelled" || terminal[0].Count("has_summary") != 0 ||
                    remote.Any(e => e.Stage == "scan.save.start" && e.Ticks >= receipt.Ticks)) return "failed";
                continue;
            }
            bool large = bound?.FixtureName == "large";
            long delta = attempt.Method == "ChangedRescan" && bound?.Role != "seed" ? 8 : 0;
            long nodes = large ? 5 : 4, own = large ? 0 : 3, total = large ? 0 : 90 + delta;
            var snapshots = local.Where(e => e.Stage == "scenario.snapshot" && e.Wire.OperationId == attempt.Id && e.Wire.Label == scan).ToArray();
            var applied = local.Where(e => e.Stage == "state.directory.terminal" && e.Stream == scan).ToArray();
            var summaries = remote.Where(e => e.Stage == "coalescer.summary").ToArray();
            if (applied.Length != 1 || terminal[0].Outcome != "succeeded" ||
                terminal[0].Count("has_summary") != 1 || terminal[0].Count("recursive_bytes") != total || summaries.Length != 1 ||
                !remote.Any(e => e.Stage == "stream.service.end" && e.Outcome == "succeeded") ||
                !remote.Any(e => e.Stage == "scan.save.end" && e.Outcome == "succeeded")) return "failed";
            var summary = summaries[0];
            if (summary.Count("accepted") < 0 || summary.Count("accepted") != summary.Count("drained") ||
                summary.Count("accepted") != summary.Count("delivered") || summary.Count("abandoned") != 0 || summary.Count("rejected") != 0) return "failed";
            if (bound != null)
            {
                var service = remote.Where(e => e.Stage == "stream.service.end").ToArray();
                var chunks = remote.Where(e => e.Stage == "stream.chunk").ToArray();
                if (service.Length != 1 || service[0].Count("own_bytes") != own || service[0].Count("recursive_bytes") != total ||
                    summary.Count("accepted") != nodes || chunks.Length == 0 || chunks.Any(e => e.Count("nodes") <= 0 || e.Count("bytes") < 0) ||
                    chunks.Sum(e => e.Count("nodes")) != nodes || chunks.Sum(e => e.Count("bytes")) != total) return "failed";
                var selected = local.SingleOrDefault(e => e.Stage == "state.directory.selected" && ScanLabel(e, scan));
                var next = selected == null ? null : local.Where(e => e.Stage == "state.directory.selected" && Number(e.Wire.EventId) > Number(selected.Wire.EventId))
                    .OrderBy(e => Number(e.Wire.EventId)).FirstOrDefault();
                if (next != null && local.Any(e => e.Stream == scan && e.Stage is "state.directory.applied" or "state.directory.terminal" &&
                    e.Outcome != "old_scan" && Number(e.Wire.EventId) > Number(next.Wire.EventId))) return "failed";
                if (bound.Role == "superseded" || bound.Role == "blocker" && applied[0].Outcome == "old_scan")
                {
                    if (next == null || applied[0].Outcome != "old_scan" || Number(applied[0].Wire.EventId) <= Number(next.Wire.EventId)) return "failed";
                    continue;
                }
            }
            if (snapshots.Length != 1 || applied[0].Outcome != "applied" || applied[0].Count("recursive_bytes") != total ||
                applied[0].Count("own_bytes") != own || applied[0].Count("nodes") != nodes) return "failed";
            var snapshot = snapshots[0];
            if (snapshot.Count("nodes") != nodes || snapshot.Count("unknown") != 0 || snapshot.Ticks < applied[0].Ticks ||
                (large ? snapshot.Count("large_roots") != 5 : snapshot.Count("a_bytes") != 33 + delta || snapshot.Count("b_bytes") != 54 ||
                    snapshot.Count("root_bytes") != 3 || snapshot.Count("zero_bytes") != 0)) return "failed";
        }
        if (attempt.Method == "ConcurrentRoots" && (attempt.ScanIds.Length < 2 || !ram)) return "failed";
        return attempt.Scans.Length == attempt.ScanIds.Length ? "passed" : "not_checked";
    }

    private static string HistoryOutcome(ScenarioAttempt attempt, Event[] local, Event[] backend)
    {
        var requests = local.Where(e => e.Stage == "history.requested").ToArray();
        if (!new long[] { 5, 15, 30, 60 }.All(m => requests.Any(e => e.Count("minutes") == m))) return "failed";
        foreach (var request in requests)
        {
            string? id = request.Wire.OperationId;
            var events = local.Where(e => e.Wire.OperationId == id).ToArray();
            var publication = events.Where(e => e.Stage == "history.publication_attempt").ToArray();
            var applied = events.Where(e => e.Stage == "history.applied").ToArray();
            var end = events.Where(e => e.Stage == "history.request.end").ToArray();
            var settled = events.Where(e => e.Stage == "history.settled").ToArray();
            var decode = events.Where(e => e.Stage == "http.decode.end").ToArray();
            var http = events.Where(e => e.Stage == "http.round_trip.end").ToArray();
            if (!attempt.RequestIds.Contains(id) || request.Count("minutes") <= 0 || request.Count("generation") <= 0 ||
                publication.Length != 1 || applied.Length != 1 || end.Length != 1 || settled.Length != 1 || decode.Length != 1 || http.Length != 1 ||
                publication[0].Outcome != "current" || end[0].Outcome != "returned" || decode[0].Outcome != "succeeded" ||
                publication[0].Ticks < decode[0].Ticks || decode[0].Ticks < http[0].Ticks || applied[0].Ticks < publication[0].Ticks ||
                end[0].Ticks < applied[0].Ticks || settled[0].Ticks < end[0].Ticks || settled[0].Count("in_flight") < 0 ||
                decode[0].Count("entity_bytes") != http[0].Count("entity_bytes") ||
                publication[0].Count("minutes") != request.Count("minutes") || applied[0].Count("minutes") != request.Count("minutes") ||
                publication[0].Count("generation") != request.Count("generation") || applied[0].Count("generation") != request.Count("generation") ||
                requests.Any(other => Number(other.Wire.EventId) > Number(request.Wire.EventId) &&
                    Number(other.Wire.EventId) <= Number(publication[0].Wire.EventId)) ||
                local.Any(e => e.Stage == "history.dispose" && e.Ticks >= request.Ticks && e.Ticks <= publication[0].Ticks) ||
                backend.Count(e => e.Stream == "http-" + id && IsRoute(e, "GET", "/api/telemetry/history")) != 1) return "failed";
        }
        return "passed";
    }
}

public sealed record EngineInvocation(string RunId, string LaunchId, int ProcessId, int Invocation,
    string Method, int Count, int Minutes, Dictionary<string, string> Actual);

public sealed class EngineIteration
{
    public const string Prefix = "// GS_ENGINE_INVOCATION ";
    private readonly string launch = Guid.NewGuid().ToString("N");
    private int invocation;
    private EngineInput? input;
    private object? output;
    private string method = "";
    private int count, minutes;

    public void Begin(string method, int count = 0, int minutes = 0)
    {
        this.method = method; this.count = count; this.minutes = minutes;
        invocation++;
        output = null;
        input = new EngineInput(method, count, minutes);
    }

    public object Run() => output = input!.Run();

    public EngineInvocation Finish()
    {
        try
        {
            var record = new EngineInvocation(Environment.GetEnvironmentVariable("GS_BENCH_RUN_ID") ?? "standalone",
                launch, Environment.ProcessId, invocation, method, count, minutes,
                output == null ? new() : EngineObservation.Capture(method, output));
            Console.WriteLine(Prefix + JsonSerializer.Serialize(record, ThresholdValidator.Json).ReplaceLineEndings(""));
            return record;
        }
        finally { Cleanup(); }
    }

    public void Cleanup() { input?.Dispose(); input = null; output = null; }
}

public sealed class EngineClock : TimeProvider
{
    public static readonly DateTimeOffset Anchor = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private long ticks = TimeSpan.TicksPerSecond;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => Anchor.AddTicks(ticks - TimeSpan.TicksPerSecond);
    public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
}

public sealed class EngineInput : IDisposable
{
    public static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public const ulong NativeBase = 0x100000;
    private readonly string method;
    private readonly int minutes;
    private readonly byte[]? native;
    private readonly double[]? loads;
    private readonly WindowsProcessMetricsProvider? nativeReader;
    private readonly WindowsCpuProvider? cpuReader;
    private readonly TelemetryHistoryBuffer? history;
    private readonly TelemetryHistoryResponse? historyResponse;
    public EngineClock Clock { get; } = new();
    internal ProcessMemorySample[] Processes { get; set; } = [];
    public NetworkInterfaceInfo[] Adapters { get; set; } = [];
    public DiskIoRawSample[] Disks { get; set; } = [];
    public bool FailReader { get; set; }
    public RamMonitoringEngine? Ram { get; }
    public NetworkSamplerEngine? Network { get; }
    public DiskIoEngine? Disk { get; }

    public EngineInput(string method, int count, int minutes = 0)
    {
        this.method = method; this.minutes = minutes;
        var settings = new Mock<ISettingService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettingDto());
        var hub = Mock.Of<IHubContext<SystemHub>>();
        var historySink = Mock.Of<ITelemetryHistoryBuffer>();
        switch (method)
        {
            case "ParseSnapshot": native = NativeFixture(count); break;
            case "ProjectSnapshot":
                Processes = ProcessFixture(count, false);
                var owners = new Mock<IProcessOwnerResolver>();
                owners.Setup(o => o.Resolve(It.IsAny<int>())).Returns("fixture");
                Ram = new(hub, settings.Object, owners.Object, historySink, NullLogger<RamMonitoringEngine>.Instance,
                    () => FailReader ? throw new IOException("controlled process read failure") : Processes, () => null, Clock);
                Ram.CaptureProcessSnapshot();
                Clock.Advance(TimeSpan.FromSeconds(2));
                Processes = ProcessFixture(count, true);
                break;
            case "ProjectCpu": loads = Enumerable.Range(0, count).Select(i => (double)(i % 4 * 20 + 10)).ToArray(); break;
            case "SampleNetwork":
                var network = new Mock<INetworkInterfaceProvider>();
                network.Setup(n => n.GetInterfaces()).Returns(() => FailReader ? throw new IOException("controlled network failure") : Adapters);
                Network = new(network.Object, hub, settings.Object, historySink, NullLogger<NetworkSamplerEngine>.Instance, Clock);
                Adapters = NetworkFixture(count, false);
                Network.SampleMetrics();
                Clock.Advance(TimeSpan.FromSeconds(2));
                Adapters = NetworkFixture(count, true);
                break;
            case "SampleDisk":
                var disk = new Mock<IDiskIoReader>();
                disk.Setup(d => d.ReadSamplesAsync()).Returns(() => FailReader
                    ? Task.FromException<IReadOnlyList<DiskIoRawSample>>(new IOException("controlled disk failure"))
                    : Task.FromResult<IReadOnlyList<DiskIoRawSample>>(Disks));
                Disks = Enumerable.Range(1, count).Select(i => new DiskIoRawSample(i.ToString(), "fixture", i * 100, i * 50, 25, 0.5)).ToArray();
                Disk = new(disk.Object, hub, settings.Object, historySink, NullLogger<DiskIoEngine>.Instance, Clock);
                Clock.Advance(TimeSpan.FromSeconds(2));
                break;
            case "QueryHistory":
            case "SerializeHistory":
                history = new(Clock);
                Clock.Advance(TimeSpan.FromMilliseconds(-300L * Math.Max(0, count - 1)));
                for (int i = 0; i < count; i++)
                {
                    history.Record("cpu", i % 100 + 0.25);
                    if (i + 1 < count) Clock.Advance(TimeSpan.FromMilliseconds(300));
                }
                if (method == "SerializeHistory") historyResponse = history.GetHistory("cpu", minutes);
                break;
            case "ReadNativeProcesses": nativeReader = new(); break;
            case "ReadNativeCpu": cpuReader = new(); break;
            default: throw new InvalidDataException("Unknown engine input: " + method);
        }
    }

    public object Run() => method switch
    {
        "ParseSnapshot" => WindowsProcessMetricsProvider.ParseSnapshot(native!, NativeBase, 8),
        "ProjectSnapshot" => Ram!.CaptureProcessSnapshot(),
        "ProjectCpu" => WindowsCpuProvider.Project(loads!, 20, 3000, 125, 7, 19, 101),
        "SampleNetwork" => Network!.SampleMetrics(),
        "SampleDisk" => Disk!.SampleMetrics(),
        "QueryHistory" => history!.GetHistory("cpu", minutes)!,
        "SerializeHistory" => JsonSerializer.SerializeToUtf8Bytes(historyResponse, WebJson),
        "ReadNativeProcesses" => nativeReader!.ReadSnapshot(),
        "ReadNativeCpu" => cpuReader!.GetNextSampleAsync().GetAwaiter().GetResult(),
        _ => throw new InvalidDataException("Unknown engine method.")
    };

    internal static ProcessMemorySample[] ProcessFixture(int count, bool advanced) => Enumerable.Range(1, count)
        .Select(i => new ProcessMemorySample(i, "进程🧪", i * 100L,
            advanced ? Environment.ProcessorCount * TimeSpan.TicksPerSecond / 2 : 0,
            i * 1000L, i % 3 == 0 ? null : i * 300L, i % 5 == 0 ? null : i * 2000L)).ToArray();

    public static NetworkInterfaceInfo[] NetworkFixture(int count, bool advanced) => Enumerable.Range(1, count).Select(i => new NetworkInterfaceInfo
    {
        Id = i.ToString(), Name = "fixture", NetworkInterfaceType = NetworkInterfaceType.Ethernet,
        OperationalStatus = OperationalStatus.Up, Speed = i % 2 == 0 ? -1 : 8000000,
        BytesReceived = 10000 + (advanced ? i * 200L : 0), BytesSent = 20000 + (advanced ? i * 100L : 0)
    }).ToArray();

    public static byte[] NativeFixture(int count)
    {
        const int stride = 288;
        var data = new byte[count * stride];
        var name = Encoding.Unicode.GetBytes("进程🧪.exe");
        for (int i = 0; i < count; i++)
        {
            var row = data.AsSpan(i * stride, stride);
            BinaryPrimitives.WriteUInt32LittleEndian(row, i == count - 1 ? 0U : stride);
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..], (ulong)((i + 1) * 300));
            BinaryPrimitives.WriteInt64LittleEndian(row[32..], (i + 1) * 100);
            BinaryPrimitives.WriteInt64LittleEndian(row[40..], 200);
            BinaryPrimitives.WriteInt64LittleEndian(row[48..], 300);
            BinaryPrimitives.WriteUInt16LittleEndian(row[56..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(row[58..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(row[64..], NativeBase + (ulong)(i * stride + 256));
            BinaryPrimitives.WriteUInt64LittleEndian(row[80..], (ulong)(i + 1));
            BinaryPrimitives.WriteUInt64LittleEndian(row[144..], (ulong)((i + 1) * 1000));
            BinaryPrimitives.WriteUInt64LittleEndian(row[200..], (ulong)((i + 1) * 2000));
            name.CopyTo(row[256..]);
        }
        return data;
    }

    public void Dispose()
    {
        Ram?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Network?.Dispose(); Disk?.Dispose();
    }
}

public static class EngineObservation
{
    public static string? TypeFor(string method) => method switch
    {
        "ParseSnapshot" or "ProjectSnapshot" => "GSAnalyzer.Benchmarks.Suites.Memory.RamMonitoringBenchmarks",
        "ReadNativeProcesses" => "GSAnalyzer.Benchmarks.Suites.Memory.NativeRamBenchmarks",
        "ProjectCpu" => "GSAnalyzer.Benchmarks.Suites.Processing.CpuLoadBenchmarks",
        "ReadNativeCpu" => "GSAnalyzer.Benchmarks.Suites.Processing.NativeCpuBenchmarks",
        "SampleNetwork" => "GSAnalyzer.Benchmarks.Suites.Processing.NetworkContractBenchmarks",
        "SampleDisk" => "GSAnalyzer.Benchmarks.Suites.Processing.DiskContractBenchmarks",
        "QueryHistory" or "SerializeHistory" => "GSAnalyzer.Benchmarks.Suites.Processing.HistoryContractBenchmarks",
        _ => null
    };
    public static bool IsLive(string method) => method is "ReadNativeProcesses" or "ReadNativeCpu";
    public static bool IsEngine(Type type) => new[] { "ParseSnapshot", "ReadNativeProcesses", "ProjectCpu", "ReadNativeCpu",
        "SampleNetwork", "SampleDisk", "QueryHistory" }.Any(m => TypeFor(m) == type.FullName);

    public static Dictionary<string, string> Capture(string method, object value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, object? item) => result.Add(key, JsonSerializer.Serialize(item));
        switch (method)
        {
            case "ReadNativeProcesses":
                var live = (IReadOnlyList<ProcessMemorySample>)value;
                Put("count", live.Count); Put("query_succeeded", true);
                Put("private_unavailable", live.Count(p => p.PrivateWorkingSetBytes == null));
                Put("commit_unavailable", live.Count(p => p.CommitBytes == null));
                break;
            case "ParseSnapshot":
                var parsed = (IReadOnlyList<ProcessMemorySample>)value;
                Put("count", parsed.Count);
                foreach (var p in parsed)
                {
                    string k = "process/" + p.ProcessId + "/";
                    Put(k + "name", p.Name); Put(k + "creation", p.CreationTimeTicks); Put(k + "cpu_ticks", p.CpuTimeTicks);
                    Put(k + "total", p.WorkingSetBytes); Put(k + "private", p.PrivateWorkingSetBytes); Put(k + "commit", p.CommitBytes);
                }
                break;
            case "ProjectSnapshot":
                var projected = (List<ProcessTelemetry>)value;
                Put("count", projected.Count); Put("order", projected.Select(p => p.ProcessId).ToArray());
                foreach (var p in projected)
                {
                    string k = "process/" + p.ProcessId + "/";
                    Put(k + "name", p.Name); Put(k + "cpu_percent", p.CpuPercent); Put(k + "total", p.WorkingSetBytes);
                    Put(k + "private", p.PrivateWorkingSetBytes); Put(k + "commit", p.CommitBytes); Put(k + "user", p.User);
                }
                break;
            case "ReadNativeCpu":
                var native = (CpuTelemetryDto)value;
                Put("count", native.CoreGroups.Values.Sum(g => g.Count)); Put("query_succeeded", true);
                Put("frequency_ghz", native.CurrentFrequencyGhz); Put("average", native.AverageLoad);
                break;
            case "ProjectCpu":
                var cpu = (CpuTelemetryDto)value;
                Put("count", cpu.CoreGroups.Values.Sum(g => g.Count)); Put("average", cpu.AverageLoad); Put("delta", cpu.Delta);
                Put("frequency_ghz", cpu.CurrentFrequencyGhz); Put("processes", cpu.TotalProcesses); Put("threads", cpu.TotalThreads);
                Put("handles", cpu.TotalHandles);
                foreach (var g in cpu.CoreGroups) Put(g.Key, g.Value);
                break;
            case "SampleNetwork":
                var net = (NetworkSnapshot)value;
                Put("count", net.Interfaces.Count); Put("primary", net.PrimaryInterfaceId);
                foreach (var n in net.Interfaces)
                {
                    string k = "adapter/" + n.Id + "/";
                    Put(k + "rx", n.RxBytesPerSec); Put(k + "tx", n.TxBytesPerSec); Put(k + "session_rx", n.SessionRxBytes);
                    Put(k + "session_tx", n.SessionTxBytes); Put(k + "utilisation", n.UtilisationPercent);
                }
                break;
            case "SampleDisk":
                var disks = (DiskIoSnapshotCollection)value;
                Put("count", disks.Disks.Count);
                foreach (var d in disks.Disks)
                {
                    string k = "disk/" + d.Id + "/";
                    Put(k + "read", d.ReadBytesPerSec); Put(k + "write", d.WriteBytesPerSec);
                    Put(k + "session_read", d.SessionReadBytes); Put(k + "session_write", d.SessionWriteBytes);
                    Put(k + "active", d.ActiveTimePercent); Put(k + "queue", d.AvgQueueLength); Put(k + "model", d.Model);
                    Put(k + "letters", d.DriveLetters);
                }
                break;
            case "QueryHistory":
            case "SerializeHistory":
                byte[] bytes = value is byte[] body ? body : JsonSerializer.SerializeToUtf8Bytes((TelemetryHistoryResponse)value, EngineInput.WebJson);
                using (var doc = JsonDocument.Parse(bytes))
                {
                    var h = doc.RootElement;
                    Put("count", h.GetProperty("points").GetArrayLength());
                    foreach (var field in h.EnumerateObject()) result.Add(field.Name, field.Value.GetRawText());
                }
                if (value is byte[]) { Put("entity_bytes", bytes.Length); Put("entity_body", Encoding.UTF8.GetString(bytes)); }
                break;
            default: throw new InvalidDataException("Unknown engine observation: " + method);
        }
        return result;
    }

    public static Dictionary<string, string> Expected(string method, int count, int minutes)
    {
        if (IsLive(method)) return new();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, object? item) => result.Add(key, JsonSerializer.Serialize(item));
        Put("count", count);
        switch (method)
        {
            case "ParseSnapshot":
            case "ProjectSnapshot":
                if (method == "ProjectSnapshot") Put("order", Enumerable.Range(1, count).Reverse().ToArray());
                for (int i = 1; i <= count; i++)
                {
                    string k = "process/" + i + "/";
                    Put(k + "name", "进程🧪"); Put(k + "total", i * 1000L);
                    Put(k + "private", method == "ProjectSnapshot" && i % 3 == 0 ? null : i * 300L);
                    Put(k + "commit", method == "ProjectSnapshot" && i % 5 == 0 ? null : i * 2000L);
                    if (method == "ParseSnapshot") { Put(k + "creation", i * 100L); Put(k + "cpu_ticks", 500); }
                    else { Put(k + "cpu_percent", 25); Put(k + "user", "fixture"); }
                }
                break;
            case "ProjectCpu":
                Put("average", count == 1 ? 10 : 40); Put("delta", count == 1 ? -10 : 20); Put("frequency_ghz", 3.75);
                Put("processes", 7); Put("threads", 19); Put("handles", 101);
                for (int i = 0; i < count; i += 4)
                    Put($"CORE {i}-{Math.Min(count - 1, i + 3)}", count == 1 ? new[] { 10 } : new[] { 10, 30, 50, 70 });
                break;
            case "SampleNetwork":
                Put("primary", count.ToString());
                for (int i = 1; i <= count; i++)
                {
                    string k = "adapter/" + i + "/";
                    Put(k + "rx", i * 100); Put(k + "tx", i * 50); Put(k + "session_rx", i * 200); Put(k + "session_tx", i * 100);
                    Put(k + "utilisation", i % 2 == 0 ? null : (double)Math.Round(i * 0.015m, 2));
                }
                break;
            case "SampleDisk":
                for (int i = 1; i <= count; i++)
                {
                    string k = "disk/" + i + "/";
                    Put(k + "read", i * 100); Put(k + "write", i * 50); Put(k + "session_read", i * 200); Put(k + "session_write", i * 100);
                    Put(k + "active", 25); Put(k + "queue", 0.5); Put(k + "model", "Physical Disk " + i); Put(k + "letters", Array.Empty<string>());
                }
                break;
            case "QueryHistory":
            case "SerializeHistory":
                int first = Math.Max(0, count - 1 - minutes * 200);
                var indices = Enumerable.Range(first, count - first).ToArray();
                result["count"] = indices.Length.ToString(CultureInfo.InvariantCulture);
                Put("metric", "cpu"); Put("minutes", minutes); Put("unit", "%");
                Put("points", indices.Select(i => new { timestamp = EngineClock.Anchor.UtcDateTime.AddMilliseconds(-(count - 1L - i) * 300), value = i % 100 + 0.25 }).ToArray());
                var values = indices.Select(i => i % 100 + 0.25).ToArray();
                Put("stats", new { min = values.Length == 0 ? 0 : values.Min(), max = values.Length == 0 ? 0 : values.Max(),
                    avg = values.Length == 0 ? 0 : Math.Round(values.Sum() / values.Length, 2), current = values.Length == 0 ? 0 : values[^1] });
                break;
            default: throw new InvalidDataException("Unknown engine reference: " + method);
        }
        return result;
    }

    public static string[] Compare(Dictionary<string, string> expected, Dictionary<string, string> actual) =>
        expected.Keys.Union(actual.Keys.Where(k => k is not ("entity_bytes" or "entity_body")), StringComparer.Ordinal)
            .Where(k => !expected.TryGetValue(k, out var e) || !actual.TryGetValue(k, out var a) || e != a).Order(StringComparer.Ordinal).ToArray();

    public static EngineInvocation[] Correlate(CaseIdentity identity, string runId, int pid, string[] stdout,
        Measurement[] measurements, long gcOperations)
    {
        void Require(bool valid, string reason) { if (!valid) throw new InvalidDataException("Engine correlation: " + reason); }
        Require(TypeFor(identity.Method) == identity.Type && identity.DatasetId == "engine-reference-v1" &&
            identity.CacheTopology == "engine-local" && identity.Scenario == "engine/" + identity.Method, "identity");
        int Parameter(string key)
        {
            if (!identity.Parameters.TryGetValue("parameter:" + key, out var text)) return 0;
            Require(text.StartsWith("System.Int32:", StringComparison.Ordinal), "parameter type");
            return int.Parse(text["System.Int32:".Length..], CultureInfo.InvariantCulture);
        }
        int count = Parameter("Count"), minutes = Parameter("Minutes");
        int[] counts = identity.Method switch
        {
            "ParseSnapshot" or "ProjectSnapshot" => [10, 100, 1000], "ProjectCpu" => [1, 8, 64],
            "SampleNetwork" => [1, 8, 32], "SampleDisk" => [1, 4, 16],
            "QueryHistory" or "SerializeHistory" => [0, 60, 1800, 10000], _ => [0]
        };
        bool history = identity.Method is "QueryHistory" or "SerializeHistory";
        Require(counts.Contains(count) && (history ? new[] { 5, 15, 30, 60 }.Contains(minutes) : minutes == 0) &&
            identity.Parameters.Keys.Order().SequenceEqual((IsLive(identity.Method) ? Array.Empty<string>() : history ?
                new[] { "parameter:Count", "parameter:Minutes" } : new[] { "parameter:Count" }).Order()) &&
            identity.Job == (IsLive(identity.Method) ? "engine-live-net10-windows-x64" : "engine-net10-windows-x64") &&
            identity.Tfm == "net10.0-windows" && identity.Architecture == "X64" && identity.Runtime == ".NET 10.0", "registered parameters/job");
        Require(pid > 0 && gcOperations == 1, "diagnostic shape");
        var ordinary = measurements.Where(m => m.IterationStage != IterationStage.Result).ToArray();
        Require(ordinary.Length == 4 && ordinary.Select((m, i) => m.IterationMode == IterationMode.Workload &&
            m.IterationStage == (i == 0 ? IterationStage.Warmup : IterationStage.Actual) && m.IterationIndex == (i == 0 ? 1 : i) &&
            m.LaunchIndex == 1 && m.Operations == 1 && double.IsFinite(m.Nanoseconds) && m.Nanoseconds >= 0).All(x => x), "measurement sequence");
        var results = measurements.Where(m => m.IterationStage == IterationStage.Result).ToArray();
        Require(results.Length == 3 && results.Select((m, i) => m.IterationMode == IterationMode.Workload && m.IterationIndex == i + 1 &&
            m.LaunchIndex == 1 && m.Operations == 1 && double.IsFinite(m.Nanoseconds) && m.Nanoseconds >= 0).All(x => x), "results");
        var records = stdout.Where(s => s.StartsWith(EngineIteration.Prefix, StringComparison.Ordinal))
            .Select(s => JsonSerializer.Deserialize<EngineInvocation>(s[EngineIteration.Prefix.Length..], ThresholdValidator.Json)!).ToArray();
        Require(records.Length == 5, "missing/duplicate invocations");
        Require(records.Select((r, i) => r.RunId == runId && r.ProcessId == pid && r.Invocation == i + 1 &&
            r.Method == identity.Method && r.Count == count && r.Minutes == minutes && Guid.TryParseExact(r.LaunchId, "N", out _) &&
            r.LaunchId == records[0].LaunchId).All(x => x), "invocation identity");
        foreach (var record in records)
        {
            if (identity.Method == "SerializeHistory")
            {
                Require(record.Actual.TryGetValue("entity_body", out var body), "serialized entity missing");
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Deserialize<string>(body!)!);
                var reconstructed = Capture("SerializeHistory", bytes);
                Require(record.Actual.Count == reconstructed.Count && reconstructed.All(p => record.Actual.GetValueOrDefault(p.Key) == p.Value),
                    "serialized entity/count disagreement");
            }
            else Require(!record.Actual.ContainsKey("entity_body") && !record.Actual.ContainsKey("entity_bytes"), "unexpected entity observation");
            if (IsLive(identity.Method))
                Require(record.Actual.GetValueOrDefault("query_succeeded") == "true" &&
                    int.TryParse(record.Actual.GetValueOrDefault("count"), out var population) && population >= 0, "native query observation");
        }
        return records;
    }

    public static CaseResult Apply(CaseResult result, EngineInvocation[]? records)
    {
        bool live = IsLive(result.Identity.Method);
        string correctness = records == null ? "not_checked" : live ? "not_checked" : records.Any(r =>
            Compare(Expected(r.Method, r.Count, r.Minutes), r.Actual).Length > 0) ? "failed" : "passed";
        return result with
        {
            CorrectnessStatus = correctness,
            CorrectnessBasis = live ? "Observed native population/query only; hardware accuracy has no independent reference." :
                "Independent fixed input formulas compared with every measured, warmup and diagnostic output; missing correlation is not checked.",
            ContractChecks = new() { ["child_provenance"] = result.ExecutionStatus == "succeeded" ? "passed" : "failed",
                ["invocation_correlation"] = records == null ? "failed" : "passed" },
            Metrics = result.Metrics.Select(m => m with { CorrectnessStatus = correctness }).ToArray()
        };
    }
}
