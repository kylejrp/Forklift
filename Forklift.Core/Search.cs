using System;
using System.Collections.Generic;
using System.Threading;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Search
    {
        public readonly record struct SearchSummary(Board.Move? BestMove, int BestScore, int CompletedDepth);

        private readonly record struct SearchNodeResult(Board.Move? BestMove, int BestScore, bool IsComplete);

        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue; // No overflow risk when negating

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

                var result = Negamax(board, depth, MinimumScore, MaximumScore, pvMove, cancellationToken);

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
            Board.Move? preferredMove = null,
            CancellationToken cancellationToken = default)
        {
            if (depth == 0)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), true);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), false);
            }

            var legalMoves = board.GenerateLegal();

            if (legalMoves.Length == 0)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), true);
            }

            bool didPvMoveOrdering = false;
            if (preferredMove is Board.Move pm)
            {
                int index = Array.FindIndex(legalMoves, m => m.Equals(pm));
                if (index > 0)
                {
                    (legalMoves[0], legalMoves[index]) = (legalMoves[index], legalMoves[0]); // Swap to front
                }
                didPvMoveOrdering = true;
            }

            Board.Move? bestMove = null;
            int bestScore = MinimumScore;

            bool sawCompleteChild = false;
            bool aborted = false;

            for (int i = 0; i < legalMoves.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var move = legalMoves[i];

                var undo = board.MakeMove(move);
                var childResult = Negamax(board, depth - 1, -beta, -alpha, preferredMove: null, cancellationToken: cancellationToken);
                board.UnmakeMove(move, undo);

                if (!childResult.IsComplete)
                {
                    // Child aborted: treat this node as aborted too
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
                    // Beta cutoff: this node is still "complete enough"
                    break;
                }
            }

            bool completed;
            if (didPvMoveOrdering)
            {
                // Root (or PV-ordered) node: as long as we saw at least one complete child,
                // we consider this iteration "good enough" to use at the root.
                completed = sawCompleteChild;
            }
            else
            {
                // Internal node: must not have aborted, and must have at least one complete child.
                completed = sawCompleteChild && !aborted;
            }

            if (bestMove is null)
            {
                // No move chosen (e.g., all children aborted) – fall back to static eval
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), completed);
            }

            return new SearchNodeResult(bestMove, bestScore, completed);
        }
    }
}
