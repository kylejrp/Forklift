using Forklift.Core;

namespace Forklift.Testing
{
    public class PrincipalVariationTableTests
    {
        internal static Board.Move TestMove(string uci, Piece mover, Piece? captured = null, Piece? promotion = null)
        {
            if (uci.Length != 4)
                throw new ArgumentException("UCI move string must be exactly 4 characters long.", nameof(uci));

            var from = ParseAlgebraicTo0x88(uci[..2]);
            var to = ParseAlgebraicTo0x88(uci[2..4]);

            if (promotion.HasValue)
            {
                if (captured.HasValue)
                {
                    return Board.Move.PromotionCapture(from, to, mover, captured.Value, promotion.Value);
                }
                else
                {
                    return Board.Move.PromotionPush(from, to, mover, promotion.Value);
                }
            }

            if (captured.HasValue)
            {
                return Board.Move.Capture(from, to, mover, captured.Value);
            }
            else
            {
                return Board.Move.Normal(from, to, mover);
            }
        }

        [Fact]
        public void Update_ShouldStoreAndPropagatePVCorrectly()
        {
            var pvTable = new PrincipalVariationTable(5);

            var m0 = TestMove("e2e4", Piece.WhitePawn);
            var m1 = TestMove("e7e5", Piece.BlackPawn);
            var m2 = TestMove("g1f3", Piece.WhiteKnight);
            var m2alt = TestMove("b8c6", Piece.WhiteKnight);

            // Simulate a real search:
            // - Enter nodes at plies 0, 1, 2 (InitPly)
            // - At ply 2, we first think m2alt is best, then replace it with m2
            // - Then bubble up through ply 1 and ply 0
            pvTable.InitPly(0);
            pvTable.InitPly(1);
            pvTable.InitPly(2);

            pvTable.Update(2, m2alt);
            pvTable.Update(2, m2);
            pvTable.Update(1, m1);
            pvTable.Update(0, m0);

            var expectedPV = new Board.Move?[] { m0, m1, m2 };

            var actualPV = pvTable.GetRootPrincipalVariation();
            if (actualPV.Length != expectedPV.Length)
            {
                Assert.Fail($"Expected PV length {expectedPV.Length}, but got {actualPV.Length}");
            }

            for (int i = 0; i < expectedPV.Length; i++)
            {
                Assert.Equal(expectedPV[i], actualPV[i]);
            }

            pvTable.Clear();
            var clearedPV = pvTable.GetRootPrincipalVariation();
            Assert.Empty(clearedPV);
        }
    }
}
