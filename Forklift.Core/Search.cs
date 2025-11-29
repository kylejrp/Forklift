using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public static class Search
    {
        public readonly record struct SearchSummary(Board.Move?[] PrincipalVariation, int BestScore, int CompletedDepth, int NodesSearched);

        private readonly record struct SearchNodeResult(Board.Move? BestMove, int BestScore, bool IsComplete, int NodesSearched);

        private readonly record struct QuiescenceResult(int BestScore, bool IsComplete, int NodesSearched);

        private const int MinimumScore = int.MinValue + 1; // Avoid overflow when negating
        private const int MaximumScore = int.MaxValue; // No overflow risk when negating

        private const int MateScore = TranspositionTable.MateValue;
        private const int NullMoveReduction = 2;
        private const int NullMoveMinDepth = 3;

        private static readonly TranspositionTable _transpositionTable = new();

        private const int MaxPly = 128;
        private static readonly Board.Move?[] _killerMovesPrimary = new Board.Move?[MaxPly];
        private static readonly Board.Move?[] _killerMovesSecondary = new Board.Move?[MaxPly];
        private static readonly HistoryTable _historyScores = new HistoryTable();

        public static void ClearTranspositionTable() => _transpositionTable.Clear();

        public static void ClearHeuristics()
        {
            Array.Clear(_killerMovesPrimary, 0, _killerMovesPrimary.Length);
            Array.Clear(_killerMovesSecondary, 0, _killerMovesSecondary.Length);
            _historyScores.Clear();
        }

        // Negamax search, returns best move and score
        public static SearchSummary FindBestMove(Board board, int maxDepth, CancellationToken cancellationToken = default, Action<SearchSummary>? summaryCallback = null)
        {
            ClearHeuristics();

            Board.Move? finalBestMove = null;
            int finalBestScore = MinimumScore;
            int completedDepth = 0;
            int totalNodesSearched = 0;

            Board.Move?[] pvMoves = Array.Empty<Board.Move?>();
            var pvTable = new PrincipalVariationTable(maxDepth);

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                pvTable.Clear();

                var result = Negamax(
                    board: board,
                    depth: depth,
                    alpha: MinimumScore,
                    beta: MaximumScore,
                    ply: 0,
                    preferredMoves: pvMoves,
                    pvTable: pvTable,
                    parentMoveWasNullMove: false,
                    cancellationToken: cancellationToken);

                totalNodesSearched += result.NodesSearched;

                if (!result.IsComplete)
                {
                    break;
                }

                completedDepth = depth;

                if (result.BestMove is not null)
                {
                    finalBestMove = result.BestMove;
                    finalBestScore = result.BestScore;
                    pvMoves = pvTable.GetRootPrincipalVariation();
                    Debug.Assert(pvMoves.Length > 0 && pvMoves[0] == finalBestMove);
                }
                else if (finalBestMove is null)
                {
                    finalBestScore = result.BestScore;
                }

                if (pvMoves.Length == 0 && finalBestMove is not null)
                {
                    pvMoves = new Board.Move?[] { finalBestMove };
                }

                summaryCallback?.Invoke(new SearchSummary(pvMoves, finalBestScore, completedDepth, totalNodesSearched));
            }

            if (finalBestMove is null)
            {
                Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
                board.GenerateLegal(ref moves);
                if (moves.Length > 0)
                {
                    finalBestMove = moves[0];
                    finalBestScore = Evaluator.EvaluateForSideToMove(board);
                    totalNodesSearched++;
                }
            }

            return new SearchSummary(pvMoves, finalBestScore, completedDepth, totalNodesSearched);
        }

        private static SearchNodeResult Negamax(
            Board board,
            int depth,
            int alpha,
            int beta,
            int ply,
            Board.Move?[] preferredMoves,
            PrincipalVariationTable? pvTable,
            bool parentMoveWasNullMove,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), false, 0);
            }

            if (depth == 0)
            {
                QuiescenceResult q = Quiescence(board, alpha, beta, ply + 1, cancellationToken);
                return new SearchNodeResult(null, q.BestScore, q.IsComplete, q.NodesSearched);
            }

            pvTable?.InitPly(ply);

            int nodesSearched = 0;

            // --- Transposition table probe -----------------------------------
            // Probe without alpha/beta; just get raw info for this key.
            var ttEntry = _transpositionTable.Probe(board.ZKey, ply);
            Board.Move? ttMove = null;

            var preferredMove = preferredMoves.Length > 0 ? preferredMoves[0] : (Board.Move?)null;
            bool isPreferredMoveNode = preferredMove.HasValue;

            // 1) Always feed ttMove to move ordering if we have a hit.
            //    This is independent of whether we can use the score for a cutoff.
            if (ttEntry.NodeType != TranspositionTable.NodeType.Miss && ttEntry.BestMove is Board.Move ttMoveValue)
            {
                ttMove = ttMoveValue;
            }

            // 2) Conservative early return from TT.
            //    Only at non-PV nodes, only when not in your "preferred move" special case,
            //    only when the stored depth is high enough, and only when the bound matches.
            if (!isPreferredMoveNode
                && ttEntry.NodeType != TranspositionTable.NodeType.Miss
                && ttEntry.Depth >= depth // require at least current search depth
                && ttEntry.Score.HasValue)
            {
                var ttScore = ttEntry.Score.Value;

                switch (ttEntry.NodeType)
                {
                    case TranspositionTable.NodeType.Exact:
                        // Full value for this position, safe to return at non-PV nodes.
                        return new SearchNodeResult(ttMove, ttScore, true, nodesSearched);

                    case TranspositionTable.NodeType.Beta:
                        // Stored as a lower bound: score >= stored beta.
                        // If it still fails high against our current beta, we can cut.
                        if (ttScore >= beta)
                        {
                            return new SearchNodeResult(ttMove, ttScore, true, nodesSearched);
                        }
                        break;

                    case TranspositionTable.NodeType.Alpha:
                        // Stored as an upper bound: score <= stored alpha.
                        // If it still fails low against our current alpha, we can cut.
                        if (ttScore <= alpha)
                        {
                            return new SearchNodeResult(ttMove, ttScore, true, nodesSearched);
                        }
                        break;

                    default:
                        break;
                }
            }

            // If we get here, either there was no TT hit, or it wasn't compatible
            // with the current window / depth for a cutoff. But we still got ttMove
            // above and can use it to seed move ordering.

            if (!parentMoveWasNullMove &&
                depth >= NullMoveMinDepth &&
                ply > 0 &&
                !board.InCheck(board.SideToMove) &&
                HasNonPawnMaterial(board))
            {
                int nullDepth = depth - 1 - NullMoveReduction;
                if (nullDepth < 0)
                {
                    nullDepth = 0;
                }

                var nullState = board.MakeNullMove();
                var nullChild = Negamax(
                    board: board,
                    depth: nullDepth,
                    alpha: -beta,
                    beta: -beta + 1,
                    ply: ply + 1,
                    preferredMoves: Array.Empty<Board.Move?>(),
                    pvTable: null,
                    parentMoveWasNullMove: true,
                    cancellationToken: cancellationToken);
                board.UnmakeNullMove(nullState);
                nodesSearched += nullChild.NodesSearched;


                if (!nullChild.IsComplete)
                {
                    return new SearchNodeResult(null, nullChild.BestScore, false, nodesSearched);
                }

                int nullScore = -nullChild.BestScore;
                if (nullScore >= beta)
                {
                    _transpositionTable.Store(board.ZKey, depth, nullScore, TranspositionTable.NodeType.Beta, bestMove: null, ply);
                    return new SearchNodeResult(null, nullScore, true, nodesSearched);
                }
            }

            Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
            var picker = new MovePicker(
                board: board,
                moveBuffer: moves,
                history: _historyScores,
                pvMove: preferredMove,
                ttMove: ttMove,
                killer1: ply < MaxPly && _killerMovesPrimary[ply] is Board.Move killer1 && killer1.IsQuiet ? killer1 : null,
                killer2: ply < MaxPly && _killerMovesSecondary[ply] is Board.Move killer2 && killer2.IsQuiet ? killer2 : null
            );

            int legalMoveCount = 0;
            Board.Move? bestMove = null;
            int bestScore = MinimumScore;

            bool sawCompleteChild = false;
            bool aborted = false;
            int alphaOriginal = alpha;
            bool betaCutoff = false;
            List<Board.Move> quietMovesSearched = new List<Board.Move>();

            while (picker.Next() is Board.Move move)
            {
                legalMoveCount++;

                // Check for cancellation at the top of the loop to ensure we
                // respond promptly even if the move generation is fast.
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                nodesSearched++;
                var undo = board.MakeMove(move);
                var childResult = Negamax(
                    board: board,
                    depth: depth - 1,
                    alpha: -beta,
                    beta: -alpha,
                    ply: ply + 1,
                    preferredMoves: preferredMoves.Length > 1 && move == preferredMove ? preferredMoves[1..] : Array.Empty<Board.Move?>(),
                    pvTable: pvTable,
                    parentMoveWasNullMove: false,
                    cancellationToken: cancellationToken);
                board.UnmakeMove(move, undo);

                nodesSearched += childResult.NodesSearched;

                if (!childResult.IsComplete)
                {
                    // Child aborted: treat this node as aborted too.
                    aborted = true;
                    break;
                }

                sawCompleteChild = true;

                int score = -childResult.BestScore;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;

                    if (score > alpha)
                    {
                        if (score < beta)
                        {
                            pvTable?.Update(ply, move);
                        }
                        alpha = score;
                    }
                }

                if (score >= beta)
                {
                    // Beta cutoff: no need to consider remaining moves.
                    if (move.IsQuiet)
                    {
                        StoreKillerMove(move, ply);
                        var bonus = 300 * depth - 250;
                        UpdateHistory(move, bonus);
                        foreach (var quietMove in quietMovesSearched)
                        {
                            UpdateHistory(quietMove, -bonus);
                        }
                    }
                    betaCutoff = true;
                    break;
                }

                // do this quiet check after beta cutoff so we don't have to account for it there
                // when updating history with a negative bonus
                if (move.IsQuiet)
                {
                    quietMovesSearched.Add(move);
                }
            }

            if (legalMoveCount == 0)
            {
                // Terminal node: mate or stalemate.
                int terminalScore = EvaluateTerminal(board, ply);
                return new SearchNodeResult(null, terminalScore, true, nodesSearched);
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
                return new SearchNodeResult(null, Evaluator.EvaluateForSideToMove(board), completed, nodesSearched);
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

            return new SearchNodeResult(bestMove, bestScore, completed, nodesSearched);
        }

        private static QuiescenceResult Quiescence(Board board, int alpha, int beta, int ply, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new QuiescenceResult(alpha, false, 0);
            }

            bool inCheck = board.InCheck(board.SideToMove);
            int nodesSearched = 0;
            int bestScore = MinimumScore;
            if (inCheck)
            {
                Span<Board.Move> moves = stackalloc Board.Move[Board.MoveBufferMax];
                var picker = new MovePicker(
                    board: board,
                    moveBuffer: moves,
                    history: _historyScores
                );

                bool exploredMove = false;
                bool allComplete = true;

                while (picker.Next() is Board.Move move)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new QuiescenceResult(alpha, false, nodesSearched);
                    }

                    nodesSearched++;
                    var undo = board.MakeMove(move);
                    QuiescenceResult child = Quiescence(board, -beta, -alpha, ply + 1, cancellationToken);
                    int score = -child.BestScore;
                    board.UnmakeMove(move, undo);
                    nodesSearched += child.NodesSearched;

                    exploredMove = true;
                    if (!child.IsComplete)
                    {
                        allComplete = false;
                    }

                    if (score >= beta)
                    {
                        return new QuiescenceResult(score, allComplete, nodesSearched);
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                    }

                    if (score > alpha)
                    {
                        alpha = score;
                    }
                }

                if (!exploredMove)
                {
                    // No legal moves in check => mate.
                    return new QuiescenceResult(EvaluateTerminal(board, ply), true, nodesSearched);
                }

                return new QuiescenceResult(bestScore, allComplete, nodesSearched);
            }

            // Stand pat
            bestScore = Evaluator.EvaluateForSideToMove(board);
            if (bestScore >= beta)
            {
                return new QuiescenceResult(bestScore, true, nodesSearched);
            }

            if (bestScore > alpha)
            {
                alpha = bestScore;
            }

            Span<Board.Move> nonQuietPseudoMoves = stackalloc Board.Move[Board.MoveBufferMax];
            var nonQuietPseudoPicker = new MovePicker(
                board: board,
                moveBuffer: nonQuietPseudoMoves,
                history: _historyScores,
                moveGenerationStrategy: MovePicker.MoveGenerationStrategy.PseudoLegalNonQuietOnly
            );
            var sideToMove = board.SideToMove;
            bool exploredNonQuietMove = false;
            bool allCompleteCaptures = true;

            while (nonQuietPseudoPicker.Next() is Board.Move move)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new QuiescenceResult(alpha, false, nodesSearched);
                }

                nodesSearched++;
                var undo = board.MakeMove(move);
                bool leavesKingInCheck = board.InCheck(sideToMove);
                if (leavesKingInCheck)
                {
                    board.UnmakeMove(move, undo);
                    continue;
                }

                exploredNonQuietMove = true;
                QuiescenceResult child = Quiescence(board, -beta, -alpha, ply + 1, cancellationToken);
                int score = -child.BestScore;
                board.UnmakeMove(move, undo);
                nodesSearched += child.NodesSearched;

                if (!child.IsComplete)
                {
                    allCompleteCaptures = false;
                }

                if (score >= beta)
                {
                    return new QuiescenceResult(score, allCompleteCaptures, nodesSearched);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                }

                if (score > alpha)
                {
                    alpha = score;
                }
            }

            if (!exploredNonQuietMove && !board.HasAnyLegalMoves())
            {
                // No non-quiet moves searched and no legal moves at all => stalemate or mate.
                return new QuiescenceResult(EvaluateTerminal(board, ply), true, nodesSearched);
            }

            return new QuiescenceResult(bestScore, allCompleteCaptures, nodesSearched);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateHistory(Board.Move move, int bonus)
        {
            _historyScores.Update(move, bonus);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetHistoryScore(Board.Move move)
        {
            return _historyScores.Get(move);
        }

        private static bool HasNonPawnMaterial(Board board)
        {
            ulong whiteNonPawns =
                board.GetPieceBitboard(Piece.WhiteKnight) |
                board.GetPieceBitboard(Piece.WhiteBishop) |
                board.GetPieceBitboard(Piece.WhiteRook) |
                board.GetPieceBitboard(Piece.WhiteQueen);

            ulong blackNonPawns =
                board.GetPieceBitboard(Piece.BlackKnight) |
                board.GetPieceBitboard(Piece.BlackBishop) |
                board.GetPieceBitboard(Piece.BlackRook) |
                board.GetPieceBitboard(Piece.BlackQueen);

            return (whiteNonPawns | blackNonPawns) != 0;
        }
    }
}
