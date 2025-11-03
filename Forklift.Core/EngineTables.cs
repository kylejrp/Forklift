using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public sealed partial class EngineTables
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
        public readonly int[] BishopOffsets;  // length 65, last = total
        public readonly int[] RookOffsets;    // length 65, last = total
        public readonly ulong[] BishopTable;    // flat table
        public readonly ulong[] RookTable;      // flat table
        public static readonly byte[] BishopIndexBits = new byte[64];
        public static readonly byte[] RookIndexBits = new byte[64];

        public readonly Zobrist Zobrist;

        // =========================
        //  Magic constants (active)
        // =========================

        // ACTIVE arrays actually used by both build & lookup (point to baked or regenerated).
        private static ulong[] CurrentBishopMagics = Array.Empty<ulong>();
        private static ulong[] CurrentRookMagics = Array.Empty<ulong>();

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
            if (zobrist == null) return _defaultInstance.Value;
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
                int bLen = 1 << BishopIndexBits[sq];
                int rLen = 1 << RookIndexBits[sq];
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

            // Precompute “attack-from” tables (target-centric)
            ReadOnlySpan<int> KNIGHT = stackalloc int[] { +33, +31, +18, +14, -14, -18, -31, -33 };
            ReadOnlySpan<int> KING = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };
            const int W_PAWN_L = +15, W_PAWN_R = +17;
            const int B_PAWN_L = -15, B_PAWN_R = -17;

            for (UnsafeSquare0x88 t88 = (UnsafeSquare0x88)0; t88.Value < 128; t88++)
            {
                if (Squares.IsOffboard(t88)) continue;

                var t64 = (Square0x64)(Square0x88)t88;
                ulong kmask = 0, Kmask = 0, wpmask = 0, bpmask = 0;

                foreach (int d in KNIGHT)
                {
                    var from = new UnsafeSquare0x88(t88.Value - d);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        kmask |= 1UL << (int)s64;
                    }
                }

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
            }

#if DEBUG
            // --- DEBUG SANITY CHECKS ---
            for (int sq = 0; sq < 64; sq++)
            {
                int bBits = BishopIndexBits[sq];
                int rBits = RookIndexBits[sq];
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
                    if (bishopTable[bishopOffsets[sq] + i] != 0) { bHas = true; break; }
                for (int i = 0; i < rLen; i++)
                    if (rookTable[rookOffsets[sq] + i] != 0) { rHas = true; break; }
                System.Diagnostics.Debug.Assert(bHas, $"[DEBUG] BishopTable empty at square {sq}");
                System.Diagnostics.Debug.Assert(rHas, $"[DEBUG] RookTable empty at square {sq}");
            }
#endif

#if FKLIFT_BAKE
            // After tables are built and Current*Magics are set:
            MaybeWriteBakedToFile(
                bishopOffsets, bishopTable,
                rookOffsets, rookTable,
                CurrentBishopMagics, CurrentRookMagics);
#endif

            return new EngineTables(
                knightAttackTable,
                kingAttackTable,
                whitePawnAttackFrom,
                blackPawnAttackFrom,
                bishopOffsets,
                bishopTable,
                rookOffsets,
                rookTable,
                zobrist ?? Zobrist.CreateDeterministic());
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
                        if (r == 0 || r == 7 || f == 0 || f == 7) break; // exclude edge
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
            int bits = bishop ? BishopIndexBits[sq] : RookIndexBits[sq];
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
                        $"Magic collision at sq {sq} ({(bishop ? "B" : "R")}), bits={bits}, index={mi}\nprevOcc={prevOcc:X16} newOcc={occ:X16}");
                seen[mi] = occ;
#endif
                table[mi] = ComputeSliderAttacks(sq, occ, bishop);
            }

            return table;
        }

        // =========================
        //  Magics selection / regeneration
        // =========================

        private static void EnsureMagicsReady(out ulong[] bishopMagics, out ulong[] rookMagics)
        {
#if FKLIFT_BAKE
            // In a FKLIFT_BAKE build, baked arrays are excluded (#if !FKLIFT_BAKE),
            // so always regenerate magics here and optionally write them later.
            bishopMagics = new ulong[64];
            rookMagics   = new ulong[64];
            for (int sq = 0; sq < 64; sq++)
            {
                bishopMagics[sq] = FindMagicForSquare(sq, true,  BishopMasks[sq], BishopIndexBits[sq]);
                rookMagics[sq]   = FindMagicForSquare(sq, false, RookMasks[sq],  RookIndexBits[sq]);
            }
#else
            // Normal builds: use the baked constants from EngineTables.Baked.cs
            bishopMagics = BishopMagics;
            rookMagics = RookMagics;
#endif
        }

