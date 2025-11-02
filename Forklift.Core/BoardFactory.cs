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
                var b = new Board();
                b.SetStartPosition();
                return b;
            }

            return FromFen(fenOrStart);
        }

        public static Board FromFen(string fen)
        {
            // FEN example: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) throw new ArgumentException("Invalid FEN");

            string boardPart = parts[0];
            bool whiteToMove = parts.Length <= 1 || parts[1] == "w";

            var b = new Board();
            b.Clear();

            var ranks = boardPart.Split('/');
            if (ranks.Length != 8) throw new ArgumentException("Invalid FEN ranks");

            for (int rank = 7; rank >= 0; rank--)
            {
                string row = ranks[7 - rank];
                int file = 0;

                foreach (char c in row)
                {
                    if (char.IsDigit(c))
                    {
                        file += c - '0';
                        continue;
                    }

                    if (file >= 8) throw new ArgumentException("Invalid FEN placement");
                    var sq88 = (rank << 4) | file;

                    Piece piece = Piece.FromFENChar(c);

                    b.Place(Squares.ToAlgebraic(new Square0x88(sq88)), piece);
                    file++;
                }
            }

            b.SetSideToMove(whiteToMove ? Color.White : Color.Black);
            return b;
        }
    }
}
