using ChessEngine.Core;
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
            divide.Sum(kv => kv.nodes).Should().Be(total);
        }

        [Theory]
        [InlineData("startpos", 1, 20)]
        [InlineData("startpos", 2, 400)]
        public void Perft_Count_Should_Match_Expected_Values(string fenOrStart, int depth, long expectedNodes)
        {
            var b = BoardFactory.FromFenOrStart(fenOrStart);
            long nodes = Perft.Count(b, depth);
            nodes.Should().Be(expectedNodes);
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
            long totalNodes = divide.Sum(kv => kv.nodes);
            long expectedTotal = Perft.Count(b, depth);

            totalNodes.Should().Be(expectedTotal);

            // Optionally, log the divide output for debugging
            foreach (var kv in divide)
            {
                System.Diagnostics.Debug.WriteLine($"Move: {kv.moveUci}, Nodes: {kv.nodes}");
            }
        }

        [Theory]
        [InlineData("startpos")]
        public void Perft_SpotCheck(string fenOrStart)
        {
            var board = fenOrStart == "startpos" ? new Board() : BoardFactory.FromFenOrStart(fenOrStart);

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
                    var kAlg = Squares.ToAlgebraic((Square0x88)k64Black).Value;

                    // High-level breakdown from your Board.AttackerBreakdown
                    var breakdown = board.AttackerBreakdown(k64Black, byWhite: true);

                    // Raw table masks at the king square (to verify the tables themselves)
                    var T = board.Tables;
                    ulong knightFromMask = T.KnightAttackTable[k64Black];
                    ulong kingFromMask = T.KingAttackTable[k64Black];
                    ulong wpawnFromMask = T.WhitePawnAttackFrom[k64Black]; // white attackers

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
            var s = Squares.ToAlgebraic(m.From88).Value + Squares.ToAlgebraic(m.To88).Value;
            if (m.Promotion.HasValue && m.Promotion != Piece.Empty)
            {
                s += m.Promotion.Value.PromotionChar;
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
                list.Add(Squares.ToAlgebraic((Square0x64)s).Value);
            }
            return list.ToArray();
        }
    }
}
