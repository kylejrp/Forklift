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
            Span<Board.Move> buf = stackalloc Board.Move[Board.MoveBufferMax];
            var span = MoveGeneration.GeneratePseudoLegal(board, buf, board.SideToMove);

            foreach (var mv in span)
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

        public struct DivideMove
        {
            public byte From88;
            public byte To88;
            public Piece Promotion; // Piece.Empty if no promotion
            public long Nodes;

            public string MoveUci
            {
                get
                {
                    var fromAlg = Squares.ToAlgebraic((Square0x88)From88).Value;
                    var toAlg = Squares.ToAlgebraic((Square0x88)To88).Value;
                    string promo = Promotion != Piece.Empty ? char.ToLower(Piece.ToFENChar((Piece)Promotion)).ToString() : string.Empty;
                    return fromAlg + toAlg + promo;
                }
            }
        }

        public static IReadOnlyList<DivideMove> Divide(Board b, int depth)
        {
            var acc = new List<DivideMove>();
            Span<Board.Move> buf = stackalloc Board.Move[Board.MoveBufferMax];
            var span = b.GenerateLegal(buf);
            foreach (var mv in span)
            {
                var u = b.MakeMove(mv);
                long n = Count(b, depth - 1);
                b.UnmakeMove(mv, u);

                acc.Add(new DivideMove
                {
                    From88 = (byte)mv.From88,
                    To88 = (byte)mv.To88,
                    Promotion = mv.Promotion,
                    Nodes = n
                });
            }
            acc.Sort((a, b) => b.Nodes.CompareTo(a.Nodes));
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
                if (board.IsCheckmate())
                    stats.Checkmates++;
                return;
            }

            Span<Board.Move> moveBuffer = stackalloc Board.Move[Board.MoveBufferMax];
            var span = MoveGeneration.GeneratePseudoLegal(board, moveBuffer, board.SideToMove);

            foreach (var mv in span)
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

            // Early bail: check for non-sliding attackers (knight, pawn, king)
            ulong knightAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Knight);
            if (knightAttackers != 0) return true;

            ulong pawnAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Pawn);
            if (pawnAttackers != 0) return true;

            ulong kingAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.King);
            if (kingAttackers != 0) return true;

            // Sliding attackers (bishop, rook, queen)
            ulong slidingAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Bishop | Piece.PieceType.Rook | Piece.PieceType.Queen);
            return System.Numerics.BitOperations.PopCount(slidingAttackers) >= 2;
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

            // Cache pre-move occupancy for all squares
            Span<bool> preOcc = stackalloc bool[128];
            for (int i = 0; i < 128; i++)
                preOcc[i] = board.At((Square0x88)i) != Piece.Empty;

            // Cache post-move occupancy as a bitboard (0x88 squares mapped to bits 0..63)
            ulong occAfter = board.GetAllOccupancy();
            // Toggle from88 (remove), to88 (add), epRemoved (remove if present)
            int from64 = (int)(Square0x64)Squares.ConvertTo0x64Index(from88);
            int to64 = (int)(Square0x64)Squares.ConvertTo0x64Index(to88);
            occAfter &= ~(1UL << from64);
            occAfter |= (1UL << to64);
            if (epRemoved.HasValue)
            {
                int ep64 = (int)(Square0x64)Squares.ConvertTo0x64Index(epRemoved.Value);
                occAfter &= ~(1UL << ep64);
            }

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
                // Walk until first occupied AFTER the move using bitboard
                var cur = (UnsafeSquare0x88)k88;
                Square0x88? firstOcc = null;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) break;
                    var s = (Square0x88)cur;
                    int s64 = (int)(Square0x64)Squares.ConvertTo0x64Index(s);
                    if ((occAfter & (1UL << s64)) != 0)
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

                // Find first occupied BEFORE the move using cached preOcc
                cur = (UnsafeSquare0x88)k88;
                Square0x88? firstOccBefore = null;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) break;
                    var s = (Square0x88)cur;

                    if (preOcc[(int)s])
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
                var sliderPc = board.At(sliderSq); // mailbox read only once
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
