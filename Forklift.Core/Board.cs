using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Forklift.Core;

/// <summary>
/// Represents the chessboard and manages all mutable game state.
/// Thread-safe as long as each thread uses its own instance.
/// </summary>
public sealed class Board
{
    /// <summary>
    /// Creates a deep copy of the board suitable for parallel search threads.
    /// Shared tables are reused, all mutable state is copied.
    /// </summary>
    public Board Copy(bool? keepTrackOfHistory = null)
    {
        var copy = new Board(this.Tables);
        Array.Copy(this.mailbox, copy.mailbox, this.mailbox.Length);
        Array.Copy(this.pieceBB, copy.pieceBB, this.pieceBB.Length);
        copy._whiteKingSquare88 = this._whiteKingSquare88;
        copy._blackKingSquare88 = this._blackKingSquare88;
        copy.OccWhite = this.OccWhite;
        copy.OccBlack = this.OccBlack;
        copy.OccAll = this.OccAll;
        copy._sideToMove = this._sideToMove;
        copy.CastlingRights = this.CastlingRights;
        copy.EnPassantFile = this.EnPassantFile;
        copy.HalfmoveClock = this.HalfmoveClock;
        copy.FullmoveNumber = this.FullmoveNumber;
        copy.ZKey = this.ZKey;
        copy.KeepTrackOfHistory = keepTrackOfHistory ?? this.KeepTrackOfHistory;
        foreach (var kvp in this._repCounts)
            copy._repCounts[kvp.Key] = kvp.Value;
        foreach (var hash in this._hashStack)
            copy._hashStack.Push(hash);
        if (KeepTrackOfHistory)
        {
            copy._moveHistory.AddRange(this._moveHistory);
            copy._undoHistory.AddRange(this._undoHistory);
        }
        return copy;
    }

    [Flags]
    public enum CastlingRightsFlags
    {
        None = 0,
        WhiteKing = 1 << 0,
        WhiteQueen = 1 << 1,
        BlackKing = 1 << 2,
        BlackQueen = 1 << 3
    }

    // Immutable tables (shared across threads/tests)
    public EngineTables Tables { get; }

    // 0x88 mailbox (mutable, per-instance)
    private readonly sbyte[] mailbox = new sbyte[128];

    // Bitboards (mutable, per-instance)
    private readonly ulong[] pieceBB = new ulong[12];
    public ulong OccWhite { get; private set; }
    public ulong OccBlack { get; private set; }
    public ulong OccAll { get; private set; }

    // State
    public Color SideToMove
    {
        get => _sideToMove;
        private set
        {
            if (_sideToMove == value) return;
            ZKey ^= Tables.Zobrist.SideToMove;
            _sideToMove = value;
        }
    }
    private Color _sideToMove = Color.White;

    public void SetSideToMove(Color sideToMove)
    {
        SideToMove = sideToMove;
    }

    public CastlingRightsFlags CastlingRights { get; private set; } =
        CastlingRightsFlags.WhiteKing | CastlingRightsFlags.WhiteQueen |
        CastlingRightsFlags.BlackKing | CastlingRightsFlags.BlackQueen;

    public FileIndex? EnPassantFile { get; private set; } // a..h => 0..7 or null
    public int HalfmoveClock { get; private set; }
    public int FullmoveNumber { get; private set; } = 1;

    public ulong ZKey { get; private set; } // zobrist

    private readonly Dictionary<ulong, int> _repCounts = new();
    private readonly Stack<ulong> _hashStack = new();

    public int HashHistoryCount => _repCounts.TryGetValue(ZKey, out var n) ? n : 0;

    public int WhiteKingCount => BitOperations.PopCount(GetPieceBitboard(Piece.WhiteKing));
    public int BlackKingCount => BitOperations.PopCount(GetPieceBitboard(Piece.BlackKing));

    public bool KeepTrackOfHistory { get; set; } = true;

    public const int MoveBufferMax = 265;


    /// <summary>
    /// Initializes a new instance of the <see cref="Board"/> class.
    /// </summary>
    /// <param name="tables">Optional engine tables. If null, default tables are used.</param>
    public Board(EngineTables? tables = null, bool startPosition = false)
    {
        Tables = tables ?? EngineTables.CreateDefault();
        if (startPosition) SetStartPosition();
    }

    /// <summary>
    /// Gets the piece at the specified square.
    /// </summary>
    /// <param name="sq88">The square in 0x88 format.</param>
    /// <returns>The piece at the square.</returns>
    public Piece? At(UnsafeSquare0x88 sq88) => Squares.IsOffboard(sq88) ? null : At((Square0x88)sq88);

    /// <summary>
    /// Gets the piece at the specified square.
    /// </summary>
    /// <param name="sq88">The square in 0x88 format.</param>
    /// <returns>The piece at the square.</returns>
    public Piece At(Square0x88 sq88) => (Piece)mailbox[sq88];

    private Square0x88? _whiteKingSquare88 = null;
    private Square0x88? _blackKingSquare88 = null;

    public Square0x88? WhiteKing => _whiteKingSquare88;
    public Square0x88? BlackKing => _blackKingSquare88;

    /// <summary>
    /// Places a piece on the board at the specified square.
    /// </summary>
    /// <param name="sq88">The square in 0x88 format.</param>
    /// <param name="pc">The piece to place.</param>
    public void Place(Square0x88 sq88, Piece pc)
    {
        var existing = RemoveIfAny(sq88);
        if (existing == Piece.WhiteKing) _whiteKingSquare88 = null;
        else if (existing == Piece.BlackKing) _blackKingSquare88 = null;
        mailbox[sq88.Value] = (sbyte)pc;
        if (pc != Piece.Empty) AddToBitboards((Square0x64)sq88, pc);
        if (pc == Piece.WhiteKing) _whiteKingSquare88 = sq88;
        else if (pc == Piece.BlackKing) _blackKingSquare88 = sq88;
    }

    private Piece RemoveIfAny(Square0x88 sq88)
    {
        var existing = (Piece)mailbox[sq88.Value];
        if (existing == Piece.Empty) return Piece.Empty;
        RemoveFromBitboards((Square0x64)sq88, existing);
        mailbox[sq88.Value] = (sbyte)Piece.Empty;
        return existing;
    }

    private void AddToBitboards(Square0x64 sq64, Piece pc)
    {
        ulong b = 1UL << (int)sq64;
        pieceBB[pc.PieceIndex] |= b;
        if (pc.IsWhite) OccWhite |= b; else OccBlack |= b;
        OccAll |= b;
    }

