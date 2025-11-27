using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.HighPerformance;

namespace Forklift.Core
{
    /// <summary>
    /// Quadratic principal variation table.
    /// PV[ply, 0..pvLength[ply)-1] is the PV starting at that ply.
    /// Root PV is PV[0, 0..pvLength[0)-1].
    /// </summary>
    public sealed class PrincipalVariationTable
    {
        private readonly Board.Move?[,] _table;
        private readonly int[] _pvLengths;
        private readonly int _maxDepth;

        public PrincipalVariationTable(int maxDepth)
        {
            if (maxDepth <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxDepth));

            _maxDepth = maxDepth;
            _table = new Board.Move?[maxDepth, maxDepth];
            _pvLengths = new int[maxDepth];
        }

        public int MaxDepth => _maxDepth;

        /// <summary>
        /// Clears the entire PV table and all lengths.
        /// Call once per new root search (e.g., at the start of FindBestMove).
        /// </summary>
        public void Clear()
        {
            Array.Clear(_table, 0, _table.Length);
            Array.Clear(_pvLengths, 0, _pvLengths.Length);
        }

        /// <summary>
        /// Resets the PV length at a given ply.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ResetAtPly(int ply)
        {
            Debug.Assert(ply < _maxDepth, "Ply out of range in PV reset");
            _pvLengths[ply] = 0;
        }

        /// <summary>
        /// Updates the PV at <paramref name="ply"/> after an alpha improvement.
        /// This sets PV[ply] = bestMove + PV[ply+1].
        /// </summary>
        /// <remarks>
        /// Assumes that PV[ply+1] already contains the child's PV and
        /// that _pvLengths[ply+1] is up to date.
        /// </remarks>
        public void UpdateFromChild(int ply, Board.Move bestMove)
        {
            Debug.Assert(ply >= 0 && ply < _maxDepth, "Ply out of range in PV update");

            var table = _table.AsSpan2D();

            // First move of the PV at this ply is the improving move
            table[ply, 0] = bestMove;

            int childPly = ply + 1;
            // Boundary check: If we are at max depth, we can't look at a child
            if (childPly >= _maxDepth)
            {
                _pvLengths[ply] = 1;
                return;
            }

            int childLength = _pvLengths[childPly];

            // Only copy if the child actually had a PV line
            if (childLength > 0)
            {
                // Source: Child Row, starting at 0, length of child PV
                var sourceRow = table.GetRowSpan(childPly).Slice(0, childLength);

                // Dest: Current Row, starting at 1 (after bestMove)
                var destinationRow = table.GetRowSpan(ply).Slice(1, childLength);

                sourceRow.CopyTo(destinationRow);
            }

            _pvLengths[ply] = childLength + 1;
        }

        /// <summary>
        /// Copies the root PV into <paramref name="destination"/>.
        /// Returns the number of moves copied.
        /// </summary>
        public ReadOnlySpan<Board.Move?> GetRootPrincipalVariation()
        {
            int length = _pvLengths[0];
            if (length <= 0)
                return [];

            return _table.AsSpan2D().GetRowSpan(0).Slice(0, length);
        }

        /// <summary>
        /// Returns the first move of the root PV (if any), otherwise null.
        /// </summary>
        public Board.Move? GetRootMove()
        {
            return _pvLengths[0] > 0
                ? _table[0, 0]
                : null;
        }

        /// <summary>
        /// Returns the move at (ply, index) within the PV for that ply,
        /// or null if out of range.
        /// </summary>
        public Board.Move? GetMoveAt(int ply, int index = 0)
        {
            Debug.Assert(ply >= 0 && index >= 0, "Negative ply or index in PV lookup");
            Debug.Assert(ply < _maxDepth, "Ply out of range in PV lookup");

            int len = _pvLengths[ply];

            if (index >= len)
                return null;

            return _table[ply, index];
        }
    }
}
