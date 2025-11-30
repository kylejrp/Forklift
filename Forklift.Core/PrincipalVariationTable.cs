using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public sealed class PrincipalVariationTable
    {
        private readonly Board.Move?[] _moves;
        private readonly int[] _pvLengths;
        private readonly int _maxDepth;
        private readonly int _stride; // Width of one "row" in the flat array

        public PrincipalVariationTable(int maxDepth)
        {
            if (maxDepth <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDepth));

            _maxDepth = maxDepth;
            _stride = maxDepth;

            // Quadratic table: row = ply, column = index in PV from that ply
            // index = ply * stride + col
            _moves = new Board.Move?[maxDepth * maxDepth];
            _pvLengths = new int[maxDepth];
        }

        public int MaxDepth => _maxDepth;

        public void Clear()
        {
            Array.Clear(_moves, 0, _moves.Length);
            Array.Clear(_pvLengths, 0, _pvLengths.Length);
        }

        /// <summary>
        /// Must be called at the entry of Negamax(ply, ...).
        /// Clears any previous PV stored for this ply.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitPly(int ply)
        {
            Debug.Assert(ply >= 0 && ply < _maxDepth);
            _pvLengths[ply] = 0;
        }

        /// <summary>
        /// Called when a new best move is found at this ply.
        /// Builds:
        ///   PV[ply] = bestMove + PV[ply + 1]
        /// using the child ply's current PV.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(int ply, Board.Move bestMove)
        {
            Debug.Assert(ply >= 0 && ply < _maxDepth);

            int rowStart = ply * _stride;
            _moves[rowStart] = bestMove;

            int childPly = ply + 1;
            int childLen = (childPly < _maxDepth) ? _pvLengths[childPly] : 0;

            if (childLen > 0)
            {
                int childRowStart = childPly * _stride;

                // Copy child's PV: PV[childPly][0..childLen) -> PV[ply][1..1+childLen)
                Array.Copy(
                    sourceArray: _moves,
                    sourceIndex: childRowStart,
                    destinationArray: _moves,
                    destinationIndex: rowStart + 1,
                    length: childLen);
            }

            _pvLengths[ply] = 1 + childLen;
        }

        /// <summary>
        /// Returns the root PV (ply 0).
        /// </summary>
        public Board.Move?[] GetRootPrincipalVariation()
        {
            int len = _pvLengths[0];
            if (len == 0)
                return Array.Empty<Board.Move?>();
            var result = new Board.Move?[len];
            Array.Copy(_moves, 0, result, 0, len);
            return result;
        }
    }
}
