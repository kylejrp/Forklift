using ChessEngine.Core;
using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class PinTests
    {
        [Fact]
        public void PinnedPiece_MovesOffFile_AreNotLegal()
        {
            // White king e1, white rook e2 (pinned), black rook e8
            var b = ChessEngine.Core.BoardFactory.FromFenOrStart("4r3/8/8/8/8/8/4R3/4K3 w - - 0 1");

            var from = Squares.ParseAlgebraicTo0x88("e2");
            var legals = b.GenerateLegal()
                          .Where(m => m.From88 == from)
                          .ToList();

            // Any legal move must stay on the e-file (e2->e1/e3/e4...) or be a capture that keeps the king covered.
            bool IsOnEFile(int sq88) => (sq88 & 0xF) == (Squares.ParseAlgebraicTo0x88("e2") & 0xF);
            var offFile = legals.Where(m => !IsOnEFile(m.To88)).ToList();
            offFile.Should().BeEmpty(); // the illegal ones are ONLY those leaving the e-file
        }
    }
}