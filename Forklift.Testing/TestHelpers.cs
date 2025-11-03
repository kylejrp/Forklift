using Forklift.Core;

namespace Forklift.Testing
{
    public static class TestHelpers
    {
        public static Square0x88 Sq88(string alg) => Squares.ParseAlgebraicTo0x88(alg.AsSpan());
    }
}
