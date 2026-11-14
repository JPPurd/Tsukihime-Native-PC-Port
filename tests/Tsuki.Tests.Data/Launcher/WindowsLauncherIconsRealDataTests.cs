using System.Buffers.Binary;
using System.Diagnostics;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Data.Launcher;

/// <summary>
/// v0.1.9, the icons of the Windows launcher's Achievements page on the real game (read only): the real TsukihimeLauncher.exe of this build, in its verification
/// modes (no window, a temporary profile), with the player's game folder, where an earned row shows its icon, cut from the game's own pictures. Without a game
/// folder the self-test runs in the Synthetic tests (WindowsAchievementsPageTests), where an earned row shows the emblem of its grade instead.
/// </summary>
public sealed class WindowsLauncherIconsRealDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-wpf-icons-real-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;

    public WindowsLauncherIconsRealDataTests()
    {
        _profile = Path.Combine(_root, "Tsuki");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a launcher that has not quite ended; the temp folder is the system's to clean
        }
    }

    /// <summary>The build output of src/Tsuki.Launcher next to this test's output (artifacts layout or the plain bin layout), or null (it is built on Windows only).</summary>
    private static string? LauncherOutput()
    {
        char s = Path.DirectorySeparatorChar;
        string here = AppContext.BaseDirectory.TrimEnd(s);
        string[] candidates =
        [
            here.Replace($"{s}Tsuki.Tests.Data{s}", $"{s}Tsuki.Launcher{s}", StringComparison.OrdinalIgnoreCase),
            here.Replace($"{s}tests{s}Tsuki.Tests.Data{s}", $"{s}src{s}Tsuki.Launcher{s}", StringComparison.OrdinalIgnoreCase) + "-windows",
        ];
        return candidates.FirstOrDefault(c => !string.Equals(c, here, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(c, GameLauncher.LauncherFileName)));
    }

    /// <summary>Runs the launcher of this build without a window, over the temporary profile and the player's game folder, and returns its exit code.</summary>
    private int RunLauncher(params string[] mode)
    {
        string? output = LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the Windows launcher's build output is not beside the tests (build src/Tsuki.slnx on Windows)");
        }

        var info = new ProcessStartInfo(Path.Combine(output, GameLauncher.LauncherFileName)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = output };
        foreach (string a in (string[])[.. mode, "--data", _profile, "--game", GameFolder.Require()])
        {
            info.ArgumentList.Add(a);
        }

        using Process launcher = Process.Start(info)!;
        if (!launcher.WaitForExit(180_000))
        {
            launcher.Kill();
            Assert.Fail("the launcher did not end within three minutes");
        }

        return launcher.ExitCode;
    }

    [GameDataFact]
    public void TheRealLauncherShowsAnEarnedRowsIconCutFromTheGameFolderAndALockedRowsTile()
    {
        string report = Path.Combine(_root, "selftest.txt");
        int exit = RunLauncher("--selftest", report);
        string text = File.Exists(report) ? File.ReadAllText(report) : "(no report)";
        Assert.True(exit == 0, text);
        Assert.DoesNotContain("FAIL", text, StringComparison.Ordinal);
        foreach (string line in new[]
        {
            // nothing at the launcher's start; then, on the page with the sample's achievements 1, 2 and 5 earned:
            "ok    before the Achievements page is shown nothing is made for it (0 icons asked for, 0 rows and 0 grades with a picture)",
            "ok    the 27 locked rows show the locked tile of their grade (4 tiles) and no game picture was asked for a locked achievement",
            "ok    the earned rows show their icons, cut from the player's game pictures (1 Icon, 2 Icon, 5 Icon; asked for: 1 x 1, 2 x 1, 5 x 1)",
            "ok    the page draws the picture of each of its 30 rows (30 squares show their row's bitmap)",
            "ok    a poll that reads the same achievements again makes no picture again: every row is the same row with the same bitmap, and no icon is asked for",
            "ok    Check again forgets the icons and has them made again (asked for: 1 x 2, 2 x 2, 5 x 2); every row keeps its bitmap, the pixels being the same",
            "ok    the achievements that are locked again show the locked tile, and no icon was asked for over that",
            "ok    the summary card shows the emblem of each grade, a cup drawn by the port's own code (Platinum emblem, Gold emblem, Silver emblem, Bronze emblem); the names are the game's chapter titles",

            // the notice and its sound (D7): off while settings.ini says nothing, with a game folder as without one, and left as the profile had them
            $"ok    the page shows the Notify switch and, in the next row, the sound switch (off, off; settings.ini: trophy_notice unset, trophy_sound unset), heard by their labels ({LauncherTexts.AchievementsNoticeLabel}; {LauncherTexts.AchievementsSoundLabel})",
            "ok    the Notify switch is off while settings.ini does not say otherwise, and writes trophy_notice=on and trophy_notice=off, leaving trophy_sound as it was",
            "ok    the sound switch is off while settings.ini does not say otherwise, and writes trophy_sound=on and trophy_sound=off, leaving trophy_notice as it was",
            "ok    the switches' checks put trophy_notice and trophy_sound back as settings.ini had them (trophy_notice unset, trophy_sound unset), and the switches show them (off, off)",
        })
        {
            Assert.Contains(line, text, StringComparison.Ordinal);
        }

        // an achievement earned while the page is open: its icon is made (the blank tile first, unless the icon was there before anybody looked), never the emblem
        string earned = text.Split('\n').Single(l => l.StartsWith("ok    an achievement earned while the page is open (14) has its icon made: asked for once, never the emblem meanwhile (", StringComparison.Ordinal));
        Assert.Contains(", then Icon); the other rows keep their bitmaps", earned, StringComparison.Ordinal);

        // a restore and an import never remove an achievement (D1): with the game folder of the launcher the replaced save's day 1 is recorded first too, not
        // only its lesson (while a game runs on this machine both leave the record to it)
        // (the self-test looks for a game every 20 ms of each step and judges the step by what it found, and it has no FAIL line, above; a step during which a
        // game started or ended, the tests' own runs of the game among them, is not judged)
        foreach ((string recorded, string leftToTheGame, string notJudged) in new[]
        {
            ("ok    importing a PS4 save first records what the replaced save implied, the chapter told by the game folder (the page showed 1, 28; trophies.ini holds 1, 28, without a date)",
                "ok    a game runs on this machine: an import leaves the record to it (trophies.ini holds )",
                "ok    a game started or ended on this machine during the import: the record is not judged"),
            ("ok    restoring an older backup first records what the replaced save implied (the page showed 1, 28; trophies.ini holds 1, 28, without a date), and the page still shows them",
                "ok    a game runs on this machine: a restore leaves the record to it (trophies.ini holds )",
                "ok    a game started or ended on this machine during the restore: the record is not judged"),
        })
        {
            Assert.True(text.Contains(recorded, StringComparison.Ordinal) || text.Contains(leftToTheGame, StringComparison.Ordinal) || text.Contains(notJudged, StringComparison.Ordinal), recorded + "\n\n" + text);
        }

        // the self-test's sample, and the save step's save and record, are gone again: no made-up achievement stays in a profile
        Assert.False(File.Exists(Path.Combine(_profile, TrophyLog.FileName)));
        Assert.False(File.Exists(Path.Combine(_profile, "save.dat")));
        Tsuki.App.Services.AppSettingsData left = Tsuki.App.Services.AppSettings.Load(Path.Combine(_profile, Tsuki.App.Services.AppSettings.FileName));
        Assert.Null(left.TrophyNotice);
        Assert.Null(left.TrophySound);
    }

    [GameDataFact]
    public void TheRenderTakesThePagesPicturesWhenTheIconsAreThere()
    {
        // a profile with five achievements earned, one of each grade among them (the platinum's row too: the record is the game's, the launcher shows what it says)
        int[] earned = [0, 1, 5, 14, 29];
        var log = new TrophyLog();
        foreach (int id in earned)
        {
            Assert.True(log.Add(id, new DateTime(2026, 11, 12, 13, id, 0, DateTimeKind.Utc)));
        }

        log.Write(Path.Combine(_profile, TrophyLog.FileName));
        string before = File.ReadAllText(Path.Combine(_profile, TrophyLog.FileName));
        string pictures = Path.Combine(_root, "render");
        int exit = RunLauncher("--render", pictures, "--page", "achievements");
        string error = Path.Combine(pictures, "render_error.txt");
        Assert.True(exit == 0 && !File.Exists(error), File.Exists(error) ? File.ReadAllText(error) : "exit code " + exit);

        // when the page's first picture was taken every earned row held its icon: the render waited for the worker (a Blank here is a picture of the tiles)
        string[] rows = File.ReadAllLines(Path.Combine(pictures, "achievements_rows.txt"));
        Assert.Equal(
            Enumerable.Range(0, TrophyTable.Count).Select(id => earned.Contains(id) ? $"{id} earned Icon" : $"{id} locked Locked"),
            rows);

        // the three tabs at the window's size and at its smallest size
        (string Name, int Width, int Height)[] expected =
        [
            ("achievements.png", 1200, 780), ("achievements_earned.png", 1200, 780), ("achievements_locked.png", 1200, 780),
            ("achievements_min.png", 960, 600), ("achievements_earned_min.png", 960, 600), ("achievements_locked_min.png", 960, 600),
        ];
        foreach ((string name, int width, int height) in expected)
        {
            byte[] png = File.ReadAllBytes(Path.Combine(pictures, name));
            Assert.True(png.Length > 20_000, name + " is a picture of a page");
            Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));           // the IHDR chunk: width, height
            Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        }

        // the list with icons is another picture than the list of locked tiles, and the render left the record alone
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(pictures, "achievements_earned.png")), File.ReadAllBytes(Path.Combine(pictures, "achievements_locked.png")));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_profile, TrophyLog.FileName)));
    }
}
