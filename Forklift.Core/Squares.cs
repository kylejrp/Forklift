using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Forklift.Core;

public readonly struct UnsafeSquare0x64 : IEquatable<UnsafeSquare0x64>
{
    public int Value { get; }

    public int Rank => Value >> 3;
    public int File => Value & 7;
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

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is UnsafeSquare0x64 square && Value == square.Value;
    public bool Equals(UnsafeSquare0x64 other) => Value == other.Value;
    public override int GetHashCode()
    {
        return Value;
    }
    public static bool operator ==(UnsafeSquare0x64 left, UnsafeSquare0x64 right) => left.Value == right.Value;
    public static bool operator !=(UnsafeSquare0x64 left, UnsafeSquare0x64 right) => left.Value != right.Value;
}

/// <summary>
/// Represents a square in 0x64 format.
/// </summary>
public readonly struct Square0x64 : IEquatable<Square0x64>
{
    public byte Value { get; }
    public const int MIN_VALUE = 0;
    public const int MAX_VALUE = 63;

    public int Rank => Value >> 3;
    public int File => Value & 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Square0x64(int value) : this((byte)value) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Square0x64(byte value)
    {
        Debug.Assert(value >= MIN_VALUE && value <= MAX_VALUE, "Square0x64 value out of range");
        Value = value;
    }

    public static explicit operator int(Square0x64 square) => square.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator Square0x64(int value) => new Square0x64(value);

    public static explicit operator Square0x64(Square0x88 square) => new Square0x64(Squares.Convert0x88IndexTo0x64Index(square.Value));

    public static explicit operator Square0x64(UnsafeSquare0x88 square) => new Square0x64(Squares.Convert0x88IndexTo0x64Index(square.Value));

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

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Square0x64 square && Value == square.Value;
    public bool Equals(Square0x64 other) => Value == other.Value;
    public override int GetHashCode()
    {
        return Value;
    }
    public static bool operator ==(Square0x64 left, Square0x64 right) => left.Value == right.Value;
    public static bool operator !=(Square0x64 left, Square0x64 right) => left.Value != right.Value;
}


/// <summary>
/// Represents a square in unsafe 0x88 format (no validation).
/// </summary>
public readonly struct UnsafeSquare0x88 : IEquatable<UnsafeSquare0x88>
{
    public int Value { get; }

    public int Rank => Value >> 4;
    public int File => Value & 0xF;
    public UnsafeSquare0x88(int value)
    {
        Value = value;
    }
    public static explicit operator int(UnsafeSquare0x88 square) => square.Value;
    public static explicit operator UnsafeSquare0x88(int value) => new UnsafeSquare0x88(value);
    public static explicit operator UnsafeSquare0x88(Square0x88 square) => new UnsafeSquare0x88(square.Value);
    public static explicit operator Square0x88(UnsafeSquare0x88 square) => new Square0x88(square.Value);
    public static explicit operator UnsafeSquare0x88(UnsafeSquare0x64 square) => new UnsafeSquare0x88(Squares.Convert0x64IndexTo0x88Index(square.Value));

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

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is UnsafeSquare0x88 square && Value == square.Value;
    public bool Equals(UnsafeSquare0x88 other) => Value == other.Value;
    public override int GetHashCode()
    {
        return Value;
    }
    public static bool operator ==(UnsafeSquare0x88 left, UnsafeSquare0x88 right) => left.Value == right.Value;
    public static bool operator !=(UnsafeSquare0x88 left, UnsafeSquare0x88 right) => left.Value != right.Value;
}

/// <summary>
/// Represents a square in 0x88 format.
/// </summary>
public readonly struct Square0x88 : IEquatable<Square0x88>
{
    public byte Value { get; }
    public const int MIN_VALUE = 0;
    public const int MAX_VALUE = 0x77;


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Square0x88(int value) : this((byte)value) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Square0x88(byte value)
    {
        Debug.Assert((value & 0x88) == 0, "Square0x88 value out of range");
        Value = value;
    }

    public int Rank => Value >> 4;
    public int File => Value & 0xF;

    public static implicit operator int(Square0x88 square) => square.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator Square0x88(int value) => new Square0x88(value);

    public static explicit operator Square0x88(Square0x64 square)
    {
        return new Square0x88(Squares.Convert0x64IndexTo0x88Index(square.Value));
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

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Square0x88 square && Value == square.Value;
    public bool Equals(Square0x88 other) => Value == other.Value;
    public override int GetHashCode()
    {
        return Value;
    }
    public static bool operator ==(Square0x88 left, Square0x88 right) => left.Value == right.Value;
    public static bool operator !=(Square0x88 left, Square0x88 right) => left.Value != right.Value;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(char f, char r) => (r - '1') * 8 + (f - 'a');

    public static AlgebraicNotation From(ReadOnlySpan<char> alg)
        => cache[Index(alg[0], alg[1])];

    public static AlgebraicNotation From(string alg) => From(alg.AsSpan());

    public static AlgebraicNotation From(Square0x88 sq88) => cache[Squares.Convert0x88IndexTo0x64Index(sq88)];
    public static AlgebraicNotation From(Square0x64 sq64) => cache[sq64.Value];

    public override string ToString() => Value;
}


public static class Squares
{
    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square0x88 S88(string alg) => ParseAlgebraicTo0x88(alg);
    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Square0x64 S64(string alg) => ParseAlgebraicTo0x64(alg);

    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Convert0x64IndexTo0x88Index(int square0x64Index)
    {
        return (square0x64Index & 0x7) + ((square0x64Index >> 3) << 4);
    }

    [DebuggerStepThrough, MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Convert0x88IndexTo0x64Index(int square0x88Index)
    {
        return (square0x88Index & 0xF) + ((square0x88Index >> 4) * 8);
    }

    public static bool IsOffboard(int square0x88Index) => (square0x88Index & 0x88) != 0;
    public static bool IsOffboard(UnsafeSquare0x88 square) => (square.Value & 0x88) != 0;
    public static bool IsOffboard(UnsafeSquare0x64 square) => square.Value < Square0x64.MIN_VALUE || square.Value > Square0x64.MAX_VALUE;


    // Fast parse from span (no allocation)
    public static Square0x64 ParseAlgebraicTo0x64(ReadOnlySpan<char> alg)
    {
        char f = alg[0], r = alg[1];
        return new Square0x64(((r - '1') * 8) + (f - 'a'));
    }

    public static Square0x88 ParseAlgebraicTo0x88(ReadOnlySpan<char> alg)
    {
        char f = alg[0], r = alg[1];
        return new Square0x88(((r - '1') << 4) | (f - 'a'));
    }
    public static Square0x64 ParseAlgebraicTo0x64(string alg) => ParseAlgebraicTo0x64(alg.AsSpan());
    public static Square0x88 ParseAlgebraicTo0x88(string alg) => ParseAlgebraicTo0x88(alg.AsSpan());

    // Reverse mapping
    public static string ToAlgebraicString(Square0x64 s64) => AlgebraicNotation.From(s64).Value;
    public static string ToAlgebraicString(Square0x88 s88) => AlgebraicNotation.From(s88).Value;

    public static AlgebraicNotation ToAlgebraic(Square0x88 s) => AlgebraicNotation.From(ToAlgebraicString(s));
    public static AlgebraicNotation ToAlgebraic(Square0x64 s) => AlgebraicNotation.From(ToAlgebraicString(s));
}
