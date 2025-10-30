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
        /// <param name="isWhiteTurn">Indicates whether it is white's turn.</param>
        public static void GeneratePseudoLegal(Board board, List<Board.Move> moves, bool isWhiteTurn)
        {
            moves.Clear();

            GeneratePawnMoves(board, moves, isWhiteTurn);
            GenerateKnightMoves(board, moves, isWhiteTurn);
            GenerateSliderMoves(board, moves, isWhiteTurn, isWhiteTurn ? Piece.WhiteBishop : Piece.BlackBishop, BishopDirs);
            GenerateSliderMoves(board, moves, isWhiteTurn, isWhiteTurn ? Piece.WhiteRook : Piece.BlackRook, RookDirs);
            // Queens = rook + bishop rays
            GenerateSliderMoves(board, moves, isWhiteTurn, isWhiteTurn ? Piece.WhiteQueen : Piece.BlackQueen, RookDirs);
            GenerateSliderMoves(board, moves, isWhiteTurn, isWhiteTurn ? Piece.WhiteQueen : Piece.BlackQueen, BishopDirs);
            GenerateKingMoves(board, moves, isWhiteTurn);
            GenerateCastling(board, moves, isWhiteTurn);
            GenerateEnPassant(board, moves, isWhiteTurn);
        }

        // --- Pawns (pushes, captures, promotions; no EP here) -----------------------------

        private static void GeneratePawnMoves(Board board, List<Board.Move> moves, bool white)
        {
            ulong pawns = board.GetPieceBitboard(white ? Piece.WhitePawn : Piece.BlackPawn);

            while (pawns != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(pawns);
                pawns &= pawns - 1;

                var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));
                int rank = from88 >> 4;

                // Forward pushes
                int one = white ? from88 + 16 : from88 - 16;
                if (!Squares.IsOffboard(new Square0x88(one)) && board.At(one) == Piece.Empty)
                {
                    if ((white && rank == 6) || (!white && rank == 1))
                    {
                        // Promotion pushes (to last rank)
                        foreach (var promo in PromoPieces(white))
                            moves.Add(new Board.Move(from88, new Square0x88(one), white ? Piece.WhitePawn : Piece.BlackPawn, Piece.Empty, promo,
                                MoveKind.Promotion));
                    }
                    else
                    {
                        moves.Add(new Board.Move(from88, new Square0x88(one), white ? Piece.WhitePawn : Piece.BlackPawn));
                        // Double push
                        bool startRank = white ? (rank == 1) : (rank == 6);
                        if (startRank)
                        {
                            int two = white ? (one + 16) : (one - 16);
                            if (!Squares.IsOffboard(new Square0x88(two)) && board.At(two) == Piece.Empty)
                                moves.Add(new Board.Move(from88, new Square0x88(two), white ? Piece.WhitePawn : Piece.BlackPawn));
                        }
                    }
                }

                // Captures (no EP here)
                int[] caps = white ? new[] { +15, +17 } : new[] { -15, -17 };
                foreach (var d in caps)
                {
                    var to88 = new Square0x88(from88.Value + d);
                    if (Squares.IsOffboard(to88)) continue;
                    var target = board.At(to88);
                    if (target == Piece.Empty) continue;
                    if (white == PieceUtil.IsWhite(target)) continue; // own piece

                    if ((white && rank == 6) || (!white && rank == 1))
                    {
                        // Promotion capture
                        foreach (var promo in PromoPieces(white))
                            moves.Add(new Board.Move(from88, to88, white ? Piece.WhitePawn : Piece.BlackPawn, target, promo,
                                MoveKind.PromotionCapture));
                    }
                    else
                    {
                        moves.Add(new Board.Move(from88, to88, white ? Piece.WhitePawn : Piece.BlackPawn, target));
                    }
                }
            }
        }

        private static Piece[] PromoPieces(bool white) => white
            ? new[] { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight }
            : new[] { Piece.BlackQueen, Piece.BlackRook, Piece.BlackBishop, Piece.BlackKnight };

        // --- Knights ----------------------------------------------------------------------

        private static void GenerateKnightMoves(Board board, List<Board.Move> moves, bool white)
        {
            // 0x88 knight deltas
            // (+/-2, +/-1) and (+/-1, +/-2) in 0x88 coordinates
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
                    var to88 = new Square0x88(from88.Value + d);
                    if (Squares.IsOffboard(to88)) continue;

                    var target = board.At(to88);
                    if (target == Piece.Empty)
                    {
                        moves.Add(new Board.Move(from88, to88, mover));
                    }
                    else if (white != PieceUtil.IsWhite(target))
                    {
                        moves.Add(new Board.Move(from88, to88, mover, target));
                    }
                }
            }
        }

        // --- Sliders ----------------------------------------------------------------------

        private static void GenerateSliderMoves(Board board, List<Board.Move> moves, bool white, Piece piece, int[] dirs)
        {
            ulong ownOcc = white ? board.OccWhite : board.OccBlack;
            ulong bb = board.GetPieceBitboard(piece);
            while (bb != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(bb);
                bb &= bb - 1;

                var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));
                foreach (var d in dirs)
                {
                    var to = from88;
                    while (true)
                    {
                        to = new Square0x88(to.Value + d);
                        if (Squares.IsOffboard(to)) break;

                        var target = board.At(to);
                        if (target == Piece.Empty)
                        {
                            moves.Add(new Board.Move(from88, to, piece));
                            continue;
                        }

                        if ((white && !PieceUtil.IsWhite(target)) || (!white && PieceUtil.IsWhite(target)))
                            moves.Add(new Board.Move(from88, to, piece, target));

                        break;
                    }
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------

        private static void GenerateKingMoves(Board board, List<Board.Move> moves, bool white)
        {
            // 8 king-neighbour deltas in 0x88
            ReadOnlySpan<int> deltas = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };

            Piece king = white ? Piece.WhiteKing : Piece.BlackKing;
            ulong bb = board.GetPieceBitboard(king);
            if (bb == 0) return;

            int s64 = BitOperations.TrailingZeroCount(bb);
            var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));

            foreach (int d in deltas)
            {
                var to88 = new Square0x88(from88.Value + d);
                if (Squares.IsOffboard(to88)) continue;

                var target = board.At(to88);
                if (target == Piece.Empty)
                {
                    moves.Add(new Board.Move(from88, to88, king));
                }
                else if (white != PieceUtil.IsWhite(target))
                {
                    moves.Add(new Board.Move(from88, to88, king, target));
                }
            }
        }

        // --- Castling (fully legal: empty path + no attacked transit squares) -------------

        private static void GenerateCastling(Board board, List<Board.Move> moves, bool white)
        {
            // Must have a king and not be in check
            ulong kingBB = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (kingBB == 0) return;

            int k64 = BitOperations.TrailingZeroCount(kingBB);
            var k88 = Squares.ConvertTo0x88Index(new Square0x64(k64));

            if (board.InCheck(white)) return;

            // White: e1=4, path squares: f1=5, g1=6 (KS); d1=3, c1=2, b1=1 (QS)
            // Black: e8=116, f8=117, g8=118; d8=115, c8=114, b8=113

            if (white)
            {
                // King side
                if ((board.CastlingRights & Board.CastlingRightsFlags.WhiteKing) != 0)
                {
                    var f1 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("f1"));
                    var g1 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("g1"));
                    if (board.At(f1) == Piece.Empty && board.At(g1) == Piece.Empty)
                    {
                        // Squares e1,f1,g1 may not be attacked by black
                        if (!board.IsSquareAttacked(k88, byWhite: false) &&
                            !board.IsSquareAttacked(f1, byWhite: false) &&
                            !board.IsSquareAttacked(g1, byWhite: false))
                        {
                            moves.Add(new Board.Move(k88, g1, Piece.WhiteKing, Piece.Empty, Piece.Empty, MoveKind.CastleKing));
                        }
                    }
                }
                // Queen side
                if ((board.CastlingRights & Board.CastlingRightsFlags.WhiteQueen) != 0)
                {
                    var d1 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("d1"));
                    var c1 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("c1"));
                    var b1 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("b1"));
                    if (board.At(d1) == Piece.Empty && board.At(c1) == Piece.Empty && board.At(b1) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k88, byWhite: false) &&
                            !board.IsSquareAttacked(d1, byWhite: false) &&
                            !board.IsSquareAttacked(c1, byWhite: false))
                        {
                            moves.Add(new Board.Move(k88, c1, Piece.WhiteKing, Piece.Empty, Piece.Empty, MoveKind.CastleQueen));
                        }
                    }
                }
            }
            else
            {
                // Black king side
                if ((board.CastlingRights & Board.CastlingRightsFlags.BlackKing) != 0)
                {
                    var f8 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("f8"));
                    var g8 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("g8"));
                    if (board.At(f8) == Piece.Empty && board.At(g8) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k88, byWhite: true) &&
                            !board.IsSquareAttacked(f8, byWhite: true) &&
                            !board.IsSquareAttacked(g8, byWhite: true))
                        {
                            moves.Add(new Board.Move(k88, g8, Piece.BlackKing, Piece.Empty, Piece.Empty, MoveKind.CastleKing));
                        }
                    }
                }
                // Black queen side
                if ((board.CastlingRights & Board.CastlingRightsFlags.BlackQueen) != 0)
                {
                    var d8 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("d8"));
                    var c8 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("c8"));
                    var b8 = Squares.ParseAlgebraicTo0x88(new AlgebraicNotation("b8"));
                    if (board.At(d8) == Piece.Empty && board.At(c8) == Piece.Empty && board.At(b8) == Piece.Empty)
                    {
                        if (!board.IsSquareAttacked(k88, byWhite: true) &&
                            !board.IsSquareAttacked(d8, byWhite: true) &&
                            !board.IsSquareAttacked(c8, byWhite: true))
                        {
                            moves.Add(new Board.Move(k88, c8, Piece.BlackKing, Piece.Empty, Piece.Empty, MoveKind.CastleQueen));
                        }
                    }
                }
            }
        }

        // --- En Passant (from Board.EnPassantFile) ---------------------------------------

        private static void GenerateEnPassant(Board board, List<Board.Move> moves, bool white)
        {
            if (board.EnPassantFile is not int file) return;

            // EP target square: file at rank 5 for white, rank 2 for black
            int epRank = white ? 5 : 2;
            var ep88 = new Square0x88((epRank << 4) | file);

            // The capturing pawns are on the adjacent files on the rank behind the EP target
            // For white, pawns on rank 4 at ep88-15 / ep88-17; for black, rank 3 at ep88+15 / ep88+17
            var candidates = white
                ? new[] { new Square0x88(ep88.Value - 15), new Square0x88(ep88.Value - 17) }
                : new[] { new Square0x88(ep88.Value + 15), new Square0x88(ep88.Value + 17) };

            foreach (var from in candidates)
            {
                if (Squares.IsOffboard(from)) continue;
                var p = board.At(from);
                var needed = white ? Piece.WhitePawn : Piece.BlackPawn;
                if (p != needed) continue;

                // Destination must be empty (the EP target square)
                if (board.At(ep88) != Piece.Empty) continue;

                // Create EP move; Captured is the pawn color
                var captured = white ? Piece.BlackPawn : Piece.WhitePawn;
                moves.Add(new Board.Move(from, ep88, needed, captured, Piece.Empty, MoveKind.EnPassant));
            }
        }
    }
}
