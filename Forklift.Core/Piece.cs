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
        public static readonly Piece Pawn = new(1);
        public static readonly Piece Knight = new(2);
        public static readonly Piece Bishop = new(3);
        public static readonly Piece Rook = new(4);
        public static readonly Piece Queen = new(5);
        public static readonly Piece King = new(6);

        // Color aliases (Option B: White == 0 == Empty)
        public static readonly Piece White = new(0);
        public static readonly Piece Black = new(8);

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

        // ===== Derived properties =====

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
        public Piece Type
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new((sbyte)(value & 0b0111));
        }

        // Option B: return Empty for Empty; otherwise 0 (white) or 8 (black)
        public Piece Color
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value == 0 ? Empty : new((sbyte)(value & 0b1000));
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
                var t when t == Queen => 'q',
                var t when t == Rook => 'r',
                var t when t == Bishop => 'b',
                var t when t == Knight => 'n',
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
            if (t == Pawn) return p.IsBlack ? 'p' : 'P';
            if (t == Knight) return p.IsBlack ? 'n' : 'N';
            if (t == Bishop) return p.IsBlack ? 'b' : 'B';
            if (t == Rook) return p.IsBlack ? 'r' : 'R';
            if (t == Queen) return p.IsBlack ? 'q' : 'Q';
            if (t == King) return p.IsBlack ? 'k' : 'K';
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
    }
}
