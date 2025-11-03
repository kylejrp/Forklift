using FluentAssertions;
using Forklift.Core;
using System.Linq;
using System.Numerics;
using Xunit;

namespace Forklift.Testing
{
    public class PerftShapeTests
    {
        [Theory]
        [InlineData("startpos")]
        [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
        public void Depth1_Equals_NumberOfLegalMoves(string fenOrStart)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long nodes = Perft.Count(b, 1);
            long legal = b.GenerateLegal().LongCount();
            nodes.Should().Be(legal);
        }

        [Theory]
        [InlineData("startpos", 3)]
        public void PerftDivide_Shows_Which_FirstMove_Is_Wrong(string fenOrStart, int depth)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long total = Perft.Count(b, depth);

            var divide = Perft.Divide(b, depth);
            divide.Sum(kv => kv.Nodes).Should().Be(total);
        }

        [Theory]
        [InlineData("startpos", 1, 20)]
        [InlineData("startpos", 2, 400)]
        [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 1, 44)]
        [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 2, 1486)]
        [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 3, 62379)]
        [InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 4, 2103487)]
        //[InlineData("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 5, 89941194)]
        public void Perft_Count_Should_Match_Expected_Values(string fenOrStart, int depth, long expectedNodes)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long nodes = Perft.Count(b, depth);
            nodes.Should().Be(expectedNodes);
        }

        [Theory(Skip = "Long running tests")]
        [InlineData("startpos", 0, 1L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L)]
        [InlineData("startpos", 1, 20L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L)]
        [InlineData("startpos", 2, 400L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L)]
        [InlineData("startpos", 3, 8902L, 34L, 0L, 0L, 0L, 12L, 0L, 0L, 0L)]
        [InlineData("startpos", 4, 197281L, 1576L, 0L, 0L, 0L, 469L, 0L, 0L, 8L)]
        [InlineData("startpos", 5, 4865609L, 82719L, 258L, 0L, 0L, 27351L, 6L, 0L, 347L)]
        //[InlineData("startpos", 6, 119060324L, 2812008L, 5248L, 0L, 0L, 809099L, 329L, 46L, 10828L)]
        //[InlineData("startpos", 7, 3195901860L, 108329926L, 319617L, 883453L, 0L, 33103848L, 18026L, 1628L, 435767L)]
        //[InlineData("startpos", 8, 84998978956L, 3523740106L, 7187977L, 23605205L, 0L, 968981593L, 847039L, 147215L, 9852036L)]
        //[InlineData("startpos", 9, 2439530234167L, 125208536153L, 319496827L, 1784356000L, 17334376L, 36095901903L, 37101713L, 5547231L, 400191963L)]
        /*
        [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", 1, 48L, 8L, 0L, 2L, 0L, 0L, 0L, 0L, 0L)]
        [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", 2, 2039L, 351L, 1L, 91L, 0L, 3L, 0L, 0L, 0L)]
        [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", 3, 97862L, 17102L, 45L, 3162L, 0L, 993L, 0L, 0L, 1L)]
        [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", 4, 4085603L, 757163L, 1929L, 128013L, 15172L, 25523L, 42L, 6L, 43L)]
        */
        //[InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", 5, 193690690L, 35043416L, 73365L, 4993637L, 8392L, 3309887L, 19883L, 2637L, 30171L)]
        //[InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq -", 6, 8031647685L, 1558445089L, 3577504L, 184513607L, 56627920L, 92238050L, 568417L, 54948L, 360003L)]
        /*
        [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 1, 14L, 1L, 0L, 0L, 0L, 2L, 0L, 0L, 0L)]
        [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 2, 191L, 14L, 0L, 0L, 0L, 10L, 0L, 0L, 0L)]
        [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 3, 2812L, 209L, 2L, 0L, 0L, 267L, 3L, 0L, 0L)]
        [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 4, 43238L, 3348L, 123L, 0L, 0L, 1680L, 106L, 0L, 17L)]
        [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 5, 674624L, 52051L, 1165L, 0L, 0L, 52950L, 1292L, 3L, 0L)]
        */
        //[InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 6, 11030083L, 940350L, 33325L, 0L, 7552L, 452473L, 26067L, 0L, 2733L)]
        //[InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 7, 178633661L, 14519036L, 294874L, 0L, 140024L, 12797406L, 370630L, 3612L, 87L)]
        //[InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 8, 3009794393L, 267586558L, 8009239L, 0L, 6578076L, 135626805L, 7181487L, 1630L, 450410L)]
        public void Perft_Statistics_Should_Match_Expected_Values(
            string fen,
            int depth,
            long expectedNodes,
            long expectedCaptures,
            long expectedEp,
            long expectedCastles,
            long expectedPromotions,
            long expectedChecks,
            long expectedDiscoveryChecks,
            long expectedDoubleChecks,
            long expectedCheckmates)
        {
            var b = BoardFactory.FromFenOrStart(fen);
            b.KeepTrackOfRepetitions = false;
            var stats = Perft.Statistics(b, depth);

            stats.Nodes.Should().Be(expectedNodes, $"Nodes at depth {depth}");
            stats.Captures.Should().Be(expectedCaptures, $"Captures at depth {depth}");
            stats.EnPassant.Should().Be(expectedEp, $"En Passant at depth {depth}");
            stats.Castles.Should().Be(expectedCastles, $"Castles at depth {depth}");
            stats.Promotions.Should().Be(expectedPromotions, $"Promotions at depth {depth}");
            stats.Checks.Should().Be(expectedChecks, $"Checks at depth {depth}");
            stats.DiscoveryChecks.Should().Be(expectedDiscoveryChecks, $"Discovery Checks at depth {depth}");
            stats.DoubleChecks.Should().Be(expectedDoubleChecks, $"Double Checks at depth {depth}");
            stats.Checkmates.Should().Be(expectedCheckmates, $"Checkmates at depth {depth}");
        }

        [Theory(Skip = "Long running tests")]
        //[InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 1, 6L, 0L, 0L, 0L, 0L, 0L, 0L)]
        //[InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 2, 264L, 87L, 0L, 6L, 48L, 10L, 0L)]
        //[InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 3, 9467L, 1021L, 4L, 0L, 120L, 38L, 22L)]
        //[InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 4, 422333L, 131393L, 0L, 7795L, 60032L, 15492L, 5L)]
        [InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 5, 15833292L, 2046173L, 6512L, 0L, 329464L, 200568L, 50562L)]
        //[InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 6, 706045033L, 210369132L, 212L, 10882006L, 81102984L, 26973664L, 81076L)]
        public void Perft_Statistics_Should_Match_Expected_Values2(
            string fen,
            int depth,
            long expectedNodes,
            long expectedCaptures,
            long expectedEp,
            long expectedCastles,
            long expectedPromotions,
            long expectedChecks,
            long expectedCheckmates)
        {
            var b = BoardFactory.FromFenOrStart(fen);
            b.KeepTrackOfRepetitions = false;
            var stats = Perft.Statistics(b, depth);

            stats.Nodes.Should().Be(expectedNodes, $"Nodes at depth {depth}");
            stats.Captures.Should().Be(expectedCaptures, $"Captures at depth {depth}");
            stats.EnPassant.Should().Be(expectedEp, $"En Passant at depth {depth}");
            stats.Castles.Should().Be(expectedCastles, $"Castles at depth {depth}");
            stats.Promotions.Should().Be(expectedPromotions, $"Promotions at depth {depth}");
            stats.Checks.Should().Be(expectedChecks, $"Checks at depth {depth}");
            stats.Checkmates.Should().Be(expectedCheckmates, $"Checkmates at depth {depth}");
        }

        [Theory]
        [InlineData("8/8/8/8/8/8/8/8 w - - 0 1", 1, 0)] // Empty board
        [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 1, 20)] // Standard starting position
        public void Perft_Count_Should_Handle_Specific_Positions(string fen, int depth, long expectedNodes)
        {
            var b = BoardFactory.FromFenOrStart(fen);
            long nodes = Perft.Count(b, depth);

            nodes.Should().Be(expectedNodes);
        }

        [Theory]
        [InlineData("startpos", 2)]
        public void Perft_Divide_Should_Match_Expected_Values(string fenOrStart, int depth)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            var divide = Perft.Divide(b, depth);

            // Verify that the sum of nodes matches the total count
            long totalNodes = divide.Sum(kv => kv.Nodes);
            long expectedTotal = Perft.Count(b, depth);

            totalNodes.Should().Be(expectedTotal);

            // Optionally, log the divide output for debugging
            foreach (var kv in divide)
            {
                System.Diagnostics.Debug.WriteLine($"Move: {kv.MoveUci}, Nodes: {kv.Nodes}");
            }
        }

        [Theory]
        [InlineData("startpos")]
        public void Perft_SpotCheck(string fenOrStart)
        {
            var board = BoardFactory.FromFenOrStart(fenOrStart);

            var rootMoves = board.GenerateLegal().ToList();
            Assert.Equal(20, rootMoves.Count);

            long total = 0;

            foreach (var m in rootMoves)
            {
                var u = board.MakeMove(m);

                var legal = board.GenerateLegal().ToList();
                if (legal.Count != 20)
                {
                    var stm = board.SideToMove;
                    bool blackInCheck = board.InCheck(Color.Black);

                    var k64Black = board.FindKingSq64(Color.Black);
                    var kAlg = Squares.ToAlgebraicString((Square0x88)k64Black);

                    // High-level breakdown from your Board.AttackerBreakdown
                    var breakdown = board.AttackerBreakdownBool(k64Black, byWhite: true);

                    // Raw table masks at the king square (to verify the tables themselves)
                    var T = board.Tables;
                    ulong knightFromMask = T.KnightAttackTable[(int)k64Black];
                    ulong kingFromMask = T.KingAttackTable[(int)k64Black];
                    ulong wpawnFromMask = T.WhitePawnAttackFrom[(int)k64Black]; // white attackers

                    // Which *white* pieces actually intersect those masks?
                    var wkMask = knightFromMask & board.GetPieceBitboard(Piece.WhiteKnight);
                    var wKAdjMask = kingFromMask & board.GetPieceBitboard(Piece.WhiteKing);
                    var wpMask = wpawnFromMask & board.GetPieceBitboard(Piece.WhitePawn);

                    // Output the list of legal moves for investigation
                    var legalMovesList = string.Join("\n        ", legal.Select(x => ToUci(x)));

                    var blackPawnBB = board.GetPieceBitboard(Piece.BlackPawn);
                    Console.WriteLine(Convert.ToString((long)blackPawnBB, 2).PadLeft(64, '0'));

                    var msg = $@"
After {ToUci(m)}:
WhiteToMove                = {stm}   (expected: Black)
Black InCheck              = {blackInCheck}
Legal replies              = {legal.Count} (expected: 20)
Legal moves:
            {legalMovesList}

AttackerBreakdown on Black king {kAlg} (byWhite: true):
    Knights                  = {breakdown.knights}   attackers: [{string.Join(", ", MaskToSquares(wkMask))}]
    Kings                    = {breakdown.kings}     attackers: [{string.Join(", ", MaskToSquares(wKAdjMask))}]
    Pawns                    = {breakdown.pawns}     attackers: [{string.Join(", ", MaskToSquares(wpMask))}]
    Bishops/Queens (diagonals)= {breakdown.bishopsQueens}
    Rooks/Queens (orthogonals)= {breakdown.rooksQueens}

RAW table contents at {kAlg} (these *should be FROM-squares that attack {kAlg}):
    KnightAttackTable[{kAlg}] -> [{string.Join(", ", MaskToSquares(knightFromMask))}]
    KingAttackTable[{kAlg}]   -> [{string.Join(", ", MaskToSquares(kingFromMask))}]
    WhitePawnAttackFrom[{kAlg}] -> [{string.Join(", ", MaskToSquares(wpawnFromMask))}]

Occupancy popcounts        = W:{BitOperations.PopCount(board.GetOccupancy(Color.White))}, B:{BitOperations.PopCount(board.GetOccupancy(Color.Black))}
CastlingRights             = {board.CastlingRights}
EnPassantFile              = {(board.EnPassantFile?.ToString() ?? "null")}
";

                    // Pinpointed assertions so the failure prints the block above
                    Assert.False(blackInCheck, "Black reported in check after a normal white first move.\n" + msg);
                    Assert.Equal(Color.Black, stm);
                    Assert.True(legal.Count == 20, "Unexpected reply count.\n" + msg);
                }

                total += legal.Count;
                board.UnmakeMove(m, u);
            }

            Assert.Equal(400, total);
        }

        private static string ToUci(Board.Move m)
        {
            var s = ToAlgebraicString(m.From88) + ToAlgebraicString(m.To88);
            if (m.Promotion != Piece.Empty)
            {
                s += m.Promotion.PromotionChar;
            }
            return s;
        }

        private static string[] MaskToSquares(ulong mask)
        {
            var list = new System.Collections.Generic.List<string>(8);
            while (mask != 0)
            {
                int s = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                list.Add(ToAlgebraicString((Square0x64)s));
            }
            return list.ToArray();
        }
    }
}
