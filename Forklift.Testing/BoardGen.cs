using System;
using System.Collections.Generic;
using System.Linq;          // for .Select / .SelectMany on Gen<T>
using System.Numerics;
using Forklift.Core;
using FsCheck;
using FsCheck.Fluent;

// Register generators for FsCheck.Xunit in THIS assembly (the test assembly)
[assembly: FsCheck.Xunit.Properties(Arbitrary = new[] { typeof(Forklift.Testing.BoardGen) })]

namespace Forklift.Testing
{
    public static class BoardGen
    {
        public sealed class RandomPosition
        {
            public Board Value { get; }
            internal RandomPosition(Board b) => Value = b;
            public override string ToString() => "RandomPosition";
        }

        public sealed class LegalPosition
        {
            public Board Value { get; }
            internal LegalPosition(Board b) => Value = b;
            public override string ToString() => "LegalPosition";
        }

        // Expose as Arbitrary<T> using the *fluent* Arb.From(...)
        public static Arbitrary<RandomPosition> RandomPositionArb()
            => Arb.From(GenRandomPosition());

        public static Arbitrary<LegalPosition> LegalPositionArb()
            => Arb.From(GenLegalPosition());

        private static Gen<RandomPosition> GenRandomPosition()
        {
            var depthGen =
                Gen.Choose(0, 23)          // 0..23 plies
                   .Select(BiasDepth);      // bias to smaller depths for test speed

            return from depth in depthGen
                   from board in GenFromStartWithRandomLegalPlies(depth, requireNonStalemate: false)
                   select new RandomPosition(board);
        }

        private static Gen<LegalPosition> GenLegalPosition()
        {
            var depthGen =
                Gen.Choose(0, 23)
                   .Select(BiasDepth);

            return from depth in depthGen
                   from board in GenFromStartWithRandomLegalPlies(depth, requireNonStalemate: true)
                   select new LegalPosition(board);
        }

        private static int BiasDepth(int n)
        {
            if (n <= 3) return n;
            if (n <= 8) return n / 2;
            return 8 + (n - 8) / 4;
        }

        private static Gen<Board> GenFromStartWithRandomLegalPlies(int plies, bool requireNonStalemate)
        {
            return Gen.Sized(_ => Gen.Constant(plies))
                      .SelectMany(plyCount =>
                          GenConstant(() =>
                          {
                              var b = new Board(startPosition: true);
                              var rng = new Random(SeedFromZKey(b.ZKey, plyCount));

                              for (int i = 0; i < plyCount; i++)
                              {
                                  if (!TryPickRandomLegalMove(b, rng, out var mv))
                                      break; // stalemate/mate reached; still a legal position

                                  var u = b.MakeMove(mv);
                                  if (IsOwnKingInCheck(b))
                                  {
                                      b.UnmakeMove(mv, u);
                                      break;
                                  }
                              }

                              if (!HasExactlyOneKingEach(b)) return new Board(startPosition: true);
                              if (IsOwnKingInCheck(b)) return new Board(startPosition: true);

                              if (requireNonStalemate && !HasAnyLegalMove(b))
                                  return new Board(startPosition: true);

                              return b;
                          })
                      );
        }

        // --- helpers ---

        private static bool HasExactlyOneKingEach(Board b)
        {
            ulong wk = b.GetPieceBitboard(Piece.WhiteKing);
            ulong bk = b.GetPieceBitboard(Piece.BlackKing);
            return wk != 0 && bk != 0
                   && (wk & (wk - 1)) == 0
                   && (bk & (bk - 1)) == 0;
        }

        private static bool IsOwnKingInCheck(Board b)
        {
            var kingSq64 = b.FindKingSq64(color: b.SideToMove);
            return b.IsSquareAttacked(kingSq64, bySide: b.SideToMove.Flip());
        }

        private static bool HasAnyLegalMove(Board b)
        {
            var moves = MoveGeneration.GeneratePseudoLegal(b, b.SideToMove);
            foreach (var mv in moves)
            {
                var u = b.MakeMove(mv);
                bool ok = !IsOwnKingInCheck(b);
                b.UnmakeMove(mv, u);
                if (ok) return true;
            }
            return false;
        }

        private static bool TryPickRandomLegalMove(Board b, Random rng, out Board.Move move)
        {
            var moves = MoveGeneration.GeneratePseudoLegal(b, b.SideToMove);
            if (moves.Length == 0) { move = default; return false; }

            int start = rng.Next(moves.Length);
            for (int i = 0; i < moves.Length; i++)
            {
                var mv = moves[(start + i) % moves.Length];
                var u = b.MakeMove(mv);
                bool ok = !IsOwnKingInCheck(b);
                b.UnmakeMove(mv, u);
                if (ok) { move = mv; return true; }
            }

            move = default;
            return false;
        }

        private static int SeedFromZKey(ulong key, int salt)
        {
            unchecked
            {
                uint x = (uint)(key ^ (key >> 32));
                x ^= (uint)(salt * 0x9E3779B9u);
                return (int)(x == 0 ? 1 : x);
            }
        }

        private static Gen<T> GenConstant<T>(Func<T> make) =>
            Gen.Sized(_ => Gen.Constant(0)).Select(_ => make());
    }
}
