using System;
using System.Collections.Generic;
using System.Numerics;

namespace Forklift.Core
{
    /// <summary>
    /// Minimal helper for tests to grab a move without introducing shared state.
    /// </summary>
    public static class MovePicker
    {
        /// <summary>
        /// Returns the first pseudo-legal move (no legality filtering).
        /// Useful for simple make/unmake smoke tests.
        /// </summary>
        public static Board.Move FirstPseudoLegal(Board board)
        {
            var moves = new List<Board.Move>(64);
            MoveGeneration.GeneratePseudoLegal(board, moves, board.WhiteToMove);
            if (moves.Count == 0)
                throw new InvalidOperationException("No pseudo-legal moves available.");
            return moves[0];
        }

        /// <summary>
        /// Returns the first fully legal move (king not left in check).
        /// Uses local list; no static buffers, so it is parallel-test safe.
        /// </summary>
        public static Board.Move FirstLegal(Board board)
        {
            var moves = new List<Board.Move>(64);
            MoveGeneration.GeneratePseudoLegal(board, moves, board.WhiteToMove);

            foreach (var mv in moves)
            {
                var undo = board.MakeMove(mv);

                // After MakeMove, board.WhiteToMove has flipped.
                // The side now to move is the opponent; check if OUR king is in check.
                bool ourKingInCheck = board.IsSquareAttacked(
                    targetSq88: FindKingSq88(board, white: !board.WhiteToMove),
                    byWhite: board.WhiteToMove);

                board.UnmakeMove(mv, undo);

                if (!ourKingInCheck)
                    return mv;
            }

            throw new InvalidOperationException("No legal moves available.");
        }

        /// <summary>
        /// Finds the king square (0x88) for the specified color using the board's bitboards.
        /// </summary>
        private static int FindKingSq88(Board board, bool white)
        {
            ulong bb = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (bb == 0)
                throw new InvalidOperationException("King bitboard is empty.");

            // If multiple bits somehow set, pick the LS1B — tests will still fail elsewhere appropriately.
            int s64 = BitOperations.TrailingZeroCount(bb);
            return Squares.ConvertTo0x88Index(s64);
        }
    }
}
