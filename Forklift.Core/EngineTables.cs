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
        public readonly ulong[] WhitePawnAttackTable;  // [target64] bitboard of sources
        public readonly ulong[] BlackPawnAttackTable;  // [target64] bitboard of sources

        // Pawn push-from masks keyed by from 0x64
        public readonly ulong[] WhitePawnPushFrom;    // [from64] bitboard of sources
        public readonly ulong[] BlackPawnPushFrom;    // [from64] bitboard of sources
        public readonly ulong[] WhitePawnAttackFrom;  // [from64] bitboard of sources
        public readonly ulong[] BlackPawnAttackFrom;  // [from64] bitboard of sources

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
            ulong[] whitePawnAttackTable,
            ulong[] blackPawnAttackTable,
            ulong[] wpPushFrom,
            ulong[] bpPushFrom,
            ulong[] wpAttackFrom,
            ulong[] bpAttackFrom,
            int[] bishopOffsets,
            ulong[] bishopTable,
            int[] rookOffsets,
            ulong[] rookTable,
            Zobrist zobrist)
        {
            KnightAttackTable = knightAttackTable;
            KingAttackTable = kingAttackTable;
            WhitePawnAttackTable = whitePawnAttackTable;
            BlackPawnAttackTable = blackPawnAttackTable;
            WhitePawnPushFrom = wpPushFrom;
            BlackPawnPushFrom = bpPushFrom;
            WhitePawnAttackFrom = wpAttackFrom;
            BlackPawnAttackFrom = bpAttackFrom;
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
#if FKLIFT_BAKE
#warning FKLIFT_BAKE is active in Forklift.Core
            // Regenerate magics and build packed tables for authoring/bake runs.
            EnsureMagicsReady(out var activeBishopMagics, out var activeRookMagics);
            CurrentBishopMagics = activeBishopMagics;
            CurrentRookMagics   = activeRookMagics;

            // Build offsets
            var bishopOffsets = new int[65];
            var rookOffsets   = new int[65];
            int bishopTotal = 0, rookTotal = 0;
            for (int sq = 0; sq < 64; sq++)
            {
                int bLen = 1 << BishopIndexBits[sq];
                int rLen = 1 << RookIndexBits[sq];
                bishopOffsets[sq] = bishopTotal; bishopTotal += bLen;
                rookOffsets[sq]   = rookTotal;   rookTotal   += rLen;
            }
            bishopOffsets[64] = bishopTotal;
            rookOffsets[64]   = rookTotal;

            var bishopTable = new ulong[bishopTotal];
            var rookTable   = new ulong[rookTotal];
            for (int sq = 0; sq < 64; sq++)
            {
                var bArr = GenerateMagicAttackTable(sq, bishop: true,  CurrentBishopMagics);
                var rArr = GenerateMagicAttackTable(sq, bishop: false, CurrentRookMagics);
                Array.Copy(bArr, 0, bishopTable, bishopOffsets[sq], bArr.Length);
                Array.Copy(rArr, 0, rookTable,   rookOffsets[sq],   rArr.Length);
            }

            // Build (or optionally bake) attack-from tables:
            var knightFrom = BuildKnightFrom();
            var kingFrom = BuildKingFrom();
            var wpAttackTable = BuildWhitePawnAttackTable();
            var bpAttackTable = BuildBlackPawnAttackTable();
            var wpPushFrom = BuildWhitePawnPushFrom();
            var bpPushFrom = BuildBlackPawnPushFrom();
            var wpAttackFrom = BuildWhitePawnAttackFrom();
            var bpAttackFrom = BuildBlackPawnAttackFrom();

            MaybeWriteBakedToFile(bishopOffsets, bishopTable, rookOffsets, rookTable, CurrentBishopMagics, CurrentRookMagics);

            return new EngineTables(
                knightFrom,
                kingFrom,
                wpAttackTable,
                bpAttackTable,
                wpPushFrom,
                bpPushFrom,
                wpAttackFrom,
                bpAttackFrom,
                bishopOffsets,
                bishopTable,
                rookOffsets,
                rookTable,
                zobrist ?? Zobrist.CreateDeterministic());

#else
            // Zero startup work: use baked magics, offsets, and packed tables.
            CurrentBishopMagics = BishopMagics;
            CurrentRookMagics = RookMagics;

            // Compute attack-from tables with the per-piece builders (lightweight and deterministic)
            var knightFrom = BuildKnightFrom();
            var kingFrom = BuildKingFrom();
            var wpAttackTable = BuildWhitePawnAttackTable();
            var bpAttackTable = BuildBlackPawnAttackTable();
            var wpPushFrom = BuildWhitePawnPushFrom();
            var bpPushFrom = BuildBlackPawnPushFrom();
            var wpAttackFrom = BuildWhitePawnAttackFrom();
            var bpAttackFrom = BuildBlackPawnAttackFrom();

            return new EngineTables(
                knightFrom,
                kingFrom,
                wpAttackTable,
                bpAttackTable,
                wpPushFrom,
                bpPushFrom,
                wpAttackFrom,
                bpAttackFrom,
                BakedBishopOffsets,
                BakedBishopTable,
                BakedRookOffsets,
                BakedRookTable,
                zobrist ?? Zobrist.CreateDeterministic());
#endif
        }



        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildWhitePawnPushFrom()
        {
            // From a square f, white pawns can push to f+16 (if on-board)
            var table = new ulong[64];

            for (int f = 0; f < 56; f++)
            {
                var f88 = (Square0x88)new Square0x64(f);
                ulong mask = 0UL;

                var to = new UnsafeSquare0x88(f88.Value + 16);
                if (!Squares.IsOffboard(to))
                    mask |= 1UL << ((Square0x64)(Square0x88)to).Value;

                table[f] = mask;
            }
            for (int f = 56; f < 64; f++)
            {
                table[f] = 0UL;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildBlackPawnPushFrom()
        {
            // From a square f, black pawns can push to f-16 (if on-board)
            var table = new ulong[64];

            for (int f = 0; f < 8; f++)
            {
                table[f] = 0UL;
            }
            for (int f = 8; f < 64; f++)
            {
                var f88 = (Square0x88)new Square0x64(f);
                ulong mask = 0UL;

                var to = new UnsafeSquare0x88(f88.Value - 16);
                if (!Squares.IsOffboard(to))
                    mask |= 1UL << ((Square0x64)(Square0x88)to).Value;

                table[f] = mask;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildWhitePawnAttackFrom()
        {
            // From a square f, white pawns can attack f+15 and f+17 (if on-board)
            var table = new ulong[64];

            for (int f = 0; f < 64; f++)
            {
                var f88 = (Square0x88)new Square0x64(f);
                ulong mask = 0UL;

                var toL = new UnsafeSquare0x88(f88.Value + 15);
                if (!Squares.IsOffboard(toL))
                    mask |= 1UL << ((Square0x64)(Square0x88)toL).Value;

                var toR = new UnsafeSquare0x88(f88.Value + 17);
                if (!Squares.IsOffboard(toR))
                    mask |= 1UL << ((Square0x64)(Square0x88)toR).Value;

                table[f] = mask;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildBlackPawnAttackFrom()
        {
            // From a square f, black pawns can attack f-15 and f-17 (if on-board)
            var table = new ulong[64];

            for (int f = 0; f < 64; f++)
            {
                var f88 = (Square0x88)new Square0x64(f);
                ulong mask = 0UL;

                var toL = new UnsafeSquare0x88(f88.Value - 15);
                if (!Squares.IsOffboard(toL))
                    mask |= 1UL << ((Square0x64)(Square0x88)toL).Value;

                var toR = new UnsafeSquare0x88(f88.Value - 17);
                if (!Squares.IsOffboard(toR))
                    mask |= 1UL << ((Square0x64)(Square0x88)toR).Value;

                table[f] = mask;
            }
            return table;
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

        private static void PrecomputeAttackFrom(ulong[] knight, ulong[] king, ulong[] wp, ulong[] bp)
        {
            ReadOnlySpan<int> KNIGHT = stackalloc[] { +33,+31,+18,+14,-14,-18,-31,-33 };
            ReadOnlySpan<int> KING   = stackalloc[] { +1,-1,+16,-16,+15,+17,-15,-17 };
            const int W_L=+15, W_R=+17, B_L=-15, B_R=-17;

            for (UnsafeSquare0x88 t88 = (UnsafeSquare0x88)0; t88.Value < 128; t88++)
            {
                if (Squares.IsOffboard(t88)) continue;
                var t64 = (Square0x64)(Square0x88)t88;

                ulong kmask=0, Kmask=0, wpmask=0, bpmask=0;
                foreach (var d in KNIGHT){ var f = new UnsafeSquare0x88(t88.Value - d); if (!Squares.IsOffboard(f)) kmask |= 1UL << (int)(Square0x64)(Square0x88)f; }
                foreach (var d in KING)  { var f = new UnsafeSquare0x88(t88.Value - d); if (!Squares.IsOffboard(f)) Kmask |= 1UL << (int)(Square0x64)(Square0x88)f; }
                { var f=new UnsafeSquare0x88(t88.Value - W_L); if(!Squares.IsOffboard(f)) wpmask |= 1UL << (int)(Square0x64)(Square0x88)f; }
                { var f=new UnsafeSquare0x88(t88.Value - W_R); if(!Squares.IsOffboard(f)) wpmask |= 1UL << (int)(Square0x64)(Square0x88)f; }
                { var f=new UnsafeSquare0x88(t88.Value - B_L); if(!Squares.IsOffboard(f)) bpmask |= 1UL << (int)(Square0x64)(Square0x88)f; }
                { var f=new UnsafeSquare0x88(t88.Value - B_R); if(!Squares.IsOffboard(f)) bpmask |= 1UL << (int)(Square0x64)(Square0x88)f; }

                knight[(int)t64]=kmask; king[(int)t64]=Kmask; wp[(int)t64]=wpmask; bp[(int)t64]=bpmask;
            }
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildKnightFrom()
        {
            // Knight attack-from masks keyed by target (0x64)
            ReadOnlySpan<int> DELTAS = stackalloc int[] { +33, +31, +18, +14, -14, -18, -31, -33 };
            var table = new ulong[64];

            for (int t = 0; t < 64; t++)
            {
                var t88 = (Square0x88)new Square0x64(t);
                ulong mask = 0UL;

                for (int i = 0; i < DELTAS.Length; i++)
                {
                    var from = new UnsafeSquare0x88(t88.Value - DELTAS[i]);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        mask |= 1UL << s64.Value;
                    }
                }
                table[t] = mask;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildKingFrom()
        {
            // King attack-from masks keyed by target (0x64)
            ReadOnlySpan<int> DELTAS = [+1, -1, +16, -16, +15, +17, -15, -17];
            var table = new ulong[64];

            for (int t = 0; t < 64; t++)
            {
                var t88 = (Square0x88)new Square0x64(t);
                ulong mask = 0UL;

                for (int i = 0; i < DELTAS.Length; i++)
                {
                    var from = new UnsafeSquare0x88(t88.Value - DELTAS[i]);
                    if (!Squares.IsOffboard(from))
                    {
                        var s64 = (Square0x64)(Square0x88)from;
                        mask |= 1UL << s64.Value;
                    }
                }
                table[t] = mask;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildWhitePawnAttackTable()
        {
            // For a target t, white pawns that could attack t are at t-15 and t-17 (if on-board)
            var table = new ulong[64];

            for (int t = 0; t < 64; t++)
            {
                var t88 = (Square0x88)new Square0x64(t);
                ulong mask = 0UL;

                var fromL = new UnsafeSquare0x88(t88.Value - 15);
                if (!Squares.IsOffboard(fromL))
                    mask |= 1UL << ((Square0x64)(Square0x88)fromL).Value;

                var fromR = new UnsafeSquare0x88(t88.Value - 17);
                if (!Squares.IsOffboard(fromR))
                    mask |= 1UL << ((Square0x64)(Square0x88)fromR).Value;

                table[t] = mask;
            }
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong[] BuildBlackPawnAttackTable()
        {
            // For a target t, black pawns that could attack t are at t+15 and t+17 (if on-board)
            var table = new ulong[64];

            for (int t = 0; t < 64; t++)
            {
                var t88 = (Square0x88)new Square0x64(t);
                ulong mask = 0UL;

                var fromL = new UnsafeSquare0x88(t88.Value + 15);
                if (!Squares.IsOffboard(fromL))
                    mask |= 1UL << ((Square0x64)(Square0x88)fromL).Value;

                var fromR = new UnsafeSquare0x88(t88.Value + 17);
                if (!Squares.IsOffboard(fromR))
                    mask |= 1UL << ((Square0x64)(Square0x88)fromR).Value;

                table[t] = mask;
            }
            return table;
        }

        // Write baked arrays to a .cs file when FKLIFT_BAKE=1 is set.
        // Optional: FKLIFT_BAKE_PATH overrides the output path (default: EngineTables.Baked.cs)
        private static void MaybeWriteBakedToFile(
            int[] bishopOffsets, ulong[] bishopTable,
            int[] rookOffsets, ulong[] rookTable,
            ulong[] bishopMagics, ulong[] rookMagics)
        {
            string? envPath = Environment.GetEnvironmentVariable("FKLIFT_BAKE_PATH");
            string path;
            if (!string.IsNullOrEmpty(envPath) &&
                !envPath.Contains("/") && !envPath.Contains("\\") && !envPath.Contains(".."))
            {
                path = envPath;
            }
            else
            {
                path = "EngineTables.Baked.cs";
            }
            var sb = new System.Text.StringBuilder(capacity: 1 << 22); // pre-size a bit (few MB)

            // Header
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// This file was generated by FKLIFT_BAKE. Do not edit by hand.");
            sb.AppendLine("// It contains baked magics, offsets, and packed attack tables.");
            sb.AppendLine("// Rebuild with FKLIFT_BAKE=1 to regenerate.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("#if !FKLIFT_BAKE");
            sb.AppendLine("namespace Forklift.Core");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed partial class EngineTables");
            sb.AppendLine("    {");

            // Helpers with strict indentation
            static void DumpU64(System.Text.StringBuilder b, string name, ulong[] data, int valuesPerLine = 4)
            {
                const int headerIndent = 8;   // spaces before "private static ..."
                const int valueIndent = 12;  // spaces before values

                string hdrPad = new string(' ', headerIndent);
                string valPad = new string(' ', valueIndent);

                b.Append(hdrPad).Append("private static readonly ulong[] ").Append(name)
                .Append(" = [").AppendLine();

                for (int i = 0; i < data.Length; i++)
                {
                    if (i % valuesPerLine == 0) b.Append(valPad);
                    b.Append("0x").Append(data[i].ToString("X16")).Append("UL");
                    if (i != data.Length - 1) b.Append(", ");
                    if (i % valuesPerLine == valuesPerLine - 1 || i == data.Length - 1) b.AppendLine();
                }

                b.Append(hdrPad).AppendLine("];");
                b.AppendLine();
            }

            static void DumpI32(System.Text.StringBuilder b, string name, int[] data, int valuesPerLine = 16)
            {
                const int headerIndent = 8;   // spaces before "private static ..."
                const int valueIndent = 12;  // spaces before values

                string hdrPad = new string(' ', headerIndent);
                string valPad = new string(' ', valueIndent);

                b.Append(hdrPad).Append("private static readonly int[] ").Append(name)
                .Append(" = [").AppendLine();

                for (int i = 0; i < data.Length; i++)
                {
                    if (i % valuesPerLine == 0) b.Append(valPad);
                    b.Append(data[i]);
                    if (i != data.Length - 1) b.Append(", ");
                    if (i % valuesPerLine == valuesPerLine - 1 || i == data.Length - 1) b.AppendLine();
                }

                b.Append(hdrPad).AppendLine("];");
                b.AppendLine();
            }

            // Emit counts as constants (handy sanity checks in DEBUG)
            sb.AppendLine("        // Packed table totals for sanity checks");
            sb.Append("        private const int BakedBishopTableTotal = ").Append(bishopTable.Length).AppendLine(";");
            sb.Append("        private const int BakedRookTableTotal   = ").Append(rookTable.Length).AppendLine(";");
            sb.AppendLine();

            // Emit arrays
            DumpU64(sb, "BishopMagics", bishopMagics);
            DumpU64(sb, "RookMagics", rookMagics);

            DumpI32(sb, "BakedBishopOffsets", bishopOffsets);
            DumpI32(sb, "BakedRookOffsets", rookOffsets);

            // Packed tables (these remove startup work entirely)
            DumpU64(sb, "BakedBishopTable", bishopTable);
            DumpU64(sb, "BakedRookTable", rookTable);

            // Footer
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("#endif");

            System.IO.File.WriteAllText(path, sb.ToString());
        }
    }
}
