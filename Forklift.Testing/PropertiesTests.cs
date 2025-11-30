using Forklift.Core;
using FsCheck;
using FsCheck.Xunit;

namespace Forklift.Testing
{
    public class PropertiesTests
    {
        [Property(MaxTest = 100)]
        public bool SideToMoveHasExactlyOneKing(BoardGen.RandomPosition pos)
        {
            var b = pos.Value;
            return b.WhiteKingCount == 1 && b.BlackKingCount == 1;
        }

        [Property(MaxTest = 100, EndSize = 60)]
        public bool LegalMovesNeverLeaveKingInCheck(BoardGen.LegalPosition pos)
        {
            var b = pos.Value;

            var moves = b.GenerateLegal();

            // Optional micro-guard for pathological positions:
            // if (moves.Length > 64) return true;

            for (int i = 0; i < moves.Length; i++)
            {
                var mv = moves[i];
                b.MakeMove(mv, out var u);

                // This check duplicates the generator’s legality filter.
                // Keep if you explicitly want to *verify* the invariant; comment out for speed.
                bool ok = !b.InCheck(b.SideToMove.Flip());

                b.UnmakeMove(mv, u);
                if (!ok) return false;
            }
            return true;
        }
    }
}
