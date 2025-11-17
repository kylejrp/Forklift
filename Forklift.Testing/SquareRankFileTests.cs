using Forklift.Core;

namespace Forklift.Testing
{
    public class SquareRankFileTests
    {
        public static readonly Square0x88[] KnownGoodSquare0x88 = {
            new (0x00), new (0x01), new (0x02), new (0x03),
            new (0x04), new (0x05), new (0x06), new (0x07),
            new (0x10), new (0x11), new (0x12), new (0x13),
            new (0x14), new (0x15), new (0x16), new (0x17),
            new (0x20), new (0x21), new (0x22), new (0x23),
            new (0x24), new (0x25), new (0x26), new (0x27),
            new (0x30), new (0x31), new (0x32), new (0x33),
            new (0x34), new (0x35), new (0x36), new (0x37),
            new (0x40), new (0x41), new (0x42), new (0x43),
            new (0x44), new (0x45), new (0x46), new (0x47),
            new (0x50), new (0x51), new (0x52), new (0x53),
            new (0x54), new (0x55), new (0x56), new (0x57),
            new (0x60), new (0x61), new (0x62), new (0x63),
            new (0x64), new (0x65), new (0x66), new (0x67),
            new (0x70), new (0x71), new (0x72), new (0x73),
            new (0x74), new (0x75), new (0x76), new (0x77)
        };

        public static readonly Square0x64[] KnownGoodSquare0x64 =
        {
            new (0), new (1), new (2), new (3), new (4), new (5), new (6), new (7),
            new (8), new (9), new (10), new (11), new (12), new (13), new (14), new (15),
            new (16), new (17), new (18), new (19), new (20), new (21), new (22), new (23),
            new (24), new (25), new (26), new (27), new (28), new (29), new (30), new (31),
            new (32), new (33), new (34), new (35), new (36), new (37), new (38), new (39),
            new (40), new (41), new (42), new (43), new (44), new (45), new (46), new (47),
            new (48), new (49), new (50), new (51), new (52), new (53), new (54), new (55),
            new (56), new (57), new (58), new (59), new (60), new (61), new (62), new (63)
        };

        public static readonly int[] KnownGoodRanks =
        {
            0,0,0,0,0,0,0,0,
            1,1,1,1,1,1,1,1,
            2,2,2,2,2,2,2,2,
            3,3,3,3,3,3,3,3,
            4,4,4,4,4,4,4,4,
            5,5,5,5,5,5,5,5,
            6,6,6,6,6,6,6,6,
            7,7,7,7,7,7,7,7
        };

        public static readonly int[] KnownGoodFiles =
        {
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7,
            0,1,2,3,4,5,6,7
        };

        public static IEnumerable<object[]> SquareIndices
        {
            get
            {
                for (int i = 0; i < 64; i++)
                {
                    yield return new object[] { i };
                }
            }
        }

        [Theory]
        [MemberData(nameof(SquareIndices))]
        public void Square0x88_RankAndFileAreCorrect(int index)
        {
            var sq88 = KnownGoodSquare0x88[index];
            var expectedRank = KnownGoodRanks[index];
            var expectedFile = KnownGoodFiles[index];

            Assert.Equal(expectedRank, sq88.Rank);
            Assert.Equal(expectedFile, sq88.File);
        }

        [Theory]
        [MemberData(nameof(SquareIndices))]
        public void Square0x64_RankAndFileAreCorrect(int index)
        {
            var sq64 = KnownGoodSquare0x64[index];
            var expectedRank = KnownGoodRanks[index];
            var expectedFile = KnownGoodFiles[index];

            Assert.Equal(expectedRank, sq64.Rank);
            Assert.Equal(expectedFile, sq64.File);
        }
    }
}
