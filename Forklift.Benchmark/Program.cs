using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;
using TraceReloggerLib;

namespace Forklift.Benchmark
{
    public static class Program
    {
        private static Job Quick(Job j) => j
            .WithIterationTime(TimeInterval.FromMilliseconds(200))
            .WithMinIterationCount(8)
            .WithMaxIterationCount(20)
            .WithMaxRelativeError(0.02);

        private static Job Thorough(Job j) => j
            .WithIterationTime(TimeInterval.FromMilliseconds(750))
            .WithMinIterationCount(20)
            .WithMaxIterationCount(60)
            .WithMaxRelativeError(0.005);

        public static int Main(string[] args)
        {
            // ---- Parse CLI ----
            var cli = CliArgs.Parse(args);

            if (string.IsNullOrWhiteSpace(cli.BaselinePath) || string.IsNullOrWhiteSpace(cli.CandidatePath))
            {
                Console.Error.WriteLine("Usage:");
                Console.Error.WriteLine("  dotnet run -c Release --project Forklift.Benchmark -- " +
                                        "--baseline <path-to-Forklift.Core.dll> --candidate <path-to-Forklift.Core.dll> " +
                                        "[--suite minimal|fast|full] [--depth 4] [--threads N] [--affinity MASK] [--highPriority]");
                return 2;
            }

            // ---- Load cores (runtime, isolated) ----
            try
            {
                Inputs.Baseline = EngineFacade.LoadFrom(cli.BaselinePath);
                Inputs.Candidate = EngineFacade.LoadFrom(cli.CandidatePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load Forklift.Core DLLs: {ex.Message}");
                return 3;
            }


            try
            {
                var (smokeNodesA, _, _) = Inputs.Baseline!.RunPerft("startpos", 1, null);
                if (smokeNodesA <= 0)
                    Console.Error.WriteLine("[WARN] Baseline smoke test returned 0 nodes for depth=1; reflection mapping is likely wrong.");
                var (smokeNodesB, _, _) = Inputs.Candidate!.RunPerft("startpos", 1, null);
                if (smokeNodesB <= 0)
                    Console.Error.WriteLine("[WARN] Candidate smoke test returned 0 nodes for depth=1; reflection mapping is likely wrong.");
                if (smokeNodesA != smokeNodesB)
                    Console.Error.WriteLine($"[WARN] Smoke test node counts differ: Baseline={smokeNodesA}, Candidate={smokeNodesB}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Smoke test failed: {ex.Message}");
                return 4;
            }


            Inputs.Depth = cli.Depth ?? 4;
            Inputs.Threads = cli.Threads; // null => engine default
            Inputs.SuiteName = (cli.Suite ?? "minimal").ToLowerInvariant();

            var job = Inputs.SuiteName switch
            {
                "minimal" => Quick(Job.Default),
                "fast" => Thorough(Job.Default),
                "full" => Thorough(Job.Default),
                _ => Quick(Job.Default)
            };

            // Create a ManualConfig and add the job and other options
            var config = ManualConfig.Create(DefaultConfig.Instance)
                .AddColumn(new NodesPerOpColumn(), new TotalNodesColumn(), new AggregateNpsColumn())
                .AddDiagnoser(MemoryDiagnoser.Default)          // you also have [MemoryDiagnoser]; harmless to keep here
                .AddDiagnoser(ThreadingDiagnoser.Default)       // adds the Threading diagnostics block
                .AddExporter(MarkdownExporter.GitHub)
                .AddExporter(JsonExporter.Full)
                .AddJob(job
                        .WithToolchain(InProcessEmitToolchain.Instance)
                        .WithEnvironmentVariable("COMPlus_ReadyToRun", "0")
                        .WithEnvironmentVariable("COMPlus_TieredCompilation", "0")
                        .WithEnvironmentVariable("COMPlus_TieredPGO", "0")
                );

            var summary = BenchmarkRunner.Run<PerftAABench>(config);

            return 0;
        }

        // ===== Shared inputs pushed into the benchmark =====
        internal static class Inputs
        {
            public static EngineFacade? Baseline;
            public static EngineFacade? Candidate;
            public static string SuiteName = "minimal";
            public static int Depth = 4;
            public static int? Threads;
        }

        // ===== Benchmark type =====
        [MemoryDiagnoser(true)]
        [Orderer(SummaryOrderPolicy.FastestToSlowest)]
        [RankColumn, MinColumn, MaxColumn, Q1Column, Q3Column, AllStatisticsColumn]
        [JsonExporterAttribute.Full]
        public class PerftAABench
        {
            // You can add/remove positions here; names feed [ParamsSource].
            private static readonly (string Name, string Fen)[] MinimalSuite =
            {
                ("startpos", "startpos"),
                ("kiwipete", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1"),
                ("tricky-ep","8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1"),
            };

            private static readonly (string Name, string Fen)[] FastSuite =
                MinimalSuite;

            private static readonly (string Name, string Fen)[] FullSuite = FastSuite;

            private (long Nodes, double Ms, double Nps) _last;
            private string _caseKey = "";
            private string CaseKey(string methodName) => $"{PositionName}::{methodName}";

            internal static readonly ConcurrentDictionary<string, long> NodesPerOp = new();


            // Parameterize which position is under test
            [ParamsSource(nameof(PositionNames))]
            public string PositionName { get; set; } = "startpos";

            public static IEnumerable<string> PositionNames =>
                GetSuite(Program.Inputs.SuiteName).Select(p => p.Name);

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
            private int _depth;
            private int? _threads;

            [GlobalSetup]
            public void Setup()
            {
                _baseline = Program.Inputs.Baseline ?? throw new InvalidOperationException("Baseline not loaded.");
                _candidate = Program.Inputs.Candidate ?? throw new InvalidOperationException("Candidate not loaded.");
                _depth = Program.Inputs.Depth;
                _threads = Program.Inputs.Threads;

                var suite = GetSuite(Program.Inputs.SuiteName);
                _position = suite.First(p => p.Name == PositionName);
            }

            // NOTE: We let BenchmarkDotNet measure the elapsed time.
            // We call into the engine once per iteration; the engine returns nodes,
            // but we don't compute NPS here—BDN gives ratios & stats we care about.

            [Benchmark(Baseline = true)]
            public (long Nodes, double Ms, double Nps) Baseline()
            {
                _last = _baseline.RunPerft(_position.Fen, _depth, _threads);
                _caseKey = CaseKey(nameof(Baseline));

                NodesPerOp.TryAdd(_caseKey, _last.Nodes);
                return _last;
            }

            [Benchmark(Baseline = false)]
            public (long Nodes, double Ms, double Nps) Candidate()
            {
                _last = _candidate.RunPerft(_position.Fen, _depth, _threads);
                _caseKey = CaseKey(nameof(Candidate));

                NodesPerOp.TryAdd(_caseKey, _last.Nodes);
                return _last;
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

            private enum EntryShape
            {
                StatsObject, // returns stats object with properties
                CountLong    // returns long nodes directly
            }

            private EngineFacade(AssemblyLoadContext alc, Assembly coreAsm,
                                MethodInfo fromFenOrStart, MethodInfo entry,
                                EntryShape shape, int depthIndex, int? parallelIndex, int? threadsIndex)
            {
                _alc = alc;
                _coreAsm = coreAsm;
                _fromFenOrStart = fromFenOrStart;
                _entry = entry;
                _shape = shape;
                _depthIndex = depthIndex;
                _parallelIndex = parallelIndex;
                _threadsIndex = threadsIndex;
            }

            public static EngineFacade LoadFrom(string corePath)
            {
                if (!File.Exists(corePath))
                    throw new FileNotFoundException("Core assembly not found", corePath);

                var bytes = File.ReadAllBytes(corePath);
                var alc = new AssemblyLoadContext($"alc:{Path.GetFileName(corePath)}", isCollectible: true);
                using var ms = new MemoryStream(bytes);
                var asm = alc.LoadFromStream(ms);

                var boardFactory = asm.GetType("Forklift.Core.BoardFactory", throwOnError: true)!;
                var perft = asm.GetType("Forklift.Core.Perft", throwOnError: true)!;

                var fromFen = boardFactory.GetMethod("FromFenOrStart", BindingFlags.Public | BindingFlags.Static)
                            ?? throw new MissingMethodException("BoardFactory.FromFenOrStart not found.");

                // Try Statistics first (richer), then Count
                var (entry, shape, depthIdx, parallelIdx, threadsIdx) =
                    ResolvePerftEntry(perft) ?? throw new MissingMethodException(
                        "No compatible Perft.Statistics/Perft.Count overloads found.");

                return new EngineFacade(alc, asm, fromFen, entry, shape, depthIdx, parallelIdx, threadsIdx);
            }

            public (long Nodes, double Ms, double Nps) RunPerft(string fenOrStart, int depth, int? threads)
            {
                var board = _fromFenOrStart.Invoke(null, new object?[] { fenOrStart })
                            ?? throw new InvalidOperationException("BoardFactory.FromFenOrStart returned null.");

                var parms = _entry.GetParameters();
                var args = new object?[parms.Length];
                for (int i = 0; i < args.Length; i++)
                    args[i] = parms[i].HasDefaultValue ? parms[i].DefaultValue : null;

                args[0] = board;
                args[_depthIndex] = depth;
                if (_parallelIndex is int pi) args[pi] = true;
                if (_threadsIndex is int ti) args[ti] = threads is int t && t > 0 ? (int?)t : null;


                var ret = _entry.Invoke(null, args);

                if (_shape == EntryShape.CountLong)
                {
                    long n = (ret is long l) ? l : Convert.ToInt64(ret!);
                    if (depth > 0 && n == 0)
                        Console.Error.WriteLine($"[WARN] Perft.Count returned 0 nodes at depth {depth} for '{fenOrStart[..Math.Min(20, fenOrStart.Length)]}...'");
                    return (n, 0.0, 0.0);
                }

                if (ret is null) throw new InvalidOperationException("Perft entry returned null.");

                // Duck-typed property reads (properties OR fields)
                object? GetMember(object obj, string name)
                {
                    var t = obj.GetType();
                    return t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj)
                        ?? t.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
                }

                long nodes =
                    (long?)GetMember(ret, "Nodes")
                 ?? (long?)GetMember(ret, "TotalNodes")
                 ?? (long?)GetMember(ret, "NodesVisited")
                 ?? (long?)GetMember(ret, "Visited")
                 ?? (long?)GetMember(ret, "Count")
                 ?? 0L;

                double ms =
                    (double?)GetMember(ret, "ElapsedMs")
                 ?? (double?)GetMember(ret, "TotalElapsedMs")
                 ?? (double?)GetMember(ret, "ElapsedMilliseconds")
                 ?? (double?)GetMember(ret, "Ms")
                 ?? (double?)GetMember(ret, "MedianElapsedMs")
                 ?? 0.0;

                double nps =
                    (double?)GetMember(ret, "Nps")
                 ?? (double?)GetMember(ret, "AggregateNps")
                 ?? (double?)GetMember(ret, "NpsMedian")
                 ?? 0.0;

                if (nps <= 0 && ms > 0)
                    nps = nodes / Math.Max(1e-9, ms / 1000.0);

                if (depth > 0 && nodes == 0)
                    Console.Error.WriteLine($"[WARN] Stats object indicates 0 nodes at depth {depth}; check reflection mapping of properties/parameters.");

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
        }

        // ===== Minimal CLI =====
        internal sealed class CliArgs
        {
            public string? BaselinePath { get; init; }
            public string? CandidatePath { get; init; }
            public string? Suite { get; init; }            // minimal|fast|full (default minimal)
            public int? Depth { get; init; }               // default 4
            public int? Threads { get; init; }             // null => engine default
            public int? AffinityMask { get; init; }        // optional
            public bool HighPriority { get; init; }

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

                return new CliArgs
                {
                    BaselinePath = GetStr("--baseline"),
                    CandidatePath = GetStr("--candidate"),
                    Suite = GetStr("--suite"),
                    Depth = GetInt("--depth"),
                    Threads = GetInt("--threads")
                };
            }
        }

        internal static class BdnAgg
        {
            public static (long totalOps, double totalMs) GetWorkloadOpsAndMs(Summary summary, BenchmarkCase bc)
            {
                var report = summary.Reports.FirstOrDefault(r => r.BenchmarkCase.Equals(bc));
                if (report is null) return (0, 0);

                // Only the actual workload measurements (exclude Overhead/Pilot/Warmup)
                var ms = report.AllMeasurements
                    .Where(m => m.IterationMode == IterationMode.Workload &&
                                m.IterationStage == IterationStage.Actual)
                    .ToList();

                long totalOps = ms.Sum(m => (long)m.Operations);
                double totalMs = ms.Sum(m => m.Nanoseconds) / 1_000_000.0;
                return (totalOps, totalMs);
            }
        }

        internal sealed class NodesPerOpColumn : IColumn
        {
            public string Id => "PerftNodesPerOp";
            public string ColumnName => "Nodes/Op";
            public bool AlwaysShow => true;
            public ColumnCategory Category => ColumnCategory.Custom;
            public int PriorityInCategory => 0;
            public bool IsNumeric => true;
            public UnitType UnitType => UnitType.Size;
            public bool IsDefault(Summary s, BenchmarkCase bc) => false;
            public string Legend => "Per-iteration node count for this position/depth.";

            public string GetValue(Summary summary, BenchmarkCase bc)
            {
                var pos = bc.Parameters["PositionName"]?.ToString() ?? "";
                var method = bc.Descriptor.WorkloadMethod.Name;
                var key = $"{pos}::{method}";
                if (Forklift.Benchmark.Program.PerftAABench.NodesPerOp.TryGetValue(key, out var nodes))
                    return nodes.ToString("N0");
                return "";
            }

            public string GetValue(Summary s, BenchmarkCase bc, SummaryStyle style) => GetValue(s, bc);
            public bool IsAvailable(Summary _) => true;
        }

        internal sealed class TotalNodesColumn : IColumn
        {
            public string Id => "PerftTotalNodes";
            public string ColumnName => "TotalNodes";
            public bool AlwaysShow => true;
            public ColumnCategory Category => ColumnCategory.Custom;
            public int PriorityInCategory => 1;
            public bool IsNumeric => true;
            public UnitType UnitType => UnitType.Size;
            public bool IsDefault(Summary s, BenchmarkCase bc) => false;
            public string Legend => "Σ(nodes-per-op × measured operations).";

            public string GetValue(Summary summary, BenchmarkCase bc)
            {
                var pos = bc.Parameters["PositionName"]?.ToString() ?? "";
                var method = bc.Descriptor.WorkloadMethod.Name;
                var key = $"{pos}::{method}";

                if (!Forklift.Benchmark.Program.PerftAABench.NodesPerOp.TryGetValue(key, out var nodesPerOp))
                    return "";

                var (ops, _) = BdnAgg.GetWorkloadOpsAndMs(summary, bc);
                if (ops <= 0) return "";
                var totalNodes = nodesPerOp * (decimal)ops;
                return totalNodes.ToString("N0");
            }

            public string GetValue(Summary s, BenchmarkCase bc, SummaryStyle style) => GetValue(s, bc);
            public bool IsAvailable(Summary _) => true;
        }

        internal sealed class AggregateNpsColumn : IColumn
        {
            public string Id => "PerftAggregateNps";
            public string ColumnName => "Agg NPS";
            public bool AlwaysShow => true;
            public ColumnCategory Category => ColumnCategory.Custom;
            public int PriorityInCategory => 2;
            public bool IsNumeric => true;
            public UnitType UnitType => UnitType.Size;
            public bool IsDefault(Summary s, BenchmarkCase bc) => false;
            public string Legend => "Σ(nodes) / Σ(time) over measured iterations only.";

            public string GetValue(Summary summary, BenchmarkCase bc)
            {
                var pos = bc.Parameters["PositionName"]?.ToString() ?? "";
                var method = bc.Descriptor.WorkloadMethod.Name;
                var key = $"{pos}::{method}";

                if (!Forklift.Benchmark.Program.PerftAABench.NodesPerOp.TryGetValue(key, out var nodesPerOp))
                    return "";

                var (ops, totalMs) = BdnAgg.GetWorkloadOpsAndMs(summary, bc);
                if (ops <= 0 || totalMs <= 0) return "";

                double totalNodes = nodesPerOp * (double)ops;
                double nps = totalNodes / (totalMs / 1000.0);

                return nps >= 1_000_000_000 ? (nps / 1_000_000_000d).ToString("0.00") + " B/s"
                     : nps >= 1_000_000 ? (nps / 1_000_000d).ToString("0.00") + " M/s"
                     : nps >= 1_000 ? (nps / 1_000d).ToString("0.00") + " K/s"
                                              : nps.ToString("0") + " /s";
            }

            public string GetValue(Summary s, BenchmarkCase bc, SummaryStyle style) => GetValue(s, bc);
            public bool IsAvailable(Summary _) => true;
        }
    }
}