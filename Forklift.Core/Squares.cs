namespace Forklift.Core;

public readonly struct UnsafeSquare0x64
{
    public int Value { get; }
    public UnsafeSquare0x64(int value)
    {
        Value = value;
    }
    public static implicit operator int(UnsafeSquare0x64 square) => square.Value;
    public static explicit operator UnsafeSquare0x64(int value) => new UnsafeSquare0x64(value);
    public static explicit operator UnsafeSquare0x64(Square0x64 square) => new UnsafeSquare0x64(square.Value);
    public static explicit operator Square0x64(UnsafeSquare0x64 square) => new Square0x64(square.Value);
    public static explicit operator UnsafeSquare0x64(UnsafeSquare0x88 square)
    {
        int value = (square.Value & 0xF) + ((square.Value >> 4) * 8);
        return new UnsafeSquare0x64(value);
    }

    public static UnsafeSquare0x64 operator +(UnsafeSquare0x64 square, int offset)
    {
        return new UnsafeSquare0x64(square.Value + offset);
    }

    public static UnsafeSquare0x64 operator -(UnsafeSquare0x64 square, int offset)
    {
        return new UnsafeSquare0x64(square.Value - offset);
    }

    public static UnsafeSquare0x64 operator ++(UnsafeSquare0x64 square)
    {
        return new UnsafeSquare0x64(square.Value + 1);
    }

    public static UnsafeSquare0x64 operator --(UnsafeSquare0x64 square)
    {
        return new UnsafeSquare0x64(square.Value - 1);
    }

    public override string ToString()
    {
        string algebraic = (Value >= 0 && Value < 64) ? Squares.ToAlgebraic(new Square0x64(Value)).Value : string.Empty;
        return string.IsNullOrEmpty(algebraic) ? $"(0x64 {Value})" : $"{algebraic} (0x64 {Value})";
    }
}

/// <summary>
/// Represents a square in 0x64 format.
/// </summary>
public readonly struct Square0x64
{
    public int Value { get; }

    public Square0x64(int value)
    {
        if (value < 0 || value >= 64)
            throw new ArgumentOutOfRangeException(nameof(value), "0x64 square must be in the range [0, 63].");

        Value = value;
    }

    public static explicit operator int(Square0x64 square) => square.Value;
    public static explicit operator Square0x64(int value) => new Square0x64(value);

    public static explicit operator Square0x64(Square0x88 square)
    {
        int value = (square.Value & 0xF) + ((square.Value >> 4) * 8);
        return new Square0x64(value);
    }

    public static explicit operator Square0x64(UnsafeSquare0x88 square)
    {
        int value = (square.Value & 0xF) + ((square.Value >> 4) * 8);
        return new Square0x64(value);
    }

    public static UnsafeSquare0x64 operator +(Square0x64 square, int offset)
    {
        return new UnsafeSquare0x64(square.Value + offset);
    }

    public static UnsafeSquare0x64 operator -(Square0x64 square, int offset)
    {
        return new UnsafeSquare0x64(square.Value - offset);
    }

    public override string ToString()
    {
        string algebraic = (Value >= 0 && Value < 64) ? Squares.ToAlgebraic(this).Value : string.Empty;
        return string.IsNullOrEmpty(algebraic) ? $"(0x64 {Value})" : $"{algebraic} (0x64 {Value})";
    }
}


/// <summary>
/// Represents a square in unsafe 0x88 format (no validation).
/// </summary>
public readonly struct UnsafeSquare0x88
{
    public int Value { get; }
    public UnsafeSquare0x88(int value)
    {
        Value = value;
    }
    public static explicit operator int(UnsafeSquare0x88 square) => square.Value;
    public static explicit operator UnsafeSquare0x88(int value) => new UnsafeSquare0x88(value);
    public static explicit operator UnsafeSquare0x88(Square0x88 square) => new UnsafeSquare0x88(square.Value);
    public static explicit operator Square0x88(UnsafeSquare0x88 square) => new Square0x88(square.Value);
    public static explicit operator UnsafeSquare0x88(UnsafeSquare0x64 square)
    {
        int value = ((square.Value >> 3) << 4) | (square.Value & 7);
        return new UnsafeSquare0x88(value);
    }

    public override string ToString()
    {
        string algebraic = ((Value & 0x88) == 0) ? Squares.ToAlgebraic(new Square0x88(Value)).Value : string.Empty;
        return string.IsNullOrEmpty(algebraic) ? $"(0x88 {Value})" : $"{algebraic} (0x88 {Value})";
    }

    public static UnsafeSquare0x88 operator +(UnsafeSquare0x88 square, int offset)
    {
        return new UnsafeSquare0x88(square.Value + offset);
    }

    public static UnsafeSquare0x88 operator -(UnsafeSquare0x88 square, int offset)
    {
        return new UnsafeSquare0x88(square.Value - offset);
    }

    public static UnsafeSquare0x88 operator ++(UnsafeSquare0x88 square)
    {
        return new UnsafeSquare0x88(square.Value + 1);
    }

    public static UnsafeSquare0x88 operator --(UnsafeSquare0x88 square)
    {
        return new UnsafeSquare0x88(square.Value - 1);
    }
}

