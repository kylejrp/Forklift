using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static Forklift.Core.Board;

namespace Forklift.Core
{
    /// <summary>
    /// Provides methods for generating pseudo-legal chess moves.
    /// </summary>
    public static class MoveGeneration
    {
        // Directions for 0x88 deltas (rook, bishop, king/knight use lookup tables)
        private static readonly int[] RookDirs = { +1, -1, +16, -16 };
        private static readonly int[] BishopDirs = { +15, +17, -15, -17 };
        private static readonly int[] QueenDirs = { +1, -1, +16, -16, +15, +17, -15, -17 };

        // Pawn capture deltas (0x88)
        private static readonly int[] WhitePawnCaps = { +15, +17 };
        private static readonly int[] BlackPawnCaps = { -15, -17 };

        // Promotion piece sets (no per-call array allocation)
        private static readonly Piece[] WhitePromos = { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight };
        private static readonly Piece[] BlackPromos = { Piece.BlackQueen, Piece.BlackRook, Piece.BlackBishop, Piece.BlackKnight };

        public static Move[] GeneratePseudoLegal(Board board, Color sideToMove)
        {
            Span<Move> moves = stackalloc Move[MoveBufferMax];
            var span = GeneratePseudoLegal(board, moves, sideToMove);
            return span.ToArray();
        }

        /// <summary>
        /// Generates all pseudo-legal moves for the current board state.
        /// </summary>
        public static Span<Move> GeneratePseudoLegal(Board board, Span<Move> moves, Color sideToMove)
        {
            int w = 0;

            GeneratePawnMoves(board, moves, ref w, sideToMove);
            AssertBufferOk(w, moves.Length, nameof(GeneratePawnMoves));

            GenerateKnightMoves(board, moves, ref w, sideToMove);
            AssertBufferOk(w, moves.Length, nameof(GenerateKnightMoves));

            // Sliders
            GenerateSliderMoves(board, moves, ref w, sideToMove,
                sideToMove.IsWhite() ? Piece.WhiteBishop : Piece.BlackBishop, BishopDirs);
            AssertBufferOk(w, moves.Length, nameof(GenerateSliderMoves) + " (Bishop)");

            GenerateSliderMoves(board, moves, ref w, sideToMove,
                sideToMove.IsWhite() ? Piece.WhiteRook : Piece.BlackRook, RookDirs);
            AssertBufferOk(w, moves.Length, nameof(GenerateSliderMoves) + " (Rook)");

            GenerateSliderMoves(board, moves, ref w, sideToMove,
                sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen, QueenDirs);
            AssertBufferOk(w, moves.Length, nameof(GenerateSliderMoves) + " (Queen)");

            GenerateKingMoves(board, moves, ref w, sideToMove);
            AssertBufferOk(w, moves.Length, nameof(GenerateKingMoves));

            GenerateCastling(board, moves, ref w, sideToMove);
            AssertBufferOk(w, moves.Length, nameof(GenerateCastling));

            GenerateEnPassant(board, moves, ref w, sideToMove);
            AssertBufferOk(w, moves.Length, nameof(GenerateEnPassant));

            return moves[..w];
        }

        [Conditional("DEBUG")]
        static void AssertBufferOk(int index, int length, string context) =>
            Debug.Assert(index <= length, $"Move buffer overflow in {context}: {index}/{length}");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Emit(ref Span<Move> buffer, ref int w, Move m)
        {
            buffer[w++] = m;
        }

        // --- Pawns (pushes, captures, promotions; EP is generated separately) -------------
        private static void GeneratePawnMoves(Board board, Span<Move> moves, ref int w, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            Piece pawn = white ? Piece.WhitePawn : Piece.BlackPawn;
            ulong pawns = board.GetPieceBitboard(pawn);

            // Occupancy used for forward-block checks; look it up once.
            ulong occAll = board.GetAllOccupancy();

            while (pawns != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(pawns);
                pawns &= pawns - 1;

                var from64 = new Square0x64(s64);
                var from88 = Squares.ConvertTo0x88Index(from64);
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
                            var promos = white ? WhitePromos : BlackPromos;
                            for (int i = 0; i < promos.Length; i++)
                                Emit(ref moves, ref w, Move.PromotionPush(from88, one88, pawn, promos[i]));
                        }
                        else
                        {
                            Emit(ref moves, ref w, Move.Normal(from88, one88, pawn));

                            // Double push from start rank (only if square two ahead empty)
                            if (white ? (rank == 1) : (rank == 6))
                            {
                                UnsafeSquare0x88 two = white ? (one + 16) : (one - 16);
                                if (!Squares.IsOffboard(two) && board.At((Square0x88)two) == Piece.Empty)
                                    Emit(ref moves, ref w, Move.Normal(from88, (Square0x88)two, pawn));
                            }
                        }
                    }
                }

