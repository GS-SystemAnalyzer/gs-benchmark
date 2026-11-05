using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using GSSystemAnalyzer.Engine;
using GSSystemAnalyzer.Hubs;
using GSSystemAnalyzer.Interfaces;
using GSSystemAnalyzer.Models;
using GSSystemAnalyzer.Models.SettingDtos;
using GSSystemAnalyzer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GSAnalyzer.Benchmarks.Core;

public sealed record StorageFile(string Path, long Bytes, int AgeDays);
public sealed record StorageManifest(string Id, string[] Directories, StorageFile[] Files)
{
    public static StorageManifest Tiny(bool unicode = false) => new(unicode ? "tiny-unicode-v1" : "tiny-v1",
        ["", "A", "A/deep", "B"], [new("zero.txt", 0, 2), new(unicode ? "文档-🤡.txt" : "root.txt", 3, 2),
            new("A/a.txt", 5, 15), new("A/a.bin", 11, 15), new("A/deep/deep.bin", 17, 120),
            new("B/plain", 23, 730), new("B/old.log", 31, 730)]);

    public static StorageManifest Legacy(bool footprint)
    {
        string[] extensions = footprint ? [".txt", ".log", ".mp4", ".mov", ".jpg", ".png", ".pdf", ".docx",
            ".xlsx", ".zip", ".rar", ".cs", ".js", ".ts", ".json", ".xml", ".dll", ".exe", ".iso", ".bin"] : [".txt"];
        var dirs = new List<string> { "" };
        var files = new List<StorageFile>();
        for (int r = 0; r < 5; r++)
        {
            dirs.Add($"Root_{r}");
            for (int s = 0; s < (footprint ? 200 : 100); s++)
            {
                string dir = $"Root_{r}/Sub_{s}";
                dirs.Add(dir);
                foreach (string ext in extensions)
                    for (int f = 0; f < (footprint ? 2 : 100); f++)
                        files.Add(new($"{dir}/" + (footprint ? $"verbose_report_document_{r}_{s}_{f}{ext}" : $"file_{f}.txt"), 0, 2));
            }
        }
        return new(footprint ? "legacy-footprint-40k-v1" : "scanner-50k-v1", dirs.ToArray(), files.ToArray());
    }
}

public sealed class StorageSession : IDisposable
{
    private readonly StorageFixture fixture;
    private readonly MemoryCache analyzerCache = new(new MemoryCacheOptions());
    private readonly ConcurrentQueue<JsonElement> progress = new();
    private readonly List<StorageNode> chunks = [];
    private readonly ISettingService settings;
    private readonly IHubContext<SystemHub> hub;
    private readonly IScanDiffService diff;
    public DiskScannerEngine Engine { get; private set; } = null!;
    public ScanCacheService Cache { get; }
    public FileTypeScanner Types { get; private set; } = null!;
    public AgeHeatmapEngine Heatmap { get; private set; } = null!;
    public DiskOperationsService Operations { get; private set; } = null!;
    public DirectoryStreamSummary? Summary { get; private set; }
    public JsonElement[] Progress => progress.ToArray();
    public StorageNode[] Chunks => chunks.ToArray();
    public CacheStatsDto? Before { get; private set; }
    public CacheStatsDto? After { get; private set; }
    public double ElapsedMs { get; private set; }

