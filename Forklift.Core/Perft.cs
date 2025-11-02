using System.Collections;
using Forklift.Core;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public static class Perft
    {
        public static long Count(Board board, int depth)
        {
            if (depth == 0) return 1;

            long nodes = 0;
            var moves = new List<Board.Move>(64);
            MoveGeneration.GeneratePseudoLegal(board, moves, board.SideToMove);

            foreach (var mv in moves)
            {
                var u = board.MakeMove(mv);
                // After MakeMove, side to move flipped; the side that just moved must NOT be in check
                bool legal = !board.InCheck(board.SideToMove.Flip());
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

                var fromAlg = Squares.ToAlgebraic(mv.From88).Value;
                var toAlg = Squares.ToAlgebraic(mv.To88).Value;
                string promo = mv.Promotion != Piece.Empty ? char.ToLower(Piece.ToFENChar(mv.Promotion)).ToString() : string.Empty;

                acc.Add((fromAlg + toAlg + promo, n));
            }
            acc.Sort((a, b) => b.nodes.CompareTo(a.nodes));
            return acc;
        }


        public struct PerftStatistics
        {
            public long Nodes;
            public long Captures;
            public long EnPassant;
            public long Castles;
            public long Promotions;
            public long Checks;
            public long DiscoveryChecks;
            public long DoubleChecks;
            public long Checkmates;
        }

        public static PerftStatistics Statistics(Board board, int depth)
        {
            var stats = new PerftStatistics();
            StatisticsImpl(board, depth, ref stats);
            return stats;
        }

        private static void StatisticsImpl(Board board, int depth, ref PerftStatistics stats)
        {
            if (depth == 0)
            {
                stats.Nodes++;
                bool hasLegal = false;
                foreach (var _ in board.GenerateLegal()) { hasLegal = true; break; }
                if (!hasLegal && board.InCheck(board.SideToMove))
                    stats.Checkmates++;
                return;
            }

            var moves = new List<Board.Move>(64);
            MoveGeneration.GeneratePseudoLegal(board, moves, board.SideToMove);

            foreach (var mv in moves)
            {
                var u = board.MakeMove(mv);
                bool legal = !board.InCheck(board.SideToMove.Flip());
                if (legal)
                {
                    if (depth == 1)
                    {
                        if (mv.IsCapture) stats.Captures++;

                        if (mv.IsEnPassant) stats.EnPassant++;
                        if (mv.IsCastle) stats.Castles++;
                        if (mv.Promotion != Piece.Empty) stats.Promotions++;

                        bool isCheck = board.InCheck(board.SideToMove);
                        if (isCheck) stats.Checks++;

                        bool isDbl = false;
                        if (isCheck)
                            isDbl = IsDoubleCheck(board);
                        if (isCheck && !isDbl && IsDiscoveryCheck(board, mv))
                            stats.DiscoveryChecks++;
                        if (isDbl)
                            stats.DoubleChecks++;
                    }

                    StatisticsImpl(board, depth - 1, ref stats);
                }

                board.UnmakeMove(mv, u);
            }
        }

        // Simplest and correct in a legal perft tree.
        internal static bool IsDoubleCheck(Board board)
        {
            // Post-move: board.SideToMove is the side that may be in check.
            var checkedSide = board.SideToMove;
            if (!board.InCheck(checkedSide)) return false;

            var kingSq64 = board.FindKingSq64(checkedSide);
            var attackerSide = checkedSide.Flip();

            ulong attackers = board.AttackersToSquare(kingSq64, attackerSide);
            return System.Numerics.BitOperations.PopCount(attackers) >= 2;
        }

        private static readonly int[] DIRS_ALL = { +1, -1, +16, -16, +15, +17, -15, -17 };


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsRookDir(int d) => d == +1 || d == -1 || d == +16 || d == -16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBishopDir(int d) => d == +15 || d == +17 || d == -15 || d == -17;

        public static bool IsDiscoveryCheck(Board board, in Board.Move mv)
        {
            // Side that just moved (pre-move state)
            var us = mv.Mover.IsWhite ? Color.White : Color.Black;
            var them = us.Flip();

            // Opponent king (target of any check we’d be “discovering”)
            Square0x64 k64 = board.FindKingSq64(them);
            Square0x88 k88 = Squares.ConvertTo0x88Index(k64);

            // Pre-move squares
            Square0x88 from88 = mv.From88;
            Square0x88 to88 = mv.To88;

            // En-passant captured pawn square (present before move; gone after move)
            Square0x88? epRemoved = null;
            if (mv.IsEnPassant)
                epRemoved = mv.Mover.IsWhite ? (Square0x88)(to88 - 16) : (Square0x88)(to88 + 16);

            // Treat a square as occupied in the "after move" snapshot.
            bool OccupiedAfter(Square0x88 s)
            {
                // EP removes the pawn behind the EP target
                if (epRemoved.HasValue && s == epRemoved.Value) return false;
                // we moved off 'from'
                if (s == from88) return false;
                // we now occupy 'to' (with either the mover or promotion piece)
                if (s == to88) return true;
                // otherwise whatever is on the board now
                return board.At(s) != Piece.Empty;
            }

            // Get the piece at a square in the *pre-move* board (used to identify the discovering slider)
            Piece PieceAtPreMove(Square0x88 s) => board.At(s);

            // Helper: is piece a slider matching direction?
            bool PieceMatchesDir(Piece p, int dir)
            {
                if (p == Piece.Empty) return false;
                bool pWhite = p.IsWhite;
                if (pWhite != us.IsWhite()) return false; // slider must be ours

                if (IsRookDir(dir)) return p == Piece.WhiteRook || p == Piece.BlackRook || p == Piece.WhiteQueen || p == Piece.BlackQueen;
                if (IsBishopDir(dir)) return p == Piece.WhiteBishop || p == Piece.BlackBishop || p == Piece.WhiteQueen || p == Piece.BlackQueen;
                return false;
            }

            // Check if a candidate destination still blocks the ray between king and slider
            static bool OnOpenSegment(Square0x88 k, Square0x88 s, int dir, Square0x88 q)
            {
                // march from king toward slider; if we hit q before s, q blocks
                var cur = (UnsafeSquare0x88)k;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) return false;
                    var c = (Square0x88)cur;
                    if (c == q) return true;   // q lies between king and slider
                    if (c == s) return false;  // reached the slider without seeing q
                }
            }

            // Scan from the opponent king outward along all 8 rays.
            foreach (int dir in DIRS_ALL)
            {
                // Walk until first occupied AFTER the move
                var cur = (UnsafeSquare0x88)k88;
                Square0x88? firstOcc = null;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) break;
                    var s = (Square0x88)cur;
                    if (OccupiedAfter(s))
                    {
                        firstOcc = s;
                        break;
                    }
                }
                if (firstOcc is null) continue;

                // For a genuine discovery, the *first* occupied square after the move
                // must be the *revealed slider*; and BEFORE the move the first block
                // must have been either the mover's from-square or the EP-captured pawn.
                // So: BEFORE the move, the first occupied from king along dir must be
                // either 'from88' or 'epRemoved' (if any). Check that now:

                // Find first occupied BEFORE the move
                cur = (UnsafeSquare0x88)k88;
                Square0x88? firstOccBefore = null;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) break;
                    var s = (Square0x88)cur;

                    bool occBefore = board.At(s) != Piece.Empty;
                    if (occBefore)
                    {
                        firstOccBefore = s;
                        break;
                    }
                }
                if (firstOccBefore is null) continue;

                // It must have been blocked by the thing we remove (from or EP-captured)
                bool wasBlockedByMoverOrEP =
                    firstOccBefore.Value == from88 ||
                    (epRemoved.HasValue && firstOccBefore.Value == epRemoved.Value);

                if (!wasBlockedByMoverOrEP) continue;

                // Now, AFTER the move the first piece must be a same-side slider in this dir.
                var sliderSq = firstOcc.Value;
                var sliderPc = PieceAtPreMove(sliderSq); // piece identity doesn’t change by moving the blocker
                if (!PieceMatchesDir(sliderPc, dir)) continue;

                // If we move TO a square that still lies between king and slider along this dir,
                // we still block the line => NOT a discovery.
                if (OnOpenSegment(k88, sliderSq, dir, to88)) continue;

                // Also, if the move CAPTURES the slider, it’s obviously not a discovery.
                if (mv.IsCapture && sliderSq == mv.To88) continue;

                // All criteria satisfied → discovered check.
                return true;
            }

            return false;
        }


        // --- helpers ---

        // Returns true and the 0x88 step (+/-1, +/-16, +/-15, +/-17) if squares are aligned; false otherwise.
        private static bool TryStepBetween(int from88, int to88, out int step)
        {
            step = 0;
            int df = (to88 & 0x0F) - (from88 & 0x0F);
            int dr = (to88 >> 4) - (from88 >> 4);

            if (dr == 0) { step = df > 0 ? +1 : -1; return true; }
            if (df == 0) { step = dr > 0 ? +16 : -16; return true; }
            if (df == dr) { step = dr > 0 ? +17 : -17; return true; }
            if (df == -dr) { step = dr > 0 ? +15 : -15; return true; }

            return false;
        }
    }
}
