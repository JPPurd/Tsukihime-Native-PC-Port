using Tsuki.Data.Trophies;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;
using Tsuki.Tests.Synthetic.Ui;
using Tsuki.Ui;

namespace Tsuki.Tests.Synthetic.Trophies;

/// <summary>
/// v0.1.9, the icons of the achievements without a game: the table of 30 squares, the frame (ring, gap, rounded picture), the area-average reducer, the tiles
/// that stand in for an icon, and the store the game's notice reads its icon from. Every picture here is made by the test.
/// </summary>
public sealed class TrophyIconTests
{
    private static readonly TrophyGrade[] Grades = [TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze];

    private static (int R, int G, int B, int A) At(byte[] rgba, int size, int x, int y)
    {
        int o = ((y * size) + x) * 4;
        return (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
    }

    private static (int R, int G, int B, int A) Colour(uint rgb, int alpha = 255) => ((int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF, alpha);

    private static byte[] Flat(int side, byte r, byte g, byte b, byte a)
    {
        byte[] rgba = new byte[side * side * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, a);
        }

        return rgba;
    }

    // left half red, right half blue; the top quarter of both lighter (green added), so a flip either way shows
    private static byte[] Quarters(int side)
    {
        byte[] rgba = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int o = ((y * side) + x) * 4;
                rgba[o] = (byte)(x < side / 2 ? 220 : 0);
                rgba[o + 1] = (byte)(y < side / 4 ? 180 : 0);
                rgba[o + 2] = (byte)(x < side / 2 ? 0 : 220);
                rgba[o + 3] = 255;
            }
        }

