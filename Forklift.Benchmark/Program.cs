using System.Reflection;
using System.Runtime.Loader;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;

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
                return _baseline.RunPerft(_position.Fen, _depth, _threads);
            }

            [Benchmark(Baseline = false)]
            public (long Nodes, double Ms, double Nps) Candidate()
            {
                return _candidate.RunPerft(_position.Fen, _depth, _threads);
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

                // Build arg list matching the selected overload
                var parms = _entry.GetParameters();
                var args = new object?[parms.Length];

                // Fill defaults
                for (int i = 0; i < args.Length; i++)
                    args[i] = parms[i].HasDefaultValue ? parms[i].DefaultValue : (object?)null;

                // Map required parameters we know exist
                // First param should be Board, second is (usually) depth — but we computed index robustly.
                args[0] = board;
                args[_depthIndex] = depth;

                if (_parallelIndex is int pi)
                    args[pi] = true; // we prefer parallel root when available

                if (_threadsIndex is int ti)
                    args[ti] = threads is int t && t > 0 ? t : null;

                var ret = _entry.Invoke(null, args);

                if (_shape == EntryShape.CountLong)
                {
                    // Long count only
                    long n = (ret is long l) ? l : Convert.ToInt64(ret!);
                    return (n, 0.0, 0.0);
                }

                // Stats object — duck read
                if (ret is null) throw new InvalidOperationException("Perft entry returned null.");

                long nodes = (long?)TryGet(ret, "Nodes")
                        ?? (long?)TryGet(ret, "TotalNodes")
                        ?? (long?)TryGet(ret, "NodesMedian")
                        ?? (long?)TryGet(ret, "Count")
                        ?? 0L;

                double ms = (double?)TryGet(ret, "ElapsedMs")
                    ?? (double?)TryGet(ret, "TotalElapsedMs")
                    ?? (double?)TryGet(ret, "ElapsedMilliseconds")
                    ?? (double?)TryGet(ret, "Ms")
                    ?? (double?)TryGet(ret, "MedianElapsedMs")
                    ?? 0.0;

                double nps = (double?)TryGet(ret, "Nps")
                        ?? (double?)TryGet(ret, "AggregateNps")
                        ?? (double?)TryGet(ret, "NpsMedian")
                        ?? 0.0;

                if (nps <= 0 && ms > 0)
                    nps = nodes / Math.Max(1e-9, ms / 1000.0);

                return (nodes, ms, nps);

                // Local helpers
                static object? TryGet(object obj, string prop)
                    => obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            }

            private static (MethodInfo entry, EntryShape shape, int depthIdx, int? parallelIdx, int? threadsIdx)? ResolvePerftEntry(Type perftType)
            {
                var cand = new List<(MethodInfo mi, bool isStats, int depthIdx, int? parIdx, int? thrIdx, int rank)>();

                foreach (var mi in perftType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name is not ("Statistics" or "Count"))
                        continue;

                    var ps = mi.GetParameters();
                    if (ps.Length == 0) continue;

                    // Must accept a Board as first parameter (loose check by name to avoid loading type)
                    var firstTypeName = ps[0].ParameterType.FullName ?? ps[0].ParameterType.Name;
                    if (!firstTypeName.EndsWith(".Board", StringComparison.Ordinal))
                        continue;

                    // Find depth param (int) anywhere after the Board
                    int depthIdx = Array.FindIndex(ps, p => p.ParameterType == typeof(int) && p.Position > 0);
                    if (depthIdx < 0) continue;

                    // Optional parallelRoot param (bool)
                    int parIdx = Array.FindIndex(ps, p => p.ParameterType == typeof(bool) && p.Position > depthIdx);

                    // Optional threads param (int or Nullable<int>) with flexible names
                    int thrIdx = Array.FindIndex(ps, p =>
                    {
                        var t = p.ParameterType;
                        bool isIntLike = t == typeof(int) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>) && t.GetGenericArguments()[0] == typeof(int));
                        if (!isIntLike) return false;
                        var n = p.Name ?? "";
                        return n.Equals("maxThreads", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("threads", StringComparison.OrdinalIgnoreCase)
                            || n.Equals("numThreads", StringComparison.OrdinalIgnoreCase);
                    });

                    bool isStats = mi.Name == "Statistics" && mi.ReturnType != typeof(void);

                    // Rank by richness: Stats with threads+parallel, then Stats with parallel, then Stats simple,
                    // then Count with threads+parallel, then Count with parallel, then Count simple.
                    int richness =
                        (isStats ? 100 : 0) +
                        (thrIdx >= 0 ? 10 : 0) +
                        (parIdx >= 0 ? 1 : 0);

                    cand.Add((mi, isStats, depthIdx, parIdx >= 0 ? parIdx : (int?)null, thrIdx >= 0 ? thrIdx : (int?)null, richness));
                }

                if (cand.Count == 0) return null;

                var best = cand.OrderByDescending(x => x.rank).First();

                var shape = best.isStats ? EntryShape.StatsObject : EntryShape.CountLong;
                return (best.mi, shape, best.depthIdx, best.parIdx, best.thrIdx);
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
    }
}