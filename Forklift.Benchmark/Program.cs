using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Forklift.Core;

public static class Program
{
    // ===== Perft Suites (edit as you like) =====
    private static readonly Dictionary<string, SuiteEntry> MinimalSuite = new()
    {
        ["startpos"] = new SuiteEntry(
            FenOrStart: "startpos",
            ExpectedByDepth: new Dictionary<int, long> {
                {1,20}, {2,400}, {3,8902}, {4,197281}, {5,4865609}
            }),
        ["kiwipete"] = new SuiteEntry(
            FenOrStart: "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            ExpectedByDepth: new Dictionary<int, long> {
                {1,48}, {2,2039}, {3,97862}, {4,4085603}, {5,193690690}
            }),
        ["tricky-ep"] = new SuiteEntry(
            FenOrStart: "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            ExpectedByDepth: new Dictionary<int, long> {
                {1,14}, {2,191}, {3,2812}, {4,43238}, {5,674624}
            }),
    };

    private static readonly Dictionary<string, SuiteEntry> FastSuite = new(MinimalSuite)
    {
        ["r2q1rk1_mix"] = new SuiteEntry(
            FenOrStart: "r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1",
            ExpectedByDepth: new Dictionary<int, long> {
                {1,6}, {2,264}, {3,9467}, {4,422333}, {5,15833292}
            })
    };

    private static readonly Dictionary<string, SuiteEntry> FullSuite = new(FastSuite);

    private static Dictionary<string, SuiteEntry> GetSuite(string name) =>
        name.ToLowerInvariant() switch
        {
            "minimal" => MinimalSuite,
            "fast" => FastSuite,
            "full" => FullSuite,
            _ => FastSuite
        };

    public static int Main(string[] args)
    {
        var p = Args.Parse(args);

        // New: optional preset and helpers
        string preset = p.Get("--preset", "");   // quick | ci | deep
        bool skipCorr = p.Has("--skipCorrectness");
        int sampleN = p.Get("--positions", 0); // 0 = use all positions

        // Tighter defaults (you can tweak these):
        string suite = p.Get("--suite", "minimal");
        int depth = p.Get("--depth", 4);
        int repeat = p.Get("--repeat", 3);
        int warmup = p.Get("--warmup", 1);
        bool json = p.Has("--json");
        string? outFile = p.Get<string?>("--out", null);
        int threads = p.Get("--threads", 0);
        bool hiPrio = p.Has("--highPriority");
        bool keepHist = p.Has("--keepHistory");

        // Apply presets AFTER parsing so they override defaults/CLI:
        switch (preset.ToLowerInvariant())
        {
            case "quick":
                suite = "minimal"; depth = Math.Min(depth, 3); repeat = 1; warmup = 0;
                skipCorr = true; // fastest possible smoke
                break;
            case "ci":
                suite = "minimal"; depth = Math.Max(depth, 4); repeat = Math.Max(repeat, 3); warmup = Math.Max(warmup, 1);
                // keep correctness on for CI unless user explicitly passes --skipCorrectness
                break;
            case "deep":
                suite = "fast"; depth = Math.Max(depth, 5); repeat = Math.Max(repeat, 5); warmup = Math.Max(warmup, 1);
                break;
        }

        var suiteDict = GetSuite(suite);
        if (suiteDict.Count == 0)
        {
            Console.Error.WriteLine($"Suite '{suite}' is empty.");
            return 2;
        }

        // Optional: sample a subset of positions for speed
        var rng = new Random(12345);
        var positions = suiteDict.ToList();
        if (sampleN > 0 && sampleN < positions.Count)
            positions = positions.OrderBy(_ => rng.Next()).Take(sampleN).ToList();

        TrySetProcessPriority(hiPrio);
        ForceGC();

        var results = new List<BenchResult>(positions.Count);
        long totalNodes = 0;
        double totalMs = 0;

        foreach (var (name, entry) in positions)
        {
            // Respect skipCorrectness
            if (!skipCorr && entry.ExpectedByDepth.TryGetValue(depth, out var expected))
            {
                LogWithLocalTimestamp($"[INFO] Correctness check for '{name}' depth {depth}...");
                var b0 = BoardFactory.FromFenOrStart(entry.FenOrStart);
                b0.KeepTrackOfHistory = keepHist;
                long nodes0 = Perft.Count(b0, depth, parallelRoot: true);
                if (nodes0 != expected)
                    ErrorWithLocalTimestamp($"[FAIL] {name} depth {depth}: expected {expected}, got {nodes0}");
                else
                    LogWithLocalTimestamp($"[PASS] {name} depth {depth}: {nodes0} nodes");
            }

            // warmups (unmeasured)
            for (int i = 0; i < warmup; i++)
            {
                var wb = BoardFactory.FromFenOrStart(entry.FenOrStart);
                wb.KeepTrackOfHistory = keepHist;
                _ = Perft.Count(wb, Math.Min(depth, 3), parallelRoot: true);
            }

            // repeats (measured)
            var measurements = new List<(long Nodes, double Ms, double Nps)>(repeat);
            for (int r = 0; r < repeat; r++)
            {
                var b = BoardFactory.FromFenOrStart(entry.FenOrStart);
                b.KeepTrackOfHistory = keepHist;

                var sw = Stopwatch.StartNew();
                var stats = Perft.Statistics(b, depth, parallelRoot: true);
                sw.Stop();

                var secs = Math.Max(1e-9, sw.Elapsed.TotalSeconds);
                measurements.Add((stats.Nodes, sw.Elapsed.TotalMilliseconds, stats.Nodes / secs));
            }

            var nodesMed = Median(measurements.Select(x => (double)x.Nodes));
            var msMed = Median(measurements.Select(x => x.Ms));
            var npsMed = Median(measurements.Select(x => x.Nps));

            totalNodes += (long)nodesMed;
            totalMs += msMed;

            results.Add(new BenchResult(
                Name: name,
                Fen: entry.FenOrStart,
                Depth: depth,
                NodesMedian: (long)nodesMed,
                ElapsedMsMedian: msMed,
                NpsMedian: npsMed,
                Repeats: repeat
            ));
        }

        var aggregateSecs = totalMs / 1000.0;
        var aggregateNps = totalNodes / Math.Max(1e-9, aggregateSecs);

        var summary = new BenchSummary(
            Suite: suite, Depth: depth, Repeat: repeat, Warmup: warmup,
            KeepHistory: keepHist, Threads: (threads > 0 ? threads : (int?)null),
            TimestampUtc: DateTime.UtcNow, Results: results,
            TotalNodes: totalNodes, TotalElapsedMs: totalMs, AggregateNps: aggregateNps
        );

        if (json)
        {
            var jsonText = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            LogWithLocalTimestamp(jsonText);
            if (!string.IsNullOrWhiteSpace(outFile))
                System.IO.File.WriteAllText(outFile!, jsonText);
        }
        else
        {
            LogWithLocalTimestamp($"Suite={suite} Depth={depth} Repeat={repeat} Positions={results.Count}");
            foreach (var r in results)
                LogWithLocalTimestamp($"{r.Name,-12} Nodes={r.NodesMedian:N0}  Elapsed={r.ElapsedMsMedian:N0} ms  NPS={r.NpsMedian:N0}");
            LogWithLocalTimestamp($"TOTAL       Nodes={summary.TotalNodes:N0}  Elapsed={summary.TotalElapsedMs:N0} ms  NPS={summary.AggregateNps:N0}");
        }

        return 0;
    }

    // ===== Helpers & models =====

    private static void TrySetProcessPriority(bool hi)
    {
        try
        {
            if (!hi) return;
            var p = Process.GetCurrentProcess();
            p.PriorityClass = ProcessPriorityClass.High;
        }
        catch { /* ignore */ }
    }

    private static void ForceGC()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.OrderBy(x => x).ToArray();
        if (arr.Length == 0) return 0;
        int mid = arr.Length / 2;
        return (arr.Length % 2 == 1) ? arr[mid] : (arr[mid - 1] + arr[mid]) / 2.0;
    }

    private record SuiteEntry(string FenOrStart, Dictionary<int, long> ExpectedByDepth);
    private record BenchResult(string Name, string Fen, int Depth, long NodesMedian, double ElapsedMsMedian, double NpsMedian, int Repeats);
    private record BenchSummary(string Suite, int Depth, int Repeat, int Warmup, bool KeepHistory, int? Threads, DateTime TimestampUtc,
        List<BenchResult> Results, long TotalNodes, double TotalElapsedMs, double AggregateNps);

    // --- Very small flag parser (---k v / ---k=v / boolean switches) ---
    private sealed class Args
    {
        private readonly Dictionary<string, string?> _map;

        private Args(Dictionary<string, string?> map) => _map = map;

        public static Args Parse(string[] argv)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < argv.Length; i++)
            {
                var tok = argv[i];
                if (!tok.StartsWith("--")) continue;

                var eq = tok.IndexOf('=');
                if (eq > 0)
                {
                    var key = tok[..eq];
                    var val = tok[(eq + 1)..];
                    dict[key] = val;
                }
                else
                {
                    var key = tok;
                    // If next token exists and is not another --flag, treat it as value
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--"))
                    {
                        dict[key] = argv[++i];
                    }
                    else
                    {
                        // boolean switch
                        dict[key] = "true";
                    }
                }
            }
            return new Args(dict);
        }

        public bool Has(string key) => _map.ContainsKey(key);

        public T Get<T>(string key, T fallback)
        {
            if (!_map.TryGetValue(key, out var s) || s is null) return fallback;
            try
            {
                if (typeof(T) == typeof(string)) return (T)(object)s;
                if (typeof(T) == typeof(int)) return (T)(object)int.Parse(s);
                if (typeof(T) == typeof(bool)) return (T)(object)ParseBool(s);
                if (typeof(T) == typeof(double)) return (T)(object)double.Parse(s);
                return fallback;
            }
            catch { return fallback; }
        }

        private static bool ParseBool(string s)
        {
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            // allow 1/0
            if (int.TryParse(s, out var i)) return i != 0;
            return true; // bare switch treated as true
        }
    }

    private static void LogWithLocalTimestamp(string message)
    {
        Console.WriteLine($"[{DateTime.Now:O}] {message}");
    }

    private static void ErrorWithLocalTimestamp(string message)
    {
        Console.Error.WriteLine($"[{DateTime.Now:O}] {message}");
    }
}