    public StorageSession(StorageFixture fixture, bool prepareRestore = false)
    {
        this.fixture = fixture;
        fixture.ValidateOwnership();
        StorageFixture.RequireNoLinks(fixture.CachePath);
        var settings = new Mock<ISettingService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettingDto { Scan = new() { Depth = 64, ExcludedPaths = [],
            SkipHiddenFiles = false, SkipSystemFiles = false, FollowSymlinks = false } });
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
            {
                if (method == "ScanProgress" && args.Length == 1) progress.Enqueue(JsonSerializer.SerializeToElement(args[0]));
            }).Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.SetupGet(c => c.All).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SystemHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        this.settings = settings.Object;
        this.hub = hub.Object;
        diff = Mock.Of<IScanDiffService>();
        Cache = new ScanCacheService(settings.Object, NullLogger<ScanCacheService>.Instance);
        try
        {
            if (!prepareRestore) { RestoreEngine(); AttachConsumers(); }
        }
        catch { Engine?.Dispose(); Cache.Dispose(); analyzerCache.Dispose(); throw; }
    }

    public void RestoreEngine() => Engine = new DiskScannerEngine(hub, settings,
        NullLogger<DiskScannerEngine>.Instance, Cache, cacheFilePath: fixture.CachePath);

    public void AttachConsumers()
    {
        Types = new FileTypeScanner(Engine, analyzerCache, Cache);
        Heatmap = new AgeHeatmapEngine(Engine, analyzerCache, Cache);
        Operations = new DiskOperationsService(Engine, hub, NullLogger<DiskOperationsService>.Instance, diff, Cache, Types);
    }

    public int AnalyzerSnapshots => analyzerCache.Keys.Count(k => analyzerCache.TryGetValue(k, out _));
    public StorageAnalyzerOutput Analyze() => new(Types.Analyze(fixture.Workload),
        Types.GetExtensionBreakdown(fixture.Workload), Heatmap.Analyze(fixture.Workload));

    public Task<DirectoryStreamSummary> Scan() => fixture.Run(async () =>
    {
        progress.Clear();
        chunks.Clear();
        Before = Cache.GetStats();
        long start = Stopwatch.GetTimestamp();
        try
        {
            Summary = await Operations.StreamDirectoryAsync(fixture.Workload, Operations.BeginScan(), nodes =>
            {
                chunks.AddRange(nodes);
                return Task.CompletedTask;
            });
            return Summary;
        }
        finally
        {
            ElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            After = Cache.GetStats();
            // Stop the service-armed watcher before declared mutations or fixture deletion.
            Engine.MoveRadarToSector(Path.Combine(fixture.State, "uncreated-watch-target"));
        }
    });

    public void Dispose()
    {
        Engine?.Dispose();
        Cache.Dispose();
        analyzerCache.Dispose();
    }
}

public sealed record StorageDifference(string Key, string Expected, string? Actual);
public sealed record StorageAnalyzerOutput(FileTypeScanResult? Types, ExtensionBreakdownResult? Breakdown, AgeHeatmapResult? Heatmap);

public sealed record StorageInvocation
{
    public required string RunId { get; init; }
    public required string LaunchId { get; init; }
    public required int ProcessId { get; init; }
    public required string Method { get; init; }
    public required int Invocation { get; init; }
    public required string Root { get; init; }
    public required string CachePath { get; init; }
    public required StorageManifest Manifest { get; init; }
    public required string Scenario { get; init; }
    public required Dictionary<string, string[]> Expected { get; init; }
    public required Dictionary<string, string> Actual { get; init; }
    public required StorageDifference[] Differences { get; init; }
    public required Dictionary<string, string> Before { get; init; }
    public required Dictionary<string, string> After { get; init; }
    public required Dictionary<string, double> StagesMs { get; init; }
    public long? BaselineBytes { get; init; }
    public long? AfterBytes { get; init; }
    public required bool Cleaned { get; init; }
}

public sealed class StorageWorkload
{
    public const string Prefix = "// GS_STORAGE ";
    public const string CleanupPrefix = "// GS_STORAGE_CLEANED ";
    public const string OsCache = "os_cache_uncontrolled";
    private readonly string launch = Guid.NewGuid().ToString("N");
    private int ordinal;
    private string method = "", scenario = "";
    private StorageFixture fixture = null!;
    private StorageSession? session;
    private Dictionary<string, string[]> expected = [];
    private Dictionary<string, string> actual = [], before = [];
    private Dictionary<string, string> seed = [], preconditions = [];
    private StorageAnalyzerOutput measured = new(null, null, null);
    private StorageAnalyzerOutput? primed;
    private int operations;
    private readonly Dictionary<string, double> stages = [];
    private long? baseline, after;

