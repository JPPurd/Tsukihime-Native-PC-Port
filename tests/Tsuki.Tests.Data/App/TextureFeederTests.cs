using System.Diagnostics;
using Tsuki.App.Play;
using Tsuki.Data;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Stage;
using Tsuki.Media.Movie;
using Tsuki.Platform;
using Tsuki.Render.Gl;
using Tsuki.Render.Grade;
using Tsuki.Render.Textures;
using Tsuki.Tests.Data.Source;
using Tsuki.Ui;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// The texture provider of the app on the GPU (a hidden window, the user's game): every kind of <see cref="TextureRef"/> a display list names becomes a resident
/// texture of the right size, with the colour grade applied through the cache's grader (package K6).
/// </summary>
[Collection(SdlWindowCollection.Name)]
public sealed class TextureFeederTests
{
    private static GameFolderSource Source => SharedSource.Source;

    private static DisplayList ListOf(params TextureRef[] keys)
    {
        var list = new DisplayList(16);
        list.Reset(1);
        foreach (TextureRef key in keys)
        {
            list.Add(new DrawCmd
            {
                Kind = DrawKind.TexturedQuad,
                Texture = key,
                V0 = new Vertex(0, 0, 0, 0, 0xFFFFFFFF),
                V1 = new Vertex(10, 0, 1, 0, 0xFFFFFFFF),
                V2 = new Vertex(0, 10, 0, 1, 0xFFFFFFFF),
                V3 = new Vertex(10, 10, 1, 1, 0xFFFFFFFF),
                BlendState = 1,
            });
        }

        list.Seal();
        return list;
    }

