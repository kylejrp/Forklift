using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class EnPassantTests
    {
        private static Board BuildEpPosition_WhiteToCapture()
        {
            // Minimal legal shell: kings + pawns
            var b = BoardFactory.FromFenOrStart("startpos");
            b.Clear();
            b.Place(S88("h1"), Piece.WhiteKing);
            b.Place(S88("h8"), Piece.BlackKing);

            b.Place(S88("e5"), Piece.WhitePawn); // white pawn ready to capture d6
            b.Place(S88("d7"), Piece.BlackPawn);

            // Black to move: play d7-d5 (double push) -> this sets EnPassantFile automatically
            b.SetSideToMove(Color.Black);
            var mv = Board.Move.Normal(
                S64("d7").Value,
                S64("d5").Value,
                Piece.BlackPawn
            );
            var u = b.MakeMove(mv);

            // Now white to move with EP on file 'd'
            return b;
        }

        private static Board BuildEpPosition_PinnedPawn(bool pin)
        {
            // Setup: white king on e1, black king on h8, white pawn e5, black pawn d7, black rook e8 (for pin)
            var b = BoardFactory.FromFenOrStart("startpos");
            b.Clear();
            b.Place(S88("e1"), Piece.WhiteKing);
            b.Place(S88("h8"), Piece.BlackKing);
            b.Place(S88("e5"), Piece.WhitePawn);
            b.Place(S88("d7"), Piece.BlackPawn);
            if (pin)
                b.Place(S88("e8"), Piece.BlackRook);
            b.SetSideToMove(Color.Black);
            var mv = Board.Move.Normal(
                S64("d7").Value,
                S64("d5").Value,
                Piece.BlackPawn
            );
            b.MakeMove(mv);
            return b;
        }



        [Fact]
        public void EpSquare_Set_Only_After_DoublePush_And_Cleared_If_Unused()
        {
            var b = BoardFactory.FromFenOrStart("startpos");
            b.Clear();
            b.Place(S88("h1"), Piece.WhiteKing);
            b.Place(S88("h8"), Piece.BlackKing);
            b.Place(S88("e2"), Piece.WhitePawn);
            b.Place(S88("d7"), Piece.BlackPawn);
            b.SetSideToMove(Color.White);

            // White plays e2-e3 (single push): no EP
            var mv1 = Board.Move.Normal(
                S64("e2").Value,
                S64("e3").Value,
                Piece.WhitePawn
            );
            b.MakeMove(mv1);
            b.EnPassantFile.Should().BeNull();

            // Black plays d7-d5 (double push): EP set
            var mv2 = Board.Move.Normal(
                S64("d7").Value,
                S64("d5").Value,
                Piece.BlackPawn
            );
            b.MakeMove(mv2);
            b.EnPassantFile.Should().Be(new FileIndex(3)); // 'd' file

            // White plays a non-EP move: king h1-g1
            var mv3 = Board.Move.Normal(
                S64("h1").Value,
                S64("g1").Value,
                Piece.WhiteKing
            );
            b.MakeMove(mv3);
            b.EnPassantFile.Should().BeNull();
        }

        [Fact]
        public void EpAvailable_Only_Immediately_After_DoublePush()
        {
            var b = BuildEpPosition_WhiteToCapture();
            var epMoves = b.GenerateLegal().Where(m => m.IsEnPassant).ToList();
            epMoves.Should().ContainSingle();
            // Make/unmake EP move to check it exists
            var u = b.MakeMove(epMoves[0]);
            b.UnmakeMove(epMoves[0], u);
        }


        [Fact]
        public void EpCapture_RemovesBehindPawn_And_RestoresOnUnmake()
        {
            var b = BuildEpPosition_WhiteToCapture();
            var ep = b.GenerateLegal().First(m => m.IsEnPassant);
            var occ0 = b.OccAll;
            var u = b.MakeMove(ep);
            b.OccAll.Should().NotBe(occ0); // captured pawn removed
            b.UnmakeMove(ep, u);
            b.OccAll.Should().Be(occ0);
        }

        [Fact]
        public void EpCapture_Legal_If_NotPinned_Illegal_If_Pinned()
        {
            // Not pinned: EP move should be legal
            var b1 = BuildEpPosition_PinnedPawn(false);
            var epMoves1 = b1.GenerateLegal().Where(m => m.IsEnPassant).ToList();
            epMoves1.Should().ContainSingle();

            // Pinned: EP move should be illegal
            var b2 = BuildEpPosition_PinnedPawn(true);
            var epMoves2 = b2.GenerateLegal().Where(m => m.IsEnPassant).ToList();
            epMoves2.Should().BeEmpty();
        }

        [Fact]
        public void ZobristKey_XorEpFile_Only_WhenEpAvailable_MakeUnmakePreservesKey()
        {
            var b = BuildEpPosition_WhiteToCapture();
            var key0 = b.ZKey;
            var epFile = b.EnPassantFile;
            epFile.Should().NotBeNull();

            // Make EP move: EP file should be cleared, key updated
            var epMove = b.GenerateLegal().First(m => m.IsEnPassant);
            var u = b.MakeMove(epMove);
            b.EnPassantFile.Should().BeNull();
            var key1 = b.ZKey;
            key1.Should().NotBe(key0); // Key should change

            // Unmake: key and EP file restored
            b.UnmakeMove(epMove, u);
            b.EnPassantFile.Should().Be(epFile);
            b.ZKey.Should().Be(key0);
        }
    }
}
