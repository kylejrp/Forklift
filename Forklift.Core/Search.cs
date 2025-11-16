using System;
using System.Threading;

namespace Forklift.Core
{
    public static class Search
    {
        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue; // No overflow risk when negating
        private static readonly TranspositionTable _transpositionTable = new();

        public static void ClearTranspositionTable()
        {
            _transpositionTable.Clear();
        }

        private static int MateValue => TranspositionTable.MateValue;

        // Negamax search, returns best move and score
        public static (Board.Move? bestMove, int bestScore) FindBestMove(Board board, int maxDepth, CancellationToken cancellationToken = default)
        {
            Board.Move? finalBestMove = null;
            int finalBestScore = MinimumScore;

            Board.Move? pvMove = null;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var (bestMove, bestScore) = Negamax(board, depth, MinimumScore, MaximumScore, ply: 0, preferredMove: pvMove, cancellationToken: cancellationToken);

                if (bestMove is not null)
                {
                    finalBestMove = bestMove;
                    finalBestScore = bestScore;
                    pvMove = bestMove;
                }
            }

            if (finalBestMove is null)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }

            return (finalBestMove, finalBestScore);
        }

        private static (Board.Move? bestMove, int bestScore) Negamax(Board board, int depth, int alpha, int beta, int ply, Board.Move? preferredMove, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }

            if (depth == 0)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }

            int alphaOriginal = alpha;

            // We only trust scores from the transposition table when they were
            // searched to at least the same depth. Shallower entries provide
            // move-ordering hints but may not be safe as bounds. NodeType
            // encodes whether the stored score is an exact value or a bound: an
            // Alpha node represents an upper bound (score <= alpha), Beta a
            // lower bound (score >= beta), and Exact a fully resolved score.
            var probe = _transpositionTable.Probe(board.ZKey, depth, alpha, beta, ply);
            Board.Move? ttMove = probe.BestMove;

            if (probe.HasScore && ply > 0)
            {
                return (ttMove, probe.Score);
            }

            var legalMoves = board.GenerateLegal();
            int legalMoveCount = legalMoves.Length;

            if (legalMoveCount == 0)
            {
                int terminalScore = board.InCheck(board.SideToMove) ? -MateValue + ply : 0;
                if (!cancellationToken.IsCancellationRequested)
                {
                    _transpositionTable.Store(board.ZKey, depth, terminalScore, TranspositionTable.NodeType.Exact, null, ply);
                }
                return (null, terminalScore);
            }

            static void PromoteMove(Board.Move[] moves, Board.Move move, int targetIndex, int moveCount)
            {
                if ((uint)targetIndex >= (uint)moveCount)
                {
                    return;
                }

                int index = Array.FindIndex(moves, targetIndex, moveCount - targetIndex, m => m.Equals(move));
                if (index > targetIndex)
                {
                    (moves[targetIndex], moves[index]) = (moves[index], moves[targetIndex]);
                }
            }

            if (ttMove is Board.Move transpositionMove)
            {
                PromoteMove(legalMoves, transpositionMove, targetIndex: 0, moveCount: legalMoveCount);
            }

            if (preferredMove is Board.Move pm)
            {
                bool sameAsTt = ttMove is Board.Move ttm && ttm.Equals(pm);
                int targetIndex = sameAsTt ? 0 : (ttMove is Board.Move ? 1 : 0);
                PromoteMove(legalMoves, pm, targetIndex, legalMoveCount);
            }

            Board.Move? bestMove = null;
            int bestScore = MinimumScore;
            bool anyMoveEvaluated = false;
            bool betaCutoff = false;

            foreach (var move in legalMoves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Soft stop: return best move so far
                    break;
                }

                var undo = board.MakeMove(move);
                // Negamax recursion: score from child, then negate score
                var (_, childScore) = Negamax(board, depth - 1, -beta, -alpha, ply + 1, null, cancellationToken);
                int score = -childScore;

                board.UnmakeMove(move, undo);

                if (!anyMoveEvaluated || score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;
                    anyMoveEvaluated = true;
                }

                if (score > alpha)
                {
                    alpha = score;
                }

                if (alpha >= beta)
                {
                    // Beta cutoff: no need to consider remaining moves
                    betaCutoff = true;
                    break;
                }
            }

            // Fallback: if we got cancelled before examining any move, just static eval
            if (!anyMoveEvaluated)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                var nodeType = TranspositionTable.NodeType.Exact;
                // If the search produced a beta cutoff, we stored a score
                // greater than or equal to beta, so this is a lower bound
                // (Beta node). Otherwise, if the best score never exceeded the
                // original alpha, we have an upper bound (Alpha node). Any
                // remaining case means alpha < score < beta and we record an
                // exact value.
                if (betaCutoff)
                {
                    nodeType = TranspositionTable.NodeType.Beta;
                }
                else if (bestScore <= alphaOriginal)
                {
                    nodeType = TranspositionTable.NodeType.Alpha;
                }

                _transpositionTable.Store(board.ZKey, depth, bestScore, nodeType, bestMove, ply);
            }

            return (bestMove, bestScore);
        }
    }
}
