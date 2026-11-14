// The logic of one 60 Hz step of the app (ARCHITECTURE 3.4; package K6), with no window, GPU or audio device in it: the input is routed between the story and the
// menus, the host steps (or its last frame is repeated under a menu), the sound and movie commands go to the media layer, the menus step and draw, and a story
// that ended hands over to the title or the gallery. The GL loop (GameApp) and the tests drive it with InputFrames.
using System.Diagnostics;
using Tsuki.App.Services;
using Tsuki.Engine.Contracts;
using Tsuki.Engine.Game;
using Tsuki.Engine.Trophies;
using Tsuki.Media.Mixer;
using Tsuki.Media.Movie;
using Tsuki.Media.Sound;
using Tsuki.Ui;
using Tsuki.Ui.Screens;

namespace Tsuki.App.Play;

/// <summary>What the runtime did in one step (for logs and tests).</summary>
/// <param name="Frame">The step number.</param>
/// <param name="StoryStepped">The host ran a frame.</param>
/// <param name="UiMode">The UI mode at the end of the step.</param>
/// <param name="Rewound">A rewind step ran.</param>
public readonly record struct StepReport(long Frame, bool StoryStepped, UiMode UiMode, bool Rewound);

/// <summary>The milliseconds one <see cref="AppRuntime.Step"/> spent: the movie clocks and services, the menus' step, the story step (interpreter, stage, sound model, display list), the media commands, the rest.</summary>
public readonly record struct StepTimes(double Movies, double Ui, double Story, double Media, double Rest, double Draw = 0);

/// <summary>The app's per-step logic.</summary>
public sealed class AppRuntime
{
    private const int RewindPeriod = 8;
    private readonly AudioCommandPlayer? _audio;
    private readonly MovieHost? _movies;
    private readonly DisplayList _frozen = new(2048);
    private readonly IReadOnlyList<MovieClock> _noClocks = [];
    private readonly MovieClock[] _clockBuffer = new MovieClock[8];
    private (int Width, int Height, byte[] Rgba)? _heldThumbnail;
    private (int Width, int Height, byte[] Rgba)? _storyThumbnail;
    private (int Width, int Height, byte[] Rgba)? _brightThumbnail;
    private long _brightThumbnailFrame;
    private long _storyThumbnailFrame = -StoryThumbnailGap;
    private GameHost? _thumbnailHost;
    private int _thumbnailRestores;
    private int _rewindHold;
    private bool _topArmed = true;
    private bool _hiddenByPointer;
    private (short X, short Y)? _gestureStart;
    private int _gesture;
    private bool _openSaveScreen;
    private SaveLoadScreen? _promptSave;
    private bool _ending;

    /// <summary>Creates the runtime over the services and the UI.</summary>
    public AppRuntime(AppServices services, UiRoot ui, AudioCommandPlayer? audio, MovieHost? movies)
    {
        Services = services;
        Ui = ui;
        _audio = audio;
        _movies = movies;
        services.Ui = ui;
        services.FrameCapture = () => _heldThumbnail ?? CleanFrame();
        services.SaveScreenRequested += () => _openSaveScreen = true;
        services.StoryEnded += OnStoryEnded;
        services.TrophyEarned += ShowTrophy;
        services.TrophiesFoundInSave += ShowTrophiesFound;
        ui.TrophyNoticeStarted += OnTrophyNoticeStarted;
        HookHost(services.Host);
        services.HostReplaced += HookHost;
    }

    // ---- the achievement notice (v0.1.9) ----------------------------------------------------------------------------------------------------------------
    // The services earn and record; the runtime only shows. A notice is shown only while Config > System > Achievement Notify is On (off until the player turns it
    // on, in Config or on the launcher's Achievements page: a game without the port's switches shows none), and never in a run that writes pictures on its own
    // schedule (PictureRun). What the start found in the save's progress is held back until the title's menu is up and then shown one achievement after the other,
    // in short notices (the title's catch-up; a key the title takes ends it). Each notice shows its achievement's icon, which the runtime has made on a worker
    // (further down), and the chime sounds as a notice comes in when Achievement Sound is On too (at the end of this part).

    private IReadOnlyList<int>? _trophiesFound;
    private bool _noticeInFrame;

    /// <summary>
    /// True in a run that writes pictures on its own schedule (a route sweep, <c>--shot-at</c>, <c>--shot-on-choice</c>): the achievement notice is never shown, so
    /// that those pictures stay the same from version to version whatever the run earns. The achievement is recorded and logged all the same. A script's own
    /// <c>shot=</c> is the tester's picture and does not count.
    /// </summary>
    public bool PictureRun { get; set; }

    private bool TrophyNoticeOn => !PictureRun && Services.Port?.TrophyNotice == true;

    private bool TrophySoundOn => TrophyNoticeOn && Services.Port?.TrophySound == true;

    // the log's words when nothing is shown (v0.1.9 FIXES D10 Q-1: "message" for what appears, "Notify" for the switch, as the Config row and the launchers say)
    private string NoticeOff => PictureRun ? " (no message: this run writes pictures)" : " (no message: Notify is off)";

