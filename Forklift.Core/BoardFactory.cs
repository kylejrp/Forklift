using Forklift.Core;
using System;
using System.Collections.Generic;

namespace Forklift.Core
{
    public static class BoardFactory
    {
        public static Board FromFenOrStart(string fenOrStart)
        {
            if (fenOrStart.Equals("startpos", StringComparison.OrdinalIgnoreCase))
            {
                var b = new Board(startPosition: true);
                return b;
            }

            return FromFen(fenOrStart);
        }

        public static Board FromFen(string fen)
        {
            var board = new Board();
            board.SetPositionFromFEN(fen);
            return board;
        }
    }
}
