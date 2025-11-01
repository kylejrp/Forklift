using ChessEngine.Core;
using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class CastlingTests
    {
        [Fact]
        public void CannotCastleThroughCheck_Or_WhileInCheck()
        {
            // Classic: white in check on e1 or path attacked => no O-O/O-O-O
            var b = BoardFactory.FromFenOrStart("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1"); // empty board + rights
            // Put a black rook attacking f1
            b.Place(Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("f8")), Piece.BlackRook);

            var legals = b.GenerateLegal().ToList();
            legals.Should().NotContain(m => m.Mover == Piece.WhiteKing &&
                                            (m.Kind == Board.MoveKind.CastleKing || m.Kind == Board.MoveKind.CastleQueen));
        }

        [Fact]
        public void CastlingRightsClear_When_RookMovesOrCaptured()
        {
            var b = BoardFactory.FromFenOrStart("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

            // Move white h1 rook away -> should clear K side for white
            var mv = Board.Move.Normal(
                Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("h1")),
                Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("h2")),
                Piece.WhiteRook
            );
            var u = b.MakeMove(mv);

            (b.CastlingRights & Board.CastlingRightsFlags.WhiteKing).Should().Be(0);

            b.UnmakeMove(mv, u);
        }
    }
}