#if FKLIFT_BAKE
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
                    int idx = (int)(((occs[i] & mask) * magic) >> (64 - bits));
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

        [System.Diagnostics.Conditional("FKLIFT_BAKE")]
        private static void DumpMagicsToConsole(string name, ulong[] magics)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("private static readonly ulong[] ").Append(name).Append(" = [\n    ");
            for (int i = 0; i < 64; i++)
            {
                sb.Append("0x").Append(magics[i].ToString("X16")).Append("UL");
                sb.Append(i == 63 ? "\n" : (i % 8 == 7 ? ",\n    " : ", "));
            }
            sb.Append("];\n");
            Console.WriteLine(sb.ToString());
        }

        [System.Diagnostics.Conditional("FKLIFT_BAKE")]
        public static void DumpAttackTablesToConsole(int[] bishopOffsets, ulong[] bishopTable, int[] rookOffsets, ulong[] rookTable)
        {
            var sb = new System.Text.StringBuilder();
            // BishopOffsets
            sb.Append("private static readonly int[] BishopOffsets = [").Append(bishopOffsets.Length).Append("] {\n    ");
            for (int i = 0; i < bishopOffsets.Length; i++)
            {
                sb.Append(bishopOffsets[i]);
                sb.Append(i == bishopOffsets.Length - 1 ? "\n" : (i % 8 == 7 ? ",\n    " : ", "));
            }
            sb.Append("};\n\n");

            // BishopTable
            sb.Append("private static readonly ulong[] BishopTable = [").Append(bishopTable.Length).Append("] {\n    ");
            for (int i = 0; i < bishopTable.Length; i++)
            {
                sb.Append("0x").Append(bishopTable[i].ToString("X16")).Append("UL");
                sb.Append(i == bishopTable.Length - 1 ? "\n" : (i % 4 == 3 ? ",\n    " : ", "));
            }
            sb.Append("];\n\n");

            // RookOffsets
            sb.Append("private static readonly int[] RookOffsets = [").Append(rookOffsets.Length).Append("] {\n    ");
            for (int i = 0; i < rookOffsets.Length; i++)
            {
                sb.Append(rookOffsets[i]);
                sb.Append(i == rookOffsets.Length - 1 ? "\n" : (i % 8 == 7 ? ",\n    " : ", "));
            }
            sb.Append("};\n\n");

            // RookTable
            sb.Append("private static readonly ulong[] RookTable = [").Append(rookTable.Length).Append("] {\n    ");
            for (int i = 0; i < rookTable.Length; i++)
            {
                sb.Append("0x").Append(rookTable[i].ToString("X16")).Append("UL");
                sb.Append(i == rookTable.Length - 1 ? "\n" : (i % 4 == 3 ? ",\n    " : ", "));
            }
            sb.Append("];\n\n");

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

            ReadOnlySpan<int> drs = bishop
                ? stackalloc int[4] { +1, +1, -1, -1 }
                : stackalloc int[4] { +1, -1, 0, 0 };
            ReadOnlySpan<int> dfs = bishop
                ? stackalloc int[4] { +1, -1, +1, -1 }
                : stackalloc int[4] { 0, 0, +1, -1 };

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

        // Write baked arrays to a .cs file when FKLIFT_BAKE=1 is set.
        // Optional: FKLIFT_BAKE_PATH overrides the output path (default: EngineTables.Baked.cs)
        private static void MaybeWriteBakedToFile(
            int[] bishopOffsets, ulong[] /*unused*/ _bishopTable,
            int[] rookOffsets, ulong[] /*unused*/ _rookTable,
            ulong[] bishopMagics, ulong[] rookMagics)
        {
            string path = Environment.GetEnvironmentVariable("FKLIFT_BAKE_PATH") ?? "EngineTables.Baked.cs";
            var sb = new System.Text.StringBuilder();

            // Header
            sb.AppendLine("#if !FKLIFT_BAKE");
            sb.AppendLine("namespace Forklift.Core");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed partial class EngineTables");
            sb.AppendLine("    {");

            // Helpers with strict indentation
            static void DumpU64(System.Text.StringBuilder b, string name, ulong[] data)
            {
                const int headerIndent = 8;   // spaces before "private static ..."
                const int valueIndent = 12;  // spaces before values
                const int perLine = 4;   // 4 per line for ULONGlists

                string hdrPad = new string(' ', headerIndent);
                string valPad = new string(' ', valueIndent);

                b.Append(hdrPad).Append("private static readonly ulong[] ").Append(name)
                 .Append(" = new ulong[64] {").AppendLine();

                for (int i = 0; i < data.Length; i++)
                {
                    if (i % perLine == 0) b.Append(valPad);
                    b.Append("0x").Append(data[i].ToString("X16")).Append("UL");
                    if (i != data.Length - 1) b.Append(", ");
                    if (i % perLine == perLine - 1 || i == data.Length - 1) b.AppendLine();
                }

                b.Append(hdrPad).AppendLine("};");
                b.AppendLine();
            }

            static void DumpI32(System.Text.StringBuilder b, string name, int[] data)
            {
                const int headerIndent = 8;   // spaces before "private static ..."
                const int valueIndent = 12;  // spaces before values
                const int perLine = 16;  // 16 per line for INT lists

                string hdrPad = new string(' ', headerIndent);
                string valPad = new string(' ', valueIndent);

                b.Append(hdrPad).Append("private static readonly int[] ").Append(name)
                 .Append(" = new int[").Append(data.Length).Append("] {").AppendLine();

                for (int i = 0; i < data.Length; i++)
                {
                    if (i % perLine == 0) b.Append(valPad);
                    b.Append(data[i]);
                    if (i != data.Length - 1) b.Append(", ");
                    if (i % perLine == perLine - 1 || i == data.Length - 1) b.AppendLine();
                }

                b.Append(hdrPad).AppendLine("};");
                b.AppendLine();
            }

            DumpU64(sb, "BishopMagics", bishopMagics);
            DumpU64(sb, "RookMagics", rookMagics);
            DumpI32(sb, "BakedBishopOffsets", bishopOffsets);
            DumpI32(sb, "BakedRookOffsets", rookOffsets);

            // Footer
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("#endif");

            System.IO.File.WriteAllText(path, sb.ToString());
        }
    }
}
