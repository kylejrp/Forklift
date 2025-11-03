using Forklift.Core;

Console.WriteLine("Perft test starting...");
var b = BoardFactory.FromFenOrStart("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1");
b.KeepTrackOfHistory = false;// in Forklift.Benchmark Program.cs
var sw = System.Diagnostics.Stopwatch.StartNew();
var stats = Perft.Statistics(b, 5, parallelRoot: true);
sw.Stop();

var secs = sw.Elapsed.TotalSeconds;
Console.WriteLine($"Nodes={stats.Nodes:N0}  Elapsed={sw.Elapsed.TotalMilliseconds:N0} ms  NPS={(stats.Nodes / secs):N0}");

Console.WriteLine(stats.Nodes);
Console.WriteLine("Perft test completed.");
