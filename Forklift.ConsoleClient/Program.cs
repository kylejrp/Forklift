
using Forklift.Core;

var board = new Board();
Console.OutputEncoding = System.Text.Encoding.UTF8;


// UCI engine options
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    { "Hash", "16" },
    { "Threads", "1" },
    { "OwnBook", "false" }
};
bool debugMode = false;
bool stopSearch = false;

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
    else if (line == "stop")
    {
        stopSearch = true;
        if (debugMode) Console.WriteLine("info string stop called");
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
        stopSearch = false;

        // Use minimax search to find best move
        int searchDepth = 2; // You can adjust this for speed/strength
        var (bestMove, bestScore) = Search.FindBestMove(board, searchDepth);
        if (bestMove is Board.Move move)
        {
            string uci = Squares.ToAlgebraic(move.From88).ToString().ToLower() + Squares.ToAlgebraic(move.To88).ToString().ToLower();
            if (move.IsPromotion)
            {
                char promoChar = char.ToLower(Piece.ToFENChar(move.Promotion));
                uci += promoChar;
            }
            Console.WriteLine($"info depth {searchDepth} score cp {bestScore} pv {uci}");
            Console.WriteLine($"bestmove {uci}");
        }
        else
        {
            Console.WriteLine("bestmove (none)");
        }
    }
    else if (line == "quit")
    {
        break;
    }
}
