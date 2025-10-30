namespace Forklift.Core;

public static class Squares
{
    // 0x88 helpers (pure; thread-safe)
    public static bool IsOffboard(int square0x88) => (square0x88 & 0x88) != 0;
    public static int ConvertTo0x64Index(int square0x88) => ((square0x88 & 0xF) + ((square0x88 >> 4) * 8));
    public static int ConvertTo0x88Index(int square0x64) => ((square0x64 >> 3) << 4) | (square0x64 & 7);

    public static int ParseAlgebraicTo0x88(string algebraicNotation)
    {
        if (algebraicNotation is null || algebraicNotation.Length != 2) 
            throw new ArgumentException("Invalid square notation");

        int fileIndex = algebraicNotation[0] - 'a';
        int rankIndex = algebraicNotation[1] - '1';
        return (rankIndex << 4) | fileIndex;
    }

    public static string ToAlgebraic(int square0x88)
    {
        if (IsOffboard(square0x88))
            throw new ArgumentException("Offboard square");

        int file = square0x88 & 0xF;    // low nibble
        int rank = square0x88 >> 4;     // high nibble

        char fileChar = (char)('a' + file);
        char rankChar = (char)('1' + rank);

        return $"{fileChar}{rankChar}";
    }

}
