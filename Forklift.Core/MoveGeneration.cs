using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.VisualBasic;
using static Forklift.Core.Board;

namespace Forklift.Core
{
    /// <summary>
    /// Provides methods for generating pseudo-legal chess moves.
    /// </summary>
    public static class MoveGeneration
    {
        private static readonly Piece[][] PromotionPieces = [
            [Piece.WhiteQueen, Piece.WhiteRook, Piece.WhiteBishop, Piece.WhiteKnight],
            [Piece.BlackQueen, Piece.BlackRook, Piece.BlackBishop, Piece.BlackKnight]
        ];

        // It is safe to use [SkipLocalsInit] here because the stackalloc'd Move buffer is fully written to
        // before any reads occur; no uninitialized memory is ever accessed. This is a performance optimization.
        [SkipLocalsInit]
        public static Move[] GeneratePseudoLegal(Board board, Color sideToMove, IMoveFilter? moveFilter = null)
        {
            Span<Move> moveBuffer = stackalloc Move[MoveBufferMax];
            GeneratePseudoLegal(board, ref moveBuffer, sideToMove, moveFilter);
            return moveBuffer.ToArray();
        }

        public interface IMoveFilter
        {
            bool Accept(MoveKind kind);
            bool AcceptsAllSquares();
            bool Accept(int fromSquare64Index);
            ulong? FromBitboardMask();
        }

        public class MoveKindFilter : IMoveFilter
        {
            private readonly MoveKind _kinds;

            public MoveKindFilter(MoveKind kinds)
            {
                _kinds = kinds;
            }
            public bool Accept(MoveKind kind) => _kinds.HasFlag(kind);
            public bool AcceptsAllSquares() => true;
            public bool Accept(int fromSquare64Index) => true;
            public ulong? FromBitboardMask() => null;
            public static readonly MoveKindFilter NonQuiet = new MoveKindFilter(MoveKind.NonQuiet);
        }

        public class Square0x64Filter : IMoveFilter
        {
            private readonly int _fromSquareIndex;

            public Square0x64Filter(int fromSquareIndex)
            {
                _fromSquareIndex = fromSquareIndex;
            }
            public bool AcceptsAllSquares() => false;
            public bool Accept(MoveKind kind) => true;
            public bool Accept(int fromSquare64Index) => fromSquare64Index == _fromSquareIndex;
            public ulong? FromBitboardMask()
            {
                ulong mask = 1UL << _fromSquareIndex;
                return mask;
            }
        }

        /// <summary>
        /// Generates all pseudo-legal moves for the current board state.
        /// </summary>
        public static void GeneratePseudoLegal(Board board, ref Span<Move> buffer, Color sideToMove, IMoveFilter? moveFilter = null)
        {
            int index = 0;

            GeneratePawnMoves(board, ref buffer, sideToMove, ref index, moveFilter);

            GenerateKnightMoves(board, ref buffer, sideToMove, ref index, moveFilter);
            // Sliders
            GenerateSliderMoves(board, ref buffer, sideToMove, ref index,
                sideToMove.IsWhite() ? Piece.WhiteBishop : Piece.BlackBishop, moveFilter);

            GenerateSliderMoves(board, ref buffer, sideToMove, ref index,
                sideToMove.IsWhite() ? Piece.WhiteRook : Piece.BlackRook, moveFilter);

            GenerateSliderMoves(board, ref buffer, sideToMove, ref index,
                sideToMove.IsWhite() ? Piece.WhiteQueen : Piece.BlackQueen, moveFilter);

            GenerateKingMoves(board, ref buffer, sideToMove, ref index, moveFilter);

            GenerateCastling(board, ref buffer, sideToMove, ref index, moveFilter);

            GenerateEnPassant(board, ref buffer, sideToMove, ref index, moveFilter);
            buffer = buffer[..index];
        }

