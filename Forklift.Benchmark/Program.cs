using Forklift.Core;

Console.WriteLine("Perft test starting...");
var b = BoardFactory.FromFenOrStart("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1");
var stats = Perft.Statistics(b, 4);
Console.WriteLine(stats.Nodes);
Console.WriteLine("Perft test completed.");