    public StorageFixture Begin(string name, string state, string dataset)
    {
        method = name;
        scenario = state;
        ordinal++;
        stages.Clear();
        baseline = after = null;
        actual = [];
        seed = [];
        preconditions = [];
        measured = new(null, null, null);
        primed = null;
        operations = 0;
        fixture = new StorageFixture(dataset switch
        {
            "tiny-v1" => StorageManifest.Tiny(),
            "tiny-unicode-v1" => StorageManifest.Tiny(true),
            _ => throw new InvalidDataException("Undeclared storage dataset: " + dataset)
        });
        try
        {
            session = fixture.Own(new StorageSession(fixture));
            if (state != "app_empty")
            {
                session.Scan().GetAwaiter().GetResult();
                seed = StorageOracle.Observe(fixture, session, "app_empty");
                AddPersistence(seed);
                preconditions["precondition/primed_snapshots"] = session.AnalyzerSnapshots.ToString();
                if (state == "analyzer_hit") primed = session.Analyze();
                if (state == "changed_rescan")
                {
                    fixture.ChangeFile("A/a.txt", 13);
                    session.Engine.InvalidatePaths([fixture.Resolve("A")]);
                }
                else if (state == "persisted_restore")
                {
                    fixture.Release(session);
                    session = fixture.Own(new StorageSession(fixture, prepareRestore: true));
                }
            }
            expected = Expected(fixture.Manifest, state);
            before = Snapshot();
            foreach (string dir in fixture.Manifest.Directories)
                preconditions["precondition/directory/" + dir] = (session.Engine?.DirectorySizeCache.ContainsKey(fixture.Resolve(dir)) ?? false).ToString();
            preconditions["precondition/persisted"] = File.Exists(fixture.CachePath).ToString();
            if (state is "forced_gc_retention" or "passive_after_product_gc") baseline = GC.GetTotalMemory(true);
            return fixture;
        }
        catch (Exception error)
        {
            try { Cleanup(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public async Task Run()
    {
        long start = Stopwatch.GetTimestamp();
        switch (scenario)
        {
            case "app_empty":
            case "app_hit":
            case "changed_rescan":
                await session!.Scan();
                break;
            case "persisted_restore":
                session!.RestoreEngine();
                break;
            case "analyzer_hit":
                measured = session!.Analyze();
                break;
            case "clear_action":
                session!.Engine.ClearCache();
                break;
            case "passive_after_product_gc":
            case "forced_gc_retention":
                long clearStart = Stopwatch.GetTimestamp();
                session!.Engine.ClearCache();
                stages["product_clear"] = Stopwatch.GetElapsedTime(clearStart).TotalMilliseconds;
                start = Stopwatch.GetTimestamp();
                if (scenario == "passive_after_product_gc") await Task.Delay(100);
                after = GC.GetTotalMemory(scenario == "forced_gc_retention");
                break;
            default: throw new InvalidDataException("Unknown storage state.");
        }
        stages[scenario] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        operations++;
    }

    public StorageInvocation Finish(bool emit = true)
    {
        if (scenario == "persisted_restore" && session?.Engine != null) session.AttachConsumers();
        Dictionary<string, string> post = Snapshot();
        if (scenario is "app_empty" or "app_hit" or "changed_rescan" or "persisted_restore" or "analyzer_hit")
        {
            actual = StorageOracle.Observe(fixture, session!, OracleScenario(scenario), scenario == "analyzer_hit" ? measured : null);
            AddPersistence(actual);
        }
        if (scenario is "clear_action" or "passive_after_product_gc" or "forced_gc_retention")
        {
            actual["clear/directory_entries"] = session!.Engine.DirectorySizeCache.Count.ToString();
            actual["clear/node_entries"] = session.Cache.GetNodesUnderRoot(fixture.Workload).Count().ToString();
            actual["clear/persisted"] = File.Exists(fixture.CachePath).ToString();
            actual["clear/analyzer_snapshots"] = session.AnalyzerSnapshots.ToString();
        }
        if (scenario == "analyzer_hit")
        {
            actual["analyzer_hit/types"] = (measured.Types != null && ReferenceEquals(primed?.Types, measured.Types)).ToString();
            actual["analyzer_hit/breakdown"] = (measured.Breakdown != null && ReferenceEquals(primed?.Breakdown, measured.Breakdown)).ToString();
            actual["analyzer_hit/heatmap"] = (measured.Heatmap != null && ReferenceEquals(primed?.Heatmap, measured.Heatmap)).ToString();
        }
        foreach (var p in seed) actual["seed/" + p.Key] = p.Value;
        foreach (var p in preconditions) actual[p.Key] = p.Value;
        actual["workload/operations"] = operations.ToString();
        var differences = StorageOracle.Compare(expected, actual);
        fixture.VerifyInventory();
        fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var record = new StorageInvocation { RunId = Environment.GetEnvironmentVariable("GS_BENCH_RUN_ID") ?? "standalone",
            LaunchId = launch, ProcessId = Environment.ProcessId, Method = method, Invocation = ordinal,
            Root = fixture.Root, CachePath = fixture.CachePath, Manifest = fixture.Manifest, Scenario = scenario,
            Expected = expected, Actual = actual, Differences = differences, Before = before, After = post,
            StagesMs = new(stages), BaselineBytes = baseline, AfterBytes = after, Cleaned = !Directory.Exists(fixture.Root) };
        session = null;
        primed = null;
        measured = new(null, null, null);
        if (emit) Emit(record);
        return record;
    }

    public static Dictionary<string, string[]> Expected(StorageManifest manifest, string state)
    {
        bool clear = state is "clear_action" or "passive_after_product_gc" or "forced_gc_retention";
        var expected = clear ? new Dictionary<string, string[]>() : OutputExpected(manifest, OracleScenario(state));
        if (state != "app_empty")
        {
            var seedManifest = state == "changed_rescan" ? manifest with
                { Files = manifest.Files.Select(f => f.Path == "A/a.txt" ? f with { Bytes = 5 } : f).ToArray() } : manifest;
            foreach (var p in OutputExpected(seedManifest, "app_empty")) expected["seed/" + p.Key] = p.Value;
        }
        foreach (string dir in manifest.Directories)
            expected["precondition/directory/" + dir] = [(state is "app_empty" or "persisted_restore" ? false :
                state == "changed_rescan" ? dir == "B" : true).ToString()];
        expected["precondition/persisted"] = [(state != "app_empty").ToString()];
        if (state != "app_empty") expected["precondition/primed_snapshots"] = ["3"];
        expected["workload/operations"] = ["1"];
        if (state is "clear_action" or "passive_after_product_gc" or "forced_gc_retention")
        {
            expected["clear/directory_entries"] = ["0"];
            expected["clear/node_entries"] = ["0"];
            expected["clear/persisted"] = ["False"];
            expected["clear/analyzer_snapshots"] = ["0"];
        }
        if (state == "analyzer_hit") foreach (string kind in new[] { "types", "breakdown", "heatmap" }) expected["analyzer_hit/" + kind] = ["True"];
        return expected;
    }

    private static Dictionary<string, string[]> OutputExpected(StorageManifest manifest, string state)
    {
        var expected = StorageOracle.Expected(manifest, state);
        foreach (string dir in manifest.Directories)
            expected["persisted/" + dir] = [manifest.Files.Where(f => dir == "" || f.Path.StartsWith(dir + "/", StringComparison.Ordinal)).Sum(f => f.Bytes).ToString()];
        return expected;
    }

    public static Dictionary<string, string[]> LegacyExpected() => new()
    {
        ["populated_directories"] = ["1005"], ["populated_files"] = ["40000"]
    };

    public static void Emit(StorageInvocation record) => Console.WriteLine(Prefix + JsonSerializer.Serialize(record,
        new JsonSerializerOptions(ThresholdValidator.Json) { WriteIndented = false }));

    public void Cleanup() => fixture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    private static string OracleScenario(string state) => state is "analyzer_hit" or "clear_action" or "passive_after_product_gc" or "forced_gc_retention" ? "app_empty" : state;

    private Dictionary<string, string> Snapshot()
    {
        var stats = session?.Cache.GetStats();
        return new() { ["directory_entries"] = (session?.Engine?.DirectorySizeCache.Count ?? 0).ToString(),
            ["cache_stats"] = JsonSerializer.Serialize(stats, ThresholdValidator.Json), ["os_cache"] = OsCache,
            ["persisted"] = File.Exists(fixture.CachePath).ToString(), ["session"] = session?.Engine == null ? "prepared" : "owned",
            ["scan_before"] = JsonSerializer.Serialize(session?.Before, ThresholdValidator.Json),
            ["scan_after"] = JsonSerializer.Serialize(session?.After, ThresholdValidator.Json),
            ["analyzer_snapshots"] = (session?.AnalyzerSnapshots ?? 0).ToString() };
    }

    private void AddPersistence(Dictionary<string, string> target)
    {
        fixture.ValidateOwnership();
        StorageFixture.RequireNoLinks(fixture.CachePath);
        if (!File.Exists(fixture.CachePath)) return;
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.CachePath));
        foreach (var entry in saved.RootElement.EnumerateObject())
        {
            string relative = Path.GetRelativePath(fixture.Workload, entry.Name).Replace('\\', '/');
            target["persisted/" + (relative == "." ? "" : relative)] = entry.Value.GetProperty("Size").GetInt64().ToString();
        }
    }
}

public static class StorageOracle
{
    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant() is "" or "no extension" ? "(none)" : Path.GetExtension(path).ToLowerInvariant();
    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static bool Under(string path, string directory) => directory == "" || path.StartsWith(directory + "/", StringComparison.Ordinal);
    private static string Age(int days) => days < 7 ? "fresh" : days < 30 ? "recent" : days < 365 ? "aging" : "stale";
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static Dictionary<string, string[]> Expected(StorageManifest manifest, string scenario)
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string key, object value) => expected.Add(key, [Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!]);
        var buckets = new Dictionary<string, (long Bytes, int Count)>(StringComparer.Ordinal);
        foreach (string bucket in new[] { "fresh", "recent", "aging", "stale" }) buckets.Add(bucket, (0, 0));
        foreach (string dir in manifest.Directories)
        {
            var direct = manifest.Files.Where(f => Parent(f.Path) == dir).ToArray();
            long own = direct.Sum(f => f.Bytes), recursive = manifest.Files.Where(f => Under(f.Path, dir)).Sum(f => f.Bytes);
            Add("size/" + dir, recursive);
            Add("node/" + dir + "/own_bytes", own);
            Add("node/" + dir + "/recursive_bytes", recursive);
            Add("node/" + dir + "/files", direct.Length);
            string age = Age(direct.Length == 0 ? 0 : direct.Min(f => f.AgeDays));
            Add("heatmap/node/" + dir + "/bytes", own);
            Add("heatmap/node/" + dir + "/age", age);
            var bucket = buckets[age];
            buckets[age] = (bucket.Bytes + own, bucket.Count + direct.Length);
            foreach (var file in direct) Add("file/" + file.Path, file.Bytes);
        }
        foreach (var (key, value) in buckets)
        {
            Add("heatmap/bucket/" + key + "/bytes", value.Bytes);
            Add("heatmap/bucket/" + key + "/count", value.Count);
        }
        foreach (var group in manifest.Files.GroupBy(f => Ext(f.Path)))
        {
            Add("extension/" + group.Key + "/bytes", group.Sum(f => f.Bytes));
            Add("extension/" + group.Key + "/count", group.Count());
            Add("types/" + group.Key + "/bytes", group.Sum(f => f.Bytes));
            Add("types/" + group.Key + "/count", group.Count());
            long largest = group.Max(f => f.Bytes);
            Add("extension/" + group.Key + "/largest_bytes", largest);
            expected.Add("extension/" + group.Key + "/largest_path", group.Where(f => f.Bytes == largest).Select(f => f.Path).ToArray());
        }
        Add("types/total_bytes", manifest.Files.Sum(f => f.Bytes));
        if (scenario != "persisted_restore")
        {
            Add("stream/own_bytes", manifest.Files.Where(f => Parent(f.Path) == "").Sum(f => f.Bytes));
            Add("stream/recursive_bytes", manifest.Files.Sum(f => f.Bytes));
            Add("stream/final", true);
            foreach (var dir in manifest.Directories.Where(d => d != "" && Parent(d) == ""))
                Add("stream/path/" + dir, "Directory:" + Number(manifest.Files.Where(f => Under(f.Path, dir)).Sum(f => f.Bytes)));
            foreach (var file in manifest.Files.Where(f => Parent(f.Path) == "")) Add("stream/path/" + file.Path, "File:" + Number(file.Bytes));
            Add("progress/terminal", "COMPLETED");
            Add("progress/visited", scenario == "app_hit" ? "unavailable" : Number(manifest.Files.Count(f =>
                scenario == "changed_rescan" ? Under(f.Path, "A") : Parent(f.Path) != "")));
            string[] subtrees = scenario == "app_hit" ? [] : scenario == "changed_rescan" ? ["A"] :
                manifest.Directories.Where(d => d != "" && Parent(d) == "").Order().ToArray();
            Add("progress/subtrees", string.Join(",", subtrees));
        }
        return expected;
    }

