using System.Diagnostics;
using Forklift.Core;

const int DefaultSearchDepth = 8;
const int MaxSearchDepthForTimedMode = 16;
const int SafetyMarginMs = 25;

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
        int? whiteTimeMs = null;
        int? blackTimeMs = null;
        int? whiteIncrementMs = null;
        int? blackIncrementMs = null;

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

                case "wtime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var wt):
                    whiteTimeMs = wt;
                    i++;
                    break;

                case "btime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var bt):
                    blackTimeMs = bt;
                    i++;
                    break;

                case "winc" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var wi):
                    whiteIncrementMs = wi;
                    i++;
                    break;

                case "binc" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var bi):
                    blackIncrementMs = bi;
                    i++;
                    break;
            }
        }

        lock (searchLock)
        {
            // Cancel any existing search
            currentSearchCancellationTokenSource?.Cancel();

            // Snapshot the position for this search so later 'position" commands don't interfere
            var boardSnapshot = board.Copy();

            var cancellationTokenSource = new CancellationTokenSource();
            var sideToMove = boardSnapshot.SideToMove;
            int? allocatedTimeMs = ComputeTimeBudget(sideToMove, moveTimeMs, whiteTimeMs, blackTimeMs, whiteIncrementMs, blackIncrementMs, debugMode);
            bool useFailSafeDepth = allocatedTimeMs.HasValue && allocatedTimeMs.Value <= 0;

            int searchDepth;
            if (depth.HasValue)
            {
                searchDepth = depth.Value;
            }
            else if (allocatedTimeMs.HasValue)
            {
                searchDepth = MaxSearchDepthForTimedMode;
            }
            else
            {
                searchDepth = DefaultSearchDepth;
            }

            if (allocatedTimeMs.HasValue && allocatedTimeMs.Value > 0)
            {
                cancellationTokenSource.CancelAfter(allocatedTimeMs.Value - SafetyMarginMs);
            }

            currentSearchCancellationTokenSource = cancellationTokenSource;
            var cancellationToken = cancellationTokenSource.Token;


            currentSearchTask = Task.Run(() =>
            {
                try
                {
                    if (debugMode) Console.WriteLine("info string search started");
                    var stopwatch = Stopwatch.StartNew();
                    var sufficientTimeToAttemptDepth = allocatedTimeMs.HasValue
                        ? new Func<bool>(() => stopwatch.ElapsedMilliseconds < allocatedTimeMs.Value - SafetyMarginMs) // TODO: better time management here
                        : null;
                    var summary = Search.FindBestMove(boardSnapshot, useFailSafeDepth ? 1 : searchDepth, sufficientTimeToAttemptDepth, cancellationToken);
                    var bestMove = summary.BestMove;
                    var bestScore = summary.BestScore;
                    var completedDepth = summary.CompletedDepth;

                    // If this search was cancelled, we can bail silently
                    if (bestMove is not Board.Move move)
                    {
                        Console.WriteLine("bestmove (none)");
                        return;
                    }

                    Console.WriteLine($"info depth {completedDepth} score cp {bestScore} pv {move.ToUCIString()}");
                    var elapsedMs = stopwatch.ElapsedMilliseconds;
                    if (debugMode)
                    {
                        var budgetDisplay = allocatedTimeMs.HasValue ? $" (budget {allocatedTimeMs.Value})" : string.Empty;
                        Console.WriteLine($"info string search completed in {elapsedMs / 1000.0:F2}s{budgetDisplay}");
                    }
                    Console.WriteLine($"bestmove {move.ToUCIString()}");

                }
                catch (Exception ex)
                {
                    var sanitizedMessage = ex.Message.Replace("\r", "").Replace("\n", "\\n");
                    Console.WriteLine($"info string search error: {sanitizedMessage}");
                    Console.WriteLine("bestmove (none)");
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

static int? ComputeTimeBudget(Color sideToMove, int? moveTimeMs, int? whiteTimeMs, int? blackTimeMs, int? whiteIncrementMs, int? blackIncrementMs, bool debugMode)
{
    const double MaxFractionOfRemaining = 0.4;
    const int MinMoveTimeMs = 10;
    const int DivisorForRemaining = 30;

    if (debugMode)
    {
        Console.WriteLine(
            $"info string go inputs side={sideToMove} movetime={moveTimeMs?.ToString() ?? "-"} wtime={whiteTimeMs?.ToString() ?? "-"} " +
            $"btime={blackTimeMs?.ToString() ?? "-"} winc={whiteIncrementMs?.ToString() ?? "-"} binc={blackIncrementMs?.ToString() ?? "-"}");
    }

    if (moveTimeMs.HasValue)
    {
        int clamped = Math.Max(moveTimeMs.Value, 0);
        if (debugMode)
        {
            Console.WriteLine($"info string allocated via movetime: {clamped}ms");
        }

        return clamped;
    }

    int? remainingTimeMs = sideToMove == Color.White ? whiteTimeMs : blackTimeMs;
    int? incrementMs = sideToMove == Color.White ? whiteIncrementMs : blackIncrementMs;

    if (remainingTimeMs.HasValue && remainingTimeMs.Value <= 0)
    {
        if (debugMode)
        {
            Console.WriteLine("info string remaining time exhausted; allocating 0ms");
        }

        return 0;
    }

    if (!remainingTimeMs.HasValue && !incrementMs.HasValue)
    {
        if (debugMode)
        {
            Console.WriteLine("info string no time control; unlimited time");
        }

        return null;
    }

    double allocation = 0.0;

    if (remainingTimeMs.HasValue)
        allocation += remainingTimeMs!.Value / 20.0;

    if (incrementMs.HasValue)
        allocation += incrementMs!.Value / 2.0;

    int allocatedMs = (int)Math.Round(allocation, MidpointRounding.AwayFromZero);

    if (debugMode)
    {
        string remainingDisplay = remainingTimeMs?.ToString() ?? "-";
        string incrementDisplay = incrementMs?.ToString() ?? "-";
        Console.WriteLine($"info string allocated time: {allocatedMs}ms (remaining={remainingDisplay}, increment={incrementDisplay})");
    }

    return allocatedMs;
}
