using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

namespace Forklift.Benchmark
{
    public static class Program
    {


        internal static class BenchParams
        {
            public static string Suite = "minimal";
            public static int Depth = 4;
            public static int? Threads = null;
            public static bool ParallelRoot = false;
        }

        private static Job Minimal(Job baseJob) => baseJob
            .WithStrategy(RunStrategy.Throughput)
            .WithWarmupCount(4)
            .WithIterationTime(TimeInterval.FromSeconds(1))
            .WithIterationCount(15)
            .WithMaxRelativeError(0.03)
            .WithOutlierMode(Perfolizer.Mathematics.OutlierDetection.OutlierMode.RemoveUpper)
            .WithLaunchCount(1)
            .WithGcServer(true)
            .WithId("Minimal");

        private static Job Fast(Job baseJob) => baseJob
            .WithStrategy(RunStrategy.Throughput)
            .WithWarmupCount(6)
            .WithIterationTime(TimeInterval.FromSeconds(2))
            .WithMinIterationCount(25)
            .WithMaxIterationCount(50)
            .WithMaxRelativeError(0.02)
            .WithOutlierMode(Perfolizer.Mathematics.OutlierDetection.OutlierMode.RemoveUpper)
            .WithGcServer(true)
            .WithId("Fast");

        private static Job Full(Job baseJob) => baseJob
            .WithStrategy(RunStrategy.Throughput)
            .WithWarmupCount(6)
            .WithIterationTime(TimeInterval.FromSeconds(3))
            .WithMinIterationCount(25)
            .WithMaxIterationCount(50)
            .WithMaxRelativeError(0.02)
            .WithOutlierMode(Perfolizer.Mathematics.OutlierDetection.OutlierMode.RemoveUpper)
            .WithGcServer(true)
            .WithId("Full");

        public static int Main(string[] args)
        {
            var cli = CliArgs.Parse(args);
            if (string.IsNullOrWhiteSpace(cli.BaselinePath) || string.IsNullOrWhiteSpace(cli.CandidatePath))
            {
                Console.Error.WriteLine("Usage:");
                Console.Error.WriteLine("  dotnet run -c Release --project Forklift.Benchmark -- " +
                                        "--baseline <path-to-Forklift.Core.dll> --candidate <path-to-Forklift.Core.dll> " +
                                        "[--suite minimal|fast|full] [--depth 4] [--threads N] [--parallelRoot true|false]");
                return 2;
            }

            var job = cli.Suite switch
            {
                "minimal" => Minimal(Job.Default),
                "fast" => Fast(Job.Default),
                "full" => Full(Job.Default),
                _ => Minimal(Job.Default),
            };

            BenchParams.Suite = (cli.Suite ?? "minimal").ToLowerInvariant();
            BenchParams.Depth = cli.Depth ?? 4;
            BenchParams.Threads = cli.Threads;
            BenchParams.ParallelRoot = cli.ParallelRoot;

            // Create a ManualConfig and add the job and other options
            var config = ManualConfig.Create(DefaultConfig.Instance)
                .WithOptions(ConfigOptions.DisableLogFile)
                .AddExporter(JsonExporter.Full)
                .AddJob(job
                        .WithEnvironmentVariable("COMPlus_ReadyToRun", "0")
                        .WithEnvironmentVariable("COMPlus_TieredCompilation", "0")
                        .WithEnvironmentVariable("COMPlus_TieredPGO", "0")
                        .WithEnvironmentVariable("COMPlus_GCHeapCount", "1")
                        .WithEnvironmentVariable("DOTNET_TieredPGO", "0")
                        .WithEnvironmentVariable("DOTNET_ReadyToRun", "0")
                        .WithEnvironmentVariable("FORKLIFT_BASELINE_PATH", cli.BaselinePath)       // e.g., ".../baseline/Forklift.Core.dll"
                        .WithEnvironmentVariable("FORKLIFT_CANDIDATE_PATH", cli.CandidatePath)      // e.g., ".../candidate/Forklift.Core.dll"
                        .WithEnvironmentVariable("FORKLIFT_SUITE", cli.Suite ?? "minimal")             // "minimal" | "fast" | "full"
                        .WithEnvironmentVariable("FORKLIFT_DEPTH", cli.Depth?.ToString() ?? "4")
                        .WithEnvironmentVariable("FORKLIFT_THREADS", cli.Threads?.ToString() ?? "")
                        .WithEnvironmentVariable("FORKLIFT_PARALLEL_ROOT", cli.ParallelRoot ? "1" : "0")

                );

            var summary = BenchmarkRunner.Run<PerftAABench>(config);
            return 0;
        }

