using System;
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

        /// <summary>
        /// Static evaluation of the board position.
        /// </summary>
        /// <param name="board">The board to evaluate.</param>
        /// <returns>The static evaluation score of the board position, positive if favorable to White, negative if favorable to Black.</returns>
        public static int StaticEvaluate(Board board)
        {
            int score = 0;
            for (int sq88 = 0; sq88 < 128; sq88++)
            {
                if (Squares.IsOffboard((UnsafeSquare0x88)sq88)) continue;
                var piece = (Piece)board.At(new Square0x88(sq88));
                if (piece == Piece.Empty) continue;

                int delta = TypeValues[piece.TypeIndex];

                // simple positional term:
                // bonus for white pawn on e4/d4, penalty for black pawn on e5/d5
                var safeSq88 = (Square0x88)sq88;
                var file = safeSq88.File; // depends on your helpers
                var rank = safeSq88.Rank;

                if ((Piece.PieceType)piece.TypeIndex == Piece.PieceType.Pawn)
                {
                    // White center pawns
                    if (piece.IsWhite && (file == 3 || file == 4) && (rank == 3 || rank == 4 || rank == 5 || rank == 6))
                        delta += 10;

                    // Black center pawns
                    if (!piece.IsWhite && (file == 3 || file == 4) && (rank == 3 || rank == 4 || rank == 5 || rank == 6))
                        delta += 10; // still positive; sign comes from IsWhite below
                }

                score += (piece.IsWhite ? +1 : -1) * delta;
            }

            return score;
        }
    }
}
