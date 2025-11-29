using System.Diagnostics;
using Forklift.Core;
using static UciLogger;

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
    { "Hash", "1" },
    { "Threads", "1" },
};
bool debugMode = false;

if (args.Length > 0 && args[0] == "bench")
{
    RunBenchmark();
    HandleQuit().Wait();
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
    var arguments = tokens.Length > 1 ? tokens[1..] : [];

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
                int nameIdx = Array.IndexOf(arguments, "name");
                int valueIdx = Array.IndexOf(arguments, "value");

                if (nameIdx > 0 && nameIdx + 1 < arguments.Length)
                {
                    var name = arguments[nameIdx + 1];
                    var value = (valueIdx > 0 && valueIdx + 1 < arguments.Length)
                        ? arguments[valueIdx + 1]
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
                bool enable = arguments.Length > 0 && arguments[^1].Equals("on", StringComparison.OrdinalIgnoreCase);
                HandleDebug(enable);
            }
            break;

        case "position":
            // position [fen <fenstring>] | startpos [moves ...]
            HandlePosition(arguments);
            break;

        case "go":
            {
                int? depth = null;
                int? moveTimeMs = null;
                int? whiteTimeMs = null;
                int? blackTimeMs = null;
                int? whiteIncrementMs = null;
                int? blackIncrementMs = null;

                for (int i = 0; i < arguments.Length; i++)
                {
                    switch (arguments[i])
                    {
                        case "depth" when i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out var d):
                            depth = d;
                            i++;
                            break;

                        case "movetime" when i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out var mt):
                            moveTimeMs = mt;
                            i++;
                            break;

                        case "wtime" when i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out var wt):
                            whiteTimeMs = wt;
                            i++;
                            break;

                        case "btime" when i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out var bt):
                            blackTimeMs = bt;
                            i++;
                            break;

                        case "winc" when i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out var wi):
                            whiteIncrementMs = wi;
                            i++;
                            break;

                        case "binc" when i + 1 < arguments.Length && int.TryParse(arguments[i + 1], out var bi):
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
            await HandleQuit();
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
    TryLog("id name Forklift");
    TryLog("id author kylejrp");
    TryLog("option name Hash type spin default 1 min 1 max 1");
    TryLog("option name Threads type spin default 1 min 1 max 1");
    TryLog("uciok");
}

void HandleIsReady()
{
    TryLog("readyok");
}

void HandleSetOption(string name, string value)
{
    options[name] = value;
    if (debugMode) TryLog($"info string setoption {name} = {value}");
}

void HandleUciNewGame()
{
    board.SetStartPosition();
    Search.ClearTranspositionTable();
    if (debugMode) TryLog("info string ucinewgame called");
}

void HandleDebug(bool enable)
{
    debugMode = enable;
    TryLog($"info string debug {(debugMode ? "on" : "off")}");
}

void HandlePosition(string[] tokens)
{
    int movesIdx = Array.IndexOf(tokens, "moves");

    if (tokens.Length >= 1 && tokens[0] == "startpos")
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
    else if (tokens.Length >= 2 && tokens[0] == "fen")
    {
        var fenParts = new List<string>();
        int idx = 1;
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
        var boardSnapshot = board.Copy(keepTrackOfHistory: false);

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
                if (debugMode) TryLog("info string search started");
                var stopwatch = Stopwatch.StartNew();
                var summaryCallback = (Search.SearchSummary s) => { TryLog(new SearchInfo(s, stopwatch.Elapsed)); };
                var summary = Search.FindBestMove(boardSnapshot, useFailSafeDepth ? 1 : searchDepth, cancellationToken, summaryCallback);
                stopwatch.Stop();
                var bestMove = summary.PrincipalVariation.Length > 0 ? summary.PrincipalVariation[0] : null;
                var bestScore = summary.BestScore;
                var completedDepth = summary.CompletedDepth;

                var elapsedMs = stopwatch.ElapsedMilliseconds;
                if (debugMode)
                {
                    var budgetDisplay = allocatedTimeMs.HasValue ? $" (budget {allocatedTimeMs.Value})" : string.Empty;
                    TryLog($"info string search completed in {elapsedMs / 1000.0:F2}s{budgetDisplay}");
                }

                if (bestMove is not Board.Move move)
                {
                    TryLog("bestmove 0000");
                    return;
                }

                TryLog($"bestmove {move.ToUCIString()}");
            }
            catch (Exception ex)
            {
                var sanitizedMessage = ex.Message.Replace("\r", "").Replace("\n", "\\n");
                Console.Error.WriteLine($"info string search error: {sanitizedMessage}");
                TryLog("bestmove 0000");
#if DEBUG
                throw;
#endif
            }
        });
    }
}

void HandleStop()
{
    if (debugMode) TryLog("info string stop called");
    lock (searchLock)
    {
        currentSearchCancellationTokenSource?.Cancel();
    }
    currentSearchTask?.Wait();
    currentSearchCancellationTokenSource = null;
    currentSearchTask = null;
}

async Task HandleQuit()
{
    lock (searchLock)
    {
        currentSearchCancellationTokenSource?.Cancel();
    }
    currentSearchTask?.Wait(100);

    try
    {
        await UciLogger.FlushAndCompleteAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"info string error flushing UCI logger: {ex.Message.Replace("\r", "").Replace("\n", "\\n")}");
    }
}

void RunBenchmark()
{
    Stopwatch sw = new();
    sw.Start();
    HandleUci();
    HandlePosition(new[] { "startpos" });
    HandleGo(depth: 8, moveTimeMs: null, whiteTimeMs: null, blackTimeMs: null, whiteIncrementMs: null, blackIncrementMs: null);
    currentSearchTask?.Wait();

    HandleUciNewGame();
    HandlePosition(new[] { "fen", "1r3rk1/5npp/p6q/3p1b2/5N2/2P3P1/PP1NQP1b/2KR3R w - - 2 21" });
    HandleGo(depth: 8, moveTimeMs: null, whiteTimeMs: null, blackTimeMs: null, whiteIncrementMs: null, blackIncrementMs: null);
    currentSearchTask?.Wait();

    var elapsedMs = sw.ElapsedMilliseconds;
    TryLog($"info string benchmark completed in {elapsedMs / 1000.0:F2}s");

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
        TryLog(
            $"info string go inputs side={sideToMove} movetime={moveTimeMs?.ToString() ?? "-"} wtime={whiteTimeMs?.ToString() ?? "-"} " +
            $"btime={blackTimeMs?.ToString() ?? "-"} winc={whiteIncrementMs?.ToString() ?? "-"} binc={blackIncrementMs?.ToString() ?? "-"}");
    }

    if (moveTimeMs.HasValue)
    {
        int clamped = Math.Max(moveTimeMs.Value, 0);
        if (debugMode)
        {
            TryLog($"info string allocated via movetime: {clamped}ms");
        }

        return clamped;
    }

    int? remainingTimeMs = sideToMove == Color.White ? whiteTimeMs : blackTimeMs;
    int? incrementMs = sideToMove == Color.White ? whiteIncrementMs : blackIncrementMs;

    if (remainingTimeMs.HasValue && remainingTimeMs.Value <= 0)
    {
        if (debugMode)
        {
            TryLog("info string remaining time exhausted; allocating 0ms");
        }

        return 0;
    }

    if (!remainingTimeMs.HasValue && !incrementMs.HasValue)
    {
        if (debugMode)
        {
            TryLog("info string no time control; unlimited time");
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
        TryLog($"info string allocated time: {allocatedMs}ms (remaining={remainingDisplay}, increment={incrementDisplay})");
    }

    return allocatedMs;
}
