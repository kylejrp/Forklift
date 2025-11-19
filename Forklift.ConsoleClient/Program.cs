using System.Diagnostics;
using Forklift.Core;

const int DefaultSearchDepth = 8;
const int MaxSearchDepthForTimedMode = 20;
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

if (args.Length > 0 && args[0] == "bench")
{
    RunBenchmark();
    currentSearchTask?.Wait();
    Environment.Exit(0);
}

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var command = tokens[0];

    switch (command)
    {
        case "uci":
            HandleUci();
            break;

        case "isready":
            HandleIsReady();
            break;

        case "setoption":
            // setoption name <name> [value <value>]
            {
                int nameIdx = Array.IndexOf(tokens, "name");
                int valueIdx = Array.IndexOf(tokens, "value");

                if (nameIdx > 0 && nameIdx + 1 < tokens.Length)
                {
                    var name = tokens[nameIdx + 1];
                    var value = (valueIdx > 0 && valueIdx + 1 < tokens.Length)
                        ? tokens[valueIdx + 1]
                        : "true";

                    HandleSetOption(name, value);
                }
            }
            break;

        case "ucinewgame":
            HandleUciNewGame();
            break;

        case "debug":
            {
                // "debug on" / "debug off"
                bool enable = tokens.Length > 1 && tokens[^1].Equals("on", StringComparison.OrdinalIgnoreCase);
                HandleDebug(enable);
            }
            break;

        case "position":
            // position [fen <fenstring>] | startpos [moves ...]
            HandlePosition(tokens);
            break;

        case "go":
            {
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
                            i++;
                            break;

                        case "movetime" when i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var mt):
                            moveTimeMs = mt;
                            i++;
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

                HandleGo(depth, moveTimeMs, whiteTimeMs, blackTimeMs, whiteIncrementMs, blackIncrementMs);
            }
            break;

        case "bench":
            RunBenchmark();
            break;

        case "stop":
            HandleStop();
            break;

        case "quit":
            HandleQuit();
            break;

        default:
            Console.Error.WriteLine($"info string unknown command: {command}");
            break;
    }

    if (command == "quit")
    {
        break;
    }
}

// ===== Command handlers (local functions) =====

void HandleUci()
{
    Console.WriteLine("id name Forklift");
    Console.WriteLine("id author kylejrp");
    Console.WriteLine("option name Hash type spin default 1 min 1 max 1");
    Console.WriteLine("option name Threads type spin default 1 min 1 max 1");
    Console.WriteLine("uciok");
}

void HandleIsReady()
{
    Console.WriteLine("readyok");
}

void HandleSetOption(string name, string value)
{
    options[name] = value;
    if (debugMode) Console.WriteLine($"info string setoption {name} = {value}");
}

void HandleUciNewGame()
{
    board.SetStartPosition();
    Search.ClearTranspositionTable();
    if (debugMode) Console.WriteLine("info string ucinewgame called");
}

void HandleDebug(bool enable)
{
    debugMode = enable;
    Console.WriteLine($"info string debug {(debugMode ? "on" : "off")}");
}

void HandlePosition(string[] tokens)
{
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
    else
    {
        Console.Error.WriteLine("info string invalid position command");
    }
}

void HandleGo(
    int? depth,
    int? moveTimeMs,
    int? whiteTimeMs,
    int? blackTimeMs,
    int? whiteIncrementMs,
    int? blackIncrementMs)
{
    lock (searchLock)
    {
        // Cancel any existing search
        currentSearchCancellationTokenSource?.Cancel();

        // Snapshot the position for this search so later 'position' commands don't interfere
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
                var summary = Search.FindBestMove(boardSnapshot, useFailSafeDepth ? 1 : searchDepth, cancellationToken);
                var bestMove = summary.BestMove;
                var bestScore = summary.BestScore;
                var completedDepth = summary.CompletedDepth;

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

void HandleStop()
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

void HandleQuit()
{
    lock (searchLock)
    {
        currentSearchCancellationTokenSource?.Cancel();
    }
    currentSearchTask?.Wait(100);
}

void RunBenchmark()
{
    HandleUci();
    HandlePosition(new[] { "position", "startpos" });
    HandleGo(depth: 10, moveTimeMs: null, whiteTimeMs: null, blackTimeMs: null, whiteIncrementMs: null, blackIncrementMs: null);
    // TODO: do a comprehensive benchmark with deep positions
}

// ===== Helper methods =====

static int? ComputeTimeBudget(
    Color sideToMove,
    int? moveTimeMs,
    int? whiteTimeMs,
    int? blackTimeMs,
    int? whiteIncrementMs,
    int? blackIncrementMs,
    bool debugMode)
{
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
