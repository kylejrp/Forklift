using System.Collections;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace Forklift.Core
{
    public static class Perft
    {
        // ---------------------------
        // Public API
        // ---------------------------

        public static long Count(Board board, int depth, bool parallelRoot = false, int? maxThreads = null)
        {
            if (!parallelRoot || depth <= 1)
                return PerftSerial(board, depth);

            // Root-split: generate once, then parallelize per root move
            Span<Board.Move> buffer = stackalloc Board.Move[Board.MoveBufferMax];
            MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);

            // MATERIALIZE to avoid capturing a Span<T> in the lambda
            var moves = buffer.ToArray();

            long total = 0;

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = (maxThreads is > 0) ? maxThreads.Value : Environment.ProcessorCount
            };

            Parallel.For<long>(
                fromInclusive: 0,
                toExclusive: Math.Max(moves.Length, maxThreads ?? 1),
                localInit: static () => 0L,
                body: (i, _state, local) =>
                {
                    var mv = moves[i];
                    var bc = board.Copy(keepTrackOfHistory: false);
                    var u = bc.MakeMove(mv);

                    // Filter illegals cheaply after making the move
                    if (!bc.InCheck(bc.SideToMove.Flip()))
                    {
                        local += PerftSerial(bc, depth - 1);
                    }

                    bc.UnmakeMove(mv, u);
                    return local;
                },
                localFinally: localSum => System.Threading.Interlocked.Add(ref total, localSum),
                parallelOptions: options
            );

            return total;
        }


        public struct DivideMove
        {
            public byte From88;
            public byte To88;
            public Piece Promotion; // Piece.Empty if no promotion
            public long Nodes;

            public string MoveUci
            {
                get
                {
                    var fromAlg = Squares.ToAlgebraicString((Square0x88)From88);
                    var toAlg = Squares.ToAlgebraicString((Square0x88)To88);
                    string promo = Promotion != Piece.Empty ? char.ToLower(Piece.ToFENChar((Piece)Promotion)).ToString() : string.Empty;
                    return fromAlg + toAlg + promo;
                }
            }
        }

        /// <summary>
        /// Root "divide" breakdown (move -> subtree nodes).
        /// Set parallelRoot=true to parallelize per root move; set sort=true to sort by nodes desc.
        /// </summary>
        public static IReadOnlyList<DivideMove> Divide(Board b, int depth, bool parallelRoot = false, bool sort = false, int? maxThreads = null)
        {
            Span<Board.Move> buffer = stackalloc Board.Move[Board.MoveBufferMax];
            // For divide, use legal move list at the root for clean output
            b.GenerateLegal(ref buffer);
            var moves = buffer.ToArray(); // <-- materialize; avoid capturing Span<T> in lambda

            var results = new DivideMove[moves.Length];

            if (!parallelRoot || depth <= 1)
            {
                for (int i = 0; i < moves.Length; i++)
                {
                    var mv = moves[i];
                    var u = b.MakeMove(mv);
                    long n = PerftSerial(b, depth - 1);
                    b.UnmakeMove(mv, u);

                    results[i] = new DivideMove
                    {
                        From88 = (byte)mv.From88,
                        To88 = (byte)mv.To88,
                        Promotion = mv.Promotion,
                        Nodes = n
                    };
                }
            }
            else
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = (maxThreads is > 0) ? maxThreads.Value : Environment.ProcessorCount
                };

                Parallel.For(0, Math.Max(moves.Length, maxThreads ?? 1), options, i =>
                {
                    var mv = moves[i];
                    var bc = b.Copy(keepTrackOfHistory: false);
                    var u = bc.MakeMove(mv);
                    long n = PerftSerial(bc, depth - 1);
                    bc.UnmakeMove(mv, u);

                    // independent slots; safe to write in parallel
                    results[i] = new DivideMove
                    {
                        From88 = (byte)mv.From88,
                        To88 = (byte)mv.To88,
                        Promotion = mv.Promotion,
                        Nodes = n
                    };
                });
            }

            if (sort)
                Array.Sort(results, (a, b2) => b2.Nodes.CompareTo(a.Nodes));

            return results;
        }


        public struct PerftStatistics
        {
            public long Nodes;
            public long Captures;
            public long EnPassant;
            public long Castles;
            public long Promotions;
            public long Checks;
            public long DiscoveryChecks;
            public long DoubleChecks;
            public long Checkmates;
        }

        /// <summary>
        /// Extended stats. Set parallelRoot=true to parallelize only the root.
        /// </summary>
        public static PerftStatistics Statistics(Board board, int depth, bool parallelRoot = false, int? maxThreads = null)
        {
            if (!parallelRoot || depth <= 1)
            {
                var s = new PerftStatistics();
                StatisticsImplSerial(board, depth, ref s);
                return s;
            }

            // Root-split stats: generate once; combine per-branch using thread-local accumulation
            Span<Board.Move> buffer = stackalloc Board.Move[Board.MoveBufferMax];
            MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);
            var moves = buffer.ToArray(); // <-- materialize

            var total = new PerftStatistics();
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = (maxThreads is > 0) ? maxThreads.Value : Environment.ProcessorCount
            };

            Parallel.For<PerftStatistics>(
                fromInclusive: 0,
                toExclusive: Math.Max(moves.Length, maxThreads ?? 1),
                localInit: static () => default,
                body: (i, _state, local) =>
                {
                    var mv = moves[i];
                    var bc = board.Copy(keepTrackOfHistory: false);
                    var u = bc.MakeMove(mv);

                    // legality filter on the child
                    if (!bc.InCheck(bc.SideToMove.Flip()))
                    {
                        var s = new PerftStatistics();
                        StatisticsImplSerial(bc, depth - 1, ref s);

                        // accumulate into thread-local
                        local.Nodes += s.Nodes;
                        local.Captures += s.Captures;
                        local.EnPassant += s.EnPassant;
                        local.Castles += s.Castles;
                        local.Promotions += s.Promotions;
                        local.Checks += s.Checks;
                        local.DiscoveryChecks += s.DiscoveryChecks;
                        local.DoubleChecks += s.DoubleChecks;
                        local.Checkmates += s.Checkmates;
                    }

                    bc.UnmakeMove(mv, u);
                    return local;
                },
                localFinally: local =>
                {
                    // one set of atomics per shard
                    System.Threading.Interlocked.Add(ref total.Nodes, local.Nodes);
                    System.Threading.Interlocked.Add(ref total.Captures, local.Captures);
                    System.Threading.Interlocked.Add(ref total.EnPassant, local.EnPassant);
                    System.Threading.Interlocked.Add(ref total.Castles, local.Castles);
                    System.Threading.Interlocked.Add(ref total.Promotions, local.Promotions);
                    System.Threading.Interlocked.Add(ref total.Checks, local.Checks);
                    System.Threading.Interlocked.Add(ref total.DiscoveryChecks, local.DiscoveryChecks);
                    System.Threading.Interlocked.Add(ref total.DoubleChecks, local.DoubleChecks);
                    System.Threading.Interlocked.Add(ref total.Checkmates, local.Checkmates);
                },
                parallelOptions: options);

            return total;
        }


        // ---------------------------
        // Serial workers
        // ---------------------------

        private static long PerftSerial(Board board, int depth)
        {
            if (depth == 0) return 1;

            Span<Board.Move> buffer = stackalloc Board.Move[Board.MoveBufferMax];
            MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);

            long nodes = 0;
            foreach (var mv in buffer)
            {
                var u = board.MakeMove(mv);
                bool legal = !board.InCheck(board.SideToMove.Flip());
                if (legal)
                    nodes += PerftSerial(board, depth - 1);
                board.UnmakeMove(mv, u);
            }

            return nodes;
        }

        private static void StatisticsImplSerial(Board board, int depth, ref PerftStatistics stats)
        {
            if (depth == 0)
            {
                stats.Nodes++;
                if (board.IsCheckmate())
                    stats.Checkmates++;
                return;
            }

            Span<Board.Move> buffer = stackalloc Board.Move[Board.MoveBufferMax];
            MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);

            foreach (var mv in buffer)
            {
                var u = board.MakeMove(mv);
                bool legal = !board.InCheck(board.SideToMove.Flip());
                if (legal)
                {
                    if (depth == 1)
                    {
                        if (mv.IsCapture) stats.Captures++;
                        if (mv.IsEnPassant) stats.EnPassant++;
                        if (mv.IsCastle) stats.Castles++;
                        if (mv.Promotion != Piece.Empty) stats.Promotions++;

                        bool isCheck = board.InCheck(board.SideToMove);
                        if (isCheck) stats.Checks++;

                        bool isDbl = false;
                        if (isCheck)
                            isDbl = IsDoubleCheck(board);
                        if (isCheck && !isDbl && IsDiscoveryCheck(board, mv))
                            stats.DiscoveryChecks++;
                        if (isDbl)
                            stats.DoubleChecks++;
                    }

                    StatisticsImplSerial(board, depth - 1, ref stats);
                }

                board.UnmakeMove(mv, u);
            }
        }

        internal static bool IsDoubleCheck(Board board)
        {
            var checkedSide = board.SideToMove;
            if (!board.InCheck(checkedSide)) return false;

            var kingSq64 = board.FindKingSq64(checkedSide);
            var attackerSide = checkedSide.Flip();

            ulong attackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Knight | Piece.PieceType.Pawn | Piece.PieceType.King | Piece.PieceType.Bishop | Piece.PieceType.Rook | Piece.PieceType.Queen);
            return BitOperations.PopCount(attackers) >= 2;
        }

        private static readonly int[] DIRS_ALL = { +1, -1, +16, -16, +15, +17, -15, -17 };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsRookDir(int d) => d == +1 || d == -1 || d == +16 || d == -16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBishopDir(int d) => d == +15 || d == +17 || d == -15 || d == -17;

        public static bool IsDiscoveryCheck(Board board, in Board.Move mv)
        {
            var us = mv.Mover.IsWhite ? Color.White : Color.Black;
            var them = us.Flip();

            // board is POST-move: king square is the opponent's king in the position after mv.
            Square0x64 king64 = board.FindKingSq64(them);
            Square0x88 king = (Square0x88)king64;

            Square0x88 from = mv.From88;
            Square0x88 to = mv.To88;

            // EP-captured pawn square in 0x88, if any (PRE-move square)
            Square0x88? epRemoved = null;
            if (mv.IsEnPassant)
                epRemoved = mv.Mover.IsWhite ? (Square0x88)(to - 16) : (Square0x88)(to + 16);

            bool OccupiedAfter(Square0x88 sq) => board.At(sq) != Piece.Empty;

            // Reconstruct "was this square occupied BEFORE mv?" from the post-move board + mv.
            bool OccupiedBefore(Square0x88 sq, Board.Move mv)
            {
                // The moving piece started on 'from'
                if (sq == from)
                    return true;

                // EP-captured pawn existed on its square before the move
                if (epRemoved.HasValue && sq == epRemoved.Value)
                    return true;

                if (sq == to)
                {
                    // Before the move:
                    //  - Normal capture: enemy piece on 'to'  -> occupied
                    //  - En passant:  'to' was empty        -> not occupied
                    //  - Quiet move:  'to' was empty        -> not occupied
                    return mv.IsCapture && !mv.IsEnPassant;
                }

                // All other squares keep their occupancy
                return OccupiedAfter(sq);
            }

            bool PieceMatchesDir(Piece p, int dir)
            {
                if (p == Piece.Empty) return false;
                bool pWhite = p.IsWhite;
                if (pWhite != us.IsWhite()) return false;

                if (IsRookDir(dir))
                    return p == Piece.WhiteRook || p == Piece.BlackRook ||
                           p == Piece.WhiteQueen || p == Piece.BlackQueen;

                if (IsBishopDir(dir))
                    return p == Piece.WhiteBishop || p == Piece.BlackBishop ||
                           p == Piece.WhiteQueen || p == Piece.BlackQueen;

                return false;
            }

            foreach (int dir in DIRS_ALL)
            {
                // 1. After-move: find first piece along this direction from the king.
                var cur = (UnsafeSquare0x88)king;
                Square0x88? sliderSq = null;

                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur))
                        break;

                    var sq = (Square0x88)cur;
                    if (!OccupiedAfter(sq))
                        continue;

                    sliderSq = sq;
                    break;
                }

                if (sliderSq is null)
                    continue;

                var sliderPc = board.At(sliderSq.Value);
                if (!PieceMatchesDir(sliderPc, dir))
                    continue;

                // If the moved piece itself is the slider, that's a direct check, not a discovered one.
                if (sliderSq.Value == to)
                    continue;

                // 2. Before-move: what was the first occupied square on this ray?
                cur = (UnsafeSquare0x88)king;
                Square0x88? firstBefore = null;

                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur))
                        break;

                    var sq = (Square0x88)cur;
                    if (!OccupiedBefore(sq, mv))
                        continue;

                    firstBefore = sq;
                    break;
                }

                if (firstBefore is null)
                    continue;

                bool wasBlockedByMoverOrEp =
                    firstBefore.Value == from ||
                    (epRemoved.HasValue && firstBefore.Value == epRemoved.Value);

                if (!wasBlockedByMoverOrEp)
                    continue;

                // We have:
                //  - After the move: first piece along ray is our slider (giving check),
                //  - Before the move: first piece along ray was the mover or EP pawn.
                // => Discovered check.
                return true;
            }

            return false;
        }


        // Returns true and the 0x88 step (+/-1, +/-16, +/-15, +/-17) if squares are aligned; false otherwise.
        private static bool TryStepBetween(int from88, int to88, out int step)
        {
            step = 0;
            int df = (to88 & 0x0F) - (from88 & 0x0F);
            int dr = (to88 >> 4) - (from88 >> 4);

            if (dr == 0) { step = df > 0 ? +1 : -1; return true; }
            if (df == 0) { step = dr > 0 ? +16 : -16; return true; }
            if (df == dr) { step = dr > 0 ? +17 : -17; return true; }
            if (df == -dr) { step = dr > 0 ? +15 : -15; return true; }

            return false;
        }
    }
}
