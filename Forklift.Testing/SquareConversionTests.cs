using System;
using System.Collections.Generic;
using Xunit;
using Forklift.Core;

namespace Forklift.Testing
{
    public class SquareConversionTests
    {
        // Static list of 0..63 for data-driven tests
        public static IEnumerable<object[]> ValidSquareValues => new List<object[]>
        {
            new object[] {0},  new object[] {1},  new object[] {2},  new object[] {3},
            new object[] {4},  new object[] {5},  new object[] {6},  new object[] {7},
            new object[] {8},  new object[] {9},  new object[] {10}, new object[] {11},
            new object[] {12}, new object[] {13}, new object[] {14}, new object[] {15},
            new object[] {16}, new object[] {17}, new object[] {18}, new object[] {19},
            new object[] {20}, new object[] {21}, new object[] {22}, new object[] {23},
            new object[] {24}, new object[] {25}, new object[] {26}, new object[] {27},
            new object[] {28}, new object[] {29}, new object[] {30}, new object[] {31},
            new object[] {32}, new object[] {33}, new object[] {34}, new object[] {35},
            new object[] {36}, new object[] {37}, new object[] {38}, new object[] {39},
            new object[] {40}, new object[] {41}, new object[] {42}, new object[] {43},
            new object[] {44}, new object[] {45}, new object[] {46}, new object[] {47},
            new object[] {48}, new object[] {49}, new object[] {50}, new object[] {51},
            new object[] {52}, new object[] {53}, new object[] {54}, new object[] {55},
            new object[] {56}, new object[] {57}, new object[] {58}, new object[] {59},
            new object[] {60}, new object[] {61}, new object[] {62}, new object[] {63}
        };

        // Static known-good 0x88 values (rank<<4 | file), a1..h8 in 0x64 order
        public static readonly Square0x88[] KnownGoodSquare0x88 = {
            new Square0x88(0x00), new Square0x88(0x01), new Square0x88(0x02), new Square0x88(0x03),
            new Square0x88(0x04), new Square0x88(0x05), new Square0x88(0x06), new Square0x88(0x07),
            new Square0x88(0x10), new Square0x88(0x11), new Square0x88(0x12), new Square0x88(0x13),
            new Square0x88(0x14), new Square0x88(0x15), new Square0x88(0x16), new Square0x88(0x17),
            new Square0x88(0x20), new Square0x88(0x21), new Square0x88(0x22), new Square0x88(0x23),
            new Square0x88(0x24), new Square0x88(0x25), new Square0x88(0x26), new Square0x88(0x27),
            new Square0x88(0x30), new Square0x88(0x31), new Square0x88(0x32), new Square0x88(0x33),
            new Square0x88(0x34), new Square0x88(0x35), new Square0x88(0x36), new Square0x88(0x37),
            new Square0x88(0x40), new Square0x88(0x41), new Square0x88(0x42), new Square0x88(0x43),
            new Square0x88(0x44), new Square0x88(0x45), new Square0x88(0x46), new Square0x88(0x47),
            new Square0x88(0x50), new Square0x88(0x51), new Square0x88(0x52), new Square0x88(0x53),
            new Square0x88(0x54), new Square0x88(0x55), new Square0x88(0x56), new Square0x88(0x57),
            new Square0x88(0x60), new Square0x88(0x61), new Square0x88(0x62), new Square0x88(0x63),
            new Square0x88(0x64), new Square0x88(0x65), new Square0x88(0x66), new Square0x88(0x67),
            new Square0x88(0x70), new Square0x88(0x71), new Square0x88(0x72), new Square0x88(0x73),
            new Square0x88(0x74), new Square0x88(0x75), new Square0x88(0x76), new Square0x88(0x77)
        };

        // Static algebraic names in 0x64 order (a1..h1, a2..h2, ..., a8..h8)
        public static readonly string[] KnownGoodAlgebraic = {
            "a1","b1","c1","d1","e1","f1","g1","h1",
            "a2","b2","c2","d2","e2","f2","g2","h2",
            "a3","b3","c3","d3","e3","f3","g3","h3",
            "a4","b4","c4","d4","e4","f4","g4","h4",
            "a5","b5","c5","d5","e5","f5","g5","h5",
            "a6","b6","c6","d6","e6","f6","g6","h6",
            "a7","b7","c7","d7","e7","f7","g7","h7",
            "a8","b8","c8","d8","e8","f8","g8","h8",
        };

