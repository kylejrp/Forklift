using Xunit;
using FluentAssertions;
using ChessEngine.Core;
using Forklift.Core;
using System.Linq;

namespace Forklift.Testing
{
    public class PerftShapeTests
    {
        [Theory]
        [InlineData("startpos")]
        [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
        public void Depth1_Equals_NumberOfLegalMoves(string fenOrStart)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long nodes = Perft.Count(b, 1);
            long legal = b.GenerateLegal().LongCount();
            nodes.Should().Be(legal);
        }

        [Theory]
        [InlineData("startpos", 3)]
        public void PerftDivide_Shows_Which_FirstMove_Is_Wrong(string fenOrStart, int depth)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long total = Perft.Count(b, depth);

            var divide = Perft.Divide(b, depth); // implement below
            divide.Sum(kv => kv.nodes).Should().Be(total);

            // Debug visibility example (doesn't assert, but helps when failures happen)
            // _output.WriteLine(string.Join("\n", divide.Select(kv => $"{kv.moveUci}: {kv.nodes}")));
        }
    }
}
