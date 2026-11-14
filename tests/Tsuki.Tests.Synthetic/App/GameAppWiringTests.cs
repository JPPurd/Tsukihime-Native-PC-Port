using System.Text.RegularExpressions;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// The call sites in GameApp that no run of the game reaches in a test (every test start is --hidden, and a hidden window never goes fullscreen), read from the
/// source: when Windows asks for fullscreen (at once only for --fullscreen, else once the game is set up, as v0.1.6 did), what SDL's fullscreen events and
/// Alt+Enter / F11 are told to, the Steam Deck's request when the window is shown, the loop's SIGTERM, and the order of the guard and the default log file.
/// </summary>
public sealed partial class GameAppWiringTests
{
    private static readonly Lazy<string> Code = new(() =>
    {
        string text = File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.App", "Play", "GameApp.cs"));
        return Whitespace().Replace(LineComment().Replace(text, "$1"), string.Empty);
    });

    // TextureFeeder.cs the same way (issue #13)
    private static readonly Lazy<string> Feeder = new(() =>
        Whitespace().Replace(LineComment().Replace(File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.App", "Play", "TextureFeeder.cs")), "$1"), string.Empty));

    // the text between two markers of the whitespace-free source
    private static string Between(string from, string to)
    {
        string code = Code.Value;
        int start = code.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"GameApp.cs no longer has {from}");
        int end = code.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"GameApp.cs has no {to} after {from}");
        return code[start..end];
    }

    [Fact]
    public void TheWindowAsksForFullscreenAtShowOnlyForTheCommandLineAndTheSteamDeck()
    {
        string show = Between("if(!_hidden){Placementplaced=_startup.PlaceWindow(_window);", "_device=GlDevice.Create(");
        Assert.Contains("_window.Show();if(_startMode.FullscreenAtShow){boolaccepted=_window.SetFullscreen(true);", show, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRememberedFullscreenIsAskedForOnceTheGameIsSetUp()
    {
        string setUp = Between("privatevoidSetUpInput()", "privateintLoop()");
        Assert.Contains("if(!_hidden&&_startMode.FullscreenAfterSetUp){boolaccepted=_window.SetFullscreen(true);", setUp, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(Code.Value, Regex.Escape("SetFullscreen(true)")));      // and nowhere else
    }

    [Fact]
    public void SdlsFullscreenEventsGoToTheEventRuleAndTheHotkeysAreRemembered()
    {
        string loop = Between("privateintLoop()", "privatevoidRequestScreenshot(");
        Assert.Contains("casePlatformEventKind.EnteredFullscreen:casePlatformEventKind.LeftFullscreen:_display?.NoteFullscreenEvent(e);", loop, StringComparison.Ordinal);
        Assert.Contains("casePlatformEventKind.FullscreenToggled:_display?.NoteFullscreen(e.Fullscreen);", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("NoteFullscreen(e.Kind", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void ASigtermEndsTheLoopBeforeTheLastSaveAndIsHeldBackOnlyUntilThen()
    {
        string loop = Between("privateintLoop()", "privatevoidRequestScreenshot(");
        int listen = loop.IndexOf("_terminate=TerminationSignal.Listen();", StringComparison.Ordinal);
        int body = loop.IndexOf("while(running){", StringComparison.Ordinal);
        int check = loop.IndexOf("if(running&&_terminate.Requested){running=false;stop=\"SIGTERM\";}", StringComparison.Ordinal);
        int save = loop.IndexOf("_services.CommitAtQuit();_terminate.Dispose();", StringComparison.Ordinal);
        Assert.True(listen >= 0 && listen < body, "the loop listens for SIGTERM before it runs");
        Assert.True(check > body && check < save, "the loop ends on SIGTERM and saves");
    }

    [Fact]
    public void TheDefaultLogFileIsOpenedOnlyByTheGameThatHoldsTheProfile()
    {
        string run = Between("publicstaticintRun(AppArgsargs)", "internalstaticstringPlayerMessage(");
        int guard = run.IndexOf("SingleInstance.TryAcquire(dataDir);", StringComparison.Ordinal);
        int refused = run.IndexOf("returnExitAlreadyRunning;", StringComparison.Ordinal);
        int defaultLog = run.IndexOf("DefaultLogFile(args,dataDir,OperatingSystem.IsWindows())", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < refused && refused < defaultLog, "a second game is refused before it opens last_run.log");
    }

    // FIXES D11 (the checker of G-1): a commit cut off between its two renames is finished by the game at its start: once it holds the profile's guard, only
    // when the profile is used, and before the save file is read. Nobody else may do it: for an instant a running game's commit looks the same on the disk,
    // and a launcher that finished it would take that game's temporary file away. (The start itself is run on the real game in ExitFromTitleTests.)
    [Fact]
    public void ACommitCutOffBetweenItsRenamesIsFinishedByTheGameAloneBeforeItReadsTheSaveFile()
    {
        string load = Between("privateSaveStoreLoadStore()", "privatestringQuarantine()");
        Assert.Contains("if(!_args.NoSave){try{if(SaveFile.FinishInterruptedCommit(_savePath)is{}finished){_log.WriteLine(", load, StringComparison.Ordinal);
        int finish = load.IndexOf("SaveFile.FinishInterruptedCommit(_savePath)", StringComparison.Ordinal);
        int read = load.IndexOf("store=SaveFile.ReadWithFallback(_savePath,outboolbackup);", StringComparison.Ordinal);
        Assert.True(finish >= 0 && read > finish, "the cut-off commit is finished before the save file is read");

        string run = Between("publicstaticintRun(AppArgsargs)", "internalstaticstringPlayerMessage(");
        int guard = run.IndexOf("SingleInstance.TryAcquire(dataDir);", StringComparison.Ordinal);
        int refused = run.IndexOf("returnExitAlreadyRunning;", StringComparison.Ordinal);
        int game = run.IndexOf("usingvarapp=newGameApp(args,log);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < refused && refused < game, "a second game is refused before it touches the profile's save files");

        var named = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Repo.Src, "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && File.ReadAllText(file).Contains("FinishInterruptedCommit", StringComparison.Ordinal))
            {
                named.Add(Path.GetRelativePath(Repo.Src, file).Replace('\\', '/'));
            }
        }

        Assert.Equal(["Tsuki.App/Play/GameApp.cs", "Tsuki.Engine/Save/SaveFile.cs"], named.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheExtraRunLogLinesAndBoxesAreLinuxAndMacOSOnly()
    {
        // every line that writes one of them sits in a block whose condition keeps Windows out (its log stays v0.1.6's)
        string[] lines = File.ReadAllLines(Path.Combine(Repo.Src, "Tsuki.App", "Play", "GameApp.cs"));
        string[] marks = ["RunReport.Sdl(", "RunReport.Steam(", "RunReport.WindowState(", "RunReport.PadAtStart(", "RunReport.AudioDevice(", "audioWatch.Note(", "presentWatch.Note(",
            "window: entered fullscreen", "window: fullscreen refused by SDL", "window mode: SDL refused", "pixels, fullscreen", "the --game folder", "CommandLineFolderMessage(_args"];
        int found = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!marks.Any(m => lines[i].Contains(m, StringComparison.Ordinal)) || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) || lines[i].Contains("internal static", StringComparison.Ordinal))
            {
                continue;
            }

            found++;
            int indent = Indent(lines[i]);
            bool gated = WindowsGate().IsMatch(lines[i]);
            for (int j = i - 1; j > 0 && indent > 0 && !gated; j--)
            {
                if (lines[j].Trim().Length == 0 || Indent(lines[j]) >= indent)
                {
                    continue;
                }

                // an enclosing line: the block's brace (whose header is the line above it) or a case label
                indent = Indent(lines[j]);
                gated = WindowsGate().IsMatch(lines[j]) || (lines[j].Trim() == "{" && WindowsGate().IsMatch(lines[j - 1]));
            }

            Assert.True(gated, $"GameApp.cs line {i + 1} is not behind a Linux/macOS condition: {lines[i].Trim()}");
        }

        Assert.Equal(14, found);
    }

    [Fact]
    public void ACommandLineTheGameCannotReadIsRefusedThroughTheReport()
    {
        string main = Whitespace().Replace(LineComment().Replace(File.ReadAllText(Path.Combine(Repo.Src, "Tsuki.App", "Program.cs")), "$1"), string.Empty);
        Assert.Contains("if(!AppArgs.TryParse(args,outAppArgs?a,outstring?error)){Console.Error.WriteLine(error);Console.Error.WriteLine(AppArgs.Usage);"
            + "RefuseCommandLine(args,error??\"?\",hidden,OperatingSystem.IsWindows());returnExitUsage;}", main, StringComparison.Ordinal);
    }

    // Issue #13: the hold for textures. In real play RenderCore does not draw a frame whose textures are not all resident (the picture before it stays), and that
    // hold had no end: a frame that could never be complete froze the game. No test run reaches the hold's end (it needs textures that never come), so the rules
    // are read from the source: the hold is given up through TextureHold, the frame is then drawn, and the feeder pins what the frame draws.
    private static string RenderCore() => Between("privateboolRenderCore(DisplayListlist,refstringstop)", "ExecuteStatsstats=_executor.Execute(list);");

    // v0.1.9, the owner's decision: no picture of the PS4's trophy file is shown, and the game does not open sce_sys/trophy/trophy00.trp at all (the notice's
    // emblems are drawn by the port; the icons are cut from the story's pictures). Nothing in the game's code names the file or its reader
    [Fact]
    public void TheGameNeverOpensThePs4TrophyFile()
    {
        int files = 0;
        foreach (string project in new[] { "Tsuki.App", "Tsuki.Ui", "Tsuki.Engine" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo.Src, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                files++;
                string text = File.ReadAllText(file);
                Assert.False(text.Contains("TrophyPack", StringComparison.Ordinal), $"{file} names the reader of the PS4's trophy file");
                Assert.False(text.Contains("trophy00", StringComparison.OrdinalIgnoreCase) || text.Contains("sce_sys/trophy", StringComparison.OrdinalIgnoreCase), $"{file} names the PS4's trophy file");
                Assert.False(text.Contains("TrophyCup", StringComparison.Ordinal) || text.Contains("CupPng", StringComparison.Ordinal), $"{file} names the PS4's cup pictures");
                Match cup = CupFallback().Match(text);                                  // nor do its comments still say the notice falls back to a cup picture
                Assert.False(cup.Success, $"{file} says the notice shows a cup where it shows the grade's emblem: \"{cup.Value}\"");
            }
        }

        Assert.True(files > 100, $"only {files} source files were read");
        Assert.False(File.Exists(Path.Combine(Repo.Src, "Tsuki.App", "Services", "TrophyCups.cs")));
    }

    [Fact]
    public void TheHoldForTexturesIsGivenUpThroughTheRule()
    {
        string core = RenderCore();
        // real play polls the feeder without waiting, holds only a frame that misses textures, and asks the rule with the hold's age and the tiles in flight
        Assert.Contains("intmissing=_feeder.EnsureResident(list,_holdMode?TimeSpan.Zero:", core, StringComparison.Ordinal);
        // the hold's age is the time the loop waited in it, pass by pass (TextureHold.Age caps a pass: a sleeping computer does not end the hold)
        Assert.Contains(
            "if(_holdMode&&missing>0&&!_holdGivenUp){longpassNow=Stopwatch.GetTimestamp();if(!_held){_held=true;_holdStart=passNow;_holdLastPass=passNow;_holdAgeMs=0;}"
            + "_holdAgeMs=TextureHold.Age(_holdAgeMs,Stopwatch.GetElapsedTime(_holdLastPass,passNow).TotalMilliseconds);_holdLastPass=passNow;"
            + "doubleheldFor=_holdAgeMs;if(!TextureHold.GiveUp(heldFor,_feeder.Pending)){",
            core,
            StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("TextureHold.GiveUp(")));
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("_holdStart=")));                       // the hold starts once, when it begins: a poll does not restart it
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("_holdAgeMs=0")));
    }