    /// <summary>
    /// The notice of achievement <paramref name="id"/> (0..29): shown, or only logged when the notice is off. The services' earned event comes here, and so does
    /// the script verb <c>trophy=</c> of the verification runs (which records nothing).
    /// </summary>
    public void ShowTrophy(int id)
    {
        if (id is < 0 or >= TrophyTable.Count)
        {
            Logged?.Invoke($"trophy {id}: not a trophy");
            return;
        }

        if (!TrophyNoticeOn)
        {
            Logged?.Invoke($"trophy {id} earned{NoticeOff}");
            return;
        }

        UiAssets assets = Ui.Context.Assets;
        RequestTrophyIcon(id);
        PrepareChime();
        Ui.ShowTrophy(TrophyTable.Grade(id), UiTrophyText.Heading(assets.Language), UiTrophyText.Name(assets, id), id);
        Logged?.Invoke($"trophy {id} earned");
    }

    // The icon of an achievement's notice is made when the notice is asked for, never at start (30 large pictures read at every start would cost every player for
    // nothing): one worker per achievement, started once in a run however often its notice is asked for. The services have recorded the achievement before they
    // raise the event that comes here, and nothing here waits: the notice itself holds back until the icon is settled (UiTrophyNotice.IconWaitFrames at most). The
    // notices of the title's catch-up are put in line all at once, but their icons are asked for one ahead: for the notice on show and the one after it (each step,
    // RequestShownTrophyIcons), so a line of twenty never starts twenty workers at once and one a key ends costs nothing more. The worker stores what it made; the
    // loop writes its line in the log, and settles the icon of a worker that failed

    private readonly bool[] _trophyIconAsked = new bool[TrophyTable.Count];
    private readonly List<(int Id, Task<string> Work)> _trophyIconWork = [];

    /// <summary>
    /// Makes the icon of achievement N (0..29) for its notice, <see cref="UiTrophyNotice.IconSize"/> pixels a side; null when it cannot be made. The app sets it
    /// over the game folder (<see cref="TrophyIcons.Notice"/>); it is called on a worker thread. Without it (a rig, a run without a game folder) every icon is
    /// settled as "there is none" the moment its notice is asked for, and the notice shows the grade's emblem.
    /// </summary>
    public Func<int, UiComposedPicture?>? TrophyIconMaker { get; set; }

    /// <summary>How many icon workers this runtime has started: one per achievement whose notice was asked for.</summary>
    public int TrophyIconsStarted { get; private set; }

    // how a worker is started: on the thread pool. A test puts its own starter in, to see the moment a worker is asked for and to run it when it likes
    internal Func<Func<string>, Task<string>> TrophyIconStarter { get; set; } = Task.Run;

    /// <summary>
    /// Waits until the icon workers that are running have finished, <paramref name="limit"/> at most; true when none is left running. For a run that steps faster
    /// than the clock (<c>--fast</c>): its frames are no time, so the 45 frames a notice waits for its icon would pass before any worker could finish, and what
    /// the run shows would depend on the machine's load.
    /// </summary>
    public bool WaitForTrophyIcons(TimeSpan limit)
    {
        if (_trophyIconWork.Count == 0)
        {
            return true;
        }

        try
        {
            return Task.WaitAll([.. _trophyIconWork.Select(w => (Task)w.Work)], limit);
        }
        catch (AggregateException)
        {
            // a worker that failed has finished too; the next step says so in the log
            return _trophyIconWork.TrueForAll(w => w.Work.IsCompleted);
        }
    }

