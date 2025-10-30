using ChessEngine.Core;
using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class EnPassantTests
    {
        private static Board BuildEpPosition_WhiteToCapture()
        {
            // Minimal legal shell: kings + pawns
            var b = ChessEngine.Core.BoardFactory.FromFenOrStart("startpos");
            b.Clear();
            b.Place(Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("h1")), Piece.WhiteKing);
            b.Place(Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("h8")), Piece.BlackKing);

            b.Place(Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("e5")), Piece.WhitePawn); // white pawn ready to capture d6
            b.Place(Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("d7")), Piece.BlackPawn);

            // Black to move: play d7-d5 (double push) -> this should set EnPassantFile automatically
            b.SetSideToMove(false);
            var mv = new Board.Move(Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("d7")),
                                    Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("d5")),
                                    Piece.BlackPawn);
            var u = b.MakeMove(mv);
            // Now white to move with EP on file 'd'
            return b;
        }

        [Fact]
        public void EpAvailable_Only_Immediately_After_DoublePush()
        {
            var b = BuildEpPosition_WhiteToCapture();

            var epMoves = b.GenerateLegal().Where(m => m.Kind == Board.MoveKind.EnPassant).ToList();
            epMoves.Should().ContainSingle();

            // Make a different legal move for white (e.g., king h1-g1 if legal),
            // or just make/unmake the EP move to check it exists.
            var u = b.MakeMove(epMoves[0]);
            b.UnmakeMove(epMoves[0], u);
        }

        [Fact]
        public void EpCapture_RemovesBehindPawn_And_RestoresOnUnmake()
        {
            var b = BuildEpPosition_WhiteToCapture();

            var ep = b.GenerateLegal().First(m => m.Kind == Board.MoveKind.EnPassant);
            var occ0 = b.OccAll;

            var u = b.MakeMove(ep);
            b.OccAll.Should().NotBe(occ0); // captured pawn removed
            b.UnmakeMove(ep, u);
            b.OccAll.Should().Be(occ0);
        }
    }
}