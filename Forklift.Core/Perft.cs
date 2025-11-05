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
            Span<Board.Move> buf = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(buf);
            var span = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);

            // MATERIALIZE to avoid capturing a Span<T> in the lambda
            var moves = span.ToArray();

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
                    var bc = board.Copy();
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
            Span<Board.Move> buf = stackalloc Board.Move[Board.MoveBufferMax];
            // For divide, use legal move list at the root for clean output
            var span = b.GenerateLegal(buf);
            var moves = span.ToArray(); // <-- materialize; avoid capturing Span<T> in lambda

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
                    var bc = b.Copy();
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
            Span<Board.Move> buf = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(buf);
            var span = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);
            var moves = span.ToArray(); // <-- materialize

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
                    var bc = board.Copy();
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

            Span<Board.Move> buf = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(buf);
            var span = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);

            long nodes = 0;
            foreach (var mv in span)
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

            Span<Board.Move> moveBuffer = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(moveBuffer);
            var span = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);

            foreach (var mv in span)
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

        // ---------------------------
        // Existing helpers (kept as-is)
        // ---------------------------

        internal static bool IsDoubleCheck(Board board)
        {
            var checkedSide = board.SideToMove;
            if (!board.InCheck(checkedSide)) return false;

            var kingSq64 = board.FindKingSq64(checkedSide);
            var attackerSide = checkedSide.Flip();

            ulong knightAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Knight);
            if (knightAttackers != 0) return true;

            ulong pawnAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Pawn);
            if (pawnAttackers != 0) return true;

            ulong kingAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.King);
            if (kingAttackers != 0) return true;

            ulong slidingAttackers = board.AttackersToSquare(kingSq64, attackerSide, Piece.PieceType.Bishop | Piece.PieceType.Rook | Piece.PieceType.Queen);
            return BitOperations.PopCount(slidingAttackers) >= 2;
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

            Square0x64 k64 = board.FindKingSq64(them);
            Square0x88 k88 = Squares.ConvertTo0x88Index(k64);

            Square0x88 from88 = mv.From88;
            Square0x88 to88 = mv.To88;

            Square0x88? epRemoved = null;
            if (mv.IsEnPassant)
                epRemoved = mv.Mover.IsWhite ? (Square0x88)(to88 - 16) : (Square0x88)(to88 + 16);

            Span<bool> preOcc = stackalloc bool[128];
            for (int i = 0; i < 128; i++)
                preOcc[i] = board.At((Square0x88)i) != Piece.Empty;

            ulong occAfter = board.GetAllOccupancy();
            int from64 = (int)(Square0x64)Squares.ConvertTo0x64Index(from88);
            int to64 = (int)(Square0x64)Squares.ConvertTo0x64Index(to88);
            occAfter &= ~(1UL << from64);
            occAfter |= (1UL << to64);
            if (epRemoved.HasValue)
            {
                int ep64 = (int)(Square0x64)Squares.ConvertTo0x64Index(epRemoved.Value);
                occAfter &= ~(1UL << ep64);
            }

            bool PieceMatchesDir(Piece p, int dir)
            {
                if (p == Piece.Empty) return false;
                bool pWhite = p.IsWhite;
                if (pWhite != us.IsWhite()) return false;

                if (IsRookDir(dir)) return p == Piece.WhiteRook || p == Piece.BlackRook || p == Piece.WhiteQueen || p == Piece.BlackQueen;
                if (IsBishopDir(dir)) return p == Piece.WhiteBishop || p == Piece.BlackBishop || p == Piece.WhiteQueen || p == Piece.BlackQueen;
                return false;
            }

            static bool OnOpenSegment(Square0x88 k, Square0x88 s, int dir, Square0x88 q)
            {
                var cur = (UnsafeSquare0x88)k;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) return false;
                    var c = (Square0x88)cur;
                    if (c == q) return true;
                    if (c == s) return false;
                }
            }

            foreach (int dir in DIRS_ALL)
            {
                var cur = (UnsafeSquare0x88)k88;
                Square0x88? firstOcc = null;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) break;
                    var s = (Square0x88)cur;
                    int s64 = (int)(Square0x64)Squares.ConvertTo0x64Index(s);
                    if ((occAfter & (1UL << s64)) != 0)
                    {
                        firstOcc = s;
                        break;
                    }
                }
                if (firstOcc is null) continue;

                cur = (UnsafeSquare0x88)k88;
                Square0x88? firstOccBefore = null;
                while (true)
                {
                    cur += dir;
                    if (Squares.IsOffboard(cur)) break;
                    var s = (Square0x88)cur;

                    if (preOcc[(int)s])
                    {
                        firstOccBefore = s;
                        break;
                    }
                }
                if (firstOccBefore is null) continue;

                bool wasBlockedByMoverOrEP =
                    firstOccBefore.Value == from88 ||
                    (epRemoved.HasValue && firstOccBefore.Value == epRemoved.Value);

                if (!wasBlockedByMoverOrEP) continue;

                var sliderSq = firstOcc.Value;
                var sliderPc = board.At(sliderSq);
                if (!PieceMatchesDir(sliderPc, dir)) continue;

                if (OnOpenSegment(k88, sliderSq, dir, to88)) continue;
                if (mv.IsCapture && sliderSq == mv.To88) continue;

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