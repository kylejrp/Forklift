using System;
using System.Numerics;
using System.Collections.Generic;

namespace Forklift.Core;

/// <summary>
/// Represents the chessboard and manages all mutable game state.
/// Thread-safe as long as each thread uses its own instance.
/// </summary>
public sealed class Board
{
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
    public bool WhiteToMove { get; private set; } = true;
    public CastlingRightsFlags CastlingRights { get; private set; } = CastlingRightsFlags.WhiteKing | CastlingRightsFlags.WhiteQueen | CastlingRightsFlags.BlackKing | CastlingRightsFlags.BlackQueen;
    public int? EnPassantFile { get; private set; } // 0..7 or null
    public int HalfmoveClock { get; private set; }
    public int FullmoveNumber { get; private set; } = 1;

    public ulong ZKey { get; private set; } // zobrist

    // Slider directions in 0x88 space (pure consts)
    private static readonly int[] RookDirections = { +1, -1, +16, -16 };
    private static readonly int[] BishopDirections = { +15, +17, -15, -17 };

    private readonly Dictionary<ulong, int> _repCounts = new();
    private readonly Stack<ulong> _hashStack = new();

    public int HashHistoryCount => _repCounts.TryGetValue(ZKey, out var n) ? n : 0;

    public int WhiteKingCount => BitOperations.PopCount(pieceBB[PieceUtil.Index(Piece.WhiteKing)]);
    public int BlackKingCount => BitOperations.PopCount(pieceBB[PieceUtil.Index(Piece.BlackKing)]);


    /// <summary>
    /// Initializes a new instance of the <see cref="Board"/> class.
    /// </summary>
    /// <param name="tables">Optional engine tables. If null, default tables are used.</param>
    public Board(EngineTables? tables = null)
    {
        Tables = tables ?? EngineTables.CreateDefault();
        SetStartPosition();
    }

    /// <summary>
    /// Clears the board and sets it to the standard starting position.
    /// </summary>
    public void ClearAndSetStart()
    {
        Array.Fill(mailbox, (sbyte)Piece.Empty);
        Array.Fill(pieceBB, 0UL);
        OccWhite = OccBlack = OccAll = 0;
        WhiteToMove = true;
        CastlingRights = CastlingRightsFlags.WhiteKing | CastlingRightsFlags.WhiteQueen | CastlingRightsFlags.BlackKing | CastlingRightsFlags.BlackQueen;
        EnPassantFile = null;
        HalfmoveClock = 0;
        FullmoveNumber = 1;
        ZKey = 0UL;

        // Place start position pieces
        PlaceStartingPieces();
        UpdateZobristFull(); // compute from scratch once; use incremental changes thereafter
    }

    private void PlaceStartingPieces()
    {
        Place("a1", Piece.WhiteRook); Place("b1", Piece.WhiteKnight); Place("c1", Piece.WhiteBishop); Place("d1", Piece.WhiteQueen);
        Place("e1", Piece.WhiteKing); Place("f1", Piece.WhiteBishop); Place("g1", Piece.WhiteKnight); Place("h1", Piece.WhiteRook);
        for (char f = 'a'; f <= 'h'; f++) Place($"{f}2", Piece.WhitePawn);

        for (char f = 'a'; f <= 'h'; f++) Place($"{f}7", Piece.BlackPawn);
        Place("a8", Piece.BlackRook); Place("b8", Piece.BlackKnight); Place("c8", Piece.BlackBishop); Place("d8", Piece.BlackQueen);
        Place("e8", Piece.BlackKing); Place("f8", Piece.BlackBishop); Place("g8", Piece.BlackKnight); Place("h8", Piece.BlackRook);
    }

    /// <summary>
    /// Gets the piece at the specified square.
    /// </summary>
    /// <param name="sq88">The square in 0x88 format.</param>
    /// <returns>The piece at the square.</returns>
    public Piece At(int sq88) => (Piece)mailbox[sq88];

    /// <summary>
    /// Places a piece on the board at the specified square.
    /// </summary>
    /// <param name="algebraic">The square in algebraic notation (e.g., "e4").</param>
    /// <param name="pc">The piece to place.</param>
    public void Place(string algebraic, Piece pc) => Place(Squares.ParseAlgebraicTo0x88(algebraic), pc);

