using System;
using System.Collections.Generic;
using FluentAssertions;
using Forklift.Core;
using Xunit;

public class MoveGenerationBufferTests
{
    public static IEnumerable<object[]> LargeMovePositions()
    {
        yield return new object[] { "R6R/3Q4/1Q4Q1/4Q3/2Q4Q/Q4Q2/pp1Q4/kBNN1KB1 w - - 0 1" };
        yield return new object[] { "3Q4/1Q4Q1/4Q3/2Q4R/Q4Q2/3Q4/1Q4Rp/1K1BBNNk w - - 0 1" };
        yield return new object[] { "4r1k1/p4pp1/2n2n1B/2b5/N6Q/P2q1N2/1r4PP/R4R1K w - - 0 1" };
        yield return new object[] { "q2Q3r/n6R/kpB1N1K1/p1p1Bppp/1PN3P1/1n1pp1b1/P1PPPP1P/r5Rb w - - 0 1" };
        yield return new object[] { "R6R/pbpppK2/1B1QNNp1/1p3p1p/P1P3P1/1Pnnq1b1/2kPPPBP/r6r w - - 0 1" };
        yield return new object[] { "1QqQqQq1/r6Q/Q6q/q6Q/B2q4/q6Q/k6K/1qQ1QqRb w - - 0 1" };
        yield return new object[] { "QQqQqQqq/q6Q/Q6q/q6Q/Q6q/q6Q/Q6q/QqQqQqQq w - - 0 1" };
    }

    [Theory]
    [MemberData(nameof(LargeMovePositions))]
    public void MoveGeneration_ShouldNotOverflow_ForLargeMovePositions(string fen)
    {
        var board = new Board();
        board.SetPositionFromFEN(fen);

        Action pseudoLegal = () =>
        {
            MoveGeneration.GeneratePseudoLegal(board, board.SideToMove);
        };

        Action legal = () =>
        {
            board.GenerateLegal();
        };

        pseudoLegal.Should().NotThrow();
        legal.Should().NotThrow();
    }
}
