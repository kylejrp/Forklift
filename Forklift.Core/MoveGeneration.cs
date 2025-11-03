using System.Collections.Generic;
using System.Diagnostics;
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

        public static Move[] GeneratePseudoLegal(Board board, Color sideToMove)
        {
            Span<Move> moves = stackalloc Move[MoveBufferMax];
            var span = GeneratePseudoLegal(board, moves, sideToMove);
            return span.ToArray();
        }

        /// <summary>
        /// Generates all pseudo-legal moves for the current board state.
        /// </summary>
        /// <param name="board">The chessboard.</param>
        /// <param name="moves">The list to populate with generated moves.</param>
        /// <param name="sideToMove">The side to move.</param>
        public static Span<Move> GeneratePseudoLegal(Board board, Span<Move> moves, Color sideToMove)
        {
            var moveIndex = 0;

            GeneratePawnMoves(board, moves, ref moveIndex, sideToMove);
            AssertBufferOk(moveIndex, moves.Length, nameof(GeneratePawnMoves));

            GenerateKnightMoves(board, moves, ref moveIndex, sideToMove);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateKnightMoves));

            GenerateSliderMoves(board, moves, ref moveIndex, sideToMove, sideToMove.IsWhite() ? Piece.WhiteBishop : Piece.BlackBishop, BishopDirs);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateSliderMoves) + " (Bishop)");
            GenerateSliderMoves(board, moves, ref moveIndex, sideToMove, sideToMove.IsWhite() ? Piece.WhiteRook : Piece.BlackRook, RookDirs);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateSliderMoves) + " (Rook)");
            // For queens, combine rook and bishop directions to avoid duplicate moves
            var queenDirs = new int[RookDirs.Length + BishopDirs.Length];
            RookDirs.CopyTo(queenDirs, 0);
            BishopDirs.CopyTo(queenDirs, RookDirs.Length);
            GenerateSliderMoves(board, moves, ref moveIndex, sideToMove, sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen, queenDirs);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateSliderMoves) + " (Queen)");

            GenerateKingMoves(board, moves, ref moveIndex, sideToMove);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateKingMoves));
            GenerateCastling(board, moves, ref moveIndex, sideToMove);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateCastling));
            GenerateEnPassant(board, moves, ref moveIndex, sideToMove);
            AssertBufferOk(moveIndex, moves.Length, nameof(GenerateEnPassant));

            return moves[..moveIndex];
        }

        [Conditional("DEBUG")]
        static void AssertBufferOk(int index, int length, string context) =>
            Debug.Assert(index <= length, $"Move buffer overflow in {context}: {index}/{length}");

        // --- Pawns (pushes, captures, promotions; EP is generated separately) -------------

        private static void GeneratePawnMoves(Board board, Span<Move> moves, ref int moveIndex, Color sideToMove)
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
                                moves[moveIndex++] = Move.PromotionPush(from88, one88, white ? Piece.WhitePawn : Piece.BlackPawn, promo);
                        }
                        else
                        {
                            moves[moveIndex++] = Move.Normal(from88, one88, pawn);

                            // Forward two (double push) from start rank if clear
                            bool startRank = white ? (rank == 1) : (rank == 6);
                            if (startRank)
                            {
                                UnsafeSquare0x88 two = white ? (one + 16) : (one - 16);
                                if (!Squares.IsOffboard(two))
                                {
                                    var two88 = (Square0x88)two;
                                    if (board.At(two88) == Piece.Empty)
                                        moves[moveIndex++] = Move.Normal(from88, two88, pawn);
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
                            moves[moveIndex++] = Move.PromotionCapture(from88, to88, white ? Piece.WhitePawn : Piece.BlackPawn, target, promo);
                    }
                    else
                    {
                        moves[moveIndex++] = Move.Capture(from88, to88, pawn, target);
                    }
                }
            }
        }

        private static Piece[] PromoPieces(Color sideToMove) => sideToMove.IsWhite()
            ? new[] { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight }
            : new[] { Piece.BlackQueen, Piece.BlackRook, Piece.BlackBishop, Piece.BlackKnight };

        // --- Knights ----------------------------------------------------------------------

        private static void GenerateKnightMoves(Board board, Span<Move> moves, ref int moveIndex, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            Piece mover = white ? Piece.WhiteKnight : Piece.BlackKnight;
            ulong knights = board.GetPieceBitboard(mover);
            ulong occSide = board.GetOccupancy(white ? Color.White : Color.Black);
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);
            ulong occAll = board.GetAllOccupancy();

            while (knights != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(knights);
                knights &= knights - 1;

                var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));
                ulong attacks = board.Tables.KnightAttackTable[s64];

                // Quiet moves: not occupied by any piece
                ulong quiets = attacks & ~occAll;
                ulong captures = attacks & occOpp;

                // Quiet moves
                ulong q = quiets;
                while (q != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(q);
                    q &= q - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    moves[moveIndex++] = Move.Normal(from88, to88, mover);
                }

                // Captures
                ulong c = captures;
                while (c != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(c);
                    c &= c - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    var target = board.At(to88);
                    moves[moveIndex++] = Move.Capture(from88, to88, mover, target);
                }
            }
        }

        // --- Sliders (bishops/rooks/queens) ----------------------------------------------

        private static void GenerateSliderMoves(Board board, Span<Move> moves, ref int moveIndex, Color sideToMove, Piece piece, int[] dirs)
        {
            bool white = sideToMove.IsWhite();
            ulong sliders = board.GetPieceBitboard(piece);
            ulong occAll = board.GetAllOccupancy();
            ulong occSide = board.GetOccupancy(white ? Color.White : Color.Black);
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            while (sliders != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(sliders);
                sliders &= sliders - 1;
                var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));

                ulong attacks = piece switch
                {
                    var p when p == Piece.WhiteBishop || p == Piece.BlackBishop => board.BishopAttacks(new Square0x64(s64)),
                    var p when p == Piece.WhiteRook || p == Piece.BlackRook => board.RookAttacks(new Square0x64(s64)),
                    var p when p == Piece.WhiteQueen || p == Piece.BlackQueen => board.BishopAttacks(new Square0x64(s64)) | board.RookAttacks(new Square0x64(s64)),
                    _ => 0UL
                };

                // Quiet moves: not occupied by any piece
                ulong quiets = attacks & ~occAll;
                ulong captures = attacks & occOpp;

                // Quiet moves
                ulong q = quiets;
                while (q != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(q);
                    q &= q - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    moves[moveIndex++] = Move.Normal(from88, to88, piece);
                }

                // Captures
                ulong c = captures;
                while (c != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(c);
                    c &= c - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    var target = board.At(to88);
                    moves[moveIndex++] = Move.Capture(from88, to88, piece, target);
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------

        private static void GenerateKingMoves(Board board, Span<Move> moves, ref int moveIndex, Color sideToMove)
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
                    moves[moveIndex++] = Move.Normal(from88, to88, king);
                }
                else if (white != target.IsWhite)
                {
                    moves[moveIndex++] = Move.Capture(from88, to88, king, target);
                }
            }
        }

        // --- Castling (requires empty path + no attacked transit squares) -----------------

        // Precomputed squares for castling
        private static readonly Square0x64 F1_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("f1"));
        private static readonly Square0x64 G1_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("g1"));
        private static readonly Square0x88 H1_88 = Squares.ParseAlgebraicTo0x88(AlgebraicNotation.From("h1"));
        private static readonly Square0x64 B1_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("b1"));
        private static readonly Square0x64 C1_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("c1"));
        private static readonly Square0x64 D1_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("d1"));
        private static readonly Square0x88 A1_88 = Squares.ParseAlgebraicTo0x88(AlgebraicNotation.From("a1"));

        private static readonly Square0x64 F8_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("f8"));
        private static readonly Square0x64 G8_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("g8"));
        private static readonly Square0x88 H8_88 = Squares.ParseAlgebraicTo0x88(AlgebraicNotation.From("h8"));
        private static readonly Square0x64 B8_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("b8"));
        private static readonly Square0x64 C8_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("c8"));
        private static readonly Square0x64 D8_64 = Squares.ParseAlgebraicTo0x64(AlgebraicNotation.From("d8"));
        private static readonly Square0x88 A8_88 = Squares.ParseAlgebraicTo0x88(AlgebraicNotation.From("a8"));

        // Helper to check if any squares in the array are attacked for castling
        private static bool AreCastlingSquaresAttacked(Board board, Color attacker, params Square0x64[] squares)
        {
            // Compute all attack masks for the attacker once
            var tables = board.Tables;
            var kingSq = board.FindKingSq64(attacker).Value;
            ulong pawnAttacks = attacker.IsWhite()
                ? tables.WhitePawnAttackFrom[kingSq]
                : tables.BlackPawnAttackFrom[kingSq];
            ulong knightAttacks = tables.KnightAttackTable[kingSq];
            ulong kingAttacks = tables.KingAttackTable[kingSq];

            // Use board's current occupancy for sliding attacks
            ulong bishops = attacker.IsWhite() ? board.GetPieceBitboard(Piece.WhiteBishop) : board.GetPieceBitboard(Piece.BlackBishop);
            ulong rooks = attacker.IsWhite() ? board.GetPieceBitboard(Piece.WhiteRook) : board.GetPieceBitboard(Piece.BlackRook);
            ulong queens = attacker.IsWhite() ? board.GetPieceBitboard(Piece.WhiteQueen) : board.GetPieceBitboard(Piece.BlackQueen);

            // Compute sliding attacks from all enemy sliders
            ulong bishopRays = 0, rookRays = 0;
            foreach (var sq in BitboardSquares(bishops))
                bishopRays |= board.BishopAttacks(sq);
            foreach (var sq in BitboardSquares(rooks))
                rookRays |= board.RookAttacks(sq);
            foreach (var sq in BitboardSquares(queens))
            {
                bishopRays |= board.BishopAttacks(sq);
                rookRays |= board.RookAttacks(sq);
            }

            // Check all squares in one pass
            foreach (var sq in squares)
            {
                int idx = sq.Value;
                ulong mask = 1UL << idx;
                if ((pawnAttacks & mask) != 0 ||
                    (knightAttacks & mask) != 0 ||
                    (kingAttacks & mask) != 0 ||
                    (bishopRays & mask) != 0 ||
                    (rookRays & mask) != 0)
                {
                    return true;
                }
            }
            return false;

            // Helper to enumerate set bits in a bitboard
            static IEnumerable<Square0x64> BitboardSquares(ulong bb)
            {
                while (bb != 0)
                {
                    int s = BitOperations.TrailingZeroCount(bb);
                    yield return new Square0x64(s);
                    bb &= bb - 1;
                }
            }
        }

        private static void GenerateCastling(Board board, Span<Move> moves, ref int moveIndex, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();

            ulong kingBB = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (kingBB == 0) return;

            var k64 = (Square0x64)BitOperations.TrailingZeroCount(kingBB);
            if (board.InCheck(sideToMove)) return; // cannot castle out of check

            if (white)
            {
                // White king side: e1,f1,g1 must be safe; f1,g1 empty; rook on h1
                if ((board.CastlingRights & Board.CastlingRightsFlags.WhiteKing) != 0)
                {
                    if (board.At((Square0x88)F1_64) == Piece.Empty && board.At((Square0x88)G1_64) == Piece.Empty)
                    {
                        if (board.At(H1_88) == Piece.WhiteRook &&
                            !AreCastlingSquaresAttacked(board, Color.Black, k64, F1_64, G1_64))
                        {
                            moves[moveIndex++] = Board.Move.CastleKingSide(Color.White);
                        }
                    }
                }

                // White queen side: b1,c1,d1 must be empty; e1,d1,c1 must be safe; rook on a1
                if ((board.CastlingRights & Board.CastlingRightsFlags.WhiteQueen) != 0)
                {
                    if (board.At((Square0x88)B1_64) == Piece.Empty &&
                    board.At((Square0x88)C1_64) == Piece.Empty &&
                    board.At((Square0x88)D1_64) == Piece.Empty)
                    {
                        if (board.At(A1_88) == Piece.WhiteRook &&
                            !AreCastlingSquaresAttacked(board, Color.Black, k64, D1_64, C1_64))
                        {
                            moves[moveIndex++] = Board.Move.CastleQueenSide(Color.White);
                        }
                    }
                }
            }
            else
            {
                // Black king side: e8,f8,g8 must be safe; f8,g8 empty; rook on h8
                if ((board.CastlingRights & Board.CastlingRightsFlags.BlackKing) != 0)
                {
                    if (board.At((Square0x88)F8_64) == Piece.Empty && board.At((Square0x88)G8_64) == Piece.Empty)
                    {
                        if (board.At(H8_88) == Piece.BlackRook &&
                            !AreCastlingSquaresAttacked(board, Color.White, k64, F8_64, G8_64))
                        {
                            moves[moveIndex++] = Board.Move.CastleKingSide(Color.Black);
                        }
                    }
                }

                // Black queen side: b8,c8,d8 must be empty; e8,d8,c8 must be safe; rook on a8
                if ((board.CastlingRights & Board.CastlingRightsFlags.BlackQueen) != 0)
                {
                    if (board.At((Square0x88)B8_64) == Piece.Empty &&
                    board.At((Square0x88)C8_64) == Piece.Empty &&
                    board.At((Square0x88)D8_64) == Piece.Empty)
                    {
                        if (board.At(A8_88) == Piece.BlackRook &&
                            !AreCastlingSquaresAttacked(board, Color.White, k64, D8_64, C8_64))
                        {
                            moves[moveIndex++] = Board.Move.CastleQueenSide(Color.Black);
                        }
                    }
                }
            }
        }

        // --- En Passant (from Board.EnPassantFile / availability pre-check) ---------------

        private static void GenerateEnPassant(Board board, Span<Move> moves, ref int moveIndex, Color sideToMove)
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

                moves[moveIndex++] = Move.EnPassant(from88, ep88, mover, captured);
            }
        }
    }
}
