using System;
using System.Threading;

namespace Forklift.Core
{
    public static class Search
    {
        public readonly record struct SearchSummary(Board.Move? BestMove, int BestScore, int CompletedDepth);

        private readonly record struct SearchNodeResult(Board.Move? BestMove, int BestScore, bool IsComplete);

        private readonly record struct QuiescenceResult(int BestScore, bool IsComplete);

        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue;     // No overflow risk when negating

        // Keep mate scores consistent with the TT.
        private const int MateScore = TranspositionTable.MateValue;

        private static readonly TranspositionTable _transpositionTable = new();

        public static void ClearTranspositionTable() => _transpositionTable.Clear();

        // Negamax search, returns best move and score
        public static SearchSummary FindBestMove(Board board, int maxDepth, CancellationToken cancellationToken = default)
        {
            Board.Move? finalBestMove = null;
            int finalBestScore = MinimumScore;
            int completedDepth = 0;

            Board.Move? pvMove = null;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var result = Negamax(board, depth, MinimumScore, MaximumScore, ply: 0, preferredMove: pvMove, cancellationToken);

                if (!result.IsComplete)
                {
                    // Iteration was cut short (time / cancellation) – don’t
                    // trust this or deeper iterations.
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
            }

            if (finalBestMove is null)
            {
                var panicMoves = board.GenerateLegal();
                if (panicMoves.Length > 0)
                {
                    finalBestMove = panicMoves[0];
                    finalBestScore = Evaluator.EvaluateForSideToMove(board);
                }
            }

            return new SearchSummary(finalBestMove, finalBestScore, completedDepth);
        }

        private static SearchNodeResult Negamax(
            Board board,
            int depth,
            int alpha,
            int beta,
            int ply,
            Board.Move? preferredMove,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), false);
            }

            if (depth == 0)
            {
                // Leaf of the main search: dive into quiescence.
                QuiescenceResult q = Quiescence(board, alpha, beta, cancellationToken);
                return new SearchNodeResult(null, q.BestScore, q.IsComplete);
            }

            int alphaOriginal = alpha;

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
                return new SearchNodeResult(ttMove, probe.Score, true);
            }

            var legalMoves = board.GenerateLegal();
            int legalMoveCount = legalMoves.Length;

            if (legalMoveCount == 0)
            {
                // Terminal node: mate or stalemate.
                int terminalScore = EvaluateTerminal(board, ply);
                return new SearchNodeResult(null, terminalScore, true);
            }

            // --- Move ordering helpers ---------------------------------------
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

            // 1. TT move first (if any).
            if (ttMove is Board.Move transpositionMove)
            {
                PromoteMove(legalMoves, transpositionMove, targetIndex: 0, moveCount: legalMoveCount);
            }

            // 2. PV move next (if distinct from TT move).
            if (preferredMove is Board.Move pm)
            {
                bool sameAsTt = ttMove is Board.Move ttm && ttm.Equals(pm);
                int targetIndex = sameAsTt ? 0 : (ttMove is Board.Move ? 1 : 0);
                PromoteMove(legalMoves, pm, targetIndex, legalMoveCount);
            }

            Board.Move? bestMove = null;
            int bestScore = MinimumScore;

            bool sawCompleteChild = false;
            bool aborted = false;
            bool betaCutoff = false;

            for (int i = 0; i < legalMoves.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var move = legalMoves[i];

                var undo = board.MakeMove(move);
                var childResult = Negamax(board, depth - 1, -beta, -alpha, ply + 1, preferredMove: null, cancellationToken: cancellationToken);
                board.UnmakeMove(move, undo);

                if (!childResult.IsComplete)
                {
                    // Child aborted: treat this node as aborted too.
                    aborted = true;
                    break;
                }

                sawCompleteChild = true;

                int score = -childResult.BestScore;

                if (score > bestScore || bestMove is null)
                {
                    bestScore = score;
                    bestMove = move;
                }

                if (score > alpha)
                {
                    alpha = score;
                }

                if (alpha >= beta)
                {
                    // Beta cutoff: no need to consider remaining moves.
                    betaCutoff = true;
                    break;
                }
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
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), completed);
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

            return new SearchNodeResult(bestMove, bestScore, completed);
        }

        private static QuiescenceResult Quiescence(Board board, int alpha, int beta, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new QuiescenceResult(alpha, false);
            }

            bool inCheck = board.InCheck(board.SideToMove);
            if (inCheck)
            {
                Span<Board.Move> moveBuffer = stackalloc Board.Move[Board.MoveBufferMax];
                var legalMoves = board.GenerateLegal(moveBuffer);

                int currentAlpha = alpha;
                bool exploredMove = false;
                bool allComplete = true;

                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new QuiescenceResult(exploredMove ? currentAlpha : alpha, false);
                    }

                    var undo = board.MakeMove(move);
                    QuiescenceResult child = Quiescence(board, -beta, -currentAlpha, cancellationToken);
                    int score = -child.BestScore;
                    board.UnmakeMove(move, undo);

                    exploredMove = true;
                    if (!child.IsComplete)
                    {
                        allComplete = false;
                    }

                    if (score >= beta)
                    {
                        return new QuiescenceResult(beta, allComplete);
                    }

                    if (score > currentAlpha)
                    {
                        currentAlpha = score;
                    }
                }

                if (!exploredMove)
                {
                    // No legal moves in check => mate.
                    return new QuiescenceResult(EvaluateTerminal(board, ply: 0), true);
                }

                return new QuiescenceResult(currentAlpha, allComplete);
            }

            int standPat = Evaluator.EvaluateForSideToMove(board);

            if (standPat >= beta)
            {
                // Fail-high on stand pat is a normal, complete result.
                return new QuiescenceResult(beta, true);
            }

            if (standPat > alpha)
            {
                alpha = standPat;
            }

            Span<Board.Move> pseudoBuffer = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(pseudoBuffer);
            var pseudoMoves = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);
            var sideToMove = board.SideToMove;
            bool exploredCapture = false;
            bool allCompleteCaptures = true;

            foreach (var move in pseudoMoves)
            {
                if (!move.IsCapture)
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new QuiescenceResult(alpha, false);
                }

                var undo = board.MakeMove(move);
                bool leavesKingInCheck = board.InCheck(sideToMove);
                if (leavesKingInCheck)
                {
                    board.UnmakeMove(move, undo);
                    continue;
                }

                exploredCapture = true;
                QuiescenceResult child = Quiescence(board, -beta, -alpha, cancellationToken);
                int score = -child.BestScore;
                board.UnmakeMove(move, undo);

                if (!child.IsComplete)
                {
                    allCompleteCaptures = false;
                }

                if (score >= beta)
                {
                    return new QuiescenceResult(beta, allCompleteCaptures);
                }

                if (score > alpha)
                {
                    alpha = score;
                }
            }

            if (!exploredCapture && !board.HasAnyLegalMoves())
            {
                // No captures searched and no legal moves at all => stalemate or mate.
                return new QuiescenceResult(EvaluateTerminal(board, ply: 0), true);
            }

            return new QuiescenceResult(alpha, allCompleteCaptures);
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
    }
}
