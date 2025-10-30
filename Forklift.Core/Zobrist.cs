using System.Security.Cryptography;

namespace Forklift.Core;

// Immutable bundle of keys. Generate once, then share safely.
public sealed class Zobrist
{
    public readonly ulong[,] PieceSquare; // [12, 64]
    public readonly ulong SideToMove;
    public readonly ulong[] Castling;     // size 16 (bitmask state -> key)
    public readonly ulong[] EnPassant;    // size 8 (file -> key)

    private Zobrist(ulong[,] pieceSquare, ulong sideToMove, ulong[] castling, ulong[] enPassant)
    {
        PieceSquare = pieceSquare; SideToMove = sideToMove; Castling = castling; EnPassant = enPassant;
    }

    public static Zobrist CreateDeterministic(int seed = 123456789)
    {
        var rng = new Random(seed);
        ulong Next() { unchecked { return ((ulong)rng.Next() << 32) ^ (uint)rng.Next(); } }

        var pieceSquare = new ulong[12, 64];
        for (int piece = 0; piece < 12; piece++)
        {
            for (int square = 0; square < 64; square++)
            {
                pieceSquare[piece, square] = Next();
            }
        }

        var castling = new ulong[16];
        for (int i = 0; i < castling.Length; i++)
        {
            castling[i] = Next();
        }

        var enPassant = new ulong[8];
        for (int f = 0; f < 8; f++) 
        { 
            enPassant[f] = Next(); 
        }

        return new Zobrist(pieceSquare, Next(), castling, enPassant);
    }
}