    /// <summary>
    /// Places a piece on the board at the specified square.
    /// </summary>
    /// <param name="sq88">The square in 0x88 format.</param>
    /// <param name="pc">The piece to place.</param>
    public void Place(int sq88, Piece pc)
    {
        if (Squares.IsOffboard(sq88)) throw new ArgumentException("Offboard");
        RemoveIfAny(sq88);
        mailbox[sq88] = (sbyte)pc;
        if (pc != Piece.Empty) AddToBitboards(sq88, pc);
    }

    private void RemoveIfAny(int sq88)
    {
        var existing = (Piece)mailbox[sq88];
        if (existing == Piece.Empty) return;
        RemoveFromBitboards(sq88, existing);
        mailbox[sq88] = (sbyte)Piece.Empty;
    }

    private void AddToBitboards(int sq88, Piece pc)
    {
        int s64 = Squares.ConvertTo0x64Index(sq88); ulong b = 1UL << s64;
        pieceBB[PieceUtil.Index(pc)] |= b;
        if (PieceUtil.IsWhite(pc)) OccWhite |= b; else OccBlack |= b;
        OccAll |= b;
    }

    private void RemoveFromBitboards(int sq88, Piece pc)
    {
        int s64 = Squares.ConvertTo0x64Index(sq88); ulong b = 1UL << s64;
        pieceBB[PieceUtil.Index(pc)] &= ~b;
        if (PieceUtil.IsWhite(pc)) OccWhite &= ~b; else OccBlack &= ~b;
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
        int From88,
        int To88,
        Piece Mover,
        Piece Captured = Piece.Empty,
        Piece Promotion = Piece.Empty,
        MoveKind Kind = MoveKind.Normal);

    public readonly record struct Undo(
        Piece Captured,
        int? EnPassantFilePrev,
        CastlingRightsFlags CastlingPrev,
        int HalfmovePrev,
        bool WhiteToMovePrev,
        ulong ZKeyPrev,
        // new: for EP/castling reversals
        int? EnPassantCapturedSq88,
        int? CastleRookFrom88,
        int? CastleRookTo88);


