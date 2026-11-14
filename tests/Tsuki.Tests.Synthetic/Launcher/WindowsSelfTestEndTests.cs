// v0.1.9 FIXES D11 W-1: a self-test of the Windows launcher always ends, with a report when one can be written. Each self-test (--selftest, --update-selftest,
// --stamp-selftest) runs in a lambda given to the dispatcher whose task nobody awaits: an exception there was lost, no report was written, Shutdown was never
// called (the mode shuts down only when told), and whoever started the launcher waited until they stopped it. Seen three ways: a trophies.ini that is a
// folder, a game folder that cannot be listed, a report whose folder is not there; and read for a fourth, a display on which no window size preset fits (as
// on a hosted runner), where the self-test indexed the empty list of presets.
// The launcher is a WPF program these tests cannot load, so the rules are read from its source, and the real TsukihimeLauncher.exe of this build is run in
// its verification mode (no window) over a temporary profile for two of the ways. Nobody can change the display from a test: that the self-test passes
// where no preset fits is pinned by what it is written to do and by the size arithmetic it asks, not by a run.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Tsuki.Launcher.Core;
using Tsuki.Platform;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class WindowsSelfTestEndTests : IDisposable
{
    private static readonly string AppFile = Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "LauncherApp.xaml.cs");
    private static readonly string RenderFile = Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "OffscreenRender.cs");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-selftest-end-" + Guid.NewGuid().ToString("N"));

    public WindowsSelfTestEndTests() => Directory.CreateDirectory(_root);

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

    // -- read from the source -----------------------------------------------------------------------------------------

    [Fact]
    public void EverySelfTestCatchesWhatItsRunThrowsAndEndsThroughTheOneGuardedWriteOfItsReport()
    {
        string code = File.ReadAllText(AppFile);
        string startup = WindowsLauncherBoxTests.Body(code, "protected override void OnStartup(");
        foreach ((string mode, string run, string stopped) in new[]
        {
            ("if (selfTest is not null)", "OffscreenRender.SelfTestAsync(", "the self-test stopped"),
            ("if (stampSelfTest is not null)", "IconStampSelfTest.RunAsync(", "the stamp self-test stopped"),
            ("if (updateSelfTest is not null)", "OffscreenRender.UpdateSelfTestAsync(", "the self-test stopped"),
        })
        {
            string block = WindowsLauncherBoxTests.Body(startup, mode);

            // what the run throws becomes a FAIL line of the report
            Assert.Matches(
                @"try\s*\{\s*report = await " + Regex.Escape(run) + @"[^;]*\);\s*\}\s*catch \(Exception ex\)\s*\{(\s*//[^\n]*\n)*\s*report = ""FAIL  " + stopped + @": "" \+ ex \+ Environment\.NewLine;\s*\}",
                block);

            // and the lambda's last statement writes the report and ends the run: no Shutdown and no write of its own that an exception could skip
            Assert.Matches(@"EndSelfTest\([^;]*\);\s*\}\);\s*return;\s*$", block);
            _ = Assert.Single(Regex.Matches(block, @"\bEndSelfTest\("));
            Assert.DoesNotContain("Shutdown(", block, StringComparison.Ordinal);
            Assert.DoesNotContain("WriteReport(", block, StringComparison.Ordinal);
        }

        // the one place a report is written: a write that fails is said on the error stream, and Shutdown comes after it either way, never with 0
        Assert.Equal(2, Regex.Matches(code, @"\bWriteReport\(").Count);                // its declaration and the call below
        string end = WindowsLauncherBoxTests.Body(code, "private void EndSelfTest(string file, string report, bool ok)");
        Assert.Matches(
            @"^\s*try\s*\{\s*WriteReport\(Path\.GetFullPath\(file\), report\);\s*\}\s*catch \(Exception e\)\s*\{\s*Console\.Error\.WriteLine\([^;]*\);\s*ok = false;\s*\}\s*Shutdown\(ok \? 0 : 1\);\s*$",
            end);
    }

    [Fact]
    public void TheSelfTestIndexesNoListOfWindowSizesAndSaysWhenNoPresetFitsTheDisplay()
    {
        string code = File.ReadAllText(RenderFile);
        Assert.DoesNotMatch(@"WindowSizes\s*\[", code);                                 // model.WindowSizes[^1] threw on a display where no preset fits
        Assert.Contains("WindowSizeChoice? big = model.WindowSizes.FirstOrDefault(c => c.Width == 1920) ?? model.WindowSizes.LastOrDefault();", code, StringComparison.Ordinal);

        // the three places that judged a preset say "no preset fits this display" as an ok line there: the fresh profile's size, the sizes offered, the size chosen
        string selfTest = WindowsLauncherBoxTests.Body(code, "public static async Task<string> SelfTestAsync(");
        Assert.Contains("bool presets = model.WindowSizes.Count > 0;", selfTest, StringComparison.Ordinal);
        Assert.Contains("(!presets || model.SelectedWindowSize is { Width: 1600, Height: 900 })", selfTest, StringComparison.Ordinal);
        Assert.Contains("Check(presets ? \"the window sizes offered fit the chosen monitor\" : \"no preset fits this display: the Display page offers no window size\",", selfTest, StringComparison.Ordinal);
        Assert.Matches(
            @"if \(big is null\)\s*\{\s*Check\(""no preset fits this display: [^""]*"", true\);\s*\}\s*else\s*\{\s*Check\(\$""settings\.ini holds window=\{big\.Width\}x\{big\.Height\}""[^;]*\);\s*Check\(""the Display page shows that preset as selected""[^;]*\);\s*\}",
            selfTest);

        // the arithmetic those lines ask: no preset on a display narrower than 1423 px or with a work area lower than 800 px, the smallest one from there on
        Assert.Empty(WindowSizeChoice.For((1024, 728)));                                // a 1024 x 768 display with its task bar
        Assert.Null(WindowSizeChoice.For((1024, 728)).LastOrDefault());
        Assert.Empty(WindowFit.PresetsFor(1422, 1040));
        Assert.Empty(WindowFit.PresetsFor(1920, 799));
        Assert.Equal([(1280, 720)], WindowFit.PresetsFor(1423, 800));
        Assert.Equal(new WindowSizeChoice(1280, 720), WindowSizeChoice.For((1423, 800)).LastOrDefault());
    }

    [Fact]
    public void TheTestsInWhichALauncherRunsItsSelfTestAreLeftOutOnAHostedRunner()
    {
        // the first hosted Windows run of a release must be green, and what a launcher's --selftest measures there (the display, the fonts) cannot be looked
        // at from here: each test that starts one says so and is skipped under GITHUB_ACTIONS (the release steps run them on a Windows desktop)
        string tests = Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Root, "tests", "Tsuki.Tests.Synthetic", "Launcher");
        foreach ((string file, string member) in new[]
        {
            ("WindowsAchievementsPageTests.cs", "public void TheRealLauncherDrivesItsAchievementsPageInTheSelfTest()"),
            ("ExeIconStampTests.cs", "public void TheStampedProgramsStillStartAndTheLauncherPutsTheCrescentBackInItsOwnRunningFolder()"),
            ("UpdateEndToEndTests.cs", "public void TheLauncherUpdatesItsOwnFolderFromTheLoopbackGitHubAndRestartsIntoTheNewFiles()"),
            ("WindowsSelfTestEndTests.cs", "private string " + "Install()"),            // in two parts: this line is not the declaration looked for
        })
        {
            string body = WindowsLauncherBoxTests.Body(File.ReadAllText(Path.Combine(tests, file)), member);
            Assert.Matches(@"^\s*(if \(!OperatingSystem\.IsWindows\(\)\)\s*\{\s*Assert\.Skip\([^;]*\);\s*\}\s*)?if \(Environment\.GetEnvironmentVariable\(""GITHUB_ACTIONS""\) == ""true""\)\s*\{(\s*//[^\n]*\n)*\s*Assert\.Skip\(", body);
        }

        // one more test restarts into a launcher that runs --selftest and is about the launcher before it: it runs there too, and only the restarted
        // launcher's report is not judged on a hosted runner. Until a self-test always ended, that report never came there, and "no report" passed; the
        // first report from that machine must not be what turns the run red
        string restart = WindowsLauncherBoxTests.Body(File.ReadAllText(Path.Combine(tests, "UpdateButtonTests.cs")), "private (string Report, int Exit) PressInTheRealLauncherAndRestart(");
        Assert.Matches(@"if \(Environment\.GetEnvironmentVariable\(""GITHUB_ACTIONS""\) != ""true""\)\s*\{(\s*//[^\n]*\n)*\s*Assert\.DoesNotContain\(""FAIL"", after, StringComparison\.Ordinal\);\s*\}", restart);
        _ = Assert.Single(Regex.Matches(restart, @"Assert\.[A-Za-z]+\("));               // nothing else there judges the restarted launcher
    }

    // -- the real launcher --------------------------------------------------------------------------------------------

    /// <summary>A copy of this build's launcher in the test's folder; the test is skipped where there is none, and on a hosted runner.</summary>
    private string Install()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            // nobody can look at a hosted runner's display or fonts from here, and the older tests in which a launcher runs --selftest never got a report
            // there (ExeIconStampTests, UpdateEndToEndTests); on a Windows desktop these pass, and the release steps run them there (PUBLISHING 4.1)
            Assert.Skip("runs the WPF launcher's --selftest, which no test of this kind has yet passed on GitHub's hosted Windows runner; run it on a Windows desktop");
        }

        string? output = UpdateEndToEndTests.LauncherOutput();
        if (output is null)
        {
            Assert.Skip("the launcher's build output is not beside the tests (build src/Tsuki.slnx)");
        }

        return UpdateEndToEndTests.CopyInstall(output, Path.Combine(_root, "Tsukihime"));
    }

    /// <summary>
    /// Runs the copied launcher's self-test (no window, no game folder, the profile <paramref name="data"/>) and returns its exit code and its error stream. A
    /// launcher that does not end is stopped (the process started here, through its own object) and the test fails: that is what happened before the fix.
    /// </summary>
    private static (int Exit, string Error) SelfTest(string install, string report, string data)
    {
        var info = new ProcessStartInfo(Path.Combine(install, GameLauncher.LauncherFileName)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install, RedirectStandardError = true };
        foreach (string a in new[] { "--selftest", report, "--data", data })
        {
            info.ArgumentList.Add(a);
        }

        info.Environment.Remove("TSUKI_GAME_DIR");
        using Process launcher = Process.Start(info)!;
        Task<string> error = launcher.StandardError.ReadToEndAsync();
        Assert.True(UpdateEndToEndTests.EndedOrStopped(launcher, 120_000), "the launcher's self-test did not end within two minutes (it was stopped)");
        return (launcher.ExitCode, error.Wait(10_000) ? error.Result : "(the error stream did not close)");
    }

    [Fact]
    public void ASelfTestThatMeetsAnExceptionEndsWithAFailLineInItsReport()
    {
        string install = Install();

        // a folder named trophies.ini: the page shows an empty record, and the self-test's write of its sample record over it throws
        string data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(data, "trophies.ini"));
        string report = Path.Combine(_root, "selftest.txt");

        (int exit, string error) = SelfTest(install, report, data);

        Assert.True(File.Exists(report), $"no report was written (exit {exit}; error stream: {error})");
        string text = File.ReadAllText(report);
        Assert.True(exit == 1, text);
        Assert.StartsWith("FAIL  the self-test stopped: System.", text, StringComparison.Ordinal);
        Assert.Contains("Exception", text.Split('\n')[0], StringComparison.Ordinal);   // the exception itself, with its trace on the lines that follow
        Assert.False(File.Exists(Path.Combine(data, "launcher_crash.log")));           // caught, not left to the handler of exceptions nobody caught
        Assert.True(Directory.Exists(Path.Combine(data, "trophies.ini")));             // and the profile's odd folder is as it was
    }

    [Fact]
    public void ASelfTestWhoseReportCannotBeWrittenStillEndsAndNotWithZero()
    {
        string install = Install();

        // the report's folder is not there: every check passes, and then the report has nowhere to go
        string data = Path.Combine(_root, "data");
        string report = Path.Combine(_root, "no-such-folder", "selftest.txt");

        (int exit, string error) = SelfTest(install, report, data);

        Assert.True(exit == 1, $"exit {exit}; error stream: {error}");
        Assert.Contains("the self-test's report could not be written to " + report, error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(report)));
    }

    // -- the tests' own waiting ---------------------------------------------------------------------------------------

    [Fact]
    public void ATestStopsTheProcessItStartedWhenThatDoesNotEndAndLeavesOneThatEndsAlone()
    {
        // a program that waits for input nobody gives (Windows: the command prompt's pause on a pipe; elsewhere sleep)
        string program = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sleep";
        if (!File.Exists(program))
        {
            Assert.Skip(program + " is not on this machine");
        }

        ProcessStartInfo Waiting(params string[] args)
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, WorkingDirectory = _root };
            foreach (string a in args)
            {
                info.ArgumentList.Add(a);
            }

            return info;
        }

        using (Process waits = Process.Start(OperatingSystem.IsWindows() ? Waiting("/d", "/c", "pause") : Waiting("600"))!)
        {
            var clock = Stopwatch.StartNew();
            Assert.False(UpdateEndToEndTests.EndedOrStopped(waits, 1000));
            Assert.True(waits.HasExited, "the process the test started is gone once the wait has run out");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"stopping it took {clock.Elapsed.TotalSeconds:0.0} s");
        }

        using Process ends = Process.Start(OperatingSystem.IsWindows() ? Waiting("/d", "/c", "exit 3") : Waiting("0"))!;
        Assert.True(UpdateEndToEndTests.EndedOrStopped(ends, 60_000));
        Assert.Equal(OperatingSystem.IsWindows() ? 3 : 0, ends.ExitCode);
    }
}
