//#define REGENERATE_MAGICS

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public sealed class EngineTables
    {
        // =========================
        //  Public precomputed tables
        // =========================

        public static readonly ulong[] BishopMasks = new ulong[64];
        public static readonly ulong[] RookMasks = new ulong[64];

        // Attack-from masks keyed by target 0x64
        public readonly ulong[] KnightAttackTable;    // [target64] bitboard of sources
        public readonly ulong[] KingAttackTable;      // [target64] bitboard of sources
        public readonly ulong[] WhitePawnAttackFrom;  // [target64] bitboard of sources
        public readonly ulong[] BlackPawnAttackFrom;  // [target64] bitboard of sources

        // Packed magic attack tables: [offset[from] + idx] -> attacks
        public readonly int[] BishopOffsets;
        public readonly int[] RookOffsets;
        public readonly ulong[] BishopTable;
        public readonly ulong[] RookTable;
        public static readonly byte[] BishopIndexBits = new byte[64];
        public static readonly byte[] RookIndexBits = new byte[64];

        public readonly Zobrist Zobrist;

        // =========================
        //  Magic constants (active)
        // =========================

        // ACTIVE arrays actually used by both build & lookup (point to baked or regenerated).
        private static ulong[] CurrentBishopMagics = Array.Empty<ulong>();
        private static ulong[] CurrentRookMagics = Array.Empty<ulong>();

        // ---- BAKED magics (from your message). After regenerating, paste new values here. ----
        private static readonly ulong[] BishopMagics = new ulong[64] {
            0x2104088094108200UL, 0x0608182080820941UL, 0x0488080108306020UL, 0x0420A10041000100UL, 0x0221104000000040UL, 0x0502080208020424UL, 0x0000512808400041UL, 0x0002008400880400UL,
            0x0018101050009080UL, 0x0200023004408082UL, 0x0000C40124030404UL, 0x0020880605400140UL, 0x4000440420104202UL, 0x0100031118400000UL, 0x0008264812282000UL, 0x24820222080C8401UL,
            0x06088020A1042086UL, 0x4022913050191500UL, 0x0008121000902208UL, 0x00A8002082044000UL, 0x002C000A84A00000UL, 0x0201004809081200UL, 0x0B00800402080280UL, 0x4800890100413041UL,
            0x0004042020A00400UL, 0x1D04040020410400UL, 0x2802248010040080UL, 0x2400802052020200UL, 0x0042002002008051UL, 0x004041010080A000UL, 0x0308810080980801UL, 0x4006204080804810UL,
            0x0408049000046030UL, 0x8288848488200812UL, 0x0000402800501040UL, 0x0024400808008200UL, 0x0140080240020050UL, 0x0009100080030063UL, 0x0981082310120500UL, 0xA40802003C404100UL,
            0x4814026240041100UL, 0x0C01080203001040UL, 0x0280420041001020UL, 0x5000004200800802UL, 0x921C840104020210UL, 0x0001040802000290UL, 0x60020481020C3400UL, 0x0704040430462220UL,
            0xE100884402202050UL, 0x0000940401044204UL, 0x0400250C070C4002UL, 0x0003006104A80032UL, 0xA000004005070422UL, 0x00000883080A0004UL, 0x0040820841210005UL, 0x4008012404004020UL,
            0x5040110401600800UL, 0x0000188088215020UL, 0x4002000201008822UL, 0x08A001018220A804UL, 0x00484820C2082204UL, 0x0000002420141500UL, 0x0180081170288300UL, 0x420801301A060024UL
        };

        private static readonly ulong[] RookMagics = new ulong[64] {
            0x0080001080204000UL, 0x40C0100140082000UL, 0xA700284060001100UL, 0x2080100084080180UL, 0x0200041020080200UL, 0x050005000400082AUL, 0x4100040081000200UL, 0x848004C980012100UL,
            0x00C4800830400080UL, 0x0103402010004000UL, 0x4200801000200080UL, 0x4A02002040100A00UL, 0x2801802400080080UL, 0x0004800200340080UL, 0x0140808002000100UL, 0x000A000081004402UL,
            0x0324228000804000UL, 0x8010004000200040UL, 0xA0B0002008040020UL, 0x9450010021001008UL, 0x0008808004000800UL, 0x00A4008004020080UL, 0x1100040001900228UL, 0x0040020008904504UL,
            0x0000400880008028UL, 0x1000420200210082UL, 0x1020080040100040UL, 0x8083040900201000UL, 0x040A040080800800UL, 0x8102008200040910UL, 0x8002008200010804UL, 0x0812008200040041UL,
            0x00D5008001002640UL, 0x0000400101002080UL, 0x0000100288802000UL, 0x0008004881801000UL, 0x0400080080800402UL, 0x0222000402000810UL, 0x6000010204009068UL, 0x8040008042000104UL,
            0x80008000C0028020UL, 0x3140010080410020UL, 0x2210002000410100UL, 0x8000090010010020UL, 0x0000110008010005UL, 0x0004000201004040UL, 0x481E000401820008UL, 0x4080A04400820001UL,
            0x2004842041060200UL, 0x0010002000400040UL, 0x00308B1000200080UL, 0x0008028880100280UL, 0x4002080100100500UL, 0x0140800400020080UL, 0x1008281201108400UL, 0x9010040471008200UL,
            0x0020820010410822UL, 0x000020D088400301UL, 0x808020004100100DUL, 0x00404900201000C5UL, 0x8002000420081002UL, 0x0002001004080102UL, 0x8804188110280204UL, 0x0000250024018046UL
        };

        // =========================
        //  Static ctor: generate masks
        // =========================

        static EngineTables()
        {
            for (int sq = 0; sq < 64; sq++)
            {
                BishopMasks[sq] = GenerateBishopMask(sq);
                RookMasks[sq] = GenerateRookMask(sq);
                BishopIndexBits[sq] = (byte)BitOperations.PopCount(BishopMasks[sq]);
                RookIndexBits[sq] = (byte)BitOperations.PopCount(RookMasks[sq]);
            }
        }

        // =========================
        //  Construction
        // =========================

        private EngineTables(
            ulong[] knightAttackTable,
            ulong[] kingAttackTable,
            ulong[] whitePawnAttackFrom,
            ulong[] blackPawnAttackFrom,
            int[] bishopOffsets,
            ulong[] bishopTable,
            int[] rookOffsets,
            ulong[] rookTable,
            Zobrist zobrist)
        {
            KnightAttackTable = knightAttackTable;
            KingAttackTable = kingAttackTable;
            WhitePawnAttackFrom = whitePawnAttackFrom;
            BlackPawnAttackFrom = blackPawnAttackFrom;
            BishopOffsets = bishopOffsets;
            BishopTable = bishopTable;
            RookOffsets = rookOffsets;
            RookTable = rookTable;
            Zobrist = zobrist;
        }

        private static readonly Lazy<EngineTables> _defaultInstance = new(() => CreateDefaultInternal());

        public static EngineTables CreateDefault(Zobrist? zobrist = null)
        {
            if (zobrist == null)
            {
                return _defaultInstance.Value;
            }
            // If a custom zobrist is provided, build a new instance
            return CreateDefaultInternal(zobrist);
        }

        private static EngineTables CreateDefaultInternal(Zobrist? zobrist = null)
        {
            // 1) Decide which magics we will use (baked vs regenerated)
            EnsureMagicsReady(out var activeBishopMagics, out var activeRookMagics);
            CurrentBishopMagics = activeBishopMagics;
            CurrentRookMagics = activeRookMagics;

            var knightAttackTable = new ulong[64];
            var kingAttackTable = new ulong[64];
            var whitePawnAttackFrom = new ulong[64];
            var blackPawnAttackFrom = new ulong[64];

            var bishopOffsets = new int[65];
            var rookOffsets = new int[65];
            int bishopTotal = 0, rookTotal = 0;

            // Precompute offsets for packed tables
            for (int sq = 0; sq < 64; sq++)
            {
                int bLen = 1 << BitOperations.PopCount(BishopMasks[sq]);
                int rLen = 1 << BitOperations.PopCount(RookMasks[sq]);
                bishopOffsets[sq] = bishopTotal;
                rookOffsets[sq] = rookTotal;
                bishopTotal += bLen;
                rookTotal += rLen;
            }
            bishopOffsets[64] = bishopTotal;
            rookOffsets[64] = rookTotal;

            var bishopTable = new ulong[bishopTotal];
            var rookTable = new ulong[rookTotal];

            // Fill packed tables
            for (int sq = 0; sq < 64; sq++)
            {
                var bArr = GenerateMagicAttackTable(sq, bishop: true, CurrentBishopMagics);
                var rArr = GenerateMagicAttackTable(sq, bishop: false, CurrentRookMagics);
                Array.Copy(bArr, 0, bishopTable, bishopOffsets[sq], bArr.Length);
                Array.Copy(rArr, 0, rookTable, rookOffsets[sq], rArr.Length);
            }

            ReadOnlySpan<int> KNIGHT = stackalloc int[] { +33, +31, +18, +14, -14, -18, -31, -33 };
            ReadOnlySpan<int> KING = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };
            const int W_PAWN_L = +15, W_PAWN_R = +17;
            const int B_PAWN_L = -15, B_PAWN_R = -17;

            for (UnsafeSquare0x88 t88 = (UnsafeSquare0x88)0; t88.Value < 128; t88++)
            {
                if (Squares.IsOffboard(t88)) continue;

                var t64 = (Square0x64)(Square0x88)t88;
                ulong kmask = 0, Kmask = 0, wpmask = 0, bpmask = 0;

                // Knight attack-from
                foreach (int d in KNIGHT)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        kmask |= 1UL << (int)s64;
                    }
                }

                // King attack-from
                foreach (int d in KING)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        Kmask |= 1UL << (int)s64;
                    }
                }

                // White pawn attack-from
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

                // Black pawn attack-from
                {
                    var fromL = new UnsafeSquare0x88(t88.Value - B_PAWN_L);
                    if (!Squares.IsOffboard(fromL))
                    {
                        var s64 = (Square0x64)(Square0x88)fromL;
                        bpmask |= 1UL << (int)s64;
                    }
                    var fromR = new UnsafeSquare0x88(t88.Value - B_PAWN_R);
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
                // Queen table logic can be refactored later if needed
            }
