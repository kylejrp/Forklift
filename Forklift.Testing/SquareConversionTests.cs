using System;
using System.Collections.Generic;
using Xunit;
using Forklift.Core;

namespace Forklift.Testing
{
    public class SquareConversionTests
    {
        // Static list of 0..63 for data-driven tests
        public static IEnumerable<object[]> ValidSquareValues =>
        [
            [0],  [1],  [2],  [3],
            [4],  [5],  [6],  [7],
            [8],  [9],  [10], [11],
            [12], [13], [14], [15],
            [16], [17], [18], [19],
            [20], [21], [22], [23],
            [24], [25], [26], [27],
            [28], [29], [30], [31],
            [32], [33], [34], [35],
            [36], [37], [38], [39],
            [40], [41], [42], [43],
            [44], [45], [46], [47],
            [48], [49], [50], [51],
            [52], [53], [54], [55],
            [56], [57], [58], [59],
            [60], [61], [62], [63]
        ];

        // Static known-good 0x88 values (rank<<4 | file), a1..h8 in 0x64 order
        public static readonly Square0x88[] KnownGoodSquare0x88 = {
            new (0x00), new (0x01), new (0x02), new (0x03),
            new (0x04), new (0x05), new (0x06), new (0x07),
            new (0x10), new (0x11), new (0x12), new (0x13),
            new (0x14), new (0x15), new (0x16), new (0x17),
            new (0x20), new (0x21), new (0x22), new (0x23),
            new (0x24), new (0x25), new (0x26), new (0x27),
            new (0x30), new (0x31), new (0x32), new (0x33),
            new (0x34), new (0x35), new (0x36), new (0x37),
            new (0x40), new (0x41), new (0x42), new (0x43),
            new (0x44), new (0x45), new (0x46), new (0x47),
            new (0x50), new (0x51), new (0x52), new (0x53),
            new (0x54), new (0x55), new (0x56), new (0x57),
            new (0x60), new (0x61), new (0x62), new (0x63),
            new (0x64), new (0x65), new (0x66), new (0x67),
            new (0x70), new (0x71), new (0x72), new (0x73),
            new (0x74), new (0x75), new (0x76), new (0x77)
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
        // Static list of 0..63 for data-driven tests (typed)
        public static TheoryData<int> ValidSquareValuesTyped
        {
            get
            {
                var data = new TheoryData<int>();
                for (int i = 0; i < 64; i++)
                    data.Add(i);
                return data;
            }
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void ValidConversion_Square0x64_To_UnsafeSquare0x64(int value)
        {
            var square = new Square0x64(value);
            var unsafeSquare = (UnsafeSquare0x64)square;
            Assert.Equal(value, unsafeSquare.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void ValidConversion_UnsafeSquare0x64_To_Square0x64(int value)
        {
            var unsafeSquare = new UnsafeSquare0x64(value);
            var square = (Square0x64)unsafeSquare;
            Assert.Equal(value, square.Value);
        }

        // --- 0x88 <-> Unsafe0x88 ---
        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void ValidConversion_Square0x88_To_UnsafeSquare0x88(int idx)
        {
            var square88 = KnownGoodSquare0x88[idx];
            var unsafeSquare88 = (UnsafeSquare0x88)square88;
            Assert.Equal(square88.Value, unsafeSquare88.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void ValidConversion_UnsafeSquare0x88_To_Square0x88(int idx)
        {
            var val = KnownGoodSquare0x88[idx].Value;
            var unsafeSquare88 = new UnsafeSquare0x88(val);
            var square88 = (Square0x88)unsafeSquare88;
            Assert.Equal(val, square88.Value);
        }

        // --- 0x64 <-> 0x88 ---
        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void ValidConversion_Square0x64_To_Square0x88(int idx)
        {
            var square64 = new Square0x64(idx);
            var square88 = (Square0x88)square64;
            Assert.Equal(KnownGoodSquare0x88[idx].Value, square88.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void ValidConversion_Square0x88_To_Square0x64(int idx)
        {
            var square88 = KnownGoodSquare0x88[idx];
            var square64 = (Square0x64)square88;
            Assert.Equal(idx, square64.Value);
        }

        // Round-trips across all 64
        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void RoundTrip_0x64_To_0x88_Back(int idx)
        {
            var a = new Square0x64(idx);
            var b = (Square0x88)a;
            var c = (Square0x64)b;
            Assert.Equal(idx, c.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void RoundTrip_0x88_To_0x64_Back(int idx)
        {
            var a = KnownGoodSquare0x88[idx];
            var b = (Square0x64)a;
            var c = (Square0x88)b;
            Assert.Equal(a.Value, c.Value);
        }

        // --- Algebraic <-> 0x64 ---
        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void Comprehensive_Algebraic_To_0x64(int idx)
        {
            var square64 = S64(KnownGoodAlgebraic[idx]);
            Assert.Equal(idx, square64.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void Comprehensive_0x64_To_Algebraic(int idx)
        {
            var square64 = new Square0x64(idx);
            var algebraic = ToAlgebraicString(square64);
            Assert.Equal(KnownGoodAlgebraic[idx], algebraic);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void Comprehensive_Algebraic_To_0x88(int idx)
        {
            var square88 = S88(KnownGoodAlgebraic[idx]);
            Assert.Equal(KnownGoodSquare0x88[idx].Value, square88.Value);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void Comprehensive_0x88_To_Algebraic(int idx)
        {
            var square88 = KnownGoodSquare0x88[idx];
            var algebraic = ToAlgebraicString(square88);
            Assert.Equal(KnownGoodAlgebraic[idx], algebraic);
        }

        // Algebraic round-trips for all squares
        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void RoundTrip_Algebraic_To_0x64_Back(int idx)
        {
            var to64 = S64(KnownGoodAlgebraic[idx]);
            var back = ToAlgebraicString(to64);
            Assert.Equal(KnownGoodAlgebraic[idx], back);
        }

        [Theory]
        [MemberData(nameof(ValidSquareValuesTyped))]
        public void RoundTrip_Algebraic_To_0x88_Back(int idx)
        {
            var to88 = S88(KnownGoodAlgebraic[idx]);
            var back = ToAlgebraicString(to88);
            Assert.Equal(KnownGoodAlgebraic[idx], back);
        }
    }
}