/// <summary>
/// Represents a square in 0x88 format.
/// </summary>
public readonly struct Square0x88
{
    public int Value { get; }

    public Square0x88(int value)
    {
        if ((value & 0x88) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "0x88 square must not have the high nibble set.");
        }

        Value = value;
    }

    public static implicit operator int(Square0x88 square) => square.Value;
    public static explicit operator Square0x88(int value) => new Square0x88(value);

    public static explicit operator Square0x88(Square0x64 square)
    {
        int value = ((square.Value >> 3) << 4) | (square.Value & 7);
        return new Square0x88(value);
    }

    public static UnsafeSquare0x88 operator +(Square0x88 square, int offset)
    {
        return new UnsafeSquare0x88(square.Value + offset);
    }

    public static UnsafeSquare0x88 operator -(Square0x88 square, int offset)
    {
        return new UnsafeSquare0x88(square.Value - offset);
    }

    public override string ToString()
    {
        string algebraic = ((Value & 0x88) == 0) ? Squares.ToAlgebraic(this).Value : string.Empty;
        return string.IsNullOrEmpty(algebraic) ? $"(0x88 {Value})" : $"{algebraic} (0x88 {Value})";
    }
}

/// <summary>
/// Represents a square in algebraic notation (e.g., "e4").
/// </summary>
public readonly struct AlgebraicNotation
{
    public string Value { get; }

    public AlgebraicNotation(string value)
    {
        if (value is null || value.Length != 2 || value[0] < 'a' || value[0] > 'h' || value[1] < '1' || value[1] > '8')
            throw new ArgumentException("Invalid algebraic notation.", nameof(value));

        Value = value;
    }

    public override string ToString() => Value;
}

public static class Squares
{
    // 0x88 helpers (pure; thread-safe)
    public static bool IsOffboard(UnsafeSquare0x88 square) => (square.Value & 0x88) != 0;

    public static Square0x64 ConvertTo0x64Index(Square0x88 square)
    {
        int value = (square.Value & 0xF) + ((square.Value >> 4) * 8);
        return new Square0x64(value);
    }

    public static Square0x88 ConvertTo0x88Index(Square0x64 square)
    {
        int value = ((square.Value >> 3) << 4) | (square.Value & 7);
        return new Square0x88(value);
    }

    public static Square0x64 ParseAlgebraicTo0x64(AlgebraicNotation algebraicNotation)
    {
        int fileIndex = algebraicNotation.Value[0] - 'a';
        int rankIndex = algebraicNotation.Value[1] - '1';
        int value = rankIndex * 8 + fileIndex;
        return new Square0x64(value);
    }

    public static Square0x88 ParseAlgebraicTo0x88(AlgebraicNotation algebraicNotation)
    {
        int fileIndex = algebraicNotation.Value[0] - 'a';
        int rankIndex = algebraicNotation.Value[1] - '1';
        return new Square0x88((rankIndex << 4) | fileIndex);
    }

    public static AlgebraicNotation ToAlgebraic(Square0x88 square)
    {
        int file = square.Value & 0xF;    // low nibble
        int rank = square.Value >> 4;     // high nibble

        char fileChar = (char)('a' + file);
        char rankChar = (char)('1' + rank);

        return new AlgebraicNotation($"{fileChar}{rankChar}");
    }

    public static AlgebraicNotation ToAlgebraic(Square0x64 square)
    {
        return ToAlgebraic(ConvertTo0x88Index(square));
    }
}