using FluentAssertions;
using Forklift.Core;
using Xunit;

namespace Forklift.Testing
{
    public class StalemateTests
    {
        [Fact]
        public void Stalemate_IsStalemate()
        {
            var b = BoardFactory.FromFenOrStart("7k/8/8/8/8/3q1q2/8/4K3 w - - 0 1");

            b.IsStalemate().Should().BeTrue();
            b.IsCheckmate().Should().BeFalse();
            b.InCheck(Color.White).Should().BeFalse();
            var legalMoves = b.GenerateLegal().ToList();
            legalMoves.Should().BeEmpty();
        }
    }
}
