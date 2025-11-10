using System;
using System.Linq;
using FluentAssertions;
using Forklift.Core;
using Xunit;

public class ParseUCIMoveBufferTests
{
    private const string OverflowPosition = "R6R/3Q4/1Q4Q1/4Q3/2Q4Q/Q4Q2/pp1Q4/kBNN1KB1 w - - 0 1";

    [Fact]
    public void ParseUCIMove_ShouldNotOverflow_ForLargeMoveList()
    {
        var board = new Board();
        board.SetPositionFromFEN(OverflowPosition);

        var legalMove = board.GenerateLegal().First();
        var uci = ToUci(legalMove);

        Action act = () => board.ParseUCIMove(uci);

        act.Should().NotThrow();
    }

    private static string ToUci(Board.Move move)
    {
        var from = Squares.ToAlgebraicString(move.From88);
        var to = Squares.ToAlgebraicString(move.To88);

        return move.Promotion != Piece.Empty
            ? string.Concat(from, to, move.Promotion.PromotionChar)
            : string.Concat(from, to);
    }
}