#if DEBUG
            // --- DEBUG SANITY CHECKS ---
            // 1) table size == 2^(popcount(mask))
            // 2) every table has at least one nonzero attack
            for (int sq = 0; sq < 64; sq++)
            {
                int bBits = PopCount(BishopMasks[sq]);
                int rBits = PopCount(RookMasks[sq]);
                int bOffset = bishopOffsets[sq];
                int rOffset = rookOffsets[sq];
                int bLen = 1 << bBits;
                int rLen = 1 << rBits;

                System.Diagnostics.Debug.Assert(
                    bLen == (bishopOffsets[sq + 1] - bishopOffsets[sq]),
                    $"[DEBUG] Bishop table size mismatch at {sq}: expected {bLen}, got {bishopOffsets[sq + 1] - bishopOffsets[sq]}");

                System.Diagnostics.Debug.Assert(
                    rLen == (rookOffsets[sq + 1] - rookOffsets[sq]),
                    $"[DEBUG] Rook table size mismatch at {sq}: expected {rLen}, got {rookOffsets[sq + 1] - rookOffsets[sq]}");

                bool bHas = false, rHas = false;
                for (int i = 0; i < bLen; i++)
                    if (bishopTable[bOffset + i] != 0) { bHas = true; break; }
                for (int i = 0; i < rLen; i++)
                    if (rookTable[rOffset + i] != 0) { rHas = true; break; }
                System.Diagnostics.Debug.Assert(bHas, $"[DEBUG] BishopTable empty at square {sq}");
                System.Diagnostics.Debug.Assert(rHas, $"[DEBUG] RookTable empty at square {sq}");
            }
