// The icon of an achievement (v0.1.9): a square of one of the player's own pictures (TrophyIconTable), reduced and framed with a ring in the grade's colour.
// The game's notice and both launchers call the same code, so the icon looks the same everywhere. Nothing here is written to disk and nothing is kept:
// callers cache what they asked for.
//
// Only the tiles under the square are decoded (a picture is up to 86 megapixels, a square at most 1563 pixels a side), into one buffer the size of the
// square. The reduction is an area average weighted by alpha, exact in whole numbers: every source pixel counts with the area it shares with the output pixel.
using Tsuki.Data.Catalog;
using Tsuki.Data.Containers;
using Tsuki.Data.Images;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;

namespace Tsuki.Data.Trophies;

/// <summary>Cuts, reduces and frames the icons of the achievements, and draws the tiles that stand in for one.</summary>
public static class TrophyIconArt
{
    /// <summary>
    /// The side of the icon in the game's notice, which draws it pixel for pixel four pixels inside the notice's 80 px picture place (UiTrophyNotice.PlaceSize;
    /// UiTrophyNotice.IconSize is this constant).
    /// </summary>
    public const int NoticeSize = 72;

    /// <summary>The side of the icons and tiles the launchers ask for (the pages scale them down).</summary>
    public const int LauncherSize = 240;

    /// <summary>The smallest tile <see cref="Frame"/>, <see cref="Locked"/>, <see cref="Blank"/> and <see cref="Emblem"/> draw.</summary>
    public const int MinSize = 16;

    /// <summary>The largest tile they draw.</summary>
    public const int MaxSize = 2048;

    // the largest side Reduce takes or gives: the sums of one row stay inside an int
    private const int MaxSide = 16384;

    // a tile of a retail picture is at most 1024 x 1024; a header that asks for more than this is not a picture to cut an icon from
    private const int MaxTileRgbaBytes = 4 * 2048 * 2048;

    private const uint TileGround = 0x161C2B;
    private const uint LockColor = 0x5B6780;
    private const double LockedRingOpacity = 0.35;

    /// <summary>
    /// The framed icon of a trophy, <paramref name="size"/> x <paramref name="size"/>, straight-alpha RGBA8, rows top-down; null when the picture is not in this
    /// game folder, the square does not lie inside it, it cannot be decoded, or the source is disposed. Never throws for what the game folder holds.
    /// Safe on any thread, and beside a <see cref="GameFolderSource.Dispose"/> on another (which waits for it).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not 0..29, or the size is not <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
    public static byte[]? Render(GameFolderSource source, int trophyId, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        TrophyIconSource row = TrophyIconTable.Source(trophyId);
        CheckSize(size);
        byte[]? square = Cut(source, row);
        return square is null ? null : Frame(square, row.Side, TrophyTable.Grade(trophyId), size);
    }

