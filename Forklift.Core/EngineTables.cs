namespace Forklift.Core;

/// <summary>
/// Immutable, thread-safe precomputed tables used by the engine.
/// Create once (e.g., in your test fixture or DI root) and share across threads.
/// </summary>
public sealed class EngineTables
{
    // Non-sliding attacks
    public readonly ulong[] KnightAttackTable; // [64]
    public readonly ulong[] KingAttackTable;   // [64]
    public readonly ulong[] WhitePawnAttackFrom; // [64] squares white pawns attack FROM
    public readonly ulong[] BlackPawnAttackFrom; // [64]

    // Optional: simple slider ray attacks (occupancy-agnostic masks along lines)
    // In this skeleton we'll compute sliders on the fly via 0x88 rays.

    public readonly Zobrist Zobrist;

    // Private ctor ensures immutability
    private EngineTables(ulong[] knightAttackTable, ulong[] kingAttackTable, ulong[] whitePawnAttackFrom, ulong[] blackPawnAttackFrom, Zobrist zobrist)
    {
        KnightAttackTable = knightAttackTable;
        KingAttackTable = kingAttackTable;
        WhitePawnAttackFrom = whitePawnAttackFrom;
        BlackPawnAttackFrom = blackPawnAttackFrom;
        Zobrist = zobrist;
    }

    private static Dictionary<int, List<int>> PrecomputeValidDeltas(int[] deltas)
    {
        var validDeltas = new Dictionary<int, List<int>>();
        for (UnsafeSquare0x88 square88 = (UnsafeSquare0x88)0; square88 < 128; square88++)
        {
            if (Squares.IsOffboard(square88)) continue;

            var validMoves = new List<int>();
            foreach (var delta in deltas)
            {
                var targetSquare = square88 + delta;
                if (targetSquare >= 0 && targetSquare < 128 && !Squares.IsOffboard(targetSquare))
                {
                    validMoves.Add(delta);
                }
            }
            validDeltas[square88] = validMoves;
        }
        return validDeltas;
    }

    public static EngineTables CreateDefault(Zobrist? zobrist = null)
    {
        var knightAttackTable = new ulong[64];
        var kingAttackTable = new ulong[64];
        var whitePawnAttackFrom = new ulong[64];
        var blackPawnAttackFrom = new ulong[64];

        int[] knightDeltas = { +31, +33, +14, +18, -31, -33, -14, -18 };
        int[] kingDeltas = { +1, -1, +16, -16, +15, +17, -15, -17 };
        int[] whitePawnDeltas = { +15, +17 };
        int[] blackPawnDeltas = { -15, -17 };

        var knightValidDeltas = PrecomputeValidDeltas(knightDeltas);
        var kingValidDeltas = PrecomputeValidDeltas(kingDeltas);
        var whitePawnValidDeltas = PrecomputeValidDeltas(whitePawnDeltas);
        var blackPawnValidDeltas = PrecomputeValidDeltas(blackPawnDeltas);

        for (UnsafeSquare0x88 square88 = (UnsafeSquare0x88)0; square88 < 128; square88++)
        {
            if (Squares.IsOffboard(new UnsafeSquare0x88(square88))) continue;
            int square64 = (Square0x64)square88;

            ulong knightMask = 0, kingMask = 0, whitePawnMask = 0, blackPawnMask = 0;
            foreach (var validDelta in knightValidDeltas[square88])
            {
                UnsafeSquare0x88 targetSquare = square88 + validDelta;
                knightMask |= 1UL << (Square0x88)targetSquare;
            }
            foreach (var delta in kingValidDeltas[square88])
            {
                UnsafeSquare0x88 targetSquare = square88 + delta;
                kingMask |= 1UL << (Square0x88)targetSquare;
            }
            foreach (var delta in whitePawnValidDeltas[square88])
            {
                UnsafeSquare0x88 targetSquare = square88 + delta;
                whitePawnMask |= 1UL << (Square0x88)targetSquare;
            }
            foreach (var delta in blackPawnValidDeltas[square88])
            {
                UnsafeSquare0x88 targetSquare = square88 + delta;
                blackPawnMask |= 1UL << (Square0x88)targetSquare;
            }
            knightAttackTable[square64] = knightMask;
            kingAttackTable[square64] = kingMask;
            whitePawnAttackFrom[square64] = whitePawnMask;
            blackPawnAttackFrom[square64] = blackPawnMask;
        }

        return new EngineTables(knightAttackTable, kingAttackTable, whitePawnAttackFrom, blackPawnAttackFrom, zobrist ?? Zobrist.CreateDeterministic());
    }

    // Fix type mismatches in EngineTables
    public static void InitializeAttackTables()
    {
        Square0x88 square = new Square0x88(0x12); // Replace with actual logic
        // Example usage
        // ulong attacks = 0UL; // Replace with actual attack generation logic
    }
}
