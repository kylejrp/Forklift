using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class PinTests
    {
        [Fact]
        public void PinnedPiece_CannotMoveOffPinnedFile()
        {
            // Arrange: Set up a board where the white rook on e2 is pinned by the black rook on e8.
            var board = BoardFactory.FromFenOrStart("4r3/8/8/8/8/8/4R3/4K3 w - - 0 1");

            // Act: Generate all legal moves for the pinned piece (white rook on e2).
            var fromSquare = S88("e2");
            var legalMoves = board.GenerateLegal()
                                  .Where(move => move.From88.Value == fromSquare.Value)
                                  .ToList();

            // Assert: Ensure no moves take the rook off the e-file.
            bool IsOnEFile(Square0x88 square) => ToAlgebraicString(square)[0] == 'e';
            var offFileMoves = legalMoves.Where(move => !IsOnEFile(move.To88)).ToList();

            // Verify that all moves off the e-file are illegal.
            offFileMoves.Should().BeEmpty();
        }
    }
}