    private void RemoveFromBitboards(Square0x64 sq64, Piece pc)
    {
        ulong b = 1UL << (int)sq64;
        pieceBB[pc.PieceIndex] &= ~b;
        if (pc.IsWhite) OccWhite &= ~b; else OccBlack &= ~b;
        OccAll &= ~b;
    }

    public enum MoveKind
    {
        Normal = 0,
        EnPassant = 1,
        CastleKing = 2,
        CastleQueen = 3,
        Promotion = 4,          // non-capture promotion
        PromotionCapture = 5     // capture + promotion
    }

    public readonly record struct Move(
        Square0x88 From88,
        Square0x88 To88,
        Piece Mover,
        Piece Captured,
        Piece Promotion,
        MoveKind Kind)
    {
        public static Move Normal(Square0x88 from, Square0x88 to, Piece mover)
            => new(from, to, mover, Piece.Empty, Piece.Empty, MoveKind.Normal);

        public static Move EnPassant(Square0x88 from, Square0x88 to, Piece mover, Piece captured)
            => new(from, to, mover, captured, Piece.Empty, MoveKind.EnPassant);

        private static readonly Square0x88 WhiteKingFrom88 = Squares.ParseAlgebraicTo0x88("e1");
        private static readonly Square0x88 WhiteKingToKingSide88 = Squares.ParseAlgebraicTo0x88("g1");
        private static readonly Square0x88 WhiteKingToQueenSide88 = Squares.ParseAlgebraicTo0x88("c1");
        private static readonly Square0x88 BlackKingFrom88 = Squares.ParseAlgebraicTo0x88("e8");
        private static readonly Square0x88 BlackKingToKingSide88 = Squares.ParseAlgebraicTo0x88("g8");
        private static readonly Square0x88 BlackKingToQueenSide88 = Squares.ParseAlgebraicTo0x88("c8");

        public static Move CastleKingSide(Color side)
        {
            var from = side.IsWhite() ? WhiteKingFrom88 : BlackKingFrom88;
            var to = side.IsWhite() ? WhiteKingToKingSide88 : BlackKingToKingSide88;
            return new(from, to, side.IsWhite() ? Piece.WhiteKing : Piece.BlackKing, Piece.Empty, Piece.Empty, MoveKind.CastleKing);
        }

        public static Move CastleQueenSide(Color side)
        {
            var from = side.IsWhite() ? WhiteKingFrom88 : BlackKingFrom88;
            var to = side.IsWhite() ? WhiteKingToQueenSide88 : BlackKingToQueenSide88;
            return new(from, to, side.IsWhite() ? Piece.WhiteKing : Piece.BlackKing, Piece.Empty, Piece.Empty, MoveKind.CastleQueen);
        }

        public static Move Capture(Square0x88 from, Square0x88 to, Piece mover, Piece captured)
            => new(from, to, mover, captured, Piece.Empty, MoveKind.Normal);

        public static Move PromotionPush(Square0x88 from, Square0x88 to, Piece mover, Piece promotion)
        {
            // Validate promotion invariants
            if (promotion == Piece.Empty)
                throw new ArgumentException("Promotion piece must not be empty.", nameof(promotion));
            int toRank = to.Value >> 4;
            if (!(toRank == 0 || toRank == 7))
                throw new ArgumentException("Promotion must occur on rank 1 or 8.", nameof(to));
            return new(from, to, mover, Piece.Empty, promotion, MoveKind.Promotion);
        }

        public static Move PromotionCapture(Square0x88 from, Square0x88 to, Piece mover, Piece captured, Piece promotion)
        {
            if (promotion == Piece.Empty)
                throw new ArgumentException("Promotion piece must not be empty.", nameof(promotion));
            int toRank = to.Value >> 4;
            if (!(toRank == 0 || toRank == 7))
                throw new ArgumentException("Promotion must occur on rank 1 or 8.", nameof(to));
            if (captured == Piece.Empty)
                throw new ArgumentException("Promotion capture must specify captured piece.", nameof(captured));
            return new(from, to, mover, captured, promotion, MoveKind.PromotionCapture);
        }
        public bool IsQuiet => !IsCapture && !IsPromotion;
        public bool IsCapture => Kind == MoveKind.Normal && Captured != Piece.Empty
                     || Kind == MoveKind.EnPassant
                     || Kind == MoveKind.PromotionCapture;
        public bool IsPromotion => Kind == MoveKind.Promotion || Kind == MoveKind.PromotionCapture;
        public bool IsCastle => Kind == MoveKind.CastleKing || Kind == MoveKind.CastleQueen;
        public bool IsEnPassant => Kind == MoveKind.EnPassant;

        public override string ToString()
        {
            string fromAlg = Squares.ToAlgebraicString(From88);
            string toAlg = Squares.ToAlgebraicString(To88);
            string promoStr = IsPromotion ? $"={Promotion}" : string.Empty;
            string captureStr = IsCapture ? "x" : "-";
            return $"{fromAlg}{captureStr}{toAlg}{promoStr}";
        }

        public string ToUCIString()
        {
            var from = Squares.ToAlgebraicString(From88).ToLower();
            var to = Squares.ToAlgebraicString(To88).ToLower();

            if (IsPromotion)
            {
                char promoChar = char.ToLower(Piece.ToFENChar(Promotion));
                return $"{from}{to}{promoChar}";
            }
            else
            {
                return $"{from}{to}";
            }
        }
    }

    public readonly record struct Undo(
        Piece Captured,
        FileIndex? EnPassantFilePrev,
        CastlingRightsFlags CastlingPrev,
        int HalfmovePrev,
        int FullmovePrev,
        Color SideToMovePrev,
        ulong ZKeyPrev,
        // new: for EP/castling reversals
        Square0x88? EnPassantCapturedSq88,
        Square0x88? CastleRookFrom88,
        Square0x88? CastleRookTo88);

    /// <summary>
    /// Lightweight snapshot used to make/unmake null moves without affecting history tracking.
    /// </summary>
    internal readonly record struct NullMoveState(
        FileIndex? EnPassantFilePrev,
        int HalfmovePrev,
        Color SideToMovePrev,
        ulong ZKeyPrev);