    [Fact]
    public void AFrameDrawnWithoutSomeTexturesIsSaidWhenTheCountChangesNotForEveryFrame()
    {
        // real play draws such frames now (a given-up hold, a tile of a damaged file): 60 lines a second went into the player's log
        Assert.Contains(
            "if(stats.MissingTextures!=_loggedMissing){_loggedMissing=stats.MissingTextures;if(stats.MissingTextures>0){TextureCacheStatscacheNow=_cache.Stats;Say($\"{stats.MissingTextures}texturesofthisframearenotresident",
            Code.Value,
            StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("texturesofthisframearenotresident")));
    }

    [Fact]
    public void AGivenUpHoldFallsThroughToDrawingTheFrame()
    {
        string core = RenderCore();
        int rule = core.IndexOf("if(!TextureHold.GiveUp(heldFor,_feeder.Pending)){", StringComparison.Ordinal);
        int givenUp = core.IndexOf(
            "_holdGivenUp=true;Say(TextureHold.GaveUpLine(heldFor,missing,_feeder.Pending,_runtime.Host.Script,_cache.Stats.ResidentBytes,_cache.CapacityBytes));}",
            StringComparison.Ordinal);
        Assert.True(rule >= 0 && givenUp > rule, "RenderCore gives the hold up after asking TextureHold.GiveUp, and says so in the log");

        // while the hold goes on, the picture before the frame is presented and RenderCore leaves: that is the only way out of the hold without a draw
        string holding = core[rule..givenUp];
        Assert.EndsWith("_compositor.Present(hw,hh);_window.Swap();_presents++;}return!_quit;}", holding, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(holding, "return"));

        // a given-up hold leaves nowhere before the frame is executed, and the hold is closed on the way (the "held N ms" line and the counters)
        string after = core[givenUp..];
        Assert.DoesNotContain("return", after, StringComparison.Ordinal);
        Assert.Contains("if(_held){_held=false;", after, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyACompleteFrameAllowsANewHold()
    {
        string core = RenderCore();
        // the flag is cleared right after the feeder's answer and only when nothing is missing: the frames after a given-up hold are drawn, not held one by one
        Assert.Contains("TimeSpan.FromMilliseconds(_hidden?4000:300));if(missing==0){_holdGivenUp=false;}if(_holdMode&&missing>0&&!_holdGivenUp){", core, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("_holdGivenUp=false")));
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("_holdGivenUp=true")));
        Assert.Contains("privatebool_holdGivenUp;", Code.Value, StringComparison.Ordinal);           // a run starts with the hold allowed
    }

    [Fact]
    public void AFrameLargerThanTheCachesCapIsNamedInTheLogOncePerScript()
    {
        // once per script, and again when the frame has grown (TextureHold.SayOverCap)
        Assert.Contains(
            "if(TextureHold.SayOverCap(_cache.PinnedBytes,_cache.CapacityBytes,_runtime.Host.Script,_overCapScript,_overCapBytes)){_overCapScript=_runtime.Host.Script;_overCapBytes=_cache.PinnedBytes;"
            + "Say(TextureHold.OverCapLine(_cache.PinnedBytes,_cache.CapacityBytes,_runtime.Host.Script));}",
            RenderCore(),
            StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("TextureHold.OverCapLine(")));
    }

    [Fact]
    public void AVerificationRunCanAskForASmallerCardsCache()
    {
        // the switch the runs of issue #13 use (a 512 MiB cache on a card with more memory); a value outside 64..8192 MiB, a sign or a blank changes nothing
        string open = Between("longcacheCap=VideoMemory.CacheCapacity(adapter?.Bytes);", "_cache=newTextureCache(_device.Gl,cacheCap,cacheCap/6)");
        Assert.Contains(
            "if(long.TryParse(Environment.GetEnvironmentVariable(\"TSUKI_TEXTURE_CACHE_MIB\"),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,outlongmib)"
            + "&&mibis>=64and<=8192){cacheCap=mib<<20;}",
            open,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheFeederPinsEveryTextureOfTheFrameAndTrimsOnceTheyAreNamed()
    {
        string feeder = Feeder.Value;
        int start = feeder.IndexOf("publicintEnsureResident(DisplayListlist,TimeSpanbudget){", StringComparison.Ordinal);
        int end = feeder.IndexOf("privateintCountMissing()", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "TextureFeeder.cs no longer has EnsureResident before CountMissing");
        string ensure = feeder[start..end];

        // the frame's set starts before its first texture is named
        int begin = ensure.IndexOf("_cache.BeginFrame();", StringComparison.Ordinal);
        int loop = ensure.IndexOf("foreach(refreadonlyDrawCmdcinlist.Commands){", StringComparison.Ordinal);
        Assert.True(begin >= 0 && begin < loop, "BeginFrame comes before the frame's textures are named");
        Assert.Equal(1, Regex.Count(feeder, Regex.Escape("_cache.BeginFrame()")));

        // every texture the frame draws is named in a loop of its own, before the loop that uploads: an upload trims the cache, and a movie frame uploaded in
        // the middle of the list evicted the textures named after it
        const string Naming = "_cache.BeginFrame();foreach(refreadonlyDrawCmdcinlist.Commands){if(c.Kind==DrawKind.TexturedQuad&&c.Texture.Kindisnot(TextureKind.CaptureorTextureKind.None)){_cache.Pin(c.Texture);}}intfresh=0;foreach(refreadonlyDrawCmdcinlist.Commands){";
        Assert.Contains(Naming, ensure, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(feeder, Regex.Escape("_cache.Pin(")));
        Assert.Contains(
            "caseTextureKind.MovieFrame:UploadMovieFrame(key);break;caseTextureKind.Thumbnail:UploadThumbnail(key);break;"
            + "caseTextureKind.UiComposed:UploadComposed(key);break;default:if(!_cache.Contains(key)&&!_workers.HasFailed(key)){_wanted.Add(key);",
            ensure,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_cache.Touch(", feeder, StringComparison.Ordinal);                    // Touch alone leaves the tile to the uploads of its own frame

        // a tile whose decode threw is not waited for (a damaged game file costs that tile's picture, not a hold of every frame that draws it)
        Assert.Contains("if(!_cache.Contains(k)&&!_workers.HasFailed(k)){missing++;", feeder, StringComparison.Ordinal);

        // what a larger frame before left above the cap goes once this frame's textures are named, before any wait
        int trim = ensure.IndexOf("}_cache.TrimUnpinned();if(_wanted.Count==0){UploadSliceAndPrune();return0;}", StringComparison.Ordinal);
        Assert.True(trim > loop, "TrimUnpinned comes after the loop over the frame's commands");
        Assert.Equal(1, Regex.Count(feeder, Regex.Escape("_cache.TrimUnpinned()")));
    }

    [Fact]
    public void TheLookAheadDoesNothingWhileTheFrameFillsTheCacheAndAnUploadedTileIsNoLongerQueued()
    {
        string feeder = Feeder.Value;
        Assert.Contains(
            "publicvoidPrefetch(IReadOnlyList<TextureRef>keys){ArgumentNullException.ThrowIfNull(keys);if(_cache.PinnedBytes>=_cache.CapacityBytes){return;}for(",
            feeder,
            StringComparison.Ordinal);
        // a tile the cache evicted again before the feeder looked stayed "queued" for ever, and the frame that needed it later waited for a decode that never came
        Assert.Contains("_scheduler=newUploadScheduler(cache,_pool){Uploaded=key=>_queued.Remove(key),};", feeder, StringComparison.Ordinal);
    }

    // v0.1.9, notice findings N-1 and N-3: the screenshot key and the save thumbnails take the frame without its overlays (the toast, the achievement notice).
    // The key's frame is normally drawn without them (HideToast), but a frame the loop held for its textures was sealed before the key and still has them: no
    // test run holds a frame, so the call sites are read from the source. What GpuCompositor.ReadPicture and Picture give is OverlayFrameTests' (Render)
    [Fact]
    public void TheScreenshotKeyAndTheSaveThumbnailsTakeTheFrameWithoutItsOverlays()
    {
        string shot = Between("_compositor.Present(pw,ph);if(_shotPending){", "_window.Swap();");
        Assert.Contains("_shotPending=false;_runtime.Ui.HideToast=false;stringpath=_screenshots.Save(pw,ph,_compositor.ReadPicture(pw,ph));", shot, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadWindow(", Code.Value, StringComparison.Ordinal);                  // nothing else reads the window back
        string grab = Between("private(intWidth,intHeight,byte[]Rgba)?GrabThumbnail(){", "}");
        Assert.Contains("byte[]full=_compositor.Read(_compositor.Picture);", grab, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("_runtime.Grab=GrabThumbnail;")));
    }

    // v0.1.9, D7: the achievement notice's chime is played into the game's own mixer, and a muted run (--mute, a hidden window, no sound device: the game's
    // _mute once the media are open) says it without playing it. No test run has a sound device, so the call site is read from the source
    [Fact]
    public void TheRuntimeGetsTheMixerForTheChimeAndKnowsWhenTheRunIsMuted()
    {
        Assert.Equal(1, Regex.Count(Code.Value, Regex.Escape("_runtime=newAppRuntime(_services,ui,_player,_movies){Mixer=_mixer,Muted=_mute};")));
        Assert.True(Code.Value.IndexOf("OpenMedia();OpenRenderer();OpenGame();", StringComparison.Ordinal) >= 0, "the media are not open before the game: the mute could still change");
    }

    // v0.1.9, notice finding N-2: the game clears the frame outside the story and before a story's first frame, which forgets the copy without the overlays
    // (GpuCompositor.Clear): a story's first transition start captures black, as it always did
    [Fact]
    public void TheFrameIsClearedBeforeItIsExecutedOutsideTheStory()
    {
        Assert.Contains(
            "boolstory=_services.StoryActive&&_runtime.Host.Phase==GamePhase.Running;if(!story||!_previousStory){_compositor.Clear(0,0,0,0,255);}_previousStory=story;longte0=Stopwatch.GetTimestamp();ExecuteStatsstats=_executor.Execute(list);",
            Code.Value,
            StringComparison.Ordinal);
    }

    // a pass of the loop behind the clock takes no step past --exit-after (StepLimit; the run of a Data test ended a step past it now and then): the inner loop
    // asks before every step, and the run ends after the pass on the same rule
    [Fact]
    public void ThePassOfTheLoopTakesNoStepPastTheExitAfter()
    {
        string loop = Between("privateintLoop()", "privatevoidRequestScreenshot(");
        Assert.Contains("while(running&&!_held&&!ExitAfterReached&&clock.TryNextTick(now,outLogicTicktick)){frame=tick.Frame;StepOnce(tick,list);", loop, StringComparison.Ordinal);
        Assert.Contains("if(running&&ExitAfterReached){running=false;stop=$\"exit-after{_args.ExitAfter}\";}", loop, StringComparison.Ordinal);
        Assert.Contains("privateboolExitAfterReached=>StepLimit.Reached(_runtime.Frame,_args.ExitAfter);", Code.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("_args.ExitAfter>0&&_runtime.Frame>=", Code.Value, StringComparison.Ordinal);
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    [GeneratedRegex(@"RunReport\.Enabled|OperatingSystem\.IsLinux\(\)|!OperatingSystem\.IsWindows\(\)|\breportSize\b|case PlatformEventKind\.AudioDevice")]
    private static partial Regex WindowsGate();

    [GeneratedRegex(@"(^|\s)//[^\n]*", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // what the comments said before the cups were removed: "drawn pixel for pixel like the cups", "the notice shows the cup", "the cup or the disc"
    [GeneratedRegex(@"\blike the cups\b|\b(shows|draws|falls back to) (the|its) cup\b|\bcup or (the )?disc\b", RegexOptions.IgnoreCase)]
    private static partial Regex CupFallback();
}
