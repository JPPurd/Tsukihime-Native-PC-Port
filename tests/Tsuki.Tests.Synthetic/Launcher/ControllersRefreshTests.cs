// v0.1.9, FIXES D12 P-1: the Windows launcher's controller list is read beside the window's refresh and never in its way (ControllerList in
// src/Tsuki.Launcher/Core/Controllers.cs, compiled into these tests). Nobody had a pad to try the 0.1.9 probe on, so whatever a device does to it (slow to name
// itself, never answering, an exception) may cost the names on the Controls page and nothing else. Here: a refresh with the shape of LauncherModel.RefreshAsync
// around a stand-in probe that does not answer, throws or answers late; the thread the list is taken on; and, because the WPF view model cannot be compiled
// here, the pins in LauncherModel.cs that its refresh has that shape. No device is needed.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Tsuki.Launcher.Core;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed class ControllersRefreshTests
{
    /// <summary>The bound of "it ends": generous, and never the measure of anything.</summary>
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<string> _applied = new();
    private readonly ConcurrentQueue<string> _log = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// A refresh with the shape of LauncherModel.RefreshAsync (pinned in its source by the last test of this file): the reading of the list is started first,
    /// the game folder is read on a worker thread and applied without it, and the short wait for the list is the last step.
    /// </summary>
    private async Task RefreshAsync(ControllerList list, Func<string> folder)
    {
        Task controllers = list.ReadAsync();
        string state = await Task.Run(folder, Cancel);
        _applied.Enqueue(state);
        await list.WaitBrieflyAsync(controllers);
    }

    /// <summary>A list whose refresh waits as long as it takes for a probe that answers (the launcher's own wait is 2 s), or <paramref name="wait"/> for one that does not.</summary>
    private ControllerList List(Func<IReadOnlyList<string>> probe, TimeSpan? wait = null) => new(probe, _log.Enqueue) { RefreshWait = wait ?? Long };

    // -- a device that does not answer -----------------------------------------------------------------------------------

    [Fact]
    public async Task AProbeThatDoesNotAnswerNeverHoldsTheGameFolderAndItsListIsTakenWhenItComes()
    {
        using var answer = new ManualResetEventSlim();
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ControllerList list = List(
            () =>
            {
                answer.Wait(Cancel);
                return ["Late pad"];
            },
            TimeSpan.FromMilliseconds(200));
        list.Changed += () => taken.TrySetResult();
        try
        {
            // the refresh ends, with the game folder's state applied, while the device has still not answered (a refresh that waited for the list would end
            // only when the test lets the device answer, which it does after this)
            await RefreshAsync(list, () => "the folder is ready").WaitAsync(Long, Cancel);
            Assert.Equal(["the folder is ready"], _applied);
            Assert.False(answer.IsSet);
            Assert.Empty(list.Names);
            Assert.False(taken.Task.IsCompleted);
        }
        finally
        {
            answer.Set();
        }

        // the list is taken when it comes, however late
        await taken.Task.WaitAsync(Long, Cancel);
        Assert.Equal(["Late pad"], list.Names);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task ADeviceThatNeverAnswersHoldsOneWorkerHoweverManyRefreshesCome()
    {
        // the list's reader as the launcher has it: Controllers.Probe with its turn, here around a device list that does not come back
        var turn = new Controllers.ProbeTurn();
        var pad = new Controllers.RawDevice(Controllers.RimTypeHid, @"\\?\HID#VID_054C&PID_0CE6&MI_03#8&01&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 0x054C, 0x0CE6, Controllers.GenericDesktop, Controllers.Gamepad);
        using var answer = new ManualResetEventSlim();
        int asked = 0;
        IReadOnlyList<string> Probe() => Controllers.Probe(
            turn,
            () =>
            {
                Interlocked.Increment(ref asked);
                answer.Wait(Cancel);
                return [pad];
            },
            (_, _) => "Pad",
            _ => null);
        ControllerList list = List(Probe, TimeSpan.FromMilliseconds(100));
        try
        {
            for (int i = 1; i <= 4; i++)
            {
                await RefreshAsync(list, () => "ready").WaitAsync(Long, Cancel);
            }

            // every refresh applied its folder; only the first is in the device's hands, the others had the last list (none yet) at once
            Assert.Equal(["ready", "ready", "ready", "ready"], _applied);
            Assert.Equal(1, Volatile.Read(ref asked));
            Assert.Empty(list.Names);
        }
        finally
        {
            answer.Set();
        }

        // once it does answer, its list is the last list, and the next refresh has it
        await WaitUntilAsync(() => turn.Last.Count == 1);
        await RefreshAsync(list, () => "ready").WaitAsync(Long, Cancel);
        Assert.Equal(["Pad"], list.Names);
        Assert.Empty(_log);
    }

    // -- a probe that throws ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProbeThatThrowsCostsTheListAndNothingElseAndIsSaidOnce()
    {
        Func<IReadOnlyList<string>> probe = () => ["Pad"];
        ControllerList list = List(() => probe());
        int changes = 0;
        list.Changed += () => Interlocked.Increment(ref changes);
        await RefreshAsync(list, () => "ready").WaitAsync(Long, Cancel);
        Assert.Equal(["Pad"], list.Names);
        Assert.Equal(1, Volatile.Read(ref changes));

        // the refresh completes, the folder's state is the right one, and the list stays what it was
        probe = () => throw new InvalidOperationException("the device said no");
        await RefreshAsync(list, () => "ready again").WaitAsync(Long, Cancel);
        Assert.Equal(["ready", "ready again"], _applied);
        Assert.Equal(["Pad"], list.Names);
        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal(
            ["controllers: the list could not be read (InvalidOperationException: the device said no); the Controls page keeps the list it had. Said once per start."],
            _log);

        // whatever the exception: nothing leaves the reading, and nothing more is said
        foreach (Exception thrown in new Exception[] { new UnauthorizedAccessException("no"), new IOException("no"), new NullReferenceException(), new System.ComponentModel.Win32Exception(5), new NotSupportedException() })
        {
            probe = () => throw thrown;
            await list.ReadAsync().WaitAsync(Long, Cancel);
            await RefreshAsync(list, () => thrown.GetType().Name).WaitAsync(Long, Cancel);
            Assert.Equal(["Pad"], list.Names);
        }

        Assert.Single(_log);
        Assert.Equal(7, _applied.Count);
        Assert.Equal(1, Volatile.Read(ref changes));

        // and a probe that works again is taken again
        probe = () => ["Pad", "Second pad"];
        await RefreshAsync(list, () => "ready").WaitAsync(Long, Cancel);
        Assert.Equal(["Pad", "Second pad"], list.Names);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task AProbeThatThrowsAndALogThatThrowsTooStillEndTheReadingAndKeepTheList()
    {
        // the launcher's log catches what a file refuses, not everything; this one throws whatever it is told. A task that faulted and that nobody looked at
        // is reported by the finalizer thread once it is collected: the one of this test is known by its exception
        var logSaidNo = new InvalidOperationException("the log said no");
        var leftBehind = new ConcurrentQueue<AggregateException>();
        void Reported(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Contains(logSaidNo))
            {
                leftBehind.Enqueue(e.Exception);
            }
        }

        Func<IReadOnlyList<string>> probe = () => ["Pad"];
        int told = 0;
        var list = new ControllerList(
            () => probe(),
            _ =>
            {
                Interlocked.Increment(ref told);
                throw logSaidNo;
            })
        { RefreshWait = Long };
        TaskScheduler.UnobservedTaskException += Reported;
        try
        {
            await RefreshAsync(list, () => "ready").WaitAsync(Long, Cancel);
            Assert.Equal(["Pad"], list.Names);
            Assert.Equal(0, Volatile.Read(ref told));

            // the probe throws, and the log that is told so throws too: the reading ends without an exception and the list stays what it was
            probe = () => throw new InvalidOperationException("the device said no");
            Assert.Equal(TaskStatus.RanToCompletion, await EndOfAReadingNobodyLooksAtAsync(list));
            Assert.Equal(["Pad"], list.Names);
            Assert.Equal(1, Volatile.Read(ref told));

            // and nothing is left behind: no task of that reading is reported once everything unreachable was collected and finalized
            await Task.Run(
                () =>
                {
                    for (int i = 0; i < 2; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                },
                Cancel).WaitAsync(Long, Cancel);
            Assert.Empty(leftBehind);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Reported;
        }
    }

    // -- a normal PC -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnANormalPcTheListIsThereWhenTheRefreshReturns()
    {
        // the launcher's --render and --selftest draw and check right after the refresh: its last step waits for the names. Here the probe answers a moment
        // after the folder's state was applied, when a refresh without that step has long returned without the list
        ControllerList list = List(() =>
        {
            SpinWait.SpinUntil(() => !_applied.IsEmpty, Long);
            Thread.Sleep(150);
            return ["Pad", "Second pad"];
        });
        await RefreshAsync(list, () => "ready").WaitAsync(Long, Cancel);

        Assert.Equal(["Pad", "Second pad"], list.Names);
        Assert.Equal(["ready"], _applied);
        Assert.Empty(_log);

        // the wait of the launcher itself is short, and none at all (no timer either) once the list is there
        Assert.Equal(TimeSpan.FromSeconds(2), new ControllerList(() => [], _ => { }).RefreshWait);
        Assert.Same(Task.CompletedTask, list.WaitBrieflyAsync(Task.CompletedTask));
    }

    [Fact]
    public async Task TheProbeRunsOnAWorkerAndItsListIsTakenOnTheThreadThatAsked()
    {
        using var window = new OneThread();
        int probedOn = 0;
        int changedOn = 0;
        ControllerList list = List(() =>
        {
            probedOn = Environment.CurrentManagedThreadId;
            return ["Pad"];
        });
        list.Changed += () => changedOn = Environment.CurrentManagedThreadId;
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Post(_ => ReadOn(list, read), null);

        int afterOn = await read.Task.WaitAsync(Long, Cancel);
        Assert.Equal(["Pad"], list.Names);
        Assert.NotEqual(0, probedOn);
        Assert.NotEqual(window.ThreadId, probedOn);
        Assert.Equal(window.ThreadId, changedOn);
        Assert.Equal(window.ThreadId, afterOn);
    }

    // -- the Windows launcher's own refresh (WPF: pinned in its source) --------------------------------------------------

    [Fact]
    public void TheWindowsLaunchersRefreshReadsTheListBesideTheGameFolderNeverWithIt()
    {
        string launcher = Path.Combine(Repo.Src, "Tsuki.Launcher");
        string model = File.ReadAllText(Path.Combine(launcher, "LauncherModel.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = model.IndexOf("    public async Task RefreshAsync()\n    {\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "RefreshAsync is not in LauncherModel.cs");
        int end = model.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        string refresh = model[start..end];

        // its steps, in their order: the reading of the list starts before the worker that reads the game folder, and waiting for it (shortly) is the last one
        string[] steps =
        [
            "ReadProfile();",
            "Task controllers = _controllers.ReadAsync();",
            "= await Task.Run(() =>",
            "GameFolderCheck.Inspect(remembered);",
            "});",
            "_graphics = graphics;",
            "_monitors = monitors;",
            "_shadImage = shad;",
            "_appExe = GameLauncher.FindApp(appDir);",
            "_folder = folder;",
            "RebuildDerived();",
            "Folder = folder;",
            "FolderMessage = string.Empty;",
            "ReloadBackups();",
            "await LoadAchievementsAsync();",
            "await LoadHeroAsync();",
            "await LoadGameIconAsync();",
            "await _controllers.WaitBrieflyAsync(controllers);",
        ];
        int at = 0;
        foreach (string step in steps)
        {
            int found = refresh.IndexOf(step, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"RefreshAsync no longer has the step '{step}' after the ones before it");
            at = found + step.Length;
        }

        Assert.EndsWith("await _controllers.WaitBrieflyAsync(controllers);", refresh, StringComparison.Ordinal);

        // and nothing else in the method names the reading or the list: each is named twice, where the reading starts and at the final short wait. A third
        // mention is another place that waits for the list ("await controllers;" or a second WaitBrieflyAsync before the folder is applied, and the game folder
        // waits for a device again; "controllers.Wait();", and the window's thread waits for a reading that needs that thread to end). Comments are not counted
        string code = WithoutComments(refresh);
        int reading = Regex.Matches(code, @"\bcontrollers\b").Count;
        int list = Regex.Matches(code, @"\b_controllers\b").Count;
        Assert.True(reading == 2, $"RefreshAsync names the reading (controllers) {reading} times: it is started, and waited for shortly as the last step, and nothing else");
        Assert.True(list == 2, $"RefreshAsync names the list (_controllers) {list} times: its reading is started, and waited for shortly as the last step, and nothing else");

        // the worker that reads the game folder, the adapters, the monitors and the shadPS4 image reads no controller: the list never decides when the folder is shown
        int worker = refresh.IndexOf("= await Task.Run(() =>", StringComparison.Ordinal);
        string folderWork = refresh[worker..refresh.IndexOf("});", worker, StringComparison.Ordinal)];
        Assert.DoesNotContain("ontroller", folderWork, StringComparison.Ordinal);

        // the probe is called once in the launcher: as the reader of the list, whose every exception ControllerList catches; the list tells the page when it changed
        Assert.Contains("        _controllers = new ControllerList(Controllers.Connected, _log.Write);\n", model, StringComparison.Ordinal);
        Assert.Contains("        _controllers.Changed += () => Raise(nameof(ControllersText));\n", model, StringComparison.Ordinal);
        Assert.Contains("public string ControllersText => _controllers.Names.Count == 0 ? \"No controller detected\" : ", model, StringComparison.Ordinal);
        var named = new List<string>();
        foreach (string file in Directory.EnumerateFiles(launcher, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Repo.Relative(file);
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || relative.EndsWith("/Core/Controllers.cs", StringComparison.Ordinal))
            {
                continue;
            }

            named.AddRange(Enumerable.Repeat($"{relative}: Controllers.Connected", ProbeCalls(File.ReadAllText(file))));
        }

        Assert.Equal(["src/Tsuki.Launcher/LauncherModel.cs: Controllers.Connected"], named);

        // and the scan sees what it must: the call itself, in code. A file's name (Controllers.cs) or a comment that names the call is no call
        Assert.Equal(1, ProbeCalls("        _controllers = new ControllerList(Controllers.Connected, _log.Write);\n"));
        Assert.Equal(2, ProbeCalls("var pads = Controllers.Connected();\nIReadOnlyList<string> again = Tsuki.Launcher.Core.Controllers.Connected(); // twice\n"));
        Assert.Equal(0, ProbeCalls("// the list is read in Core/Controllers.cs (Controllers.Connected), beside the refresh\n/// <see cref=\"Controllers.Connected\"/>\n"));
        Assert.Equal(0, ProbeCalls("string file = \"Controllers.cs\"; int n = GameControllers.Connected + Controllers.ConnectedOnes;\n"));
    }

    /// <summary>How often a source text calls the probe (<c>Controllers.Connected</c>) in its code; comments are not code.</summary>
    private static int ProbeCalls(string source) => Regex.Matches(WithoutComments(source), @"\bControllers\.Connected\b").Count;

    /// <summary>A source text without its <c>//</c> comments (the launcher's sources have no other kind).</summary>
    private static string WithoutComments(string source) => Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static async void ReadOn(ControllerList list, TaskCompletionSource<int> read)
    {
        try
        {
            await list.ReadAsync();
            read.TrySetResult(Environment.CurrentManagedThreadId);
        }
        catch (Exception e)
        {
            read.TrySetException(e);
        }
    }

    /// <summary>
    /// Starts a reading and waits for it the way a refresh does, which never looks at how it ended; says how it ended (reading the status takes no exception
    /// off a task) and lets go of the task.
    /// </summary>
    private static async Task<TaskStatus> EndOfAReadingNobodyLooksAtAsync(ControllerList list)
    {
        Task reading = list.ReadAsync();
        await list.WaitBrieflyAsync(reading).WaitAsync(Long, Cancel);
        return reading.Status;
    }

    private static async Task WaitUntilAsync(Func<bool> met)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!met())
        {
            Assert.True(clock.Elapsed < Long, "the wait did not end");
            await Task.Delay(10, Cancel);
        }
    }

    /// <summary>A stand-in for the window's thread: one thread that runs what is posted to it, as a dispatcher does.</summary>
    private sealed class OneThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _posted = [];
        private readonly Thread _thread;

        public OneThread()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public void Dispose()
        {
            _posted.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _posted.Dispose();
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach ((SendOrPostCallback work, object? state) in _posted.GetConsumingEnumerable())
            {
                work(state);
            }
        }
    }
}