    public static StorageDifference[] Compare(StorageManifest manifest, string scenario, IReadOnlyDictionary<string, string> actual)
        => Compare(Expected(manifest, scenario), actual);

    public static StorageDifference[] Compare(IReadOnlyDictionary<string, string[]> expected, IReadOnlyDictionary<string, string> actual)
    {
        var differences = expected.Where(p => !actual.TryGetValue(p.Key, out string? value) || !p.Value.Contains(value, StringComparer.Ordinal))
            .Select(p => new StorageDifference(p.Key, string.Join(" OR ", p.Value), actual.GetValueOrDefault(p.Key))).ToList();
        differences.AddRange(actual.Keys.Except(expected.Keys).Select(k => new StorageDifference(k, "absent", actual[k])));
        return differences.OrderBy(d => d.Key, StringComparer.Ordinal).ToArray();
    }

    public static Dictionary<string, string> Observe(StorageFixture fixture, StorageSession session, string scenario, StorageAnalyzerOutput? analyzers = null)
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, object value)
        {
            if (!actual.TryAdd(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!))
                actual[key] = "duplicate";
        }
        string Relative(string path)
        {
            string normalized = path.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetRelativePath(fixture.Workload, normalized).Replace('\\', '/') is "." ? "" :
                Path.GetRelativePath(fixture.Workload, normalized).Replace('\\', '/');
        }
        foreach (var (path, entry) in session.Engine.DirectorySizeCache) Add("size/" + Relative(path), entry.Size);
        foreach (var node in session.Cache.GetNodesUnderRoot(fixture.Workload))
        {
            string dir = Relative(node.Path);
            Add("node/" + dir + "/own_bytes", node.OwnBytes);
            Add("node/" + dir + "/recursive_bytes", node.RecursiveBytes);
            Add("node/" + dir + "/files", node.Files.Count);
            foreach (var file in node.Files) Add("file/" + (dir == "" ? "" : dir + "/") + file.Name, file.Length);
        }
        analyzers ??= session.Analyze();
        var types = analyzers.Types;
        if (types != null)
        {
            Add("types/total_bytes", types.TotalScannedBytes);
            foreach (var ext in types.Categories.SelectMany(c => c.Extensions))
            {
                string name = ext.Ext is "" or "no extension" ? "(none)" : ext.Ext;
                Add("types/" + name + "/bytes", ext.TotalBytes);
                Add("types/" + name + "/count", ext.FileCount);
            }
        }
        var breakdown = analyzers.Breakdown;
        if (breakdown != null) foreach (var ext in breakdown.Extensions)
        {
            Add("extension/" + ext.Ext + "/bytes", ext.TotalBytes);
            Add("extension/" + ext.Ext + "/count", ext.FileCount);
            Add("extension/" + ext.Ext + "/largest_bytes", ext.LargestFileBytes);
            Add("extension/" + ext.Ext + "/largest_path", string.IsNullOrEmpty(ext.LargestFilePath) ? "unavailable" : Relative(ext.LargestFilePath));
        }
        var heatmap = analyzers.Heatmap;
        if (heatmap != null)
        {
            foreach (var node in heatmap.Nodes)
            {
                Add("heatmap/node/" + Relative(node.Path) + "/bytes", node.SizeBytes);
                Add("heatmap/node/" + Relative(node.Path) + "/age", node.AgeBucket);
            }
            foreach (var (bucket, value) in heatmap.Summary)
            {
                Add("heatmap/bucket/" + bucket + "/bytes", value.TotalBytes);
                Add("heatmap/bucket/" + bucket + "/count", value.Count);
            }
        }
        if (scenario != "persisted_restore")
        {
            if (session.Summary != null)
            {
                Add("stream/own_bytes", session.Summary.OwnBytes);
                Add("stream/recursive_bytes", session.Summary.RecursiveBytes);
                Add("stream/final", session.Summary.RecursiveBytesFinal);
            }
            foreach (var node in session.Chunks) Add("stream/path/" + Relative(node.Path), node.Type + ":" + Number(node.SizeBytes));
            var terminal = session.Progress.Where(p => p.TryGetProperty("status", out var status) && status.GetString() is "COMPLETED" or "CANCELED").ToArray();
            if (terminal.Length == 1)
            {
                Add("progress/terminal", terminal[0].GetProperty("status").GetString()!);
                Add("progress/visited", terminal[0].TryGetProperty("count", out var count) ? Number(count.GetInt64()) : "unavailable");
            }
            Add("progress/subtrees", string.Join(",", session.Progress.Where(p => p.GetProperty("status").GetString() == "SCANNING" &&
                p.TryGetProperty("completed", out _)).Select(p => p.GetProperty("currentTarget").GetString()).Order()));
        }
        return actual;
    }
}

