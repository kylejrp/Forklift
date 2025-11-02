using Xunit;
using FluentAssertions;
using Forklift.Core;

namespace Forklift.Testing
{
    public class MakeUnmakeTests
    {
        [Fact]
        public void MakeThenUnmake_RestoresZobristAndOccupancy()
        {
            var b = BoardFactory.FromFenOrStart("startpos");
            var key0 = b.ZKey; var occ0 = b.OccAll;

            var m = MovePicker.FirstLegal(b); // simple helper in Core
            var undo = b.MakeMove(m);
            b.UnmakeMove(m, undo);

            b.ZKey.Should().Be(key0);
            b.OccAll.Should().Be(occ0);
            b.HashHistoryCount.Should().Be(0); // if you maintain it incrementally
        }
    }
}