        return rgba;
    }

    // -- the table ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTableHasOneSaneRowForEachOfThe30Trophies()
    {
        Assert.Equal(1, TrophyIconTable.Version);
        Assert.Equal(30, TrophyTable.Count);
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            TrophyIconSource row = TrophyIconTable.Source(id);
            Assert.True(row.X >= 0 && row.Y >= 0, $"trophy {id}: the square starts at {row.X}, {row.Y}");

            // a square smaller than the launcher's icon would be enlarged; none is larger than the pictures are
            Assert.InRange(row.Side, TrophyIconArt.LauncherSize, 4096);
            Assert.InRange(row.X + row.Side, row.Side, 16384);
            Assert.InRange(row.Y + row.Side, row.Side, 16384);
            Assert.True(row.Ground <= 0xFFFFFF, $"trophy {id}: the ground is not 0xRRGGBB");
            if (row.Kind == TrophyIconKind.Picture)
            {
                Assert.Matches("^[A-Z0-9_]{4,24}$", row.Name);      // a name of the name tables, without the extension
                Assert.Equal((0, 0), (row.Sheet, row.Child));
            }
            else
            {
                Assert.Equal(TrophyIconKind.UiSheet, row.Kind);
                Assert.Equal("", row.Name);
                Assert.True(Enum.IsDefined((UiSet)row.Sheet), $"trophy {id}: sheet {row.Sheet} is not a UI set");
                Assert.InRange(row.Child, 0, 255);
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconTable.Source(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconTable.Source(30));
    }

    [Fact]
    public void NoTwoTrophiesShareAPicture()
    {
        var rows = Enumerable.Range(0, TrophyTable.Count).Select(TrophyIconTable.Source).ToArray();

        // the same picture and square would be the same icon twice; the approved set does not even use a picture twice
        Assert.Equal(rows.Length, rows.Distinct().Count());
        Assert.Equal(rows.Length, rows.Select(r => (r.Kind, r.Name, r.Sheet, r.Child)).Distinct().Count());
    }

    // -- the frame ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(80, 2, 1, 74)]
    [InlineData(240, 6, 3, 222)]
    public void TheFrameIsARingAGapAndThePictureWithRoundedCorners(int size, int ring, int gap, int picture)
    {
        Assert.Equal(size, 2 * (ring + gap) + picture);
        foreach (TrophyGrade grade in Grades)
        {
            byte[] framed = TrophyIconArt.Frame(Flat(300, 10, 200, 90, 255), 300, grade, size);
            Assert.Equal(size * size * 4, framed.Length);
            var ringColour = Colour(TrophyIconArt.RingColor(grade));
            int middle = size / 2;
            int last = size - 1;

            // along the middle of each side, from the outside in: the ring in the grade's colour, the gap nothing at all, then the picture
            foreach ((int dx, int dy, int x0, int y0) in new[] { (0, 1, middle, 0), (0, -1, middle, last), (1, 0, 0, middle), (-1, 0, last, middle) })
            {
                for (int i = 0; i < ring + gap + 3; i++)
                {
                    var pixel = At(framed, size, x0 + (i * dx), y0 + (i * dy));
                    var expected = i < ring ? ringColour : i < ring + gap ? (0, 0, 0, 0) : (10, 200, 90, 255);
                    Assert.True(expected == pixel, $"size {size}, {grade}, step {i} from the edge at {x0},{y0}: {pixel}, expected {expected}");
                }
            }

            // the corners are cut round: nothing at the corner itself, the ring a little way in on the diagonal, the picture further in
            foreach ((int cx, int cy, int sx, int sy) in new[] { (0, 0, 1, 1), (last, 0, -1, 1), (0, last, 1, -1), (last, last, -1, -1) })
            {
                Assert.Equal((0, 0, 0, 0), At(framed, size, cx, cy));
                Assert.Equal((0, 0, 0, 0), At(framed, size, cx + sx, cy + sy));
                int onRing = size == 80 ? 3 : 10;
                Assert.Equal(ringColour, At(framed, size, cx + (onRing * sx), cy + (onRing * sy)));
                int inPicture = size == 80 ? 6 : 17;
                Assert.Equal((10, 200, 90, 255), At(framed, size, cx + (inPicture * sx), cy + (inPicture * sy)));
            }

            Assert.Equal((10, 200, 90, 255), At(framed, size, middle, middle));
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(240)]
    public void TheCornersAreRoundAndSoftWithNoColourOfTheirOwn(int size)
    {
        // radii: 13 % of the side outside, less the ring and the gap for the picture (10 and 7 at 80, 31 and 22 at 240)
        int radius = size == 80 ? 10 : 31;
        byte[] framed = TrophyIconArt.Frame(Flat(64, 250, 250, 250, 255), 64, TrophyGrade.Bronze, size);
        var ring = Colour(TrophyIconArt.RingColor(TrophyGrade.Bronze));
        int soft = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var (r, g, b, a) = At(framed, size, x, y);
                double fromCentre = Math.Sqrt(Math.Pow(Math.Min(x, size - 1 - x) + 0.5 - radius, 2) + Math.Pow(Math.Min(y, size - 1 - y) + 0.5 - radius, 2));
                bool inCorner = Math.Min(x, size - 1 - x) < radius && Math.Min(y, size - 1 - y) < radius;
                if (inCorner && fromCentre > radius + 0.75)
                {
                    Assert.True((r, g, b, a) == (0, 0, 0, 0), $"size {size}: {x},{y} lies outside the round corner and is {(r, g, b, a)}");
                }

                if (a is > 0 and < 255)
                {
                    // a soft pixel is on a corner, and keeps the colour of what covers it: the ring's, the picture's, or between the two; never darker or lighter
                    soft++;
                    Assert.True(inCorner, $"size {size}: {x},{y} is half covered but on a straight side");
                    Assert.InRange(r, ring.R, 250);
                    Assert.InRange(g, ring.G, 250);
                    Assert.InRange(b, ring.B, 250);
                }
                else if (a == 0)
                {
                    Assert.Equal((0, 0, 0), (r, g, b));
                }
            }
        }

        Assert.True(soft >= 16, $"size {size}: {soft} half covered pixels; the corners are not antialiased");
    }

    [Fact]
    public void TheFrameKeepsThePictureTheRightWayRound()
    {
        const int Size = 240;
        byte[] framed = TrophyIconArt.Frame(Quarters(400), 400, TrophyGrade.Gold, Size);
        Assert.Equal((220, 180, 0, 255), At(framed, Size, 60, 30));          // top left: red, lighter
        Assert.Equal((0, 180, 220, 255), At(framed, Size, 180, 30));         // top right: blue, lighter
        Assert.Equal((220, 0, 0, 255), At(framed, Size, 60, 200));           // bottom left
        Assert.Equal((0, 0, 220, 255), At(framed, Size, 180, 200));          // bottom right
    }

    [Fact]
    public void ATransparentPictureLeavesOnlyTheRing()
    {
        byte[] framed = TrophyIconArt.Frame(Flat(100, 255, 255, 255, 0), 100, TrophyGrade.Silver, 80);
        Assert.Equal(Colour(TrophyIconArt.RingColor(TrophyGrade.Silver)), At(framed, 80, 40, 0));
        Assert.Equal((0, 0, 0, 0), At(framed, 80, 40, 40));
        Assert.Equal((0, 0, 0, 0), At(framed, 80, 3, 40));

        // half transparent stays half transparent, with its own colour
        byte[] half = TrophyIconArt.Frame(Flat(100, 40, 80, 120, 128), 100, TrophyGrade.Silver, 80);
        Assert.Equal((40, 80, 120, 128), At(half, 80, 40, 40));
    }

    [Fact]
    public void TheFrameRefusesWhatIsNotASquarePictureOrASaneSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Frame(new byte[4], 1, TrophyGrade.Gold, 80));           // 1 x 1
        Assert.Throws<ArgumentException>(() => TrophyIconArt.Frame(new byte[8 * 4 * 4], 8, TrophyGrade.Gold, 80));             // 8 x 4 handed in as a square of 8
        Assert.Throws<ArgumentException>(() => TrophyIconArt.Frame(new byte[(8 * 8 * 4) + 4], 8, TrophyGrade.Gold, 80));
        Assert.Throws<ArgumentException>(() => TrophyIconArt.Frame([], 8, TrophyGrade.Gold, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Frame(new byte[16], 2, TrophyGrade.Gold, TrophyIconArt.MinSize - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Frame(new byte[16], 2, TrophyGrade.Gold, TrophyIconArt.MaxSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Frame(new byte[16], 2, (TrophyGrade)4, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Frame(new byte[16], 0, TrophyGrade.Gold, 80));

        // the smallest picture and the smallest and largest tiles are taken
        Assert.Equal(16 * 16 * 4, TrophyIconArt.Frame(new byte[16], 2, TrophyGrade.Gold, TrophyIconArt.MinSize).Length);
        Assert.Equal(2048 * 2048 * 4, TrophyIconArt.Frame(new byte[16], 2, TrophyGrade.Gold, TrophyIconArt.MaxSize).Length);
    }

    [Fact]
    public void TheRingColoursAreTheApprovedOnes()
    {
        Assert.Equal(0xCFE6FFu, TrophyIconArt.RingColor(TrophyGrade.Platinum));
        Assert.Equal(0xF0C050u, TrophyIconArt.RingColor(TrophyGrade.Gold));
        Assert.Equal(0xC0C8D4u, TrophyIconArt.RingColor(TrophyGrade.Silver));
        Assert.Equal(0xC98D5Au, TrophyIconArt.RingColor(TrophyGrade.Bronze));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.RingColor((TrophyGrade)4));
        // the notice draws its icon (or the grade's emblem) at 72 px, pixel for pixel, inside the 80 px place of its picture; the size the game asks the icon code for is this one
        Assert.Equal(72, TrophyIconArt.NoticeSize);
        Assert.Equal(TrophyIconArt.NoticeSize, Tsuki.Ui.UiTrophyNotice.IconSize);
        Assert.Equal(80, Tsuki.Ui.UiTrophyNotice.PlaceSize);
        Assert.Equal(240, TrophyIconArt.LauncherSize);
    }

    // -- the reducer --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheReducerAveragesByTheAreaEachPixelShares()
    {
        // three pixels onto two: the first takes two thirds of pixel 0 and a third of pixel 1, the second the rest
        byte[] three = [0, 30, 60, 255, 90, 120, 150, 255, 255, 210, 240, 255];
        Assert.Equal<byte>([30, 60, 90, 255, 200, 180, 210, 255], TrophyIconArt.Reduce(three, 3, 1, 2, 1));

        // the same down a column
        Assert.Equal<byte>([30, 60, 90, 255, 200, 180, 210, 255], TrophyIconArt.Reduce(three, 1, 3, 1, 2));

        // five onto two: each output pixel takes two whole pixels and half of the middle one
        byte[] five = [.. new byte[] { 10, 20, 30, 40, 50 }.SelectMany(v => new byte[] { v, v, v, 255 })];
        Assert.Equal<byte>([18, 18, 18, 255, 42, 42, 42, 255], TrophyIconArt.Reduce(five, 5, 1, 2, 1));

        // 4 x 4 in four flat blocks comes out as the four colours
        byte[] blocks = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                blocks[((y * 4) + x) * 4] = (byte)(x < 2 ? 200 : 20);
                blocks[(((y * 4) + x) * 4) + 1] = (byte)(y < 2 ? 100 : 40);
                blocks[(((y * 4) + x) * 4) + 3] = 255;
            }
        }

        Assert.Equal<byte>([200, 100, 0, 255, 20, 100, 0, 255, 200, 40, 0, 255, 20, 40, 0, 255], TrophyIconArt.Reduce(blocks, 4, 4, 2, 2));
    }

    [Fact]
    public void TheReducerKeepsAFlatPictureFlatAtAnyRatio()
    {
        foreach ((int from, int to) in new[] { (157, 74), (1563, 222), (500, 74), (223, 222), (75, 74), (74, 74), (30, 74) })
        {
            byte[] reduced = TrophyIconArt.Reduce(Flat(from, 13, 77, 201, 255), from, from, to, to);
            Assert.Equal(to * to * 4, reduced.Length);
            for (int i = 0; i < reduced.Length; i += 4)
            {
                Assert.True((13, 77, 201, 255) == (reduced[i], reduced[i + 1], reduced[i + 2], reduced[i + 3]), $"{from} to {to}: pixel {i / 4} is not the flat colour");
            }
        }
    }

    [Fact]
    public void TheReducerWeighsColourByAlphaSoATransparentPixelAddsNone()
    {
        // white that nobody sees beside red: the result is red at half cover, not pink
        Assert.Equal<byte>([200, 0, 0, 128], TrophyIconArt.Reduce([255, 255, 255, 0, 200, 0, 0, 255], 2, 1, 1, 1));

        // all transparent: nothing, whatever colour the pixels carried
        Assert.Equal<byte>([0, 0, 0, 0], TrophyIconArt.Reduce([255, 255, 255, 0, 9, 9, 9, 0], 2, 1, 1, 1));

        // a quarter and three quarters of cover: the colour leans the same way
        Assert.Equal<byte>([60, 0, 160, 160], TrophyIconArt.Reduce([0, 0, 200, 240, 240, 0, 40, 80], 2, 1, 1, 1));
    }

    [Fact]
    public void ForAWholeFactorTheReducerIsTheAlphaWeightedBoxAverage()
    {
        var random = new Random(7);
        byte[] noise = new byte[60 * 60 * 4];
        random.NextBytes(noise);
        for (int i = 3; i < noise.Length; i += 16)
        {
            noise[i] = 0;       // some pixels nobody sees
        }

        Assert.Equal(BoxAverage(noise, 60, 60, 3), TrophyIconArt.Reduce(noise, 60, 60, 20, 20));
        Assert.Equal(BoxAverage(noise, 60, 60, 4), TrophyIconArt.Reduce(noise, 60, 60, 15, 15));

        // a block whose cover rounds to nothing is 0, 0, 0, 0, whatever colour its pixels carry
        byte[] faint = new byte[3 * 3 * 4];
        faint[0] = faint[1] = faint[2] = 200;
        faint[3] = 4;                                               // 4 / 9 rounds to 0
        Assert.Equal(new byte[4], TrophyIconArt.Reduce(faint, 3, 3, 1, 1));
        Assert.Equal(new byte[4], BoxAverage(faint, 3, 3, 3));
        faint[3] = 5;                                               // 5 / 9 rounds to 1, and the colour is the one pixel's
        Assert.Equal(new byte[] { 200, 200, 200, 1 }, TrophyIconArt.Reduce(faint, 3, 3, 1, 1));

        // the same size: every pixel as it was, but one nobody sees loses the colour it carried
        byte[] same = [.. noise.Select((v, i) => noise[i | 3] == 0 ? (byte)0 : v)];
        Assert.Equal(same, TrophyIconArt.Reduce(noise, 60, 60, 60, 60));

        // The rule written out (what the trophy file's picture reducer did before v0.1.9 stopped reading that file): each output pixel is a factor x factor block.
        // Its alpha is the block's mean alpha, rounded half up; its colour is the mean of the block's colours each weighted by its alpha, rounded half up, so a
        // transparent pixel adds no colour. A block whose alpha rounds to 0 is 0, 0, 0, 0.
        static byte[] BoxAverage(byte[] rgba, int width, int height, int factor)
        {
            int outWidth = width / factor;
            int outHeight = height / factor;
            byte[] reduced = new byte[outWidth * outHeight * 4];
            long cells = (long)factor * factor;
            for (int oy = 0; oy < outHeight; oy++)
            {
                for (int ox = 0; ox < outWidth; ox++)
                {
                    long r = 0;
                    long g = 0;
                    long b = 0;
                    long a = 0;
                    for (int y = oy * factor; y < (oy + 1) * factor; y++)
                    {
                        for (int x = ox * factor; x < (ox + 1) * factor; x++)
                        {
                            int at = ((y * width) + x) * 4;
                            int alpha = rgba[at + 3];
                            r += rgba[at] * alpha;
                            g += rgba[at + 1] * alpha;
                            b += rgba[at + 2] * alpha;
                            a += alpha;
                        }
                    }

                    long cover = (a + (cells / 2)) / cells;
                    if (cover == 0)
                    {
                        continue;
                    }

                    int to = ((oy * outWidth) + ox) * 4;
                    reduced[to] = (byte)((r + (a / 2)) / a);
                    reduced[to + 1] = (byte)((g + (a / 2)) / a);
                    reduced[to + 2] = (byte)((b + (a / 2)) / a);
                    reduced[to + 3] = (byte)cover;
                }
            }

            return reduced;
        }
    }

    [Fact]
    public void TheReducerEnlargesByRepeatingPixelsAndRefusesBadInput()
    {
        byte[] two = [10, 20, 30, 255, 40, 50, 60, 255];
        Assert.Equal<byte>([10, 20, 30, 255, 10, 20, 30, 255, 40, 50, 60, 255, 40, 50, 60, 255], TrophyIconArt.Reduce(two, 2, 1, 4, 1));
        Assert.Equal<byte>([.. Enumerable.Repeat(new byte[] { 1, 2, 3, 255 }, 9).SelectMany(p => p)], TrophyIconArt.Reduce([1, 2, 3, 255], 1, 1, 3, 3));
        Assert.Throws<ArgumentException>(() => TrophyIconArt.Reduce(new byte[12], 2, 2, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Reduce([], 0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Reduce(new byte[4], 1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Reduce(new byte[4], 1, 1, 1, 16385));
    }

    // -- the tiles that stand in for an icon --------------------------------------------------------------------------

    [Theory]
    [InlineData(80)]
    [InlineData(240)]
    public void TheLockedTileIsADarkGroundWithAPadlockAndAMutedRingAndDiffersByGradeInTheRingOnly(int size)
    {
        var ground = Colour(0x161C2B);
        var padlock = Colour(0x5B6780);
        int inset = size == 80 ? 3 : 9;
        byte[][] tiles = [.. Grades.Select(g => TrophyIconArt.Locked(g, size))];
        for (int k = 0; k < Grades.Length; k++)
        {
            byte[] tile = tiles[k];
            Assert.Equal(size * size * 4, tile.Length);

            // the ring: the grade's colour at 35 %; the corner nothing
            Assert.Equal(Colour(TrophyIconArt.RingColor(Grades[k]), 89), At(tile, size, size / 2, 0));
            Assert.Equal((0, 0, 0, 0), At(tile, size, 0, 0));
            Assert.Equal((0, 0, 0, 0), At(tile, size, size / 2, inset - 1));

            // the ground, the padlock's body, the hole in it, the shackle's top and the air under the shackle
            Assert.Equal(ground, At(tile, size, size / 2, inset));
            Assert.Equal(ground, At(tile, size, inset + 2, size / 2));
            Assert.Equal(padlock, At(tile, size, (int)(size * 0.40), (int)(size * 0.60)));
            Assert.Equal(padlock, At(tile, size, (int)(size * 0.60), (int)(size * 0.60)));
            Assert.Equal(ground, At(tile, size, size / 2, (int)(size * 0.555)));
            Assert.Equal(padlock, At(tile, size, size / 2, (int)(size * 0.32)));
            Assert.Equal(ground, At(tile, size, size / 2, (int)(size * 0.43)));

            // about 40 % of the side high, in the middle (looked for clear of the round corners, where the ring passes)
            int radius = size == 80 ? 10 : 31;
            int[] rows = [.. Enumerable.Range(radius, size - (2 * radius)).Where(y => Enumerable.Range(radius, size - (2 * radius)).Any(x => At(tile, size, x, y) != ground))];
            Assert.InRange((rows[^1] - rows[0] + 1) / (double)size, 0.37, 0.43);
            Assert.InRange((rows[0] + rows[^1] + 1) / 2.0 / size, 0.48, 0.52);
            int[] columns = [.. Enumerable.Range(radius, size - (2 * radius)).Where(x => rows.Any(y => At(tile, size, x, y) != ground))];
            Assert.InRange((columns[0] + columns[^1] + 1) / 2.0 / size, 0.49, 0.51);
            Assert.InRange((columns[^1] - columns[0] + 1) / (double)size, 0.27, 0.33);

            // two grades: the same inside the ring (which runs through the corners of the picture's square), another colour in it
            for (int y = inset; y < size - inset; y++)
            {
                for (int x = inset; x < size - inset; x++)
                {
                    bool corner = Math.Min(x, size - 1 - x) < radius && Math.Min(y, size - 1 - y) < radius;
                    Assert.True(corner || At(tile, size, x, y) == At(tiles[0], size, x, y), $"{Grades[k]}: {x},{y} differs inside the ring");
                }
            }

            Assert.True(k == 0 || !tile.AsSpan().SequenceEqual(tiles[0]));
            Assert.True(k == 0 || At(tile, size, size / 2, 1) != At(tiles[0], size, size / 2, 1));
        }
    }

    [Theory]
    [InlineData(80)]
    [InlineData(240)]
    public void TheBlankTileIsTheSameGroundWithTheFullRingAndNoPadlock(int size)
    {
        int inset = size == 80 ? 3 : 9;
        foreach (TrophyGrade grade in Grades)
        {
            byte[] blank = TrophyIconArt.Blank(grade, size);
            Assert.Equal(size * size * 4, blank.Length);

            // exactly the frame of a flat picture in the ground's colour: ring, gap, corners and all
            Assert.Equal(TrophyIconArt.Frame(Flat(8, 0x16, 0x1C, 0x2B, 255), 8, grade, size), blank);
            Assert.Equal(Colour(TrophyIconArt.RingColor(grade)), At(blank, size, size / 2, 0));
            Assert.Equal(Colour(0x161C2B), At(blank, size, size / 2, size / 2));

            // the locked tile is this with the padlock on it and the ring muted
            byte[] locked = TrophyIconArt.Locked(grade, size);
            Assert.Equal(At(blank, size, size / 2, inset), At(locked, size, size / 2, inset));
            Assert.NotEqual(At(blank, size, (int)(size * 0.40), (int)(size * 0.60)), At(locked, size, (int)(size * 0.40), (int)(size * 0.60)));
            Assert.NotEqual(At(blank, size, size / 2, 0), At(locked, size, size / 2, 0));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Blank((TrophyGrade)7, size));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Locked((TrophyGrade)7, size));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Blank(TrophyGrade.Gold, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Locked(TrophyGrade.Gold, 2049));
    }

    // -- the grade's emblem (the owner's choice: a cup drawn by the port, in place of the PS4's cup pictures) -------------------------------------------

    [Theory]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(52)]
    [InlineData(60)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(240)]
    public void TheEmblemIsACupDrawnInTheGradesColourOnTheBlankTile(int size)
    {
        var ground = Colour(0x161C2B);
        foreach (TrophyGrade grade in Grades)
        {
            byte[] emblem = TrophyIconArt.Emblem(grade, size);
            byte[] blank = TrophyIconArt.Blank(grade, size);
            Assert.Equal(size * size * 4, emblem.Length);

            // the frame of the tiles: the grade's full ring, transparent corners and gap, the ground inside
            int inset = Inset(size);
            Assert.Equal(Colour(TrophyIconArt.RingColor(grade)), At(emblem, size, size / 2, 0));
            Assert.Equal(Colour(TrophyIconArt.RingColor(grade)), At(emblem, size, 0, size / 2));
            Assert.Equal(Colour(TrophyIconArt.RingColor(grade)), At(emblem, size, size - 1, size / 2));
            Assert.Equal(Colour(TrophyIconArt.RingColor(grade)), At(emblem, size, size / 2, size - 1));
            Assert.Equal((0, 0, 0, 0), At(emblem, size, 0, 0));
            Assert.Equal((0, 0, 0, 0), At(emblem, size, size - 1, 0));
            Assert.Equal((0, 0, 0, 0), At(emblem, size, 0, size - 1));
            Assert.Equal((0, 0, 0, 0), At(emblem, size, size - 1, size - 1));
            Assert.Equal((0, 0, 0, 0), At(emblem, size, size / 2, inset - 1));
            Assert.Equal(ground, At(emblem, size, size / 2, inset));
            Assert.Equal(ground, At(emblem, size, inset + 1, size / 2));

            // outside the cup's box (0.20 to 0.80 of the side across, 0.24 to 0.79 down) it is the blank tile, pixel for pixel
            int left = (int)(0.18 * size);
            int right = (int)Math.Ceiling(0.82 * size);
            int top = (int)(0.22 * size);
            int bottom = (int)Math.Ceiling(0.81 * size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (x < left || x >= right || y < top || y >= bottom)
                    {
                        Assert.True(At(blank, size, x, y) == At(emblem, size, x, y), $"{grade} at {size}: {x},{y} is not the blank tile's");
                    }
                }
            }

            // the middle column runs through the rim, the bowl, the stem and the base: brighter than the ground all the way, lighter at the top than at the
            // bottom, in the sketch's gradient (the grade's colour mixed 45 % with white at the top of the tile, 72 % of it at the bottom)
            int middle = size / 2;
            int first = (int)Math.Ceiling(0.26 * size);
            int last = (int)(0.77 * size);
            for (int y = first; y < last; y++)
            {
                Assert.True(Light(At(emblem, size, middle, y)) > Light(ground) + 60, $"{grade} at {size}: the middle column at {y} is not the cup");
                Assert.Equal(255, At(emblem, size, middle, y).A);
                var expected = Gradient(TrophyIconArt.RingColor(grade), (y + 0.5) / size);
                var seen = At(emblem, size, middle, y);
                Assert.InRange(seen.R, expected.R - 1, expected.R + 1);
                Assert.InRange(seen.G, expected.G - 1, expected.G + 1);
                Assert.InRange(seen.B, expected.B - 1, expected.B + 1);
            }

            Assert.True(Light(At(emblem, size, middle, first)) > Light(At(emblem, size, middle, last - 1)) + 20);

            // above the rim and under the base the ground again; the handles are rings: the ground in their eye, the cup's colour on their outer side
            Assert.Equal(ground, At(emblem, size, middle, (int)(0.20 * size)));
            Assert.Equal(ground, At(emblem, size, middle, (int)Math.Ceiling(0.80 * size)));
            foreach (int x in new[] { (int)(0.28 * size), size - 1 - (int)(0.28 * size) })
            {
                Assert.Equal(ground, At(emblem, size, x, (int)(0.36 * size)));
            }

            foreach (int x in new[] { (int)(0.21 * size), size - 1 - (int)(0.21 * size) })
            {
                Assert.True(Light(At(emblem, size, x, (int)(0.36 * size))) > Light(ground) + 40, $"{grade} at {size}: no handle at {x}");
            }

            // and the stem is narrower than the bowl and the base
            int stemRow = (int)(0.62 * size);
            int bowlRow = (int)(0.27 * size);
            int baseRow = (int)(0.765 * size);
            Assert.True(Width(emblem, size, stemRow, ground) < Width(emblem, size, bowlRow, ground) / 3);
            Assert.True(Width(emblem, size, stemRow, ground) < Width(emblem, size, baseRow, ground) / 3);
        }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(240)]
    public void TheFourEmblemsDifferOnlyInColour(int size)
    {
        var ground = Colour(0x161C2B);
        byte[][] emblems = [.. Grades.Select(g => TrophyIconArt.Emblem(g, size))];
        for (int k = 1; k < Grades.Length; k++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // the same shape: the same alpha everywhere, and the ground where the first has the ground
                    var a = At(emblems[0], size, x, y);
                    var b = At(emblems[k], size, x, y);
                    Assert.True(a.A == b.A, $"{Grades[k]} at {size}: {x},{y} has another alpha");
                    Assert.True((a == ground) == (b == ground), $"{Grades[k]} at {size}: {x},{y} is the ground in one and not in the other");
                }
            }

            // another colour on the ring and on the cup
            Assert.NotEqual(At(emblems[0], size, size / 2, 0), At(emblems[k], size, size / 2, 0));
            Assert.NotEqual(At(emblems[0], size, size / 2, size / 2), At(emblems[k], size, size / 2, size / 2));
        }

        // made again, the same pixels
        Assert.Equal(emblems[1], TrophyIconArt.Emblem(Grades[1], size));
    }

    [Fact]
    public void TheEmblemRefusesWhatTheTilesRefuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Emblem((TrophyGrade)7, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Emblem((TrophyGrade)(-1), 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Emblem(TrophyGrade.Gold, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Emblem(TrophyGrade.Gold, 2049));

        // the smallest and the largest tile are drawn
        Assert.Equal(16 * 16 * 4, TrophyIconArt.Emblem(TrophyGrade.Bronze, TrophyIconArt.MinSize).Length);
        Assert.Equal(2048 * 2048 * 4, TrophyIconArt.Emblem(TrophyGrade.Platinum, TrophyIconArt.MaxSize).Length);
    }

    [Fact]
    public void TheEmblemCanBeDrawnOnManyThreadsAtOnce()
    {
        byte[] alone = TrophyIconArt.Emblem(TrophyGrade.Silver, 80);
        var drawn = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ => drawn.Add(TrophyIconArt.Emblem(TrophyGrade.Silver, 80)));
        Assert.Equal(32, drawn.Count);
        Assert.All(drawn, d => Assert.Equal(alone, d));
    }

    // the ring and the gap of the tiles: ring = size / 40 rounded (at least 2), gap = half of it (at least 1)
    private static int Inset(int size)
    {
        int ring = Math.Max(2, (size + 20) / 40);
        return ring + Math.Max(1, ring / 2);
    }

    private static double Light((int R, int G, int B, int A) c) => (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);

    private static (int R, int G, int B) Gradient(uint rgb, double t)
    {
        int Channel(int shift)
        {
            double value = (rgb >> shift) & 0xFF;
            double light = value + ((255 - value) * 0.45);
            return (int)Math.Round(light + (((value * 0.72) - light) * t));
        }

        return (Channel(16), Channel(8), Channel(0));
    }

    // how many pixels of a row are not the ground
    private static int Width(byte[] rgba, int size, int y, (int R, int G, int B, int A) ground) => Enumerable.Range(0, size).Count(x => At(rgba, size, x, y) != ground && At(rgba, size, x, y).A == 255 && x > size / 8 && x < size - (size / 8));

    // -- the game's store ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheIconIdsAreARangeOf30AfterTheEmblems()
    {
        Assert.Equal(32, UiComposedId.TrophyIconFirst);
        Assert.Equal(32, UiComposedId.TrophyIcon(0));
        Assert.Equal(61, UiComposedId.TrophyIcon(29));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiComposedId.TrophyIcon(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiComposedId.TrophyIcon(30));
        for (int id = -4; id < 80; id++)
        {
            Assert.Equal(id is >= 32 and <= 61, UiComposedId.IsTrophyIcon(id));
            Assert.False(UiComposedId.IsTrophyIcon(id) && UiComposedId.IsTrophyEmblem(id));
        }

        Assert.False(UiComposedId.IsTrophyIcon(UiComposedId.TitleExit));
        Assert.False(UiComposedId.IsTrophyIcon(UiComposedId.TitleExitCursor));
        Assert.All(Grades, g => Assert.False(UiComposedId.IsTrophyIcon(UiComposedId.TrophyEmblem(g))));
    }

    [Fact]
    public void TheAssetsKeepTheHostsIconPerTrophyAndKnowWhenOneIsSettled()
    {
        var assets = new UiAssets(UiTestKit.Data(), Language.En);
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            Assert.False(assets.TrophyIconSettled(id));
            Assert.Null(assets.Composed(UiComposedId.TrophyIcon(id)));
        }

        // asking did not settle anything
        Assert.False(assets.TrophyIconSettled(5));

        var five = new UiComposedPicture(80, 80, new byte[80 * 80 * 4]);
        assets.SetTrophyIcon(5, five);
        Assert.True(assets.TrophyIconSettled(5));
        Assert.Same(five, assets.Composed(UiComposedId.TrophyIcon(5)));
        Assert.Same(five, assets.Composed(UiComposedId.TrophyIcon(5), Language.Ja));       // the same in every language
        Assert.False(assets.TrophyIconSettled(4));
        Assert.False(assets.TrophyIconSettled(6));
        Assert.Null(assets.Composed(UiComposedId.TrophyIcon(4)));
        Assert.Null(assets.Composed(UiComposedId.TrophyIcon(6)));

        // "settled, there is none": the notice stops waiting and draws the grade's emblem
        assets.SetTrophyIcon(29, null);
        Assert.True(assets.TrophyIconSettled(29));
        Assert.Null(assets.Composed(UiComposedId.TrophyIcon(29)));

        // the first and the last id, and a picture that replaces one
        var zero = new UiComposedPicture(80, 80, new byte[80 * 80 * 4]);
        assets.SetTrophyIcon(0, zero);
        assets.SetTrophyIcon(29, zero);
        Assert.Same(zero, assets.Composed(UiComposedId.TrophyIcon(0)));
        Assert.Same(zero, assets.Composed(UiComposedId.TrophyIcon(29)));
        Assert.True(assets.TrophyIconSettled(0));

        // the emblems are no part of the host's store (the port draws them, the same for every UI), and a language switch keeps the icons
        Assert.All(Grades, g => Assert.Equal(TrophyIconArt.NoticeSize, assets.Composed(UiComposedId.TrophyEmblem(g))!.Width));
        Assert.All(Grades, g => Assert.NotSame(five, assets.Composed(UiComposedId.TrophyEmblem(g))));
        Assert.Same(zero, assets.Composed(UiComposedId.TrophyIcon(0)));
        assets.SetLanguage(Language.Ja);
        Assert.Same(five, assets.Composed(UiComposedId.TrophyIcon(5)));
        Assert.True(assets.TrophyIconSettled(5));
        Assert.True(assets.TrophyIconSettled(29));

        Assert.Throws<ArgumentOutOfRangeException>(() => assets.SetTrophyIcon(-1, five));
        Assert.Throws<ArgumentOutOfRangeException>(() => assets.SetTrophyIcon(30, five));
        Assert.False(assets.TrophyIconSettled(-1));
        Assert.False(assets.TrophyIconSettled(30));
    }
}
