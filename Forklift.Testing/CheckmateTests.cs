using FluentAssertions;
using Forklift.Core;
using Xunit;

namespace Forklift.Testing
{
    public class CheckmateTests
    {
        [Fact]
        public void Checkmate_IsCheckmate()
        {
            var b = BoardFactory.FromFenOrStart("7k/8/8/8/8/3p4/4q3/4K3 w - - 0 1");

            b.IsStalemate().Should().BeFalse();
            b.IsCheckmate().Should().BeTrue();
            b.InCheck(Color.White).Should().BeTrue();
            var legalMoves = b.GenerateLegal().ToList();
            legalMoves.Should().BeEmpty();
        }
    }
}
