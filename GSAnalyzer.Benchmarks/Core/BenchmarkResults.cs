using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using GSAnalyzer.Benchmarks.Models;
using GSSystemAnalyzer.Engine;
using Perfolizer.Mathematics.Common;

namespace GSAnalyzer.Benchmarks.Core;

public static class BenchmarkResults
{
    public const string ProvenancePrefix = "// GS_PROVENANCE ";
    public const string SdkVersion = "10.0.302";
    public static string CurrentTfm => typeof(BenchmarkResults).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BenchmarkTfm").Value!;

    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, ThresholdValidator.Json) + "\n");

    public static Dictionary<string, string> LoadedIdentity() => new()
    {
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["tfm"] = CurrentTfm,
        ["core_path"] = typeof(DiskScannerEngine).Assembly.Location,
        ["core_sha256"] = Hash(typeof(DiskScannerEngine).Assembly.Location),
        ["core_framework"] = typeof(DiskScannerEngine).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName,
        ["core_platform"] = typeof(DiskScannerEngine).Assembly.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName ?? "portable",
        ["harness_sha256"] = Hash(typeof(BenchmarkResults).Assembly.Location),
        ["bdn"] = typeof(BenchmarkRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
        ["process_id"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
    };

    public static void WriteChildProvenance()
    {
        var identity = LoadedIdentity();
        identity["run_id"] = Environment.GetEnvironmentVariable("GS_BENCH_RUN_ID") ?? "missing";
        Console.WriteLine(ProvenancePrefix + JsonSerializer.Serialize(identity));
    }

    public static string Command(string directory, string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start " + file);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(file + " exceeded 60 seconds.");
        }
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException(file + " failed: " + error + output);
        return output.Trim();
    }

    public static SourceIdentity Source(string directory) => new(Command(directory, "git", "rev-parse", "HEAD"),
        Command(directory, "git", "status", "--porcelain", "--untracked-files=normal").Length > 0);

    public static string SourceHash(string directory) => HashText(string.Join("\n",
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".cs" or ".csproj" or ".props" or ".targets" or ".json")
            .Where(p => !Path.GetRelativePath(directory, p).Split(Path.DirectorySeparatorChar)
                .Any(s => s is "bin" or "obj" or ".git"))
            .Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(directory, p) + ":" + Hash(p))));

    public static string ResolveSource(string? source)
    {
        if (source != null)
        {
            string path = Path.GetFullPath(source);
            if (File.Exists(path) && Path.GetFileName(path) == "GSAnalyzer.Benchmarks.csproj") return path;
            throw new InvalidDataException("--source-project must name the matching GSAnalyzer.Benchmarks.csproj checkout.");
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "GSAnalyzer.Benchmarks.csproj");
            if (File.Exists(path)) return path;
        }
        throw new InvalidDataException("Benchmark execution needs a source checkout: supply --source-project. Binary-only output supports --evidence validation.");
    }

    public static Dictionary<string, string> Preflight(string source, string core)
    {
        if (!Path.IsPathFullyQualified(core) || !File.Exists(Path.Combine(core, "backend", "GSSystemAnalyzer.csproj")))
            throw new InvalidDataException("CoreRepoPath must be an absolute matching core checkout.");
        string directory = Path.GetDirectoryName(source)!;
        var identity = LoadedIdentity();
        string sourceAssembly = Path.Combine(directory, "bin", "Release", CurrentTfm, "GSAnalyzer.Benchmarks.dll");
        if (!File.Exists(sourceAssembly) || Hash(sourceAssembly) != identity["harness_sha256"])
            throw new InvalidDataException("Source checkout Release assembly does not match the invoked harness. Rebuild or publish this checkout.");
        string coreAssembly = Path.Combine(core, "backend", "bin", "Release", CurrentTfm, "GSSystemAnalyzer.dll");
        if (!File.Exists(coreAssembly) || Hash(coreAssembly) != identity["core_sha256"])
            throw new InvalidDataException("Loaded core assembly differs from CoreRepoPath/TFM. Rebuild with that checkout.");
        identity["sdk"] = Command(directory, "dotnet", "--version");
        if (identity["sdk"] != SdkVersion) throw new InvalidDataException("Unexpected SDK: " + identity["sdk"]);
        if (CurrentTfm.Contains("windows") && !identity["core_platform"].StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows harness loaded a portable core assembly.");
        identity["source_project"] = source;
        identity["core_repo"] = core;
        identity["harness_source_sha256"] = SourceHash(directory);
        identity["core_source_sha256"] = SourceHash(Path.Combine(core, "backend"));
        identity["os"] = RuntimeInformation.OSDescription;
        return identity;
    }

    public static string DatasetHash(IEnumerable<CaseIdentity> cases) => HashText(string.Join("\n",
        cases.Select(c => c.DatasetId).Distinct().Order(StringComparer.Ordinal)));

    public static void RequireSafeDiscovery(Type type)
    {
        var allowed = new[] { typeof(BenchmarkAttribute), typeof(GlobalSetupAttribute), typeof(GlobalCleanupAttribute),
            typeof(IterationSetupAttribute), typeof(IterationCleanupAttribute), typeof(ParamsAttribute),
            typeof(ArgumentsAttribute), typeof(MemoryDiagnoserAttribute), typeof(SimpleJobAttribute) };
        var attributes = type.Assembly.CustomAttributes.Concat(type.CustomAttributes)
            .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(m => m.CustomAttributes));
        foreach (var a in attributes)
            if (!allowed.Contains(a.AttributeType) && a.AttributeType.Assembly != typeof(object).Assembly &&
                a.AttributeType.Assembly != typeof(TargetFrameworkAttribute).Assembly)
                throw new InvalidDataException("Constructor-free discovery does not support attribute " + a.AttributeType.FullName);
        if (type.BaseType != typeof(object)) throw new InvalidDataException("Discovery requires a direct, declarative benchmark type.");
    }

    public static BenchmarkRunInfo[] Discover(BenchmarkProfile profile, string rawRoot)
    {
        if (profile.CollectionKind != "bdn") throw new InvalidDataException("BDN discovery requires collection_kind=bdn.");
        var runs = new List<BenchmarkRunInfo>();
        foreach (string typeName in profile.Types)
        {
            Type? type = typeof(BenchmarkResults).Assembly.GetType(typeName);
            if (type == null) continue;
            RequireSafeDiscovery(type);
            bool synthetic = type.Namespace == "GSAnalyzer.Benchmarks.Suites.Diagnostics";
            bool windows = !synthetic || type == typeof(Suites.Diagnostics.HarnessWindowsSmokeBenchmarks);
            string tfm = windows ? "net10.0-windows" : "net10.0";
            bool engine = EngineObservation.IsEngine(type);
            bool live = type == typeof(Suites.Memory.NativeRamBenchmarks) || type == typeof(Suites.Processing.NativeCpuBenchmarks);
            string jobName = engine ? (live ? "engine-live-net10-windows-x64" : "engine-net10-windows-x64") :
                synthetic ? (windows ? "contract-net10-windows-x64" : "contract-net10-x64") :
                type == typeof(Suites.Storage.CacheFootprintBenchmark) ? "legacy-net10-windows-x64" : "storage-net10-windows-x64";
            var config = ManualConfig.Create(DefaultConfig.Instance)
                .WithArtifactsPath(rawRoot)
                .WithOptions(ConfigOptions.KeepBenchmarkFiles | ConfigOptions.LogBuildOutput | ConfigOptions.GenerateMSBuildBinLog)
                .AddDiagnoser(MemoryDiagnoser.Default)
                .AddExporter(JsonExporter.Full, CsvMeasurementsExporter.Default);
            // Mutators preserve the legacy attribute's iteration strategy without adding a second job.
            var mutator = Job.Default.WithRuntime(CoreRuntime.Core10_0).WithPlatform(Platform.X64)
                .WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings(tfm, null, jobName)));
            if (synthetic)
                mutator = mutator.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(3)
                     .WithInvocationCount(1024).WithUnrollFactor(1);
            else mutator = mutator.WithUnrollFactor(1);
            config.AddJob(mutator.AsMutator());
            var converted = BenchmarkConverter.TypeToBenchmarks(type, config);
            var cases = converted.BenchmarksCases.Select(c => BenchmarkCase.Create(c.Descriptor,
                c.Job.WithId(jobName), c.Parameters, c.Config)).ToArray();
            runs.Add(new BenchmarkRunInfo(cases, type, converted.Config));
        }
        return runs.ToArray();
    }

    public static CaseIdentity Identity(BenchmarkCase c)
    {
        bool synthetic = c.Descriptor.Type.Namespace == "GSAnalyzer.Benchmarks.Suites.Diagnostics";
        bool engine = EngineObservation.IsEngine(c.Descriptor.Type);
        string method = c.Descriptor.WorkloadMethod.Name;
        string scenario = engine ? "engine/" + method : synthetic ? (method == "Throw" ? "synthetic-throw" : "synthetic-allocation") : method switch
        {
            "Footprint_FullStoragePipeline" => "legacy-pipeline",
            "ReturnToIdle_AfterClearCache" => "legacy-clear",
            _ => IsStorage(c.Descriptor.Type) ? StorageScenario(method) + "/" + StorageWorkload.OsCache : "unregistered"
        };
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in c.Parameters.Items)
        {
            Type type = p.Definition.ParameterType;
            if (type != typeof(string) && !type.IsPrimitive && type != typeof(decimal))
                throw new InvalidDataException("Unsupported typed parameter: " + type.FullName);
            parameters.Add((p.IsArgument ? "argument:" : "parameter:") + p.Name,
                type.FullName + ":" + JsonSerializer.Serialize(p.Value, type));
        }
        return new()
        {
            Type = c.Descriptor.Type.FullName!, Method = method, Parameters = parameters, Job = c.Job.Id,
            Runtime = c.Job.Environment.Runtime!.Name,
            Tfm = synthetic && c.Descriptor.Type != typeof(Suites.Diagnostics.HarnessWindowsSmokeBenchmarks) ? "net10.0" : "net10.0-windows",
            Architecture = c.Job.Environment.Platform.ToString(), Scenario = scenario,
            CacheTopology = engine ? "engine-local" : synthetic ? "none" : scenario.StartsWith("legacy-") ? "legacy-shared" : "production-node-shared-analyzers",
            DatasetId = engine ? "engine-reference-v1" : synthetic ? "synthetic-v1" : scenario.StartsWith("legacy-") ? "legacy-storage-v1" :
                c.Parameters.Items.SingleOrDefault(p => p.Name == "Dataset")?.Value as string ?? "tiny-v1"
        };
    }

    public static bool CanExecute(Type type) => type == typeof(Suites.Diagnostics.HarnessSmokeBenchmarks) ||
        type == typeof(Suites.Diagnostics.HarnessThrowBenchmarks) || type == typeof(Suites.Diagnostics.HarnessWindowsSmokeBenchmarks) ||
        type == typeof(Suites.Storage.ScannerBenchmarks) || type == typeof(Suites.Storage.CacheFootprintBenchmark) ||
        type == typeof(Suites.Memory.CacheMemoryBenchmarks) || EngineObservation.IsEngine(type);

    private static bool IsStorage(Type type) => type == typeof(Suites.Storage.ScannerBenchmarks) ||
        type == typeof(Suites.Storage.CacheFootprintBenchmark) || type == typeof(Suites.Memory.CacheMemoryBenchmarks);

    public static string StorageScenario(string method) => method switch
    {
        "AppEmpty" or "PopulationRebuild" => "app_empty", "AppHit" => "app_hit", "ChangedRescan" => "changed_rescan",
        "PersistedRestore" => "persisted_restore", "WarmedAnalyzerHit" => "analyzer_hit", "ClearAction" => "clear_action",
        "PassiveAfterProductGc" => "passive_after_product_gc", "ForcedGcRetention" => "forced_gc_retention",
        "Footprint_FullStoragePipeline" => "legacy-pipeline", "ReturnToIdle_AfterClearCache" => "legacy-clear",
        _ => throw new InvalidDataException("Unregistered storage method: " + method)
    };

    public static CaseResult Normalize(BenchmarkReport report, string runId, string root,
        Dictionary<string, string> parent, string sourceProject)
    {
        var identity = Identity(report.BenchmarkCase);
        var errors = new List<string>();
        if (!report.Success) errors.Add("Benchmark report unsuccessful.");
        if (report.GenerateResult?.IsGenerateSuccess != true) errors.Add("Generation failed: " + report.GenerateResult?.GenerateException);
        if (report.BuildResult?.IsBuildSuccess != true) errors.Add("Build failed: " + report.BuildResult?.ErrorMessage);
        if (report.ExecuteResults.Count == 0) errors.Add("No child launches.");
        var build = report.BuildResult?.ArtifactsPaths ?? report.GenerateResult?.ArtifactsPaths;
        var childProofs = new List<Dictionary<string, string>>();
        foreach (var launch in report.ExecuteResults)
        {
            if (!launch.FoundExecutable || launch.ExitCode != 0 || !launch.IsSuccess || launch.Errors.Count > 0)
                errors.Add($"Launch failed: found={launch.FoundExecutable}, exit={launch.ExitCode}, result={launch.IsSuccess}: {string.Join(";", launch.Errors)}");
            var lines = launch.StandardOutput.Where(s => s.StartsWith(ProvenancePrefix, StringComparison.Ordinal)).ToArray();
            if (lines.Length != 1) { errors.Add("Missing/duplicate child provenance."); continue; }
            try
            {
                var child = JsonSerializer.Deserialize<Dictionary<string, string>>(lines[0][ProvenancePrefix.Length..])!;
                string binaries = build?.BinariesDirectoryPath ?? throw new InvalidDataException("No generated output directory.");
                if (child["run_id"] != runId || child["tfm"] != identity.Tfm ||
                    child["architecture"] != identity.Architecture || child["runtime"] != parent["runtime"] ||
                    child["process_id"] != launch.ProcessId?.ToString(CultureInfo.InvariantCulture) ||
                    child["core_platform"] != parent["core_platform"] || child["core_framework"] != parent["core_framework"] ||
                    Path.GetFullPath(child["core_path"]) != Path.GetFullPath(Path.Combine(binaries, "GSSystemAnalyzer.dll")) ||
                    Hash(child["core_path"]) != child["core_sha256"] ||
                    Hash(Path.Combine(binaries, "GSAnalyzer.Benchmarks.dll")) != child["harness_sha256"])
                    errors.Add("Child provenance does not match this run/build.");
                childProofs.Add(child);
            }
            catch (Exception e) { errors.Add("Invalid child provenance: " + e.Message); }
        }
        string key = HashText(identity.Id);
        string raw = "component-results/" + key + ".json";
        Directory.CreateDirectory(Path.Combine(root, "component-results"));
        if (build != null && File.Exists(build.ProjectFilePath))
        {
            string projectText = File.ReadAllText(build.ProjectFilePath);
            string sdk = Command(Path.GetDirectoryName(build.ProjectFilePath)!, "dotnet", "--version");
            var project = System.Xml.Linq.XDocument.Parse(projectText);
            if (sdk != SdkVersion || !project.Descendants().Any(e => e.Name.LocalName == "TargetFramework" && e.Value == identity.Tfm) ||
                !project.Descendants().Any(e => e.Name.LocalName == "ProjectReference" &&
                    Path.GetFullPath(e.Attribute("Include")?.Value ?? "") == sourceProject))
                errors.Add("Generated project/SDK/TFM differs from the selected source.");
            File.Copy(build.ProjectFilePath, Path.Combine(root, "component-results", key + ".csproj"));
            foreach (string file in new[] { build.BuildScriptFilePath, build.ProgramCodePath })
                if (File.Exists(file)) File.Copy(file, Path.Combine(root, "component-results", key + "-" + Path.GetFileName(file)));
            Write(Path.Combine(root, "component-results", key + "-build.json"), new { sdk, build.ProjectFilePath, sourceProject });
            foreach (string binlog in Directory.EnumerateFiles(build.BuildArtifactsDirectoryPath, "*.binlog", SearchOption.AllDirectories))
                File.Copy(binlog, Path.Combine(root, "component-results", key + "-" + Path.GetFileName(binlog)), overwrite: true);
        }
        else errors.Add("Generated project evidence missing.");
        bool storage = IsStorage(report.BenchmarkCase.Descriptor.Type);
        bool engine = EngineObservation.IsEngine(report.BenchmarkCase.Descriptor.Type);
        EngineInvocation[]? engineRecords = null;
        StorageInvocation[]? storageRecords = null;
        var job = report.BenchmarkCase.Job;
        var storageJob = !storage ? null : new { strategy = job.Run.RunStrategy.ToString(), job.Run.LaunchCount, job.Run.WarmupCount,
            job.Run.IterationCount, job.Run.InvocationCount, job.Run.UnrollFactor,
            report.BenchmarkCase.Descriptor.OperationsPerInvoke, gc_operations = report.GcStats.TotalOperations };
        if (storage)
        {
            try
            {
                if (storageJob!.strategy != "Monitoring" || storageJob.LaunchCount != 1 || storageJob.WarmupCount != 1 ||
                    storageJob.IterationCount != (identity.Scenario.StartsWith("legacy-") ? 8 : 3) || storageJob.InvocationCount != 1 ||
                    storageJob.UnrollFactor != 1 || storageJob.OperationsPerInvoke != 1 || report.ExecuteResults.Count != 1)
                    throw new InvalidDataException("Unsupported storage job shape.");
                var execution = report.ExecuteResults.Single();
                storageRecords = CorrelateStorage(identity, runId, execution.ProcessId ?? 0, execution.StandardOutput.ToArray(),
                    execution.Measurements.ToArray(), report.GcStats.TotalOperations);
            }
            catch (Exception error) { errors.Add("Storage correlation failed: " + error.Message); }
        }
        var engineJob = !engine ? null : new { strategy = job.Run.RunStrategy.ToString(), job.Run.LaunchCount, job.Run.WarmupCount,
            job.Run.IterationCount, job.Run.InvocationCount, job.Run.UnrollFactor,
            report.BenchmarkCase.Descriptor.OperationsPerInvoke, gc_operations = report.GcStats.TotalOperations };
        if (engine)
        {
            try
            {
                if (engineJob!.strategy != "Monitoring" || engineJob.LaunchCount != 1 || engineJob.WarmupCount != 1 ||
                    engineJob.IterationCount != 3 || engineJob.InvocationCount != 1 || engineJob.UnrollFactor != 1 ||
                    engineJob.OperationsPerInvoke != 1 || report.ExecuteResults.Count != 1)
                    throw new InvalidDataException("Unsupported engine job shape.");
                var execution = report.ExecuteResults.Single();
                engineRecords = EngineObservation.Correlate(identity, runId, execution.ProcessId ?? 0, execution.StandardOutput.ToArray(),
                    execution.Measurements.ToArray(), report.GcStats.TotalOperations);
            }
            catch (Exception error) { errors.Add("Engine correlation failed: " + error.Message); }
        }
        Write(Path.Combine(root, raw), new
        {
            run_id = runId, case_id = identity.Id, report.Success,
            generated = report.GenerateResult?.IsGenerateSuccess, built = report.BuildResult?.IsBuildSuccess,
            build_error = report.BuildResult?.ErrorMessage, errors, children = childProofs,
            launches = report.ExecuteResults.Select(e => new { e.FoundExecutable, e.ExitCode, e.ProcessId, e.Errors, e.StandardOutput, e.Results, e.PrefixedLines }),
            measurements = report.AllMeasurements.Select(m => new { m.LaunchIndex, m.IterationIndex, m.IterationMode, m.IterationStage, m.Operations, m.Nanoseconds }),
            storage_job = storageJob,
            engine_job = engineJob,
            storage_mapping = storageRecords?.Select(r => new { r.Invocation, stage = r.Invocation == 1 ? "warmup" :
                r.Invocation == storageRecords.Length ? "diagnostic" : "actual" })
        });
        var statistics = report.ResultStatistics;
        long? allocation = report.BenchmarkCase.Config.GetDiagnosers().Any(d => d is MemoryDiagnoser) && report.GcStats.TotalOperations > 0 &&
            report.Metrics.ContainsKey("Allocated Memory") ? report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) : null;
        double? uncertainty = statistics == null ? null : statistics.GetConfidenceInterval(ConfidenceLevel.L999).Margin;
        MetricResult Metric(string name, double? value, long count) => new()
        {
            CaseId = identity.Id, Scenario = identity.Scenario, DatasetId = identity.DatasetId, Metric = name,
            Unit = ThresholdValidator.Metrics[name].Unit, Direction = ThresholdValidator.Metrics[name].Direction,
            Statistic = ThresholdValidator.Metrics[name].Statistic,
            MeasurementStatus = value is double v && double.IsFinite(v) && v >= 0 && count > 0 ? "measured" : "not_measured",
            Value = value is double n && double.IsFinite(n) && n >= 0 && count > 0 ? value : null,
            SampleCount = value is double x && double.IsFinite(x) && x >= 0 && count > 0 ? count : 0,
            CorrectnessStatus = "not_checked", Reason = "BDN result; missing/nonfinite diagnostics remain unmeasured.", RawArtifact = raw
        };
        var normalized = new CaseResult
        {
            Identity = identity, ExecutionStatus = errors.Count == 0 ? "succeeded" : "failed", ExecutionErrors = errors.ToArray(),
            LaunchCount = report.ExecuteResults.Count, ContractChecks = new()
            {
                ["child_provenance"] = errors.Count == 0 ? "passed" : "failed",
                ["allocation_control"] = allocation >= 128 ? "passed" : "failed"
            },
            CorrectnessStatus = "not_checked", CorrectnessBasis = "Synthetic harness control; independent product correctness not checked.",
            Metrics = [Metric("mean", statistics?.Mean, statistics?.N ?? 0), Metric("allocated", allocation, allocation == null ? 0 : 1),
                Metric("uncertainty", uncertainty, statistics?.N ?? 0)]
        };
        return storage ? ApplyStorage(normalized, storageRecords) : engine ? EngineObservation.Apply(normalized, engineRecords) : normalized;
    }

    public static StorageInvocation[] CorrelateStorage(CaseIdentity identity, string runId, int processId,
        string[] stdout, Measurement[] measurements, long gcOperations)
    {
        void Require(bool valid, string reason) { if (!valid) throw new InvalidDataException(reason); }
        bool legacy = identity.Scenario.StartsWith("legacy-");
        int actualCount = legacy ? 8 : 3;
        Require(processId > 0 && gcOperations == 1, "Diagnostic invocation shape.");
        var ordinary = measurements.Where(m => m.IterationStage != IterationStage.Result).ToArray();
        Require(ordinary.Length == actualCount + 1, "Measurement count.");
        for (int i = 0; i < ordinary.Length; i++)
        {
            var m = ordinary[i];
            Require(m.IterationMode == IterationMode.Workload && m.IterationStage == (i == 0 ? IterationStage.Warmup : IterationStage.Actual) &&
                m.IterationIndex == (i == 0 ? 1 : i) && m.LaunchIndex == 1 && m.Operations == 1 &&
                double.IsFinite(m.Nanoseconds) && m.Nanoseconds >= 0, "Measurement sequence/operations.");
        }
        var results = measurements.Where(m => m.IterationStage == IterationStage.Result).ToArray();
        Require(results.Length == actualCount && results.Select((m, i) => m.IterationMode == IterationMode.Workload &&
            m.IterationIndex == i + 1 && m.LaunchIndex == 1 && m.Operations == 1 && double.IsFinite(m.Nanoseconds) &&
            m.Nanoseconds >= 0).All(x => x), "Result measurement sequence/operations.");
        var records = stdout.Where(s => s.StartsWith(StorageWorkload.Prefix, StringComparison.Ordinal))
            .Select(s => JsonSerializer.Deserialize<StorageInvocation>(s[StorageWorkload.Prefix.Length..], ThresholdValidator.Json)!).ToArray();
        Require(records.Length == actualCount + 2, "Missing/duplicate storage invocations.");
        var manifest = legacy ? StorageManifest.Legacy(true) : StorageManifest.Tiny(identity.DatasetId == "tiny-unicode-v1");
        if (!legacy && StorageScenario(identity.Method) == "changed_rescan")
            manifest = manifest with { Files = manifest.Files.Select(f => f.Path == "A/a.txt" ? f with { Bytes = 13 } : f).ToArray() };
        var expected = legacy ? StorageWorkload.LegacyExpected() : StorageWorkload.Expected(manifest, StorageScenario(identity.Method));
        for (int i = 0; i < records.Length; i++)
        {
            var r = records[i];
            Require(r.RunId == runId && r.ProcessId == processId && r.Method == identity.Method && r.Invocation == i + 1 &&
                r.Scenario == StorageScenario(identity.Method) && Guid.TryParseExact(r.LaunchId, "N", out _) &&
                r.LaunchId == records[0].LaunchId, "Storage invocation identity.");
            Require(JsonSerializer.Serialize(r.Manifest, ThresholdValidator.Json) == JsonSerializer.Serialize(manifest, ThresholdValidator.Json), "Storage manifest identity.");
            Require(r.Expected.Count == expected.Count && expected.All(p => r.Expected.TryGetValue(p.Key, out var values) && p.Value.SequenceEqual(values)), "Independent expected output.");
            Require(r.Differences.SequenceEqual(StorageOracle.Compare(expected, r.Actual)), "Storage discrepancies.");
            Require(Path.IsPathFullyQualified(r.Root) && Path.GetFullPath(r.Root) == r.Root &&
                Path.GetFileName(r.Root).StartsWith("gs-storage-", StringComparison.Ordinal) &&
                Guid.TryParseExact(Path.GetFileName(r.Root)["gs-storage-".Length..], "N", out _) &&
                r.CachePath == Path.Combine(r.Root, "state", "scanner_memory.json") &&
                r.Before.GetValueOrDefault("os_cache") == StorageWorkload.OsCache, "Storage ownership/state identity.");
            Require(legacy ? !r.Cleaned && stdout.Count(s => s == StorageWorkload.CleanupPrefix + JsonSerializer.Serialize(r.Root)) == 1 :
                r.Cleaned, "Storage cleanup evidence.");
            Require(r.StagesMs.ContainsKey(r.Scenario) && r.StagesMs.All(p => double.IsFinite(p.Value) && p.Value >= 0) &&
                r.StagesMs.Count == (r.Scenario is "passive_after_product_gc" or "forced_gc_retention" ? 2 : 1) &&
                (r.StagesMs.Count == 1 || r.StagesMs.ContainsKey("product_clear")), "Stage records.");
            bool residual = r.Scenario is "legacy-clear" or "forced_gc_retention" or "passive_after_product_gc";
            Require(residual ? r.BaselineBytes >= 0 && r.AfterBytes >= 0 : r.BaselineBytes == null && r.AfterBytes == null, "Managed snapshots.");
        }
        Require(legacy ? records.All(r => r.Root == records[0].Root) : records.Select(r => r.Root).Distinct().Count() == records.Length,
            "Invocation fixture ownership.");
        return records;
    }

    private static CaseResult ApplyStorage(CaseResult result, StorageInvocation[]? records)
    {
        bool legacy = result.Identity.Scenario.StartsWith("legacy-");
        string correctness = records == null || records.Any(r => r.Differences.Length > 0) ? "failed" : legacy ? "not_checked" : "passed";
        var metrics = result.Metrics.Select(m => m with { CorrectnessStatus = correctness }).ToList();
        void Add(string name, double? value, int count)
        {
            var d = ThresholdValidator.Metrics[name];
            metrics.Add(new() { CaseId = result.Identity.Id, DatasetId = result.Identity.DatasetId, Scenario = result.Identity.Scenario,
                Metric = name, Unit = d.Unit, Direction = d.Direction, Statistic = d.Statistic, Value = value,
                SampleCount = count, MeasurementStatus = value == null ? "not_measured" : "measured", CorrectnessStatus = correctness,
                Reason = "Validated child invocation evidence; residual maximum includes warmup and extra-stats diagnostic.",
                RawArtifact = result.Metrics[0].RawArtifact });
        }
        if (!legacy) Add("stage_duration", records?.Where(r => r.Invocation > 1 && r.Invocation < records.Length).Average(r => r.StagesMs[r.Scenario]), records == null ? 0 : records.Length - 2);
        if (StorageScenario(result.Identity.Method) is "legacy-clear" or "forced_gc_retention" or "passive_after_product_gc")
            Add("managed_residual", records?.Max(r => (double)(r.AfterBytes!.Value - r.BaselineBytes!.Value)), records?.Length ?? 0);
        return result with { Metrics = metrics.ToArray(), CorrectnessStatus = correctness,
            CorrectnessBasis = legacy ? "Legacy population contract only; no production-performance acceptance." : "Independent manifest output, persistence and actual traversal observations.",
            ContractChecks = new() { ["child_provenance"] = result.ExecutionStatus == "succeeded" ? "passed" : "failed",
                ["owned_fixture"] = records == null ? "failed" : "passed", ["invocation_correlation"] = records == null ? "failed" : "passed" } };
    }

    public static RunManifest FinalizeRun(string root, RunManifest manifest, string configPath,
        RunEvidence evidence, ValidationResult validation)
    {
        File.Copy(configPath, Path.Combine(root, "policy.json"));
        if (Hash(Path.Combine(root, "policy.json")) != manifest.ConfigSha256)
            throw new InvalidDataException("Policy changed during collection.");
        Write(Path.Combine(root, "case-results.json"), evidence with { Cases = validation.Cases });
        Write(Path.Combine(root, "coverage.json"), validation.Coverage);
        Write(Path.Combine(root, "validation.json"), validation);
        File.WriteAllText(Path.Combine(root, "scorecard.md"), $"# {manifest.ProfileId}\n\nMode: {validation.Mode}; purpose: {validation.Purpose}; decision: **{validation.Decision}**.\n\n" +
            "Contract-only results supply no product-performance or demo acceptance.\n\n" +
            string.Join("\n", validation.Failures.Select(f => "- FAIL: " + f)) + "\n" +
            string.Join("\n", validation.Warnings.Select(f => "- WARN: " + f)) +
            "\n\n[Coverage](coverage.json) · [Case evidence](case-results.json) · [Validation](validation.json) · [Raw evidence](raw/)\n");
        var artifacts = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => p != Path.Combine(root, "manifest.json"))
            .Order(StringComparer.Ordinal).Select(p => new ArtifactEntry(Path.GetRelativePath(root, p).Replace('\\', '/'), new FileInfo(p).Length, Hash(p))).ToArray();
        manifest = manifest with { EndedUtc = manifest.CollectionKind == "scenario" ? manifest.EndedUtc : DateTimeOffset.UtcNow, Artifacts = artifacts };
        Write(Path.Combine(root, "manifest.json"), manifest);
        return manifest;
    }

    public static void VerifyInventory(string root, RunManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.CollectionKind is not ("bdn" or "scenario") || manifest.Mode is not ("discover" or "report" or "gate") ||
            manifest.StartedUtc > manifest.EndedUtc || !Guid.TryParseExact(manifest.RunId, "N", out _))
            throw new InvalidDataException("Invalid manifest metadata.");
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.Path == "manifest.json" || !seen.Add(artifact.Path) || !ThresholdValidator.RawExists(root, artifact.Path))
                throw new InvalidDataException("Missing, duplicate or invalid artifact: " + artifact.Path);
            string path = Path.Combine(root, artifact.Path);
            if (new FileInfo(path).Length != artifact.Bytes || Hash(path) != artifact.Sha256)
                throw new InvalidDataException("Artifact checksum mismatch: " + artifact.Path);
        }
        foreach (string required in new[] { "policy.json", "case-results.json", "coverage.json", "validation.json", "scorecard.md" })
            if (!seen.Contains(required)) throw new InvalidDataException("Incomplete evidence bundle: " + required);
        if (manifest.Mode != "discover" && !seen.Any(p => p.StartsWith("raw/", StringComparison.Ordinal)))
            throw new InvalidDataException("Metadata-only bundle has no raw evidence.");
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => p != "manifest.json");
        if (!seen.SetEquals(actual)) throw new InvalidDataException("Artifact inventory does not match the bundle.");
    }

    public static void VerifyExecution(string root, RunManifest manifest, RunEvidence evidence)
    {
        if (manifest.CollectionKind != "bdn" || evidence.CollectionKind != "bdn") throw new InvalidDataException("BDN replay requires collection_kind=bdn.");
        var successful = evidence.Cases.Where(c => c.ExecutionStatus == "succeeded").ToArray();
        if (successful.Length == 0) return;
        void Require(bool valid, string reason)
        {
            if (!valid) throw new InvalidDataException("Execution evidence mismatch: " + reason);
        }
        bool Hex(string? text, int length) => text?.Length == length && text.All(Uri.IsHexDigit);
        var parent = manifest.Environment;
        Require(manifest.Mode != "discover" && evidence.RunId == manifest.RunId, "run/mode");
        Require(Hex(manifest.Core.Sha, 40) && Hex(manifest.Harness.Sha, 40), "source revisions");
        string[] fields = ["sdk", "runtime", "bdn", "architecture", "tfm", "core_path", "core_framework", "core_platform",
            "core_sha256", "harness_sha256", "process_id", "source_project", "core_repo", "os", "harness_source_sha256", "core_source_sha256"];
        Require(fields.All(k => parent.TryGetValue(k, out string? value) && !string.IsNullOrWhiteSpace(value)), "environment identities");
        Require(parent["sdk"] == SdkVersion && parent["bdn"].Split('+')[0] == "0.15.8" &&
            parent["runtime"].StartsWith(".NET 10.0.", StringComparison.Ordinal) && parent["architecture"] == "X64" &&
            parent["tfm"] is "net10.0" or "net10.0-windows" && parent["core_framework"] == ".NETCoreApp,Version=v10.0" &&
            (parent["tfm"] == "net10.0" ? parent["core_platform"] == "portable" : parent["core_platform"].StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(parent["process_id"], out int parentPid) && parentPid > 0, "toolchain/platform");
        Require(new[] { "core_sha256", "harness_sha256", "harness_source_sha256", "core_source_sha256" }
            .All(k => Hex(parent[k], 64)), "source/build fingerprints");
        foreach (var result in successful)
        {
            string id = result.Identity.Id;
            string prefix = "component-results/" + HashText(id);
            Require(result.Identity.Tfm == parent["tfm"] && result.Identity.Architecture == parent["architecture"], "case target: " + id);
            Require(result.Metrics.Length > 0 && result.Metrics.All(m => m.RawArtifact == prefix + ".json"), "component reference: " + id);
            var raw = ThresholdValidator.Read<JsonElement>(Path.Combine(root, prefix + ".json"));
            Require(raw.GetProperty("run_id").GetString() == manifest.RunId && raw.GetProperty("case_id").GetString() == id, "component identity: " + id);
            Require(raw.GetProperty("success").GetBoolean() && raw.GetProperty("generated").GetBoolean() &&
                raw.GetProperty("built").GetBoolean() && string.IsNullOrEmpty(raw.GetProperty("build_error").GetString()) &&
                raw.GetProperty("errors").GetArrayLength() == 0, "component outcome: " + id);
            var launches = raw.GetProperty("launches");
            var children = raw.GetProperty("children");
            Require(result.LaunchCount > 0 && launches.GetArrayLength() == result.LaunchCount &&
                children.GetArrayLength() == result.LaunchCount, "launch count: " + id);
            for (int i = 0; i < result.LaunchCount; i++)
            {
                var launch = launches[i];
                Require(launch.GetProperty("found_executable").GetBoolean() && launch.GetProperty("exit_code").GetInt32() == 0 &&
                    launch.GetProperty("errors").GetArrayLength() == 0 &&
                    launch.GetProperty("results").EnumerateArray().Any(r => r.GetString()!.StartsWith("WorkloadResult", StringComparison.Ordinal)), "launch outcome: " + id);
                var child = children[i].Deserialize<Dictionary<string, string>>(ThresholdValidator.Json)!;
                var lines = launch.GetProperty("standard_output").EnumerateArray().Select(s => s.GetString()!)
                    .Where(s => s.StartsWith(ProvenancePrefix, StringComparison.Ordinal)).ToArray();
                Require(lines.Length == 1, "child provenance count: " + id);
                using var proof = JsonDocument.Parse(lines[0][ProvenancePrefix.Length..]);
                Require(proof.RootElement.EnumerateObject().Count() == child.Count && proof.RootElement.EnumerateObject()
                    .All(p => child.TryGetValue(p.Name, out string? value) && value == p.Value.GetString()), "child provenance copies: " + id);
                Require(child["run_id"] == manifest.RunId && new[] { "runtime", "tfm", "architecture", "core_framework", "core_platform", "bdn" }
                    .All(k => child[k] == parent[k]) && launch.GetProperty("process_id").GetInt32() > 0 &&
                    child["process_id"] == launch.GetProperty("process_id").GetInt32().ToString(CultureInfo.InvariantCulture) &&
                    !string.IsNullOrWhiteSpace(child["core_path"]) && Hex(child["core_sha256"], 64) && Hex(child["harness_sha256"], 64), "child identity: " + id);
            }
            if (result.Identity.CacheTopology is "production-node-shared-analyzers" or "legacy-shared")
            {
                var job = raw.GetProperty("storage_job");
                int actualCount = result.Identity.Scenario.StartsWith("legacy-") ? 8 : 3;
                Require(result.LaunchCount == 1 && job.GetProperty("strategy").GetString() == "Monitoring" &&
                    job.GetProperty("launch_count").GetInt32() == 1 && job.GetProperty("warmup_count").GetInt32() == 1 &&
                    job.GetProperty("iteration_count").GetInt32() == actualCount && job.GetProperty("invocation_count").GetInt64() == 1 &&
                    job.GetProperty("unroll_factor").GetInt32() == 1 && job.GetProperty("operations_per_invoke").GetInt64() == 1,
                    "storage job shape");
                var launch = launches[0];
                var measurements = launch.GetProperty("results").EnumerateArray().Select(s => Measurement.Parse(s.GetString()!, 1))
                    .Where(m => m.IterationMode != IterationMode.Unknown).ToArray();
                var records = CorrelateStorage(result.Identity, manifest.RunId, launch.GetProperty("process_id").GetInt32(),
                    launch.GetProperty("standard_output").EnumerateArray().Select(s => s.GetString()!).ToArray(), measurements,
                    job.GetProperty("gc_operations").GetInt64());
                var mapping = raw.GetProperty("storage_mapping");
                Require(mapping.GetArrayLength() == records.Length && mapping.EnumerateArray().Select((m, i) =>
                    m.GetProperty("invocation").GetInt32() == i + 1 && m.GetProperty("stage").GetString() ==
                    (i == 0 ? "warmup" : i == records.Length - 1 ? "diagnostic" : "actual")).All(x => x), "storage stage mapping");
                var derived = ApplyStorage(result with { Metrics = result.Metrics.Where(m => m.Metric is not ("stage_duration" or "managed_residual")).ToArray() }, records);
                Require(derived.CorrectnessStatus == result.CorrectnessStatus && result.Metrics.All(m => m.CorrectnessStatus == derived.CorrectnessStatus) &&
                    result.ContractChecks.Count == derived.ContractChecks.Count && derived.ContractChecks.All(p => result.ContractChecks.GetValueOrDefault(p.Key) == p.Value), "storage correctness/contract");
                foreach (var metric in derived.Metrics.Where(m => m.Metric is "stage_duration" or "managed_residual"))
                {
                    var recorded = result.Metrics.SingleOrDefault(m => m.Metric == metric.Metric);
                    Require(recorded != null && recorded.Value == metric.Value && recorded.SampleCount == metric.SampleCount &&
                        recorded.MeasurementStatus == metric.MeasurementStatus, "storage metric: " + metric.Metric);
                }
            }
            if (EngineObservation.TypeFor(result.Identity.Method) == result.Identity.Type)
            {
                var job = raw.GetProperty("engine_job");
                Require(result.LaunchCount == 1 && job.GetProperty("strategy").GetString() == "Monitoring" &&
                    job.GetProperty("launch_count").GetInt32() == 1 && job.GetProperty("warmup_count").GetInt32() == 1 &&
                    job.GetProperty("iteration_count").GetInt32() == 3 && job.GetProperty("invocation_count").GetInt64() == 1 &&
                    job.GetProperty("unroll_factor").GetInt32() == 1 && job.GetProperty("operations_per_invoke").GetInt64() == 1, "engine job shape");
                var launch = launches[0];
                var records = EngineObservation.Correlate(result.Identity, manifest.RunId, launch.GetProperty("process_id").GetInt32(),
                    launch.GetProperty("standard_output").EnumerateArray().Select(s => s.GetString()!).ToArray(),
                    launch.GetProperty("results").EnumerateArray().Select(s => Measurement.Parse(s.GetString()!, 1))
                        .Where(m => m.IterationMode != IterationMode.Unknown).ToArray(), job.GetProperty("gc_operations").GetInt64());
                var derived = EngineObservation.Apply(result, records);
                Require(derived.CorrectnessStatus == result.CorrectnessStatus && result.Metrics.All(m => m.CorrectnessStatus == derived.CorrectnessStatus) &&
                    result.ContractChecks.Count == derived.ContractChecks.Count && derived.ContractChecks.All(p => result.ContractChecks.GetValueOrDefault(p.Key) == p.Value),
                    "engine correctness/contract");
            }
            var build = ThresholdValidator.Read<JsonElement>(Path.Combine(root, prefix + "-build.json"));
            var project = System.Xml.Linq.XDocument.Load(Path.Combine(root, prefix + ".csproj"));
            Require(build.GetProperty("sdk").GetString() == parent["sdk"] && build.GetProperty("source_project").GetString() == parent["source_project"] &&
                !string.IsNullOrWhiteSpace(build.GetProperty("project_file_path").GetString()) &&
                project.Descendants().Any(e => e.Name.LocalName == "TargetFramework" && e.Value == result.Identity.Tfm) &&
                project.Descendants().Any(e => e.Name.LocalName == "ProjectReference" && e.Attribute("Include")?.Value == parent["source_project"]), "generated build: " + id);
        }
    }
}