    // Hoisted castling squares for performance (span-based, no AlgebraicNotation)
    private static readonly Square0x88 E1 = Squares.ParseAlgebraicTo0x88("e1");
    private static readonly Square0x88 G1 = Squares.ParseAlgebraicTo0x88("g1");
    private static readonly Square0x88 C1 = Squares.ParseAlgebraicTo0x88("c1");
    private static readonly Square0x88 H1 = Squares.ParseAlgebraicTo0x88("h1");
    private static readonly Square0x88 F1 = Squares.ParseAlgebraicTo0x88("f1");
    private static readonly Square0x88 A1 = Squares.ParseAlgebraicTo0x88("a1");
    private static readonly Square0x88 D1 = Squares.ParseAlgebraicTo0x88("d1");
    private static readonly Square0x88 E8 = Squares.ParseAlgebraicTo0x88("e8");
    private static readonly Square0x88 G8 = Squares.ParseAlgebraicTo0x88("g8");
    private static readonly Square0x88 C8 = Squares.ParseAlgebraicTo0x88("c8");
    private static readonly Square0x88 H8 = Squares.ParseAlgebraicTo0x88("h8");
    private static readonly Square0x88 F8 = Squares.ParseAlgebraicTo0x88("f8");
    private static readonly Square0x88 A8 = Squares.ParseAlgebraicTo0x88("a8");
    private static readonly Square0x88 D8 = Squares.ParseAlgebraicTo0x88("d8");

    public Undo MakeMove(in Move m)
    {
#if DEBUG
        var destPieceBefore = (Piece)mailbox[m.To88];
        if (m.IsEnPassant)
        {
            // EP destination must be empty by definition
            if (destPieceBefore != Piece.Empty)
                throw new InvalidOperationException("EP destination square was not empty.");
        }
        else if (m.IsCapture)
        {
            if (destPieceBefore == Piece.Empty)
                throw new InvalidOperationException($"Capture to empty square at {Squares.ToAlgebraic(m.To88)}.");
            if (destPieceBefore == m.Mover || destPieceBefore.IsWhite == m.Mover.IsWhite)
                throw new InvalidOperationException("Capture of own piece generated.");
        }
        else
        {
            if (destPieceBefore != Piece.Empty)
                throw new InvalidOperationException($"Quiet move to occupied square at {Squares.ToAlgebraic(m.To88)}.");
        }
#endif

        // Save undo (we'll fill the new special fields below)
        var undo = new Undo(
            Captured: m.Kind == MoveKind.EnPassant
                ? (SideToMove.IsWhite() ? Piece.BlackPawn : Piece.WhitePawn)
                : (Piece)mailbox[m.To88],
            EnPassantFilePrev: EnPassantFile,
            CastlingPrev: CastlingRights,
            HalfmovePrev: HalfmoveClock,
            FullmovePrev: FullmoveNumber,
            SideToMovePrev: SideToMove,
            ZKeyPrev: ZKey,
            EnPassantCapturedSq88: null,
            CastleRookFrom88: null,
            CastleRookTo88: null);

        if (KeepTrackOfHistory)
        {
            // Track move history
            _moveHistory.Add(m);
            _undoHistory.Add(undo);
        }

        // --- Clear old EP key
        SetEnPassantFile(null);

        // --- Halfmove + fullmove
        bool isPawnMove = (m.Mover.Type == Piece.PieceType.Pawn);
        HalfmoveClock = (isPawnMove || undo.Captured != Piece.Empty) ? 0 : (HalfmoveClock + 1);
        if (!SideToMove.IsWhite()) FullmoveNumber++;

        // --- Update castling rights if king/rook move or rook captured on home square
        var newCR = CastlingRights;
        // own king moved -> clear both sides
        if (m.Mover == Piece.WhiteKing)
        {
            newCR &= ~(CastlingRightsFlags.WhiteKing | CastlingRightsFlags.WhiteQueen);
            _whiteKingSquare88 = m.To88;
        }
        if (m.Mover == Piece.BlackKing)
        {
            newCR &= ~(CastlingRightsFlags.BlackKing | CastlingRightsFlags.BlackQueen);
            _blackKingSquare88 = m.To88;
        }
        // own rook moved from corner
        if (m.Mover == Piece.WhiteRook)
        {
            if (m.From88 == A1) newCR &= ~CastlingRightsFlags.WhiteQueen;
            if (m.From88 == H1) newCR &= ~CastlingRightsFlags.WhiteKing;
        }
        if (m.Mover == Piece.BlackRook)
        {
            if (m.From88 == A8) newCR &= ~CastlingRightsFlags.BlackQueen;
            if (m.From88 == H8) newCR &= ~CastlingRightsFlags.BlackKing;
        }
        // captured rook on its corner
        if (undo.Captured == Piece.WhiteRook)
        {
            if (m.To88 == A1) newCR &= ~CastlingRightsFlags.WhiteQueen;
            if (m.To88 == H1) newCR &= ~CastlingRightsFlags.WhiteKing;
        }
        if (undo.Captured == Piece.BlackRook)
        {
            if (m.To88 == A8) newCR &= ~CastlingRightsFlags.BlackQueen;
            if (m.To88 == H8) newCR &= ~CastlingRightsFlags.BlackKing;
        }
        if (newCR != CastlingRights) SetCastlingRights(newCR); // toggles ZKey appropriately

        // --- Handle captures (normal capture only; EP handled later)
        if (m.IsCapture && !m.IsEnPassant)
        {
            RemoveFromBitboards((Square0x64)m.To88, undo.Captured);
            mailbox[m.To88] = (sbyte)Piece.Empty;
            XorZPiece(undo.Captured, m.To88);
        }

        // --- Move the mover piece off the from-square
        RemoveFromBitboards((Square0x64)m.From88, m.Mover);
        mailbox[m.From88] = (sbyte)Piece.Empty;
        XorZPiece(m.Mover, m.From88);

        // --- Special: castling rook movement
        if (m.IsCastle)
        {
            bool white = m.Mover.IsWhite;
            // Define king/rook target squares
            var kFrom = m.From88;
            var kTo = m.To88;
            Square0x88 rFrom, rTo;

            if (white)
            {
                if (m.Kind == MoveKind.CastleKing)
                {
                    rFrom = H1;
                    rTo = F1;
                }
                else
                {
                    rFrom = A1;
                    rTo = D1;
                }
            }
            else
            {
                if (m.Kind == MoveKind.CastleKing)
                {
                    rFrom = H8;
                    rTo = F8;
                }
                else
                {
                    rFrom = A8;
                    rTo = D8;
                }
            }

            // Move king to kTo
            mailbox[kTo] = (sbyte)m.Mover;
            AddToBitboards((Square0x64)kTo, m.Mover);
            XorZPiece(m.Mover, kTo);

            // Move rook rFrom -> rTo
            var rook = white ? Piece.WhiteRook : Piece.BlackRook;
            RemoveFromBitboards((Square0x64)rFrom, rook);
            mailbox[rFrom] = (sbyte)Piece.Empty;
            XorZPiece(rook, rFrom);

            mailbox[rTo] = (sbyte)rook;
            AddToBitboards((Square0x64)rTo, rook);
            XorZPiece(rook, rTo);

            undo = undo with { CastleRookFrom88 = rFrom, CastleRookTo88 = rTo };
        }
        else
        {
            // --- EP capture removal (captured pawn sits behind the to-square)
            if (m.IsEnPassant)
            {
                bool white = m.Mover.IsWhite;
                var capSq = white ? (m.To88 - 16) : (m.To88 + 16);
                var capPiece = white ? Piece.BlackPawn : Piece.WhitePawn;

                if (Squares.IsOffboard(capSq))
                    throw new InvalidOperationException("En Passant capture square is offboard.");

                var safeCapSq = (Square0x88)capSq;

                RemoveFromBitboards((Square0x64)safeCapSq, capPiece);
                mailbox[(int)safeCapSq] = (sbyte)Piece.Empty;
                XorZPiece(capPiece, safeCapSq);

                undo = undo with { EnPassantCapturedSq88 = safeCapSq };
            }

            // --- Place the moved piece (promotion if any)
            var placed = m.IsPromotion ? m.Promotion : m.Mover;
            mailbox[m.To88] = (sbyte)placed;
            AddToBitboards((Square0x64)m.To88, placed);
            XorZPiece(placed, m.To88);
        }

        // --- New EP target if a pawn moved two squares
        if (isPawnMove && ((int)(m.To88 - m.From88) == +32 || (int)(m.To88 - m.From88) == -32))
        {
            var file = (FileIndex)(m.From88.Value & 0x0F);
            SetEnPassantFile(file);
        }

        // --- Side to move
        SideToMove = SideToMove.Flip();

        // Repetition bookkeeping
        if (KeepTrackOfHistory)
        {
            _hashStack.Push(ZKey);
            if (_repCounts.TryGetValue(ZKey, out var c)) _repCounts[ZKey] = c + 1;
            else _repCounts[ZKey] = 1;
        }

        return undo;
    }