#endif
            var inst = new EngineTables(
                knightAttackTable,
                kingAttackTable,
                whitePawnAttackFrom,
                blackPawnAttackFrom,
                bishopOffsets,
                bishopTable,
                rookOffsets,
                rookTable,
                zobrist ?? Zobrist.CreateDeterministic());
            return inst;
        }

        // =========================
        //  Lookup helper (static)
        // =========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetSliderAttackIndex(int sq, ulong occ, Piece.PieceType type)
        {
            bool bishop = type == Piece.PieceType.Bishop;
            ulong mask = bishop ? BishopMasks[sq] : RookMasks[sq];
            ulong magic = bishop ? CurrentBishopMagics[sq] : CurrentRookMagics[sq];
            int bits = bishop ? BishopIndexBits[sq] : RookIndexBits[sq];
            return (int)(((occ & mask) * magic) >> (64 - bits));
        }

        // =========================
        //  Mask generation
        // =========================

        private static ulong GenerateBishopMask(int sq)
        {
            ulong mask = 0UL;
            int rk = sq / 8, fl = sq % 8;
            for (int dr = -1; dr <= 1; dr += 2)
            {
                for (int df = -1; df <= 1; df += 2)
                {
                    int r = rk + dr, f = fl + df;
                    while (r >= 0 && r <= 7 && f >= 0 && f <= 7)
                    {
                        // Exclude edge squares
                        if (r == 0 || r == 7 || f == 0 || f == 7) break;
                        mask |= 1UL << (r * 8 + f);
                        r += dr; f += df;
                    }
                }
            }
            return mask;
        }

        private static ulong GenerateRookMask(int sq)
        {
            ulong mask = 0UL;
            int rk = sq / 8, fl = sq % 8;
            for (int r = rk + 1; r <= 6; r++) mask |= 1UL << (r * 8 + fl); // up
            for (int r = rk - 1; r >= 1; r--) mask |= 1UL << (r * 8 + fl); // down
            for (int f = fl + 1; f <= 6; f++) mask |= 1UL << (rk * 8 + f); // right
            for (int f = fl - 1; f >= 1; f--) mask |= 1UL << (rk * 8 + f); // left
            return mask;
        }

        // =========================
        //  Magic table build
        // =========================

        private static ulong[] GenerateMagicAttackTable(int sq, bool bishop, ulong[] magics)
        {
            ulong mask = bishop ? BishopMasks[sq] : RookMasks[sq];
            int bits = PopCount(mask);
            ulong magic = magics[sq];

            int tableSize = 1 << bits;
            var table = new ulong[tableSize];

#if DEBUG
            var seen = new Dictionary<int, ulong>(tableSize);
#endif

            for (int occIdx = 0; occIdx < tableSize; occIdx++)
            {
                ulong occ = IndexToOccupancy(occIdx, bits, mask);
                int mi = MagicIndex(occ, mask, magic, bits);

#if DEBUG
                if (seen.TryGetValue(mi, out var prevOcc) && prevOcc != occ)
                    throw new InvalidOperationException(
                        $"Magic collision at sq {sq} ({(bishop ? "B" : "R")}), " +
                        $"bits={bits}, index={mi}\nprevOcc={prevOcc:X16} newOcc={occ:X16}");
                seen[mi] = occ;
#endif
                table[mi] = ComputeSliderAttacks(sq, occ, bishop);
            }

            return table;
        }

        // =========================
        //  Regeneration plumbing
        // =========================

        private static void EnsureMagicsReady(out ulong[] bishopMagics, out ulong[] rookMagics)
        {
#if REGENERATE_MAGICS
            bishopMagics = new ulong[64];
            rookMagics = new ulong[64];

            for (int sq = 0; sq < 64; sq++)
            {
                ulong bMask = BishopMasks[sq];
                int bBits = PopCount(bMask);
                bishopMagics[sq] = FindMagicForSquare(sq, bishop: true, mask: bMask, bits: bBits);

                ulong rMask = RookMasks[sq];
                int rBits = PopCount(rMask);
                rookMagics[sq] = FindMagicForSquare(sq, bishop: false, mask: rMask, bits: rBits);
            }

            DumpMagicsToConsole("BishopMagics", bishopMagics);
            DumpMagicsToConsole("RookMagics", rookMagics);
#else
            bishopMagics = BishopMagics;
            rookMagics = RookMagics;
#endif
        }