    private void RequestTrophyIcon(int id)
    {
        if (_trophyIconAsked[id])
        {
            return;
        }

        _trophyIconAsked[id] = true;
        UiAssets assets = Ui.Context.Assets;
        if (TrophyIconMaker is not { } make)
        {
            assets.SetTrophyIcon(id, null);
            return;
        }

        TrophyIconsStarted++;
        _trophyIconWork.Add((id, TrophyIconStarter(() =>
        {
            long start = Stopwatch.GetTimestamp();
            UiComposedPicture? picture = make(id);
            assets.SetTrophyIcon(id, picture);
            return picture is null
                ? $"achievement icon {id}: not in this game folder's pictures; the message shows the grade's emblem"
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"achievement icon {id}: {picture.Width} px from the game's pictures in {Ms(start, Stopwatch.GetTimestamp()):F0} ms");
        })));
    }

    // the icons of the notice on show and of the one after it (the catch-up's line asks for them here, a notice of its own was asked for already)
    private void RequestShownTrophyIcons()
    {
        if (Ui.TrophyNotice is { } current && current.Id is >= 0 and < TrophyTable.Count)
        {
            RequestTrophyIcon(current.Id);
        }

        if (Ui.NextTrophyNotice is { } next && next.Id is >= 0 and < TrophyTable.Count)
        {
            RequestTrophyIcon(next.Id);
        }
    }

    private void ReportTrophyIcons()
    {
        for (int i = 0; i < _trophyIconWork.Count; i++)
        {
            (int id, Task<string> work) = _trophyIconWork[i];
            if (!work.IsCompleted)
            {
                continue;
            }

            _trophyIconWork.RemoveAt(i--);
            if (work.IsCompletedSuccessfully)
            {
                Logged?.Invoke(work.Result);
            }
            else
            {
                // the worker ended before it stored anything: the notice must not wait for it
                Ui.Context.Assets.SetTrophyIcon(id, null);
                Logged?.Invoke($"achievement icon {id}: not made ({work.Exception?.GetBaseException().Message}); the message shows the grade's emblem");
            }
        }
    }

    /// <summary>
    /// The achievement notice on show, for the state line: its trophy, its name, the picture it shows (icon or emblem; "waiting" until its icon is settled), its alpha
    /// and how many notices wait behind it; "-" for none.
    /// </summary>
    public string TrophyNoticeSummary() => Ui.TrophyNotice is { } notice
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{notice.Id} \"{notice.Name}\" {PictureWord(notice.Picture)} a {notice.Alpha:F2} +{Ui.TrophyNoticesWaiting}")
        : "-";

    private static string PictureWord(UiTrophyPicture picture) => picture switch
    {
        UiTrophyPicture.Icon => "icon",
        UiTrophyPicture.Emblem => "emblem",
        _ => "waiting",
    };

    /// <summary>
    /// The achievements the start found in the save's progress (the services' event; the script verb <c>trophies=</c>): once the title's menu is up, one short notice
    /// for each of them in the order of their ids (the title's catch-up, v0.1.9 D7). Nothing is shown while the notice is off.
    /// </summary>
    public void ShowTrophiesFound(IReadOnlyList<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        if (!TrophyNoticeOn)
        {
            Logged?.Invoke($"trophies from the save's progress: {ids.Count}{NoticeOff}");
            return;
        }

        _trophiesFound = ids;
    }

    private void ShowFoundTrophiesOnTheTitle()
    {
        if (_trophiesFound is not { } ids || Ui.Top is not TitleScreen { MenuShown: true })
        {
            return;
        }

        _trophiesFound = null;
        if (!TrophyNoticeOn)
        {
            Logged?.Invoke($"trophies from the save's progress: {ids.Count}{NoticeOff}");
            return;
        }

        // one after the other in the order of their ids, each with its own icon (asked for as it comes near, RequestShownTrophyIcons) and in the short times; the
        // chime, when it is on, sounds once for the whole line: with the first
        int[] line = [.. ids.Where(id => id is >= 0 and < TrophyTable.Count).Distinct().Order()];
        UiAssets assets = Ui.Context.Assets;
        bool first = true;
        foreach (int id in line)
        {
            Ui.ShowTrophy(TrophyTable.Grade(id), UiTrophyText.Heading(assets.Language), UiTrophyText.Name(assets, id), id, catchUp: true, chime: first);
            first = false;
        }

        if (line.Length > 0)
        {
            PrepareChime();
        }

        RequestShownTrophyIcons();
        Logged?.Invoke($"trophies from the save's progress: {line.Length}, one message each on the title ({string.Join(", ", line)})");
    }

    // ---- the chime (v0.1.9, D7) ---------------------------------------------------------------------------------------------------------------------------
    // Config > System > Achievement Sound (off until the player turns it on; it sounds only while the notice is on too): the owner's short chime as a notice comes
    // in, once for the title's whole catch-up. It plays at the player's sound-effect volume (Config > Sound, "Sound Effects") and 6 dB below it, on a voice of its
    // own (AchievementChime), so it never takes or lowers a voice of the story or the music. It stays silent in skip and fast-forward, while the sound effects are
    // muted (or at 0), and within 1.5 s of the last one; a muted run (--mute, a hidden window, no sound device) has no device to hear it on. The log says
    // "achievement chime" each time it plays

    private long _lastChimeFrame = long.MinValue / 2;

    /// <summary>The mixer the chime plays on; set by the app (null: the chime is decided and logged, and nothing is played).</summary>
    public AudioMixer? Mixer { get; set; }

    /// <summary>True when the run has no sound device to hear anything on (<c>--mute</c>, a hidden window, no device): the chime is not played into the mixer.</summary>
    public bool Muted { get; set; }

    /// <summary>How many chimes this runtime has played (for the tests).</summary>
    public int ChimesPlayed { get; private set; }

    // the chime's samples are made once, on a worker, the first time a notice is put in line while the sound is on (a few milliseconds the loop would otherwise spend
    // in the frame the notice comes in); a test puts its own starter in to see that it is asked for
    internal Action<Action> ChimeStarter { get; set; } = work => Task.Run(work);

    private bool _chimePrepared;

    private void PrepareChime()
    {
        if (TrophySoundOn && !_chimePrepared)
        {
            _chimePrepared = true;
            ChimeStarter(() => _ = AchievementChime.Clip);
        }
    }

    // the UI says a notice starts its way in (UiRoot.TrophyNoticeStarted); internal for the tests of the 1.5 s rule, which no flow of notices reaches by itself
    internal void OnTrophyNoticeStarted(UiTrophyNotice notice)
    {
        if (!notice.Chime || !TrophySoundOn)
        {
            return;
        }

        // the story's skip and fast-forward (the latch, a held key or button, the mode panel's buttons, the drag gesture)
        string? quiet = Services.StoryActive && (Host.SkipLatch || Host.Mode.SkipActive != 0 || Services.FastForwardHeld || _gesture > 0) ? "skip or fast-forward"
            : Services.SoundEffectGain <= 0f ? "the sound effects are muted"
            : Frame - _lastChimeFrame < AchievementChime.MinimumGapFrames ? "less than 1.5 s after the last one"
            : null;
        if (quiet is not null)
        {
            Logged?.Invoke($"no achievement chime for achievement {notice.Id}: {quiet}");
            return;
        }

        float gain = Services.SoundEffectGain * AchievementChime.Attenuation;
        _lastChimeFrame = Frame;
        ChimesPlayed++;
        if (!Muted)
        {
            Mixer?.Post(MixerCommand.Play(AchievementChime.Voice, AchievementChime.Clip, false, gain, 0f));
        }

        Logged?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"achievement chime for achievement {notice.Id} (gain {gain:F3}{(Muted ? "; silent: this run is muted" : string.Empty)})"));
    }

    // A frame that shows the achievement notice is not a save's thumbnail: while one is in the frame the last clean page of this story stands in for it, if it is
    // recent. Without such a page (skip, fast-forward, a story just loaded, a long while without a page) the frame is grabbed, and the renderer's grab never has
    // the notice or the toast in it: UiRoot marks where they begin and the compositor keeps the frame as it was there (GameApp.GrabThumbnail, v0.1.9)
    private (int Width, int Height, byte[] Rgba)? CleanFrame() =>
        _noticeInFrame && _storyThumbnail is { } page && Frame - _storyThumbnailFrame <= BrightThumbnailAge ? page : Grab?.Invoke();

    // _RDAS on a scene the player has completed (GameHost.CmdRdas, with Auto Skip Cleared Scenes on): the engine only raises the offer; the port answers it the
    // way the PS4's mode panel does (knife ui.rs 6177-6184, sub_40ED0 top state 1): the next scene, asked first (dialog 10) when Show Confirmation Message is
    // on, else after a 4-frame countdown (off_AE06B8). Package R1: both config options did nothing before.
    private const int AutoSkipCountdownFrames = 4;
    private bool _autoSkipOffered;
    private int _autoSkipCountdown;

    private void HookHost(GameHost host) => host.AutoSkipOffered += _ => _autoSkipOffered = true;

    private void OfferAutoSkip(bool story)
    {
        if (!story || Services.ReplayActive)
        {
            _autoSkipOffered = false;
            _autoSkipCountdown = 0;
            return;
        }

        if (_autoSkipOffered && !Ui.Active)
        {
            // a menu over the story keeps the offer until it closes
            _autoSkipOffered = false;
            if (Services.Config.SkipConfirm != 0)
            {
                BeginOverlay();
                Ui.Push(new ConfirmDialog(ConfirmKind.NextScene, yes =>
                {
                    if (yes)
                    {
                        Services.NextScene();
                    }
                }));
            }
            else
            {
                _autoSkipCountdown = AutoSkipCountdownFrames;
            }
        }

        if (_autoSkipCountdown > 0 && --_autoSkipCountdown == 0)
        {
            Services.NextScene();
        }
    }

    /// <summary>The game services.</summary>
    public AppServices Services { get; }

    /// <summary>The menus.</summary>
    public UiRoot Ui { get; }

    /// <summary>The interpreter (replaced when the language changes).</summary>
    public GameHost Host => Services.Host;

    /// <summary>The step counter.</summary>
    public long Frame { get; private set; }

    /// <summary>
    /// Reads the last drawn frame as a 320 x 180 thumbnail, without what that frame drew after its <see cref="DrawKind.OverlaysBegin"/> mark (the toast, the
    /// achievement notice; v0.1.9); set by the renderer (null in tests: saves have no thumbnail).
    /// </summary>
    public Func<(int Width, int Height, byte[] Rgba)?>? Grab { get; set; }

    /// <summary>Raised for lines the runtime wants logged.</summary>
    public event Action<string>? Logged;

    /// <summary>True when the player asked to quit: the title's EXIT was confirmed (package K18) or the window's close was confirmed (package K21). The app then ends the run.</summary>
    public bool ExitRequested => Services.QuitRequested;

    /// <summary>True when the quit came from the window's close button or Alt+F4 (confirmed in the quit question, or asked twice), not from the title's EXIT.</summary>
    public bool CloseConfirmed { get; private set; }

    private ConfirmDialog? _closeDialog;

    /// <summary>True while the quit question the window's close request opened is up and unanswered.</summary>
    public bool CloseDialogOpen => _closeDialog is { Waiting: true } d && Ui.Stack.Contains(d);

    /// <summary>
    /// The window's close button or Alt+F4 (package K21): the game's own quit question ("Are you sure you want to quit without saving?", ui_text 236, the K18 dialog of
    /// the title's EXIT) opens over whatever is on the screen, the cursor on No; Yes ends the run the way EXIT does (the system data is written, no slot). While that
    /// question is already up, from this or from the title's EXIT, a second close request quits at once. Returns true when the run ends now.
    /// </summary>
    public bool RequestClose()
    {
        if (CloseDialogOpen || (Ui.Top is ConfirmDialog { Waiting: true } top && top.MessageId(Ui.Context.Assets) == TitleScreen.QuitMessage))
        {
            CloseConfirmed = true;
            Services.RequestQuit();
            return true;
        }

        BeginOverlay();
        _closeDialog = Ui.Push(new ConfirmDialog(TitleScreen.QuitMessage, noFirst: true, yes =>
        {
            if (yes)
            {
                CloseConfirmed = true;
                Services.RequestQuit();
            }
        }));
        return false;
    }

    /// <summary>The last step's report.</summary>
    public StepReport Last { get; private set; }

    /// <summary>Where the last step spent its time (verification mode: the hitch log names the part that was slow).</summary>
    public StepTimes LastTimes { get; private set; }

    /// <summary>Opens the boot sequence: the autosave notice, then the title.</summary>
    public void Start()
    {
        Ui.Push(new BootScreen());
    }

    private void OnStoryEnded(EndReason reason)
    {
        _ending = true;
        Logged?.Invoke($"story ended: {reason}");
    }

    /// <summary>Runs one step: the story or the menus, and the display list of the frame.</summary>
    public StepReport Step(in InputFrame raw, long nominalTimestamp, DisplayList list)
    {
        long t0 = Stopwatch.GetTimestamp();
        Frame++;
        ReportTrophyIcons();
        RequestShownTrophyIcons();
        Services.Tick();
        IReadOnlyList<MovieClock> clocks = _movies?.Step(nominalTimestamp) ?? _noClocks;
        long t1 = Stopwatch.GetTimestamp();
        int n = Math.Min(clocks.Count, _clockBuffer.Length);
        for (int i = 0; i < n; i++)
        {
            _clockBuffer[i] = clocks[i];
        }

        Host.SetMovieClocks(_clockBuffer.AsSpan(0, n));
        Services.UpdateMovieClocks(clocks);

        InputFrame input = raw;
        bool uiWasActive = Ui.Active;
        bool story = Services.StoryActive && Host.Phase == GamePhase.Running;
        if (!story || !ReferenceEquals(Host, _thumbnailHost) || Host.RestoreCount != _thumbnailRestores)
        {
            // another story, a load or a rewind: the page seen before is not this story's last page
            _storyThumbnail = null;
            _brightThumbnail = null;
            _storyThumbnailFrame = -StoryThumbnailGap;
            _thumbnailHost = Host;
            _thumbnailRestores = Host.RestoreCount;
        }

        UpdateGesture(raw, story && !Ui.Active && !Host.ChoiceOpen && !Host.MovieActive);
        if (story && !Ui.Active)
        {
            input = RouteStoryInput(raw);
        }
        else if (!Ui.Active)
        {
            _heldThumbnail = null;
        }

        // the yes of a _SELA prompt: the save screen opens over the story (the dialog has faded out), and the script goes on once it is closed, saved or not
        if (_openSaveScreen && !Ui.Active)
        {
            _openSaveScreen = false;
            BeginOverlay();
            // a chapter start is a black screen: the save made here shows the last page the player read, as a save from the system menu does (package K16)
            (int Width, int Height, byte[] Rgba)? bright = Frame - _brightThumbnailFrame <= BrightThumbnailAge ? _brightThumbnail : null;
            _heldThumbnail = bright ?? _storyThumbnail ?? _heldThumbnail;
            _promptSave = Ui.Push(new SaveLoadScreen(save: true));
        }

        // the guide line's Confirm word, clicked while the story's yes / no prompt is up, is the decide button
        if (story && !Ui.Active && Host.PromptOpen && Ui.StoryPromptConfirmClicked(raw.Pointer))
        {
            input = input with { Pressed = input.Pressed | PadButtons.Decide, Pointer = input.Pointer with { Pressed = input.Pointer.Pressed & ~PointerButtons.Left } };
        }

        // the title asked for while a menu is still up (the system menu's Title button, the safety net below) waits until the menu has closed
        if (ApplyPendingHandOff())
        {
            story = false;
        }

        Ui.Step(uiWasActive ? raw : input with { Pressed = PadButtons.None, Repeated = PadButtons.None, MenuPressed = PadButtons.None, MenuRepeated = PadButtons.None, ShellPressed = ShellActions.None, Pointer = input.Pointer with { Pressed = PointerButtons.None, Wheel = 0 } });

        // a menu that left the story in its step (the system menu's Title tab, the end of a replay) has cleared the screens: the title or the gallery takes over in the
        // same frame, so nothing is ever drawn with no story and no screen (package K38)
        ApplyPendingHandOff();

        if (_promptSave is not null && !Ui.Stack.Contains(_promptSave))
        {
            _promptSave = null;
            Host.SavePromptScreenClosed();
        }

        long t2 = Stopwatch.GetTimestamp();

        // a story that started under the title (New Game, a load, a gallery replay) takes the screen: the title does not clear itself on the first-visit path, and
        // its looping background movie stops with it (the title screen's FadeIn starts it again when the story ends; package R1)
        if (Services.StoryActive && Ui.Mode == UiMode.Title && Host.Phase == GamePhase.Running)
        {
            Ui.Clear();
            Services.StopTitleMovie();
        }

        bool panelDrive = Ui.Top is ModePanelScreen && (Services.FastForwardHeld || Services.RewindHeld);
        bool rewound = false;
        bool stepped = false;
        list.Reset(Frame);
        story = Services.StoryActive && Host.Phase == GamePhase.Running;
        if (story && (!Ui.Active || panelDrive))
        {
            InputFrame gi = Ui.Active ? default : input;
            if (Services.FastForwardHeld || _gesture > 0)
            {
                gi = gi with { ShellHeld = gi.ShellHeld | ShellActions.FastForward };
            }

            rewound = Rewind(raw);
            HoverChoice(raw);
            bool auto = Host.AutoMode;
            bool skip = Host.SkipLatch;
            Host.Step(gi, list);
            stepped = true;
            if (Host.AutoMode && !auto)
            {
                Ui.ShowBanner(BannerKind.Auto);
            }

            if (Host.SkipLatch && !skip)
            {
                Ui.ShowBanner(BannerKind.Skip);
            }

            CopyFrozen(list);
            NoteStoryFrame();
        }
        else if (story && Ui.Active)
        {
            // the story stands still under the menu; the system menu and the backlog hide the message window and the text (UiRoot.BlurScene)
            ReadOnlySpan<DrawCmd> frozen = _frozen.Commands;
            int keep = Ui.BlurScene ? Math.Min(_frozenSceneCount, frozen.Length) : frozen.Length;
            for (int i = 0; i < keep; i++)
            {
                list.Add(frozen[i]);
            }
        }

        if (stepped || story)
        {
            Services.NoteStoryStep();
        }

        OfferAutoSkip(story);
        long t3 = Stopwatch.GetTimestamp();

        // the sound and movie commands the host produced go to the media layer in the same step (the voice plays went earlier, inside the step: AppServices.PerformHostAudio)
        Services.PerformHostAudio();
        MovieCommand[] movieCommands = Host.TakeMovieCommands();
        if (movieCommands.Length > 0)
        {
            _movies?.Perform(movieCommands);
        }

        _audio?.Pump();
        long t4 = Stopwatch.GetTimestamp();
        Services.CheckStoryEnd();
        if (Services.StoryActive && Host.Phase == GamePhase.NotStarted)
        {
            // a story whose host has nothing to run (a host rebuilt for a language change that could not restart its scene): the title takes over once the menus
            // have closed, so the screen can never stay dead (package R1)
            Logged?.Invoke("story host not started: back to the title");
            Services.ReturnToTitle();
        }

        if (_ending)
        {
            _ending = false;
            EndStory();
        }

        ApplyPendingHandOff();
        if (!Services.StoryActive && !Ui.Active)
        {
            // nothing is on the screen: no story runs and no screen is open. No path should end a step here (a story ends into the title or the gallery, a menu that
            // leaves the story hands over first); one that does would leave the player on a black screen that takes no key, so the title takes over and the log says
            // so (package K38: issue #7 was such a screen, from the end of a replay under the system menu)
            Logged?.Invoke("nothing on screen (no story, no menu): back to the title");
            Services.StopStoryOutputs();
            Ui.Push(new TitleScreen());
        }

        ShowFoundTrophiesOnTheTitle();
        long t4b = Stopwatch.GetTimestamp();
        Ui.Draw(list);
        _noticeInFrame = Ui is { HideToast: false, TrophyNotice.Alpha: > 0f };
        list.Seal();
        long t5 = Stopwatch.GetTimestamp();
        LastTimes = new StepTimes(Ms(t0, t1), Ms(t1, t2), Ms(t2, t3), Ms(t3, t4), Ms(t4, t4b), Ms(t4b, t5));
        Last = new StepReport(Frame, stepped, Ui.Mode, rewound);
        return Last;
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The mouse over a choice line moves the cursor to it (the click that decides it is the interpreter's own rule).</summary>
    private void HoverChoice(in InputFrame raw)
    {
        if (!Host.ChoiceSelectable || !raw.Pointer.Active)
        {
            return;
        }

        if (raw.Pointer.X == _lastPointerX && raw.Pointer.Y == _lastPointerY && raw.Pointer.Pressed == PointerButtons.None)
        {
            return;
        }

        _lastPointerX = raw.Pointer.X;
        _lastPointerY = raw.Pointer.Y;
        int line = Host.ChoiceLineAt(CanvasY(raw.Pointer));
        if (line >= 0)
        {
            Host.HoverChoice(line);
        }
    }

    private static double CanvasY(in PointerState p) => float.IsNaN(p.CanvasY) ? (p.Y + 0.5) * UiMetrics.UnitY : p.CanvasY;

    // a drag that starts in the middle of the screen (knife touch.rs: x 121..519, y 125..383 of 640 x 448) and runs 45 units or more to the right fast-forwards while the
    // button stays down, to the left it rewinds (the PS4 Controls page: "Click the center of the screen and drag right to fast-forward / left to rewind")
    private void UpdateGesture(in InputFrame raw, bool allowed)
    {
        PointerState p = raw.Pointer;
        if (!allowed || !p.Active || (p.Held & PointerButtons.Left) == 0)
        {
            _gestureStart = null;
            _gesture = 0;
            return;
        }

        if (_gestureStart is null && (p.Pressed & PointerButtons.Left) != 0 && p.X is >= 121 and <= 519 && p.Y is >= 125 and <= 383)
        {
            _gestureStart = (p.X, p.Y);
        }

        if (_gestureStart is { } s)
        {
            int dx = p.X - s.X;
            _gesture = dx >= GestureUnits ? 1 : dx <= -GestureUnits ? -1 : 0;
        }
    }

    private const int TopEdgeUnits = 12;
    private const int BottomEdgeUnits = 436;
    private const int GestureUnits = 45;

    private short _lastPointerX = -1;
    private short _lastPointerY = -1;

    private void CopyFrozen(DisplayList list)
    {
        _frozen.Reset(Frame);
        _frozenSceneCount = 0;
        int index = 0;
        foreach (DrawCmd c in list.Commands)
        {
            if (c.Kind != DrawKind.Capture)
            {
                _frozen.Add(c);
                if (index < Host.SceneCommandCount)
                {
                    _frozenSceneCount++;
                }
            }

            index++;
        }
    }

    private int _frozenSceneCount;

    private void EndStory()
    {
        Services.StopStoryOutputs();
        if (Host.ReplayRequested && Ui.ReturnToGallery())
        {
            return;
        }

        if (!Ui.Active)
        {
            Ui.Push(new TitleScreen());
        }
    }

    /// <summary>
    /// The hand-offs a menu asked for while it was still on the stack, performed once no screen is open: the title (the system menu's Title tab, the safety nets) or
    /// the gallery (a replay ended from the system menu, which reopens the gallery on the chart the replay was picked from; the title when the data has no gallery).
    /// True when a screen was pushed.
    /// </summary>
    private bool ApplyPendingHandOff()
    {
        if (Ui.Active)
        {
            return false;
        }

        if (Services.TakePendingTitle())
        {
            Services.StopStoryOutputs();
            Ui.Push(new TitleScreen());
            return true;
        }

        if (Services.TakePendingGalleryReturn())
        {
            Services.StopStoryOutputs();
            if (!Ui.ReturnToGallery())
            {
                Ui.Push(new TitleScreen());
            }

            return true;
        }

        return false;
    }

    // The last frame of a page that was being read (a message fully shown in an open window), grabbed at most once in StoryThumbnailGap steps: the thumbnail of
    // a save made from a story prompt, whose own frame is the black start of a chapter (package K16; K13 open issue 4)
    private const int StoryThumbnailGap = 180;

    private void NoteStoryFrame()
    {
        if (Grab is null || _noticeInFrame || Frame - _storyThumbnailFrame < StoryThumbnailGap || Host.ChoiceOpen || Host.MovieActive || Host.SkipLatch || Services.FastForwardHeld
            || Host.PageMessages.Count == 0 || Host.Wait.WindowAlpha < 0.99f || Host.TextFade < 1f || Host.F(1103) != 0)
        {
            return;
        }

        _storyThumbnailFrame = Frame;
        _storyThumbnail = Grab();
        if (_storyThumbnail is { } g && MeanLuma(g.Rgba) >= BrightLuma)
        {
            // a chapter ends on a dark page: the last bright page, if it is recent, says more of where the save is than the dark one
            _brightThumbnail = g;
            _brightThumbnailFrame = Frame;
        }
    }

    private const int BrightThumbnailAge = 6000;
    private const double BrightLuma = 14.0;

    private static double MeanLuma(byte[] rgba)
    {
        long sum = 0;
        for (int i = 0; i + 2 < rgba.Length; i += 4)
        {
            sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
        }

        return rgba.Length < 4 ? 0 : sum / 3.0 / (rgba.Length / 4);
    }

    private void BeginOverlay()
    {
        _heldThumbnail = CleanFrame();
    }

    private bool Rewind(in InputFrame raw)
    {
        bool held = (raw.Held & PadButtons.L2) != 0 || (raw.ShellHeld & ShellActions.Rewind) != 0 || Services.RewindHeld || _gesture < 0;
        if (!held)
        {
            _rewindHold = 0;
            return false;
        }

        _rewindHold++;
        if (_rewindHold != 1 && _rewindHold % RewindPeriod != 0)
        {
            return false;
        }

        bool ok = Host.RewindStep();
        if (ok)
        {
            Host.SetSkipLatch(false);
            Logged?.Invoke($"rewind: script {Host.Script} offset {Host.Machine.Cursor}");
        }

        return ok;
    }

    /// <summary>
    /// The shell actions of a story with no menu open: the system menu, the backlog, the mode panel, quick save and load, the scene jumps and the voice replay.
    /// Returns the input the host gets (the keys that opened a menu are taken out of it).
    /// </summary>
    private InputFrame RouteStoryInput(in InputFrame raw)
    {
        InputFrame input = raw;
        ShellActions sp = raw.ShellPressed;
        PadButtons pp = raw.Pressed;
        bool right = (raw.Pointer.Pressed & PointerButtons.Right) != 0;
        bool movie = Host.MovieActive;
        bool choice = Host.ChoiceOpen;
        PointerState ptr = raw.Pointer;

        // the mouse rows of the PS4 Controls page (package K12): the pointer at the top of the screen opens the control panel (the mode panel), at the bottom it
        // hides the message window while it stays there, a drag from the middle of the screen to the right fast-forwards and to the left rewinds (UpdateGesture); the
        // wheel button toggles skip (InputMapper), the wheel up opens the backlog, a left click advances and a right click opens the system menu
        bool inTop = ptr.Active && ptr.Y < TopEdgeUnits;
        bool inBottom = ptr.Active && ptr.Y > BottomEdgeUnits;
        if (!ptr.Active || ptr.Y >= TopEdgeUnits + 10)
        {
            _topArmed = true;
        }

        ShellActions mouseShell = ShellActions.None;
        if (!choice && !movie)
        {
            if (inBottom && !_hiddenByPointer && !Host.WindowHidden)
            {
                mouseShell |= ShellActions.HideWindow;
                _hiddenByPointer = true;
            }
            else if (!inBottom && _hiddenByPointer)
            {
                _hiddenByPointer = false;
                if (Host.WindowHidden)
                {
                    mouseShell |= ShellActions.HideWindow;
                }
            }
        }
        else if (!inBottom)
        {
            _hiddenByPointer = false;
        }

        // the click that would advance the text does not when it lands where the window is hidden, nor on empty space while a choice list waits for a pick
        if ((ptr.Pressed & PointerButtons.Left) != 0
            && ((inBottom && Host.WindowHidden && !choice) || (Host.ChoiceSelectable && Host.ChoiceKind == ChoiceKind.List && Host.ChoiceLineAt(CanvasY(ptr)) < 0)))
        {
            input = input with { Pointer = ptr with { Pressed = ptr.Pressed & ~PointerButtons.Left } };
        }

        bool menuKey = (sp & ShellActions.SystemMenu) != 0 || (pp & PadButtons.Square) != 0 || right;
        if (menuKey && !movie)
        {
            if (!Ui.Active)
            {
                BeginOverlay();
                Ui.OpenSystemMenu();
            }

            return Strip(input);
        }

        if (!choice && !movie && ((pp & PadButtons.Triangle) != 0 || (sp & ShellActions.Backlog) != 0) && Services.Backlog.Count > 0)
        {
            BeginOverlay();
            if (Ui.OpenBacklog())
            {
                return Strip(input);
            }
        }

        if (!choice && !movie && ((pp & PadButtons.Up) != 0 || (inTop && _topArmed)) && Services.PanelAllowed)
        {
            BeginOverlay();
            if (Ui.OpenModePanel())
            {
                _topArmed = false;
                return Strip(input);
            }
        }

        if ((sp & ShellActions.QuickSave) != 0 && !movie)
        {
            if (Services.QuickSave())
            {
                Ui.ShowBanner(BannerKind.QuickSave);
            }
        }

        if ((sp & ShellActions.QuickLoad) != 0 && !movie && Services.HasQuickSave)
        {
            BeginOverlay();
            Ui.Push(new ConfirmDialog(ConfirmKind.QuickLoad, yes =>
            {
                if (yes && Services.QuickLoad())
                {
                    Ui.Clear();
                }
            }, thumbnailSlot: 139));
            return Strip(input);
        }

        if ((sp & ShellActions.NextScene) != 0 && Services.PanelAllowed && Services.SceneJumpEnabled)
        {
            BeginOverlay();
            Ui.Push(new ConfirmDialog(ConfirmKind.NextScene, yes =>
            {
                if (yes)
                {
                    Services.NextScene();
                }
            }));
            return Strip(input);
        }

        if ((sp & ShellActions.PreviousScene) != 0 && Services.PanelAllowed && Services.SceneJumpEnabled && Host.CanGoToPreviousScene)
        {
            BeginOverlay();
            Ui.Push(new ConfirmDialog(ConfirmKind.PrevScene, yes =>
            {
                if (yes)
                {
                    Services.PreviousScene();
                }
            }));
            return Strip(input);
        }

        if ((sp & ShellActions.ReplayVoice) != 0)
        {
            Services.ReplayVoice();
        }

        // the keys the menus use must not also act on the story
        PadButtons mask = ~(PadButtons.Square | PadButtons.Triangle | PadButtons.L2);
        return input with { Held = input.Held & mask, Pressed = input.Pressed & mask, Repeated = input.Repeated & mask, ShellPressed = input.ShellPressed | mouseShell };
    }

    private static InputFrame Strip(in InputFrame input)
    {
        PadButtons mask = ~(PadButtons.Cancel | PadButtons.Square | PadButtons.Triangle | PadButtons.L2 | PadButtons.Decide | PadButtons.Up);
        PointerState p = input.Pointer;
        return input with
        {
            Held = input.Held & mask,
            Pressed = input.Pressed & mask,
            Repeated = input.Repeated & mask,
            Pointer = p with { Held = p.Held & ~(PointerButtons.Right | PointerButtons.Left), Pressed = p.Pressed & ~(PointerButtons.Right | PointerButtons.Left), Wheel = 0 },
            ShellPressed = ShellActions.None,
        };
    }
}
