using System;
using System.Diagnostics;
using System.Numerics;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Evaluator
    {
        private static readonly int[] TypeValues = {
            100, // Pawn
            320, // Knight
            330, // Bishop
            500, // Rook
            900, // Queen
            0    // King
        };

        public const int MaxEvaluationScore = 30_000; // Arbitrary large value representing a decisive advantage

        /// <summary>
        /// Static evaluation of the board position.
        /// </summary>
        /// <param name="board">The board to evaluate.</param>
        /// <returns>The static evaluation score of the board position, positive if favorable to White, negative if favorable to Black. Will always be between -<see cref="MaxEvaluationScore"/> and <see cref="MaxEvaluationScore"/>.</returns>
        public static int StaticEvaluate(Board board)
        {
            int score = 0;
            foreach (var piece in Piece.AllPieces)
            {
                var pieceBitboard = board.GetPieceBitboard(piece);
                score += BitOperations.PopCount(pieceBitboard) * TypeValues[piece.TypeIndex] * (piece.IsWhite ? 1 : -1);
                score += EvaluatePieceTypePositional(piece, pieceBitboard);
            }

            Debug.Assert(score >= -MaxEvaluationScore && score <= MaxEvaluationScore, $"Evaluation score of {score} out of bounds of ±{MaxEvaluationScore} for position {board.GetFEN()}.");

            return score;
        }

        private static int EvaluatePieceTypePositional(Piece piece, ulong pieceBitboard)
        {
            return piece.Type switch
            {
                Piece.PieceType.Pawn => EvaluatePawnPositions(pieceBitboard, piece.Color),
                _ => 0
            };
        }

        private static int EvaluatePawnPositions(ulong pawnBitboard, Color color)
        {
            const int bonus = 10;
            int score = 0;

            while (pawnBitboard != 0)
            {
                UnsafeSquare0x64 sq64 = (UnsafeSquare0x64)BitOperations.TrailingZeroCount(pawnBitboard);
                pawnBitboard &= pawnBitboard - 1; // Remove the least significant bit

                int file = sq64.File;
                int rank = sq64.Rank;

                if (color == Color.Black)
                {
                    rank = 7 - rank; // Mirror rank for black pawns
                }

                if ((file == 3 || file == 4) && (rank == 3 || rank == 4))
                {
                    score += bonus;
                }
            }
            return score * (color == Color.White ? 1 : -1);
        }

        /// <summary>
        /// Evaluates the board position from the perspective of the side to move.
        /// </summary>
        /// <param name="board">The board to evaluate.</param>
        /// <returns>The evaluation score from the perspective of the side to move, positive if favorable to the side to move, negative if unfavorable. Will always be between -<see cref="MaxEvaluationScore"/> and <see cref="MaxEvaluationScore"/>.</returns>
        public static int EvaluateForSideToMove(Board board)
        {
            int staticScore = StaticEvaluate(board);
            return board.SideToMove == Color.White ? staticScore : -staticScore;
        }
    }
}
