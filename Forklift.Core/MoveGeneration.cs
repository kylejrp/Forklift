using System.Collections.Generic;
using System.Numerics;
using static Forklift.Core.Board;

namespace Forklift.Core
{
    /// <summary>
    /// Provides methods for generating pseudo-legal chess moves.
    /// </summary>
    public static class MoveGeneration
    {
        private static readonly int[] RookDirs = { +1, -1, +16, -16 };
        private static readonly int[] BishopDirs = { +15, +17, -15, -17 };

        /// <summary>
        /// Generates all pseudo-legal moves for the current board state.
        /// </summary>
        /// <param name="board">The chessboard.</param>
        /// <param name="moves">The list to populate with generated moves.</param>
        /// <param name="sideToMove">The side to move.</param>
        public static void GeneratePseudoLegal(Board board, IList<Board.Move> moves, Color sideToMove)
        {
            moves.Clear();

            GeneratePawnMoves(board, moves, sideToMove);
            GenerateKnightMoves(board, moves, sideToMove);

            // Bishops, Rooks, Queens (queens = rook rays + bishop rays)
            GenerateSliderMoves(board, moves, sideToMove, sideToMove.IsWhite() ? Piece.WhiteBishop : Piece.BlackBishop, BishopDirs);
            GenerateSliderMoves(board, moves, sideToMove, sideToMove.IsWhite() ? Piece.WhiteRook : Piece.BlackRook, RookDirs);
            GenerateSliderMoves(board, moves, sideToMove, sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen, RookDirs);
            GenerateSliderMoves(board, moves, sideToMove, sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen, BishopDirs);

            GenerateKingMoves(board, moves, sideToMove);
            GenerateCastling(board, moves, sideToMove);
            GenerateEnPassant(board, moves, sideToMove);
        }

        // --- Pawns (pushes, captures, promotions; EP is generated separately) -------------

        private static void GeneratePawnMoves(Board board, IList<Board.Move> moves, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            Piece pawn = white ? Piece.WhitePawn : Piece.BlackPawn;
            ulong pawns = board.GetPieceBitboard(pawn);

            while (pawns != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(pawns);
                pawns &= pawns - 1;

                var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));
                int rank = from88 >> 4;

                // Forward one
                UnsafeSquare0x88 one = white ? from88 + 16 : from88 - 16;
                if (!Squares.IsOffboard(one))
                {
                    var one88 = (Square0x88)one;
                    if (board.At(one88) == Piece.Empty)
                    {
                        // Promotion push
                        if ((white && rank == 6) || (!white && rank == 1))
                        {
                            foreach (var promo in PromoPieces(sideToMove))
                                moves.Add(Move.PromotionPush(from88, one88, white ? Piece.WhitePawn : Piece.BlackPawn, promo));
                        }
                        else
                        {
                            moves.Add(Move.Normal(from88, one88, pawn));

                            // Forward two (double push) from start rank if clear
                            bool startRank = white ? (rank == 1) : (rank == 6);
                            if (startRank)
                            {
                                UnsafeSquare0x88 two = white ? (one + 16) : (one - 16);
                                if (!Squares.IsOffboard(two))
                                {
                                    var two88 = (Square0x88)two;
                                    if (board.At(two88) == Piece.Empty)
                                        moves.Add(Move.Normal(from88, two88, pawn));
                                }
                            }
                        }
                    }
                }