    public Undo MakeMove(in Move m)
    {
        // Save undo (we'll fill the new special fields below)
        var undo = new Undo(
            Captured: (m.Kind == MoveKind.EnPassant ? (Piece)(WhiteToMove ? Piece.BlackPawn : Piece.WhitePawn) : (Piece)mailbox[m.To88]),
            EnPassantFilePrev: EnPassantFile,
            CastlingPrev: CastlingRights,
            HalfmovePrev: HalfmoveClock,
            WhiteToMovePrev: WhiteToMove,
            ZKeyPrev: ZKey,
            EnPassantCapturedSq88: null,
            CastleRookFrom88: null,
            CastleRookTo88: null);

        // --- Clear old EP key
        if (EnPassantFile is int oldEPFile) ZKey ^= Tables.Zobrist.EnPassant[oldEPFile];
        EnPassantFile = null;

        // --- Halfmove + fullmove
        bool isPawnMove = (m.Mover == Piece.WhitePawn || m.Mover == Piece.BlackPawn);
        HalfmoveClock = (isPawnMove || undo.Captured != Piece.Empty) ? 0 : (HalfmoveClock + 1);
        if (!WhiteToMove) FullmoveNumber++;

        // --- Update castling rights if king/rook move or rook captured on home square
        var newCR = CastlingRights;
        // own king moved -> clear both sides
        if (m.Mover == Piece.WhiteKing) newCR &= ~(CastlingRightsFlags.WhiteKing | CastlingRightsFlags.WhiteQueen);
        if (m.Mover == Piece.BlackKing) newCR &= ~(CastlingRightsFlags.BlackKing | CastlingRightsFlags.BlackQueen);
        // own rook moved from corner
        if (m.Mover == Piece.WhiteRook)
        {
            if (m.From88 == Squares.ParseAlgebraicTo0x88("a1")) newCR &= ~CastlingRightsFlags.WhiteQueen;
            if (m.From88 == Squares.ParseAlgebraicTo0x88("h1")) newCR &= ~CastlingRightsFlags.WhiteKing;
        }
        if (m.Mover == Piece.BlackRook)
        {
            if (m.From88 == Squares.ParseAlgebraicTo0x88("a8")) newCR &= ~CastlingRightsFlags.BlackQueen;
            if (m.From88 == Squares.ParseAlgebraicTo0x88("h8")) newCR &= ~CastlingRightsFlags.BlackKing;
        }
        // captured rook on its corner
        if (undo.Captured == Piece.WhiteRook)
        {
            if (m.To88 == Squares.ParseAlgebraicTo0x88("a1")) newCR &= ~CastlingRightsFlags.WhiteQueen;
            if (m.To88 == Squares.ParseAlgebraicTo0x88("h1")) newCR &= ~CastlingRightsFlags.WhiteKing;
        }
        if (undo.Captured == Piece.BlackRook)
        {
            if (m.To88 == Squares.ParseAlgebraicTo0x88("a8")) newCR &= ~CastlingRightsFlags.BlackQueen;
            if (m.To88 == Squares.ParseAlgebraicTo0x88("h8")) newCR &= ~CastlingRightsFlags.BlackKing;
        }
        if (newCR != CastlingRights) SetCastlingRights(newCR); // toggles ZKey appropriately

        // --- Handle captures (normal capture only; EP handled later)
        if (m.Kind != MoveKind.EnPassant && undo.Captured != Piece.Empty)
        {
            RemoveFromBitboards(m.To88, undo.Captured);
            mailbox[m.To88] = (sbyte)Piece.Empty;
            XorZPiece(undo.Captured, m.To88);
        }

        // --- Move the mover piece off the from-square
        RemoveFromBitboards(m.From88, m.Mover);
        mailbox[m.From88] = (sbyte)Piece.Empty;
        XorZPiece(m.Mover, m.From88);

        // --- Special: castling rook movement
        if (m.Kind == MoveKind.CastleKing || m.Kind == MoveKind.CastleQueen)
        {
            bool white = PieceUtil.IsWhite(m.Mover);
            // Define king/rook target squares
            int kFrom = m.From88;
            int kTo = m.To88;
            int rFrom, rTo;

            if (white)
            {
                if (m.Kind == MoveKind.CastleKing)
                {
                    rFrom = Squares.ParseAlgebraicTo0x88("h1");
                    rTo = Squares.ParseAlgebraicTo0x88("f1");
                }
                else
                {
                    rFrom = Squares.ParseAlgebraicTo0x88("a1");
                    rTo = Squares.ParseAlgebraicTo0x88("d1");
                }
            }
            else
            {
                if (m.Kind == MoveKind.CastleKing)
                {
                    rFrom = Squares.ParseAlgebraicTo0x88("h8");
                    rTo = Squares.ParseAlgebraicTo0x88("f8");
                }
                else
                {
                    rFrom = Squares.ParseAlgebraicTo0x88("a8");
                    rTo = Squares.ParseAlgebraicTo0x88("d8");
                }
            }

            // Move king to kTo
            mailbox[kTo] = (sbyte)m.Mover;
            AddToBitboards(kTo, m.Mover);
            XorZPiece(m.Mover, kTo);

            // Move rook rFrom -> rTo
            var rook = white ? Piece.WhiteRook : Piece.BlackRook;
            RemoveFromBitboards(rFrom, rook);
            mailbox[rFrom] = (sbyte)Piece.Empty;
            XorZPiece(rook, rFrom);

            mailbox[rTo] = (sbyte)rook;
            AddToBitboards(rTo, rook);
            XorZPiece(rook, rTo);

            undo = undo with { CastleRookFrom88 = rFrom, CastleRookTo88 = rTo };
        }
        else
        {
            // --- EP capture removal (captured pawn sits behind the to-square)
            if (m.Kind == MoveKind.EnPassant)
            {
                bool white = PieceUtil.IsWhite(m.Mover);
                int capSq = white ? (m.To88 - 16) : (m.To88 + 16);
                var capPiece = white ? Piece.BlackPawn : Piece.WhitePawn;

                RemoveFromBitboards(capSq, capPiece);
                mailbox[capSq] = (sbyte)Piece.Empty;
                XorZPiece(capPiece, capSq);

                undo = undo with { EnPassantCapturedSq88 = capSq };
            }

            // --- Place the moved piece (promotion if any)
            var placed = (m.Promotion != Piece.Empty) ? m.Promotion : m.Mover;
            mailbox[m.To88] = (sbyte)placed;
            AddToBitboards(m.To88, placed);
            XorZPiece(placed, m.To88);
        }

        // --- New EP target if a pawn moved two squares
        if (isPawnMove && (m.To88 - m.From88 == +32 || m.To88 - m.From88 == -32))
        {
            int file = m.From88 & 0x0F;
            EnPassantFile = file;
            ZKey ^= Tables.Zobrist.EnPassant[file];
        }

        // --- Side to move
        WhiteToMove = !WhiteToMove;
        ZKey ^= Tables.Zobrist.SideToMove;

        // Repetition bookkeeping
        _hashStack.Push(ZKey);
        if (_repCounts.TryGetValue(ZKey, out var c)) _repCounts[ZKey] = c + 1;
        else _repCounts[ZKey] = 1;

        return undo;
    }

