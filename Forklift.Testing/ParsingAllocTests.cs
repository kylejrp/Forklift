using System;
using FluentAssertions;
using Xunit;
using Forklift.Core;

public class ParsingAllocTests
{
    [Fact]
    public void ParseAlgebraic_Span_NoAlloc()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200_000; i++)
        {
            _ = Squares.ParseAlgebraicTo0x88("e2".AsSpan());
            _ = Squares.ParseAlgebraicTo0x88("e4".AsSpan());
            _ = Squares.ParseAlgebraicTo0x64("h7".AsSpan());
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        (after - before).Should().Be(0, "span-based parsing must be allocation-free");
    }

    [Theory]
    [InlineData("a1")]
    [InlineData("h8")]
    [InlineData("e4")]
    [InlineData("c6")]
    public void RoundTrip_Interned(string alg)
    {
        var s88 = Squares.ParseAlgebraicTo0x88(alg.AsSpan());
        var rt = Squares.ToAlgebraicString(s88);
        ReferenceEquals(rt, string.Intern(alg)).Should().BeTrue("reverse mapping should return interned canonical string");
    }

    [Fact]
    public void AlgebraicNotation_From_IsSingleton()
    {
        var a = ToAlgebraicString(S88("e1"));
        var b = ToAlgebraicString(S88("e1"));
        ReferenceEquals(a, b).Should().BeTrue("interning ensures same instance");
    }
}
