using System;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public readonly struct Piece : IEquatable<Piece>
    {
        private readonly sbyte value;

        private Piece(sbyte value) { this.value = value; }

        // Public canonical instances
        public static readonly Piece Empty = new(0);

        // Uncolored (type) atoms 1..6
        private static readonly Piece Pawn = new(1);
        private static readonly Piece Knight = new(2);
        private static readonly Piece Bishop = new(3);
        private static readonly Piece Rook = new(4);
        private static readonly Piece Queen = new(5);
        private static readonly Piece King = new(6);

        // Color aliases (Option B: White == 0 == Empty)
        private static readonly Piece White = new(0);
        private static readonly Piece Black = new(8);

        // Precomposed white pieces (1..6)
        public static readonly Piece WhitePawn = new(1);
        public static readonly Piece WhiteKnight = new(2);
        public static readonly Piece WhiteBishop = new(3);
        public static readonly Piece WhiteRook = new(4);
        public static readonly Piece WhiteQueen = new(5);
        public static readonly Piece WhiteKing = new(6);

        // Precomposed black pieces (8|type = 9..14)
        public static readonly Piece BlackPawn = new(9);
        public static readonly Piece BlackKnight = new(10);
        public static readonly Piece BlackBishop = new(11);
        public static readonly Piece BlackRook = new(12);
        public static readonly Piece BlackQueen = new(13);
        public static readonly Piece BlackKing = new(14);

        public static IEnumerable<Piece> AllPieces
        {
            get
            {
                yield return WhitePawn;
                yield return WhiteKnight;
                yield return WhiteBishop;
                yield return WhiteRook;
                yield return WhiteQueen;
                yield return WhiteKing;
                yield return BlackPawn;
                yield return BlackKnight;
                yield return BlackBishop;
                yield return BlackRook;
                yield return BlackQueen;
                yield return BlackKing;
            }
        }

        public enum PieceType : sbyte
        {
            Pawn = 0,
            Knight = 1,
            Bishop = 2,
            Rook = 3,
            Queen = 4,
            King = 5
        }

        public bool IsWhite
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value != 0 && (value & 0b1000) == 0;
        }

        public bool IsBlack
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (value & 0b1000) != 0 && (value & 0b0111) != 0;
        }

        // Returns only the 3-bit type (0..6) as a Piece value (0 => Empty)
        public PieceType Type
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (PieceType)((value & 0b0111) - 1);
        }

        public Color Color
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value == 0 ? throw new InvalidOperationException("Empty has no color.") : ((value & 0b1000) == 0b1000 ? Color.Black : Color.White);
        }

        // 0..5 for white, 6..11 for black; throws for Empty/invalid
        public sbyte PieceIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (value == 0) throw new InvalidOperationException("Empty has no bitboard index.");
                sbyte t = (sbyte)(value & 0b0111);
                if (t < 1 || t > 6) throw new InvalidOperationException($"Invalid piece value: {value}");
                return (sbyte)((t - 1) + (((value & 0b1000) != 0) ? 6 : 0));
            }
        }

        public sbyte TypeIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (value == 0) throw new InvalidOperationException("Empty has no type index.");
                sbyte t = (sbyte)(value & 0b0111);
                if (t < 1 || t > 6) throw new InvalidOperationException($"Invalid piece value: {value}");
                return (sbyte)(t - 1);
            }
        }

        public char PromotionChar
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Type switch
            {
                var t when t == PieceType.Queen => 'q',
                var t when t == PieceType.Rook => 'r',
                var t when t == PieceType.Bishop => 'b',
                var t when t == PieceType.Knight => 'n',
                _ => throw new InvalidOperationException("Invalid promotion piece")
            };
        }

        // Use Forklift.Types.Core.Color (White = 0, Black = 1)
        public static Piece FromPromotionChar(char c, Color color)
        {
            var type = c switch
            {
                'q' => Queen,
                'r' => Rook,
                'b' => Bishop,
                'n' => Knight,
                _ => throw new ArgumentException("Invalid promotion character", nameof(c))
            };
            sbyte mask = color == Core.Color.Black ? (sbyte)0b1000 : (sbyte)0;
            return FromRaw((sbyte)(type.value | mask));
        }

        public static Piece FromFENChar(char c) => c switch
        {
            'P' => WhitePawn,
            'N' => WhiteKnight,
            'B' => WhiteBishop,
            'R' => WhiteRook,
            'Q' => WhiteQueen,
            'K' => WhiteKing,
            'p' => BlackPawn,
            'n' => BlackKnight,
            'b' => BlackBishop,
            'r' => BlackRook,
            'q' => BlackQueen,
            'k' => BlackKing,
            _ => Empty
        };

        // Per choice (4): throw for invalid/empty
        public static char ToFENChar(Piece p)
        {
            var t = p.Type;
            if (t == PieceType.Pawn) return p.IsBlack ? 'p' : 'P';
            if (t == PieceType.Knight) return p.IsBlack ? 'n' : 'N';
            if (t == PieceType.Bishop) return p.IsBlack ? 'b' : 'B';
            if (t == PieceType.Rook) return p.IsBlack ? 'r' : 'R';
            if (t == PieceType.Queen) return p.IsBlack ? 'q' : 'Q';
            if (t == PieceType.King) return p.IsBlack ? 'k' : 'K';
            throw new ArgumentException("Invalid piece for FEN conversion");
        }

        // ===== Validated construction =====

        public static Piece FromRaw(sbyte v)
        {
            if (v == 0) return Empty; // empty ok
            sbyte t = (sbyte)(v & 0b0111);
            if (t < 1 || t > 6)
                throw new ArgumentOutOfRangeException(nameof(v), $"Invalid piece type bits in value: {v}");
            sbyte c = (sbyte)(v & 0b1000);
            if (c != 0 && c != 0b1000)
                throw new ArgumentOutOfRangeException(nameof(v), $"Invalid color bit in value: {v}");
            return new Piece(v);
        }

        // Equality / hashing / casts

        public override bool Equals(object? obj) => obj is Piece p && value == p.value;
        public bool Equals(Piece other) => value == other.value;
        public override int GetHashCode() => value;

        public static implicit operator sbyte(Piece p) => p.value;
        public static explicit operator Piece(sbyte v) => FromRaw(v);

        public static bool operator ==(Piece a, Piece b) => a.value == b.value;
        public static bool operator !=(Piece a, Piece b) => a.value != b.value;

        public override string ToString()
        {
            if (this == Empty) return "Empty";
            var unicodeSymbol = this switch
            {
                var p when p == WhitePawn => "♙",
                var p when p == WhiteKnight => "♘",
                var p when p == WhiteBishop => "♗",
                var p when p == WhiteRook => "♖",
                var p when p == WhiteQueen => "♕",
                var p when p == WhiteKing => "♔",
                var p when p == BlackPawn => "♟",
                var p when p == BlackKnight => "♞",
                var p when p == BlackBishop => "♝",
                var p when p == BlackRook => "♜",
                var p when p == BlackQueen => "♛",
                var p when p == BlackKing => "♚",
                _ => "?"
            };

            return $"{unicodeSymbol} ({Color} {Type})";
        }
    }
}