    /// <summary>
    /// The square of a trophy as the table names it, unreduced and unframed: Side x Side straight-alpha RGBA8 (the engine's opaque alpha 254 lifted to 255, the
    /// row's ground colour already under it), or null as <see cref="Render"/>. For tests and tools.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is not 0..29.</exception>
    public static byte[]? Cut(GameFolderSource source, int trophyId)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Cut(source, TrophyIconTable.Source(trophyId));
    }

    /// <summary>The tile of a locked achievement (no game data): dark ground, a padlock drawn by code, a muted ring of the grade. Straight-alpha RGBA8.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown, or the size is not <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
    public static byte[] Locked(TrophyGrade grade, int size)
    {
        uint ring = RingColor(grade);
        CheckSize(size);
        Layout at = Layout.Of(size);
        byte[] ground = Flat(at.Picture, TileGround);
        DrawLock(ground, at, size);
        return Compose(ground, at, size, ring, LockedRingOpacity);
    }

    /// <summary>
    /// The tile shown while an earned achievement's icon is still being made: the same dark ground and the grade's full ring, no lock, no picture.
    /// Straight-alpha RGBA8.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown, or the size is not <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
    public static byte[] Blank(TrophyGrade grade, int size)
    {
        uint ring = RingColor(grade);
        CheckSize(size);
        Layout at = Layout.Of(size);
        return Compose(Flat(at.Picture, TileGround), at, size, ring, 1.0);
    }

    /// <summary>
    /// The emblem of a grade (no game data): the tile of <see cref="Blank"/> with a trophy cup drawn by code in the middle, in the grade's colour, light at the
    /// top and dark at the bottom. It stands for the grade on the launchers' summary card and where an icon cannot be made.
    /// Straight-alpha RGBA8. Safe on any thread.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown, or the size is not <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
    public static byte[] Emblem(TrophyGrade grade, int size)
    {
        uint ring = RingColor(grade);
        CheckSize(size);
        Layout at = Layout.Of(size);
        byte[] ground = Flat(at.Picture, TileGround);
        DrawCup(ground, at, size, ring);
        return Compose(ground, at, size, ring, 1.0);
    }

    /// <summary>
    /// Frames a square picture: from the outside in, a ring in the grade's colour (width = size / 40 rounded, at least 2 px), a transparent gap (half the
    /// ring, at least 1 px), then the picture area-averaged to the rest, with rounded corners (outer radius = 13 % of size; the picture's radius = outer radius
    /// - ring - gap). Antialiased edges. At 72 px (the notice): ring 2, gap 1, picture 66, radii 9 and 6. At 240 px: ring 6, gap 3, picture 222, radii 31 and 22.
    /// Returns size x size straight-alpha RGBA8 (a pixel nothing covers is 0, 0, 0, 0): premultiply it before scaling or blending it.
    /// </summary>
    /// <param name="rgba">The picture, <paramref name="side"/> x <paramref name="side"/> straight-alpha RGBA8, rows top-down.</param>
    /// <param name="side">Its side, at least 2.</param>
    /// <param name="grade">The grade whose colour the ring takes.</param>
    /// <param name="size">The side of the result.</param>
    /// <exception cref="ArgumentOutOfRangeException">The side is below 2 or too large, the grade is unknown, or the size is not <see cref="MinSize"/>..<see cref="MaxSize"/>.</exception>
    /// <exception cref="ArgumentException">The pixels are not side x side.</exception>
    public static byte[] Frame(ReadOnlySpan<byte> rgba, int side, TrophyGrade grade, int size)
    {
        if (side is < 2 or > MaxSide)
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, $"the side of the picture is 2..{MaxSide}");
        }

        if (rgba.Length != (long)side * side * 4)
        {
            throw new ArgumentException($"{rgba.Length} bytes are not a square picture of {side} x {side} RGBA pixels", nameof(rgba));
        }

        uint ring = RingColor(grade);
        CheckSize(size);
        Layout at = Layout.Of(size);
        return Compose(Reduce(rgba, side, side, at.Picture, at.Picture), at, size, ring, 1.0);
    }

    /// <summary>The ring colour of a grade as 0xRRGGBB.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The grade is unknown.</exception>
    public static uint RingColor(TrophyGrade grade) => grade switch
    {
        TrophyGrade.Platinum => 0xCFE6FF,
        TrophyGrade.Gold => 0xF0C050,
        TrophyGrade.Silver => 0xC0C8D4,
        TrophyGrade.Bronze => 0xC98D5A,
        _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "not a trophy grade"),
    };

    /// <summary>
    /// Resizes a straight-alpha RGBA8 picture by area average weighted by alpha: an output pixel is the mean of the source pixels under it, each counted with the
    /// area it shares with the output pixel and with its alpha, so a transparent pixel adds no colour (no fringe) and nothing rings or shimmers. Any size to any
    /// size (enlarging repeats pixels). A pixel that comes out fully transparent is 0, 0, 0, 0. For a whole factor it is the alpha-weighted box average of each
    /// factor x factor block, rounded half up.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is below 1 or above 16384.</exception>
    /// <exception cref="ArgumentException">The pixels are not width x height.</exception>
    public static byte[] Reduce(ReadOnlySpan<byte> rgba, int width, int height, int outWidth, int outHeight)
    {
        CheckSide(width, nameof(width));
        CheckSide(height, nameof(height));
        CheckSide(outWidth, nameof(outWidth));
        CheckSide(outHeight, nameof(outHeight));
        if (rgba.Length != (long)width * height * 4)
        {
            throw new ArgumentException($"{rgba.Length} bytes are not {width} x {height} RGBA pixels", nameof(rgba));
        }

        // In units of 1 / outWidth of a source pixel, source pixel i lies on [i * outWidth, (i + 1) * outWidth) and output pixel o on [o * width, (o + 1) * width):
        // what they share is a whole number, and an output pixel's shares add up to width. The same down the rows.
        byte[] output = new byte[outWidth * outHeight * 4];
        int[] row = new int[outWidth * 4];
        long[] sums = new long[outWidth * 4];
        long cells = (long)width * height;
        int rowAt = -1;
        for (int oy = 0; oy < outHeight; oy++)
        {
            long top = (long)oy * height;
            long bottom = top + height;
            int first = (int)(top / outHeight);
            int last = (int)((bottom - 1) / outHeight);
            Array.Clear(sums);
            for (int y = first; y <= last; y++)
            {
                if (y != rowAt)
                {
                    // a source row that two output rows share is summed once
                    SumRow(rgba.Slice(y * width * 4, width * 4), width, outWidth, row);
                    rowAt = y;
                }

                long share = Math.Min(((long)y + 1) * outHeight, bottom) - Math.Max((long)y * outHeight, top);
                for (int i = 0; i < row.Length; i++)
                {
                    sums[i] += share * row[i];
                }
            }

            Span<byte> line = output.AsSpan(oy * outWidth * 4, outWidth * 4);
            for (int i = 0; i < line.Length; i += 4)
            {
                long a = sums[i + 3];
                long cover = (a + (cells / 2)) / cells;
                if (cover == 0)
                {
                    continue;
                }

                line[i] = (byte)((sums[i] + (a / 2)) / a);
                line[i + 1] = (byte)((sums[i + 1] + (a / 2)) / a);
                line[i + 2] = (byte)((sums[i + 2] + (a / 2)) / a);
                line[i + 3] = (byte)cover;
            }
        }

        return output;
    }

    // One source row summed into the output columns: per column the colours times alpha times share, and alpha times share. At most width x 255 x 255 each.
    private static void SumRow(ReadOnlySpan<byte> source, int width, int outWidth, int[] row)
    {
        for (int ox = 0; ox < outWidth; ox++)
        {
            long left = (long)ox * width;
            long right = left + width;
            int first = (int)(left / outWidth);
            int last = (int)((right - 1) / outWidth);
            int r = 0;
            int g = 0;
            int b = 0;
            int a = 0;

            // the pixels that lie wholly inside the output pixel all count the same: sum them first, weigh them once
            for (int x = first + 1; x < last; x++)
            {
                int alpha = source[(4 * x) + 3];
                r += source[4 * x] * alpha;
                g += source[(4 * x) + 1] * alpha;
                b += source[(4 * x) + 2] * alpha;
                a += alpha;
            }

            r *= outWidth;
            g *= outWidth;
            b *= outWidth;
            a *= outWidth;
            Edge(source, first, (int)(Math.Min(((long)first + 1) * outWidth, right) - left), ref r, ref g, ref b, ref a);
            if (last != first)
            {
                Edge(source, last, (int)(right - ((long)last * outWidth)), ref r, ref g, ref b, ref a);
            }

            row[4 * ox] = r;
            row[(4 * ox) + 1] = g;
            row[(4 * ox) + 2] = b;
            row[(4 * ox) + 3] = a;
        }
    }

    private static void Edge(ReadOnlySpan<byte> source, int x, int share, ref int r, ref int g, ref int b, ref int a)
    {
        int weight = source[(4 * x) + 3] * share;
        r += source[4 * x] * weight;
        g += source[(4 * x) + 1] * weight;
        b += source[(4 * x) + 2] * weight;
        a += weight;
    }

    // -- the square ---------------------------------------------------------------------------------------------------

    private static byte[]? Cut(GameFolderSource source, in TrophyIconSource row)
    {
        try
        {
            // the archives stay mapped until this is left: a Dispose on another thread waits, and one that came first makes this throw
            using UseGate.Use use = source.EnterUse();
            ImageMzp? image = Open(source, row);
            if (image is null)
            {
                return null;
            }

            ImageHeader h = image.Header;
            int side = row.Side;
            if (side < 2 || row.X < 0 || row.Y < 0 || row.X > h.DisplayWidth - side || row.Y > h.DisplayHeight - side
                || h.InnerWidth <= 0 || h.InnerHeight <= 0 || h.TileRgbaBytes > MaxTileRgbaBytes)
            {
                return null;
            }

            // a tile the picture leaves out (flag 0) stays transparent, as in the assembled picture
            byte[] square = new byte[side * side * 4];
            byte[] tile = GC.AllocateUninitializedArray<byte>(h.TileRgbaBytes);
            var scratch = new TileScratch();
            int lastColumn = (row.X + side - 1) / h.InnerWidth;
            int lastRow = (row.Y + side - 1) / h.InnerHeight;
            for (int ty = row.Y / h.InnerHeight; ty <= lastRow && ty < h.TilesY; ty++)
            {
                for (int tx = row.X / h.InnerWidth; tx <= lastColumn && tx < h.TilesX; tx++)
                {
                    int t = (ty * h.TilesX) + tx;
                    if (!h.IsLive(t))
                    {
                        continue;
                    }

                    // the tile's displayed window (its border left out) lands at x, y of the picture; copy what of it lies in the square
                    (int sx, int sy, int w, int ht, int x, int y) = h.TileWindow(t);
                    int left = Math.Max(x, row.X);
                    int right = Math.Min(x + w, row.X + side);
                    int top = Math.Max(y, row.Y);
                    int bottom = Math.Min(y + ht, row.Y + side);
                    if (left >= right || top >= bottom)
                    {
                        continue;
                    }

                    image.DecodeTile(t, scratch, tile);
                    for (int line = top; line < bottom; line++)
                    {
                        tile.AsSpan(4 * (((sy + line - y) * h.TileWidth) + sx + left - x), 4 * (right - left))
                            .CopyTo(square.AsSpan(4 * (((line - row.Y) * side) + left - row.X)));
                    }
                }
            }

            // The palette pictures never reach alpha 255: the engine's "opaque" is 254 (docs/formats/images.md 4.2). An icon takes it as opaque, or every
            // event picture would let a trace of whatever lies under it through.
            for (int i = 3; i < square.Length; i += 4)
            {
                if (square[i] == 254)
                {
                    square[i] = 255;
                }
            }

            if (row.Ground != 0)
            {
                PutOnGround(square, row.Ground);
            }

            return square;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException or ThreadAbortException))
        {
            // whatever the folder holds, or a source closed meanwhile: there is no icon
            return null;
        }
    }

    private static ImageMzp? Open(GameFolderSource source, in TrophyIconSource row)
    {
        if (row.Kind == TrophyIconKind.Picture)
        {
            // the name tables hold file names: the engine's search takes the name with its extension
            string file = row.Name.Contains('.', StringComparison.Ordinal) ? row.Name : row.Name + ".MZP";
            return source.Catalog.NameIndex.Find(file) is { } id ? source.OpenImage((int)id.Archive, id.Index) : null;
        }

        // a sheet of the menus exists once per language and this child shows the same in each: the English one, else the first the copy has
        var set = (UiSet)row.Sheet;
        foreach (Language language in (Language[])[Language.En, .. source.AvailableLanguages])
        {
            try
            {
                return source.OpenUiSheetImage(set, language, row.Child);
            }
            catch (Exception e) when (e is FileNotFoundException or ArgumentOutOfRangeException or ContainerFormatException)
            {
                // not in this language
            }
        }

        return null;
    }

    private static void PutOnGround(byte[] rgba, uint ground)
    {
        int gr = (int)((ground >> 16) & 0xFF);
        int gg = (int)((ground >> 8) & 0xFF);
        int gb = (int)(ground & 0xFF);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a != 255)
            {
                rgba[i] = (byte)(((rgba[i] * a) + (gr * (255 - a)) + 127) / 255);
                rgba[i + 1] = (byte)(((rgba[i + 1] * a) + (gg * (255 - a)) + 127) / 255);
                rgba[i + 2] = (byte)(((rgba[i + 2] * a) + (gb * (255 - a)) + 127) / 255);
                rgba[i + 3] = 255;
            }
        }
    }

    // -- the frame ----------------------------------------------------------------------------------------------------

    /// <summary>The measures of a tile of one size.</summary>
    /// <param name="Ring">Width of the ring.</param>
    /// <param name="Gap">Width of the transparent gap inside it.</param>
    /// <param name="Radius">Corner radius of the tile.</param>
    /// <param name="Picture">Side of the picture.</param>
    private readonly record struct Layout(int Ring, int Gap, int Radius, int Picture)
    {
        /// <summary>Where the picture starts, from the tile's edge.</summary>
        public int Inset => Ring + Gap;

        public static Layout Of(int size)
        {
            int ring = Math.Max(2, (size + 20) / 40);
            int gap = Math.Max(1, ring / 2);
            return new Layout(ring, gap, ((13 * size) + 50) / 100, size - (2 * (ring + gap)));
        }
    }

    // The ring, the gap and the picture with rounded corners. An edge takes the share of the pixel it covers, measured as the distance of the pixel's
    // centre from the edge: whole pixels along the straight sides (they lie on pixel borders), a soft step round the corners.
    private static byte[] Compose(ReadOnlySpan<byte> picture, Layout at, int size, uint ring, double ringOpacity)
    {
        byte[] output = new byte[size * size * 4];
        double ringR = (ring >> 16) & 0xFF;
        double ringG = (ring >> 8) & 0xFF;
        double ringB = ring & 0xFF;
        int inset = at.Inset;
        for (int y = 0; y < size; y++)
        {
            double py = y + 0.5;
            for (int x = 0; x < size; x++)
            {
                double px = x + 0.5;
                double outer = Cover(RoundBox(px, py, 0, 0, size, size, at.Radius));
                if (outer <= 0)
                {
                    continue;
                }

                double band = Math.Max(0, outer - Cover(RoundBox(px, py, at.Ring, at.Ring, size - at.Ring, size - at.Ring, Math.Max(0, at.Radius - at.Ring)))) * ringOpacity;
                double r = ringR * band;
                double g = ringG * band;
                double b = ringB * band;
                double a = band;
                int ix = x - inset;
                int iy = y - inset;
                if ((uint)ix < (uint)at.Picture && (uint)iy < (uint)at.Picture)
                {
                    int p = ((iy * at.Picture) + ix) * 4;
                    double inside = Cover(RoundBox(px, py, inset, inset, size - inset, size - inset, Math.Max(0, at.Radius - inset))) * picture[p + 3] / 255.0;
                    r += picture[p] * inside;
                    g += picture[p + 1] * inside;
                    b += picture[p + 2] * inside;
                    a += inside;
                }

                int alpha = (int)Math.Round(Math.Min(a, 1.0) * 255.0);
                if (alpha == 0)
                {
                    continue;
                }

                int o = ((y * size) + x) * 4;
                output[o] = ToByte(r / a);
                output[o + 1] = ToByte(g / a);
                output[o + 2] = ToByte(b / a);
                output[o + 3] = (byte)alpha;
            }
        }

        return output;
    }

    private static byte[] Flat(int side, uint colour)
    {
        byte[] rgba = new byte[side * side * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(colour >> 16);
            rgba[i + 1] = (byte)(colour >> 8);
            rgba[i + 2] = (byte)colour;
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    // A padlock in the middle of the tile, 40 % of its side high: a rounded body, a shackle (half a ring on two legs) and a keyhole left in the ground's colour.
    private static void DrawLock(byte[] picture, Layout at, int size)
    {
        double s = size;
        double cx = s / 2;
        double bodyTop = 0.47 * s;
        double bodyBottom = 0.70 * s;
        double bodyHalf = 0.15 * s;
        double bodyRadius = 0.035 * s;
        double arcY = 0.40 * s;
        double stroke = 0.04 * s;
        double arcRadius = (0.10 * s) - (stroke / 2);
        double legBottom = bodyTop + (0.01 * s);
        double holeY = 0.555 * s;
        double holeRadius = 0.03 * s;
        double slotHalf = 0.013 * s;
        double slotBottom = 0.635 * s;
        double lockR = (LockColor >> 16) & 0xFF;
        double lockG = (LockColor >> 8) & 0xFF;
        double lockB = LockColor & 0xFF;
        int side = at.Picture;
        int firstRow = Math.Max(0, (int)(0.28 * s) - at.Inset);
        int lastRow = Math.Min(side - 1, (int)(0.72 * s) - at.Inset);
        for (int iy = firstRow; iy <= lastRow; iy++)
        {
            double py = iy + at.Inset + 0.5;
            for (int ix = 0; ix < side; ix++)
            {
                double px = ix + at.Inset + 0.5;
                double dx = Math.Abs(px - cx);
                if (dx > bodyHalf + 1)
                {
                    continue;
                }

                double body = Cover(RoundBox(px, py, cx - bodyHalf, bodyTop, cx + bodyHalf, bodyBottom, bodyRadius));
                double shackle = py <= arcY
                    ? Cover(Math.Abs(Math.Sqrt((dx * dx) + ((py - arcY) * (py - arcY))) - arcRadius) - (stroke / 2))
                    : Cover(RoundBox(dx, py, arcRadius - (stroke / 2), arcY, arcRadius + (stroke / 2), legBottom, 0));
                double hole = Math.Max(
                    Cover(Math.Sqrt((dx * dx) + ((py - holeY) * (py - holeY))) - holeRadius),
                    Cover(RoundBox(px, py, cx - slotHalf, holeY, cx + slotHalf, slotBottom, 0)));
                double cover = Math.Max(body, shackle) * (1 - hole);
                if (cover <= 0)
                {
                    continue;
                }

                int p = ((iy * side) + ix) * 4;
                picture[p] = ToByte(picture[p] + ((lockR - picture[p]) * cover));
                picture[p + 1] = ToByte(picture[p + 1] + ((lockG - picture[p + 1]) * cover));
                picture[p + 2] = ToByte(picture[p + 2] + ((lockB - picture[p + 2]) * cover));
            }
        }
    }

    // The emblem's trophy cup, as the sketch the owner chose draws it (0.24 to 0.79 of the side high): a bowl (a rim on the lower half of an ellipse), a ring on
    // either side for a handle, a stem and a base of two steps. Each pixel is tested at 8 x 8 points (4 x 4 above 512 px) and covered by the share inside,
    // which antialiases the curves; the straight edges lie on pixel borders, so they stay sharp at 40 px. The colour runs down the tile from the grade's colour mixed
    // 45 % with white to 72 % of it, as the sketch's gradient does.
    private static void DrawCup(byte[] picture, Layout at, int size, uint colour)
    {
        var cup = new CupShape(size);
        int samples = size <= 512 ? 8 : 4;
        double step = 1.0 / samples;
        double count = samples * samples;
        double[] light = new double[3];
        double[] dark = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double value = (colour >> (16 - (8 * c))) & 0xFF;
            light[c] = value + ((255 - value) * 0.45);
            dark[c] = value * 0.72;
        }

        int side = at.Picture;
        for (int y = cup.Top; y < cup.Bottom; y++)
        {
            int iy = y - at.Inset;
            if ((uint)iy >= (uint)side)
            {
                continue;
            }

            double t = (y + 0.5) / size;
            double cr = light[0] + ((dark[0] - light[0]) * t);
            double cg = light[1] + ((dark[1] - light[1]) * t);
            double cb = light[2] + ((dark[2] - light[2]) * t);
            for (int x = cup.Left; x < cup.Right; x++)
            {
                int ix = x - at.Inset;
                if ((uint)ix >= (uint)side)
                {
                    continue;
                }

                int hits = 0;
                for (int j = 0; j < samples; j++)
                {
                    double sy = y + ((j + 0.5) * step);
                    for (int i = 0; i < samples; i++)
                    {
                        if (cup.Contains(x + ((i + 0.5) * step), sy))
                        {
                            hits++;
                        }
                    }
                }

                if (hits == 0)
                {
                    continue;
                }

                double cover = hits / count;
                int p = ((iy * side) + ix) * 4;
                picture[p] = ToByte(picture[p] + ((cr - picture[p]) * cover));
                picture[p + 1] = ToByte(picture[p + 1] + ((cg - picture[p + 1]) * cover));
                picture[p + 2] = ToByte(picture[p + 2] + ((cb - picture[p + 2]) * cover));
            }
        }
    }

    /// <summary>
    /// The outline of the emblem's cup in the pixels of a tile of one size. It is the same on both sides of the middle, so a point is tested by its distance from
    /// the middle. A right edge is rounded to a pixel border and the left edge is its mirror, so the cup stays centred.
    /// </summary>
    private readonly struct CupShape
    {
        // the thinnest a handle's ring is drawn (the sketch's 3.5 % of the side is 0.6 px at 16)
        private const double MinStroke = 1.0;

        private readonly double _middle;
        private readonly double _rimTop;
        private readonly double _rimHalf;
        private readonly double _equator;
        private readonly double _bowlDepth;
        private readonly double _handleOffset;
        private readonly double _handleY;
        private readonly double _handleWidth;
        private readonly double _handleHeight;
        private readonly double _eyeWidth;
        private readonly double _eyeHeight;
        private readonly double _stemHalf;
        private readonly double _stemTop;
        private readonly double _footTop;
        private readonly double _footBottom;
        private readonly double _footHalf;
        private readonly double _baseTop;
        private readonly double _baseBottom;
        private readonly double _baseHalf;
        private readonly double _corner;

        public CupShape(int size)
        {
            double s = size;
            double middle = s / 2;
            _middle = middle;
            double Half(double right) => Math.Round(right * s) - middle;

            // the bowl: a rim from 0.30 to 0.70 of the side, 0.24 to 0.29 down, on the lower half of an ellipse that reaches 0.56
            _rimTop = Math.Round(0.24 * s);
            _rimHalf = Half(0.70);
            _equator = 0.29 * s;
            _bowlDepth = 0.27 * s;

            // the handles: rings 0.16 x 0.18 of the side round 0.28 (and 0.72), 0.36, 3.5 % of the side thick; the bowl covers their inner half
            _handleOffset = 0.22 * s;
            _handleY = 0.36 * s;
            _handleWidth = 0.08 * s;
            _handleHeight = 0.09 * s;
            double stroke = Math.Max(0.035 * s, MinStroke);
            _eyeWidth = _handleWidth - stroke;
            _eyeHeight = _handleHeight - stroke;

            // the stem, 7 % of the side wide (at least a pixel), from inside the bowl to the base
            _stemHalf = Math.Max(Half(0.535), Math.Floor(_middle) + 1 - _middle);
            _stemTop = 0.55 * s;

            // the base: a step 0.28 wide from 0.68 to 0.75, on a step 0.36 wide from 0.74 to 0.79, each at least a pixel high, corners rounded 1.5 % of the side;
            // the upper step is the lower one less 0.04 of the side on either side (at least a pixel), so the two stay apart at 40 px
            _footTop = Math.Round(0.68 * s);
            _footBottom = Math.Max(Math.Round(0.75 * s), _footTop + 1);
            _baseTop = Math.Min(Math.Round(0.74 * s), _footBottom);
            _baseBottom = Math.Max(Math.Round(0.79 * s), _footBottom + 1);
            _baseHalf = Half(0.68);
            _footHalf = Math.Max(_stemHalf + 1, _baseHalf - Math.Max(1, Math.Round(0.04 * s)));
            _corner = 0.015 * s;

            Top = (int)_rimTop;
            Bottom = (int)_baseBottom;
            double reach = Math.Max(_handleOffset + _handleWidth, _baseHalf);
            Left = Math.Max(0, (int)Math.Floor(_middle - reach));
            Right = Math.Min(size, (int)Math.Ceiling(_middle + reach));
        }

        /// <summary>The first row the cup touches.</summary>
        public int Top { get; }

        /// <summary>The row below the last it touches.</summary>
        public int Bottom { get; }

        /// <summary>The first column it touches.</summary>
        public int Left { get; }

        /// <summary>The column after the last it touches.</summary>
        public int Right { get; }

        public bool Contains(double x, double y)
        {
            double u = Math.Abs(x - _middle);

            // the rim and the bowl
            if (u < _rimHalf && y >= _rimTop)
            {
                if (y < _equator)
                {
                    return true;
                }

                double bx = u / _rimHalf;
                double by = (y - _equator) / _bowlDepth;
                if ((bx * bx) + (by * by) < 1)
                {
                    return true;
                }
            }

            // a handle: inside its ring, outside its eye
            double hx = u - _handleOffset;
            double hy = y - _handleY;
            if (Ellipse(hx, hy, _handleWidth, _handleHeight) < 1 && Ellipse(hx, hy, _eyeWidth, _eyeHeight) >= 1)
            {
                return true;
            }

            // the stem and the two steps of the base
            return (u < _stemHalf && y >= _stemTop && y < _footTop)
                || RoundBox(u, y, -_footHalf, _footTop, _footHalf, _footBottom, _corner) < 0
                || RoundBox(u, y, -_baseHalf, _baseTop, _baseHalf, _baseBottom, _corner) < 0;
        }

        private static double Ellipse(double x, double y, double width, double height) =>
            width <= 0 || height <= 0 ? double.PositiveInfinity : ((x / width) * (x / width)) + ((y / height) * (y / height));
    }

    // the distance of a point from the edge of a rectangle with rounded corners: below zero inside
    private static double RoundBox(double px, double py, double x0, double y0, double x1, double y1, double radius)
    {
        double qx = Math.Abs(px - ((x0 + x1) / 2)) - ((x1 - x0) / 2) + radius;
        double qy = Math.Abs(py - ((y0 + y1) / 2)) - ((y1 - y0) / 2) + radius;
        double ax = Math.Max(qx, 0);
        double ay = Math.Max(qy, 0);
        return Math.Sqrt((ax * ax) + (ay * ay)) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    // the share of a pixel a shape covers, from the distance of the pixel's centre to the shape's edge
    private static double Cover(double distance) => Math.Clamp(0.5 - distance, 0.0, 1.0);

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static void CheckSize(int size)
    {
        if (size is < MinSize or > MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"the side of a tile is {MinSize}..{MaxSize}");
        }
    }

    private static void CheckSide(int side, string name)
    {
        if (side is < 1 or > MaxSide)
        {
            throw new ArgumentOutOfRangeException(name, side, $"a side is 1..{MaxSide}");
        }
    }
}
