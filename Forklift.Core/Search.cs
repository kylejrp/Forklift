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

            if (completedDepth == 0)
            {
                var panicMoves = board.GenerateLegal();
                if (panicMoves.Length > 0)
                {
                    finalBestMove = panicMoves[0];
                    finalBestScore = Evaluator.EvaluateForSideToMove(board);
                }
            }

            if (finalBestMove is null)
            {
                return new SearchSummary(null, Evaluator.EvaluateForSideToMove(board), completedDepth);
            }

            return new SearchSummary(finalBestMove, finalBestScore, completedDepth);
        }

        private static SearchNodeResult Negamax(Board board, int depth, int alpha, int beta, Board.Move? preferredMove = null, CancellationToken cancellationToken = default)
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

            if (preferredMove is Board.Move pm)
            {
                int index = Array.FindIndex(legalMoves, m => m.Equals(pm));
                if (index > 0)
                {
                    (legalMoves[0], legalMoves[index]) = (legalMoves[index], legalMoves[0]); // Swap to front
                }
            }

            Board.Move? bestMove = null;
            int bestScore = MinimumScore;
            bool completed = true;

            foreach (var move in legalMoves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completed = false;
                    break;
                }

                var undo = board.MakeMove(move);
                // Negamax recursion: score from child, then negate score
                var childResult = Negamax(board, depth - 1, -beta, -alpha, preferredMove: null, cancellationToken: cancellationToken);
                board.UnmakeMove(move, undo);

                if (!childResult.IsComplete)
                {
                    completed = false;
                    break;
                }

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
                    // Beta cutoff: no need to consider remaining moves
                    break;
                }
            }

            if (bestMove is null)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), completed);
            }

            return new SearchNodeResult(bestMove, bestScore, completed);
        }
    }
}
