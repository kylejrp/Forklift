namespace Forklift.Core;

public enum Piece : sbyte
{
    Empty = 0,
    WhitePawn = 1, WhiteKnight = 2, WhiteBishop = 3, WhiteRook = 4, WhiteQueen = 5, WhiteKing = 6,
    BlackPawn = 7, BlackKnight = 8, BlackBishop = 9, BlackRook = 10, BlackQueen = 11, BlackKing = 12
}

public static class PieceUtil
{
    public static bool IsWhite(Piece p) => p >= Piece.WhitePawn && p <= Piece.WhiteKing;
    public static bool IsBlack(Piece p) => p >= Piece.BlackPawn && p <= Piece.BlackKing;
    public static int Index(Piece p) => ((int)p) - 1; // 0..11 for non-empty
}
