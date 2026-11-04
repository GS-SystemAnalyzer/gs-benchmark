using System.Text.Json;
using System.Text.Json.Serialization;
using GSAnalyzer.Benchmarks.Models;

namespace GSAnalyzer.Benchmarks.Core;

public static class ThresholdValidator
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public static readonly IReadOnlyDictionary<string, (string Unit, string Direction, string Statistic)> Metrics =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["mean"] = ("ns/op", "lower", "mean"),
            ["allocated"] = ("B/op", "lower", "bytes_per_operation"),
            ["uncertainty"] = ("ci999_half_width_ns_per_op", "lower", "ci999_half_width")
        };

    public static T Read<T>(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        RejectDuplicates(doc.RootElement);
        return doc.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("Null document: " + path);
    }

    private static void RejectDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON property: " + p.Name);
                RejectDuplicates(p.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new InvalidDataException("Null array entry.");
                RejectDuplicates(item);
            }
    }

    public static BenchmarkProfile Profile(BenchmarkPolicy policy, string id)
    {
        Require(policy.SchemaVersion == 1, "Unsupported policy schema.");
        Require(policy.Profiles.Length > 0, "Empty policy.");
        Unique(policy.Profiles.Select(p => p.Id), "profile");
        foreach (var p in policy.Profiles)
        {
            Require(p.Purpose is "contract" or "performance" or "release", "Unknown profile purpose: " + p.Purpose);
            Require(p.Types.Length > 0 && p.Cases.Length > 0, "Empty profile: " + p.Id);
            Unique(p.Types, "type");
            Unique(p.Cases.Select(c => c.Identity.Id), "case");
            Require(p.Cases.Any(c => c.Required), "Profile has no required cases: " + p.Id);
            foreach (var c in p.Cases)
            {
                ValidateIdentity(c.Identity);
                Require(p.Types.Contains(c.Identity.Type, StringComparer.Ordinal), "Case outside profile scope.");
                Require(c.Required || !string.IsNullOrWhiteSpace(c.OptionalReason), "Optional case needs a reason.");
                Require(c.ContractChecks.Length > 0, "Case has no required contract checks.");
                Unique(c.ContractChecks, "contract check");
                Unique(c.Metrics.Select(m => m.Metric), "metric policy");
                Require(c.Metrics.Any(m => m.Required), "Case has no required metric.");
                foreach (var m in c.Metrics)
                {
                    Require(Metrics.TryGetValue(m.Metric, out var definition) &&
                        definition == (m.Unit, m.Direction, m.Statistic), "Unknown metric/unit/direction/statistic: " + m.Metric);
                    Require(m.BudgetStatus is "approved" or "historical" or "tbd", "Unknown budget status.");
                    Require(!string.IsNullOrWhiteSpace(m.Owner), "Budget owner/provenance absent.");
                    Require(ValidBound(m.Lower) && ValidBound(m.Upper), "Nonfinite or negative bound.");
                    Require(m.Lower is null || m.Upper is null || m.Lower <= m.Upper, "Inverted bounds.");
                    Require(m.BudgetStatus != "approved" || m.Lower is not null || m.Upper is not null, "Approved budget has no bound.");
                    Require(m.BudgetStatus != "tbd" || m.Lower is null && m.Upper is null, "TBD budget cannot contain limits.");
                }
            }
        }
        return policy.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Unknown profile: " + id);
    }

    public static ValidationResult Evaluate(BenchmarkProfile profile, RunEvidence run, string mode,
        string expectedRunId, string configSha256, string artifactRoot)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        void Check(bool ok, string reason) { if (!ok) failures.Add(reason); }
        Check(mode is "discover" or "report" or "gate", "Unknown mode.");
        Check(run.SchemaVersion == 1, "Unknown evidence schema.");
        Check(run.RunId == expectedRunId && Guid.TryParseExact(run.RunId, "N", out _), "Run identity mismatch.");
        Check(run.ProfileId == profile.Id, "Profile identity mismatch.");
        Check(run.ConfigSha256 == configSha256 && configSha256.Length == 64, "Configuration hash mismatch.");
        failures.AddRange(run.CollectionErrors);
        string[] expected = profile.Cases.Select(c => c.Identity.Id).ToArray();
        string[] discovered = run.Discovered.Select(c => c.Id).ToArray();
        string[] observed = run.Cases.Select(c => c.Identity.Id).ToArray();
        Check(discovered.Length > 0, "Empty discovery.");
        Check(discovered.Distinct(StringComparer.Ordinal).Count() == discovered.Length, "Duplicate discovered identities.");
        Check(observed.Distinct(StringComparer.Ordinal).Count() == observed.Length, "Duplicate result identities.");
        var missing = expected.Except(discovered).ToList();
        var extra = discovered.Concat(observed).Except(expected).Distinct().ToArray();
        foreach (string id in extra) failures.Add("Undeclared case: " + id);
        foreach (string id in missing) failures.Add("Expected case not discovered: " + id);
        foreach (string id in observed.Except(discovered)) failures.Add("Executed case was not discovered: " + id);

        var evaluated = new List<CaseResult>();
        foreach (var result in run.Cases)
        {
            string id = result.Identity.Id;
            // Execution failures are independent of policy membership.
            Check(result.ExecutionStatus is "succeeded" or "not_supported", "Execution failed: " + id);
            failures.AddRange(result.ExecutionErrors.Select(e => id + ": " + e));
            Check(result.LaunchCount >= 0, "Negative launch count: " + id);
            Check(result.CorrectnessStatus is "passed" or "failed" or "not_checked", "Unknown correctness status: " + id);
            Check(result.CorrectnessStatus != "failed", "Product correctness failed: " + id);
            Check(result.ContractChecks.All(c => !string.IsNullOrWhiteSpace(c.Key) && c.Value is "passed" or "not_checked"),
                "Failed/invalid contract check: " + id);
            Check(!string.IsNullOrWhiteSpace(result.CorrectnessBasis), "Correctness basis missing: " + id);
            Check(result.Metrics.Select(m => m.Metric).Distinct().Count() == result.Metrics.Length, "Duplicate metric: " + id);
            var policy = profile.Cases.SingleOrDefault(c => c.Identity.Id == id);
            if (policy == null) continue;
            bool optionalUnsupported = result.ExecutionStatus == "not_supported" && !policy.Required;
            if (result.ExecutionStatus == "not_supported")
            {
                Check(!policy.Required && !string.IsNullOrWhiteSpace(policy.OptionalReason), "Required case unsupported: " + id);
                Check(result.LaunchCount == 0 && result.Metrics.All(m => m.MeasurementStatus == "not_supported" && m.Value == null),
                    "Unsupported case contains measurements: " + id);
                warnings.Add("Optional capability unsupported: " + id + ": " + policy.OptionalReason);
            }
            else
            {
                Check(result.LaunchCount > 0, "No launches: " + id);
                foreach (string check in policy.ContractChecks)
                    Check(result.ContractChecks.TryGetValue(check, out string? status) && status == "passed", "Contract check missing/failed: " + check + ": " + id);
                if (mode == "gate" && policy.Required && profile.Purpose != "contract")
                    Check(result.CorrectnessStatus == "passed", "Independent product correctness not passed: " + id);
            }

            foreach (var metric in policy.Metrics.Where(m => m.Required && !optionalUnsupported))
                Check(result.Metrics.Any(m => m.Metric == metric.Metric), "Required metric absent: " + metric.Metric + ": " + id);
            var values = new List<MetricResult>();
            foreach (var metric in result.Metrics)
            {
                string decision = "inconclusive";
                string reason = metric.Reason;
                var budget = policy.Metrics.SingleOrDefault(m => m.Metric == metric.Metric);
                bool valid = Metrics.TryGetValue(metric.Metric, out var definition) &&
                    definition == (metric.Unit, metric.Direction, metric.Statistic) &&
                    metric.CaseId == id && metric.DatasetId == result.Identity.DatasetId &&
                    metric.Scenario == result.Identity.Scenario && metric.CorrectnessStatus == result.CorrectnessStatus &&
                    metric.SampleCount >= 0 && metric.MeasurementStatus is "measured" or "not_measured" or "not_supported" or "failed";
                if (metric.MeasurementStatus == "measured")
                    valid &= metric.Value is double value && double.IsFinite(value) && value >= 0 &&
                        metric.SampleCount > 0 && RawExists(artifactRoot, metric.RawArtifact);
                else
                    valid &= metric.Value == null && metric.SampleCount == 0 && !string.IsNullOrWhiteSpace(metric.Reason);
                if (!valid || budget == null || metric.MeasurementStatus == "failed")
                {
                    decision = "fail";
                    reason = "Invalid or undeclared metric evidence.";
                    failures.Add(reason + " " + id + ": " + metric.Metric);
                }
                else if (metric.MeasurementStatus != "measured")
                {
                    if (policy.Required && budget.Required || result.ExecutionStatus != "not_supported" && budget.Required)
                        failures.Add("Required evidence missing: " + id + ": " + metric.Metric);
                }
                else if (budget.BudgetStatus != "approved")
                {
                    decision = "warn";
                    reason = "Provisional budget: " + budget.BudgetStatus;
                    warnings.Add(reason + ": " + id + ": " + metric.Metric);
                    if (mode == "gate" && profile.Purpose != "contract" && policy.Required && budget.Required)
                        failures.Add("Required budget not approved: " + id + ": " + metric.Metric);
                }
                else if (!policy.Required || !budget.Required)
                {
                    decision = "warn";
                    reason = "Report-only observation: provisional; " +
                        (metric.Value < budget.Lower || metric.Value > budget.Upper ? "outside" : "within") + " inclusive limits.";
                    warnings.Add(reason + " " + id + ": " + metric.Metric);
                }
                else if (metric.Value < budget.Lower || metric.Value > budget.Upper)
                {
                    decision = budget.Required && policy.Required ? "fail" : "warn";
                    reason = "Outside inclusive limits.";
                    if (decision == "fail" && mode == "gate") failures.Add(reason + " " + id + ": " + metric.Metric);
                    else warnings.Add(reason + " " + id + ": " + metric.Metric);
                }
                else { decision = "pass"; reason = "Within inclusive limits."; }
                values.Add(metric with { GateDecision = decision, Reason = reason });
            }
            evaluated.Add(result with { Metrics = values.ToArray() });
        }
        if (mode != "discover")
        {
            Check(observed.Length > 0, "Empty execution results.");
            foreach (string id in expected.Except(observed))
            {
                failures.Add("Expected case did not execute: " + id);
                missing.Add(id);
            }
        }
        var coverage = new CoverageResult(expected, discovered,
            run.Cases.Where(c => c.LaunchCount > 0).Select(c => c.Identity.Id).ToArray(),
            run.Cases.Where(c => c.ExecutionStatus == "succeeded").Select(c => c.Identity.Id).ToArray(),
            evaluated.Where(c => c.ExecutionStatus == "succeeded" && profile.Cases.Single(p => p.Identity.Id == c.Identity.Id)
                .Metrics.Where(m => m.Required).All(m => c.Metrics.Any(r => r.Metric == m.Metric && r.MeasurementStatus == "measured" &&
                    r.Value is double v && double.IsFinite(v) && v >= 0 && r.SampleCount > 0 && RawExists(artifactRoot, r.RawArtifact))))
                .Select(c => c.Identity.Id).ToArray(),
            missing.Distinct().ToArray(), extra,
            profile.Cases.Where(c => !c.Required).ToDictionary(c => c.Identity.Id, c => c.OptionalReason!));
        return new(mode, profile.Purpose, failures.Count == 0 ? "pass" : "fail", failures.ToArray(), warnings.ToArray(), coverage, evaluated.ToArray());
    }

    public static bool Aggregate(IEnumerable<string> requiredStates) =>
        requiredStates.Any() && requiredStates.All(s => s == "success");

    public static bool RawExists(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static bool ValidBound(double? v) => v == null || double.IsFinite(v.Value) && v >= 0;
    private static void ValidateIdentity(CaseIdentity c)
    {
        Require(new[] { c.Type, c.Method, c.Job, c.Runtime, c.Tfm, c.Architecture, c.Scenario, c.CacheTopology, c.DatasetId }
            .All(s => !string.IsNullOrWhiteSpace(s)), "Incomplete case identity.");
        Require(c.Parameters.All(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value != null), "Invalid parameter identity.");
    }

    private static void Unique(IEnumerable<string> values, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values) Require(!string.IsNullOrWhiteSpace(value) && seen.Add(value), "Empty/duplicate " + kind + ": " + value);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }
}