        // ===== Benchmark type =====
        [MemoryDiagnoser(true)]
        [Orderer(SummaryOrderPolicy.FastestToSlowest)]
        [RankColumn, MinColumn, MaxColumn, Q1Column, Q3Column, AllStatisticsColumn]
        public class PerftAABench
        {
            private static readonly (string Name, string Fen)[] MinimalSuite =
            [
                ("startpos", "startpos"),
            ];

            private static readonly (string Name, string Fen)[] FastSuite =
            [
                ("kiwipete", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"),
                ("tricky-ep","8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1"),
            ];

            private static readonly (string Name, string Fen)[] FullSuite =
            [
                ("complex-castle", "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1"),
                ("position-5", "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8"),
                ("position-6", "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10"),
                ("illegal-ep-1", "3k4/3p4/8/K1P4r/8/8/8/8 b - - 0 1"),
                ("illegal-ep-2", "8/8/4k3/8/2p5/8/B2P2K1/8 w - - 0 1"),
                ("castling-through", "r3k2r/8/3Q4/8/8/5q2/8/R3K2R b KQkq - 0 1"),
                ("short-castle-check", "5k2/8/8/8/8/8/8/4K2R w K - 0 1"),
                ("long-castle-check", "3k4/8/8/8/8/8/8/R3K3 w Q - 0 1"),
                ("promotion-out-of-check", "2K2r2/4P3/8/8/8/8/8/3k4 w - - 0 1"),
                ("underpromotion-to-check", "8/P1k5/K7/8/8/8/8/8 w - - 0 1"),
                ("self-stalemate", "K1k5/8/P7/8/8/8/8/8 w - - 0 1"),
            ];

            // Parameterize which position is under test
            [ParamsSource(nameof(PositionNames))]
            public string PositionName { get; set; } = "startpos";

            private static IEnumerable<(string Name, string Fen)> GetSuite(string name) =>
                name switch
                {
                    "minimal" => MinimalSuite,
                    "fast" => FastSuite,
                    "full" => FullSuite,
                    _ => FastSuite
                };

            private (string Name, string Fen) _position;
            private EngineFacade _baseline = null!;
            private EngineFacade _candidate = null!;

            [ParamsSource(nameof(DepthValues))]
            public int Depth { get; set; } = 4;

            [ParamsSource(nameof(ThreadValues))]
            public int? Threads { get; set; } = null; // null => engine default

            [ParamsSource(nameof(ParallelRootValues))]
            public bool ParallelRoot { get; set; } = false;

            public static IEnumerable<int> DepthValues() => [BenchParams.Depth];
            public static IEnumerable<int?> ThreadValues() => [BenchParams.Threads];
            public static IEnumerable<bool> ParallelRootValues() => [BenchParams.ParallelRoot];

            static string GetEnv(string name, bool required = true)
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (required && string.IsNullOrWhiteSpace(v))
                    throw new InvalidOperationException($"{name} not set.");
                return v ?? string.Empty;
            }
            static int GetEnvInt(string name, int @default)
                => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : @default;
            static int? GetEnvIntNullable(string name)
                => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : (int?)null;
            static bool GetEnvBool(string name, bool @default = false)
            {
                var v = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(v)) return @default;
                return v == "1" || bool.TryParse(v, out var b) && b;
            }

            public static IEnumerable<string> PositionNames =>
                GetSuite(BenchParams.Suite).Select(p => p.Name);

