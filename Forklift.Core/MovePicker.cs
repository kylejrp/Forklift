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
            Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(moves);
            var span = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);
            if (span.Length == 0)
                throw new InvalidOperationException("No pseudo-legal moves available.");
            return span[0];
        }

        /// <summary>
        /// Returns the first fully legal move (king not left in check).
        /// </summary>
        public static Board.Move FirstLegal(Board board)
        {
            Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(moves);
            var span = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);
            foreach (var mv in span)
            {
                var undo = board.MakeMove(mv);
                bool ourKingInCheck = board.IsSquareAttacked(
                    t64: board.FindKingSq64(board.SideToMove.Flip()),
                    bySide: board.SideToMove);
                board.UnmakeMove(mv, undo);
                if (!ourKingInCheck)
                    return mv;
            }
            throw new InvalidOperationException("No legal moves available.");
        }
    }
}
