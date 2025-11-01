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

    public static Piece FromFENChar(char c)
    {
        return c switch
        {
            'P' => Piece.WhitePawn,
            'N' => Piece.WhiteKnight,
            'B' => Piece.WhiteBishop,
            'R' => Piece.WhiteRook,
            'Q' => Piece.WhiteQueen,
            'K' => Piece.WhiteKing,
            'p' => Piece.BlackPawn,
            'n' => Piece.BlackKnight,
            'b' => Piece.BlackBishop,
            'r' => Piece.BlackRook,
            'q' => Piece.BlackQueen,
            'k' => Piece.BlackKing,
            _ => Piece.Empty
        };
    }

    public static char ToFENChar(Piece p)
    {
        return p switch
        {
            Piece.WhitePawn => 'P',
            Piece.WhiteKnight => 'N',
            Piece.WhiteBishop => 'B',
            Piece.WhiteRook => 'R',
            Piece.WhiteQueen => 'Q',
            Piece.WhiteKing => 'K',
            Piece.BlackPawn => 'p',
            Piece.BlackKnight => 'n',
            Piece.BlackBishop => 'b',
            Piece.BlackRook => 'r',
            Piece.BlackQueen => 'q',
            Piece.BlackKing => 'k',
            _ => '.'
        };
    }
}
