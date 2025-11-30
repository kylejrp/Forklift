using System;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public sealed class HistoryTable
    {
        private const int MaxHistory = 16_384;

        private static readonly int ColorLength = Enum.GetValues(typeof(Color)).Length;
        private static readonly int PieceLength = Enum.GetValues(typeof(Piece.PieceType)).Length;
        private static readonly int SquareLength = Square0x64.MAX_VALUE + 1;

        private static readonly int ColorStride = PieceLength * SquareLength;
        private static readonly int PieceStride = SquareLength;

        private readonly int[] _table = new int[ColorLength * PieceLength * SquareLength];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Index(Color color, Piece.PieceType piece, int to64Index)
            => ((int)color * ColorStride) + ((int)piece * PieceStride) + to64Index;

        public void Clear()
        {
            Array.Clear(_table, 0, _table.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Get(Board.Move move)
        {
            return _table[Index(move.Mover.Color, move.Mover.Type, move.To64Index)];
        }

        /// <summary>
        /// Gravity-style update: new = old + bonus - old * |bonus| / MaxHistory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(Board.Move move, int bonus)
        {
            Update(move.Mover.Color, move.Mover.Type, move.To64Index, bonus);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Update(Color color, Piece.PieceType piece, int to64Index, int bonus)
        {
            int idx = Index(color, piece, to64Index);

            int clampedBonus = Math.Clamp(bonus, -MaxHistory, MaxHistory);

            int oldValue = _table[idx];
            int newValue = oldValue + clampedBonus - oldValue * Math.Abs(clampedBonus) / MaxHistory;

            _table[idx] = newValue;
        }
    }
}
