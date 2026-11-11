using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tsuki.Tests.Data.App;

/// <summary>
/// Issue #13 on the real game, in the real program (hidden, muted, a temporary profile): the scene 03_00_ARC03_5B (script 70) has one frame that draws more
/// picture tiles than the 512 MiB texture cache of a card with 1.5 GB or less. The run asks for that cache (<c>TSUKI_TEXTURE_CACHE_MIB</c>), jumps to the scene's
/// flowchart node and skips through it the way real play runs (no <c>--fast</c> and no screenshots, so a frame whose textures are not all resident is held).
/// Before the fix the frame's own uploads evicted its first tiles, the frame was never complete, and the game stopped stepping at it for ever (step 956 of this
/// run, the script's cursor near 53000): the process never reached its <c>--exit-after</c>.
/// </summary>
public sealed partial class SmallCacheSceneTests : IDisposable
{
    private const int Node = 356;                       // the flowchart node of the scene
    private const int Script = 70;                      // its script
    private const int CursorPastTheFrame = 54000;       // the frame comes while the cursor is between 50952 and 53351
    private const int Steps = 1200;                     // the frame is step 956 with the skipping autoplay; 20 s at 60 steps a second

    // the run takes about 25 s; a frozen game is stopped after this long (a full parallel run of the data tests slows the game down, never this much)
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(180);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-i13-scene-" + Guid.NewGuid().ToString("N"));

    public SmallCacheSceneTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [GameDataFact]
    public void TheSceneWhoseFrameIsLargerThanASmallCardsCacheIsPlayedThrough()
    {
        string game = GameFolder.Require();
        string app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Tsukihime.exe" : "Tsukihime");
        string log = Path.Combine(_root, "run.log");
        string save = Path.Combine(_root, "profile");
        string tour = Path.Combine(_root, "tour.txt");
        Directory.CreateDirectory(save);
        File.WriteAllText(tour, Node.ToString(CultureInfo.InvariantCulture) + "\n");

        var info = new ProcessStartInfo(app) { WorkingDirectory = Path.GetDirectoryName(app)!, UseShellExecute = false };
        info.Environment["SDL_AUDIO_DRIVER"] = "dummy";
        info.Environment["TSUKI_TEXTURE_CACHE_MIB"] = "512";
        foreach (string a in new[]
        {
            "--game", game, "--log", log, "--hidden", "--mute", "--windowed", "--save-dir", save,
            "--autoplay", "skip", "--sweep-tour", tour, "--tour-end", "--tour-cap", "120000",
            "--script", "900:log;1000:log;1100:log;1190:log", "--exit-after", Steps.ToString(CultureInfo.InvariantCulture),
        })
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        bool exited = process.WaitForExit(Limit);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);          // the game this test started, and nothing else
            process.WaitForExit(10_000);
        }

        // the log holds lines with the story's text (the state lines): a failure quotes only step numbers and the lines this test is about
        string text = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
        string[] logLines = text.Split('\n', StringSplitOptions.TrimEntries);
        string? Line(string marker) => logLines.FirstOrDefault(l => l.Contains(marker, StringComparison.Ordinal));
        void None(string marker, string what)
        {
            string? line = Line(marker);
            Assert.True(line is null, $"{what}: {line}");
        }

        int lastStep = StepLine().Matches(text) is { Count: > 0 } steps ? int.Parse(steps[^1].Groups[1].ValueSpan, CultureInfo.InvariantCulture) : -1;
        Assert.True(exited, $"the game froze: it did not reach step {Steps} in {Limit.TotalSeconds:F0} s and stopped logging at step {lastStep}");
        Assert.Equal(0, process.ExitCode);
        Assert.True(Line("texture cache: 512 MiB (") is not null, "the run did not get the 512 MiB cache it asked for");
        Assert.True(Line($"tour: node {Node} (scene ") is not null, $"the run did not jump to node {Node}");
        Assert.True(Line($"done: {Steps} steps in ") is not null, $"the run did not end after {Steps} steps (last step {lastStep})");

        // no hold was given up and no frame was drawn with a hole
        None("gave up holding for textures", "a hold for textures was given up");
        None("textures of this frame are not resident", "a frame was drawn without all of its textures");
        None("texture decode failed", "a tile of the game did not decode");

        // the frame was reached, was larger than the cache, and all of its textures stayed resident for it
        Match frame = OverCapLine().Match(text);
        Assert.True(
            frame.Success,
            "the log has no \"this frame draws N MiB of textures, more than the cache's 512 MiB cap\" line: the frame's textures did not all stay resident, or the run no longer reaches the frame of issue #13");
        Assert.True(int.Parse(frame.Groups[1].ValueSpan, CultureInfo.InvariantCulture) > 512);
        Assert.Equal(Script, int.Parse(frame.Groups[2].ValueSpan, CultureInfo.InvariantCulture));

        // and the story went on behind it
        MatchCollection states = StateLine().Matches(text);
        Assert.True(states.Count > 0, "the log has no state line");
        int script = int.Parse(states[^1].Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        int cursor = int.Parse(states[^1].Groups[3].ValueSpan, CultureInfo.InvariantCulture);
        Assert.True(script != Script || cursor > CursorPastTheFrame, $"the story is at cursor {cursor} of script {script} at step {states[^1].Groups[1].Value}: it did not pass the frame");
    }

    [GeneratedRegex(@"^\[ *(\d+)\] ", RegexOptions.Multiline)]
    private static partial Regex StepLine();

    [GeneratedRegex(@"^\[ *\d+\] this frame draws (\d+) MiB of textures, more than the cache's 512 MiB cap: they all stay resident for it \(script (\d+)\)\r?$", RegexOptions.Multiline)]
    private static partial Regex OverCapLine();

    [GeneratedRegex(@"^\[ *(\d+)\] state: [^\n]*?, script (\d+) \(scene \d+\) cursor (\d+),", RegexOptions.Multiline)]
    private static partial Regex StateLine();
}
