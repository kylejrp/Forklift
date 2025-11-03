using Forklift.Core;

namespace Forklift.Testing
{
    public static class TestHelpers
    {
        public static Square0x88 _(string alg) => Squares.ParseAlgebraicTo0x88(alg.AsSpan());
    }
}
