using System.Collections;
using Forklift.Core;

namespace ChessEngine.Core
{
    public static class Perft
    {
        public static long Count(Board board, int depth)
        {
            if (depth == 0) return 1;

            long nodes = 0;
            var moves = new List<Board.Move>(64);
            MoveGeneration.GeneratePseudoLegal(board, moves, board.WhiteToMove);

            foreach (var mv in moves)
            {
                var u = board.MakeMove(mv);
                // After MakeMove, side to move flipped; the side that just moved must NOT be in check
                bool legal = !board.InCheck(!board.WhiteToMove);
                if (legal)
                    nodes += Count(board, depth - 1);
                board.UnmakeMove(mv, u);
            }

            return nodes;
        }

        public static IReadOnlyList<(string moveUci, long nodes)> Divide(Board b, int depth)
        {
            var acc = new List<(string moveUci, long nodes)>();
            foreach (var mv in b.GenerateLegal())
            {
                var u = b.MakeMove(mv);
                long n = Count(b, depth - 1);
                b.UnmakeMove(mv, u);
                acc.Add(($"{Squares.ToAlgebraic(mv.From88)}{Squares.ToAlgebraic(mv.To88)}", n));
            }
            return acc.OrderByDescending(x => x.nodes).ToList();
        }
    }
}
