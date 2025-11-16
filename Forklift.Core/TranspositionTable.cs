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
    // TODO: Revisit MateValue if evaluation scaling in Evaluator changes.
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

    public void Store(ulong zobristKey, int depth, int score, NodeType nodeType, Board.Move? bestMove, int ply)
    {
        ref var entry = ref _entries[(int)(zobristKey & (uint)_mask)];

        if (entry.IsValid && entry.ZobristKey == zobristKey && entry.Depth > depth)
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
