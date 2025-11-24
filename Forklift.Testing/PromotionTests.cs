using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class PromotionTests
    {
        [Fact]
        public void Promotion_Generates_AllFour_Pieces()
        {
            var b = BoardFactory.FromFenOrStart("7K/P7/8/8/8/8/8/7k w - - 0 1");
            var moves = b.GenerateLegal();

            Span<Piece> found = stackalloc Piece[4];
            int n = 0;
            for (int i = 0; i < moves.Length; i++)
                if (moves[i].Kind == Board.MoveKind.Promotion)
                    found[n++] = moves[i].Promotion;

            found[..n].ToArray().Should().BeEquivalentTo(
                new[] { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight });
        }


        [Fact]
        public void PromotionCapture_Generates_AllFour_Pieces()
        {
            // White pawn on a7, black piece on b8 -> capture promotions x4
            var b = BoardFactory.FromFenOrStart("1r5K/P7/8/8/8/8/8/7k w - - 0 1");
            var capPromos = b.GenerateLegal().Where(m => m.Kind == Board.MoveKind.PromotionCapture).ToList();
            capPromos.Should().HaveCount(4);
        }
    }

}
