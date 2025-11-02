using FluentAssertions;
using Forklift.Core;
using Xunit;

namespace Forklift.Testing
{
    public class DoubleCheckTests
    {
        [Fact]
        public void OnlyKingMovesAllowed_InDoubleCheck()
        {
            var b = BoardFactory.FromFenOrStart("4r2k/8/8/8/8/8/6n1/4K3 w - - 0 1");
            // It's white to move, and both rook and knight attack e1
            var legalMoves = b.GenerateLegal().ToList();
            // Only king moves should be allowed
            legalMoves.Should().OnlyContain(m => m.Mover == Piece.WhiteKing);
            legalMoves.Should().NotBeEmpty();
        }
    }
}