public sealed class StorageFixture : IAsyncDisposable
{
    private readonly string parent;
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    private readonly List<Task> operations = [];
    private readonly List<IDisposable> resources = [];
    private Task? disposal;
    private bool closing;
    public string Root { get; }
    public string Workload => Path.Combine(Root, "workload");
    public string State => Path.Combine(Root, "state");
    public string Output => Path.Combine(Root, "output");
    public string Marker => Path.Combine(Root, ".owner");
    public string CachePath => Path.Combine(State, "scanner_memory.json");
    public bool Preserved { get; private set; }
    public StorageManifest Manifest { get; private set; }
    public DateTime EpochUtc { get; } = DateTime.UtcNow;

    public StorageFixture(StorageManifest manifest, string? tempParent = null)
    {
        parent = Path.GetFullPath(tempParent ?? Path.GetTempPath());
        if (Path.TrimEndingDirectorySeparator(parent) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
            !IsChild(Path.GetTempPath(), parent)) throw new IOException("Fixture parent must be inside the temporary directory.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        RequireNoLinks(parent);
        Root = Path.Combine(parent, "gs-storage-" + Guid.NewGuid().ToString("N"));
        Manifest = manifest;
        ValidateManifest(manifest);
        if (!IsChild(parent, Root) || Directory.Exists(Root) || File.Exists(Root))
            throw new IOException("Fixture root is not a new owned child.");
        Directory.CreateDirectory(Root);
        try
        {
            using (var marker = new StreamWriter(new FileStream(Marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                marker.Write(token);
            Journal("created");
            foreach (string dir in new[] { Workload, State, Output, Workload + "-decoy" }) Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(Workload + "-decoy", "outside.txt"), "outside");
            foreach (string dir in manifest.Directories.Where(d => d.Length > 0)) Directory.CreateDirectory(Resolve(dir));
            foreach (var file in manifest.Files) WriteFile(file);
            Journal("generated");
            VerifyInventory();
            Journal("verified");
        }
        catch (Exception setupError)
        {
            try { DeleteOwned(); }
            catch (Exception cleanupError)
            {
                Preserved = true;
                throw new IOException("Setup failed; preserved fixture: " + Root, new AggregateException(setupError, cleanupError));
            }
            throw;
        }
    }

    private void Journal(string phase) => Console.WriteLine("// GS_STORAGE_OWNER " + JsonSerializer.Serialize(new
    {
        run_id = Environment.GetEnvironmentVariable("GS_BENCH_RUN_ID") ?? "standalone", process_id = Environment.ProcessId,
        root = Root, dataset = Manifest.Id, phase, utc = DateTimeOffset.UtcNow
    }));

    public static bool IsChild(string parent, string candidate)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static void RequireNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point in fixture path: " + current);
    }

    private static void ValidateManifest(StorageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Directories.Length == 0 || manifest.Directories[0] != "" ||
            manifest.Directories.Length > 2_000 || manifest.Files.Length > 50_000 || manifest.Files.Any(f => f.Bytes < 0 || f.AgeDays < 0) ||
            manifest.Files.Sum(f => f.Bytes) > 16_000_000) throw new InvalidDataException("Fixture exceeds declared metadata caps.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in manifest.Directories.Skip(1).Concat(manifest.Files.Select(f => f.Path)))
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
                path.Split('/').Any(s => s is "." or ".." or "") || !paths.Add(path)) throw new InvalidDataException("Invalid manifest path: " + path);
            string parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            if (!manifest.Directories.Contains(parent)) throw new InvalidDataException("Missing manifest parent: " + path);
        }
    }

