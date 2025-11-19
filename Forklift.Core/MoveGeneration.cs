using System;
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
        // Precomputed pawn move tables
        private static readonly int[] PawnPushDelta = { +16, -16 }; // [White, Black]
        private static readonly int[][] PawnCaptureDeltas = new int[][] {
            new int[] { +15, +17 }, // White
            new int[] { -15, -17 }  // Black
        };
        private static readonly int[] PromotionRank = { 6, 1 }; // [White, Black]
        private static readonly int[] DoublePushStartRank = { 1, 6 }; // [White, Black]
        private static readonly Piece[][] PromotionPieces = new Piece[][] {
            new Piece[] { Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight },
            new Piece[] { Piece.BlackQueen, Piece.BlackRook, Piece.BlackBishop, Piece.BlackKnight }
        };

        // It is safe to use [SkipLocalsInit] here because the stackalloc'd Move buffer is fully written to
        // before any reads occur; no uninitialized memory is ever accessed. This is a performance optimization.
        [SkipLocalsInit]
        public static Move[] GeneratePseudoLegal(Board board, Color sideToMove)
        {
            Span<Move> moves = stackalloc Move[MoveBufferMax];
            var buffer = new MoveBuffer(moves);
            var span = GeneratePseudoLegal(board, ref buffer, sideToMove);
            return span.ToArray();
        }

        /// <summary>
        /// Generates all pseudo-legal moves for the current board state.
        /// </summary>
        public static MoveSpan GeneratePseudoLegal(Board board, ref MoveBuffer buffer, Color sideToMove)
        {
            GeneratePawnMoves(board, ref buffer, sideToMove);
            buffer.AssertNoOverflow(nameof(GeneratePawnMoves));

            GenerateKnightMoves(board, ref buffer, sideToMove);
            buffer.AssertNoOverflow(nameof(GenerateKnightMoves));

            // Sliders
            GenerateSliderMoves(board, ref buffer, sideToMove,
                sideToMove.IsWhite() ? Piece.WhiteBishop : Piece.BlackBishop);
            buffer.AssertNoOverflow(nameof(GenerateSliderMoves) + " (Bishop)");

            GenerateSliderMoves(board, ref buffer, sideToMove,
                sideToMove.IsWhite() ? Piece.WhiteRook : Piece.BlackRook);
            buffer.AssertNoOverflow(nameof(GenerateSliderMoves) + " (Rook)");

            GenerateSliderMoves(board, ref buffer, sideToMove,
                sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen);
            buffer.AssertNoOverflow(nameof(GenerateSliderMoves) + " (Queen)");

            GenerateKingMoves(board, ref buffer, sideToMove);
            buffer.AssertNoOverflow(nameof(GenerateKingMoves));

            GenerateCastling(board, ref buffer, sideToMove);
            buffer.AssertNoOverflow(nameof(GenerateCastling));

            GenerateEnPassant(board, ref buffer, sideToMove);
            buffer.AssertNoOverflow(nameof(GenerateEnPassant));

            return buffer.ToMoveSpan();
        }

        // --- Pawns (pushes, captures, promotions; EP is generated separately) -------------
        private static void GeneratePawnMoves(Board board, ref MoveBuffer buffer, Color sideToMove)
        {
            int colorIdx = sideToMove.IsWhite() ? 0 : 1;
            Piece pawn = colorIdx == 0 ? Piece.WhitePawn : Piece.BlackPawn;
            ulong pawns = board.GetPieceBitboard(pawn);
            ulong occAll = board.GetAllOccupancy();

            while (pawns != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(pawns);
                pawns &= pawns - 1;
                var from64 = new Square0x64(s64);
                var from88 = Squares.ConvertTo0x88Index(from64);
                int rank = from88 >> 4;

                // Forward one
                UnsafeSquare0x88 one = from88 + PawnPushDelta[colorIdx];
                if (!Squares.IsOffboard(one))
                {
                    var one88 = (Square0x88)one;
                    if (board.At(one88) == Piece.Empty)
                    {
                        if (rank == PromotionRank[colorIdx])
                        {
                            foreach (var promo in PromotionPieces[colorIdx])
                                buffer.Add(Move.PromotionPush(from88, one88, pawn, promo));
                        }
                        else
                        {
                            buffer.Add(Move.Normal(from88, one88, pawn));
                            // Double push from start rank (lookup table)
                            if (rank == DoublePushStartRank[colorIdx])
                            {
                                UnsafeSquare0x88 two = one + PawnPushDelta[colorIdx];
                                if (!Squares.IsOffboard(two) && board.At((Square0x88)two) == Piece.Empty)
                                    buffer.Add(Move.Normal(from88, (Square0x88)two, pawn));
                            }
                        }
                    }
                }

                // Captures (no EP here)
                var caps = PawnCaptureDeltas[colorIdx];
                for (int i = 0; i < caps.Length; i++)
                {
                    var toUnsafe = new UnsafeSquare0x88(from88.Value + caps[i]);
                    if (Squares.IsOffboard(toUnsafe)) continue;
                    var to88 = (Square0x88)toUnsafe;
                    var target = board.At(to88);
                    if (target == Piece.Empty || target.IsWhite == (colorIdx == 0)) continue;
                    if (rank == PromotionRank[colorIdx])
                    {
                        foreach (var promo in PromotionPieces[colorIdx])
                            buffer.Add(Move.PromotionCapture(from88, to88, pawn, target, promo));
                    }
                    else
                    {
                        buffer.Add(Move.Capture(from88, to88, pawn, target));
                    }
                }
            }
        }

        // --- Knights ----------------------------------------------------------------------
        private static void GenerateKnightMoves(Board board, ref MoveBuffer buffer, Color sideToMove)
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
                    buffer.Add(Move.Normal(from88, Squares.ConvertTo0x88Index(new Square0x64(toS64)), mover));
                }

                // Captures
                ulong captures = attacks & occOpp;
                while (captures != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(captures);
                    captures &= captures - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    buffer.Add(Move.Capture(from88, to88, mover, board.At(to88)));
                }
            }
        }

        // --- Sliders (bishops/rooks/queens) ----------------------------------------------
        private static void GenerateSliderMoves(Board board, ref MoveBuffer buffer, Color sideToMove, Piece piece)
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

                var T = board.Tables;
                int ti = from64.Value;
                ulong occ = board.GetAllOccupancy();

                // Bishop-like
                ulong bMaskOcc = occ & EngineTables.BishopMasks[ti];
                int bIdx = EngineTables.GetSliderAttackIndex(ti, bMaskOcc, Piece.PieceType.Bishop);
                ulong bAtt = T.BishopTable[T.BishopOffsets[ti] + bIdx];

                // Rook-like
                ulong rMaskOcc = occ & EngineTables.RookMasks[ti];
                int rIdx = EngineTables.GetSliderAttackIndex(ti, rMaskOcc, Piece.PieceType.Rook);
                ulong rAtt = T.RookTable[T.RookOffsets[ti] + rIdx];

                ulong attacks =
                    (piece.Type == Piece.PieceType.Bishop) ? bAtt :
                    (piece.Type == Piece.PieceType.Rook) ? rAtt :
                    (bAtt | rAtt);

                // Quiet moves
                ulong quiets = attacks & ~occAll;
                while (quiets != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(quiets);
                    quiets &= quiets - 1;
                    buffer.Add(Move.Normal(from88, Squares.ConvertTo0x88Index(new Square0x64(toS64)), piece));
                }

                // Captures
                ulong captures = attacks & occOpp;
                while (captures != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(captures);
                    captures &= captures - 1;
                    var to88 = Squares.ConvertTo0x88Index(new Square0x64(toS64));
                    buffer.Add(Move.Capture(from88, to88, piece, board.At(to88)));
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------
        private static void GenerateKingMoves(Board board, ref MoveBuffer buffer, Color sideToMove)
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
                    buffer.Add(Move.Normal(from88, to88, king));
                }
                else if (target.IsWhite != white)
                {
                    buffer.Add(Move.Capture(from88, to88, king, target));
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

        private static void GenerateCastling(Board board, ref MoveBuffer buffer, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();

            // Must have a king and not already be in check.
            ulong kingBB = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (kingBB == 0) return;
            if (board.InCheck(sideToMove)) return;

            Color enemy = sideToMove.Flip();

            if (white)
            {
                // ---- White O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.WhiteKing) != 0)
                {
                    // Path empties + rook presence first (cheap)
                    if (board.At((Square0x88)F1_64) == Piece.Empty &&
                        board.At((Square0x88)G1_64) == Piece.Empty &&
                        board.At(H1_88) == Piece.WhiteRook)
                    {
                        // Three probes, short-circuit
                        if (!board.IsSquareAttacked(E1_64, enemy) &&
                            !board.IsSquareAttacked(F1_64, enemy) &&
                            !board.IsSquareAttacked(G1_64, enemy))
                        {
                            buffer.Add(Board.Move.CastleKingSide(Color.White));
                        }
                    }
                }

                // ---- White O-O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.WhiteQueen) != 0)
                {
                    if (board.At((Square0x88)B1_64) == Piece.Empty &&
                        board.At((Square0x88)C1_64) == Piece.Empty &&
                        board.At((Square0x88)D1_64) == Piece.Empty &&
                        board.At(A1_88) == Piece.WhiteRook)
                    {
                        if (!board.IsSquareAttacked(E1_64, enemy) &&
                            !board.IsSquareAttacked(D1_64, enemy) &&
                            !board.IsSquareAttacked(C1_64, enemy))
                        {
                            buffer.Add(Board.Move.CastleQueenSide(Color.White));
                        }
                    }
                }
            }
            else
            {
                // ---- Black O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.BlackKing) != 0)
                {
                    if (board.At((Square0x88)F8_64) == Piece.Empty &&
                        board.At((Square0x88)G8_64) == Piece.Empty &&
                        board.At(H8_88) == Piece.BlackRook)
                    {
                        if (!board.IsSquareAttacked(E8_64, enemy) &&
                            !board.IsSquareAttacked(F8_64, enemy) &&
                            !board.IsSquareAttacked(G8_64, enemy))
                        {
                            buffer.Add(Board.Move.CastleKingSide(Color.Black));
                        }
                    }
                }

                // ---- Black O-O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.BlackQueen) != 0)
                {
                    if (board.At((Square0x88)B8_64) == Piece.Empty &&
                        board.At((Square0x88)C8_64) == Piece.Empty &&
                        board.At((Square0x88)D8_64) == Piece.Empty &&
                        board.At(A8_88) == Piece.BlackRook)
                    {
                        if (!board.IsSquareAttacked(E8_64, enemy) &&
                            !board.IsSquareAttacked(D8_64, enemy) &&
                            !board.IsSquareAttacked(C8_64, enemy))
                        {
                            buffer.Add(Board.Move.CastleQueenSide(Color.Black));
                        }
                    }
                }
            }
        }

        // --- En Passant -------------------------------------------------------------------
        private static void GenerateEnPassant(Board board, ref MoveBuffer buffer, Color sideToMove)
        {
            bool white = sideToMove.IsWhite();
            if (board.EnPassantFile is not FileIndex file) return;

            // Must be the immediate reply after a double push.
            if (!board.EnPassantAvailableFor(sideToMove)) return;

            // EP target is the passed-over square:
            int epRank = white ? 5 : 2;
            var ep88 = new Square0x88((epRank << 4) | file.Value);

            var capturedSqUnsafe = white ? new UnsafeSquare0x88(ep88.Value - 16) : new UnsafeSquare0x88(ep88.Value + 16);
            var captured = white ? Piece.BlackPawn : Piece.WhitePawn;

            var leftFrom = white ? new UnsafeSquare0x88(ep88.Value - 15) : new UnsafeSquare0x88(ep88.Value + 15);
            var rightFrom = white ? new UnsafeSquare0x88(ep88.Value - 17) : new UnsafeSquare0x88(ep88.Value + 17);
            var mover = white ? Piece.WhitePawn : Piece.BlackPawn;

            if (!Squares.IsOffboard(leftFrom) && board.At((Square0x88)leftFrom) == mover)
                buffer.Add(Move.EnPassant((Square0x88)leftFrom, ep88, mover, captured));

            if (!Squares.IsOffboard(rightFrom) && board.At((Square0x88)rightFrom) == mover)
                buffer.Add(Move.EnPassant((Square0x88)rightFrom, ep88, mover, captured));
        }

    }
}
