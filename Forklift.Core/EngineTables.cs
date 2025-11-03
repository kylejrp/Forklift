namespace Forklift.Core
{
    public sealed class EngineTables
    {
        public readonly ulong[] KnightAttackTable;    // attack-from masks keyed by target 0x64
        public readonly ulong[] KingAttackTable;      // attack-from masks keyed by target 0x64
        public readonly ulong[] WhitePawnAttackFrom;  // attack-from masks keyed by target 0x64
        public readonly ulong[] BlackPawnAttackFrom;  // attack-from masks keyed by target 0x64

        // Slider directions in 0x88 space (pure consts)
        private static readonly int[] RookDirections = [+1, -1, +16, -16];
        private static readonly int[] BishopDirections = [+15, +17, -15, -17];

        public readonly ulong[][] BishopAttackMasks;   // [fromSq][occupancy] -> attacks
        public readonly ulong[][] RookAttackMasks;     // [fromSq][occupancy] -> attacks
        public readonly ulong[][] QueenAttackMasks;    // [fromSq][occupancy] -> attacks

        public readonly Zobrist Zobrist;

        private EngineTables(
            ulong[] knightAttackTable,
            ulong[] kingAttackTable,
            ulong[] whitePawnAttackFrom,
            ulong[] blackPawnAttackFrom,
            ulong[][] bishopAttackMasks,
            ulong[][] rookAttackMasks,
            ulong[][] queenAttackMasks,
            Zobrist zobrist)
        {
            KnightAttackTable = knightAttackTable;
            KingAttackTable = kingAttackTable;
            WhitePawnAttackFrom = whitePawnAttackFrom;
            BlackPawnAttackFrom = blackPawnAttackFrom;
            BishopAttackMasks = bishopAttackMasks;
            RookAttackMasks = rookAttackMasks;
            QueenAttackMasks = queenAttackMasks;
            Zobrist = zobrist;
        }

        public static EngineTables CreateDefault(Zobrist? zobrist = null)
        {
            var knightAttackTable = new ulong[64];
            var kingAttackTable = new ulong[64];
            var whitePawnAttackFrom = new ulong[64];
            var blackPawnAttackFrom = new ulong[64];

            var bishopAttackMasks = new ulong[64][];
            var rookAttackMasks = new ulong[64][];
            var queenAttackMasks = new ulong[64][];

            // 0x88 deltas (same as your generator)
            ReadOnlySpan<int> KNIGHT = stackalloc int[] { +33, +31, +18, +14, -14, -18, -31, -33 };
            ReadOnlySpan<int> KING = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };
            // Pawn deltas are defined relative to the PAWN’s attack direction.
            // For ATTACK-FROM tables keyed by target, we reverse them (see below).
            const int W_PAWN_L = +15, W_PAWN_R = +17; // white attacks "up"
            const int B_PAWN_L = -15, B_PAWN_R = -17; // black attacks "down"

            for (UnsafeSquare0x88 t88 = (UnsafeSquare0x88)0; t88.Value < 128; t88++)
            {
                if (Squares.IsOffboard(t88)) continue;

                var t64 = (Square0x64)(Square0x88)t88;

                ulong kmask = 0, Kmask = 0, wpmask = 0, bpmask = 0;

                // Knights: for each target t, attackers are at (t - d) for all d
                foreach (int d in KNIGHT)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        kmask |= 1UL << (int)s64;
                    }
                }

                // Kings: same reverse-lookup idea
                foreach (int d in KING)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        Kmask |= 1UL << (int)s64;
                    }
                }

                // White pawns attack from (t - 15) and (t - 17)
                {
                    var fromL = new UnsafeSquare0x88(t88.Value - W_PAWN_L);
                    if (!Squares.IsOffboard(fromL))
                    {
                        var s64 = (Square0x64)(Square0x88)fromL;
                        wpmask |= 1UL << (int)s64;
                    }
                    var fromR = new UnsafeSquare0x88(t88.Value - W_PAWN_R);
                    if (!Squares.IsOffboard(fromR))
                    {
                        var s64 = (Square0x64)(Square0x88)fromR;
                        wpmask |= 1UL << (int)s64;
                    }
                }

                // Black pawns attack from (t - (-15)) = (t + 15) and (t + 17)
                {
                    var fromL = new UnsafeSquare0x88(t88.Value - B_PAWN_L); // t + 15
                    if (!Squares.IsOffboard(fromL))
                    {
                        var s64 = (Square0x64)(Square0x88)fromL;
                        bpmask |= 1UL << (int)s64;
                    }
                    var fromR = new UnsafeSquare0x88(t88.Value - B_PAWN_R); // t + 17
                    if (!Squares.IsOffboard(fromR))
                    {
                        var s64 = (Square0x64)(Square0x88)fromR;
                        bpmask |= 1UL << (int)s64;
                    }
                }

                knightAttackTable[(int)t64] = kmask;
                kingAttackTable[(int)t64] = Kmask;
                whitePawnAttackFrom[(int)t64] = wpmask;
                blackPawnAttackFrom[(int)t64] = bpmask;

                // Precompute slider attacks for bishop, rook, queen
                bishopAttackMasks[(int)t64] = PrecomputeSliderAttacks((int)t64, BishopDirections);
                rookAttackMasks[(int)t64] = PrecomputeSliderAttacks((int)t64, RookDirections);
                // Queen = bishop | rook
                queenAttackMasks[(int)t64] = new ulong[512];
                for (int occ = 0; occ < 512; occ++)
                    queenAttackMasks[(int)t64][occ] = bishopAttackMasks[(int)t64][occ] | rookAttackMasks[(int)t64][occ];
            }

            return new EngineTables(
                knightAttackTable,
                kingAttackTable,
                whitePawnAttackFrom,
                blackPawnAttackFrom,
                bishopAttackMasks,
                rookAttackMasks,
                queenAttackMasks,
                zobrist ?? Zobrist.CreateDeterministic());
        }

        // Helper to precompute slider attacks for a square and directions
        private static ulong[] PrecomputeSliderAttacks(int fromSq, int[] directions)
        {
            // For simplicity, use a small occupancy mask (e.g., 9 bits for relevant squares)
            // This is a placeholder; real magic bitboards would be more complex
            var attacks = new ulong[512];
            for (int occ = 0; occ < attacks.Length; occ++)
            {
                attacks[occ] = 0UL;
                foreach (var d in directions)
                {
                    int sq = fromSq;
                    while (true)
                    {
                        sq += d;
                        if (sq < 0 || sq >= 64) break;
                        attacks[occ] |= 1UL << sq;
                        // Stop if occupancy bit is set (simulate blockers)
                        if ((occ & (1 << (sq % 9))) != 0) break;
                    }
                }
            }
            return attacks;
        }
    }
}
