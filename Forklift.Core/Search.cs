using System;
using System.Collections.Generic;
using System.Threading;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Search
    {
        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue; // No overflow risk when negating

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


                var (bestMove, bestScore) = Negamax(board, depth, MinimumScore, MaximumScore, pvMove, cancellationToken);

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

        private static (Board.Move? bestMove, int bestScore) Negamax(Board board, int depth, int alpha, int beta, Board.Move? preferredMove = null, CancellationToken cancellationToken = default)
        {
            if (depth == 0 || cancellationToken.IsCancellationRequested)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }

            var legalMoves = board.GenerateLegal();

            if (legalMoves.Count() == 0)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
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

            foreach (var move in legalMoves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Soft stop: return best move so far
                    break;
                }

                var undo = board.MakeMove(move);
                // Negamax recursion: score from child, then negate score
                var (_, childScore) = Negamax(board, depth - 1, -beta, -alpha, preferredMove: null, cancellationToken: cancellationToken);
                int score = -childScore;

                board.UnmakeMove(move, undo);

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

            // Fallback: if we got cancelled before examining any move, just static eval
            if (bestMove is null)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }
            return (bestMove, bestScore);
        }
    }
}
