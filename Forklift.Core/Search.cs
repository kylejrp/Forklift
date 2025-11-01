using System;
using System.Collections.Generic;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Search
    {
        // Minimax search, returns best move and score
        public static (Board.Move? bestMove, int bestScore) FindBestMove(Board board, int depth)
        {
            var legalMoves = board.GenerateLegal();
            Board.Move? bestMove = null;
            bool whiteToMove = board.SideToMove == Color.White;
            int bestScore = whiteToMove ? int.MinValue : int.MaxValue;

            foreach (var move in legalMoves)
            {
                var undo = board.MakeMove(move);
                int score = Minimax(board, depth - 1, !whiteToMove);
                board.UnmakeMove(move, undo);

                if (whiteToMove)
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
            return (bestMove, bestScore);
        }

        private static int Minimax(Board board, int depth, bool maximizingPlayer)
        {
            if (depth == 0)
                return Evaluator.Evaluate(board);

            var legalMoves = board.GenerateLegal();
            bool hasMoves = false;
            int bestScore = maximizingPlayer ? int.MinValue : int.MaxValue;
            foreach (var move in legalMoves)
            {
                hasMoves = true;
                var undo = board.MakeMove(move);
                int score = Minimax(board, depth - 1, !maximizingPlayer);
                board.UnmakeMove(move, undo);

                if (maximizingPlayer)
                    bestScore = Math.Max(bestScore, score);
                else
                    bestScore = Math.Min(bestScore, score);
            }
            return hasMoves ? bestScore : Evaluator.Evaluate(board);
        }
    }
}
