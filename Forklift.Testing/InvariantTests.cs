using ChessEngine.Core;
using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class InvariantTests
    {
        [Fact]
        public void After_ManyRandomPlies_Bitboards_Match_Mailbox_And_Occupancies()
        {
            var b = BoardFactory.FromFenOrStart("startpos");
            var rng = new System.Random(1234);

            for (int i = 0; i < 200; i++)
            {
                var legals = b.GenerateLegal().ToList();
                if (legals.Count == 0) break;

                var mv = legals[rng.Next(legals.Count)];
                var u = b.MakeMove(mv);
                // occasionally undo
                if (rng.NextDouble() < 0.3) b.UnmakeMove(mv, u);
            }

            // Cross-check mailboxes/bitboards/OccAll
            ulong recomputedAll = 0UL;
            ulong recomputedWhite = 0UL;
            ulong recomputedBlack = 0UL;

            for (int s88 = 0; s88 < 128; s88++)
            {
                if ((s88 & 0x88) != 0) continue;
                var p = b.At(s88);
                if (p == Piece.Empty) continue;
                int s64 = Squares.ConvertTo0x64Index(new Square0x88(s88)).Value;
                ulong bit = 1UL << s64;
                recomputedAll |= bit;
                if (PieceUtil.IsWhite(p)) recomputedWhite |= bit; else recomputedBlack |= bit;
            }

            b.OccAll.Should().Be(recomputedAll);
            b.OccWhite.Should().Be(recomputedWhite);
            b.OccBlack.Should().Be(recomputedBlack);
        }
    }
}