            [GlobalSetup]
            public void Setup()
            {
                // Load engines from the paths that we passed via env vars
                var baselinePath = GetEnv("FORKLIFT_BASELINE_PATH");
                var candidatePath = GetEnv("FORKLIFT_CANDIDATE_PATH");

                _baseline = EngineFacade.LoadFrom(baselinePath);
                _candidate = EngineFacade.LoadFrom(candidatePath);

                // Lock in the run params (BDN already set our [Params] values from env-driven ParamsSource)
                var suite = GetSuite(Environment.GetEnvironmentVariable("FORKLIFT_SUITE") ?? "fast");
                _position = suite.First(p => p.Name == PositionName);

                Console.WriteLine($"[BenchInit] Baseline:  {_baseline.Identity}");
                Console.WriteLine($"[BenchInit] Candidate: {_candidate.Identity}");
                Console.WriteLine($"[BenchInit] Depth={Depth} Threads={Threads?.ToString() ?? "<engine default>"} ParallelRoot={ParallelRoot}");

                try
                {
                    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                    {
                        var p = System.Diagnostics.Process.GetCurrentProcess();
                        p.ProcessorAffinity = (IntPtr)1; // CPU0
                        p.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
                    }
                }
                catch { /* ignore on restricted CI */ }
            }

            [GlobalCleanup]
            public void Cleanup()
            {
                _candidate?.Dispose();
                _baseline?.Dispose();
            }

            // NOTE: We let BenchmarkDotNet measure the elapsed time.
            // We call into the engine once per iteration; the engine returns nodes,
            // but we don't compute NPS here—BDN gives ratios & stats we care about.

            [Benchmark(Baseline = true)]
            public (long nodes, double ms, double nps) Baseline()
            {
                var result = _baseline.RunPerft(_position.Fen, Depth, Threads, ParallelRoot);
                return result;
            }

            [Benchmark]
            public (long nodes, double ms, double nps) Candidate()
            {
                var result = _candidate.RunPerft(_position.Fen, Depth, Threads, ParallelRoot);
                return result;
            }
        }

        // ===== Runtime loader & reflection façade (tolerates old/new APIs) =====
        internal sealed class EngineFacade
        {
            private readonly Assembly _coreAsm;
            private readonly MethodInfo _fromFenOrStart;
            private readonly MethodInfo _entry; // Statistics(...) or Count(...)
            private readonly EntryShape _shape;
            private readonly int _depthIndex;
            private readonly int? _parallelIndex;   // nullable => not present
            private readonly int? _threadsIndex;    // nullable => not present
            private readonly AssemblyLoadContext _alc;
            private readonly WeakReference _alcWeakRef;

            private bool _disposed;
            public string Identity =>
                $"{Path.GetFileName(_coreAsm.Location)} " +
                $"sha256={Convert.ToHexString(SHA256.HashData(File.OpenRead(_coreAsm.Location)))[..12]} " +
                $"entry={_entry.DeclaringType!.FullName}.{_entry.Name}({string.Join(",", _entry.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})";


            private enum EntryShape
            {
                StatsObject, // returns stats object with properties
                CountLong    // returns long nodes directly
            }

            private sealed class CoreAlc : AssemblyLoadContext
            {
                private readonly AssemblyDependencyResolver _resolver;
                public CoreAlc(string corePath) : base($"alc:{Path.GetFileName(corePath)}", isCollectible: true)
                    => _resolver = new AssemblyDependencyResolver(corePath);

                protected override Assembly? Load(AssemblyName name)
                {
                    var path = _resolver.ResolveAssemblyToPath(name);
                    return path is null ? null : LoadFromAssemblyPath(path);
                }
            }