    public void UnmakeMove(in Move m, in Undo u)
    {
        // Pop repetition entry
        if (_hashStack.Count > 0)
        {
            var keyAfterMove = _hashStack.Pop();
            if (_repCounts.TryGetValue(keyAfterMove, out var c))
            {
                if (c <= 1) _repCounts.Remove(keyAfterMove);
                else _repCounts[keyAfterMove] = c - 1;
            }
        }

        // Restore side and ZKey (this also covers EP and castling zobrist, so do it early)
        WhiteToMove = u.WhiteToMovePrev;
        ZKey = u.ZKeyPrev;

        // Clear destination / rook squares as needed and put things back
        if (m.Kind == MoveKind.CastleKing || m.Kind == MoveKind.CastleQueen)
        {
            // Undo rook move
            if (u.CastleRookFrom88 is int rFrom && u.CastleRookTo88 is int rTo)
            {
                var rook = PieceUtil.IsWhite(m.Mover) ? Piece.WhiteRook : Piece.BlackRook;

                RemoveFromBitboards(rTo, rook);
                mailbox[rTo] = (sbyte)Piece.Empty;

                mailbox[rFrom] = (sbyte)rook;
                AddToBitboards(rFrom, rook);
            }

            // Move king back
            RemoveFromBitboards(m.To88, m.Mover);
            mailbox[m.To88] = (sbyte)Piece.Empty;

            mailbox[m.From88] = (sbyte)m.Mover;
            AddToBitboards(m.From88, m.Mover);
        }
        else
        {
            // Remove piece from To (promotion piece may be there)
            var placed = (m.Promotion != Piece.Empty) ? m.Promotion : m.Mover;
            RemoveFromBitboards(m.To88, placed);
            mailbox[m.To88] = (sbyte)Piece.Empty;

            // Put mover back
            mailbox[m.From88] = (sbyte)m.Mover;
            AddToBitboards(m.From88, m.Mover);

            // Restore captured piece (normal capture at To, EP captured behind To)
            if (m.Kind == MoveKind.EnPassant)
            {
                if (u.EnPassantCapturedSq88 is int capSq)
                {
                    var capPiece = PieceUtil.IsWhite(m.Mover) ? Piece.BlackPawn : Piece.WhitePawn;
                    mailbox[capSq] = (sbyte)capPiece;
                    AddToBitboards(capSq, capPiece);
                }
            }
            else if (u.Captured != Piece.Empty)
            {
                mailbox[m.To88] = (sbyte)u.Captured;
                AddToBitboards(m.To88, u.Captured);
            }
        }

        EnPassantFile = u.EnPassantFilePrev;
        CastlingRights = u.CastlingPrev;
        HalfmoveClock = u.HalfmovePrev;
    }

    /// <summary>
    /// Generates all legal moves for the side to move.
    /// </summary>
    /// <returns>An enumerable collection of legal moves.</returns>
    public IEnumerable<Move> GenerateLegal()
    {
        var pseudo = new List<Move>(64);
        MoveGeneration.GeneratePseudoLegal(this, pseudo, WhiteToMove);

        foreach (var mv in pseudo)
        {
            var u = MakeMove(mv);
            // after MakeMove, side to move flipped; the side that just moved is !WhiteToMove
            bool ownKingInCheck = InCheck(!WhiteToMove);
            UnmakeMove(mv, u);
            if (!ownKingInCheck)
                yield return mv;
        }
    }

