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
/// Interned: every square reuses one of 64 prebuilt instances.
/// </summary>
public readonly struct AlgebraicNotation
{
    public string Value { get; }

    // Private ctor: only the cache builds these.
    private AlgebraicNotation(string value) => Value = value;

    // 64 singletons, indexed by rank*8 + file (rank/file are 0..7)
    private static readonly AlgebraicNotation[] Cache = BuildCache();
    private static AlgebraicNotation[] BuildCache()
    {
        var arr = new AlgebraicNotation[64];
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
            {
                var s = new string(new[] { (char)('a' + f), (char)('1' + r) });
                arr[(r << 3) | f] = new AlgebraicNotation(s);
            }
        return arr;
    }

    // Factory: from "e4" style strings (returns cached instance)
    public static ref readonly AlgebraicNotation From(string value)
    {
        if (value is null || value.Length != 2) throw new ArgumentException("Invalid algebraic notation.", nameof(value));
        int f = value[0] - 'a';
        int r = value[1] - '1';
        if ((uint)f > 7 || (uint)r > 7) throw new ArgumentException("Invalid algebraic notation.", nameof(value));
        return ref Cache[(r << 3) | f];
    }

    // Factories: from square indices (also cached)
    public static ref readonly AlgebraicNotation From(Square0x88 sq88)
    {
        int f = sq88.Value & 0xF;
        int r = sq88.Value >> 4;
        return ref Cache[(r << 3) | f];
    }
    public static ref readonly AlgebraicNotation From(Square0x64 sq64)
    {
        int idx = sq64.Value; // 0..63
        return ref Cache[idx];
    }

    public override string ToString() => Value;
}


public static class Squares
{
    public static bool IsOffboard(UnsafeSquare0x88 square) => (square.Value & 0x88) != 0;

    public static Square0x64 ConvertTo0x64Index(Square0x88 square)
        => new Square0x64((square.Value & 0xF) + ((square.Value >> 4) * 8));

    public static Square0x88 ConvertTo0x88Index(Square0x64 square)
        => new Square0x88(((square.Value >> 3) << 4) | (square.Value & 7));

    public static Square0x64 ParseAlgebraicTo0x64(string algebraic)
    {
        ref readonly var a = ref AlgebraicNotation.From(algebraic);
        int f = a.Value[0] - 'a';
        int r = a.Value[1] - '1';
        return new Square0x64(r * 8 + f);
    }

    public static Square0x64 ParseAlgebraicTo0x64(AlgebraicNotation a)
    {
        int f = a.Value[0] - 'a';
        int r = a.Value[1] - '1';
        return new Square0x64(r * 8 + f);
    }

    public static Square0x88 ParseAlgebraicTo0x88(string algebraic)
    {
        ref readonly var a = ref AlgebraicNotation.From(algebraic);
        int f = a.Value[0] - 'a';
        int r = a.Value[1] - '1';
        return new Square0x88((r << 4) | f);
    }

    public static Square0x88 ParseAlgebraicTo0x88(AlgebraicNotation a)
    {
        int f = a.Value[0] - 'a';
        int r = a.Value[1] - '1';
        return new Square0x88((r << 4) | f);
    }

    // No string interpolation here—just return the interned instance
    public static AlgebraicNotation ToAlgebraic(Square0x88 square) => AlgebraicNotation.From(square);
    public static AlgebraicNotation ToAlgebraic(Square0x64 square) => AlgebraicNotation.From(square);
}