            private EngineFacade(AssemblyLoadContext alc, Assembly coreAsm,
                                MethodInfo fromFenOrStart, MethodInfo entry,
                                EntryShape shape, int depthIndex, int? parallelIndex, int? threadsIndex)
            {
                _alc = alc;
                _alcWeakRef = new WeakReference(alc, trackResurrection: false);
                _coreAsm = coreAsm;
                _fromFenOrStart = fromFenOrStart;
                _entry = entry;
                _shape = shape;
                _depthIndex = depthIndex;
                _parallelIndex = parallelIndex;
                _threadsIndex = threadsIndex;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                // Drop strong refs to ALC-held objects ASAP
                // (fields above referencing types/methods keep the ALC alive).
                // We null what we can and then try to unload.
                // Note: fields are readonly; set via locals to aid GC.
                // GC will see they are no longer rooted by this instance once ALC is unloaded.
                _alc.Unload();

                // Aggressive but fast: try a few GC cycles until the ALC dies or we give up.
                for (int i = 0; i < 3 && _alcWeakRef.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                // Optional: one last collection pass
                if (_alcWeakRef.IsAlive)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            public static EngineFacade LoadFrom(string corePath)
            {
                if (!File.Exists(corePath))
                    throw new FileNotFoundException("Core assembly not found", corePath);

                var alc = new CoreAlc(corePath);
                var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(corePath));

                // types
                var boardFactory = asm.GetType("Forklift.Core.BoardFactory", throwOnError: true)!;
                var perft = asm.GetType("Forklift.Core.Perft", throwOnError: true)!;

                var fromFen = boardFactory.GetMethod("FromFenOrStart", BindingFlags.Public | BindingFlags.Static)
                             ?? throw new MissingMethodException("BoardFactory.FromFenOrStart not found.");

                var resolved = ResolvePerftEntry(perft) ?? throw new MissingMethodException(
                    "No compatible Perft.Statistics/Perft.Count overloads found.");

                return new EngineFacade(alc, asm, fromFen, resolved.entry, resolved.shape,
                                        resolved.depthIdx, resolved.parallelIdx, resolved.threadsIdx);
            }

            public (long Nodes, double Ms, double Nps) RunPerft(string fenOrStart, int depth, int? threads, bool? parallelRoot = null)
            {
                var board = _fromFenOrStart.Invoke(null, new object?[] { fenOrStart })
                            ?? throw new InvalidOperationException("BoardFactory.FromFenOrStart returned null.");

                var parameters = _entry.GetParameters();
                var args = new object?[parameters.Length];
                for (int i = 0; i < args.Length; i++)
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;

                args[0] = board;
                args[_depthIndex] = depth;
                if (_parallelIndex is int prIdx) args[prIdx] = parallelRoot is bool pr ? pr : null;
                if (_threadsIndex is int thIdx) args[thIdx] = threads is int t && t > 0 ? (int?)t : null;

                // Optimized path: Count returns long directly.
                if (_shape == EntryShape.CountLong)
                {
                    var timer = Stopwatch.StartNew();
                    var result = _entry.Invoke(null, args);
                    timer.Stop();

                    long countNodes = (result is long l) ? l : Convert.ToInt64(result!);
                    double elapsedMs = timer.Elapsed.TotalMilliseconds;
                    double npsValue = elapsedMs > 0 ? countNodes / Math.Max(1e-9, elapsedMs / 1000.0) : 0.0;

                    if (depth > 0 && countNodes == 0)
                        Console.Error.WriteLine($"[WARN] Perft.Count returned 0 nodes at depth {depth} for '{fenOrStart[..Math.Min(20, fenOrStart.Length)]}...'");

                    return (countNodes, elapsedMs, npsValue);
                }

                // Stats object path: discover once, cache mapper.
                var returnType = _entry.ReturnType;
                var mapper = s_mapperCache.GetOrAdd(returnType, BuildMapper);

                // When the stats type has no timing fields, measure wall time here.
                Stopwatch? wall = null;
                if (!mapper.HasAnyTiming)
                    wall = Stopwatch.StartNew();

                var ret = _entry.Invoke(null, args);
                if (ret is null)
                    throw new InvalidOperationException("Perft entry returned null.");

                wall?.Stop();

                long nodes = mapper.ReadNodes?.Invoke(ret) ?? 0L;
                double ms = mapper.ReadMs?.Invoke(ret) ?? (wall?.Elapsed.TotalMilliseconds ?? 0.0);
                double nps = mapper.ReadNps?.Invoke(ret) ?? (ms > 0 ? nodes / Math.Max(1e-9, ms / 1000.0) : 0.0);

                if (depth > 0 && nodes == 0)
                    Console.Error.WriteLine($"[WARN] Stats object indicates 0 nodes at depth {depth}; verify mapping or implementation.");

                return (nodes, ms, nps);
            }

            private static (MethodInfo entry, EntryShape shape, int depthIdx, int? parallelIdx, int? threadsIdx)? ResolvePerftEntry(Type perftType)
            {
                // We only accept these exact shapes from Forklift history:
                //   Stats: Statistics(Board board, int depth [, bool parallelRoot] [, int? maxThreads])
                //   Count: Count     (Board board, int depth [, bool parallelRoot] [, int? maxThreads])
                //
                // Order is fixed: Board, depth, [parallelRoot], [maxThreads].
                // Names are fixed: "parallelRoot" and "maxThreads".
                // Threads param must be Nullable<int>.

                bool IsNullableInt(Type t) =>
                    t.IsGenericType &&
                    t.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                    t.GetGenericArguments()[0] == typeof(int);

                (MethodInfo mi, EntryShape shape, int depthIdx, int? parIdx, int? thrIdx, int rank)? best = null;

                foreach (var method in perftType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var isStats = method.Name == "Statistics";
                    var isCount = method.Name == "Count";
                    if (!isStats && !isCount)
                        continue;

                    var ps = method.GetParameters();
                    if (ps.Length < 2 || ps.Length > 4)
                        continue;

                    // ps[0] must be Board (we check by name to avoid loading the type across contexts)
                    var p0Name = ps[0].ParameterType.FullName ?? ps[0].ParameterType.Name;
                    if (!p0Name.EndsWith(".Board", StringComparison.Ordinal))
                        continue;

                    // ps[1] must be int depth (name must be "depth")
                    if (ps[1].ParameterType != typeof(int) ||
                        !string.Equals(ps[1].Name, "depth", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int depthIdx = 1;
                    int? parallelIdx = null;
                    int? threadsIdx = null;

                    // Optional ps[2]: bool parallelRoot
                    if (ps.Length >= 3)
                    {
                        if (ps[2].ParameterType == typeof(bool) &&
                            string.Equals(ps[2].Name, "parallelRoot", StringComparison.OrdinalIgnoreCase))
                        {
                            parallelIdx = 2;
                        }
                        else
                        {
                            // If there are 3+ parameters and #2 isn't the canonical parallelRoot, reject.
                            continue;
                        }
                    }

                    // Optional ps[3]: int? maxThreads
                    if (ps.Length == 4)
                    {
                        if (IsNullableInt(ps[3].ParameterType) &&
                            string.Equals(ps[3].Name, "maxThreads", StringComparison.OrdinalIgnoreCase))
                        {
                            threadsIdx = 3;
                        }
                        else
                        {
                            // If there are 4 parameters and #3 isn't the canonical maxThreads, reject.
                            continue;
                        }
                    }

                    // Validate return type for the two shapes
                    var shape = isStats ? EntryShape.StatsObject : EntryShape.CountLong;
                    if (shape == EntryShape.CountLong && method.ReturnType != typeof(long))
                        continue; // Count must return long
                    if (shape == EntryShape.StatsObject && method.ReturnType == typeof(void))
                        continue; // Statistics must return a value (struct/object with Nodes, etc.)

                    // Rank preference: prefer richest Statistics overload, then Count.
                    // parallelRoot + maxThreads (2 extras) outranks only parallelRoot (1 extra), which outranks none.
                    int richness = (shape == EntryShape.StatsObject ? 100 : 0)
                                 + (parallelIdx.HasValue ? 10 : 0)
                                 + (threadsIdx.HasValue ? 1 : 0);

                    var candidate = (method, shape, depthIdx, parallelIdx, threadsIdx, richness);
                    if (best is null || candidate.richness > best.Value.rank)
                        best = candidate;
                }

                if (best is null) return null;
                return (best.Value.mi, best.Value.shape, best.Value.depthIdx, best.Value.parIdx, best.Value.thrIdx);
            }

            private sealed class StatsMapper
            {
                public Func<object, long>? ReadNodes { get; init; }
                public Func<object, double>? ReadMs { get; init; }
                public Func<object, double>? ReadNps { get; init; }
                public bool HasAnyTiming => ReadMs is not null || ReadNps is not null;
            }

            private static readonly ConcurrentDictionary<Type, StatsMapper> s_mapperCache = new();

            private static StatsMapper BuildMapper(Type returnType)
            {
                // Count(long) shape handled elsewhere; only map object/struct shapes here.
                var param = Expression.Parameter(typeof(object), "obj");
                var typed = Expression.Convert(param, returnType);

                static MemberInfo? FindMember(Type t, string[] names, Type? exactType = null, Func<Type, bool>? typePred = null)
                {
                    BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                    foreach (var n in names)
                    {
                        var p = t.GetProperty(n, flags);
                        if (p is not null && (exactType is null ? typePred?.Invoke(p.PropertyType) != false : p.PropertyType == exactType))
                            return p;

                        var f = t.GetField(n, flags);
                        if (f is not null && (exactType is null ? typePred?.Invoke(f.FieldType) != false : f.FieldType == exactType))
                            return f;
                    }
                    return null;
                }

                // Build accessor: property or field -> object -> TOut (via Convert/ChangeType)
                static Func<object, TOut>? CompileGetter<TOut>(ParameterExpression objParam, Expression typedObj, MemberInfo? member)
                {
                    if (member is null) return null;

                    Expression read = member switch
                    {
                        PropertyInfo pi => Expression.Property(typedObj, pi),
                        FieldInfo fi => Expression.Field(typedObj, fi),
                        _ => throw new InvalidOperationException()
                    };

                    // Handle TimeSpan -> double ms conversion in the ReadMs builder (below).
                    var body = Expression.Convert(read, typeof(TOut));
                    return Expression.Lambda<Func<object, TOut>>(body, objParam).Compile();
                }

                // Accept common aliases from your history and typical perf objects:
                string[] nodesNames = { "Nodes", "TotalNodes", "NodesVisited", "Visited", "Count" };
                string[] msDoubleNames = { "Ms", "ElapsedMs", "MedianElapsedMs" };
                string[] msTimeSpanNames = { "Elapsed", "TotalElapsed", "ElapsedTime", "TotalElapsedTime", "Duration" };
                string[] npsNames = { "Nps", "AggregateNps", "NpsMedian" };

                var nodesMember = FindMember(returnType, nodesNames, exactType: typeof(long));
                var npsMember = FindMember(returnType, npsNames, exactType: typeof(double));

                // Prefer double-ms members; if absent, accept TimeSpan-typed members and convert to ms.
                var msDoubleMember = FindMember(returnType, msDoubleNames, exactType: typeof(double));
                var msTimeSpanMember = msDoubleMember is null
                    ? FindMember(returnType, msTimeSpanNames, exactType: typeof(TimeSpan))
                    : null;

                var readNodes = CompileGetter<long>(param, typed, nodesMember);
                var readNps = CompileGetter<double>(param, typed, npsMember);

                Func<object, double>? readMs = null;
                if (msDoubleMember is not null)
                {
                    readMs = CompileGetter<double>(param, typed, msDoubleMember);
                }
                else if (msTimeSpanMember is not null)
                {
                    // Build a lambda that converts TimeSpan to milliseconds as double.
                    MemberExpression readTs = msTimeSpanMember switch
                    {
                        PropertyInfo pi => Expression.Property(typed, pi),
                        FieldInfo fi => Expression.Field(typed, fi),
                        _ => throw new InvalidOperationException()
                    };
                    var tsTotalMs = Expression.Property(readTs, nameof(TimeSpan.TotalMilliseconds));
                    var body = Expression.Convert(tsTotalMs, typeof(double));
                    readMs = Expression.Lambda<Func<object, double>>(body, param).Compile();
                }

                return new StatsMapper
                {
                    ReadNodes = readNodes,
                    ReadMs = readMs,
                    ReadNps = readNps
                };
            }
        }

        // ===== Minimal CLI =====
        internal sealed class CliArgs
        {
            public string? BaselinePath { get; init; }
            public string? CandidatePath { get; init; }
            public string? Suite { get; init; }            // minimal|fast|full (default minimal)
            public int? Depth { get; init; }               // default 4
            public int? Threads { get; init; }             // null => engine default
            public bool ParallelRoot { get; init; }

            public static CliArgs Parse(string[] argv)
            {
                var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < argv.Length; i++)
                {
                    var tok = argv[i];
                    if (!tok.StartsWith("--")) continue;
                    var eq = tok.IndexOf('=');
                    if (eq > 0) { map[tok[..eq]] = tok[(eq + 1)..]; }
                    else
                    {
                        var key = tok;
                        if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) map[key] = argv[++i];
                        else map[key] = "true";
                    }
                }

                int? GetInt(string k)
                    => map.TryGetValue(k, out var s) && int.TryParse(s, out var v) ? v : (int?)null;

                string? GetStr(string k) => map.TryGetValue(k, out var s) ? s : null;

                bool GetBool(string k) => map.TryGetValue(k, out var s) && bool.TryParse(s, out var v) && v;

                return new CliArgs
                {
                    BaselinePath = GetStr("--baseline"),
                    CandidatePath = GetStr("--candidate"),
                    Suite = GetStr("--suite"),
                    Depth = GetInt("--depth"),
                    Threads = GetInt("--threads"),
                    ParallelRoot = GetBool("--parallelRoot")
                };
            }
        }
    }
}