    public void UnmakeMove(in Move m, in Undo u)
    {
        // Pop repetition entry
        if (_hashStack.Count > 0)
        {
            var keyAfterMove = _hashStack.Pop();
            if (KeepTrackOfHistory && _repCounts.TryGetValue(keyAfterMove, out var c))
            {
                if (c <= 1) _repCounts.Remove(keyAfterMove);
                else _repCounts[keyAfterMove] = c - 1;
            }
        }

        // Track undo history
        if (_moveHistory.Count > 0) _moveHistory.RemoveAt(_moveHistory.Count - 1);
        if (_undoHistory.Count > 0) _undoHistory.RemoveAt(_undoHistory.Count - 1);

        // Restore side and ZKey (this also covers EP and castling zobrist, so do it early)
        _sideToMove = u.SideToMovePrev;
        ZKey = u.ZKeyPrev;

        // Clear destination / rook squares as needed and put things back
        if (m.IsCastle)
        {
            // Undo rook move
            if (u.CastleRookFrom88 is Square0x88 rFrom && u.CastleRookTo88 is Square0x88 rTo)
            {
                var rook = m.Mover.IsWhite ? Piece.WhiteRook : Piece.BlackRook;

                RemoveFromBitboards((Square0x64)rTo, rook);
                mailbox[rTo] = (sbyte)Piece.Empty;

                mailbox[rFrom] = (sbyte)rook;
                AddToBitboards((Square0x64)rFrom, rook);
            }

            // Move king back
            RemoveFromBitboards((Square0x64)m.To88, m.Mover);
            mailbox[m.To88] = (sbyte)Piece.Empty;

            mailbox[m.From88] = (sbyte)m.Mover;
            AddToBitboards((Square0x64)m.From88, m.Mover);
        }
        else
        {
            // Remove piece from To (promotion piece may be there)
            var placed = m.IsPromotion ? m.Promotion : m.Mover;

            RemoveFromBitboards((Square0x64)m.To88, placed);  // placed is Piece (non-null)
            mailbox[m.To88] = (sbyte)Piece.Empty;

            // Put mover back
            mailbox[m.From88] = (sbyte)m.Mover;
            AddToBitboards((Square0x64)m.From88, m.Mover);

            // Restore captured piece ...
            if (m.IsEnPassant)
            {
                if (u.EnPassantCapturedSq88 is Square0x88 capSq)
                {
                    var capPiece = m.Mover.IsWhite ? Piece.BlackPawn : Piece.WhitePawn;
                    mailbox[capSq] = (sbyte)capPiece;
                    AddToBitboards((Square0x64)capSq, capPiece);
                }
            }
            else if (m.IsCapture && u.Captured != Piece.Empty)
            {
                mailbox[m.To88] = (sbyte)u.Captured;
                AddToBitboards((Square0x64)m.To88, u.Captured);
            }
        }

        if (m.Mover == Piece.WhiteKing) _whiteKingSquare88 = m.From88;
        else if (m.Mover == Piece.BlackKing) _blackKingSquare88 = m.From88;

        EnPassantFile = u.EnPassantFilePrev;
        CastlingRights = u.CastlingPrev;
        HalfmoveClock = u.HalfmovePrev;
        FullmoveNumber = u.FullmovePrev;
    }

    /// <summary>
    /// Performs a null move for search, flipping the side to move and clearing en passant without touching history.
    /// </summary>
    internal NullMoveState MakeNullMove()
    {
        var state = new NullMoveState(
            EnPassantFilePrev: EnPassantFile,
            HalfmovePrev: HalfmoveClock,
            SideToMovePrev: SideToMove,
            ZKeyPrev: ZKey);

        if (EnPassantFile is FileIndex oldEp)
        {
            ZKey ^= Tables.Zobrist.EnPassant[oldEp.Value];
        }

        EnPassantFile = null;
        HalfmoveClock++;
        SideToMove = SideToMove.Flip();

        return state;
    }

