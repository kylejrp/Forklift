using FluentAssertions;
using Forklift.Core;
using static Forklift.Testing.TestHelpers;

namespace Forklift.Testing
{
    public class CastlingTests
    {
        [Theory]
        [InlineData("4k3/r7/8/8/8/8/8/R3K2R w KQ - 0 1", true, true)]
        [InlineData("4k3/1r6/8/8/8/8/8/R3K2R w KQ - 0 1", true, true)]
        [InlineData("4k3/2r5/8/8/8/8/8/R3K2R w KQ - 0 1", false, true)]
        [InlineData("4k3/3r4/8/8/8/8/8/R3K2R w KQ - 0 1", false, true)]
        [InlineData("4k3/4r3/8/8/8/8/8/R3K2R w KQ - 0 1", false, false)]
        [InlineData("4k3/5r2/8/8/8/8/8/R3K2R w KQ - 0 1", true, false)]
        [InlineData("4k3/6r1/8/8/8/8/8/R3K2R w KQ - 0 1", true, false)]
        [InlineData("4k3/7r/8/8/8/8/8/R3K2R w KQ - 0 1", true, true)]
        [InlineData("4k3/2r2r2/8/8/8/8/8/R3K2R w KQ - 0 1", false, false)]
        public void CannotCastleThroughCheck_Or_WhileInCheck(string fen, bool canCastleQueenSide, bool canCastleKingSide)
        {
            var b = BoardFactory.FromFenOrStart(fen);
            var moves = b.GenerateLegal();

            bool foundKingSide = false, foundQueenSide = false;
            for (int i = 0; i < moves.Length; i++)
            {
                ref readonly var m = ref moves[i];
                if (m.Mover == Piece.WhiteKing)
                {
                    foundKingSide |= m.Kind.HasFlag(Board.MoveKind.CastleKing);
                    foundQueenSide |= m.Kind.HasFlag(Board.MoveKind.CastleQueen);
                }
            }

            foundKingSide.Should().Be(canCastleKingSide);
            foundQueenSide.Should().Be(canCastleQueenSide);
        }


        [Fact]
        public void CastlingRightsClear_When_KingMoves()
        {
            var b = BoardFactory.FromFenOrStart("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
            // Move white king e1-f1
            var mv = Board.Move.Normal(
                Sq88("e1"),
                Sq88("f1"),
                Piece.WhiteKing
            );
            var u = b.MakeMove(mv);
            (b.CastlingRights & Board.CastlingRightsFlags.WhiteKing).Should().Be(0);
            (b.CastlingRights & Board.CastlingRightsFlags.WhiteQueen).Should().Be(0);
            b.UnmakeMove(mv, u);
        }

        [Fact]
        public void CastlingRightsClear_When_RookMovesOrCaptured()
        {
            var b = BoardFactory.FromFenOrStart("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

            // Move white h1 rook away -> should clear K side for white
            var mv = Board.Move.Normal(
                Sq88("h1"),
                Sq88("h2"),
                Piece.WhiteRook
            );
            var u = b.MakeMove(mv);
            (b.CastlingRights & Board.CastlingRightsFlags.WhiteKing).Should().Be(0);
            b.UnmakeMove(mv, u);

            // Move white a1 rook away -> should clear Q side for white
            var mv2 = Board.Move.Normal(
                Sq88("a1"),
                Sq88("a2"),
                Piece.WhiteRook
            );
            var u2 = b.MakeMove(mv2);
            (b.CastlingRights & Board.CastlingRightsFlags.WhiteQueen).Should().Be(0);
            b.UnmakeMove(mv2, u2);

            // Capture white h1 rook -> should clear K side for white
            b.Place(Sq88("h1"), Piece.WhiteRook);
            var mv3 = Board.Move.Capture(
                Sq88("g2"),
                Sq88("h1"),
                Piece.BlackRook,
                Piece.WhiteRook
            );
            var u3 = b.MakeMove(mv3);
            (b.CastlingRights & Board.CastlingRightsFlags.WhiteKing).Should().Be(0);
            b.UnmakeMove(mv3, u3);
        }

        [Fact]
        public void CastlingRightsNotRestored_When_RookReturns()
        {
            var b = BoardFactory.FromFenOrStart("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
            // Move h1 rook away and back
            var mv1 = Board.Move.Normal(
                Sq88("h1"),
                Sq88("h2"),
                Piece.WhiteRook
            );
            b.MakeMove(mv1);
            var mv2 = Board.Move.Normal(
                Sq88("h2"),
                Sq88("h1"),
                Piece.WhiteRook
            );
            b.MakeMove(mv2);
            (b.CastlingRights & Board.CastlingRightsFlags.WhiteKing).Should().Be(0);
        }

        [Fact]
        public void CannotCastle_When_DiscoveredCheckOnPath()
        {
            // White king on e1, rook on h1, black bishop on c3 attacking f2 (discovered check if king moves)
            var b = BoardFactory.FromFenOrStart("r3k2r/8/8/8/8/8/2b5/R3K2R w KQkq - 0 1");
            // Place a white pawn on f2 to block bishop
            b.Place(Sq88("f2"), Piece.WhitePawn);
            // Move pawn away, so bishop attacks f2
            var mv = Board.Move.Normal(
                Sq88("f2"),
                Sq88("f3"),
                Piece.WhitePawn
            );
            b.MakeMove(mv);
            // Now bishop attacks f2, so castling through f1 is illegal
            var legals = b.GenerateLegal().ToList();
            legals.Should().NotContain(m => m.Mover == Piece.WhiteKing && m.Kind.HasFlag(Board.MoveKind.CastleKing));
        }
    }
}