    /// <summary>
    /// Generates all pseudo-legal moves for the side to move.
    /// </summary>
    /// <returns>An enumerable collection of pseudo-legal moves.</returns>
    public IEnumerable<Move> GeneratePseudoLegal()
    {
        var moves = new List<Move>(64);
        MoveGeneration.GeneratePseudoLegal(this, moves, WhiteToMove);
        return moves;
    }

    private void XorZPiece(Piece p, int sq88)
    {
        if (p == Piece.Empty) return;
        int s64 = Squares.ConvertTo0x64Index(sq88);
        ZKey ^= Tables.Zobrist.PieceSquare[PieceUtil.Index(p), s64];
    }

    // --- Attacks (thread-safe; relies only on immutable tables + this board instance) -----

    private static ulong RayAttacksFrom(int sq64, ulong occ, ReadOnlySpan<int> directions)
    {
        ulong attacks = 0;
        int s88 = Squares.ConvertTo0x88Index(sq64);
        foreach (int d in directions)
        {
            int t = s88;
            while (true)
            {
                t += d;
                if (Squares.IsOffboard(t)) break;
                int t64 = Squares.ConvertTo0x64Index(t);
                attacks |= 1UL << t64;
                if (((occ >> t64) & 1UL) != 0) break;
            }
        }
        return attacks;
    }

    private ulong RookAttacks(int sq64) => RayAttacksFrom(sq64, OccAll, RookDirections);
    private ulong BishopAttacks(int sq64) => RayAttacksFrom(sq64, OccAll, BishopDirections);

    public bool IsSquareAttacked(int targetSq88, bool byWhite)
    {
        if (Squares.IsOffboard(targetSq88)) return false;
        int t64 = Squares.ConvertTo0x64Index(targetSq88);

        var T = Tables; // local alias

        // Knights
        ulong knights = byWhite ? pieceBB[PieceUtil.Index(Piece.WhiteKnight)] : pieceBB[PieceUtil.Index(Piece.BlackKnight)];
        if ((T.KnightAttackTable[t64] & knights) != 0) return true;

        // Kings
        ulong kings = byWhite ? pieceBB[PieceUtil.Index(Piece.WhiteKing)] : pieceBB[PieceUtil.Index(Piece.BlackKing)];
        if ((T.KingAttackTable[t64] & kings) != 0) return true;

        // Pawns (reverse attack)
        if (byWhite)
        {
            if ((T.BlackPawnAttackFrom[t64] & pieceBB[PieceUtil.Index(Piece.WhitePawn)]) != 0) return true;
        }
        else
        {
            if ((T.WhitePawnAttackFrom[t64] & pieceBB[PieceUtil.Index(Piece.BlackPawn)]) != 0) return true;
        }

        // Sliders
        ulong bishopsQueens = byWhite
            ? (pieceBB[PieceUtil.Index(Piece.WhiteBishop)] | pieceBB[PieceUtil.Index(Piece.WhiteQueen)])
            : (pieceBB[PieceUtil.Index(Piece.BlackBishop)] | pieceBB[PieceUtil.Index(Piece.BlackQueen)]);
        if ((BishopAttacks(t64) & bishopsQueens) != 0) return true;

        ulong rooksQueens = byWhite
            ? (pieceBB[PieceUtil.Index(Piece.WhiteRook)] | pieceBB[PieceUtil.Index(Piece.WhiteQueen)])
            : (pieceBB[PieceUtil.Index(Piece.BlackRook)] | pieceBB[PieceUtil.Index(Piece.BlackQueen)]);
        if ((RookAttacks(t64) & rooksQueens) != 0) return true;

        return false;
    }
    // Inside your Board class

    public void Clear()
    {
        // Mailbox + bitboards
        Array.Fill(mailbox, (sbyte)Piece.Empty);
        Array.Fill(pieceBB, 0UL);

        // Occupancy
        OccWhite = OccBlack = OccAll = 0UL;

        // Scalar state
        WhiteToMove = true;
        EnPassantFile = null;
        HalfmoveClock = 0;
        FullmoveNumber = 1;
        CastlingRights = CastlingRightsFlags.None;

        // Repetition tracking
        _repCounts.Clear();
        _hashStack.Clear();

        // Zobrist from empty state
        UpdateZobristFull();
    }

