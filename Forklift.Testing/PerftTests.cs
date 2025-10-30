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

            var divide = Perft.Divide(b, depth);
            divide.Sum(kv => kv.nodes).Should().Be(total);
        }

        [Theory]
        [InlineData("startpos", 1)]
        [InlineData("startpos", 2)]
        public void Perft_Count_Should_Match_Expected_Values(string fenOrStart, int depth)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long nodes = Perft.Count(b, depth);

            // Expected values for startpos at depth 1 and 2
            long expectedNodes = depth switch
            {
                1 => 20, // Depth 1:20 legal moves in the starting position
                2 => 400, // Depth 2:400 legal positions after 1 move
                _ => throw new ArgumentOutOfRangeException()
            };

            nodes.Should().Be(expectedNodes);
        }

        [Theory]
        [InlineData("8/8/8/8/8/8/8/8 w - - 0 1", 1)] // Empty board
        [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 1)] // Standard starting position
        public void Perft_Count_Should_Handle_Specific_Positions(string fen, int depth)
        {
            var b = BoardFactory.FromFenOrStart(fen);
            long nodes = Perft.Count(b, depth);

            // Add expected values for specific positions if known
            // For now, just ensure it runs without exceptions
            nodes.Should().BeGreaterThan(0);
        }

        [Theory]
        [InlineData("startpos", 2)]
        public void Perft_Divide_Should_Match_Expected_Values(string fenOrStart, int depth)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            var divide = Perft.Divide(b, depth);

            // Verify that the sum of nodes matches the total count
            long totalNodes = divide.Sum(kv => kv.nodes);
            long expectedTotal = Perft.Count(b, depth);

            totalNodes.Should().Be(expectedTotal);

            // Optionally, log the divide output for debugging
            foreach (var kv in divide)
            {
                System.Diagnostics.Debug.WriteLine($"Move: {kv.moveUci}, Nodes: {kv.nodes}");
            }
        }
    }
}
