namespace Forklift.Core
{
    public sealed class EngineTables
    {
        public readonly ulong[] KnightAttackTable;    // attack-from masks keyed by target 0x64
        public readonly ulong[] KingAttackTable;      // attack-from masks keyed by target 0x64
        public readonly ulong[] WhitePawnAttackFrom;  // attack-from masks keyed by target 0x64
        public readonly ulong[] BlackPawnAttackFrom;  // attack-from masks keyed by target 0x64

        public readonly Zobrist Zobrist;

        private EngineTables(
            ulong[] knightAttackTable,
            ulong[] kingAttackTable,
            ulong[] whitePawnAttackFrom,
            ulong[] blackPawnAttackFrom,
            Zobrist zobrist)
        {
            KnightAttackTable = knightAttackTable;
            KingAttackTable = kingAttackTable;
            WhitePawnAttackFrom = whitePawnAttackFrom;
            BlackPawnAttackFrom = blackPawnAttackFrom;
            Zobrist = zobrist;
        }

        public static EngineTables CreateDefault(Zobrist? zobrist = null)
        {
            var knightAttackTable = new ulong[64];
            var kingAttackTable = new ulong[64];
            var whitePawnAttackFrom = new ulong[64];
            var blackPawnAttackFrom = new ulong[64];

            // 0x88 deltas (same as your generator)
            ReadOnlySpan<int> KNIGHT = stackalloc int[] { +33, +31, +18, +14, -14, -18, -31, -33 };
            ReadOnlySpan<int> KING = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };
            // Pawn deltas are defined relative to the PAWN’s attack direction.
            // For ATTACK-FROM tables keyed by target, we reverse them (see below).
            const int W_PAWN_L = +15, W_PAWN_R = +17; // white attacks "up"
            const int B_PAWN_L = -15, B_PAWN_R = -17; // black attacks "down"

            for (UnsafeSquare0x88 t88 = (UnsafeSquare0x88)0; t88 < 128; t88++)
            {
                if (Squares.IsOffboard(t88)) continue;

                int t64 = (Square0x64)(Square0x88)t88;

                ulong kmask = 0, Kmask = 0, wpmask = 0, bpmask = 0;

                // Knights: for each target t, attackers are at (t - d) for all d
                foreach (int d in KNIGHT)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        int s64 = (Square0x64)(Square0x88)from;
                        kmask |= 1UL << s64;
                    }
                }

                // Kings: same reverse-lookup idea
                foreach (int d in KING)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        int s64 = (Square0x64)(Square0x88)from;
                        Kmask |= 1UL << s64;
                    }
                }

                // White pawns attack from (t - 15) and (t - 17)
                {
                    var fromL = new UnsafeSquare0x88(t88.Value - W_PAWN_L);
                    if (!Squares.IsOffboard(fromL))
                    {
                        int s64 = (Square0x64)(Square0x88)fromL;
                        wpmask |= 1UL << s64;
                    }
                    var fromR = new UnsafeSquare0x88(t88.Value - W_PAWN_R);
                    if (!Squares.IsOffboard(fromR))
                    {
                        int s64 = (Square0x64)(Square0x88)fromR;
                        wpmask |= 1UL << s64;
                    }
                }

                // Black pawns attack from (t - (-15)) = (t + 15) and (t + 17)
                {
                    var fromL = new UnsafeSquare0x88(t88.Value - B_PAWN_L); // t + 15
                    if (!Squares.IsOffboard(fromL))
                    {
                        int s64 = (Square0x64)(Square0x88)fromL;
                        bpmask |= 1UL << s64;
                    }
                    var fromR = new UnsafeSquare0x88(t88.Value - B_PAWN_R); // t + 17
                    if (!Squares.IsOffboard(fromR))
                    {
                        int s64 = (Square0x64)(Square0x88)fromR;
                        bpmask |= 1UL << s64;
                    }
                }

                knightAttackTable[t64] = kmask;
                kingAttackTable[t64] = Kmask;
                whitePawnAttackFrom[t64] = wpmask;
                blackPawnAttackFrom[t64] = bpmask;
            }

            return new EngineTables(
                knightAttackTable,
                kingAttackTable,
                whitePawnAttackFrom,
                blackPawnAttackFrom,
                zobrist ?? Zobrist.CreateDeterministic());
        }
    }
}
