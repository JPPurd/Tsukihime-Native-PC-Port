using System.Diagnostics;
using Tsuki.Data;
using Tsuki.Data.Images;
using Tsuki.Data.Trophies;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.Trophies;

/// <summary>
/// v0.1.9, the icons of the achievements on the real game (read only): every row of the table names a picture the game folder holds and a square inside it,
/// the icon cut from it is a real picture in its frame at the notice's and the launchers' size, and the launchers' core hands the same icon out, made once.
/// No pixel of the game is kept or compared with anything stored: the tests look at the frame, and at whether the picture is flat. The time each icon takes is
/// written to the test output.
/// </summary>
[Collection(LauncherCacheCollection)]
public sealed class TrophyIconRealDataTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>
    /// The classes that use what the launcher's core keeps per process (Achievements: names, cups, icons) and make it forget: they run one after the other,
    /// or one class's Forget would empty the cache another is counting on.
    /// </summary>
    public const string LauncherCacheCollection = "Launcher achievements cache";

    public void Dispose() => Achievements.Forget();

    private static GameFolderSource Open() => GameFolderSource.Open(GameFolder.Require());

    // the header of a row's picture, found the way the renderer finds it
    private static ImageHeader? Header(GameFolderSource source, TrophyIconSource row)
    {
        if (row.Kind == TrophyIconKind.UiSheet)
        {
            return source.OpenUiSheetImage((UiSet)row.Sheet, Language.En, row.Child).Header;
        }

        return source.Catalog.NameIndex.Find(row.Name + ".MZP") is { } id ? source.OpenImage((int)id.Archive, id.Index).Header : null;
    }

    // how many different colours the middle of a tile holds (every third pixel of the middle half)
    private static int ColoursInTheMiddle(byte[] rgba, int size)
    {
        var colours = new HashSet<int>();
        for (int y = size / 4; y < size * 3 / 4; y += 3)
        {
            for (int x = size / 4; x < size * 3 / 4; x += 3)
            {
                int o = ((y * size) + x) * 4;
                colours.Add((rgba[o] << 16) | (rgba[o + 1] << 8) | rgba[o + 2]);
            }
        }

        return colours.Count;
    }

    private static (int, int, int, int) At(byte[] rgba, int size, int x, int y)
    {
        int o = ((y * size) + x) * 4;
        return (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]);
    }

    [GameDataFact]
    public void EveryRowsPictureIsInTheGameAndItsSquareLiesInsideIt()
    {
        using GameFolderSource source = Open();
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            TrophyIconSource row = TrophyIconTable.Source(id);
            ImageHeader? header = Header(source, row);
            Assert.True(header is not null, $"trophy {id}: the picture is not in the game folder");
            Assert.True(row.X + row.Side <= header.DisplayWidth && row.Y + row.Side <= header.DisplayHeight,
                $"trophy {id}: the square {row.X},{row.Y} + {row.Side} leaves the picture of {header.DisplayWidth} x {header.DisplayHeight}");

            // the square as cut: its own size, and something in it
            byte[]? cut = TrophyIconArt.Cut(source, id);
            Assert.NotNull(cut);
            Assert.Equal(row.Side * row.Side * 4, cut.Length);
            if (row.Ground != 0)
            {
                // a cut-out stands on its ground: nothing of it is see-through any more, and the ground shows where the picture had nothing
                for (int i = 3; i < cut.Length; i += 4)
                {
                    Assert.True(cut[i] == 255, $"trophy {id}: pixel {i / 4} of the square is see-through");
                }

                Assert.Equal(((int)(row.Ground >> 16) & 0xFF, (int)(row.Ground >> 8) & 0xFF, (int)row.Ground & 0xFF, 255), At(cut, row.Side, 2, row.Side - 3));
            }
        }
    }

    [GameDataFact]
    public void EveryIconIsARealPictureInItsFrameAtBothSizes()
    {
        using GameFolderSource source = Open();
        var times = new List<string>();
        double total = 0;
        double slowest = 0;
        foreach (int size in new[] { TrophyIconArt.LauncherSize, TrophyIconArt.NoticeSize })
        {
            int ring = size == TrophyIconArt.NoticeSize ? 2 : 6;
            int inset = size == TrophyIconArt.NoticeSize ? 3 : 9;
            for (int id = 0; id < TrophyTable.Count; id++)
            {
                var watch = Stopwatch.StartNew();
                byte[]? icon = TrophyIconArt.Render(source, id, size);
                watch.Stop();
                Assert.True(icon is not null, $"trophy {id} at {size}: no icon");
                Assert.Equal(size * size * 4, icon.Length);
                if (size == TrophyIconArt.LauncherSize)
                {
                    times.Add($"{id}: {watch.Elapsed.TotalMilliseconds:F1}");
                    total += watch.Elapsed.TotalMilliseconds;
                    slowest = Math.Max(slowest, id == 0 ? 0 : watch.Elapsed.TotalMilliseconds);
                }

                // the frame: the ring of the trophy's grade, the gap, round corners
                uint colour = TrophyIconArt.RingColor(TrophyTable.Grade(id));
                var ringColour = ((int)(colour >> 16) & 0xFF, (int)(colour >> 8) & 0xFF, (int)colour & 0xFF, 255);
                Assert.Equal(ringColour, At(icon, size, size / 2, 0));
                Assert.Equal(ringColour, At(icon, size, size - 1, size / 2));
                Assert.Equal((0, 0, 0, 0), At(icon, size, size / 2, ring));
                Assert.Equal((0, 0, 0, 0), At(icon, size, 0, 0));
                Assert.Equal((0, 0, 0, 0), At(icon, size, size - 1, size - 1));

                // the picture: nothing see-through along its sides or in its middle, and not one flat colour
                foreach ((int x, int y) in new[] { (size / 2, inset), (inset, size / 2), (size - 1 - inset, size / 2), (size / 2, size - 1 - inset), (size / 2, size / 2) })
                {
                    Assert.True(At(icon, size, x, y).Item4 == 255, $"trophy {id} at {size}: the picture is see-through at {x},{y}");
                }

                int colours = ColoursInTheMiddle(icon, size);
                Assert.True(colours >= 24, $"trophy {id} at {size}: {colours} colours in the middle; the picture is flat");
            }
        }

        output.WriteLine($"icons at {TrophyIconArt.LauncherSize} px, first call of each in this test run, ms (trophy 0 also opens its archive and is the first use of the code): {string.Join("  ", times)}");
        output.WriteLine($"all 30: {total:F0} ms; slowest after the first: {slowest:F1} ms (a debug build is several times slower than the release)");

        // generous: this is a debug build beside other tests; the release takes 6 to 28 ms an icon
        Assert.True(total < 15000, $"30 icons took {total:F0} ms");
    }

    [GameDataFact]
    public void TwoTrophiesDoNotGetTheSameIcon()
    {
        using GameFolderSource source = Open();
        var seen = new Dictionary<string, int>();
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            byte[] icon = TrophyIconArt.Render(source, id, TrophyIconArt.NoticeSize)!;
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(icon));
            Assert.False(seen.TryGetValue(key, out int other), $"trophies {other} and {id} have the same icon");
            seen[key] = id;

            // the same call again gives the same pixels
            Assert.Equal(icon, TrophyIconArt.Render(source, id, TrophyIconArt.NoticeSize));
        }
    }

    [GameDataFact]
    public void TheIconIsTheSquareOfTheAssembledPictureReducedAndFramed()
    {
        // one picture with tile borders, cut at once from the whole decoded picture and by the renderer from the tiles under the square: the same pixels,
        // but for the alpha of 254 the engine's palette pictures have where they are opaque, which the icon takes as 255
        using GameFolderSource source = Open();
        foreach (int id in new[] { 16, 29 })
        {
            TrophyIconSource row = TrophyIconTable.Source(id);
            var found = source.Catalog.NameIndex.Find(row.Name + ".MZP")!.Value;
            ImageMzp image = source.OpenImage((int)found.Archive, found.Index);
            byte[] whole = image.DecodeDisplayed();
            byte[] expected = new byte[row.Side * row.Side * 4];
            for (int y = 0; y < row.Side; y++)
            {
                whole.AsSpan((((row.Y + y) * image.Header.DisplayWidth) + row.X) * 4, row.Side * 4).CopyTo(expected.AsSpan(y * row.Side * 4));
            }

            Assert.Equal(PixelFormat.Hep8, image.Header.Format);
            Assert.Equal(1, image.Header.Border);
            Assert.Contains((byte)254, expected.Where((_, i) => i % 4 == 3));
            for (int i = 3; i < expected.Length; i += 4)
            {
                expected[i] = expected[i] == 254 ? (byte)255 : expected[i];
            }

            byte[] cut = TrophyIconArt.Cut(source, id)!;
            Assert.True(expected.AsSpan().SequenceEqual(cut), $"trophy {id}: the square cut tile by tile is not the square of the assembled picture");
            Assert.Equal(TrophyIconArt.Frame(expected, row.Side, TrophyTable.Grade(id), 240), TrophyIconArt.Render(source, id, 240));
        }
    }

    [GameDataFact]
    public void ASourceThatIsClosedGivesNoIconAndNoError()
    {
        GameFolderSource source = Open();
        Assert.NotNull(TrophyIconArt.Render(source, 1, TrophyIconArt.NoticeSize));
        source.Dispose();
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            Assert.Null(TrophyIconArt.Render(source, id, TrophyIconArt.NoticeSize));
            Assert.Null(TrophyIconArt.Cut(source, id));
        }

        // what the caller got wrong is still said
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Render(source, 30, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Render(source, -1, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrophyIconArt.Render(source, 1, 8));
        Assert.Throws<ArgumentNullException>(() => TrophyIconArt.Render(null!, 1, 80));
    }

    [GameDataFact]
    public void IconsAreMadeBesideEachOtherOnSeveralThreads()
    {
        using GameFolderSource source = Open();
        byte[][] alone = [.. Enumerable.Range(0, 8).Select(id => TrophyIconArt.Render(source, id, TrophyIconArt.NoticeSize)!)];
        var together = new byte[8][];
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => together[i % 8] = TrophyIconArt.Render(source, i % 8, TrophyIconArt.NoticeSize)!);
        for (int id = 0; id < 8; id++)
        {
            Assert.Equal(alone[id], together[id]);
        }
    }

    [GameDataFact]
    public void TheLaunchersCoreHandsOutEachIconAt240MadeOnceFromOneOpeningOfTheFolder()
    {
        string folder = GameFolder.Require();
        Achievements.Forget();
        int openings = Achievements.IconFolderOpenings;
        var icons = new AchievementIcon[TrophyTable.Count];
        var watch = Stopwatch.StartNew();
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            AchievementIcon? icon = Achievements.Icon(folder, id);
            Assert.True(icon is not null, $"trophy {id}: the launcher has no icon");
            Assert.Equal(240, icon.Size);
            Assert.Equal(240 * 240 * 4, icon.Rgba.Length);
            icons[id] = icon;
        }

        output.WriteLine($"Achievements.Icon, all 30 for the first time, the game folder opened once: {watch.Elapsed.TotalMilliseconds:F0} ms (debug build)");
        Assert.Equal(1, Achievements.IconFolderOpenings - openings);

        // kept: the same objects again, at once and without the folder being opened
        watch.Restart();
        for (int id = 0; id < TrophyTable.Count; id++)
        {
            Assert.Same(icons[id], Achievements.Icon(folder, id));
            Assert.Same(icons[id], Achievements.Icon("  " + folder + " ", id));
        }

        Assert.Equal(1, Achievements.IconFolderOpenings - openings);
        output.WriteLine($"Achievements.Icon, all 30 twice more from the cache: {watch.Elapsed.TotalMilliseconds:F2} ms");

        // they are the renderer's icons
        using (GameFolderSource source = Open())
        {
            foreach (int id in new[] { 0, 1, 14, 28, 29 })
            {
                Assert.Equal(TrophyIconArt.Render(source, id, TrophyIconArt.LauncherSize), icons[id].Rgba);
            }
        }

        // an id that is no trophy's, and no folder: nothing
        Assert.Null(Achievements.Icon(folder, 30));
        Assert.Null(Achievements.Icon(folder, -1));
        Assert.Null(Achievements.Icon(null, 1));

        // after a Forget they are made again: other objects, the same pixels
        Achievements.Forget();
        AchievementIcon? again = Achievements.Icon(folder, 7);
        Assert.NotNull(again);
        Assert.NotSame(icons[7], again);
        Assert.Equal(icons[7].Rgba, again.Rgba);
        Assert.Equal(2, Achievements.IconFolderOpenings - openings);
    }

    [GameDataFact]
    public void TheLauncherClosesTheGameFolderSoonAfterTheLastIconAndKeepsTheIcons()
    {
        string folder = GameFolder.Require();
        Achievements.Forget();
        Assert.False(Achievements.IconFolderIsOpen);
        int openings = Achievements.IconFolderOpenings;
        AchievementIcon? first = Achievements.Icon(folder, 2);
        Assert.NotNull(first);

        // held open for the icons that follow at once (a page asks for thirty in a row) ...
        Assert.True(Achievements.IconFolderIsOpen);
        var watch = Stopwatch.StartNew();
        Assert.NotNull(Achievements.Icon(folder, 3));
        Assert.Equal(1, Achievements.IconFolderOpenings - openings);

        // ... and closed about three seconds after the last, so an idle launcher does not hold the player's files
        while (Achievements.IconFolderIsOpen && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(100);
        }

        Assert.False(Achievements.IconFolderIsOpen);
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2), $"the folder was closed {watch.ElapsedMilliseconds} ms after the last icon");

        // the icons stay, and are handed out without the folder
        Assert.Same(first, Achievements.Icon(folder, 2));
        Assert.False(Achievements.IconFolderIsOpen);
        Assert.Equal(1, Achievements.IconFolderOpenings - openings);

        // a new icon opens it once more, and a Forget closes it at once
        Assert.NotNull(Achievements.Icon(folder, 4));
        Assert.Equal(2, Achievements.IconFolderOpenings - openings);
        Assert.True(Achievements.IconFolderIsOpen);
        Achievements.Forget();
        Assert.False(Achievements.IconFolderIsOpen);
    }

    [GameDataFact]
    public void SeveralThreadsAskingTheLauncherForIconsGetOneObjectEach()
    {
        string folder = GameFolder.Require();
        Achievements.Forget();
        int openings = Achievements.IconFolderOpenings;
        var got = new AchievementIcon?[60];
        Parallel.For(0, got.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => got[i] = Achievements.Icon(folder, i % 10));
        for (int i = 0; i < got.Length; i++)
        {
            Assert.NotNull(got[i]);
            Assert.Same(got[i % 10], got[i]);
        }

        Assert.Equal(1, Achievements.IconFolderOpenings - openings);
    }
}