#if REGENERATE_MAGICS
        // Deterministic RNG for magics search
        private static readonly Random rng = new(0xC0FFEE);

        private static ulong Rand64()
        {
            ulong x = ((ulong)rng.Next() << 32) | (uint)rng.Next();
            x ^= (x << 13); x ^= (x >> 7); x ^= (x << 17);
            return x;
        }

        private static ulong RandMagicCandidate()
        {
            // Sparse candidates empirically collide less
            return Rand64() & Rand64() & Rand64();
        }

        private static ulong FindMagicForSquare(int sq, bool bishop, ulong mask, int bits)
        {
            int size = 1 << bits;
            var occs = new ulong[size];
            var attacks = new ulong[size];
            var used = new ulong[size];

            // Precompute all subsets + reference attacks
            for (int i = 0; i < size; i++)
            {
                ulong occ = IndexToOccupancy(i, bits, mask);
                occs[i] = occ;
                attacks[i] = ComputeSliderAttacks(sq, occ, bishop);
            }

            const int LOG_EVERY = 50_000;
            const int MAX_ATTEMPTS = 5_000_000;

            // Try candidates until collision-free
            for (int attempts = 1; attempts <= MAX_ATTEMPTS; attempts++)
            {
                ulong magic = RandMagicCandidate();

                // Skip weak candidates (top bits heuristic)
                if (BitOperations.PopCount((mask * magic) & 0xFF00_0000_0000_0000UL) < 6)
                    continue;

                Array.Fill(used, 0UL);
                bool fail = false;

                for (int i = 0; i < size && !fail; i++)
                {
                    int idx = (int)((occs[i] * magic) >> (64 - bits));
                    if (used[idx] == 0UL) used[idx] = attacks[i];
                    else if (used[idx] != attacks[i]) fail = true;
                }

                if (!fail) return magic;

                if ((attempts % LOG_EVERY) == 0)
                    Console.WriteLine($"[{(bishop ? 'B' : 'R')}{sq:D2}] attempts={attempts} (bits={bits}) still searching...");
            }

            throw new InvalidOperationException(
                $"Failed to find magic for {(bishop ? "B" : "R")} sq={sq} after MAX_ATTEMPTS; check masks/numbering.");
        }

        [System.Diagnostics.Conditional("REGENERATE_MAGICS")]
        private static void DumpMagicsToConsole(string name, ulong[] magics)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("private static readonly ulong[] ").Append(name).Append(" = new ulong[64] {\n    ");
            for (int i = 0; i < 64; i++)
            {
                sb.Append("0x").Append(magics[i].ToString("X16")).Append("UL");
                sb.Append(i == 63 ? "\n" : (i % 8 == 7 ? ",\n    " : ", "));
            }
            sb.Append("};\n");
            Console.WriteLine(sb.ToString());
        }
