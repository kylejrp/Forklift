using System;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public sealed class PrincipalVariationTable
    {
        private readonly Board.Move?[] _moves;
        private readonly int[] _pvLengths;
        private readonly int _maxDepth;
        private readonly int _stride; // The width of one row in the flat array

        public PrincipalVariationTable(int maxDepth)
        {
            if (maxDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));

            _maxDepth = maxDepth;
            _stride = maxDepth;

            // Flattened 2D array: [ply * stride + index]
            _moves = new Board.Move?[maxDepth * maxDepth];
            _pvLengths = new int[maxDepth];
        }

        public int MaxDepth => _maxDepth;

        public void Clear()
        {
            Array.Clear(_moves, 0, _moves.Length);
            Array.Clear(_pvLengths, 0, _pvLengths.Length);
        }

        // Must be called at the entry of Negamax
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitPly(int ply)
        {
            _pvLengths[ply] = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(int ply, Board.Move bestMove)
        {
            int childPly = ply + 1;
            if (childPly >= _maxDepth) return;

            int childLen = _pvLengths[childPly];
            int currentOffset = ply * _stride;
            int childOffset = childPly * _stride;

            // 1. Store the best move for this ply
            _moves[currentOffset] = bestMove;

            // 2. Copy the child's PV line after our move
            if (childLen > 0)
            {
                Array.Copy(_moves, childOffset, _moves, currentOffset + 1, childLen);
            }

            // 3. Update length
            _pvLengths[ply] = childLen + 1;
        }

        public ReadOnlySpan<Board.Move?> GetRootPv()
        {
            int len = _pvLengths[0];
            if (len == 0) return ReadOnlySpan<Board.Move?>.Empty;
            return new ReadOnlySpan<Board.Move?>(_moves, 0, len);
        }
    }
}
