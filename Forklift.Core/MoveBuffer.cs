using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Forklift.Core;

/// <summary>
/// Provides a stack-first buffer for collecting generated moves while allowing an overflow
/// array if the stack storage proves insufficient. The buffer exposes helper APIs for adding
/// moves and for materialising the resulting span of moves without leaking ownership of the
/// underlying storage.
/// </summary>
public ref struct MoveBuffer
{
    private Span<Board.Move> _stackSpan;
    private Board.Move[]? _overflow;
    private int _count;

    public MoveBuffer(Span<Board.Move> stackSpan)
    {
        _stackSpan = stackSpan;
        _overflow = null;
        _count = 0;
    }

    /// <summary>Number of moves written into the buffer.</summary>
    public readonly int Count => _count;

    /// <summary>Indicates whether the buffer had to allocate an overflow array.</summary>
    public readonly bool UsedOverflow => _overflow is not null;

    /// <summary>Adds a move to the buffer, growing the overflow storage if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Board.Move move)
    {
        if (!UsedOverflow && _count < _stackSpan.Length)
        {
            _stackSpan[_count++] = move;
            return;
        }

        EnsureOverflowCapacity(_count + 1);
        _overflow![_count++] = move;
    }

    /// <summary>Creates a <see cref="MoveSpan"/> representing the moves written so far.</summary>
    public readonly MoveSpan ToMoveSpan()
    {
        if (UsedOverflow)
        {
            return new MoveSpan(_overflow!.AsSpan(0, _count));
        }

        return new MoveSpan(_stackSpan[.._count]);
    }

    /// <summary>Asserts that the stack buffer was sufficient for the written moves.</summary>
    [Conditional("DEBUG")]
    public readonly void AssertNoOverflow(string context)
    {
        Debug.Assert(!UsedOverflow, $"Move buffer overflow in {context}: {_count}/{_stackSpan.Length}");
    }

    private void EnsureOverflowCapacity(int required)
    {
        if (_overflow is null)
        {
            int newSize = Math.Max(_stackSpan.Length * 2, required);
            _overflow = new Board.Move[newSize];
            if (_count > 0 && _stackSpan.Length > 0)
            {
                _stackSpan[.._count].CopyTo(_overflow);
            }
        }
        else if (_overflow.Length < required)
        {
            Array.Resize(ref _overflow, Math.Max(_overflow.Length * 2, required));
        }
    }
}

/// <summary>
/// Represents the finalised collection of generated moves. This ref struct wraps either the
/// original stack span or the overflow array as a read-only view while supporting enumeration
/// and conversion helpers.
/// </summary>
public readonly ref struct MoveSpan
{
    private readonly ReadOnlySpan<Board.Move> _span;

    public MoveSpan(ReadOnlySpan<Board.Move> span) => _span = span;

    public int Length => _span.Length;

    public Board.Move this[int index] => _span[index];

    public Board.Move[] ToArray() => _span.ToArray();

    public ReadOnlySpan<Board.Move> AsReadOnlySpan() => _span;

    public ReadOnlySpan<Board.Move>.Enumerator GetEnumerator() => _span.GetEnumerator();
}
