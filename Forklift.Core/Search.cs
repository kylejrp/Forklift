using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public static class Search
    {
        public readonly record struct SearchSummary(Board.Move? BestMove, int BestScore, int CompletedDepth, int NodesSearched);

        private readonly record struct SearchNodeResult(Board.Move? BestMove, int BestScore, bool IsComplete, int NodesSearched);

        private readonly record struct QuiescenceResult(int BestScore, bool IsComplete, int NodesSearched);

        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue; // No overflow risk when negating

        private const int MateScore = TranspositionTable.MateValue;
        private const int NullMoveReduction = 2;
        private const int NullMoveMinDepth = 3;

        private static readonly TranspositionTable _transpositionTable = new();

        private const int MaxPly = 128;
        private static readonly Board.Move?[] _killerMovesPrimary = new Board.Move?[MaxPly];
        private static readonly Board.Move?[] _killerMovesSecondary = new Board.Move?[MaxPly];
        private static readonly HistoryTable _historyScores = new HistoryTable();

        private static readonly int[] _pieceOrderingValues = {
            100, // Pawn
            320, // Knight
            330, // Bishop
            500, // Rook
            900, // Queen
            2000 // King
        };

        public static void ClearTranspositionTable() => _transpositionTable.Clear();

        public static void ClearHeuristics()
        {
            Array.Clear(_killerMovesPrimary, 0, _killerMovesPrimary.Length);
            Array.Clear(_killerMovesSecondary, 0, _killerMovesSecondary.Length);
            _historyScores.Clear();
        }

        // Negamax search, returns best move and score
        public static SearchSummary FindBestMove(Board board, int maxDepth, CancellationToken cancellationToken = default, Action<SearchSummary>? summaryCallback = null)
        {
            ClearHeuristics();

            Board.Move? finalBestMove = null;
            int finalBestScore = MinimumScore;
            int completedDepth = 0;
            int totalNodesSearched = 0;

            Board.Move? pvMove = null;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var result = Negamax(
                    board: board,
                    depth: depth,
                    alpha: MinimumScore,
                    beta: MaximumScore,
                    ply: 0,
                    preferredMove: pvMove,
                    parentMoveWasNullMove: false,
                    cancellationToken: cancellationToken);

                totalNodesSearched += result.NodesSearched;

                if (!result.IsComplete)
                {
                    break;
                }

                completedDepth = depth;

                if (result.BestMove is not null)
                {
                    finalBestMove = result.BestMove;
                    finalBestScore = result.BestScore;
                    pvMove = result.BestMove;
                }
                else if (finalBestMove is null)
                {
                    finalBestScore = result.BestScore;
                }

                summaryCallback?.Invoke(new SearchSummary(finalBestMove, finalBestScore, completedDepth, totalNodesSearched));
            }

            if (finalBestMove is null)
            {
                Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
                board.GenerateLegal(ref moves);
                if (moves.Length > 0)
                {
                    finalBestMove = moves[0];
                    finalBestScore = Evaluator.EvaluateForSideToMove(board);
                    totalNodesSearched++;
                }
            }

            return new SearchSummary(finalBestMove, finalBestScore, completedDepth, totalNodesSearched);
        }

        private static SearchNodeResult Negamax(
            Board board,
            int depth,
            int alpha,
            int beta,
            int ply,
            Board.Move? preferredMove,
            bool parentMoveWasNullMove,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), false, 0);
            }

            if (depth == 0)
            {
                QuiescenceResult q = Quiescence(board, alpha, beta, ply + 1, cancellationToken);
                return new SearchNodeResult(null, q.BestScore, q.IsComplete, q.NodesSearched);
            }

            int alphaOriginal = alpha;
            int nodesSearched = 0;

            if (!parentMoveWasNullMove &&
                depth >= NullMoveMinDepth &&
                ply > 0 &&
                !board.InCheck(board.SideToMove) &&
                HasNonPawnMaterial(board))
            {
                int nullDepth = depth - 1 - NullMoveReduction;
                if (nullDepth < 0)
                {
                    nullDepth = 0;
                }

                var nullState = board.MakeNullMove();
                var nullChild = Negamax(
                    board: board,
                    depth: nullDepth,
                    alpha: -beta,
                    beta: -beta + 1,
                    ply: ply + 1,
                    preferredMove: null,
                    parentMoveWasNullMove: true,
                    cancellationToken: cancellationToken);
                board.UnmakeNullMove(nullState);
                nodesSearched += nullChild.NodesSearched;


                if (!nullChild.IsComplete)
                {
                    return new SearchNodeResult(null, nullChild.BestScore, false, nodesSearched);
                }

                int nullScore = -nullChild.BestScore;
                if (nullScore >= beta)
                {
                    _transpositionTable.Store(board.ZKey, depth, nullScore, TranspositionTable.NodeType.Beta, bestMove: null, ply);
                    return new SearchNodeResult(null, nullScore, true, nodesSearched);
                }
            }

            // --- Transposition table probe -----------------------------------
            //
            // We only trust stored scores when:
            // - The stored node was searched to at least this depth, and
            // - The node type (Exact / Alpha / Beta) is compatible with the
            //   current alpha/beta window.
            //
            // Exact  : full value for this position (can be returned directly).
            // Alpha  : upper bound  (score <= alpha when stored).
            // Beta   : lower bound  (score >= beta  when stored).
            //
            // At the root (ply == 0), we *never* return the TT score directly,
            // but we still use the stored best move for ordering – this keeps
            // the PV stable and avoids “playing from the table” at the root.
            var probe = _transpositionTable.Probe(board.ZKey, depth, alpha, beta, ply);
            Board.Move? ttMove = probe.BestMove;

            if (probe.HasScore && ply > 0)
            {
                // Trusted hit: use the stored value and best move.
                return new SearchNodeResult(ttMove, probe.Score, true, nodesSearched);
            }

            Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
            var picker = new MovePicker(
                board: board,
                moveBuffer: moves,
                history: _historyScores,
                pvMove: preferredMove,
                ttMove: ttMove,
                killer1: ply < MaxPly && _killerMovesPrimary[ply] is Board.Move killer1 && killer1.IsQuiet ? killer1 : null,
                killer2: ply < MaxPly && _killerMovesSecondary[ply] is Board.Move killer2 && killer2.IsQuiet ? killer2 : null
            );

            int legalMoveCount = 0;
            Board.Move? bestMove = null;
            int bestScore = MinimumScore;

            bool sawCompleteChild = false;
            bool aborted = false;
            bool betaCutoff = false;
            List<Board.Move> quietMovesSearched = new List<Board.Move>();

            while (picker.Next() is Board.Move move)
            {
                legalMoveCount++;

                // Check for cancellation at the top of the loop to ensure we
                // respond promptly even if the move generation is fast.
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                nodesSearched++;
                var undo = board.MakeMove(move);
                var childResult = Negamax(
                    board: board,
                    depth: depth - 1,
                    alpha: -beta,
                    beta: -alpha,
                    ply: ply + 1,
                    preferredMove: null,
                    parentMoveWasNullMove: false,
                    cancellationToken: cancellationToken);
                board.UnmakeMove(move, undo);

                nodesSearched += childResult.NodesSearched;

                if (!childResult.IsComplete)
                {
                    // Child aborted: treat this node as aborted too.
                    aborted = true;
                    break;
                }

                sawCompleteChild = true;

                int score = -childResult.BestScore;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;

                    if (score > alpha)
                    {
                        alpha = score;
                    }
                }

                if (score >= beta)
                {
                    // Beta cutoff: no need to consider remaining moves.
                    if (move.IsQuiet)
                    {
                        StoreKillerMove(move, ply);
                        var bonus = 300 * depth - 250;
                        UpdateHistory(move, bonus);
                        foreach (var quietMove in quietMovesSearched)
                        {
                            UpdateHistory(quietMove, -bonus);
                        }
                    }
                    betaCutoff = true;
                    break;
                }

                // do this quiet check after beta cutoff so we don't have to account for it there
                // when updating history with a negative bonus
                if (move.IsQuiet)
                {
                    quietMovesSearched.Add(move);
                }
            }

            if (legalMoveCount == 0)
            {
                // Terminal node: mate or stalemate.
                int terminalScore = EvaluateTerminal(board, ply);
                return new SearchNodeResult(null, terminalScore, true, nodesSearched);
            }

            bool completed;
            if (preferredMove is not null)
            {
                // Root (or PV-ordered) node: as long as we saw at least one
                // complete child, we consider this iteration usable at the root.
                completed = sawCompleteChild;
            }
            else
            {
                // Internal node: must not have aborted, and must have at least
                // one complete child.
                completed = sawCompleteChild && !aborted;
            }

            if (bestMove is null)
            {
                // No move chosen (e.g., all children aborted) – fall back to static eval.
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), completed, nodesSearched);
            }

            // --- Transposition table store -----------------------------------
            //
            // Only store if the search for this node actually completed (no
            // partial / cancelled results) and we weren’t cancelled here.
            if (completed && !cancellationToken.IsCancellationRequested)
            {
                var nodeType = TranspositionTable.NodeType.Exact;

                // If the search produced a beta cutoff, the score is a lower bound (Beta).
                if (betaCutoff)
                {
                    nodeType = TranspositionTable.NodeType.Beta;
                }
                // If the best score never exceeded the original alpha, it’s an upper bound (Alpha).
                else if (bestScore <= alphaOriginal)
                {
                    nodeType = TranspositionTable.NodeType.Alpha;
                }
                // Otherwise alphaOriginal < bestScore < beta: Exact value.

                _transpositionTable.Store(board.ZKey, depth, bestScore, nodeType, bestMove, ply);
            }

            return new SearchNodeResult(bestMove, bestScore, completed, nodesSearched);
        }

        private static QuiescenceResult Quiescence(Board board, int alpha, int beta, int ply, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new QuiescenceResult(alpha, false, 0);
            }

            bool inCheck = board.InCheck(board.SideToMove);
            int nodesSearched = 0;
            if (inCheck)
            {
                Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
                var picker = new MovePicker(
                    board: board,
                    moveBuffer: moves,
                    history: _historyScores
                );

                int currentAlpha = alpha;
                bool exploredMove = false;
                bool allComplete = true;

                while (picker.Next() is Board.Move move)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new QuiescenceResult(exploredMove ? currentAlpha : alpha, false, nodesSearched);
                    }

                    nodesSearched++;
                    var undo = board.MakeMove(move);
                    QuiescenceResult child = Quiescence(board, -beta, -currentAlpha, ply + 1, cancellationToken);
                    int score = -child.BestScore;
                    board.UnmakeMove(move, undo);
                    nodesSearched += child.NodesSearched;

                    exploredMove = true;
                    if (!child.IsComplete)
                    {
                        allComplete = false;
                    }

                    if (score >= beta)
                    {
                        return new QuiescenceResult(beta, allComplete, nodesSearched);
                    }

                    if (score > currentAlpha)
                    {
                        currentAlpha = score;
                    }
                }

                if (!exploredMove)
                {
                    // No legal moves in check => mate.
                    return new QuiescenceResult(EvaluateTerminal(board, ply), true, nodesSearched);
                }

                return new QuiescenceResult(currentAlpha, allComplete, nodesSearched);
            }

            int standPat = Evaluator.EvaluateForSideToMove(board);

            if (standPat >= beta)
            {
                // Fail-high on stand pat is a normal, complete result.
                return new QuiescenceResult(beta, true, nodesSearched);
            }

            if (standPat > alpha)
            {
                alpha = standPat;
            }

            Span<Board.Move> pseudoMoves = stackalloc Board.Move[Board.MoveBufferMax];
            var pseudoPicker = new MovePicker(
                board: board,
                moveBuffer: pseudoMoves,
                history: _historyScores,
                moveGenerationStrategy: MovePicker.MoveGenerationStrategy.PseudoLegal
            );
            var sideToMove = board.SideToMove;
            bool exploredCapture = false;
            bool allCompleteCaptures = true;

            while (pseudoPicker.Next() is Board.Move move)
            {
                if (move.IsQuiet)
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new QuiescenceResult(alpha, false, nodesSearched);
                }

                nodesSearched++;
                var undo = board.MakeMove(move);
                bool leavesKingInCheck = board.InCheck(sideToMove);
                if (leavesKingInCheck)
                {
                    board.UnmakeMove(move, undo);
                    continue;
                }

                exploredCapture = true;
                QuiescenceResult child = Quiescence(board, -beta, -alpha, ply + 1, cancellationToken);
                int score = -child.BestScore;
                board.UnmakeMove(move, undo);
                nodesSearched += child.NodesSearched;

                if (!child.IsComplete)
                {
                    allCompleteCaptures = false;
                }

                if (score >= beta)
                {
                    return new QuiescenceResult(beta, allCompleteCaptures, nodesSearched);
                }

                if (score > alpha)
                {
                    alpha = score;
                }
            }

            if (!exploredCapture && !board.HasAnyLegalMoves())
            {
                // No captures searched and no legal moves at all => stalemate or mate.
                return new QuiescenceResult(EvaluateTerminal(board, ply), true, nodesSearched);
            }

            return new QuiescenceResult(alpha, allCompleteCaptures, nodesSearched);
        }

        private static int EvaluateTerminal(Board board, int ply)
        {
            bool sideToMoveInCheck = board.InCheck(board.SideToMove);
            if (sideToMoveInCheck)
            {
                // Mate scores are offset by ply so they can be normalized to
                // “mate in N” later, consistent with the TT helpers.
                return -MateScore + ply;
            }

            // Stalemate (or other terminal draw) – 0 from side-to-move POV.
            return 0;
        }

        private static void StoreKillerMove(Board.Move move, int ply)
        {
            if (ply >= MaxPly)
            {
                return;
            }

            if (_killerMovesPrimary[ply] is Board.Move existing && existing.Equals(move))
            {
                return;
            }

            _killerMovesSecondary[ply] = _killerMovesPrimary[ply];
            _killerMovesPrimary[ply] = move;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateHistory(Board.Move move, int bonus)
        {
            _historyScores.Update(move, bonus);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetHistoryScore(Board.Move move)
        {
            return _historyScores.Get(move);
        }

        private static int ScoreCapture(Board.Move move)
        {
            int victimValue = _pieceOrderingValues[(int)move.Captured.Type];
            int attackerValue = _pieceOrderingValues[(int)move.Mover.Type];
            return victimValue * 10 - attackerValue;
        }
        private static void OrderCapturesByMvvLva(Span<Board.Move> moves, int startIndex, int moveCount)
        {
            for (int current = startIndex; current < moveCount; current++)
            {
                int bestIndex = -1;
                int bestScore = int.MinValue;

                for (int candidate = current; candidate < moveCount; candidate++)
                {
                    var move = moves[candidate];
                    if (!move.IsCapture)
                    {
                        continue;
                    }

                    int score = ScoreCapture(move);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = candidate;
                    }
                }

                if (bestIndex == -1)
                {
                    break;
                }

                if (bestIndex != current)
                {
                    (moves[current], moves[bestIndex]) = (moves[bestIndex], moves[current]);
                }
            }
        }

        private static void OrderQuietMovesByHistory(Span<Board.Move> moves, int startIndex, int moveCount)
        {
            for (int current = startIndex; current < moveCount; current++)
            {
                int bestIndex = -1;
                int bestScore = int.MinValue;

                for (int candidate = current; candidate < moveCount; candidate++)
                {
                    var move = moves[candidate];
                    if (!move.IsQuiet)
                    {
                        continue;
                    }

                    int score = GetHistoryScore(move);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = candidate;
                    }
                }

                if (bestIndex == -1)
                {
                    break;
                }

                if (bestIndex != current)
                {
                    (moves[current], moves[bestIndex]) = (moves[bestIndex], moves[current]);
                }
            }
        }

        private static bool HasNonPawnMaterial(Board board)
        {
            ulong whiteNonPawns =
                board.GetPieceBitboard(Piece.WhiteKnight) |
                board.GetPieceBitboard(Piece.WhiteBishop) |
                board.GetPieceBitboard(Piece.WhiteRook) |
                board.GetPieceBitboard(Piece.WhiteQueen);

            ulong blackNonPawns =
                board.GetPieceBitboard(Piece.BlackKnight) |
                board.GetPieceBitboard(Piece.BlackBishop) |
                board.GetPieceBitboard(Piece.BlackRook) |
                board.GetPieceBitboard(Piece.BlackQueen);

            return (whiteNonPawns | blackNonPawns) != 0;
        }
    }
}
