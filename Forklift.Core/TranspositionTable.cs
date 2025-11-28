using System;

namespace Forklift.Core;

/// <summary>
/// Fixed-size transposition table for alpha-beta search.
/// </summary>
public sealed class TranspositionTable
{
    public enum NodeType
    {
        Exact,
        Alpha,
        Beta
    }

    // MateValue must always be larger than any evaluation score returned by
    // Evaluator.EvaluateForSideToMove so mate scores cannot be confused with
    // regular evaluation values.
    internal const int MateValue = Evaluator.MaxEvaluationScore * 2;
    internal const int MateScoreThreshold = MateValue - 512;

    private readonly Entry[] _entries;
    private readonly int _mask;

    private struct Entry
    {
        public ulong ZobristKey;
        public int Depth;
        public int Score;
        public NodeType NodeType;
        public Board.Move BestMove;
        public bool HasMove;
        public bool IsValid;
    }

    public readonly record struct ProbeResult(bool Hit, bool HasScore, int Score, Board.Move? BestMove)
    {
        public static ProbeResult Miss => new(false, false, 0, null);
    }

    public TranspositionTable(int sizePowerOfTwo = 20)
    {
        if (sizePowerOfTwo < 1 || sizePowerOfTwo > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(sizePowerOfTwo));
        }

        var length = 1 << sizePowerOfTwo;
        _entries = new Entry[length];
        _mask = length - 1;
    }

    public void Clear() => Array.Clear(_entries, 0, _entries.Length);

    /// <summary>
    /// Probes the transposition table for an entry matching the given position and search parameters.
    /// </summary>
    /// <param name="zobristKey">The Zobrist hash key representing the current board position.</param>
    /// <param name="depth">The search depth (in plies) for which the entry is valid.</param>
    /// <param name="alpha">The lower bound of the alpha-beta search window.</param>
    /// <param name="beta">The upper bound of the alpha-beta search window.</param>
    /// <param name="ply">The current search ply, used to adjust mate scores relative to the root.</param>
    /// <returns>
    /// A <see cref="ProbeResult"/> indicating whether a matching entry was found (<c>Hit</c>),
    /// whether the stored score is usable for the current search window (<c>HasScore</c>),
    /// the score (if available, adjusted for mate distance), and the best move (if available).
    /// </returns>
    public ProbeResult Probe(ulong zobristKey, int depth, int alpha, int beta, int ply)
    {
        ref var entry = ref _entries[(int)(zobristKey & (uint)_mask)];
        if (!entry.IsValid || entry.ZobristKey != zobristKey)
        {
            return ProbeResult.Miss;
        }

        Board.Move? bestMove = entry.HasMove ? entry.BestMove : null;

        if (entry.Depth >= depth)
        {
            int score = RestoreScoreFromStorage(entry.Score, ply);
            bool useScore = entry.NodeType switch
            {
                NodeType.Exact => true,
                NodeType.Alpha => score <= alpha,
                NodeType.Beta => score >= beta,
                _ => false,
            };

            if (useScore)
            {
                return new ProbeResult(true, true, score, bestMove);
            }
        }

        return new ProbeResult(true, false, 0, bestMove);
    }

    /// <summary>
    /// Stores a search result in the transposition table.
    /// Call this method after searching a node to cache its evaluation and best move for future lookups.
    /// The entry is only replaced if the slot is empty or the new entry has greater or equal search depth.
    /// </summary>
    /// <param name="zobristKey">The Zobrist hash of the board position.</param>
    /// <param name="depth">The search depth (in plies) at which this node was evaluated.</param>
    /// <param name="score">
    /// The evaluation score for the position, from the perspective of the side to move.
    /// Mate scores should be in the standard format (e.g., positive for mate in N, negative for being mated in N).
    /// </param>
    /// <param name="nodeType">
    /// The type of node:
    /// <list type="bullet">
    /// <item><description><see cref="NodeType.Exact"/>: The score is exact (PV node).</description></item>
    /// <item><description><see cref="NodeType.Alpha"/>: The score is a lower bound (fail-low).</description></item>
    /// <item><description><see cref="NodeType.Beta"/>: The score is an upper bound (fail-high).</description></item>
    /// </list>
    /// </param>
    /// <param name="bestMove">The best move found from this position, or <c>null</c> if unknown.</param>
    /// <param name="ply">
    /// The distance from the root node (in plies). Used to normalize mate scores for correct retrieval.
    /// </param>
    /// <remarks>
    /// The replacement strategy only overwrites an existing entry if the new entry is for the same position and has greater or equal search depth.
    /// </remarks>
    public void Store(ulong zobristKey, int depth, int score, NodeType nodeType, Board.Move? bestMove, int ply)
    {
        ref var entry = ref _entries[(int)(zobristKey & (uint)_mask)];

        if (entry.IsValid && entry.ZobristKey == zobristKey && entry.Depth >= depth)
        {
            return;
        }

        entry.ZobristKey = zobristKey;
        entry.Depth = depth;
        entry.Score = NormalizeScoreForStorage(score, ply);
        entry.NodeType = nodeType;
        if (bestMove is Board.Move mv)
        {
            entry.BestMove = mv;
            entry.HasMove = true;
        }
        else
        {
            entry.BestMove = default;
            entry.HasMove = false;
        }
        entry.IsValid = true;
    }

    private static int NormalizeScoreForStorage(int score, int ply)
    {
        if (score >= MateScoreThreshold)
        {
            return score + ply;
        }

        if (score <= -MateScoreThreshold)
        {
            return score - ply;
        }

        return score;
    }

    private static int RestoreScoreFromStorage(int score, int ply)
    {
        if (score >= MateScoreThreshold)
        {
            return score - ply;
        }

        if (score <= -MateScoreThreshold)
        {
            return score + ply;
        }

        return score;
    }
}
