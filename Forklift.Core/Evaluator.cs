using System;
using Forklift.Core;

namespace Forklift.Core
{
    public static class Evaluator
    {
        // Piece values: pawn=100, knight=320, bishop=330, rook=500, queen=900, king=0
        private static readonly int[] PieceValues = {
            0,    // Empty
            100,  // WhitePawn
            320,  // WhiteKnight
            330,  // WhiteBishop
            500,  // WhiteRook
            900,  // WhiteQueen
            0,    // WhiteKing
            100,  // BlackPawn
            320,  // BlackKnight
            330,  // BlackBishop
            500,  // BlackRook
            900,  // BlackQueen
            0     // BlackKing
        };

        public static int Evaluate(Board board)
        {
            int score = 0;
            for (int sq88 = 0; sq88 < 128; sq88++)
            {
                if (Squares.IsOffboard((UnsafeSquare0x88)sq88)) continue;
                var piece = (Piece)board.At(new Square0x88(sq88));
                score += PieceValues[(int)piece];
            }
            return score;
        }
    }
}
