namespace Forklift.Core
{
    public static class Search
    {
        public readonly record struct SearchSummary(Board.Move? BestMove, int BestScore, int CompletedDepth);

        private readonly record struct SearchNodeResult(Board.Move? BestMove, int BestScore, bool IsComplete);

        private readonly record struct QuiescenceResult(int BestScore, bool IsComplete);

        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue; // No overflow risk when negating

        private const int MateScore = TranspositionTable.MateValue;

        private static readonly TranspositionTable _transpositionTable = new();

        private const int MaxPly = 128;
        private static readonly int PieceTypeCount = Enum.GetValues(typeof(Piece.PieceType)).Length;
        private static readonly Board.Move?[] _killerMovesPrimary = new Board.Move?[MaxPly];
        private static readonly Board.Move?[] _killerMovesSecondary = new Board.Move?[MaxPly];
        private static readonly Dictionary<Piece.PieceType, Dictionary<Square0x88, int>> _historyScores = new Dictionary<Piece.PieceType, Dictionary<Square0x88, int>>();
        private static readonly int[] _pieceOrderingValues = {
            100, // Pawn
            320, // Knight
            330, // Bishop
            500, // Rook
            900, // Queen
            2000 // King
        };

        public static void ClearTranspositionTable() => _transpositionTable.Clear();

        public static void ClearHeuristics()
        {
            Array.Clear(_killerMovesPrimary, 0, _killerMovesPrimary.Length);
            Array.Clear(_killerMovesSecondary, 0, _killerMovesSecondary.Length);
            _historyScores.Clear();
        }

        // Negamax search, returns best move and score
        public static SearchSummary FindBestMove(Board board, int maxDepth, CancellationToken cancellationToken = default)
        {
            ClearHeuristics();

            Board.Move? finalBestMove = null;
            int finalBestScore = MinimumScore;
            int completedDepth = 0;

            Board.Move? pvMove = null;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var result = Negamax(
                    board: board,
                    depth: depth,
                    alpha: MinimumScore,
                    beta: MaximumScore,
                    ply: 0,
                    preferredMove: pvMove,
                    cancellationToken: cancellationToken);

                if (!result.IsComplete)
                {
                    break;
                }

                completedDepth = depth;

                if (result.BestMove is not null)
                {
                    finalBestMove = result.BestMove;
                    finalBestScore = result.BestScore;
                    pvMove = result.BestMove;
                }
                else if (finalBestMove is null)
                {
                    finalBestScore = result.BestScore;
                }
            }

            if (finalBestMove is null)
            {
                var panicMoves = board.GenerateLegal();
                if (panicMoves.Length > 0)
                {
                    finalBestMove = panicMoves[0];
                    finalBestScore = Evaluator.EvaluateForSideToMove(board);
                }
            }

            return new SearchSummary(finalBestMove, finalBestScore, completedDepth);
        }