#endif

        // =========================
        //  Core helpers
        // =========================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PopCount(ulong x) => BitOperations.PopCount(x);

        // Enumerate subsets of mask in LSB order; idx's ith bit toggles ith set-bit of mask
        private static ulong IndexToOccupancy(int idx, int bits, ulong mask)
        {
            ulong occ = 0UL;
            for (int i = 0; i < bits; i++)
            {
                int sq = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1; // pop LSB
                if (((idx >> i) & 1) != 0)
                    occ |= 1UL << sq;
            }
            return occ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MagicIndex(ulong occ, ulong mask, ulong magic, int bits)
        {
            unchecked
            {
                return (int)(((occ & mask) * magic) >> (64 - bits));
            }
        }

        // Reference ray tracer used for table fill & debug
        private static ulong ComputeSliderAttacks(int sq, ulong occ, bool bishop)
        {
            ulong attacks = 0UL;
            int rk = sq / 8, fl = sq % 8;
            int[] drs, dfs;
            if (bishop)
            {
                drs = new[] { +1, +1, -1, -1 };
                dfs = new[] { +1, -1, +1, -1 };
            }
            else
            {
                drs = new[] { +1, -1, 0, 0 };
                dfs = new[] { 0, 0, +1, -1 };
            }

            for (int d = 0; d < 4; d++)
            {
                int r = rk + drs[d], f = fl + dfs[d];
                while (r >= 0 && r < 8 && f >= 0 && f < 8)
                {
                    int s = r * 8 + f;
                    attacks |= 1UL << s;
                    if ((occ & (1UL << s)) != 0) break;
                    r += drs[d];
                    f += dfs[d];
                }
            }
            return attacks;
        }
    }
}
