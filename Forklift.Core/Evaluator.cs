using System;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Evaluator
    {
        private static readonly int[] TypeValues = { 0, 100, 320, 330, 500, 900, 0 };

        public static int Evaluate(Board board)
        {
            int score = 0;
            for (int sq88 = 0; sq88 < 128; sq88++)
            {
                if (Squares.IsOffboard((UnsafeSquare0x88)sq88)) continue;
                var piece = (Piece)board.At(new Square0x88(sq88));
                if (piece.Equals(Piece.Empty))
                {
                    continue;
                }

                score += (piece.IsWhite ? +1 : -1) * TypeValues[piece.TypeIndex];
            }
            return score;
        }
    }
}