                // Diagonal captures (no EP here)
                int[] caps = white ? new[] { +15, +17 } : new[] { -15, -17 };
                foreach (var d in caps)
                {
                    var toUnsafe = new UnsafeSquare0x88(from88.Value + d);
                    if (Squares.IsOffboard(toUnsafe)) continue;

                    var to88 = (Square0x88)toUnsafe;
                    var target = board.At(to88);
                    if (target == Piece.Empty) continue;
                    if (white == target.IsWhite) continue; // own piece

                    // Promotion capture
                    if ((white && rank == 6) || (!white && rank == 1))
                    {
                        foreach (var promo in PromoPieces(sideToMove))
                            moves.Add(Board.Move.PromotionCapture(from88, to88, white ? Piece.WhitePawn : Piece.BlackPawn, target, promo));
                    }
                    else
                    {
                        moves.Add(Move.Capture(from88, to88, pawn, target));
                    }
                }
            }
        }

        private static Piece[] PromoPieces(Color sideToMove) => sideToMove.IsWhite()
            ? new[] { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight }
            : new[] { Piece.BlackQueen, Piece.BlackRook, Piece.BlackBishop, Piece.BlackKnight };

        // --- Knights ----------------------------------------------------------------------

        private static void GenerateKnightMoves(Board board, IList<Board.Move> moves, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            // 0x88 deltas: (+/-2, +/-1) and (+/-1, +/-2)
            ReadOnlySpan<int> deltas = stackalloc int[] { +33, +31, +18, +14, -14, -18, -31, -33 };

            Piece mover = white ? Piece.WhiteKnight : Piece.BlackKnight;
            ulong bb = board.GetPieceBitboard(mover);

            while (bb != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(bb);
                bb &= bb - 1;

                var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));
                foreach (int d in deltas)
                {
                    var toUnsafe = new UnsafeSquare0x88(from88.Value + d);
                    if (Squares.IsOffboard(toUnsafe)) continue;

                    var to88 = (Square0x88)toUnsafe;
                    var target = board.At(to88);

                    if (target == Piece.Empty)
                    {
                        moves.Add(Move.Normal(from88, to88, mover));
                    }
                    else if (white != target.IsWhite)
                    {
                        moves.Add(Move.Capture(from88, to88, mover, target));
                    }
                }
            }
        }

        // --- Sliders (bishops/rooks/queens) ----------------------------------------------

        private static void GenerateSliderMoves(Board board, IList<Board.Move> moves, Color sideToMove, Piece piece, int[] dirs)
        {
            bool white = sideToMove.IsWhite();
            ulong bb = board.GetPieceBitboard(piece);

            while (bb != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(bb);
                bb &= bb - 1;

                var from = (UnsafeSquare0x88)Squares.ConvertTo0x88Index(new Square0x64(s64));

                foreach (var d in dirs)
                {
                    var to = from;
                    while (true)
                    {
                        to = new UnsafeSquare0x88(to.Value + d);
                        if (Squares.IsOffboard(to)) break;

                        var to88 = (Square0x88)to;
                        var target = board.At(to88);

                        if (target == Piece.Empty)
                        {
                            moves.Add(Move.Normal((Square0x88)from, to88, piece));
                            continue;
                        }

                        if (white != target.IsWhite)
                            moves.Add(Move.Capture((Square0x88)from, to88, piece, target));

                        break; // stop on first occupied square
                    }
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------

        private static void GenerateKingMoves(Board board, IList<Board.Move> moves, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            ReadOnlySpan<int> deltas = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };

            Piece king = white ? Piece.WhiteKing : Piece.BlackKing;
            ulong bb = board.GetPieceBitboard(king);
            if (bb == 0) return;

            int s64 = BitOperations.TrailingZeroCount(bb);
            var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));

            foreach (int d in deltas)
            {
                var toUnsafe = new UnsafeSquare0x88(from88.Value + d);
                if (Squares.IsOffboard(toUnsafe)) continue;

                var to88 = (Square0x88)toUnsafe;
                var target = board.At(to88);

                if (target == Piece.Empty)
                {
                    moves.Add(Move.Normal(from88, to88, king));
                }
                else if (white != target.IsWhite)
                {
                    moves.Add(Move.Capture(from88, to88, king, target));
                }
            }
        }

        // --- Castling (requires empty path + no attacked transit squares) -----------------

        private static void GenerateCastling(Board board, IList<Board.Move> moves, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();

            ulong kingBB = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (kingBB == 0) return;

            var k64 = (Square0x64)BitOperations.TrailingZeroCount(kingBB);
            if (board.InCheck(sideToMove)) return;

            if (white)
            {
                // White king side: e1,f1,g1 must be safe and f1,g1 empty
                if ((board.CastlingRights & Board.CastlingRightsFlags.WhiteKing) != 0)
                {
                    var f1 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("f1"));
                    var g1 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("g1"));

                    if (board.At((Square0x88)f1) == Piece.Empty && board.At((Square0x88)g1) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k64, Color.Black) &&
                            !board.IsSquareAttacked(f1, Color.Black) &&
                            !board.IsSquareAttacked(g1, Color.Black))
                        {
                            moves.Add(Move.CastleKingSide(Color.White));
                        }
                    }
                }

                // White queen side: d1,c1 must be safe and d1,c1 empty (b1 may be occupied)
                if ((board.CastlingRights & Board.CastlingRightsFlags.WhiteQueen) != 0)
                {
                    var d1 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("d1"));
                    var c1 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("c1"));

                    if (board.At((Square0x88)d1) == Piece.Empty && board.At((Square0x88)c1) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k64, Color.Black) &&
                            !board.IsSquareAttacked(d1, Color.Black) &&
                            !board.IsSquareAttacked(c1, Color.Black))
                        {
                            moves.Add(Move.CastleQueenSide(Color.White));
                        }
                    }
                }
            }
            else
            {
                // Black king side
                if ((board.CastlingRights & Board.CastlingRightsFlags.BlackKing) != 0)
                {
                    var f8 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("f8"));
                    var g8 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("g8"));

                    if (board.At((Square0x88)f8) == Piece.Empty && board.At((Square0x88)g8) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k64, Color.White) &&
                            !board.IsSquareAttacked(f8, Color.White) &&
                            !board.IsSquareAttacked(g8, Color.White))
                        {
                            moves.Add(Move.CastleKingSide(Color.Black));
                        }
                    }
                }

                // Black queen side
                if ((board.CastlingRights & Board.CastlingRightsFlags.BlackQueen) != 0)
                {
                    var d8 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("d8"));
                    var c8 = Squares.ParseAlgebraicTo0x64(new AlgebraicNotation("c8"));

                    if (board.At((Square0x88)d8) == Piece.Empty && board.At((Square0x88)c8) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k64, Color.White) &&
                            !board.IsSquareAttacked(d8, Color.White) &&
                            !board.IsSquareAttacked(c8, Color.White))
                        {
                            moves.Add(Move.CastleQueenSide(Color.Black));
                        }
                    }
                }
            }
        }

        // --- En Passant (from Board.EnPassantFile / availability pre-check) ---------------

        private static void GenerateEnPassant(Board board, IList<Board.Move> moves, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            if (board.EnPassantFile is not FileIndex file) return;

            // Must be the immediate reply after a double-push
            if (!board.EnPassantAvailableFor(sideToMove)) return;

            // EP target is the passed-over square:
            // For white-to-move, target is on rank 6 (index 5); for black-to-move, rank 3 (index 2).
            int epRank = white ? 5 : 2;
            var ep88 = new Square0x88((epRank << 4) | file.Value);

            // The pawn that moved two squares sits behind the EP target
            var capturedSqUnsafe = white
                ? new UnsafeSquare0x88(ep88.Value - 16)
                : new UnsafeSquare0x88(ep88.Value + 16);

            var captured = white ? Piece.BlackPawn : Piece.WhitePawn;
            if (Squares.IsOffboard(capturedSqUnsafe) || board.At((Square0x88)capturedSqUnsafe) != captured)
                return;

            // Capturing pawns are adjacent, one rank behind the target
            var candidates = white
                ? new[] { new UnsafeSquare0x88(ep88.Value - 15), new UnsafeSquare0x88(ep88.Value - 17) }
                : new[] { new UnsafeSquare0x88(ep88.Value + 15), new UnsafeSquare0x88(ep88.Value + 17) };

            var mover = white ? Piece.WhitePawn : Piece.BlackPawn;

            foreach (var fromUnsafe in candidates)
            {
                if (Squares.IsOffboard(fromUnsafe)) continue;
                var from88 = (Square0x88)fromUnsafe;
                if (board.At(from88) != mover) continue;

                // Use the dedicated EP factory (captured piece is inferred/validated in MakeMove)
                moves.Add(Move.EnPassant(from88, ep88, mover));
            }
        }
    }
}