    [GameDataFact]
    public void EveryKindOfTextureResolvesAndTheWholeSheetQuadGetsTheWholeSheet()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, 512L << 20) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        // a picture's first live tile, a UI sheet tile and the whole sheet, a glyph page, a parts picture
        int pic = Source.Eboot.Pictures.Lookup("IMG1696");
        Assert.True(pic >= 0);
        PictureLayout layout = new GamePictureLayouts(Source).Get(pic, (int)Language.En)!;
        PictureFileLayout file = layout.Files[0];
        TextureRef tile = TextureRef.ForPictureTile(file.Archive, file.Entry, file.Tiles[0].Tile);
        TextureRef sheetTile = TextureRef.ForUiTile(UiSet.DispParts, Language.En, 10, 0);
        TextureRef wholeSheet = TextureRef.ForUiTile(UiSet.DispParts, Language.En, 10, -1);
        TextureRef glyphs = TextureRef.ForGlyphPage(Language.En, 1);
        TextureRef parts = TextureRef.ForPictureTile(UiPictureArchive.Parts, 8 + 1, 0);
        TextureRef thumb = TextureRef.ForThumbnail(7, 1);
        TextureRef movie = TextureRef.ForMovieFrame(5);

        byte[] thumbPixels = new byte[4 * 3 * 4];
        for (int i = 0; i < thumbPixels.Length; i += 4)
        {
            thumbPixels[i] = 10;
            thumbPixels[i + 1] = 20;
            thumbPixels[i + 2] = 30;
            thumbPixels[i + 3] = 255;
        }

        feeder.Thumbnails = slot => slot == 7 ? new UiThumbnail(4, 3, 1, thumbPixels) : null;
        var frame = new VideoFrame(8, 4) { Index = 1 };
        Array.Fill(frame.Y, (byte)235);
        Array.Fill(frame.U, (byte)128);
        Array.Fill(frame.V, (byte)128);
        feeder.MovieFrames = slot => slot == 5 ? frame : null;

        int missing = feeder.EnsureResident(ListOf(tile, sheetTile, wholeSheet, glyphs, parts, thumb, movie), TimeSpan.FromSeconds(30));
        Assert.Equal(0, missing);
        foreach (TextureRef key in new[] { tile, sheetTile, wholeSheet, glyphs, parts, thumb, movie })
        {
            Assert.True(cache.Contains(key), $"{key} is not resident");
        }

        Picture sheet = Source.UiSheet(UiSet.DispParts, Language.En, 10);
        Assert.Equal(4 * sheet.Width * sheet.Height, cache.ReadBack(wholeSheet).Length);          // the whole sheet, not one tile
        Assert.Equal(4 * 1024 * 1024, cache.ReadBack(glyphs).Length);
        byte[] movieBack = cache.ReadBack(movie);
        Assert.Equal(4 * 8 * 4, movieBack.Length);
        Assert.InRange(movieBack[0], 254, 255);                                                   // Y 235 is white
        Assert.Equal(thumbPixels, cache.ReadBack(thumb));
        Assert.True(feeder.Stats.Requested >= 5);
        Assert.Null(feeder.Fault);
    }

    [GameDataFact]
    public void AGradedTileIsBuiltRecolouredAndCachedBesideThePlainOne()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, 512L << 20) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        int pic = Source.Eboot.Pictures.Lookup("IMG1696");
        PictureFileLayout file = new GamePictureLayouts(Source).Get(pic, (int)Language.En)!.Files[0];
        int t = file.Tiles[0].Tile;
        int grey = GradeKeys.Intern(ColourGrade.From(mode: 2, brightness: 0, contrast: 0, rgb: 0, globalColourMode: 2));
        Assert.NotEqual(0, grey);
        TextureRef plain = TextureRef.ForPictureTile(file.Archive, file.Entry, t);
        TextureRef graded = TextureRef.ForPictureTile(file.Archive, file.Entry, t, grey);

        Assert.Equal(0, feeder.EnsureResident(ListOf(plain, graded), TimeSpan.FromSeconds(30)));
        byte[] a = cache.ReadBack(plain);
        byte[] b = cache.ReadBack(graded);
        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(a, b);
        int greys = 0;
        for (int i = 0; i < b.Length; i += 4)
        {
            if (b[i + 3] != 0 && b[i] == b[i + 1] && b[i + 1] == b[i + 2])
            {
                greys++;
            }
        }

        Assert.True(greys > b.Length / 8, "the grey grade should leave grey pixels");
    }

    // Issue #13: a frame whose textures are larger than the cache's cap. With the 512 MiB cache of a card with 1.5 GB or less one frame of a scene drew 566 MiB of
    // picture tiles: its last uploads evicted its first tiles, it was never complete, and the game held the picture before it for ever. The tests below use a
    // 32 MiB cache and whole pictures of the game (the first large ones of the catalog), and poll the feeder the way the game's hold does.
    private const long SmallCap = 32L << 20;

    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);          // far more than the decode of a few dozen tiles needs; the game gives a hold up after 8 s

    /// <summary>The tiles of the catalog's large pictures (files of 4 MiB and more) in catalog order, each tile once, with its size as a texture.</summary>
    private static IEnumerable<(TextureRef Key, long Bytes)> LargePictureTiles()
    {
        var layouts = new GamePictureLayouts(Source);
        var seen = new HashSet<TextureRef>();
        for (int pic = 0; pic < Source.Catalog.Images.Count; pic++)
        {
            if (layouts.Get(pic, (int)Language.En) is not { } layout)
            {
                continue;
            }

            foreach (PictureFileLayout file in layout.Files)
            {
                long tileBytes = Source.OpenImage(file.Archive, file.Entry).Header.TileRgbaBytes;
                if (file.Tiles.Count * tileBytes < 4L << 20)
                {
                    continue;
                }

                foreach (TileQuad quad in file.Tiles)
                {
                    TextureRef key = TextureRef.ForPictureTile(file.Archive, file.Entry, quad.Tile);
                    if (seen.Add(key))
                    {
                        yield return (key, tileBytes);
                    }
                }
            }
        }
    }

    /// <summary>The next tiles until they hold at least <paramref name="atLeast"/> bytes.</summary>
    private static (TextureRef[] Keys, long Bytes) Take(IEnumerator<(TextureRef Key, long Bytes)> tiles, long atLeast)
    {
        var keys = new List<TextureRef>();
        long bytes = 0;
        while (bytes < atLeast && tiles.MoveNext())
        {
            keys.Add(tiles.Current.Key);
            bytes += tiles.Current.Bytes;
        }

        Assert.True(bytes >= atLeast, "the game has fewer large pictures than this test needs");
        return ([.. keys], bytes);
    }

    /// <summary>The next tiles for as long as they stay under <paramref name="under"/> bytes (the first tile that does not fit is left out).</summary>
    private static (TextureRef[] Keys, long Bytes) TakeUnder(IEnumerator<(TextureRef Key, long Bytes)> tiles, long under)
    {
        var keys = new List<TextureRef>();
        long bytes = 0;
        while (tiles.MoveNext() && bytes + tiles.Current.Bytes < under)
        {
            keys.Add(tiles.Current.Key);
            bytes += tiles.Current.Bytes;
        }

        return ([.. keys], bytes);
    }

    /// <summary>
    /// The game's hold for textures (GameApp.RenderCore in real play): the feeder is asked without a budget, pass after pass, until the frame misses nothing.
    /// Returns the textures still missing when the time is up.
    /// </summary>
    private static int Hold(TextureFeeder feeder, DisplayList frame)
    {
        var clock = Stopwatch.StartNew();
        int missing;
        while ((missing = feeder.EnsureResident(frame, TimeSpan.Zero)) > 0 && clock.Elapsed < HoldLimit)
        {
            Thread.Sleep(1);
        }

        return missing;
    }

    /// <summary>Keeps <paramref name="frame"/> (complete) on the screen until every tile on its way is decoded and uploaded.</summary>
    private static void ShowUntilNothingIsInFlight(TextureFeeder feeder, DisplayList frame)
    {
        var clock = Stopwatch.StartNew();
        int idle = 0;
        while (idle < 5 && clock.Elapsed < HoldLimit)
        {
            Assert.Equal(0, feeder.EnsureResident(frame, TimeSpan.Zero));
            idle = feeder.Pending == 0 ? idle + 1 : 0;              // a few passes in a row: a job a worker has just taken is counted a moment later
            Thread.Sleep(1);
        }

        Assert.Equal(0, feeder.Pending);
    }

    private static void AssertAllResident(TextureCache cache, TextureRef[] keys, string what)
    {
        int absent = keys.Count(key => !cache.Contains(key));
        Assert.True(
            absent == 0,
            $"{absent} of the {keys.Length} tiles of {what} are not resident ({cache.ResidentBytes >> 20} MiB resident, cap {cache.CapacityBytes >> 20} MiB, {cache.Stats.Evictions} evictions)");
    }

    [GameDataFact]
    public void AFrameLargerThanTheCachesCapBecomesCompletelyResidentAndTheCacheTrimsAfterIt()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, SmallCap) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        // one frame of several whole pictures, one and a half times the cap; then four of its tiles and the next picture's for a later, small frame
        using IEnumerator<(TextureRef Key, long Bytes)> tiles = LargePictureTiles().GetEnumerator();
        (TextureRef[] large, long largeBytes) = Take(tiles, atLeast: SmallCap + (SmallCap / 2));
        (TextureRef[] other, long otherBytes) = Take(tiles, atLeast: 4L << 20);
        Assert.True(largeBytes > cache.CapacityBytes && otherBytes <= SmallCap / 4, $"{largeBytes >> 20} MiB and {otherBytes >> 20} MiB of tiles do not suit a {SmallCap >> 20} MiB cache");
        Assert.True(large.Select(k => (k.A, k.B)).Distinct().Count() >= 2, "the large frame draws several pictures");

        DisplayList frame = ListOf(large);
        int missing = Hold(feeder, frame);
        Assert.True(
            missing == 0,
            $"{missing} of the frame's {large.Length} tiles ({largeBytes >> 20} MiB) never became resident in a {SmallCap >> 20} MiB cache: "
            + $"{cache.ResidentBytes >> 20} MiB resident, {cache.Stats.Evictions} evictions, {feeder.Stats.Requested} tiles requested, {feeder.Pending} in flight");
        AssertAllResident(cache, large, "the frame the feeder called complete");

        // everything the frame draws and nothing else: the cache is above its cap for this frame, none of the frame's tiles was evicted and none was decoded twice
        Assert.Equal(largeBytes, cache.ResidentBytes);
        Assert.Equal(largeBytes, cache.PinnedBytes);
        Assert.True(cache.ResidentBytes > cache.CapacityBytes);
        Assert.Equal(0, cache.Stats.Evictions);
        Assert.Equal(large.Length, feeder.Stats.Requested);
        Assert.Null(feeder.Fault);

        // it stays complete for as long as it is on the screen
        for (int pass = 0; pass < 10; pass++)
        {
            Assert.Equal(0, feeder.EnsureResident(frame, TimeSpan.Zero));
        }

        Assert.Equal(largeBytes, cache.ResidentBytes);
        Assert.Equal(0, cache.Stats.Evictions);

        // the later small frame: what nothing draws any more goes as soon as the frame's textures are named, and the cache is back within its cap
        TextureRef[] small = [.. large.Take(4), .. other];
        DisplayList smallFrame = ListOf(small);
        feeder.EnsureResident(smallFrame, TimeSpan.Zero);
        Assert.InRange(cache.ResidentBytes, 1, cache.CapacityBytes);
        Assert.Equal(0, Hold(feeder, smallFrame));
        AssertAllResident(cache, small, "the small frame");
        Assert.True(cache.PinnedBytes > otherBytes && cache.PinnedBytes < cache.CapacityBytes, $"the small frame pins {cache.PinnedBytes >> 20} MiB");
        Assert.InRange(cache.ResidentBytes, cache.PinnedBytes, cache.CapacityBytes);
        Assert.True(cache.Stats.Evictions > 0);
        Assert.Null(feeder.Fault);
    }

    [GameDataFact]
    public void TheLookAheadQueuesNothingWhileTheFrameFillsTheCache()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, SmallCap) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        using IEnumerator<(TextureRef Key, long Bytes)> tiles = LargePictureTiles().GetEnumerator();
        (TextureRef[] large, _) = Take(tiles, atLeast: SmallCap + (SmallCap / 2));
        (TextureRef[] ahead, long aheadBytes) = Take(tiles, atLeast: 4L << 20);
        Assert.True(aheadBytes <= SmallCap / 4, $"{aheadBytes >> 20} MiB of look-ahead tiles do not suit a {SmallCap >> 20} MiB cache");
        Assert.Equal(0, Hold(feeder, ListOf(large)));
        Assert.True(cache.PinnedBytes >= cache.CapacityBytes);

        // the frame on the screen fills the cache by itself: a look-ahead tile would be evicted the moment it is uploaded, so none is decoded
        FeederStats before = feeder.Stats;
        feeder.Prefetch(ahead);
        Assert.Equal(before.Requested, feeder.Stats.Requested);
        Assert.Equal(before.Prefetched, feeder.Stats.Prefetched);
        Assert.Equal(0, feeder.Pending);

        // with a small frame on the screen the same tiles are fetched ahead, and the frame that names them later finds them resident
        DisplayList smallFrame = ListOf(large[..4]);
        Assert.Equal(0, feeder.EnsureResident(smallFrame, TimeSpan.Zero));
        feeder.Prefetch(ahead);
        Assert.Equal(before.Prefetched + ahead.Length, feeder.Stats.Prefetched);
        ShowUntilNothingIsInFlight(feeder, smallFrame);
        AssertAllResident(cache, ahead, "the look-ahead");
        Assert.InRange(cache.ResidentBytes, aheadBytes, cache.CapacityBytes);
        long requested = feeder.Stats.Requested;
        Assert.Equal(0, feeder.EnsureResident(ListOf(ahead), TimeSpan.Zero));
        Assert.Equal(requested, feeder.Stats.Requested);
    }

    [GameDataFact]
    public void ALookAheadTileTheCacheEvictedAtOnceIsDecodedAgainWhenAFrameNamesIt()
    {
        using GameWindow window = GameWindow.Create(new WindowOptions("feeder", 64, 64, Hidden: true, Resizable: false, SwapInterval: 0));
        GlDevice device = GlDevice.Create(window.GetProcAddress);
        using var cache = new TextureCache(device.Gl, SmallCap) { Grader = TileGrader.FromEboot(Source.Tables.Reader) };
        using var feeder = new TextureFeeder(Source, cache);

        // a frame that leaves room for at most one more tile (so the look-ahead is not switched off), and 8 MiB of the pictures after it for the look-ahead
        using IEnumerator<(TextureRef Key, long Bytes)> tiles = LargePictureTiles().GetEnumerator();
        (TextureRef[] nearlyFull, long frameBytes) = TakeUnder(tiles, SmallCap);
        (TextureRef[] ahead, long aheadBytes) = Take(tiles, atLeast: 8L << 20);
        Assert.True(
            frameBytes > 0 && frameBytes < SmallCap && frameBytes + (aheadBytes / 2) > SmallCap && aheadBytes <= SmallCap / 2,
            $"{frameBytes >> 20} MiB and {aheadBytes >> 20} MiB of tiles do not suit a {SmallCap >> 20} MiB cache");
        DisplayList frame = ListOf(nearlyFull);
        Assert.Equal(0, Hold(feeder, frame));

        // the look-ahead tiles are decoded and uploaded while the frame stays on the screen: the frame keeps every tile, so the cache evicts most of them again
        feeder.Prefetch(ahead);
        Assert.Equal(ahead.Length, feeder.Stats.Prefetched);
        ShowUntilNothingIsInFlight(feeder, frame);
        AssertAllResident(cache, nearlyFull, "the frame on the screen");
        Assert.Equal(frameBytes, cache.PinnedBytes);
        int evicted = ahead.Count(key => !cache.Contains(key));
        Assert.True(evicted * 2 >= ahead.Length, $"only {evicted} of the {ahead.Length} look-ahead tiles were evicted: the test does not reach its case");

        // the scene changes to the pictures the look-ahead named: the evicted tiles are no longer "queued" (they were, for ever), so they are decoded again
        long requested = feeder.Stats.Requested;
        int missing = Hold(feeder, ListOf(ahead));
        Assert.True(
            missing == 0,
            $"{missing} of the {ahead.Length} tiles the look-ahead had named never became resident: {feeder.Stats.Requested - requested} were requested again, {feeder.Pending} in flight");
        AssertAllResident(cache, ahead, "the frame the look-ahead had named");
        Assert.Equal(evicted, feeder.Stats.Requested - requested);
        Assert.InRange(cache.ResidentBytes, aheadBytes, cache.CapacityBytes);
        Assert.Null(feeder.Fault);
    }
}
