using System;

namespace Forklift.Core
{
    // Immutable bundle of keys. Generate once, then share safely.
    public sealed class Zobrist
    {
        public readonly ulong[,] PieceSquare; // [12, 64]
        public readonly ulong SideToMove;
        public readonly ulong[] Castling;     // size 16 (bitmask state -> key)
        public readonly ulong[] EnPassant;    // size 8 (file -> key)

        private Zobrist(ulong[,] pieceSquare, ulong sideToMove, ulong[] castling, ulong[] enPassant)
        {
            PieceSquare = pieceSquare;
            SideToMove = sideToMove;
            Castling = castling;
            EnPassant = enPassant;
        }

        /// <summary>
        /// Deterministic SplitMix64-based generator for high-quality 64-bit keys.
        /// </summary>
        private struct SplitMix64
        {
            private ulong state;
            public SplitMix64(ulong seed) => state = seed;

            public ulong Next()
            {
                ulong z = state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            public ulong NextNonZero()
            {
                ulong x;
                do { x = Next(); } while (x == 0UL);
                return x;
            }
        }

        public static Zobrist CreateDeterministic(int seed = 123456789)
        {
            // Seed widening to 64-bit
            var gen = new SplitMix64(unchecked((ulong)seed) ^ 0xD1B54A32D192ED03UL);

            var pieceSquare = new ulong[12, 64];
            for (int p = 0; p < 12; p++)
                for (int s = 0; s < 64; s++)
                    pieceSquare[p, s] = gen.NextNonZero();

            var castling = new ulong[16];
            for (int i = 0; i < castling.Length; i++)
                castling[i] = gen.NextNonZero();

            var enPassant = new ulong[8];
            for (int f = 0; f < 8; f++)
                enPassant[f] = gen.NextNonZero();

            var sideToMove = gen.NextNonZero();

            return new Zobrist(pieceSquare, sideToMove, castling, enPassant);
        }
    }
}
