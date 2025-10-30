using ChessEngine.Core;
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
                for (UnsafeSquare0x88 sq88 = (UnsafeSquare0x88)0; sq88 < 128; sq88++)
                {
                    if (Squares.IsOffboard(sq88)) continue;
                    var p = bb.At((Square0x88)sq88);
                    if (p == Piece.Empty) continue;
                    int s64 = (Square0x64)sq88;
                    key ^= bb.Tables.Zobrist.PieceSquare[PieceUtil.Index(p), s64];
                }
                if (!bb.WhiteToMove) key ^= bb.Tables.Zobrist.SideToMove;
                if (bb.EnPassantFile is int epf) key ^= bb.Tables.Zobrist.EnPassant[epf];
                key ^= bb.Tables.Zobrist.Castling[(int)bb.CastlingRights & 0xF];
                return key;
            }
        }
    }
}