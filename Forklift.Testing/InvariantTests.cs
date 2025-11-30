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
                b.MakeMove(mv, out var u);
                // occasionally undo
                if (rng.NextDouble() < 0.3) b.UnmakeMove(mv, u);
            }

            // Cross-check mailboxes/bitboards/OccAll
            ulong recomputedAll = 0UL;
            ulong recomputedWhite = 0UL;
            ulong recomputedBlack = 0UL;

            for (int s88Index = 0; s88Index < 128; s88Index++)
            {
                if (Squares.IsOffboard(s88Index)) continue;
                var p = b.At88(s88Index);
                if (p == Piece.Empty) continue;
                var s64 = Squares.Convert0x88IndexTo0x64Index(s88Index);
                ulong bit = 1UL << s64;
                recomputedAll |= bit;
                if (p.IsWhite) recomputedWhite |= bit; else recomputedBlack |= bit;
            }

            b.OccAll.Should().Be(recomputedAll);
            b.OccWhite.Should().Be(recomputedWhite);
            b.OccBlack.Should().Be(recomputedBlack);
        }
    }
}