        private static SearchNodeResult Negamax(
            Board board,
            int depth,
            int alpha,
            int beta,
            int ply,
            Board.Move? preferredMove,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), false);
            }

            if (depth == 0)
            {
                QuiescenceResult q = Quiescence(board, alpha, beta, cancellationToken);
                return new SearchNodeResult(null, q.BestScore, q.IsComplete);
            }

            int alphaOriginal = alpha;

            // --- Transposition table probe -----------------------------------
            //
            // We only trust stored scores when:
            // - The stored node was searched to at least this depth, and
            // - The node type (Exact / Alpha / Beta) is compatible with the
            //   current alpha/beta window.
            //
            // Exact  : full value for this position (can be returned directly).
            // Alpha  : upper bound  (score <= alpha when stored).
            // Beta   : lower bound  (score >= beta  when stored).
            //
            // At the root (ply == 0), we *never* return the TT score directly,
            // but we still use the stored best move for ordering – this keeps
            // the PV stable and avoids “playing from the table” at the root.
            var probe = _transpositionTable.Probe(board.ZKey, depth, alpha, beta, ply);
            Board.Move? ttMove = probe.BestMove;

            if (probe.HasScore && ply > 0)
            {
                // Trusted hit: use the stored value and best move.
                return new SearchNodeResult(ttMove, probe.Score, true);
            }

            var legalMoves = board.GenerateLegal();
            int legalMoveCount = legalMoves.Length;

            if (legalMoveCount == 0)
            {
                // Terminal node: mate or stalemate.
                int terminalScore = EvaluateTerminal(board, ply);
                return new SearchNodeResult(null, terminalScore, true);
            }

            // --- Move ordering helpers ---------------------------------------
            /// <summary>
            /// Swaps the specified move to the target position in the move list for ordering purposes.
            ///
            /// <param name="moves">The array of moves to reorder.</param>
            /// <param name="move">The move to promote to the target index.</param>
            /// <param name="targetIndex">The index in the move list to which the move should be promoted.</param>
            /// <param name="moveCount">The number of valid moves in the move list.</param>
            /// <returns>True if the move was found and promoted; false otherwise.</returns>
            /// </summary>
            static bool PromoteMove(Board.Move[] moves, Board.Move move, int targetIndex, int moveCount)
            {
                if (targetIndex < 0 || targetIndex >= moveCount)
                {
                    return false;
                }

                int index = Array.FindIndex(moves, targetIndex, moveCount - targetIndex, m => m.Equals(move));
                if (index < 0)
                {
                    return false;
                }

                if (index > targetIndex)
                {
                    (moves[targetIndex], moves[index]) = (moves[index], moves[targetIndex]);
                }

                return true;
            }

            // Promote PV move and TT move with clear semantics:
            //
            // - If we have a PV move for this node, we *always* search it first.
            //   This preserves the invariant that "PV searched first" means we can
            //   treat the node as "complete enough" when some children are left.
            //
            // - If a TT move exists and it's different from the PV move, we put it
            //   in slot 1 so it is still searched early.
            //
            // - If there is no PV move at this node, we let the TT move lead.

            int orderedCount = 0;

            if (preferredMove is Board.Move pv)
            {
                // PV is our primary candidate: it must be searched first.
                if (PromoteMove(legalMoves, pv, targetIndex: orderedCount, moveCount: legalMoveCount))
                {
                    orderedCount++;
                }

                // If TT move exists and is distinct, let it be second.
                if (ttMove is Board.Move tt && !tt.Equals(pv))
                {
                    if (PromoteMove(legalMoves, tt, targetIndex: orderedCount, moveCount: legalMoveCount))
                    {
                        orderedCount++;
                    }
                }
            }
            else if (ttMove is Board.Move tt)
            {
                // No PV for this node: TT move can take slot 0.
                if (PromoteMove(legalMoves, tt, targetIndex: orderedCount, moveCount: legalMoveCount))
                {
                    orderedCount++;
                }
            }

            // Captures ordered by MVV-LVA.
            int captureCount = 0;
            for (int i = orderedCount; i < legalMoveCount; i++)
            {
                if (legalMoves[i].IsCapture)
                {
                    captureCount++;
                }
            }

            if (captureCount > 0)
            {
                OrderCapturesByMvvLva(legalMoves, orderedCount, legalMoveCount);
                orderedCount += captureCount;
            }

            // Killer moves.
            if (ply < MaxPly)
            {
                if (_killerMovesPrimary[ply] is Board.Move killer1 && IsQuietMove(killer1))
                {
                    if (PromoteMove(legalMoves, killer1, orderedCount, legalMoveCount))
                    {
                        orderedCount++;
                    }
                }

                if (_killerMovesSecondary[ply] is Board.Move killer2 && IsQuietMove(killer2))
                {
                    if (PromoteMove(legalMoves, killer2, orderedCount, legalMoveCount))
                    {
                        orderedCount++;
                    }
                }
            }

            // Remaining quiet moves ordered by history score.
            int quietCount = 0;
            for (int i = orderedCount; i < legalMoveCount; i++)
            {
                if (IsQuietMove(legalMoves[i]))
                {
                    quietCount++;
                }
            }

            if (quietCount > 0)
            {
                OrderQuietMovesByHistory(legalMoves, orderedCount, legalMoveCount);
                orderedCount += quietCount;
            }


            Board.Move? bestMove = null;
            int bestScore = MinimumScore;

            bool sawCompleteChild = false;
            bool aborted = false;
            bool betaCutoff = false;

            for (int i = 0; i < legalMoves.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var move = legalMoves[i];

                var undo = board.MakeMove(move);
                var childResult = Negamax(
                    board: board,
                    depth: depth - 1,
                    alpha: -beta,
                    beta: -alpha,
                    ply: ply + 1,
                    preferredMove: null,
                    cancellationToken: cancellationToken);
                board.UnmakeMove(move, undo);

                if (!childResult.IsComplete)
                {
                    // Child aborted: treat this node as aborted too.
                    aborted = true;
                    break;
                }

                sawCompleteChild = true;

                int score = -childResult.BestScore;

                if (score > bestScore || bestMove is null)
                {
                    bestScore = score;
                    bestMove = move;
                }

                if (score > alpha)
                {
                    alpha = score;
                }

                if (alpha >= beta)
                {
                    // Beta cutoff: no need to consider remaining moves.
                    if (IsQuietMove(move))
                    {
                        StoreKillerMove(move, ply);
                        UpdateHistory(move, depth);
                    }
                    betaCutoff = true;
                    break;
                }
            }

            bool completed;
            if (preferredMove is not null)
            {
                // Root (or PV-ordered) node: as long as we saw at least one
                // complete child, we consider this iteration usable at the root.
                completed = sawCompleteChild;
            }
            else
            {
                // Internal node: must not have aborted, and must have at least
                // one complete child.
                completed = sawCompleteChild && !aborted;
            }

            if (bestMove is null)
            {
                // No move chosen (e.g., all children aborted) – fall back to static eval.
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), completed);
            }

            // --- Transposition table store -----------------------------------
            //
            // Only store if the search for this node actually completed (no
            // partial / cancelled results) and we weren’t cancelled here.
            if (completed && !cancellationToken.IsCancellationRequested)
            {
                var nodeType = TranspositionTable.NodeType.Exact;

                // If the search produced a beta cutoff, the score is a lower bound (Beta).
                if (betaCutoff)
                {
                    nodeType = TranspositionTable.NodeType.Beta;
                }
                // If the best score never exceeded the original alpha, it’s an upper bound (Alpha).
                else if (bestScore <= alphaOriginal)
                {
                    nodeType = TranspositionTable.NodeType.Alpha;
                }
                // Otherwise alphaOriginal < bestScore < beta: Exact value.

                _transpositionTable.Store(board.ZKey, depth, bestScore, nodeType, bestMove, ply);
            }

            return new SearchNodeResult(bestMove, bestScore, completed);
        }

        private static QuiescenceResult Quiescence(Board board, int alpha, int beta, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new QuiescenceResult(alpha, false);
            }

            bool inCheck = board.InCheck(board.SideToMove);
            if (inCheck)
            {
                Span<Board.Move> moveBuffer = stackalloc Board.Move[Board.MoveBufferMax];
                var legalMoves = board.GenerateLegal(moveBuffer);

                int currentAlpha = alpha;
                bool exploredMove = false;
                bool allComplete = true;

                foreach (var move in legalMoves)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new QuiescenceResult(exploredMove ? currentAlpha : alpha, false);
                    }

                    var undo = board.MakeMove(move);
                    QuiescenceResult child = Quiescence(board, -beta, -currentAlpha, cancellationToken);
                    int score = -child.BestScore;
                    board.UnmakeMove(move, undo);

                    exploredMove = true;
                    if (!child.IsComplete)
                    {
                        allComplete = false;
                    }

                    if (score >= beta)
                    {
                        return new QuiescenceResult(beta, allComplete);
                    }

                    if (score > currentAlpha)
                    {
                        currentAlpha = score;
                    }
                }

                if (!exploredMove)
                {
                    // No legal moves in check => mate.
                    return new QuiescenceResult(EvaluateTerminal(board, ply: 0), true);
                }

                return new QuiescenceResult(currentAlpha, allComplete);
            }

            int standPat = Evaluator.EvaluateForSideToMove(board);

            if (standPat >= beta)
            {
                // Fail-high on stand pat is a normal, complete result.
                return new QuiescenceResult(beta, true);
            }

            if (standPat > alpha)
            {
                alpha = standPat;
            }

            Span<Board.Move> pseudoBuffer = stackalloc Board.Move[Board.MoveBufferMax];
            var buffer = new MoveBuffer(pseudoBuffer);
            var pseudoMoves = MoveGeneration.GeneratePseudoLegal(board, ref buffer, board.SideToMove);
            var sideToMove = board.SideToMove;
            bool exploredCapture = false;
            bool allCompleteCaptures = true;

            foreach (var move in pseudoMoves)
            {
                if (!move.IsCapture)
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new QuiescenceResult(alpha, false);
                }

                var undo = board.MakeMove(move);
                bool leavesKingInCheck = board.InCheck(sideToMove);
                if (leavesKingInCheck)
                {
                    board.UnmakeMove(move, undo);
                    continue;
                }

                exploredCapture = true;
                QuiescenceResult child = Quiescence(board, -beta, -alpha, cancellationToken);
                int score = -child.BestScore;
                board.UnmakeMove(move, undo);

                if (!child.IsComplete)
                {
                    allCompleteCaptures = false;
                }

                if (score >= beta)
                {
                    return new QuiescenceResult(beta, allCompleteCaptures);
                }

                if (score > alpha)
                {
                    alpha = score;
                }
            }

            if (!exploredCapture && !board.HasAnyLegalMoves())
            {
                // No captures searched and no legal moves at all => stalemate or mate.
                return new QuiescenceResult(EvaluateTerminal(board, ply: 0), true);
            }

            return new QuiescenceResult(alpha, allCompleteCaptures);
        }

        private static int EvaluateTerminal(Board board, int ply)
        {
            bool sideToMoveInCheck = board.InCheck(board.SideToMove);
            if (sideToMoveInCheck)
            {
                // Mate scores are offset by ply so they can be normalized to
                // “mate in N” later, consistent with the TT helpers.
                return -MateScore + ply;
            }

            // Stalemate (or other terminal draw) – 0 from side-to-move POV.
            return 0;
        }

        private static void StoreKillerMove(Board.Move move, int ply)
        {
            if (ply >= MaxPly)
            {
                return;
            }

            if (_killerMovesPrimary[ply] is Board.Move existing && existing.Equals(move))
            {
                return;
            }

            _killerMovesSecondary[ply] = _killerMovesPrimary[ply];
            _killerMovesPrimary[ply] = move;
        }

        private static void UpdateHistory(Board.Move move, int depth)
        {
            var pieceIndex = move.Mover.Type;

            if (!_historyScores.TryGetValue(pieceIndex, out var table))
            {
                table = new Dictionary<Square0x88, int>();
                _historyScores[pieceIndex] = table;
            }

            var to = move.To88;
            table[to] = table.GetValueOrDefault(to, 0) + depth * depth;
        }

        private static int GetHistoryScore(Board.Move move)
        {
            var pieceIndex = move.Mover.Type;
            return _historyScores.TryGetValue(pieceIndex, out var table)
                ? table.GetValueOrDefault(move.To88, 0)
                : 0;
        }

        private static int ScoreCapture(Board.Move move)
        {
            int victimValue = _pieceOrderingValues[(int)move.Captured.Type];
            int attackerValue = _pieceOrderingValues[(int)move.Mover.Type];
            return victimValue * 10 - attackerValue;
        }

        private static bool IsQuietMove(Board.Move move) => !move.IsCapture && !move.IsPromotion;

        private static void OrderCapturesByMvvLva(Board.Move[] moves, int startIndex, int moveCount)
        {
            for (int current = startIndex; current < moveCount; current++)
            {
                int bestIndex = -1;
                int bestScore = int.MinValue;

                for (int candidate = current; candidate < moveCount; candidate++)
                {
                    var move = moves[candidate];
                    if (!move.IsCapture)
                    {
                        continue;
                    }

                    int score = ScoreCapture(move);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = candidate;
                    }
                }

                if (bestIndex == -1)
                {
                    break;
                }

                if (bestIndex != current)
                {
                    (moves[current], moves[bestIndex]) = (moves[bestIndex], moves[current]);
                }
            }
        }

        private static void OrderQuietMovesByHistory(Board.Move[] moves, int startIndex, int moveCount)
        {
            for (int current = startIndex; current < moveCount; current++)
            {
                int bestIndex = -1;
                int bestScore = int.MinValue;

                for (int candidate = current; candidate < moveCount; candidate++)
                {
                    var move = moves[candidate];
                    if (!IsQuietMove(move))
                    {
                        continue;
                    }

                    int score = GetHistoryScore(move);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = candidate;
                    }
                }

                if (bestIndex == -1)
                {
                    break;
                }

                if (bestIndex != current)
                {
                    (moves[current], moves[bestIndex]) = (moves[bestIndex], moves[current]);
                }
            }
        }
    }
}