        // --- Pawns (pushes, captures, promotions; EP is generated separately) -------------
        private static void GeneratePawnMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, IMoveFilter? moveFilter = null)
        {
            if (moveFilter != null
                && !moveFilter.Accept(MoveKind.Normal)
                && !moveFilter.Accept(MoveKind.Promotion)
                && !moveFilter.Accept(MoveKind.Capture))
            {
                return;
            }

            var isWhite = sideToMove.IsWhite();
            Piece pawn = isWhite ? Piece.WhitePawn : Piece.BlackPawn;
            ulong pawns = board.GetPieceBitboard(pawn);
            if (moveFilter?.FromBitboardMask() is ulong fromMask)
            {
                pawns &= fromMask;
            }

            var promotionRank = isWhite ? 7 : 0;
            var doublePushRank = isWhite ? 1 : 6;
            var allUnoccupied = ~board.GetAllOccupancy();
            while (pawns != 0)
            {
                var from64 = BitOperations.TrailingZeroCount(pawns);
                pawns &= pawns - 1;

                var pawnSinglePush = (isWhite ? board.Tables.WhitePawnPushFrom[from64] : board.Tables.BlackPawnPushFrom[from64]) & allUnoccupied;
                if (pawnSinglePush != 0)
                {
                    var singlePushTo64 = BitOperations.TrailingZeroCount(pawnSinglePush);
                    int toRank = singlePushTo64 / 8;
                    bool isPromotionRank = toRank == promotionRank;
                    if (isPromotionRank && (moveFilter == null || moveFilter.Accept(MoveKind.Promotion)))
                    {
                        foreach (var promo in PromotionPieces[isWhite ? 0 : 1])
                        {
                            buffer[index++] = Move.PromotionPush(from64, singlePushTo64, pawn, promo);
                        }
                    }
                    else if (!isPromotionRank && (moveFilter == null || moveFilter.Accept(MoveKind.Normal)))
                    {
                        buffer[index++] = Move.Normal(from64, singlePushTo64, pawn);
                    }

                    // Double push
                    var canDoublePush = from64 / 8 == doublePushRank;

                    if (canDoublePush && (moveFilter == null || moveFilter.Accept(MoveKind.Normal)))
                    {
                        var pawnDoublePush = (isWhite ? board.Tables.WhitePawnPushFrom[singlePushTo64] : board.Tables.BlackPawnPushFrom[singlePushTo64]) & allUnoccupied;
                        if (pawnDoublePush != 0)
                        {
                            var toDouble64 = BitOperations.TrailingZeroCount(pawnDoublePush);
                            buffer[index++] = Move.Normal(from64, toDouble64, pawn);
                        }
                    }
                }

                // Captures
                ulong toAttackSquares = isWhite
                    ? board.Tables.WhitePawnAttackFrom[from64] & board.GetOccupancy(Color.Black)
                    : board.Tables.BlackPawnAttackFrom[from64] & board.GetOccupancy(Color.White);

                if (moveFilter == null || moveFilter.Accept(MoveKind.Capture))
                {
                    while (toAttackSquares != 0)
                    {
                        var to64 = BitOperations.TrailingZeroCount(toAttackSquares);
                        toAttackSquares &= toAttackSquares - 1;
                        int toRank = to64 / 8;
                        bool isPromotionRank = toRank == promotionRank;
                        var captured = board.At64(to64);
                        if (isPromotionRank && (moveFilter == null || moveFilter.Accept(MoveKind.Promotion))) // capture or null already accepted, so just need to additionally check for promotion
                        {
                            foreach (var promo in PromotionPieces[isWhite ? 0 : 1])
                            {
                                buffer[index++] = Move.PromotionCapture(from64, to64, pawn, captured, promo);
                            }
                        }
                        else if (!isPromotionRank)
                        {
                            buffer[index++] = Move.Capture(from64, to64, pawn, captured);
                        }
                    }
                }
            }
        }

