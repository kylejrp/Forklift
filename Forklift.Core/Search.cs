using System;
using System.Collections.Generic;
using System.Threading;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Search
    {
        // Minimax search, returns best move and score
        public static (Board.Move? bestMove, int bestScore) FindBestMove(Board board, int depth, CancellationToken cancellationToken = default)
        {
            var legalMoves = board.GenerateLegal();
            Board.Move? bestMove = null;
            int bestScore = board.SideToMove == Color.White ? int.MinValue : int.MaxValue;

            foreach (var move in legalMoves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Soft stop: return what we've got so far
                    break;
                }

                var undo = board.MakeMove(move);
                int score = Minimax(board, depth - 1, cancellationToken);
                board.UnmakeMove(move, undo);

                if (board.SideToMove == Color.White)
                {
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMove = move;
                    }
                }
                else
                {
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestMove = move;
                    }
                }
            }

            if (bestMove == null)
            {
                foreach (var move in legalMoves)
                {
                    // just set the best move to something legal
                    bestMove = move;
                    bestScore = Evaluator.StaticEvaluate(board);
                    break;
                }

                // here, if the above loop didn't find a legal move, there's no legal move.
                // we're just going to probably return a null move and a score of either int.MinValue or int.MaxValue
                // 🙃
            }

            return (bestMove, bestScore);
        }

        private static int Minimax(Board board, int depth, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (depth == 0 || cancellationToken.IsCancellationRequested)
            {
                return Evaluator.StaticEvaluate(board);
            }

            var legalMoves = board.GenerateLegal();
            bool hasMoves = false;
            int bestScore = board.SideToMove == Color.White ? int.MinValue : int.MaxValue;

            foreach (var move in legalMoves)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Stop expanding deeper; return whatever best we have so far
                    break;
                }

                hasMoves = true;
                var undo = board.MakeMove(move);
                int score = Minimax(board, depth - 1, cancellationToken);
                board.UnmakeMove(move, undo);

                if (board.SideToMove == Color.White)
                    bestScore = Math.Max(bestScore, score);
                else
                    bestScore = Math.Min(bestScore, score);
            }
            return hasMoves ? bestScore : Evaluator.StaticEvaluate(board);
        }
    }
}
