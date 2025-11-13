using System.Diagnostics;
using Forklift.Core;

var board = new Board();
Console.OutputEncoding = System.Text.Encoding.UTF8;
CancellationTokenSource? currentSearchCancellationTokenSource = null;
Task? currentSearchTask = null;
object searchLock = new();

// UCI engine options
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    { "Hash", "16" },
    { "Threads", "1" },
    { "OwnBook", "false" }
};
bool debugMode = false;

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;
    line = line.Trim();
    if (line == "uci")
    {
        Console.WriteLine("id name Forklift");
        Console.WriteLine("id author kylejrp");
        Console.WriteLine("option name Hash type spin default 16 min 1 max 1024");
        Console.WriteLine("option name Threads type spin default 1 min 1 max 16");
        Console.WriteLine("option name OwnBook type check default false");
        Console.WriteLine("uciok");
    }
    else if (line == "isready")
    {
        Console.WriteLine("readyok");
    }
    else if (line.StartsWith("setoption "))
    {
        // setoption name <name> [value <value>]
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int nameIdx = Array.IndexOf(tokens, "name");
        int valueIdx = Array.IndexOf(tokens, "value");
        if (nameIdx > 0 && nameIdx + 1 < tokens.Length)
        {
            var name = tokens[nameIdx + 1];
            var value = (valueIdx > 0 && valueIdx + 1 < tokens.Length) ? tokens[valueIdx + 1] : "true";
            options[name] = value;
            if (debugMode) Console.WriteLine($"info string setoption {name} = {value}");
        }
    }
    else if (line == "ucinewgame")
    {
        board.SetStartPosition();
        if (debugMode) Console.WriteLine("info string ucinewgame called");
    }
    else if (line.StartsWith("debug"))
    {
        debugMode = line.EndsWith("on");
        Console.WriteLine($"info string debug {(debugMode ? "on" : "off")}");
    }
    else if (line.StartsWith("position"))
    {
        // position [fen <fenstring>] | startpos [moves ...]
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int movesIdx = Array.IndexOf(tokens, "moves");
        if (tokens.Length >= 2 && tokens[1] == "startpos")
        {
            board.SetStartPosition();
            if (movesIdx > 0)
            {
                for (int i = movesIdx + 1; i < tokens.Length; i++)
                {
                    board.TryApplyUCIMove(tokens[i]);
                }
            }
        }
        else if (tokens.Length >= 3 && tokens[1] == "fen")
        {
            var fenParts = new List<string>();
            int idx = 2;
            while (idx < tokens.Length && tokens[idx] != "moves")
            {
                fenParts.Add(tokens[idx]);
                idx++;
            }
            var fen = string.Join(" ", fenParts);
            board.SetPositionFromFEN(fen);
            if (idx < tokens.Length && tokens[idx] == "moves")
            {
                for (int i = idx + 1; i < tokens.Length; i++)
                {
                    board.TryApplyUCIMove(tokens[i]);
                }
            }
        }
    }
    else if (line.StartsWith("go"))
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int? depth = null;
        int? moveTimeMs = null;

        for (int i = 1; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "depth" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var d):
                    depth = d;
                    i++; // skip value
                    break;

                case "movetime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var mt):
                    moveTimeMs = mt;
                    i++; // skip value
                    break;
            }
        }

        depth ??= 5; // default depth

        lock (searchLock)
        {
            // Cancel any existing search
            currentSearchCancellationTokenSource?.Cancel();

            // Snapshot the position for this search so later 'position" commands don't interfere
            var boardSnapshot = board.Copy();

            var cancellationTokenSource = new CancellationTokenSource();

            if (moveTimeMs.HasValue && moveTimeMs.Value > 0)
            {
                cancellationTokenSource.CancelAfter(moveTimeMs.Value);
            }

            currentSearchCancellationTokenSource = cancellationTokenSource;
            var cancellationToken = cancellationTokenSource.Token;


            currentSearchTask = Task.Run(() =>
            {
                try
                {
                    if (debugMode) Console.WriteLine("info string search started");
                    var stopwatch = Stopwatch.StartNew();

                    var (bestMove, bestScore) = Search.FindBestMove(boardSnapshot, depth.Value, cancellationToken);

                    // If this search was cancelled, we can bail silently
                    if (bestMove is not Board.Move move)
                    {
                        Console.WriteLine("bestmove 0000");
                        return;
                    }

                    Console.WriteLine($"info depth {depth.Value} score cp {bestScore} pv {move.ToUCIString()}");
                    var elapsedMs = stopwatch.ElapsedMilliseconds;
                    if (debugMode)
                    {
                        Console.WriteLine($"info string search completed in {elapsedMs / 1000.0:F2}s");
                    }
                    Console.WriteLine($"bestmove {move.ToUCIString()}");

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"info string search error: {ex.Message}");
                    Console.WriteLine("bestmove 0000");
                }
            });
        }
    }
    else if (line == "stop")
    {
        if (debugMode) Console.WriteLine("info string stop called");
        lock (searchLock)
        {
            currentSearchCancellationTokenSource?.Cancel();
        }
        currentSearchTask?.Wait();
        currentSearchCancellationTokenSource = null;
        currentSearchTask = null;
    }
    else if (line == "quit")
    {
        lock (searchLock)
        {
            currentSearchCancellationTokenSource?.Cancel();
        }
        currentSearchTask?.Wait(100);
        break;
    }
}