        // --- Knights ----------------------------------------------------------------------
        private static void GenerateKnightMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, IMoveFilter? moveFilter = null)
        {
            if (moveFilter != null
                && !moveFilter.Accept(MoveKind.Normal)
                && !moveFilter.Accept(MoveKind.Capture))
            {
                return;
            }

            bool white = sideToMove.IsWhite();
            Piece mover = white ? Piece.WhiteKnight : Piece.BlackKnight;
            ulong knights = board.GetPieceBitboard(mover);
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            if (moveFilter?.FromBitboardMask() is ulong fromMask)
            {
                knights &= fromMask;
            }

            while (knights != 0)
            {
                int from64 = BitOperations.TrailingZeroCount(knights);
                knights &= knights - 1;

                ulong attacks = board.Tables.KnightAttackTable[from64];

                // Quiet moves
                if (moveFilter == null || moveFilter.Accept(MoveKind.Normal))
                {
                    ulong quiets = attacks & ~occAll;
                    while (quiets != 0)
                    {
                        var to64 = BitOperations.TrailingZeroCount(quiets);
                        quiets &= quiets - 1;
                        buffer[index++] = Move.Normal(from64, to64, mover);
                    }
                }

                // Captures
                if (moveFilter == null || moveFilter.Accept(MoveKind.Capture))
                {
                    ulong captures = attacks & occOpp;
                    while (captures != 0)
                    {
                        var to64 = BitOperations.TrailingZeroCount(captures);
                        captures &= captures - 1;
                        buffer[index++] = Move.Capture(from64, to64, mover, board.At64(to64));
                    }
                }
            }
        }

        // --- Sliders (bishops/rooks/queens) ----------------------------------------------
        private static void GenerateSliderMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, Piece piece, IMoveFilter? moveFilter = null)
        {
            if (moveFilter != null
                && !moveFilter.Accept(MoveKind.Normal)
                && !moveFilter.Accept(MoveKind.Capture))
            {
                return;
            }

            bool white = sideToMove.IsWhite();
            ulong sliders = board.GetPieceBitboard(piece);
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            if (moveFilter?.FromBitboardMask() is ulong fromMask)
            {
                sliders &= fromMask;
            }

            while (sliders != 0)
            {
                var from64 = BitOperations.TrailingZeroCount(sliders);
                sliders &= sliders - 1;

                var T = board.Tables;
                ulong occ = board.GetAllOccupancy();

                // Bishop-like
                ulong bMaskOcc = occ & EngineTables.BishopMasks[from64];
                int bIdx = EngineTables.GetSliderAttackIndex(from64, bMaskOcc, Piece.PieceType.Bishop);
                ulong bAtt = T.BishopTable[T.BishopOffsets[from64] + bIdx];

                // Rook-like
                ulong rMaskOcc = occ & EngineTables.RookMasks[from64];
                int rIdx = EngineTables.GetSliderAttackIndex(from64, rMaskOcc, Piece.PieceType.Rook);
                ulong rAtt = T.RookTable[T.RookOffsets[from64] + rIdx];

                ulong attacks =
                    (piece.Type == Piece.PieceType.Bishop) ? bAtt :
                    (piece.Type == Piece.PieceType.Rook) ? rAtt :
                    (bAtt | rAtt);

                // Quiet moves
                if (moveFilter == null || moveFilter.Accept(MoveKind.Normal))
                {
                    ulong quiets = attacks & ~occAll;
                    while (quiets != 0)
                    {
                        var to64 = BitOperations.TrailingZeroCount(quiets);
                        quiets &= quiets - 1;
                        buffer[index++] = Move.Normal(from64, to64, piece);
                    }
                }

                // Captures
                if (moveFilter == null || moveFilter.Accept(MoveKind.Capture))
                {
                    ulong captures = attacks & occOpp;
                    while (captures != 0)
                    {
                        var to64 = BitOperations.TrailingZeroCount(captures);
                        captures &= captures - 1;
                        buffer[index++] = Move.Capture(from64, to64, piece, board.At64(to64));
                    }
                }
            }
        }

        // --- King (no castling here; see GenerateCastling) --------------------------------
        private static void GenerateKingMoves(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, IMoveFilter? moveFilter = null)
        {
            if (moveFilter != null
                && !moveFilter.Accept(MoveKind.Normal)
                && !moveFilter.Accept(MoveKind.Capture))
            {
                return;
            }

            bool white = sideToMove.IsWhite();

            Piece king = white ? Piece.WhiteKing : Piece.BlackKing;
            var from64 = white ? board.WhiteKingSquare64Index!.Value : board.BlackKingSquare64Index!.Value;

            if (moveFilter?.Accept(from64) == false)
            {
                return;
            }

            ulong attacks = board.Tables.KingAttackTable[from64];
            ulong occAll = board.GetAllOccupancy();
            ulong occOpp = board.GetOccupancy(white ? Color.Black : Color.White);

            // Quiet moves
            if (moveFilter == null || moveFilter.Accept(MoveKind.Normal))
            {
                ulong quiets = attacks & ~occAll;
                while (quiets != 0)
                {
                    var to64 = BitOperations.TrailingZeroCount(quiets);
                    quiets &= quiets - 1;
                    buffer[index++] = Move.Normal(from64, to64, king);
                }
            }

            // Captures
            if (moveFilter == null || moveFilter.Accept(MoveKind.Capture))
            {
                ulong captures = attacks & occOpp;
                while (captures != 0)
                {
                    var to64 = BitOperations.TrailingZeroCount(captures);
                    captures &= captures - 1;
                    buffer[index++] = Move.Capture(from64, to64, king, board.At64(to64));
                }
            }
        }

        // --- Castling (requires empty path + no attacked transit squares) -----------------
        // Precomputed 0x64 squares used by castling checks
        private static readonly Square0x64 E1_64 = Squares.ParseAlgebraicTo0x64("e1");
        private static readonly int E1_64_Index = E1_64.Value;
        private static readonly Square0x64 F1_64 = Squares.ParseAlgebraicTo0x64("f1");
        private static readonly int F1_64_Index = F1_64.Value;
        private static readonly Square0x88 F1_88 = Squares.ParseAlgebraicTo0x88("f1");
        private static readonly int F1_88_Index = F1_88.Value;
        private static readonly Square0x64 G1_64 = Squares.ParseAlgebraicTo0x64("g1");
        private static readonly int G1_64_Index = G1_64.Value;
        private static readonly Square0x88 G1_88 = Squares.ParseAlgebraicTo0x88("g1");
        private static readonly int G1_88_Index = G1_88.Value;
        private static readonly Square0x64 D1_64 = Squares.ParseAlgebraicTo0x64("d1");
        private static readonly int D1_64_Index = D1_64.Value;
        private static readonly Square0x88 D1_88 = Squares.ParseAlgebraicTo0x88("d1");
        private static readonly int D1_88_Index = D1_88.Value;
        private static readonly Square0x64 C1_64 = Squares.ParseAlgebraicTo0x64("c1");
        private static readonly int C1_64_Index = C1_64.Value;
        private static readonly Square0x88 C1_88 = Squares.ParseAlgebraicTo0x88("c1");
        private static readonly int C1_88_Index = C1_88.Value;
        private static readonly Square0x88 B1_88 = Squares.ParseAlgebraicTo0x88("b1");
        private static readonly int B1_88_Index = B1_88.Value;
        private static readonly Square0x88 H1_88 = Squares.ParseAlgebraicTo0x88("h1");
        private static readonly int H1_88_Index = H1_88.Value;
        private static readonly Square0x88 A1_88 = Squares.ParseAlgebraicTo0x88("a1");
        private static readonly int A1_88_Index = A1_88.Value;
        private static readonly Square0x64 E8_64 = Squares.ParseAlgebraicTo0x64("e8");
        private static readonly int E8_64_Index = E8_64.Value;
        private static readonly Square0x64 F8_64 = Squares.ParseAlgebraicTo0x64("f8");
        private static readonly int F8_64_Index = F8_64.Value;
        private static readonly Square0x88 F8_88 = Squares.ParseAlgebraicTo0x88("f8");
        private static readonly int F8_88_Index = F8_88.Value;
        private static readonly Square0x64 G8_64 = Squares.ParseAlgebraicTo0x64("g8");
        private static readonly int G8_64_Index = G8_64.Value;
        private static readonly Square0x88 G8_88 = Squares.ParseAlgebraicTo0x88("g8");
        private static readonly int G8_88_Index = G8_88.Value;
        private static readonly Square0x64 D8_64 = Squares.ParseAlgebraicTo0x64("d8");
        private static readonly int D8_64_Index = D8_64.Value;
        private static readonly Square0x88 D8_88 = Squares.ParseAlgebraicTo0x88("d8");
        private static readonly int D8_88_Index = D8_88.Value;
        private static readonly Square0x64 C8_64 = Squares.ParseAlgebraicTo0x64("c8");
        private static readonly int C8_64_Index = C8_64.Value;
        private static readonly Square0x88 C8_88 = Squares.ParseAlgebraicTo0x88("c8");
        private static readonly int C8_88_Index = C8_88.Value;
        private static readonly Square0x64 B8_64 = Squares.ParseAlgebraicTo0x64("b8");
        private static readonly int B8_64_Index = B8_64.Value;
        private static readonly Square0x88 B8_88 = Squares.ParseAlgebraicTo0x88("b8");
        private static readonly int B8_88_Index = B8_88.Value;
        private static readonly Square0x88 H8_88 = Squares.ParseAlgebraicTo0x88("h8");
        private static readonly int H8_88_Index = H8_88.Value;
        private static readonly Square0x88 A8_88 = Squares.ParseAlgebraicTo0x88("a8");
        private static readonly int A8_88_Index = A8_88.Value;

        private static void GenerateCastling(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, IMoveFilter? moveFilter = null)
        {
            if (moveFilter != null
                && !moveFilter.Accept(MoveKind.CastleKing)
                && !moveFilter.Accept(MoveKind.CastleQueen))
            {
                return;
            }

            bool white = sideToMove.IsWhite();

            if (moveFilter?.AcceptsAllSquares() == false && moveFilter?.Accept(white ? board.WhiteKingSquare64Index!.Value : board.BlackKingSquare64Index!.Value) == false)
            {
                return;
            }

            if (board.InCheck(sideToMove)) return;

            Color enemy = sideToMove.Flip();

            if (white)
            {
                // ---- White O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.WhiteKing) != 0 && (moveFilter == null || moveFilter.Accept(MoveKind.CastleKing)))
                {
                    // Path empties + rook presence first (cheap)
                    if (board.At88(F1_88_Index) == Piece.Empty &&
                        board.At88(G1_88_Index) == Piece.Empty &&
                        board.At88(H1_88_Index) == Piece.WhiteRook)
                    {
                        // Three probes, short-circuit
                        if (!board.IsSquareAttacked(E1_64_Index, enemy) &&
                            !board.IsSquareAttacked(F1_64_Index, enemy) &&
                            !board.IsSquareAttacked(G1_64_Index, enemy))
                        {
                            buffer[index++] = Move.CastleKingSide(Color.White);
                        }
                    }
                }

                // ---- White O-O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.WhiteQueen) != 0 && (moveFilter == null || moveFilter.Accept(MoveKind.CastleQueen)))
                {
                    if (board.At88(B1_88_Index) == Piece.Empty &&
                        board.At88(C1_88_Index) == Piece.Empty &&
                        board.At88(D1_88_Index) == Piece.Empty &&
                        board.At88(A1_88_Index) == Piece.WhiteRook)
                    {
                        if (!board.IsSquareAttacked(E1_64_Index, enemy) &&
                            !board.IsSquareAttacked(D1_64_Index, enemy) &&
                            !board.IsSquareAttacked(C1_64_Index, enemy))
                        {
                            buffer[index++] = Board.Move.CastleQueenSide(Color.White);
                        }
                    }
                }
            }
            else
            {
                // ---- Black O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.BlackKing) != 0 && (moveFilter == null || moveFilter.Accept(MoveKind.CastleKing)))
                {
                    if (board.At88(F8_88_Index) == Piece.Empty &&
                        board.At88(G8_88_Index) == Piece.Empty &&
                        board.At88(H8_88_Index) == Piece.BlackRook)
                    {
                        if (!board.IsSquareAttacked(E8_64_Index, enemy) &&
                            !board.IsSquareAttacked(F8_64_Index, enemy) &&
                            !board.IsSquareAttacked(G8_64_Index, enemy))
                        {
                            buffer[index++] = Board.Move.CastleKingSide(Color.Black);
                        }
                    }
                }

                // ---- Black O-O-O ----
                if ((board.CastlingRights & CastlingRightsFlags.BlackQueen) != 0 && (moveFilter == null || moveFilter.Accept(MoveKind.CastleQueen)))
                {
                    if (board.At88(B8_88_Index) == Piece.Empty &&
                        board.At88(C8_88_Index) == Piece.Empty &&
                        board.At88(D8_88_Index) == Piece.Empty &&
                        board.At88(A8_88_Index) == Piece.BlackRook)
                    {
                        if (!board.IsSquareAttacked(E8_64_Index, enemy) &&
                            !board.IsSquareAttacked(D8_64_Index, enemy) &&
                            !board.IsSquareAttacked(C8_64_Index, enemy))
                        {
                            buffer[index++] = Board.Move.CastleQueenSide(Color.Black);
                        }
                    }
                }
            }
        }

        // --- En Passant -------------------------------------------------------------------
        private static void GenerateEnPassant(Board board, ref Span<Move> buffer, Color sideToMove, ref int index, IMoveFilter? moveFilter = null)
        {
            if (moveFilter != null
                && !moveFilter.Accept(MoveKind.Capture))
            {
                // MoveKind.Capture already accepts MoveKind.EnPassant,
                // so we don't need to check it separately here
                return;
            }

            bool white = sideToMove.IsWhite();
            if (!board.EnPassantAvailableFor(sideToMove, out var file)) return;

            // EP target is the passed-over square:
            int epVal = white ? 40 : 16;
            var ep64 = epVal | file.Value;
            var captured = white ? Piece.BlackPawn : Piece.WhitePawn;

            var attackers = white
                ? board.Tables.WhitePawnAttackTable[ep64]
                : board.Tables.BlackPawnAttackTable[ep64];

            attackers &= board.GetPieceBitboard(white ? Piece.WhitePawn : Piece.BlackPawn);

            while (attackers != 0)
            {
                int from64 = BitOperations.TrailingZeroCount(attackers);
                attackers &= attackers - 1;

                if (moveFilter?.Accept(from64) == false)
                {
                    continue;
                }

                buffer[index++] = Move.EnPassant(from64, ep64, white ? Piece.WhitePawn : Piece.BlackPawn, captured);
            }
        }
    }
}
