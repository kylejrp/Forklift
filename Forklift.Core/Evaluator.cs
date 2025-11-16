using System;
using System.Diagnostics;
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
            for (int sq88 = 0; sq88 < 128; sq88++)
            {
                if (Squares.IsOffboard((UnsafeSquare0x88)sq88)) continue;
                var piece = board.At(new Square0x88(sq88));
                if (piece == Piece.Empty) continue;

                int delta = TypeValues[piece.TypeIndex];

                // simple positional term:
                // bonus for any pawns on e4/d4/e5/d5
                var safeSq88 = (Square0x88)sq88;
                var file = safeSq88.File;
                var rank = safeSq88.Rank;

                if ((Piece.PieceType)piece.TypeIndex == Piece.PieceType.Pawn)
                {
                    // White center pawns
                    if (piece.IsWhite && (file == 3 || file == 4) && (rank == 3 || rank == 4))
                        delta += 10;

                    // Black center pawns
                    if (!piece.IsWhite && (file == 3 || file == 4) && (rank == 3 || rank == 4))
                        delta += 10; // still positive; sign comes from IsWhite below
                }

                score += (piece.IsWhite ? +1 : -1) * delta;
            }

            Debug.Assert(score >= -MaxEvaluationScore && score <= MaxEvaluationScore, $"Evaluation score of {score} out of bounds of ±{MaxEvaluationScore} for position {board.GetFEN()}.");

            return score;
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
