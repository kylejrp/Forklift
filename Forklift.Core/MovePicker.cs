namespace Forklift.Core
{
    public ref struct MovePicker
    {
        private readonly Board _board;
        private Span<Board.Move> _moveBuffer;
        private readonly HistoryTable _history;
        private readonly MoveGenerationStrategy _moveGenerationStrategy;
        private readonly MoveScoringStrategy _moveScoringStrategy;

        private readonly Board.Move? _pvMove;
        private readonly Board.Move? _ttMove;
        private readonly Board.Move? _killer1;
        private readonly Board.Move? _killer2;

        private Stage _stage = Stage.GenerateMoves;
        private int _currentIndex = 0;
        private int _orderedIndex = 0;
        private int _count = 0;
        private int? _lastCaptureIndex = null;
        private int? _lastKillerIndex = null;
        private int? _lastQuietIndex = null;

        private enum Stage
        {
            GenerateMoves,
            OrderPvMove,
            PickBestPvMove,
            OrderTtMove,
            PickBestTtMove,
            OrderCapturesByMvvLva,
            PickBestCaptureByMvvLva,
            OrderKillerMoves,
            PickBestKillerMoves,
            OrderQuietMovesByHistory,
            PickBestQuietMove,
            PickBestRemainingMove,
            Done
        }

        public enum MoveGenerationStrategy
        {
            Legal,
            PseudoLegal,
            PseudoLegalNonQuietOnly,
        }

        public enum MoveScoringStrategy
        {
            None,
            Standard,
        }

        public MovePicker(
            Board board,
            Span<Board.Move> moveBuffer,
            HistoryTable history,
            MoveGenerationStrategy moveGenerationStrategy = MoveGenerationStrategy.Legal,
            MoveScoringStrategy moveScoringStrategy = MoveScoringStrategy.Standard,
            Board.Move? pvMove = null,
            Board.Move? ttMove = null,
            Board.Move? killer1 = null,
            Board.Move? killer2 = null
        )
        {
            _board = board;
            _moveBuffer = moveBuffer;
            _history = history;
            _moveGenerationStrategy = moveGenerationStrategy;
            _moveScoringStrategy = moveScoringStrategy;
            _pvMove = pvMove;
            _ttMove = ttMove;
            _killer1 = killer1;
            _killer2 = killer2;
        }

        /// <summary>
        /// Returns the next best move, or null if no moves remain.
        /// </summary>
        public Board.Move? Next()
        {
            if (_stage == Stage.Done)
            {
                return null;
            }

            if (_stage == Stage.GenerateMoves)
            {
                _stage = Stage.OrderPvMove;
                if (_moveGenerationStrategy == MoveGenerationStrategy.Legal)
                {
                    _board.GenerateLegal(ref _moveBuffer);
                }
                else if (_moveGenerationStrategy == MoveGenerationStrategy.PseudoLegal)
                {
                    MoveGeneration.GeneratePseudoLegal(_board, ref _moveBuffer, _board.SideToMove);
                }
                else if (_moveGenerationStrategy == MoveGenerationStrategy.PseudoLegalNonQuietOnly)
                {
                    MoveGeneration.GeneratePseudoLegal(_board, ref _moveBuffer, _board.SideToMove, MoveGeneration.MoveKindFilter.NonQuiet);
                }
                else
                {
                    throw new ArgumentOutOfRangeException();
                }
                _count = _moveBuffer.Length;
            }

            if (_stage == Stage.OrderPvMove)
            {
                if (PromoteMove(_pvMove))
                {
                    _stage = Stage.PickBestPvMove;
                }
                else
                {
                    _stage = Stage.OrderTtMove;
                }
            }

            if (_stage == Stage.PickBestPvMove)
            {
                _stage = Stage.OrderTtMove;
                return TryGetMoveAtCurrentIndex();
            }

            if (_stage == Stage.OrderTtMove)
            {
                if (_ttMove.HasValue && (!_pvMove.HasValue || _pvMove.Value != _ttMove.Value) && _board.MoveIsLegal(_ttMove) && PromoteMove(_ttMove))
                {
                    _stage = Stage.PickBestTtMove;
                }
                else
                {
                    _stage = Stage.OrderCapturesByMvvLva;
                }
            }

            if (_stage == Stage.PickBestTtMove)
            {
                _stage = Stage.OrderCapturesByMvvLva;
                return TryGetMoveAtCurrentIndex();
            }

            if (_stage == Stage.OrderCapturesByMvvLva)
            {
                if (_moveGenerationStrategy == MoveGenerationStrategy.PseudoLegalNonQuietOnly)
                {
                    //FilterOutBadCaptures();
                }

                _stage = Stage.PickBestCaptureByMvvLva;
                OrderCapturesByMvvLva();
            }

            if (_stage == Stage.PickBestCaptureByMvvLva)
            {
                if (_lastCaptureIndex.HasValue && _currentIndex <= _lastCaptureIndex.Value)
                {
                    return _moveBuffer[_currentIndex++];
                }
                _stage = Stage.OrderKillerMoves;
            }

            if (_stage == Stage.OrderKillerMoves)
            {
                if (PromoteKillerMoves())
                {
                    _stage = Stage.PickBestKillerMoves;
                }
                else
                {
                    _stage = Stage.OrderQuietMovesByHistory;
                }
            }

            if (_stage == Stage.PickBestKillerMoves)
            {
                if (_lastKillerIndex.HasValue && _currentIndex <= _lastKillerIndex.Value)
                {
                    return TryGetMoveAtCurrentIndex();
                }
                _stage = Stage.OrderQuietMovesByHistory;
            }

            if (_stage == Stage.OrderQuietMovesByHistory)
            {
                if (OrderQuietMovesByHistory())
                {
                    _stage = Stage.PickBestQuietMove;
                }
                else
                {
                    _stage = Stage.PickBestRemainingMove;
                }
            }

            if (_stage == Stage.PickBestQuietMove)
            {
                if (_lastQuietIndex.HasValue && _currentIndex <= _lastQuietIndex.Value)
                {
                    return TryGetMoveAtCurrentIndex();
                }
                _stage = Stage.PickBestRemainingMove;
            }

            if (_stage == Stage.PickBestRemainingMove)
            {
                return TryGetMoveAtCurrentIndex();
            }

            throw new InvalidOperationException($"Invalid MovePicker state: {_stage}");
        }

        private Board.Move? TryGetMoveAtCurrentIndex()
        {
            if (_currentIndex < _count)
            {
                return _moveBuffer[_currentIndex++];
            }
            _stage = Stage.Done;
            return null;
        }

        /// <param name="moves">The array of <see cref="Board.Move"/>s to reorder.</param>
        /// <param name="move">The <see cref="Board.Move"/> to promote to the target index.</param>
        /// <param name="targetIndex">The index in the <see cref="Board.Move"/> list to which the <see cref="Board.Move"/> should be promoted.</param>
        /// <param name="moveCount">The number of valid <see cref="Board.Move"/>s in the move list.</param>
        /// <returns><see langword="true"/> if the move was found and promoted; <see langword="false"/> otherwise.</returns>
        private bool PromoteMove(Board.Move? move)
        {
            if (!move.HasValue)
            {
                return false;
            }

            var targetIndex = _orderedIndex;
            if (targetIndex < 0 || targetIndex >= _count)
            {
                return false;
            }

            var slice = _moveBuffer.Slice(targetIndex, _count - targetIndex);
            var relativeIndex = slice.IndexOf(move.Value);
            if (relativeIndex < 0)
            {
                return false;
            }

            var index = targetIndex + relativeIndex;
            if (index > targetIndex)
            {
                (_moveBuffer[targetIndex], _moveBuffer[index]) = (_moveBuffer[index], _moveBuffer[targetIndex]);
            }

            _orderedIndex++;

            return true;
        }

        private void OrderCapturesByMvvLva()
        {
            int captureCount = 0;
            for (int i = _orderedIndex; i < _count; i++)
            {
                if (_moveBuffer[i].IsCapture)
                {
                    captureCount++;
                }
            }

            if (captureCount > 0)
            {
                for (int current = _orderedIndex; current < _count; current++)
                {
                    int? bestIndex = null;
                    int bestScore = int.MinValue;

                    for (int candidate = current; candidate < _count; candidate++)
                    {
                        var move = _moveBuffer[candidate];
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

                    if (!bestIndex.HasValue)
                    {
                        break;
                    }

                    if (bestIndex != current)
                    {
                        (_moveBuffer[current], _moveBuffer[bestIndex.Value]) = (_moveBuffer[bestIndex.Value], _moveBuffer[current]);
                    }
                }
                _orderedIndex += captureCount;
                _lastCaptureIndex = _orderedIndex - 1;
            }
        }

        private void FilterOutBadCaptures()
        {
            int writeIndex = _currentIndex;
            for (int readIndex = _currentIndex; readIndex < _count; readIndex++)
            {
                var move = _moveBuffer[readIndex];
                if (move.IsCapture)
                {
                    int victimValue = _pieceOrderingValues[(int)move.Captured.Type];
                    int attackerValue = _pieceOrderingValues[(int)move.Mover.Type];
                    int score = victimValue - attackerValue;
                    if (score >= 0)
                    {
                        _moveBuffer[writeIndex++] = move;
                    }
                }
                else
                {
                    _moveBuffer[writeIndex++] = move;
                }
            }
            _count = writeIndex;
        }

        private static readonly int[] _pieceOrderingValues = {
            100, // Pawn
            320, // Knight
            330, // Bishop
            500, // Rook
            900, // Queen
            2000 // King
        };

        private static int ScoreCapture(Board.Move move)
        {
            int victimValue = _pieceOrderingValues[(int)move.Captured.Type];
            int attackerValue = _pieceOrderingValues[(int)move.Mover.Type];
            return victimValue * 10 - attackerValue;
        }

        private bool PromoteKillerMoves()
        {
            bool promoted = false;
            promoted |= PromoteMove(_killer1);
            promoted |= PromoteMove(_killer2);
            if (promoted)
            {
                _lastKillerIndex = _orderedIndex - 1;
            }
            return promoted;
        }

        private bool OrderQuietMovesByHistory()
        {
            int quietCount = 0;
            for (int i = _orderedIndex; i < _count; i++)
            {
                if (_moveBuffer[i].IsQuiet)
                {
                    quietCount++;
                }
            }

            if (quietCount > 0)
            {
                for (int current = _orderedIndex; current < _count; current++)
                {
                    int? bestIndex = null;
                    int bestScore = int.MinValue;

                    for (int candidate = current; candidate < _count; candidate++)
                    {
                        var move = _moveBuffer[candidate];
                        if (!move.IsQuiet)
                        {
                            continue;
                        }

                        int score = _history.Get(move);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestIndex = candidate;
                        }
                    }

                    if (!bestIndex.HasValue)
                    {
                        break;
                    }

                    if (bestIndex != current)
                    {
                        (_moveBuffer[current], _moveBuffer[bestIndex.Value]) = (_moveBuffer[bestIndex.Value], _moveBuffer[current]);
                    }
                }
                _orderedIndex += quietCount;
                _lastQuietIndex = _orderedIndex - 1;
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
