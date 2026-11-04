using System.Text.RegularExpressions;
using BenchmarkDotNet.Running;
using GSAnalyzer.Benchmarks.Core;
using GSAnalyzer.Benchmarks.Models;

namespace GSAnalyzer.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string? root = null;
        string originalDirectory = Environment.CurrentDirectory;
        string? originalCore = Environment.GetEnvironmentVariable("CoreRepoPath");
        string? originalRun = Environment.GetEnvironmentVariable("GS_BENCH_RUN_ID");
        try
        {
            var options = Parse(args);
            if (options.TryGetValue("aggregate", out string? states))
                return ThresholdValidator.Aggregate(states.Split(',')) ? 0 : 1;
            string mode = Required(options, "mode");
            if (mode is not ("discover" or "report" or "gate")) throw new InvalidDataException("--mode must be discover, report or gate.");
            string profileId = Required(options, "profile");
            string runId = Guid.NewGuid().ToString("N");
            string output = Path.GetFullPath(options.GetValueOrDefault("output") ?? Path.Combine(AppContext.BaseDirectory, "runs"));
            string? input = options.TryGetValue("evidence", out string? suppliedEvidence) ? Path.GetFullPath(suppliedEvidence) : null;
            if (input != null)
            {
                string relative = Path.GetRelativePath(input, output);
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("--output must be outside --evidence; no output was created.");
                    return 1;
                }
            }
            root = Path.Combine(output, "run-" + runId);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "raw"));
            Console.WriteLine("Evidence: " + root);
            string configPath = Path.GetFullPath(options.GetValueOrDefault("config") ?? Path.Combine(AppContext.BaseDirectory, "thresholds.json"));
            var policy = ThresholdValidator.Read<BenchmarkPolicy>(configPath);
            var profile = ThresholdValidator.Profile(policy, profileId);
            string configHash = BenchmarkResults.Hash(configPath);

            if (input != null)
            {
                var manifest = ThresholdValidator.Read<RunManifest>(Path.Combine(input, "manifest.json"));
                if (options.TryGetValue("manifest-sha", out string? trustedHash) && BenchmarkResults.Hash(Path.Combine(input, "manifest.json")) != trustedHash)
                    throw new InvalidDataException("Manifest differs from the supplied external hash.");
                BenchmarkResults.VerifyInventory(input, manifest);
                var evidence = ThresholdValidator.Read<RunEvidence>(Path.Combine(input, "case-results.json"));
                if (BenchmarkResults.Hash(Path.Combine(input, "policy.json")) != configHash || manifest.ConfigSha256 != configHash ||
                    manifest.ProfileId != profileId || manifest.Purpose != profile.Purpose ||
                    manifest.DatasetSha256 != BenchmarkResults.DatasetHash(profile.Cases.Select(c => c.Identity)))
                    throw new InvalidDataException("Evidence manifest does not match the selected policy/profile/dataset.");
                if (manifest.Mode == "discover" && mode != "discover") throw new InvalidDataException("Discovery cannot satisfy execution evidence.");
                BenchmarkResults.VerifyExecution(input, manifest, evidence);
                var result = ThresholdValidator.Evaluate(profile, evidence, mode, manifest.RunId, configHash, input);
                BenchmarkResults.Write(Path.Combine(root, "validation.json"), result);
                Console.WriteLine($"Read-only evidence validation: {result.Decision}; checksum consistency does not establish trusted provenance.");
                return result.Success ? 0 : 1;
            }

            DateTimeOffset start = DateTimeOffset.UtcNow;
            var runs = BenchmarkResults.Discover(profile, Path.Combine(root, "raw"));
            var discovered = runs.SelectMany(r => r.BenchmarksCases).Select(BenchmarkResults.Identity).ToArray();
            var collectionErrors = new List<string>();
            if (options.TryGetValue("filter", out string? filter))
            {
                var expression = new Regex("^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.CultureInvariant);
                if (discovered.Any(c => !expression.IsMatch(c.Type + "." + c.Method)))
                    collectionErrors.Add("Filter removes declared profile coverage. Select a checked-in subset profile instead.");
            }
            var run = new RunEvidence
            {
                SchemaVersion = 1, RunId = runId, ProfileId = profileId, ConfigSha256 = configHash,
                Discovered = discovered, Cases = [], CollectionErrors = collectionErrors.ToArray()
            };
            var discovery = ThresholdValidator.Evaluate(profile, run, "discover", runId, configHash, root);
            var environment = BenchmarkResults.LoadedIdentity();
            SourceIdentity coreSource = new("not_collected", false), harnessSource = new("not_collected", false);
            if (mode != "discover" && discovery.Success)
            {
                if (runs.Any(r => !BenchmarkResults.CanExecute(r.Type)))
                    collectionErrors.Add("Legacy storage execution is disabled until source-registered owned fixtures are implemented.");
                else if (discovered.Any(c => c.Tfm != BenchmarkResults.CurrentTfm))
                    collectionErrors.Add("Selected profile requires a different parent target framework.");
                else
                {
                    string source = BenchmarkResults.ResolveSource(options.GetValueOrDefault("source-project"));
                    string core = Path.GetFullPath(options.GetValueOrDefault("core-repo") ?? originalCore ??
                        throw new InvalidDataException("Set CoreRepoPath or --core-repo to the matching checkout."));
                    environment = BenchmarkResults.Preflight(source, core);
                    coreSource = BenchmarkResults.Source(core);
                    harnessSource = BenchmarkResults.Source(Path.GetDirectoryName(source)!);
                    Environment.SetEnvironmentVariable("GS_BENCH_RUN_ID", runId);
                    Environment.SetEnvironmentVariable("CoreRepoPath", core);
                    if (options.ContainsKey("selftest-build-failure"))
                    {
                        if (!profile.Id.StartsWith("selftest-", StringComparison.Ordinal)) throw new InvalidDataException("Build-failure control is synthetic-only.");
                        Environment.SetEnvironmentVariable("CoreRepoPath", Path.Combine(root, "intentionally-missing-core"));
                    }
                    Environment.CurrentDirectory = Path.GetDirectoryName(source)!;
                    var summaries = BenchmarkRunner.Run(runs);
                    collectionErrors.AddRange(summaries.SelectMany(s => s.ValidationErrors).Where(e => e.IsCritical).Select(e => e.Message));
                    run = run with { Cases = summaries.SelectMany(s => s.Reports)
                        .Select(r => BenchmarkResults.Normalize(r, runId, root, environment, source)).ToArray() };
                    if (BenchmarkResults.SourceHash(Path.GetDirectoryName(source)!) != environment["harness_source_sha256"] ||
                        BenchmarkResults.SourceHash(Path.Combine(core, "backend")) != environment["core_source_sha256"])
                        collectionErrors.Add("Source inputs changed during child collection.");
                    if (!Directory.EnumerateFiles(Path.Combine(root, "raw"), "*-report-full.json", SearchOption.AllDirectories).Any())
                        collectionErrors.Add("Original BDN full JSON is missing.");
                }
            }
            run = run with { CollectionErrors = collectionErrors.ToArray() };
            var validation = ThresholdValidator.Evaluate(profile, run, mode, runId, configHash, root);
            if (collectionErrors.Count > 0) BenchmarkResults.Write(Path.Combine(root, "raw", "collection-errors.json"), collectionErrors);
            var completed = BenchmarkResults.FinalizeRun(root,
                new(1, runId, profileId, profile.Purpose, mode, start, start, coreSource, harnessSource,
                    configHash, BenchmarkResults.DatasetHash(profile.Cases.Select(c => c.Identity)), environment, []), configPath, run, validation);
            BenchmarkResults.VerifyInventory(root, completed);
            BenchmarkResults.VerifyExecution(root, completed, run);
            foreach (string failure in validation.Failures) Console.Error.WriteLine(failure);
            Console.WriteLine($"{mode}/{profile.Purpose}: {validation.Decision}. Manifest SHA256: {BenchmarkResults.Hash(Path.Combine(root, "manifest.json"))}");
            return validation.Success ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Benchmark invocation failed: " + error.Message);
            try
            {
                if (root == null)
                {
                    root = Path.Combine(AppContext.BaseDirectory, "runs", "run-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                }
                BenchmarkResults.Write(Path.Combine(root, "failure.json"), new { decision = "fail", error = error.ToString() });
                Console.Error.WriteLine("Failure artifact: " + Path.Combine(root, "failure.json"));
            }
            catch (Exception writeError) { Console.Error.WriteLine("Failure artifact could not be written: " + writeError.Message); }
            return 1;
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("CoreRepoPath", originalCore);
            Environment.SetEnvironmentVariable("GS_BENCH_RUN_ID", originalRun);
        }
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("--" + name + " is required.");

    private static Dictionary<string, string> Parse(string[] args)
    {
        string[] names = ["mode", "profile", "config", "output", "source-project", "core-repo", "filter", "evidence", "manifest-sha", "aggregate"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--selftest-build-failure") { options.Add("selftest-build-failure", "true"); continue; }
            if (!args[i].StartsWith("--") || !names.Contains(args[i][2..]) || i + 1 == args.Length)
                throw new InvalidDataException("Unknown or incomplete option: " + args[i]);
            options.Add(args[i][2..], args[++i]);
        }
        return options;
    }
}