    public string Resolve(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(Workload, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (relative != "" && !IsChild(Workload, path)) throw new IOException("Path escapes workload.");
        RequireNoLinks(path);
        return path;
    }

    private void WriteFile(StorageFile file)
    {
        ValidateOwnership();
        string path = Resolve(file.Path);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.SetLength(file.Bytes);
        File.SetLastWriteTimeUtc(path, EpochUtc.AddDays(-file.AgeDays));
    }

    public void ChangeFile(string relative, long bytes)
    {
        ValidateOwnership();
        var changed = Manifest with { Files = Manifest.Files.Select(f => f.Path == relative ? f with { Bytes = bytes } : f).ToArray() };
        ValidateManifest(changed);
        if (!Manifest.Files.Any(f => f.Path == relative)) throw new InvalidDataException("Unknown mutation target.");
        using (var stream = new FileStream(Resolve(relative), FileMode.Open, FileAccess.Write, FileShare.None)) stream.SetLength(bytes);
        File.SetLastWriteTimeUtc(Resolve(relative), EpochUtc.AddDays(-changed.Files.Single(f => f.Path == relative).AgeDays));
        Manifest = changed;
        VerifyInventory();
    }

    public void ValidateOwnership()
    {
        if (!IsChild(parent, Root)) throw new IOException("Fixture containment changed.");
        RequireNoLinks(Marker);
        if (!File.Exists(Marker) || File.ReadAllText(Marker) != token) throw new IOException("Fixture ownership marker mismatch.");
    }

    public void VerifyInventory()
    {
        ValidateOwnership();
        var entries = Walk(Workload).ToArray();
        string Relative(string p) => Path.GetRelativePath(Workload, p).Replace('\\', '/');
        if (!entries.Where(Directory.Exists).Select(Relative).Order().SequenceEqual(Manifest.Directories.Skip(1).Order()) ||
            !entries.Where(File.Exists).Select(p => Relative(p) + ":" + new FileInfo(p).Length).Order()
                .SequenceEqual(Manifest.Files.Select(f => f.Path + ":" + f.Bytes).Order())) throw new InvalidDataException("Generated inventory differs from manifest.");
        foreach (var file in Manifest.Files)
            if (Math.Abs((File.GetLastWriteTimeUtc(Resolve(file.Path)) - EpochUtc.AddDays(-file.AgeDays)).TotalSeconds) > 2)
                throw new InvalidDataException("Generated timestamp differs from manifest.");
    }

    private static IEnumerable<string> Walk(string directory)
    {
        RequireNoLinks(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            RequireNoLinks(path);
            yield return path;
            if (Directory.Exists(path)) foreach (string child in Walk(path)) yield return child;
        }
    }

    public T Own<T>(T resource) where T : IDisposable
    {
        lock (gate)
        {
            if (closing) { resource.Dispose(); throw new ObjectDisposedException(nameof(StorageFixture)); }
            resources.Add(resource);
            return resource;
        }
    }

    public Task<T> Run<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closing) throw new ObjectDisposedException(nameof(StorageFixture));
            operations.Add(completion.Task);
        }
        _ = Invoke();
        return completion.Task;

        async Task Invoke()
        {
            try { completion.SetResult(await action()); }
            catch (Exception error) { completion.SetException(error); }
        }
    }

    public void Release(IDisposable resource)
    {
        lock (gate)
        {
            if (closing || operations.Any(t => !t.IsCompleted)) throw new InvalidOperationException("Owners require settled operations.");
            if (!resources.Contains(resource)) throw new InvalidOperationException("Resource is not owned by this fixture.");
            resource.Dispose();
            resources.Remove(resource);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal != null) return new(disposal);
            closing = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            _ = Complete();
            return new(disposal);

            async Task Complete()
            {
                try { await DrainAndDelete(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
            }
        }
    }

    private async Task DrainAndDelete()
    {
        try { await Task.WhenAll(operations); }
        catch { Preserved = true; }
        foreach (var resource in resources.AsEnumerable().Reverse())
            try { resource.Dispose(); } catch { Preserved = true; }
        if (Preserved) throw new IOException("Preserved unsettled/failed fixture: " + Root);
        try { DeleteOwned(); }
        catch { Preserved = true; throw; }
    }

    private void DeleteOwned()
    {
        ValidateOwnership();
        var entries = Walk(Root).ToArray();
        foreach (string path in entries.Where(File.Exists).Where(p => !string.Equals(p, Marker,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            ValidateOwnership();
            RequireNoLinks(path);
            File.Delete(path);
        }
        foreach (string path in entries.Where(Directory.Exists).OrderByDescending(p => p.Length))
        {
            ValidateOwnership();
            RequireNoLinks(path);
            Directory.Delete(path);
        }
        ValidateOwnership();
        File.Delete(Marker);
        Directory.Delete(Root);
    }
}
