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

        for (int square88 = 0; square88 < 128; square88++)
        {
            if (Squares.IsOffboard((Square0x88)square88)) continue;
            int square64 = Squares.ConvertTo0x64Index(new Square0x88(square88));

            ulong knightMask = 0, kingMask = 0, whitePawnMask = 0, blackPawnMask = 0;
            foreach (var delta in knightDeltas)
            {
                int targetSquare = square88 + delta;
                if (!Squares.IsOffboard(new Square0x88(targetSquare))) knightMask |= 1UL << Squares.ConvertTo0x64Index(new Square0x88(targetSquare));
            }
            foreach (var delta in kingDeltas)
            {
                int targetSquare = square88 + delta;
                if (!Squares.IsOffboard(new Square0x88(targetSquare))) kingMask |= 1UL << Squares.ConvertTo0x64Index(new Square0x88(targetSquare));
            }
            foreach (var delta in whitePawnDeltas)
            {
                int targetSquare = square88 + delta;
                if (!Squares.IsOffboard(new Square0x88(targetSquare))) whitePawnMask |= 1UL << Squares.ConvertTo0x64Index(new Square0x88(targetSquare));
            }
            foreach (var delta in blackPawnDeltas)
            {
                int targetSquare = square88 + delta;
                if (!Squares.IsOffboard(new Square0x88(targetSquare))) blackPawnMask |= 1UL << Squares.ConvertTo0x64Index(new Square0x88(targetSquare));
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
        ulong attacks = 0UL; // Replace with actual attack generation logic
    }
}
