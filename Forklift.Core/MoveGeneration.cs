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
        public static Move[] GeneratePseudoLegal(Board board, Color sideToMove, MoveKind? onlyKinds = null)
        {
            Span<Move> moveBuffer = stackalloc Move[MoveBufferMax];
            GeneratePseudoLegal(board, ref moveBuffer, sideToMove, onlyKinds);
            return moveBuffer.ToArray();
        }

        /// <summary>
        /// Generates all pseudo-legal moves for the current board state.
        /// </summary>
        public static void GeneratePseudoLegal(Board board, ref Span<Move> buffer, Color sideToMove, MoveKind? onlyKinds = null)
        {
            int index = 0;

            GeneratePawnMoves(board, ref buffer, sideToMove, ref index, onlyKinds);

            GenerateKnightMoves(board, ref buffer, sideToMove, ref index, onlyKinds);

            // Sliders
            GenerateSliderMoves(board, ref buffer, sideToMove, ref index,
                sideToMove.IsWhite() ? Piece.WhiteBishop : Piece.BlackBishop, onlyKinds);

            GenerateSliderMoves(board, ref buffer, sideToMove, ref index,
                sideToMove.IsWhite() ? Piece.WhiteRook : Piece.BlackRook, onlyKinds);

            GenerateSliderMoves(board, ref buffer, sideToMove, ref index,
                sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen, onlyKinds);

            GenerateKingMoves(board, ref buffer, sideToMove, ref index, onlyKinds);

            GenerateCastling(board, ref buffer, sideToMove, ref index, onlyKinds);

            GenerateEnPassant(board, ref buffer, sideToMove, ref index, onlyKinds);

            buffer = buffer[..index];
        }

        // --- Pawns (pushes, captures, promotions; EP is generated separately) -------------
        private static void GeneratePawnMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, MoveKind? onlyKinds = null)
        {
            if (onlyKinds != null
                && !onlyKinds.Value.HasFlag(MoveKind.Normal)
                && !onlyKinds.Value.HasFlag(MoveKind.Promotion)
                && !onlyKinds.Value.HasFlag(MoveKind.Capture))
            {
                return;
            }

            int colorIdx = sideToMove.IsWhite() ? 0 : 1;
            Piece pawn = colorIdx == 0 ? Piece.WhitePawn : Piece.BlackPawn;
            ulong pawns = board.GetPieceBitboard(pawn);

            while (pawns != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(pawns);
                pawns &= pawns - 1;
                var from64 = new Square0x64(s64);
                var from88 = (Square0x88)from64;
                int rank = from88 >> 4;

                // Forward one
                UnsafeSquare0x88 one = from88 + PawnPushDelta[colorIdx];
                if (!Squares.IsOffboard(one))
                {
                    var one88 = (Square0x88)one;
                    if (board.At(one88) == Piece.Empty)
                    {
                        var isPromotionRank = rank == PromotionRank[colorIdx];
                        if (isPromotionRank && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Promotion) || onlyKinds.Value.HasFlag(MoveKind.Normal)))
                        {
                            foreach (var promo in PromotionPieces[colorIdx])
                                buffer[index++] = Move.PromotionPush(from88, one88, pawn, promo);
                        }
                        else if (!isPromotionRank && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Normal)))
                        {
                            buffer[index++] = Move.Normal(from88, one88, pawn);
                            // Double push from start rank (lookup table)
                            if (rank == DoublePushStartRank[colorIdx])
                            {
                                UnsafeSquare0x88 two = one + PawnPushDelta[colorIdx];
                                if (!Squares.IsOffboard(two) && board.At((Square0x88)two) == Piece.Empty)
                                    buffer[index++] = Move.Normal(from88, (Square0x88)two, pawn);
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
                    var isPromotionRank = rank == PromotionRank[colorIdx];
                    if (isPromotionRank && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Promotion) || onlyKinds.Value.HasFlag(MoveKind.Capture)))
                    {
                        foreach (var promo in PromotionPieces[colorIdx])
                            buffer[index++] = Move.PromotionCapture(from88, to88, pawn, target, promo);
                    }
                    else if (!isPromotionRank && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Capture)))
                    {
                        buffer[index++] = Move.Capture(from88, to88, pawn, target);
                    }
                }
            }
        }

        // --- Knights ----------------------------------------------------------------------
        private static void GenerateKnightMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, MoveKind? onlyKinds = null)
        {
            if (onlyKinds != null
                && !onlyKinds.Value.HasFlag(MoveKind.Normal)
                && !onlyKinds.Value.HasFlag(MoveKind.Capture))
            {
                return;
            }

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
                var from88 = (Square0x88)from64;
                ulong attacks = board.Tables.KnightAttackTable[s64];

                // Quiet moves
                if (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Normal))
                {
                    ulong quiets = attacks & ~occAll;
                    while (quiets != 0)
                    {
                        int toS64 = BitOperations.TrailingZeroCount(quiets);
                        quiets &= quiets - 1;
                        buffer[index++] = Move.Normal(from88, (Square0x88)new Square0x64(toS64)), mover);
                    }
                }

                // Captures
                if (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Capture))
                {
                    ulong captures = attacks & occOpp;
                    while (captures != 0)
                    {
                        int toS64 = BitOperations.TrailingZeroCount(captures);
                        captures &= captures - 1;
                        var to88 = (Square0x88)new Square0x64(toS64);
                        buffer[index++] = Move.Capture(from88, to88, mover, board.At(to88));
                    }
                }
            }
        }

        // --- Sliders (bishops/rooks/queens) ----------------------------------------------
        private static void GenerateSliderMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, Piece piece, MoveKind? onlyKinds = null)
        {
            if (onlyKinds != null
                && !onlyKinds.Value.HasFlag(MoveKind.Normal)
                && !onlyKinds.Value.HasFlag(MoveKind.Capture))
            {
                return;
            }

            bool white = sideToMove.IsWhite();
            ulong sliders = board.GetPieceBitboard(piece);
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            while (sliders != 0)
            {
                int s64 = BitOperations.TrailingZeroCount(sliders);
                sliders &= sliders - 1;

                var from64 = new Square0x64(s64);
                var from88 = (Square0x88)from64;

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
                if (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Normal))
                {
                    ulong quiets = attacks & ~occAll;
                    while (quiets != 0)
                    {
                        int toS64 = BitOperations.TrailingZeroCount(quiets);
                        quiets &= quiets - 1;
                        buffer[index++] = Move.Normal(from88, (Square0x88)new Square0x64(toS64), piece);
                    }
                }

                // Captures
                if (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.Capture))
                {
                    ulong captures = attacks & occOpp;
                    while (captures != 0)
                    {
                        int toS64 = BitOperations.TrailingZeroCount(captures);
                        captures &= captures - 1;
                        var to88 = (Square0x88)new Square0x64(toS64);
                        buffer[index++] = Move.Capture(from88, to88, piece, board.At(to88));
                    }
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------
        private static void GenerateKingMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, MoveKind? onlyKinds = null)
        {
            if (onlyKinds != null
                && !onlyKinds.Value.HasFlag(MoveKind.Normal)
                && !onlyKinds.Value.HasFlag(MoveKind.Capture))
            {
                return;
            }

            bool white = sideToMove.IsWhite();

            Piece king = white ? Piece.WhiteKing : Piece.BlackKing;
            var from88 = white ? board.WhiteKing!.Value : board.BlackKing!.Value;

            var s64 = (Square0x64)from88;

            ulong attacks = board.Tables.KingAttackTable[s64.Value];
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            // Quiet moves
            if (onlyKinds.Value.HasFlag(MoveKind.Normal))
            {
                ulong quiets = attacks & ~occAll;
                while (quiets != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(quiets);
                    quiets &= quiets - 1;
                    buffer[index++] = Move.Normal(from88, (Square0x88)new Square0x64(toS64), king);
                }
            }

            // Captures
            if (onlyKinds.Value.HasFlag(MoveKind.Capture)) 
            {
                ulong captures = attacks & occOpp;
                while (captures != 0)
                {
                    int toS64 = BitOperations.TrailingZeroCount(captures);
                    captures &= captures - 1;
                    var to88 = (Square0x88)new Square0x64(toS64);
                    buffer[index++] = Move.Capture(from88, to88, king, board.At(to88));
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

        private static void GenerateCastling(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, MoveKind? onlyKinds = null)
        {
            if (onlyKinds != null && !onlyKinds.Value.HasFlag(MoveKind.CastleKing) && !onlyKinds.Value.HasFlag(MoveKind.CastleQueen))
            {
                return;
            }

            bool white = sideToMove.IsWhite();

            // Must have a king and not already be in check.
            ulong kingBB = board.GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
            if (kingBB == 0) return;
            if (board.InCheck(sideToMove)) return;

            Color enemy = sideToMove.Flip();

            if (white)
            {
                // ---- White O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.WhiteKing) != 0 && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.CastleKing)))
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
                            buffer[index++] = Board.Move.CastleKingSide(Color.White);
                        }
                    }
                }

                // ---- White O-O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.WhiteQueen) != 0 && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.CastleQueen)))
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
                            buffer[index++] = Board.Move.CastleQueenSide(Color.White);
                        }
                    }
                }
            }
            else
            {
                // ---- Black O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.BlackKing) != 0 && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.CastleKing)))
                {
                    if (board.At((Square0x88)F8_64) == Piece.Empty &&
                        board.At((Square0x88)G8_64) == Piece.Empty &&
                        board.At(H8_88) == Piece.BlackRook)
                    {
                        if (!board.IsSquareAttacked(E8_64, enemy) &&
                            !board.IsSquareAttacked(F8_64, enemy) &&
                            !board.IsSquareAttacked(G8_64, enemy))
                        {
                            buffer[index++] = Board.Move.CastleKingSide(Color.Black);
                        }
                    }
                }

                // ---- Black O-O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.BlackQueen) != 0 && (onlyKinds == null || onlyKinds.Value.HasFlag(MoveKind.CastleQueen)))
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
                            buffer[index++] = Board.Move.CastleQueenSide(Color.Black);
                        }
                    }
                }
            }
        }

        // --- En Passant -------------------------------------------------------------------
        private static void GenerateEnPassant(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, MoveKind? onlyKinds = null)
        {
            if (onlyKinds != null
                && !onlyKinds.Value.HasFlag(MoveKind.EnPassant)
                && !onlyKinds.Value.HasFlag(MoveKind.Capture))
            {
                return;
            }

            bool white = sideToMove.IsWhite();
            if (board.EnPassantFile is not FileIndex file) return;

            // Must be the immediate reply after a double push.
            if (!board.EnPassantAvailableFor(sideToMove)) return;

            // EP target is the passed-over square:
            int epRank = white ? 5 : 2;
            var ep88 = new Square0x88((epRank << 4) | file.Value);
            var captured = white ? Piece.BlackPawn : Piece.WhitePawn;

            var leftFrom = white ? new UnsafeSquare0x88(ep88.Value - 15) : new UnsafeSquare0x88(ep88.Value + 15);
            var rightFrom = white ? new UnsafeSquare0x88(ep88.Value - 17) : new UnsafeSquare0x88(ep88.Value + 17);
            var mover = white ? Piece.WhitePawn : Piece.BlackPawn;

            if (!Squares.IsOffboard(leftFrom) && board.At((Square0x88)leftFrom) == mover)
                buffer[index++] = Move.EnPassant((Square0x88)leftFrom, ep88, mover, captured);

            if (!Squares.IsOffboard(rightFrom) && board.At((Square0x88)rightFrom) == mover)
                buffer[index++] = Move.EnPassant((Square0x88)rightFrom, ep88, mover, captured);
        }
    }
}
