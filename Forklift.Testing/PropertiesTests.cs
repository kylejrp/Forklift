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

        [Property(MaxTest = 100)]
        public bool LegalMovesNeverLeaveKingInCheck(BoardGen.LegalPosition pos)
        {
            var b = pos.Value;
            foreach (var mv in b.GenerateLegal())
            {
                var u = b.MakeMove(mv);
                bool ok = !b.InCheck(b.SideToMove.Flip());
                b.UnmakeMove(mv, u);
                if (!ok) return false;
            }
            return true;
        }
    }
}