        // --- 0x64 <-> Unsafe0x64 ---
        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void ValidConversion_Square0x64_To_UnsafeSquare0x64(int value)
        {
            var square = new Square0x64(value);
            var unsafeSquare = (UnsafeSquare0x64)square;
            Assert.Equal(value, unsafeSquare.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void ValidConversion_UnsafeSquare0x64_To_Square0x64(int value)
        {
            var unsafeSquare = new UnsafeSquare0x64(value);
            var square = (Square0x64)unsafeSquare;
            Assert.Equal(value, square.Value);
        }

        // --- 0x88 <-> Unsafe0x88 ---
        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void ValidConversion_Square0x88_To_UnsafeSquare0x88(int idx)
        {
            var square88 = KnownGoodSquare0x88[idx];
            var unsafeSquare88 = (UnsafeSquare0x88)square88;
            Assert.Equal(square88.Value, unsafeSquare88.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void ValidConversion_UnsafeSquare0x88_To_Square0x88(int idx)
        {
            var val = KnownGoodSquare0x88[idx].Value;
            var unsafeSquare88 = new UnsafeSquare0x88(val);
            var square88 = (Square0x88)unsafeSquare88;
            Assert.Equal(val, square88.Value);
        }

        // --- 0x64 <-> 0x88 ---
        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void ValidConversion_Square0x64_To_Square0x88(int idx)
        {
            var square64 = new Square0x64(idx);
            var square88 = (Square0x88)square64;
            Assert.Equal(KnownGoodSquare0x88[idx].Value, square88.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void ValidConversion_Square0x88_To_Square0x64(int idx)
        {
            var square88 = KnownGoodSquare0x88[idx];
            var square64 = (Square0x64)square88;
            Assert.Equal(idx, square64.Value);
        }

        // Round-trips across all 64
        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void RoundTrip_0x64_To_0x88_Back(int idx)
        {
            var a = new Square0x64(idx);
            var b = (Square0x88)a;
            var c = (Square0x64)b;
            Assert.Equal(idx, c.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void RoundTrip_0x88_To_0x64_Back(int idx)
        {
            var a = KnownGoodSquare0x88[idx];
            var b = (Square0x64)a;
            var c = (Square0x88)b;
            Assert.Equal(a.Value, c.Value);
        }

        // --- Algebraic <-> 0x64 ---
        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void Comprehensive_Algebraic_To_0x64(int idx)
        {
            var algebraic = AlgebraicNotation.From(KnownGoodAlgebraic[idx]);
            var square64 = Squares.ParseAlgebraicTo0x64(algebraic);
            Assert.Equal(idx, square64.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void Comprehensive_0x64_To_Algebraic(int idx)
        {
            var square64 = new Square0x64(idx);
            var algebraic = Squares.ToAlgebraic(square64);
            Assert.Equal(KnownGoodAlgebraic[idx], algebraic.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void Comprehensive_Algebraic_To_0x88(int idx)
        {
            var algebraic = AlgebraicNotation.From(KnownGoodAlgebraic[idx]);
            var square88 = Squares.ParseAlgebraicTo0x88(algebraic);
            Assert.Equal(KnownGoodSquare0x88[idx].Value, square88.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void Comprehensive_0x88_To_Algebraic(int idx)
        {
            var square88 = KnownGoodSquare0x88[idx];
            var algebraic = Squares.ToAlgebraic(square88);
            Assert.Equal(KnownGoodAlgebraic[idx], algebraic.Value);
        }

        // Algebraic round-trips for all squares
        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void RoundTrip_Algebraic_To_0x64_Back(int idx)
        {
            var algebraic = AlgebraicNotation.From(KnownGoodAlgebraic[idx]);
            var to64 = Squares.ParseAlgebraicTo0x64(algebraic);
            var back = Squares.ToAlgebraic(to64);
            Assert.Equal(KnownGoodAlgebraic[idx], back.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValues))]
        public void RoundTrip_Algebraic_To_0x88_Back(int idx)
        {
            var algebraic = AlgebraicNotation.From(KnownGoodAlgebraic[idx]);
            var to88 = Squares.ParseAlgebraicTo0x88(algebraic);
            var back = Squares.ToAlgebraic(to88);
            Assert.Equal(KnownGoodAlgebraic[idx], back.Value);
        }
    }
}
