using System.Text.Json.Serialization;

namespace GSAnalyzer.Benchmarks.Models;

public sealed record BenchmarkPolicy
{
    public required int SchemaVersion { get; init; }
    public required BenchmarkProfile[] Profiles { get; init; }
}

public sealed record BenchmarkProfile
{
    public required string Id { get; init; }
    public required string Purpose { get; init; }
    public required string[] Types { get; init; }
    public required CasePolicy[] Cases { get; init; }
}

public sealed record CaseIdentity
{
    public required string Type { get; init; }
    public required string Method { get; init; }
    public required SortedDictionary<string, string> Parameters { get; init; }
    public required string Job { get; init; }
    public required string Runtime { get; init; }
    public required string Tfm { get; init; }
    public required string Architecture { get; init; }
    public required string Scenario { get; init; }
    public required string CacheTopology { get; init; }
    public required string DatasetId { get; init; }

    [JsonIgnore]
    public string Id => string.Join("|", new[] { Type, Method,
        string.Join(";", Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))),
        Job, Runtime, Tfm, Architecture, Scenario, CacheTopology, DatasetId }.Select(Uri.EscapeDataString));
}

public sealed record CasePolicy
{
    public required CaseIdentity Identity { get; init; }
    public required bool Required { get; init; }
    public string? OptionalReason { get; init; }
    public required string[] ContractChecks { get; init; }
    public required MetricPolicy[] Metrics { get; init; }
}

public sealed record MetricPolicy
{
    public required string Metric { get; init; }
    public required string Unit { get; init; }
    public required string Direction { get; init; }
    public required string Statistic { get; init; }
    public required bool Required { get; init; }
    public required string BudgetStatus { get; init; }
    public required string Owner { get; init; }
    public double? Lower { get; init; }
    public double? Upper { get; init; }
}

public sealed record MetricResult
{
    public required string CaseId { get; init; }
    public required string Scenario { get; init; }
    public required string DatasetId { get; init; }
    public required string Metric { get; init; }
    public required string Unit { get; init; }
    public required string Direction { get; init; }
    public required string MeasurementStatus { get; init; }
    public required double? Value { get; init; }
    public required long SampleCount { get; init; }
    public required string Statistic { get; init; }
    public required string CorrectnessStatus { get; init; }
    public string GateDecision { get; init; } = "inconclusive";
    public required string Reason { get; init; }
    public required string RawArtifact { get; init; }
}

public sealed record CaseResult
{
    public required CaseIdentity Identity { get; init; }
    public required string ExecutionStatus { get; init; }
    public required string[] ExecutionErrors { get; init; }
    public required int LaunchCount { get; init; }
    public required Dictionary<string, string> ContractChecks { get; init; }
    public required string CorrectnessStatus { get; init; }
    public required string CorrectnessBasis { get; init; }
    public required MetricResult[] Metrics { get; init; }
}

public sealed record RunEvidence
{
    public required int SchemaVersion { get; init; }
    public required string RunId { get; init; }
    public required string ProfileId { get; init; }
    public required string ConfigSha256 { get; init; }
    public required CaseIdentity[] Discovered { get; init; }
    public required CaseResult[] Cases { get; init; }
    public required string[] CollectionErrors { get; init; }
}

public sealed record CoverageResult(string[] Expected, string[] Discovered, string[] Launched,
    string[] Executed, string[] Measured, string[] Missing, string[] Extra, Dictionary<string, string> OptionalReasons);

public sealed record ValidationResult(string Mode, string Purpose, string Decision, string[] Failures,
    string[] Warnings, CoverageResult Coverage, CaseResult[] Cases)
{
    [JsonIgnore] public bool Success => Decision == "pass";
}

public sealed record SourceIdentity(string Sha, bool Dirty);
public sealed record ArtifactEntry(string Path, long Bytes, string Sha256);
public sealed record RunManifest(int SchemaVersion, string RunId, string ProfileId, string Purpose,
    string Mode, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, SourceIdentity Core,
    SourceIdentity Harness, string ConfigSha256, string DatasetSha256,
    Dictionary<string, string> Environment, ArtifactEntry[] Artifacts);
