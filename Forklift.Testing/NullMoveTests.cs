using FluentAssertions;
using Forklift.Core;
using Xunit;

namespace Forklift.Testing
{
    public class NullMoveTests
    {
        [Fact]
        public void MakeNullMove_ThenUnmake_RestoresBoardState()
        {
            // Position with an en passant square so we also exercise EP Zobrist toggling.
            // This is the standard position after 1. e4:
            // FEN: rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1
            var b = BoardFactory.FromFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");

            var key0 = b.ZKey;
            var side0 = b.SideToMove;
            var ep0 = b.EnPassantFile;
            var half0 = b.HalfmoveClock;
            var occ0 = b.OccAll;
            var hash0 = b.HashHistoryCount;

            var state = b.MakeNullMove();

            // Sanity: null move should visibly change some things
            b.SideToMove.Should().NotBe(side0);
            b.EnPassantFile.Should().BeNull();
            b.ZKey.Should().NotBe(key0);

            b.UnmakeNullMove(state);

            b.ZKey.Should().Be(key0);
            b.SideToMove.Should().Be(side0);
            b.EnPassantFile.Should().Be(ep0);
            b.HalfmoveClock.Should().Be(half0);
            b.OccAll.Should().Be(occ0);
            b.HashHistoryCount.Should().Be(hash0);
        }

        [Fact]
        public void NullMove_DoesNotAffectRepetitionOrHistory()
        {
            var b = BoardFactory.FromFenOrStart("startpos");

            var hashInitial = b.HashHistoryCount;
            var moveHistoryInitial = b.MoveHistory.Count;
            var undoHistoryInitial = b.UndoHistory.Count;

            // Make a real move so history and repetition tracking are in play.
            var legal = b.GenerateLegal();
            legal.Length.Should().BeGreaterThan(0);
            var firstMove = legal[0];
            var undo = b.MakeMove(firstMove);

            var hashBeforeNull = b.HashHistoryCount;
            var moveHistoryBeforeNull = b.MoveHistory.Count;
            var undoHistoryBeforeNull = b.UndoHistory.Count;

            var state = b.MakeNullMove();

            // During the null move, HashHistoryCount may differ because ZKey changed
            // but we must not record a null move as a real move in history.
            var moveHistoryDuring = b.MoveHistory.Count;
            var undoHistoryDuring = b.UndoHistory.Count;

            b.UnmakeNullMove(state);

            var hashAfterNull = b.HashHistoryCount;
            var moveHistoryAfterNull = b.MoveHistory.Count;
            var undoHistoryAfterNull = b.UndoHistory.Count;

            // Null moves must not be recorded as real moves.
            moveHistoryDuring.Should().Be(moveHistoryBeforeNull);
            undoHistoryDuring.Should().Be(undoHistoryBeforeNull);

            moveHistoryAfterNull.Should().Be(moveHistoryBeforeNull);
            undoHistoryAfterNull.Should().Be(undoHistoryBeforeNull);

            // After the null-move round trip, repetition count for the current ZKey
            // should be exactly as it was before the null move.
            hashAfterNull.Should().Be(hashBeforeNull);

            // Cleanup: unmake the real move and ensure we return to original repetition + history.
            b.UnmakeMove(firstMove, undo);

            b.HashHistoryCount.Should().Be(hashInitial);
            b.MoveHistory.Count.Should().Be(moveHistoryInitial);
            b.UndoHistory.Count.Should().Be(undoHistoryInitial);
        }
    }
}
