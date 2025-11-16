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
        private const int MateScore = 30000;

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
            if (cancellationToken.IsCancellationRequested)
            {
                return (null, Evaluator.EvaluateForSideToMove(board));
            }

            if (depth == 0)
            {
                int quiescenceScore = Quiescence(board, alpha, beta, cancellationToken);
                return (null, quiescenceScore);
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

        private static int Quiescence(Board board, int alpha, int beta, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return alpha;
            }

            bool inCheck = board.InCheck(board.SideToMove);
            if (inCheck)
            {
                Span<Board.Move> moveBuffer = stackalloc Board.Move[Board.MoveBufferMax];
                var legalMoves = board.GenerateLegal(moveBuffer);

                int currentAlpha = alpha;
                bool exploredMove = false;

                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return exploredMove ? currentAlpha : alpha;
                    }

                    var undo = board.MakeMove(move);
                    int score = -Quiescence(board, -beta, -currentAlpha, cancellationToken);
                    board.UnmakeMove(move, undo);

                    exploredMove = true;

                    if (score >= beta)
                    {
                        return beta;
                    }

                    if (score > currentAlpha)
                    {
                        currentAlpha = score;
                    }
                }

                if (!exploredMove)
                {
                    return EvaluateTerminal(board);
                }

                return currentAlpha;
            }

            int standPat = Evaluator.EvaluateForSideToMove(board);

            if (standPat >= beta)
            {
                return beta;
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

            foreach (var move in pseudoMoves)
            {
                if (!move.IsCapture)
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return alpha;
                }

                var undo = board.MakeMove(move);
                bool leavesKingInCheck = board.InCheck(sideToMove);
                if (leavesKingInCheck)
                {
                    board.UnmakeMove(move, undo);
                    continue;
                }

                exploredCapture = true;
                int score = -Quiescence(board, -beta, -alpha, cancellationToken);
                board.UnmakeMove(move, undo);

                if (score >= beta)
                {
                    return beta;
                }

                if (score > alpha)
                {
                    alpha = score;
                }
            }

            if (!exploredCapture && !board.HasAnyLegalMoves())
            {
                return EvaluateTerminal(board);
            }

            return alpha;
        }

        private static int EvaluateTerminal(Board board)
        {
            bool sideToMoveInCheck = board.InCheck(board.SideToMove);
            if (sideToMoveInCheck)
            {
                return -MateScore;
            }

            return 0;
        }
    }
}
