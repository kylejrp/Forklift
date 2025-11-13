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
        lock (searchLock)
        {
            // Cancel any existing search
            currentSearchCancellationTokenSource?.Cancel();

            // Snapshot the position for this search so later 'position" commands don't interfere
            var boardSnapshot = board.Copy();

            var cancellationTokenSource = new CancellationTokenSource();
            currentSearchCancellationTokenSource = cancellationTokenSource;
            var cancellationToken = cancellationTokenSource.Token;

            int searchDepth = 2;

            currentSearchTask = Task.Run(() =>
            {
                try
                {
                    var (bestMove, bestScore) = Search.FindBestMove(boardSnapshot, searchDepth, cancellationToken);

                    // If this search was cancelled, we can bail silently
                    if (cancellationToken.IsCancellationRequested || bestMove is not Board.Move move)
                    {
                        // don't send a bestmove if cancelled
                        return;
                    }

                    Console.WriteLine($"info depth {searchDepth} score cp {bestScore} pv {move.ToUCIString()}");
                    Console.WriteLine($"bestmove {move.ToUCIString()}");

                }
                catch (OperationCanceledException)
                {
                    // Search stopped due to cancellation; do nothing.
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
