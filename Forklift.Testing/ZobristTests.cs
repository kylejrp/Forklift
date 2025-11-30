using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class ZobristTests
    {
        [Fact]
        public void MakeUnmake_Zobrist_RoundTrips_OnRandomSequence()
        {
            var b = BoardFactory.FromFenOrStart("startpos");
            var rng = new System.Random(7);
            var snapshots = new Stack<(Board.Move mv, Board.Undo u, ulong key)>();

            for (int i = 0; i < 100; i++)
            {
                var legals = b.GenerateLegal().ToList();
                if (legals.Count == 0) break;
                var mv = legals[rng.Next(legals.Count)];
                var keyBefore = b.ZKey;
                var u = b.MakeMove(mv);
                snapshots.Push((mv, u, keyBefore));
                // sanity: recompute full key sometimes
                if (i % 9 == 0)
                {
                    var full = RecomputeZ(b);
                    b.ZKey.Should().Be(full);
                }
            }
            while (snapshots.Count > 0)
            {
                var (mv, u, keyBefore) = snapshots.Pop();
                b.UnmakeMove(mv, u);
                b.ZKey.Should().Be(keyBefore);
            }

            static ulong RecomputeZ(Board bb)
            {
                // uses public UpdateZobristFull semantics; if it's private, expose a Debug recompute or mirror here
                // For the test, mirror the logic:
                ulong key = 0;
                for (int sq88Index = 0; sq88Index < 128; sq88Index++)
                {
                    if (Squares.IsOffboard(sq88Index)) continue;
                    var p = bb.At88(sq88Index);
                    if (p == Piece.Empty) continue;
                    var s64 = Squares.Convert0x88IndexTo0x64Index(sq88Index);
                    key ^= bb.Tables.Zobrist.PieceSquare[p.PieceIndex, s64];
                }
                if (!bb.SideToMove.IsWhite()) key ^= bb.Tables.Zobrist.SideToMove;
                if (bb.EnPassantFile is FileIndex epf) key ^= bb.Tables.Zobrist.EnPassant[epf];
                key ^= bb.Tables.Zobrist.Castling[(int)bb.CastlingRights & 0xF];
                return key;
            }
        }
    }
}
