using ChessEngine.Core;
using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class PromotionTests
    {
        [Fact]
        public void Promotion_Generates_AllFour_Pieces()
        {
            // White pawn on a7, empty a8 -> 4 promotion pushes
            var b = BoardFactory.FromFenOrStart("8/P7/8/8/8/8/8/7k w - - 0 1");
            var promos = b.GenerateLegal().Where(m => m.Kind == Board.MoveKind.Promotion).ToList();
            promos.Select(m => m.Promotion).Should().BeEquivalentTo(
                new[] { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight });
        }

        [Fact]
        public void PromotionCapture_Generates_AllFour_Pieces()
        {
            // White pawn on a7, black piece on b8 -> capture promotions x4
            var b = BoardFactory.FromFenOrStart("1r6/P7/8/8/8/8/8/7k w - - 0 1");
            var capPromos = b.GenerateLegal().Where(m => m.Kind == Board.MoveKind.PromotionCapture).ToList();
            capPromos.Should().HaveCount(4);
        }
    }

}