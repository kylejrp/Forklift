using System.Diagnostics;
using System.Runtime.CompilerServices;

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
        string algebraic = (Value >= 0 && Value < 64) ? Squares.ToAlgebraicString(new Square0x64(Value)) : string.Empty;
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
        string algebraic = (Value >= 0 && Value < 64) ? Squares.ToAlgebraicString(this) : string.Empty;
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
        string algebraic = ((Value & 0x88) == 0) ? Squares.ToAlgebraicString(new Square0x88(Value)) : string.Empty;
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
        string algebraic = ((Value & 0x88) == 0) ? Squares.ToAlgebraicString(this) : string.Empty;
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

    private AlgebraicNotation(string v) { Value = v; }

    private static readonly AlgebraicNotation[] cache = BuildCache();

    private static AlgebraicNotation[] BuildCache()
    {
        var arr = new AlgebraicNotation[64];
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
            {
                int idx = r * 8 + f;
                string s = string.Intern($"{(char)('a' + f)}{(char)('1' + r)}");
                arr[idx] = new AlgebraicNotation(s);
            }
        return arr;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Index(char f, char r) => (r - '1') * 8 + (f - 'a');

    public static AlgebraicNotation From(ReadOnlySpan<char> alg)
        => cache[Index(alg[0], alg[1])];

    public static AlgebraicNotation From(string alg) => From(alg.AsSpan());

    public static AlgebraicNotation From(Square0x88 sq88)
        => cache[((sq88.Value >> 4) * 8) + (sq88.Value & 0xF)];

    public static AlgebraicNotation From(Square0x64 sq64)
        => cache[sq64.Value];

    public override string ToString() => Value;
}


public static class Squares
{

    // ------------------------------------------------------------
    // Tiny test-friendly helpers (alloc-free via AsSpan):
    // S88/S64 let you write S88("e4") instead of verbose chains.
    // ------------------------------------------------------------
    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square0x88 S88(string alg) => ParseAlgebraicTo0x88(alg.AsSpan());
    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square0x64 S64(string alg) => ParseAlgebraicTo0x64(alg.AsSpan());


    // Precompute both encodings for all 64 squares.
    private static readonly Square0x64[] s64ByIndex = new Square0x64[64];
    private static readonly Square0x88[] s88ByIndex = new Square0x88[64];
    private static readonly string[] algByIndex = new string[64]; // interned literals

    static Squares()
    {
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
            {
                int idx = r * 8 + f;
                s64ByIndex[idx] = new Square0x64(idx);
                s88ByIndex[idx] = new Square0x88((r << 4) | f);
                algByIndex[idx] = string.Intern($"{(char)('a' + f)}{(char)('1' + r)}");
            }
    }

    public static bool IsOffboard(UnsafeSquare0x88 square) => (square.Value & 0x88) != 0;

    public static Square0x64 ConvertTo0x64Index(Square0x88 square)
        => s64ByIndex[(square.Value & 0xF) + ((square.Value >> 4) * 8)];

    public static Square0x88 ConvertTo0x88Index(Square0x64 square)
        => s88ByIndex[square.Value];

    // Fast parse from span (no allocation)
    public static Square0x64 ParseAlgebraicTo0x64(ReadOnlySpan<char> alg)
    {
        char f = alg[0], r = alg[1];
        return s64ByIndex[((r - '1') * 8) + (f - 'a')];
    }

    public static Square0x88 ParseAlgebraicTo0x88(ReadOnlySpan<char> alg)
    {
        char f = alg[0], r = alg[1];
        return s88ByIndex[((r - '1') * 8) + (f - 'a')];
    }

    // String overloads stay for convenience (calls span)
    public static Square0x64 ParseAlgebraicTo0x64(string alg) => ParseAlgebraicTo0x64(alg.AsSpan());
    public static Square0x88 ParseAlgebraicTo0x88(string alg) => ParseAlgebraicTo0x88(alg.AsSpan());

    // Reverse mapping (no allocation: returns the interned string)
    public static string ToAlgebraicString(Square0x64 s64) => algByIndex[s64.Value];
    public static string ToAlgebraicString(Square0x88 s88)
        => algByIndex[((s88.Value >> 4) * 8) + (s88.Value & 0xF)];

    // For convenience, keep AlgebraicNotation for UI/logging/tests
    public static AlgebraicNotation ToAlgebraic(Square0x88 s) => AlgebraicNotation.From(ToAlgebraicString(s));
    public static AlgebraicNotation ToAlgebraic(Square0x64 s) => AlgebraicNotation.From(ToAlgebraicString(s));
}
