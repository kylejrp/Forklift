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
            var moves = MoveGeneration.GeneratePseudoLegal(board, board.SideToMove);
            if (moves.Length == 0)
                throw new InvalidOperationException("No pseudo-legal moves available.");
            return moves[0];
        }

        /// <summary>
        /// Returns the first fully legal move (king not left in check).
        /// </summary>
        public static Board.Move FirstLegal(Board board)
        {
            var moves = MoveGeneration.GeneratePseudoLegal(board, board.SideToMove);
            foreach (var mv in moves)
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