                // Captures (no EP here)
                var caps = white ? WhitePawnCaps : BlackPawnCaps;
                for (int i = 0; i < caps.Length; i++)
                {
                    var toUnsafe = new UnsafeSquare0x88(from88.Value + caps[i]);
                    if (Squares.IsOffboard(toUnsafe)) continue;

                    var to88 = (Square0x88)toUnsafe;
                    var target = board.At(to88);
                    if (target == Piece.Empty || target.IsWhite == white) continue;

                    // Promotion capture
                    if ((white && rank == 6) || (!white && rank == 1))
                    {
                        var promos = white ? WhitePromos : BlackPromos;
                        for (int p = 0; p < promos.Length; p++)
                            Emit(ref moves, ref w, Move.PromotionCapture(from88, to88, pawn, target, promos[p]));
                    }
                    else
                    {
                        Emit(ref moves, ref w, Move.Capture(from88, to88, pawn, target));
                    }
                }
            }
        }

        // --- Knights ----------------------------------------------------------------------
        private static void GenerateKnightMoves(Board board, Span<Move> moves, ref int w, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            Piece mover = white ? Piece.WhiteKnight : Piece.BlackKnight;
            ulong knights = board.GetPieceBitboard(mover);
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            while (knights != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(knights);
                knights &= knights - 1;

                var from64 = new Square0x64(s64);
                var from88 = Squares.ConvertTo0x88Index(from64);
                ulong attacks = board.Tables.KnightAttackTable[s64];

                // Quiet moves
                ulong quiets = attacks & ~occAll;
                while (quiets != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(quiets);
                    quiets &= quiets - 1;
                    Emit(ref moves, ref w, Move.Normal(from88, Squares.ConvertTo0x88Index(new Square0x64(toS64)), mover));
                }

                // Captures
                ulong captures = attacks & occOpp;
                while (captures != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(captures);
                    captures &= captures - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    Emit(ref moves, ref w, Move.Capture(from88, to88, mover, board.At(to88)));
                }
            }
        }

        // --- Sliders (bishops/rooks/queens) ----------------------------------------------
        private static void GenerateSliderMoves(Board board, Span<Move> moves, ref int w, Color sideToMove, Piece piece, int[] dirs /*unused but kept for compatibility*/)
        {
            bool white = sideToMove.IsWhite();
            ulong sliders = board.GetPieceBitboard(piece);
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            while (sliders != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(sliders);
                sliders &= sliders - 1;

                var from64 = new Square0x64(s64);
                var from88 = Squares.ConvertTo0x88Index(from64);

                // Use your fast magics/lookup methods; union for queen
                ulong attacks =
                    (piece == Piece.WhiteBishop || piece == Piece.BlackBishop) ? board.BishopAttacks(from64) :
                    (piece == Piece.WhiteRook || piece == Piece.BlackRook) ? board.RookAttacks(from64) :
                    /* queen */                                                     (board.BishopAttacks(from64) | board.RookAttacks(from64));

                // Quiet moves
                ulong quiets = attacks & ~occAll;
                while (quiets != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(quiets);
                    quiets &= quiets - 1;
                    Emit(ref moves, ref w, Move.Normal(from88, Squares.ConvertTo0x88Index(new Square0x64(toS64)), piece));
                }

                // Captures
                ulong captures = attacks & occOpp;
                while (captures != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(captures);
                    captures &= captures - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    Emit(ref moves, ref w, Move.Capture(from88, to88, piece, board.At(to88)));
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------
        private static void GenerateKingMoves(Board board, Span<Move> moves, ref int w, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            ReadOnlySpan<int> deltas = stackalloc int[] { +1, -1, +16, -16, +15, +17, -15, -17 };

            Piece king = white ? Piece.WhiteKing : Piece.BlackKing;
            ulong bb = board.GetPieceBitboard(king);
            if (bb == 0) return;

            int s64 = BitOperations.TrailingZeroCount(bb);
            var from88 = Squares.ConvertTo0x88Index(new Square0x64(s64));

            for (int i = 0; i < deltas.Length; i++)
            {
                var toUnsafe = new UnsafeSquare0x88(from88.Value + deltas[i]);
                if (Squares.IsOffboard(toUnsafe)) continue;

                var to88 = (Square0x88)toUnsafe;
                var target = board.At(to88);

                if (target == Piece.Empty)
                {
                    Emit(ref moves, ref w, Move.Normal(from88, to88, king));
                }
                else if (target.IsWhite != white)
                {
                    Emit(ref moves, ref w, Move.Capture(from88, to88, king, target));
                }
            }
        }

        // --- Castling (requires empty path + no attacked transit squares) -----------------
        // Precomputed 0x64 squares used by castling checks
        private static readonly Square0x64 E1_64 = Squares.ParseAlgebraicTo0x64("e1");
        private static readonly Square0x64 F1_64 = Squares.ParseAlgebraicTo0x64("f1");
        private static readonly Square0x64 G1_64 = Squares.ParseAlgebraicTo0x64("g1");
        private static readonly Square0x64 D1_64 = Squares.ParseAlgebraicTo0x64("d1");
        private static readonly Square0x64 C1_64 = Squares.ParseAlgebraicTo0x64("c1");
        private static readonly Square0x64 B1_64 = Squares.ParseAlgebraicTo0x64("b1");
        private static readonly Square0x88 H1_88 = Squares.ParseAlgebraicTo0x88("h1");
        private static readonly Square0x88 A1_88 = Squares.ParseAlgebraicTo0x88("a1");

        private static readonly Square0x64 E8_64 = Squares.ParseAlgebraicTo0x64("e8");
        private static readonly Square0x64 F8_64 = Squares.ParseAlgebraicTo0x64("f8");
        private static readonly Square0x64 G8_64 = Squares.ParseAlgebraicTo0x64("g8");
        private static readonly Square0x64 D8_64 = Squares.ParseAlgebraicTo0x64("d8");
        private static readonly Square0x64 C8_64 = Squares.ParseAlgebraicTo0x64("c8");
        private static readonly Square0x64 B8_64 = Squares.ParseAlgebraicTo0x64("b8");
        private static readonly Square0x88 H8_88 = Squares.ParseAlgebraicTo0x88("h8");
        private static readonly Square0x88 A8_88 = Squares.ParseAlgebraicTo0x88("a8");

        private static void GenerateCastling(Board board, Span<Move> moves, ref int w, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();

            // King must exist and not be in check now.
            ulong kingBB = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (kingBB == 0) return;
            if (board.InCheck(sideToMove)) return;

            Color enemy = sideToMove.Flip();

            if (white)
            {
                // King side: squares f1,g1 empty; e1,f1,g1 not attacked by Black
                if ((board.CastlingRights & CastlingRightsFlags.WhiteKing) != 0)
                {
                    if (board.At((Square0x88)F1_64) == Piece.Empty &&
                        board.At((Square0x88)G1_64) == Piece.Empty &&
                        board.At(H1_88) == Piece.WhiteRook &&
                        !board.IsSquareAttacked(E1_64, enemy) &&
                        !board.IsSquareAttacked(F1_64, enemy) &&
                        !board.IsSquareAttacked(G1_64, enemy))
                    {
                        Emit(ref moves, ref w, Board.Move.CastleKingSide(Color.White));
                    }
                }

                // Queen side: squares b1,c1,d1 empty; e1,d1,c1 not attacked by Black
                if ((board.CastlingRights & CastlingRightsFlags.WhiteQueen) != 0)
                {
                    if (board.At((Square0x88)B1_64) == Piece.Empty &&
                        board.At((Square0x88)C1_64) == Piece.Empty &&
                        board.At((Square0x88)D1_64) == Piece.Empty &&
                        board.At(A1_88) == Piece.WhiteRook &&
                        !board.IsSquareAttacked(E1_64, enemy) &&
                        !board.IsSquareAttacked(D1_64, enemy) &&
                        !board.IsSquareAttacked(C1_64, enemy))
                    {
                        Emit(ref moves, ref w, Board.Move.CastleQueenSide(Color.White));
                    }
                }
            }
            else
            {
                // King side: squares f8,g8 empty; e8,f8,g8 not attacked by White
                if ((board.CastlingRights & CastlingRightsFlags.BlackKing) != 0)
                {
                    if (board.At((Square0x88)F8_64) == Piece.Empty &&
                        board.At((Square0x88)G8_64) == Piece.Empty &&
                        board.At(H8_88) == Piece.BlackRook &&
                        !board.IsSquareAttacked(E8_64, enemy) &&
                        !board.IsSquareAttacked(F8_64, enemy) &&
                        !board.IsSquareAttacked(G8_64, enemy))
                    {
                        Emit(ref moves, ref w, Board.Move.CastleKingSide(Color.Black));
                    }
                }

                // Queen side: squares b8,c8,d8 empty; e8,d8,c8 not attacked by White
                if ((board.CastlingRights & CastlingRightsFlags.BlackQueen) != 0)
                {
                    if (board.At((Square0x88)B8_64) == Piece.Empty &&
                        board.At((Square0x88)C8_64) == Piece.Empty &&
                        board.At((Square0x88)D8_64) == Piece.Empty &&
                        board.At(A8_88) == Piece.BlackRook &&
                        !board.IsSquareAttacked(E8_64, enemy) &&
                        !board.IsSquareAttacked(D8_64, enemy) &&
                        !board.IsSquareAttacked(C8_64, enemy))
                    {
                        Emit(ref moves, ref w, Board.Move.CastleQueenSide(Color.Black));
                    }
                }
            }
        }

        /// <summary>
        /// Fast, target-centric "is square attacked by side?" using precomputed tables
        /// and current occupancy. Avoids scanning all enemy sliders piece-by-piece.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSquareAttackedFast(Board board, Color attacker, Square0x64 target, ulong occAll)
        {
            var t = board.Tables;
            int idx = target.Value;

            // Knight / King sources (tables are "attack-from masks keyed by target")
            ulong knights = attacker.IsWhite() ? board.GetPieceBitboard(Piece.WhiteKnight) : board.GetPieceBitboard(Piece.BlackKnight);
            if ((t.KnightAttackTable[idx] & knights) != 0) return true;

            ulong king = attacker.IsWhite() ? board.GetPieceBitboard(Piece.WhiteKing) : board.GetPieceBitboard(Piece.BlackKing);
            if ((t.KingAttackTable[idx] & king) != 0) return true;

            // Pawn sources (separate white/black)
            if (attacker.IsWhite())
            {
                ulong wp = board.GetPieceBitboard(Piece.WhitePawn);
                if ((t.WhitePawnAttackFrom[idx] & wp) != 0) return true;
            }
            else
            {
                ulong bp = board.GetPieceBitboard(Piece.BlackPawn);
                if ((t.BlackPawnAttackFrom[idx] & bp) != 0) return true;
            }

            // Sliding: compute rays FROM the target and see if any enemy slider sits on them.
            // This is cheaper than expanding all enemy sliders every time.
            ulong bishops = attacker.IsWhite()
                ? (board.GetPieceBitboard(Piece.WhiteBishop) | board.GetPieceBitboard(Piece.WhiteQueen))
                : (board.GetPieceBitboard(Piece.BlackBishop) | board.GetPieceBitboard(Piece.BlackQueen));
            if ((board.BishopAttacks(target) & bishops) != 0) return true;

            ulong rooks = attacker.IsWhite()
                ? (board.GetPieceBitboard(Piece.WhiteRook) | board.GetPieceBitboard(Piece.WhiteQueen))
                : (board.GetPieceBitboard(Piece.BlackRook) | board.GetPieceBitboard(Piece.BlackQueen));
            if ((board.RookAttacks(target) & rooks) != 0) return true;

            return false;
        }

        // --- En Passant (from Board.EnPassantFile / availability pre-check) ---------------
        private static void GenerateEnPassant(Board board, Span<Move> moves, ref int w, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            if (board.EnPassantFile is not FileIndex file) return;

            // Must be the immediate reply after a double push.
            if (!board.EnPassantAvailableFor(sideToMove)) return;

            // EP target is the passed-over square:
            // For white-to-move, target is rank 6 (index 5); for black-to-move, rank 3 (index 2).
            int epRank = white ? 5 : 2;
            var ep88 = new Square0x88((epRank << 4) | file.Value);

            // The pawn that moved two squares sits behind the EP target
            var capturedSqUnsafe = white ? new UnsafeSquare0x88(ep88.Value - 16) : new UnsafeSquare0x88(ep88.Value + 16);
            var captured = white ? Piece.BlackPawn : Piece.WhitePawn;

#if DEBUG
            Debug.Assert(!Squares.IsOffboard(capturedSqUnsafe) && board.At((Square0x88)capturedSqUnsafe) == captured,
                $"En Passant invariant failed: expected {captured} behind EP target at {(Square0x88)capturedSqUnsafe}, got {board.At((Square0x88)capturedSqUnsafe)}");
#endif
            if (Squares.IsOffboard(capturedSqUnsafe) || board.At((Square0x88)capturedSqUnsafe) != captured) return;

            // Capturing pawns are adjacent, one rank behind the target
            var leftFrom = white ? new UnsafeSquare0x88(ep88.Value - 15) : new UnsafeSquare0x88(ep88.Value + 15);
            var rightFrom = white ? new UnsafeSquare0x88(ep88.Value - 17) : new UnsafeSquare0x88(ep88.Value + 17);
            var mover = white ? Piece.WhitePawn : Piece.BlackPawn;

            if (!Squares.IsOffboard(leftFrom) && board.At((Square0x88)leftFrom) == mover)
                Emit(ref moves, ref w, Move.EnPassant((Square0x88)leftFrom, ep88, mover, captured));

            if (!Squares.IsOffboard(rightFrom) && board.At((Square0x88)rightFrom) == mover)
                Emit(ref moves, ref w, Move.EnPassant((Square0x88)rightFrom, ep88, mover, captured));
        }
    }
}