    /// <summary>
    /// Reverses a previously made null move using the supplied snapshot.
    /// </summary>
    internal void UnmakeNullMove(in NullMoveState state)
    {
        _sideToMove = state.SideToMovePrev;
        ZKey = state.ZKeyPrev;
        EnPassantFile = state.EnPassantFilePrev;
        HalfmoveClock = state.HalfmovePrev;
    }

    /// <summary>
    /// Generates all legal moves for the side to move.
    /// </summary>
    /// <returns>An array of legal moves</returns>
    // It is safe to use [SkipLocalsInit] here because the stackalloc'd Move buffer is fully written to
    // before any reads occur; no uninitialized memory is ever accessed. This is a performance optimization.
    [SkipLocalsInit]
    public Move[] GenerateLegal()
    {
        Span<Move> moveBuffer = stackalloc Move[MoveBufferMax];
        GenerateLegal(ref moveBuffer);
        Move[] result = moveBuffer.ToArray();
        return result;
    }

    public void GenerateLegal(ref Span<Move> moveBuffer)
    {
        MoveGeneration.GeneratePseudoLegal(this, ref moveBuffer, SideToMove); // <- slice of candidates

        int i = 0;
        for (int j = 0; j < moveBuffer.Length; j++)   // iterate only pseudo-candidates, not full buffer
        {
            var mv = moveBuffer[j];
            var u = MakeMove(mv);
            bool inCheck = InCheck(SideToMove.Flip());
            UnmakeMove(mv, u);
            if (!inCheck) moveBuffer[i++] = mv;
        }
        moveBuffer = moveBuffer[..i];
    }

    // It is safe to use [SkipLocalsInit] here because the stackalloc'd Move buffer is fully written to
    // before any reads occur; no uninitialized memory is ever accessed. This is a performance optimization.
    [SkipLocalsInit]
    public bool HasAnyLegalMoves()
    {
        Span<Move> moveBuffer = stackalloc Move[MoveBufferMax];
        MoveGeneration.GeneratePseudoLegal(this, ref moveBuffer, SideToMove);

        foreach (var mv in moveBuffer)
        {
            var u = MakeMove(mv);
            bool inCheck = InCheck(SideToMove.Flip());
            UnmakeMove(mv, u);
            if (!inCheck) return true;
        }
        return false;
    }

    /// <summary>
    /// Generates all pseudo-legal moves for the side to move.
    /// </summary>
    public Move[] GeneratePseudoLegal() =>
        MoveGeneration.GeneratePseudoLegal(this, SideToMove);