    /// <summary>
    /// Sets the board to the standard starting position.
    /// </summary>
    public void SetStartPosition()
    {
        Clear(); // ensures repetition tables, occ, etc. are reset

        static int Sq(int f, int r) => (r << 4) | f;

        // White back rank
        Place(Sq(0, 0), Piece.WhiteRook); Place(Sq(1, 0), Piece.WhiteKnight);
        Place(Sq(2, 0), Piece.WhiteBishop); Place(Sq(3, 0), Piece.WhiteQueen);
        Place(Sq(4, 0), Piece.WhiteKing); Place(Sq(5, 0), Piece.WhiteBishop);
        Place(Sq(6, 0), Piece.WhiteKnight); Place(Sq(7, 0), Piece.WhiteRook);

        // Pawns
        for (int f = 0; f < 8; f++) Place(Sq(f, 1), Piece.WhitePawn);
        for (int f = 0; f < 8; f++) Place(Sq(f, 6), Piece.BlackPawn);

        // Black back rank
        Place(Sq(0, 7), Piece.BlackRook); Place(Sq(1, 7), Piece.BlackKnight);
        Place(Sq(2, 7), Piece.BlackBishop); Place(Sq(3, 7), Piece.BlackQueen);
        Place(Sq(4, 7), Piece.BlackKing); Place(Sq(5, 7), Piece.BlackBishop);
        Place(Sq(6, 7), Piece.BlackKnight); Place(Sq(7, 7), Piece.BlackRook);

        // Side & rights
        WhiteToMove = true;
        EnPassantFile = null;
        HalfmoveClock = 0;
        FullmoveNumber = 1;
        CastlingRights = CastlingRightsFlags.WhiteKing | CastlingRightsFlags.WhiteQueen |
                         CastlingRightsFlags.BlackKing | CastlingRightsFlags.BlackQueen;

        UpdateZobristFull();
    }

    private void UpdateZobristFull()
    {
        ulong key = 0;
        for (int sq88 = 0; sq88 < 128; sq88++)
        {
            if (Squares.IsOffboard(sq88)) continue;
            var p = (Piece)mailbox[sq88];
            if (p == Piece.Empty) continue;
            int s64 = Squares.ConvertTo0x64Index(sq88);
            key ^= Tables.Zobrist.PieceSquare[PieceUtil.Index(p), s64];
        }
        if (!WhiteToMove) key ^= Tables.Zobrist.SideToMove;
        if (EnPassantFile is int epf) key ^= Tables.Zobrist.EnPassant[epf];
        key ^= Tables.Zobrist.Castling[(int)CastlingRights & 0xF];
        ZKey = key;
    }

    public void SetSideToMove(bool whiteToMove)
    {
        if (WhiteToMove == whiteToMove) return;
        // Zobrist side-to-move is a single toggle bit
        ZKey ^= Tables.Zobrist.SideToMove;
        WhiteToMove = whiteToMove;
    }

    public void SetEnPassantFile(int? file)
    {
        // Zobrist typically encodes the *file* (a..h -> 0..7) or "no EP" as no key
        if (EnPassantFile == file) return;

        if (EnPassantFile is int oldFile)
            ZKey ^= Tables.Zobrist.EnPassant[oldFile];

        EnPassantFile = file;

        if (file is int newFile)
            ZKey ^= Tables.Zobrist.EnPassant[newFile];
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
    public ulong GetPieceBitboard(Piece piece)
    {
        if (piece == Piece.Empty)
            throw new ArgumentException("Piece cannot be empty.", nameof(piece));
        return pieceBB[PieceUtil.Index(piece)];
    }

    // Occupancy helpers (already exposed as properties, but symmetric with the API above)
    public ulong GetOccupancy(bool white) => white ? OccWhite : OccBlack;
    public ulong GetAllOccupancy() => OccAll;

    public bool InCheck(bool white)
    {
        // Find king square for the requested side
        ulong kingBB = GetPieceBitboard(white ? Piece.WhiteKing : Piece.BlackKing);
        if (kingBB == 0) return false; // ill-formed position
        int kingSq64 = BitOperations.TrailingZeroCount(kingBB);
        int kingSq88 = Squares.ConvertTo0x88Index(kingSq64);
        return IsSquareAttacked(kingSq88, byWhite: !white);
    }
}