    private void XorZPiece(Piece p, Square0x88 sq88)
    {
        if (p == Piece.Empty) return;
        var s64 = (Square0x64)sq88;
        ZKey ^= Tables.Zobrist.PieceSquare[p.PieceIndex, (int)s64];
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RayAttacksFromRook(Square0x64 sq64, ulong occ)
    {
        ulong attacks = 0;
        Square0x88 sq88 = (Square0x88)sq64;
        // +1 (right)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t += 1;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        // -1 (left)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t -= 1;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        // +16 (up)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t += 16;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        // -16 (down)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t -= 16;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        return attacks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RayAttacksFromBishop(Square0x64 sq64, ulong occ)
    {
        ulong attacks = 0;
        Square0x88 sq88 = (Square0x88)sq64;
        // +15 (up-left)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t += 15;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        // +17 (up-right)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t += 17;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        // -15 (down-right)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t -= 15;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        // -17 (down-left)
        {
            UnsafeSquare0x88 t = (UnsafeSquare0x88)sq88;
            while (true)
            {
                t -= 17;
                if (Squares.IsOffboard(t)) break;
                Square0x64 t64 = (Square0x64)t;
                attacks |= 1UL << (int)t64;
                if (((occ >> (int)t64) & 1UL) != 0) break;
            }
        }
        return attacks;
    }

    internal ulong RookAttacks(Square0x64 sq64) => RayAttacksFromRook(sq64, OccAll);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong BishopAttacks(Square0x64 sq64) => RayAttacksFromBishop(sq64, OccAll);

    // Board.cs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSquareAttacked(Square0x64 t64, Color bySide)
    {
        var T = Tables;
        int ti = t64.Value;
        bool byWhite = bySide.IsWhite();
        ulong occAll = OccAll; // single load; JIT can keep this in a register

        // Knights
        ulong knights = byWhite ? GetPieceBitboard(Piece.WhiteKnight) : GetPieceBitboard(Piece.BlackKnight);
        if ((T.KnightAttackTable[ti] & knights) != 0) return true;

        // Kings
        ulong kings = byWhite ? GetPieceBitboard(Piece.WhiteKing) : GetPieceBitboard(Piece.BlackKing);
        if ((T.KingAttackTable[ti] & kings) != 0) return true;

        // Pawns (reverse attack-from masks keyed by target)
        if (byWhite)
        {
            if ((T.WhitePawnAttackFrom[ti] & GetPieceBitboard(Piece.WhitePawn)) != 0) return true;
        }
        else
        {
            if ((T.BlackPawnAttackFrom[ti] & GetPieceBitboard(Piece.BlackPawn)) != 0) return true;
        }

        // Bishop-like
        ulong bishopOcc = occAll & EngineTables.BishopMasks[ti];
        int bIdx = EngineTables.GetSliderAttackIndex(ti, bishopOcc, Piece.PieceType.Bishop);
        ulong bishopLike = byWhite
            ? (GetPieceBitboard(Piece.WhiteBishop) | GetPieceBitboard(Piece.WhiteQueen))
            : (GetPieceBitboard(Piece.BlackBishop) | GetPieceBitboard(Piece.BlackQueen));
        if ((T.BishopTable[T.BishopOffsets[ti] + bIdx] & bishopLike) != 0) return true;

        // Rook-like
        ulong rookOcc = occAll & EngineTables.RookMasks[ti];
        int rIdx = EngineTables.GetSliderAttackIndex(ti, rookOcc, Piece.PieceType.Rook);
        ulong rookLike = byWhite
            ? (GetPieceBitboard(Piece.WhiteRook) | GetPieceBitboard(Piece.WhiteQueen))
            : (GetPieceBitboard(Piece.BlackRook) | GetPieceBitboard(Piece.BlackQueen));
        if ((T.RookTable[T.RookOffsets[ti] + rIdx] & rookLike) != 0) return true;

        return false;
    }

    public (bool knights, bool kings, bool pawns, bool bishopsQueens, bool rooksQueens) AttackerBreakdownBool(Square0x64 t64, bool byWhite)
    {
        var T = Tables;
        ulong wp = GetPieceBitboard(Piece.WhitePawn);
        ulong bp = GetPieceBitboard(Piece.BlackPawn);
        ulong wn = GetPieceBitboard(Piece.WhiteKnight);
        ulong bn = GetPieceBitboard(Piece.BlackKnight);
        ulong wk = GetPieceBitboard(Piece.WhiteKing);
        ulong bk = GetPieceBitboard(Piece.BlackKing);
        ulong wb = GetPieceBitboard(Piece.WhiteBishop);
        ulong bb = GetPieceBitboard(Piece.BlackBishop);
        ulong wr = GetPieceBitboard(Piece.WhiteRook);
        ulong br = GetPieceBitboard(Piece.BlackRook);
        ulong wq = GetPieceBitboard(Piece.WhiteQueen);
        ulong bq = GetPieceBitboard(Piece.BlackQueen);

        bool knt = (T.KnightAttackTable[(int)t64] & (byWhite ? wn : bn)) != 0;
        bool kng = (T.KingAttackTable[(int)t64] & (byWhite ? wk : bk)) != 0;
        bool pwn = byWhite
            ? ((T.WhitePawnAttackFrom[(int)t64] & wp) != 0)
            : ((T.BlackPawnAttackFrom[(int)t64] & bp) != 0);
        bool bishopQ = (BishopAttacks(t64) & (byWhite ? (wb | wq) : (bb | bq))) != 0;
        bool rookQ = (RookAttacks(t64) & (byWhite ? (wr | wq) : (br | bq))) != 0;

        return (knt, kng, pwn, bishopQ, rookQ);
    }

    public ulong AttackersToSquare(Square0x64 t64, Color bySide, Piece.PieceType? pieceFilters = null)
    {
        var T = Tables;
        bool byWhite = bySide.IsWhite();

        ulong attackers = 0UL;

        // here, pieceFilters can be any combination of piece types, eg. Piece.PieceType.Knight | Piece.PieceType.Bishop
        if (!pieceFilters.HasValue || pieceFilters.Value.HasFlag(Piece.PieceType.Knight))
        {
            // Knights
            ulong knights = byWhite ? GetPieceBitboard(Piece.WhiteKnight) : GetPieceBitboard(Piece.BlackKnight);
            attackers |= (T.KnightAttackTable[(int)t64] & knights);
        }

        if (!pieceFilters.HasValue || pieceFilters.Value.HasFlag(Piece.PieceType.King))
        {
            // Kings
            ulong kings = byWhite ? GetPieceBitboard(Piece.WhiteKing) : GetPieceBitboard(Piece.BlackKing);
            attackers |= (T.KingAttackTable[(int)t64] & kings);
        }

        if (!pieceFilters.HasValue || pieceFilters.Value.HasFlag(Piece.PieceType.Pawn))
        {
            // Pawns (reverse attack-from masks)
            if (byWhite)
                attackers |= (T.WhitePawnAttackFrom[(int)t64] & GetPieceBitboard(Piece.WhitePawn));
            else
                attackers |= (T.BlackPawnAttackFrom[(int)t64] & GetPieceBitboard(Piece.BlackPawn));
        }

        if (!pieceFilters.HasValue || pieceFilters.Value.HasFlag(Piece.PieceType.Bishop) || pieceFilters.Value.HasFlag(Piece.PieceType.Queen))
        {
            // Bishops / Queens along diagonals
            ulong bishopsQueens = byWhite
                ? (GetPieceBitboard(Piece.WhiteBishop) | GetPieceBitboard(Piece.WhiteQueen))
                : (GetPieceBitboard(Piece.BlackBishop) | GetPieceBitboard(Piece.BlackQueen));
            attackers |= (BishopAttacks(t64) & bishopsQueens);
        }

        if (!pieceFilters.HasValue || pieceFilters.Value.HasFlag(Piece.PieceType.Rook) || pieceFilters.Value.HasFlag(Piece.PieceType.Queen))
        {
            // Rooks / Queens along ranks/files
            ulong rooksQueens = byWhite
                ? (GetPieceBitboard(Piece.WhiteRook) | GetPieceBitboard(Piece.WhiteQueen))
                : (GetPieceBitboard(Piece.BlackRook) | GetPieceBitboard(Piece.BlackQueen));
            attackers |= (RookAttacks(t64) & rooksQueens);
        }

        return attackers;
    }

    public void Clear()
    {
        // Mailbox + bitboards
        Array.Fill(mailbox, (sbyte)Piece.Empty);
        Array.Fill(pieceBB, 0UL);

        // Occupancy
        OccWhite = OccBlack = OccAll = 0UL;

        // Scalar state
        SideToMove = Color.White;
        EnPassantFile = null;
        HalfmoveClock = 0;
        FullmoveNumber = 1;
        CastlingRights = CastlingRightsFlags.None;

        // Repetition tracking
        _repCounts.Clear();
        _hashStack.Clear();

        // Zobrist from empty state
        UpdateZobristFull();

        // Move history tracking
        _moveHistory.Clear();
        _undoHistory.Clear();
    }

    /// <summary>
    /// Sets the board to the standard starting position.
    /// </summary>
    public void SetStartPosition() => SetPositionFromFEN("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

    private void UpdateZobristFull()
    {
        ulong key = 0;
        for (UnsafeSquare0x88 sq88 = (UnsafeSquare0x88)0; (int)sq88 < 128; sq88++)
        {
            if (Squares.IsOffboard(sq88)) continue;
            var p = (Piece)mailbox[(int)sq88];
            if (p == Piece.Empty) continue;
            var s64 = (Square0x64)sq88;
            key ^= Tables.Zobrist.PieceSquare[p.PieceIndex, (int)s64];
        }
        if (!SideToMove.IsWhite()) key ^= Tables.Zobrist.SideToMove;
        if (EnPassantFile is FileIndex epf) key ^= Tables.Zobrist.EnPassant[epf.Value];
        key ^= Tables.Zobrist.Castling[(int)CastlingRights & 0xF];
        ZKey = key;
    }

    /// <summary>
    /// Sets the board position from a FEN string.
    /// </summary>
    public void SetPositionFromFEN(string fen)
    {
        Clear();
        var parts = fen.Split(' ');
        if (parts.Length < 4)
            throw new ArgumentException("Invalid FEN string.", nameof(fen));
        var ranks = parts[0].Split('/');
        if (ranks.Length != 8)
            throw new ArgumentException("Invalid FEN: must have 8 ranks.", nameof(fen));
        Array.Fill(mailbox, (sbyte)Piece.Empty);
        Array.Fill(pieceBB, 0UL);
        OccWhite = OccBlack = OccAll = 0UL;
        for (int rank = 0; rank < 8; rank++)
        {
            int file = 0;
            foreach (char c in ranks[rank])
            {
                if (char.IsDigit(c))
                {
                    file += c - '0';
                }
                else
                {
                    int sq88 = ((7 - rank) << 4) | file;
                    Piece pc = Piece.FromFENChar(c);
                    Place(new Square0x88(sq88), pc);
                    file++;
                }
            }
        }
        SideToMove = parts[1] == "w" ? Color.White : Color.Black;
        CastlingRightsFlags cr = CastlingRightsFlags.None;
        if (parts[2].Contains('K')) cr |= CastlingRightsFlags.WhiteKing;
        if (parts[2].Contains('Q')) cr |= CastlingRightsFlags.WhiteQueen;
        if (parts[2].Contains('k')) cr |= CastlingRightsFlags.BlackKing;
        if (parts[2].Contains('q')) cr |= CastlingRightsFlags.BlackQueen;
        CastlingRights = cr;
        if (parts[3] != "-")
        {
            var epFile = parts[3][0] - 'a';
            EnPassantFile = new FileIndex(epFile);
        }
        else
        {
            EnPassantFile = null;
        }
        if (parts.Length > 4)
            HalfmoveClock = int.TryParse(parts[4], out var hmc) ? hmc : 0;
        else
            HalfmoveClock = 0;
        if (parts.Length > 5)
            FullmoveNumber = int.TryParse(parts[5], out var fmn) ? fmn : 1;
        else
            FullmoveNumber = 1;
        UpdateZobristFull();
    }

    /// <summary>
    /// Gets the FEN string for the current board position.
    /// </summary>
    public string GetFEN()
    {
        var fen = new System.Text.StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                int sq88 = (rank << 4) | file;
                Piece pc = (Piece)mailbox[sq88];
                if (pc == Piece.Empty)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0)
                    {
                        fen.Append(empty);
                        empty = 0;
                    }
                    fen.Append(Piece.ToFENChar(pc));
                }
            }
            if (empty > 0)
                fen.Append(empty);
            if (rank > 0)
                fen.Append('/');
        }
        fen.Append(' ');
        fen.Append(SideToMove == Color.White ? 'w' : 'b');
        fen.Append(' ');
        string cr = "";
        if ((CastlingRights & CastlingRightsFlags.WhiteKing) != 0) cr += "K";
        if ((CastlingRights & CastlingRightsFlags.WhiteQueen) != 0) cr += "Q";
        if ((CastlingRights & CastlingRightsFlags.BlackKing) != 0) cr += "k";
        if ((CastlingRights & CastlingRightsFlags.BlackQueen) != 0) cr += "q";
        fen.Append(cr.Length > 0 ? cr : "-");
        fen.Append(' ');
        if (EnPassantFile is FileIndex epf)
        {
            int epRank = SideToMove == Color.White ? 6 : 3;
            fen.Append((char)('a' + epf.Value));
            fen.Append(epRank);
        }
        else
        {
            fen.Append('-');
        }
        fen.Append(' ');
        fen.Append(HalfmoveClock);
        fen.Append(' ');
        fen.Append(FullmoveNumber);
        return fen.ToString();
    }

    public void SetEnPassantFile(FileIndex? file)
    {
        if (EnPassantFile == file) return;

        if (EnPassantFile is FileIndex oldFile)
            ZKey ^= Tables.Zobrist.EnPassant[oldFile.Value];

        EnPassantFile = file;

        if (file is FileIndex newFile)
            ZKey ^= Tables.Zobrist.EnPassant[newFile.Value];
    }


    // If your Zobrist has a single entry per full rights mask:
    public void SetCastlingRights(CastlingRightsFlags newRightsMask)
    {
        if (CastlingRights == newRightsMask) return;

        // XOR out old mask, XOR in new mask
        ZKey ^= Tables.Zobrist.Castling[(int)CastlingRights];  // old
        ZKey ^= Tables.Zobrist.Castling[(int)newRightsMask];   // new
        CastlingRights = newRightsMask;
    }

    // Returns the bitboard for a specific piece type.
    // Safe for parallel reads; it's just a by-value ulong from this instance's state.
    public ulong GetPieceBitboard(Piece? piece) => piece == null ? throw new ArgumentNullException(nameof(piece)) : GetPieceBitboard(piece.Value);

    public ulong GetPieceBitboard(Piece piece)
    {
        if (piece == Piece.Empty)
            throw new ArgumentException("Piece cannot be empty.", nameof(piece));
        return pieceBB[piece.PieceIndex];
    }

    // Occupancy helpers (already exposed as properties, but symmetric with the API above)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetOccupancy(Color side) => side == Color.White ? OccWhite : OccBlack;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetAllOccupancy() => OccAll;

    public bool InCheck(Color side)
    {
        ulong kingBB = GetPieceBitboard(side.IsWhite() ? Piece.WhiteKing : Piece.BlackKing);
        if (kingBB == 0) return false;
        var kingSq64 = (Square0x64)BitOperations.TrailingZeroCount(kingBB);

        var attacker = side.IsWhite() ? Color.Black : Color.White;
        return IsSquareAttacked(kingSq64, attacker);
    }

    public bool EnPassantAvailableFor(Color sideToMove)
    {
        // EP only valid on the immediate reply by the current side to move
        if (EnPassantFile is not FileIndex file) return false;
        if (sideToMove != SideToMove) return false;

        // EP target: square passed over. For White-to-move, it lies on 6th rank (index 5).
        // For Black-to-move, it lies on 3rd rank (index 2).
        int epRank = sideToMove.IsWhite() ? 5 : 2;
        int ep88 = (epRank << 4) | file.Value;

        // Target must be empty by definition.
        if (mailbox[ep88] != (sbyte)Piece.Empty) return false;

        // The pawn that moved two squares must be behind the target.
        int capSq88 = sideToMove.IsWhite() ? (ep88 - 16) : (ep88 + 16);
        if ((capSq88 & 0x88) != 0) return false;

        var expectedCaptured = sideToMove.IsWhite() ? Piece.BlackPawn : Piece.WhitePawn;
        if ((Piece)mailbox[capSq88] != expectedCaptured) return false;

        return true;
    }

    /// <summary>
    /// Checks if the current position is a threefold repetition draw.
    /// </summary>
    public bool IsThreefoldRepetitionDraw()
    {
        return KeepTrackOfHistory && _repCounts.TryGetValue(ZKey, out var count) && count >= 3;
    }

    /// <summary>
    /// Checks if the fifty-move rule draw applies.
    /// </summary>
    public bool IsFiftyMoveRuleDraw()
    {
        return HalfmoveClock >= 100;
    }

    /// <summary>
    /// Checks if the position is a draw due to insufficient material.
    /// </summary>
    public bool IsInsufficientMaterialDraw()
    {
        // Count pieces
        int whitePawns = BitOperations.PopCount(GetPieceBitboard(Piece.WhitePawn));
        int blackPawns = BitOperations.PopCount(GetPieceBitboard(Piece.BlackPawn));
        int whiteKnights = BitOperations.PopCount(GetPieceBitboard(Piece.WhiteKnight));
        int blackKnights = BitOperations.PopCount(GetPieceBitboard(Piece.BlackKnight));
        int whiteBishops = BitOperations.PopCount(GetPieceBitboard(Piece.WhiteBishop));
        int blackBishops = BitOperations.PopCount(GetPieceBitboard(Piece.BlackBishop));
        int whiteRooks = BitOperations.PopCount(GetPieceBitboard(Piece.WhiteRook));
        int blackRooks = BitOperations.PopCount(GetPieceBitboard(Piece.BlackRook));
        int whiteQueens = BitOperations.PopCount(GetPieceBitboard(Piece.WhiteQueen));
        int blackQueens = BitOperations.PopCount(GetPieceBitboard(Piece.BlackQueen));

        // Only kings
        if (whitePawns + blackPawns + whiteKnights + blackKnights + whiteBishops + blackBishops + whiteRooks + blackRooks + whiteQueens + blackQueens == 0)
            return true;

        // King + bishop or king + knight vs king
        if (whitePawns + blackPawns + whiteRooks + blackRooks + whiteQueens + blackQueens == 0)
        {
            if ((whiteKnights + whiteBishops <= 1) && (blackKnights + blackBishops == 0)) return true;
            if ((blackKnights + blackBishops <= 1) && (whiteKnights + whiteBishops == 0)) return true;
        }

        // King + bishop vs king + bishop (bishops on same color)
        if (whitePawns + blackPawns + whiteRooks + blackRooks + whiteQueens + blackQueens + whiteKnights + blackKnights == 0)
        {
            if (whiteBishops == 1 && blackBishops == 1)
            {
                // Check if both bishops are on the same color square
                int whiteBishopSq = BitOperations.TrailingZeroCount(GetPieceBitboard(Piece.WhiteBishop));
                int blackBishopSq = BitOperations.TrailingZeroCount(GetPieceBitboard(Piece.BlackBishop));
                bool whiteIsLight = whiteBishopSq % 2 == 0;
                bool blackIsLight = blackBishopSq % 2 == 0;
                if (whiteIsLight == blackIsLight)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the side to move is stalemated.
    /// </summary>
    public bool IsStalemate()
    {
        if (InCheck(SideToMove)) return false;
        return !HasAnyLegalMoves();
    }

    /// <summary>
    /// Checks if the side to move is checkmated.
    /// </summary>
    public bool IsCheckmate()
    {
        if (!InCheck(SideToMove)) return false;
        return !HasAnyLegalMoves();
    }

    /// <summary>
    /// Parses a UCI move string (e.g., "e2e4", "e7e8q") and returns a legal Move if possible.
    /// </summary>
    public Move? ParseUCIMove(string uci)
    {
        if (uci is null) return null;

        // Work on a span (no Substring/allocations)
        ReadOnlySpan<char> s = uci.AsSpan();
        // (Optional) accept surrounding whitespace with span trim:
        s = s.Trim();

        if (s.Length != 4 && s.Length != 5) return null;

        // Parse squares directly from spans
        var fromSq = Squares.ParseAlgebraicTo0x88(s.Slice(0, 2));
        var toSq = Squares.ParseAlgebraicTo0x88(s.Slice(2, 2));

        // Promotion (if any)
        Piece promotion = Piece.Empty;
        if (s.Length == 5)
            promotion = Piece.FromPromotionChar(s[4], SideToMove); // case-insensitive, uses side to choose color

        // Scan legal moves without allocating
        Span<Move> buffer = stackalloc Move[MoveBufferMax];
        GenerateLegal(ref buffer);

        for (int i = 0; i < buffer.Length; i++)
        {
            ref readonly var m = ref buffer[i];
            if (m.From88 == fromSq && m.To88 == toSq)
            {
                // If UCI included a promo, it must match; otherwise require no-promo move
                bool promoOk = promotion == Piece.Empty ? m.Promotion == Piece.Empty
                                                        : m.Promotion == promotion;
                if (promoOk)
                    return m;
            }
        }

        return null;
    }


    /// <summary>
    /// Parses and applies a UCI move string if legal. Returns true if move was made.
    /// </summary>
    public bool TryApplyUCIMove(string uci)
    {
        var move = ParseUCIMove(uci);
        if (move is Move m)
        {
            MakeMove(m);
            return true;
        }
        return false;
    }

    // Public move history for undo/redo (UCI go/undo commands)
    public IReadOnlyList<Move> MoveHistory => _moveHistory;
    public IReadOnlyList<Undo> UndoHistory => _undoHistory;
    private readonly List<Move> _moveHistory = new();
    private readonly List<Undo> _undoHistory = new();

    /// <summary>
    /// Undoes the last move in the history, if any.
    /// </summary>
    public bool UndoLastMove()
    {
        if (_moveHistory.Count == 0 || _undoHistory.Count == 0)
            return false;
        var lastMove = _moveHistory[^1];
        var lastUndo = _undoHistory[^1];
        UnmakeMove(lastMove, lastUndo);
        _moveHistory.RemoveAt(_moveHistory.Count - 1);
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        return true;
    }

    /// <summary>
    /// Clears the move and undo history.
    /// </summary>
    public void ClearMoveHistory()
    {
        _moveHistory.Clear();
        _undoHistory.Clear();
    }
}
