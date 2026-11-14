// Verification mode (package K17): draws the shell to PNG files without ever showing a window. The shell is attached to a message-only HwndSource (an invisible
// window handle, so that controls are loaded and text boxes draw their text), measured and arranged at a fixed size and drawn with RenderTargetBitmap.
// Nothing appears on the screen; animations are off because no clock is needed.
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Tsuki.Engine.Trophies;
using Tsuki.Launcher.Core;
using Tsuki.Launcher.Views;

namespace Tsuki.Launcher;

internal static class OffscreenRender
{
    public const int Width = 1200;

    /// <summary>The window's smallest width (MainWindow.xaml MinWidth), for the check that the footer still fits there (package K42).</summary>
    private const int MainWindowMinWidth = 960;

    /// <summary>The window's smallest height (MainWindow.xaml MinHeight), for the picture of the Achievements page in the smallest window (v0.1.9).</summary>
    private const int MainWindowMinHeight = 600;

    /// <summary>The height of the picture (780 is the window; a taller value shows a whole page, for the check of the lower cards).</summary>
    public static int Height { get; set; } = WindowHeight;

    /// <summary>The window's own height (MainWindow.xaml Height).</summary>
    private const int WindowHeight = 780;

    /// <summary>
    /// The display scale the pictures are drawn at (<c>--scale</c>; 1.5 is Windows' 150 %): the shell is laid out for that scale, as a window on such a screen is
    /// (layout rounding and text snap to its pixels), and the pictures have that many pixels per unit. For the check of how scaled-down bitmaps look there.
    /// </summary>
    public static double Scale { get; set; } = 1.0;

    /// <summary>HWND_MESSAGE: a window parented to it is never visible.</summary>
    private static readonly IntPtr MessageOnly = new(-3);

    public static async Task RunAsync(LauncherModel model, string outDirectory, string? onlyPage)
    {
        Directory.CreateDirectory(outDirectory);
        await model.RefreshAsync();
        var shell = new Shell { Animate = false, DataContext = model, Width = Width, Height = Height };
        using var source = new HwndSource(new HwndSourceParameters("TsukihimeLauncherRender", Width, Height) { ParentWindow = MessageOnly, WindowStyle = 0 })
        {
            RootVisual = shell,
        };
        if (Scale != 1.0)
        {
            VisualTreeHelper.SetRootDpi(shell, new DpiScale(Scale, Scale));
        }

        if (model.HeroArt is BitmapSource art)
        {
            LauncherArt.SavePng(art, Path.Combine(outDirectory, "art_source.png"));
        }

        foreach (NavItem item in model.NavItems)
        {
            string name = item.Page.ToString().ToLowerInvariant();
            if (onlyPage is not null && !name.Equals(onlyPage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            model.Page = item.Page;
            if (item.Page == LauncherPage.Achievements)
            {
                // the pictures of its rows are made on a worker thread once the page is shown: the picture is taken when they are all there, not of the blank tiles
                await model.AchievementPicturesSettledAsync();
            }

            await DrawAsync(shell, Path.Combine(outDirectory, name + ".png"));
            if (item.Page == LauncherPage.Achievements)
            {
                // what the squares of the rows held when that picture was taken, a line per row ("5 earned Icon"): a Blank here is a picture taken too early
                File.WriteAllLines(
                    Path.Combine(outDirectory, "achievements_rows.txt"),
                    model.AchievementRows.Select(r => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.Id} {(r.Earned ? "earned" : "locked")} {r.Art}")));
                await DrawAchievementsAsync(model, shell, outDirectory, AchievementsView.Of(shell)?.Account(model) ?? "no page");
            }
        }

        if (onlyPage is null)
        {
            // the real window, never shown: its handle is created so that the frame, the chrome and the icon are known to load
            var window = new MainWindow(model);
            _ = new WindowInteropHelper(window).EnsureHandle();
            File.WriteAllText(Path.Combine(outDirectory, "window_ok.txt"), $"window {window.Title}, min {window.MinWidth} x {window.MinHeight}, icon {window.Icon is not null}, chrome {WindowChrome.GetWindowChrome(window) is not null}, game icon {window.GameIconApplied ?? "none (the crescent)"}");
            window.Close();

            // the confirm overlay, as the saves page shows it
            model.Page = LauncherPage.Saves;
            model.AskImport("savedata000");
            await DrawAsync(shell, Path.Combine(outDirectory, "confirm.png"));
            model.ConfirmNoCommand.Execute(null);
            model.Page = LauncherPage.Play;
            model.ShowToast("The game could not be started: example message for the render check.");
            await DrawAsync(shell, Path.Combine(outDirectory, "toast.png"));
            model.ShowToast(string.Empty);

            await DrawUpdateAsync(model, shell, outDirectory);
        }
    }

    /// <summary>
    /// v0.1.9: the Achievements page beyond its first picture (achievements.png: the All tab as the page opens, its top: the summary and under it the notice card
    /// with both switches, D8 P-2). The Earned and the Locked tab, each with the page scrolled down to the card of the list, so that the picture shows the tab bar
    /// and the first rows of that tab (or the sentence of an empty tab); then the same two in the smallest window the launcher allows (960 x 600), and the All
    /// tab there as the page opens (achievements_min.png: the sidebar's eight entries, the summary and the switches). With <c>--height</c> the pictures are as
    /// tall as asked and show the page from its top (a page that is all in view does not scroll), so the whole list is seen at the smallest width too.
    /// achievements_pictures.txt says for each picture how far the page was scrolled and which of its parts are wholly in view. The page shows what the profile
    /// of this run holds.
    /// </summary>
    /// <param name="model">The view model, on the Achievements page.</param>
    /// <param name="shell">The shell, laid out as the first picture was taken.</param>
    /// <param name="outDirectory">The folder of the pictures.</param>
    /// <param name="first">What of the page was in view in its first picture (<see cref="AchievementsView.Account"/>).</param>
    private static async Task DrawAchievementsAsync(LauncherModel model, Shell shell, string outDirectory, string first)
    {
        List<string> account = [string.Create(System.Globalization.CultureInfo.InvariantCulture, $"achievements.png {Width} x {Height}, scrolled 0: {first}")];
        void Tell(string picture, int width, int height)
        {
            AchievementsView? view = AchievementsView.Of(shell);
            account.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{picture} {width} x {height}, scrolled {(view?.Scroll.VerticalOffset ?? 0):0}: {view?.Account(model) ?? "no page"}"));
        }

        AchievementTabView all = model.SelectedAchievementTab;
        foreach (AchievementTabView tab in model.AchievementTabs)
        {
            if (!ReferenceEquals(tab, all))
            {
                model.SelectedAchievementTab = tab;
                string picture = "achievements_" + tab.Filter.ToString().ToLowerInvariant() + ".png";
                await ScrollAchievementsAsync(shell, Width, Height, toTheList: true);
                await DrawAsync(shell, Path.Combine(outDirectory, picture));
                Tell(picture, Width, Height);
            }
        }

        int minHeight = Height == WindowHeight ? MainWindowMinHeight : Height;
        shell.Width = MainWindowMinWidth;
        shell.Height = minHeight;
        foreach (AchievementTabView tab in model.AchievementTabs)
        {
            if (!ReferenceEquals(tab, all))
            {
                model.SelectedAchievementTab = tab;
                string picture = "achievements_" + tab.Filter.ToString().ToLowerInvariant() + "_min.png";
                await ScrollAchievementsAsync(shell, MainWindowMinWidth, minHeight, toTheList: true);
                await DrawAsync(shell, Path.Combine(outDirectory, picture), MainWindowMinWidth, minHeight);
                Tell(picture, MainWindowMinWidth, minHeight);
            }
        }

        model.SelectedAchievementTab = all;
        await ScrollAchievementsAsync(shell, MainWindowMinWidth, minHeight, toTheList: false);
        await DrawAsync(shell, Path.Combine(outDirectory, "achievements_min.png"), MainWindowMinWidth, minHeight);
        Tell("achievements_min.png", MainWindowMinWidth, minHeight);
        shell.Width = Width;
        shell.Height = Height;
        await ScrollAchievementsAsync(shell, Width, Height, toTheList: false);
        File.WriteAllLines(Path.Combine(outDirectory, "achievements_pictures.txt"), account);
    }

    /// <summary>
    /// Scrolls the Achievements page of the shell, laid out at that size: to the card of the list (the tab bar at the top of the view, the page's gap above it, the
    /// rows under it), or back to the page's top. A page that is all in view (a tall picture) does not move.
    /// </summary>
    private static async Task ScrollAchievementsAsync(Shell shell, int width, int height, bool toTheList)
    {
        await LayoutAsync(shell, width, height);
        if (AchievementsView.Of(shell) is not { } view)
        {
            return;
        }

        FrameworkElement list = view.Page.ListCard;
        double offset = toTheList ? list.TransformToAncestor(view.Scroll).Transform(default).Y + view.Scroll.VerticalOffset - list.Margin.Top : 0;
        view.Scroll.ScrollToVerticalOffset(Math.Max(0, offset));
    }

    /// <summary>
    /// The Achievements page as the shell is laid out now, seen from its view (the page's scroll viewer: 0 is the view's top, and the view ends at the footer's
    /// top): where each of its parts is, and which are wholly in view. A part the page's state does not show (a collapsed note) has no place. For the check that
    /// the page fits the window as it opens (D8 P-2) and for the render's account of its pictures.
    /// </summary>
    private sealed class AchievementsView
    {
        private AchievementsView(AchievementsPage page) => Page = page;

        /// <summary>The page.</summary>
        public AchievementsPage Page { get; }

        /// <summary>The page's scroll viewer, whose view the page is seen through.</summary>
        public ScrollViewer Scroll => Page.Scroll;

        /// <summary>The view's height: from under the window's top strip to the footer.</summary>
        public double Height => Page.Scroll.ViewportHeight;

        /// <summary>The Achievements page of the shell, or null when the shell shows another page.</summary>
        public static AchievementsView? Of(Shell shell) => Find<AchievementsPage>(shell, _ => true) is { } page ? new AchievementsView(page) : null;

        /// <summary>Where the element is in the view, its top and its bottom, or null when it is not shown (it or a parent collapsed).</summary>
        public (double Top, double Bottom)? Span(FrameworkElement element)
        {
            for (DependencyObject? at = element; at is not null && !ReferenceEquals(at, Scroll); at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: not Visibility.Visible })
                {
                    return null;
                }
            }

            if (element.ActualHeight <= 0 || !Scroll.IsAncestorOf(element))
            {
                return null;
            }

            Rect bounds = element.TransformToAncestor(Scroll).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return (bounds.Top, bounds.Bottom);
        }

        /// <summary>Whether the whole element is in view: shown, neither above the view's top nor under the footer.</summary>
        public bool Shows(FrameworkElement element) => Span(element) is { } span && span.Top >= -0.5 && span.Bottom <= Height + 0.5;

        /// <summary>How far the bottom of the element is above the footer's top, in words ("ends 12 px above the footer", "is cut by the footer by 30 px").</summary>
        public string Ends(FrameworkElement element) => Span(element) is not { } span
            ? "is not shown"
            : Height - span.Bottom >= -0.5
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ends {Math.Max(0, Height - span.Bottom):0} px above the footer")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"is cut by the footer by {span.Bottom - Height:0} px");

        /// <summary>The parts of the page wholly in view, in the page's order, and the ids of the rows wholly in view ("summary, Notify switch, ..., rows 1 2 5").</summary>
        public string Account(LauncherModel model)
        {
            List<string> parts = [];
            foreach ((string name, FrameworkElement part) in new (string, FrameworkElement)[]
                     {
                         ("summary", Page.SummaryCard), ("Notify switch", Page.NoticeRow), ("sound switch", Page.SoundRow), ("record note", Page.FileNote),
                         ("damaged note", Page.DamagedNote), ("no-game note", Page.NoGameNote), ("tabs", Page.Tabs), ("nothing to list", Page.EmptyText),
                     })
            {
                if (Shows(part))
                {
                    parts.Add(name);
                }
            }

            int[] rows = [.. Enumerable.Range(0, model.AchievementRows.Count)
                .Where(i => Page.Rows.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row && Shows(row))
                .Select(i => model.AchievementRows[i].Id)];
            parts.Add(rows.Length == 0 ? "no row" : "rows " + string.Join(" ", rows));
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Package K40: the update button and its overlay, drawn from a made-up release (no network): the footer with "Update to vX.Y.Z", the overlay with the notes,
    /// the download in progress, the two refusals (the game is running, a folder that cannot be written), a failure, the Help page's Updates card in a normal and in
    /// a personal build. Package K42: before that the always-shown button as "Check for updates", "Checking…" and the toasts of a manual check (newest, offline),
    /// the gold button at the window's smallest width (the status line trims, PLAY keeps its size), and a personal build's button with its toast.
    /// </summary>
    private static async Task DrawUpdateAsync(LauncherModel model, Shell shell, string outDirectory)
    {
        model.Page = LauncherPage.Play;
        model.PreviewUpdate(null);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer_idle.png"));
        model.PreviewChecking(true);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer_checking.png"));
        model.PreviewChecking(false);
        model.PreviewManualCheck(new UpdateCheckResult(null, [], null));
        await DrawAsync(shell, Path.Combine(outDirectory, "update_toast_newest.png"));
        model.PreviewManualCheck(new UpdateCheckResult(null, [], "no connection (No such host is known. (api.github.com:443))"));
        await DrawAsync(shell, Path.Combine(outDirectory, "update_toast_offline.png"));
        model.ShowToast(string.Empty);

        ReleaseVersion next = new(model.CurrentVersion.Major, model.CurrentVersion.Minor, model.CurrentVersion.Patch + 1);
        string zip = UpdateSource.ZipAssetName(next);
        var sample = new ReleaseInfo(
            next,
            next.Tag,
            $"Tsukihime PC port {next.Tag}",
            UpdateFeed.PlainText(
                "## Fixed\n\n- **Sample notes for the render check.** A release's notes appear here as plain text, scrollable when they are long.\n" +
                "- Headings, **bold** and `code` marks of GitHub's Markdown are taken out; [links](https://example.invalid) keep their words.\n\n" +
                "## How to update\n\nPress **Update now**: the launcher downloads the zip and SHA256SUMS.txt, checks the checksum, replaces its files and restarts.\n\n" +
                "> Fan-made and unofficial. This contains no game data; you must own the game."),
            "https://github.com/" + UpdateSource.DefaultRepository + "/releases/tag/" + next.Tag,
            true,
            new ReleaseAsset(zip, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + zip), 110_268_124),
            new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + UpdateSource.SumsAssetName), 98));
        model.PreviewUpdate(sample);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer.png"));
        shell.Width = MainWindowMinWidth;
        await DrawAsync(shell, Path.Combine(outDirectory, "update_footer_narrow.png"), MainWindowMinWidth);
        shell.Width = Width;
        model.PreviewUpdateState(UpdateStage.Offer, null, string.Empty);   // the overlay a manual check opens (a press of the gold button would start an update)
        await DrawAsync(shell, Path.Combine(outDirectory, "update_offer.png"));
        model.PreviewUpdateState(UpdateStage.Working, new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(46_137_344, 110_268_124)), string.Empty);
        await DrawAsync(shell, Path.Combine(outDirectory, "update_progress.png"));
        model.PreviewUpdateState(UpdateStage.Offer, null, "Close the game first: Tsukihime.exe of this folder is running, and its files are the ones the update replaces. Quit the game (your progress is in your saves), then press Update now again.");
        await DrawAsync(shell, Path.Combine(outDirectory, "update_game_running.png"));
        model.PreviewUpdateState(UpdateStage.Failed, null, UpdateInstaller.NotWritableMessage(@"C:\Program Files\Tsukihime", next));
        await DrawAsync(shell, Path.Combine(outDirectory, "update_not_writable.png"));
        model.PreviewUpdateState(UpdateStage.Failed, null, $"The update did not work: the downloaded {zip} does not match its checksum in {UpdateSource.SumsAssetName} (SHA-256 3f7a09c2d1e4...), so it was not installed. Nothing was changed; the launcher you have keeps working.");
        await DrawAsync(shell, Path.Combine(outDirectory, "update_failed.png"));
        model.PreviewUpdateState(UpdateStage.Hidden, null, string.Empty);
        model.Page = LauncherPage.Help;
        await DrawAsync(shell, Path.Combine(outDirectory, "help_update.png"));
        model.PreviewPersonal(true);
        await DrawAsync(shell, Path.Combine(outDirectory, "help_personal.png"));
        model.Page = LauncherPage.Play;
        model.ShowToast(UpdateButtonState.PersonalToast);          // what the button says in a personal build (PressUpdateButtonAsync, without its log line)
        await DrawAsync(shell, Path.Combine(outDirectory, "update_toast_personal.png"));
        model.ShowToast(string.Empty);
        model.PreviewPersonal(false);
        model.PreviewUpdate(null);
    }

    /// <summary>
    /// Package K40, verification mode: checks the loopback stand-in of GitHub the model was given, opens the overlay and installs the update over the launcher's own
    /// folder (the folder of this process), as "Update now" does. The caller then starts the new launcher. Returns the report (a FAIL line per check that did not hold).
    /// Package K42: the check is a press of the footer button, whose states before, during and after it (and its toast) are reported too; a run without a newer
    /// release (up to date, offline, a personal build) ends after those lines with the FAIL of "the check found an update".
    /// </summary>
    public static async Task<string> UpdateSelfTestAsync(LauncherModel model)
    {
        var report = new System.Text.StringBuilder();
        void Check(string what, bool ok) => report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
        string app = AppContext.BaseDirectory;
        report.AppendLine($"ok    launcher {model.Build.InformationalVersion} runs from {app}; the check compares with {model.CurrentVersion.Tag}; source {model.UpdateSource.ReleasesUri}");
        Check("the source is the loopback address", model.UpdateSource.Policy.IsLoopback);

        // package K42: the check is the footer button's, pressed the way a click does; the button is there before it, reads "Checking…" while it runs, and says the outcome
        Check($"before the check the footer button reads \"{model.UpdateButtonLabel}\" in the quiet look and can be pressed", model.UpdateButtonLabel == UpdateButtonState.CheckLabel && !model.UpdateButtonAccent && model.UpdateButtonEnabled);
        Task press = model.PressUpdateButtonAsync();
        if (!model.UpdatesOff)
        {
            Check($"while it checks the footer button reads \"{model.UpdateButtonLabel}\" and is disabled", press.IsCompleted || (model.UpdateButtonLabel == UpdateButtonState.CheckingLabel && !model.UpdateButtonEnabled));
        }

        await press;
        ReleaseInfo? found = model.AvailableUpdate;
        string expectedToast = model.UpdatesOff ? UpdateButtonState.PersonalToast : model.LastCheckOutcome switch
        {
            UpdateCheckOutcome.Failed => UpdateButtonState.FailedToast,
            UpdateCheckOutcome.Newest => $"You have the newest version ({model.CurrentVersion.Tag}).",
            _ => string.Empty,
        };
        Check($"after the press the footer button reads \"{model.UpdateButtonLabel}\" ({(model.UpdateButtonAccent ? "gold" : "quiet")}, {(model.UpdateButtonEnabled ? "enabled" : "disabled")}); outcome {model.LastCheckOutcome?.ToString() ?? "no check"}; toast \"{model.Toast}\"",
            model.Toast == expectedToast && model.UpdateButtonAccent == (found is not null) && (model.LastCheckOutcome is not null) != model.UpdatesOff);
        Check($"the check found an update ({found?.Tag ?? "none"}; {model.UpdateStatus})", found is not null);
        if (found is null)
        {
            return report.ToString();
        }

        Check($"the footer offers \"{model.UpdateButtonLabel}\" in the gold look", model.UpdateButtonAccent && model.UpdateButtonLabel == $"Update to {found.Tag}");
        Check("the manual check opened the overlay at once", model.UpdateStage == UpdateStage.Offer);
        Check($"the overlay shows the release's title and notes ({found.Title}; {found.Notes.Length} characters)", model.UpdateOverlayVisible && model.UpdateStage == UpdateStage.Offer && model.UpdateNotes.Length > 0 && model.UpdateReleaseTitle == found.Title);
        // one click: Later closes the overlay, the gold button stays, and one press of it installs the update
        model.UpdateLaterCommand.Execute(null);
        Check("Later closed the overlay and the gold button still offers the update; one press of it installs", model.UpdateStage == UpdateStage.Hidden && model.UpdateButtonAccent && model.UpdateButtonEnabled);
        await model.PressUpdateButtonAsync();
        Check($"the update was installed and the restart was asked for (stage {model.UpdateStage}{(model.UpdateMessage.Length > 0 ? "; " + model.UpdateMessage : string.Empty)})", model.UpdateStage == UpdateStage.Restarting);
        string exe = Path.Combine(app, GameLauncher.LauncherFileName);
        Check("the running TsukihimeLauncher.exe was renamed to .old and the new one moved in", File.Exists(exe + UpdateInstaller.OldSuffix) && File.Exists(exe));
        string journal = new UpdateStaging(app).Journal;
        Check("the journal of the update ends with done", File.Exists(journal) && File.ReadAllLines(journal).Contains("done"));
        Tsuki.Data.Icons.AssemblyMarks? marks = Tsuki.Data.Icons.PeIcons.Marks(Path.Combine(app, "TsukihimeLauncher.dll"));
        Check($"the launcher now in the folder is {found.Tag} ({marks?.InformationalVersion})", ReleaseVersion.TryParseInformational(marks?.InformationalVersion, out ReleaseVersion installed) && installed == found.Version);
        return report.ToString();
    }

    /// <summary>
    /// Drives the view model the way the pages do (every setter, the folder choice) over a temporary profile and checks what lands in settings.ini and game_dir.txt. Returns the
    /// report; it contains a line that starts with FAIL for every check that did not hold.
    /// </summary>
    /// <param name="model">The view model.</param>
    /// <param name="paths">The temporary profile.</param>
    /// <param name="gameFolder">The game folder of <c>--game</c>, or null.</param>
    /// <param name="afterUpdate">Package K40: the version an update came from and the .old files its cleanup left, when an update restarted into this launcher.</param>
    public static async Task<string> SelfTestAsync(LauncherModel model, LauncherPaths paths, string? gameFolder, (ReleaseVersion From, int Left)? afterUpdate = null)
    {
        var report = new System.Text.StringBuilder();
        void Check(string what, bool ok) => report.AppendLine((ok ? "ok    " : "FAIL  ") + what);
        string app = AppContext.BaseDirectory;
        report.AppendLine($"ok    launcher {model.Build.InformationalVersion} runs from {app} (personal build: {model.Build.IsPersonal})");
        if (afterUpdate is { } after)
        {
            string[] old = Directory.GetFiles(app, "*" + UpdateInstaller.OldSuffix, SearchOption.AllDirectories);
            Check($"after the update from {after.From.Tag}: {after.Left} file(s) left by the cleanup, {old.Length} .old file(s) in the folder, staging folder {(Directory.Exists(new UpdateStaging(app).Root) ? "still there" : "gone")}",
                after.Left == 0 && old.Length == 0 && !Directory.Exists(new UpdateStaging(app).Root));
            Check($"this launcher ({model.Build.Version.Tag}) is newer than the one it replaced ({after.From.Tag})", model.Build.Version > after.From);
        }

        await model.RefreshAsync();

        // v0.1.9 FIXES D11 W-1: a display narrower than 1423 px or with a work area lower than 800 px (a hosted runner's) has room for no window size preset. The Display
        // page then offers none and the game fits its own window, so that is no fault: the checks of the presets say so instead of failing
        bool presets = model.WindowSizes.Count > 0;
        Check(presets ? "fresh profile: English, windowed, vsync on, 1600 x 900, auto prompts" : "fresh profile: English, windowed, vsync on, auto prompts",
            model.LanguageIndex == 1 && !model.Fullscreen && model.VSync && (!presets || model.SelectedWindowSize is { Width: 1600, Height: 900 }) && model.PromptsIndex == 0);
        Check(presets ? "the window sizes offered fit the chosen monitor" : "no preset fits this display: the Display page offers no window size",
            model.WindowSizes.All(c => model.SelectedMonitor is not { } sm || Tsuki.Platform.WindowFit.Fits(c.Width, c.Height, sm.WorkArea.Width, sm.WorkArea.Height)));
        await AchievementsSelfTestAsync(model, paths, Check);
        await AchievementsPageFitSelfTestAsync(model, paths, Check);
        await SettingsPagesFollowTheGameSelfTestAsync(model, paths, Check);

        model.LanguageIndex = 0;
        model.Fullscreen = true;
        model.VSync = false;
        // null where no preset fits (the setter then leaves the size alone); indexing the empty list threw here, and the self-test never ended
        WindowSizeChoice? big = model.WindowSizes.FirstOrDefault(c => c.Width == 1920) ?? model.WindowSizes.LastOrDefault();
        model.SelectedWindowSize = big;
        model.PromptsIndex = 3;
        MonitorEntry? monitor = model.Monitors.LastOrDefault();
        if (monitor is not null)
        {
            model.SelectedMonitor = monitor;
        }

        Tsuki.App.Services.AppSettingsData saved = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        Check("settings.ini holds language=ja", saved.GameLanguage == Tsuki.Engine.Contracts.Language.Ja);
        Check("settings.ini holds fullscreen=on", saved.Fullscreen == true);
        Check("settings.ini holds vsync=off", saved.VSync == false);
        if (big is null)
        {
            Check("no preset fits this display: the two checks of a chosen window size are left out", true);
        }
        else
        {
            Check($"settings.ini holds window={big.Width}x{big.Height}", saved.WindowSize == (big.Width, big.Height));
            Check("the Display page shows that preset as selected", model.SelectedWindowSize == big);
        }

        Check("settings.ini holds prompts=xbox", saved.Prompts == Tsuki.Ui.PromptSetting.Xbox);
        Check("settings.ini holds the monitor corner", monitor is null || saved.Monitor == new Tsuki.App.Services.MonitorOrigin(monitor.X, monitor.Y));
        Check("the status line follows", model.StatusLine.Contains("Fullscreen", StringComparison.Ordinal) && model.StatusLine.Contains("Xbox", StringComparison.Ordinal));
        Check("the sidebar follows the page", ((model.Page = LauncherPage.Saves) == LauncherPage.Saves) && model.SelectedNav.Page == LauncherPage.Saves);

        model.BackupNowCommand.Execute(null);
        Check("a backup of settings.ini was made (no save yet)", model.Backups.Count == 1 && model.Backups[0].Info.Files.SequenceEqual(["settings.ini"]));

        // package K20: the shortcuts go into a temporary folder of the profile, never onto the desktop of the machine the self-test runs on
        string links = Path.Combine(paths.DataDirectory, "selftest-links");
        model.ShortcutFolders = new ShortcutFolders(Path.Combine(links, "desktop"), Path.Combine(links, "startmenu"));
        (IReadOnlyList<ShortcutSpec> desktop, string? desktopProblem) = model.PlanShortcuts(LauncherModel.ShortcutKind.DesktopGame);
        Check("the desktop shortcut plan names Tsukihime.lnk pointing at Tsukihime.exe" + (desktopProblem is null ? string.Empty : $" ({desktopProblem})"),
            desktopProblem is null && desktop.Count == 1 && Path.GetFileName(desktop[0].LinkPath) == Shortcuts.GameLinkName && Path.GetFileName(desktop[0].TargetPath) == GameLauncher.AppFileName);
        if (desktopProblem is null)
        {
            model.DesktopShortcutCommand.Execute(null);
            ShortcutInfo? made = Shortcuts.Read(desktop[0].LinkPath);
            Check("the desktop shortcut was written into the temporary folder and reads back", made is not null && made.TargetPath == desktop[0].TargetPath && made.WorkingDirectory == desktop[0].WorkingDirectory && made.IconPath == desktop[0].IconPath);
            Check("the shortcut's icon is the Arcueid .ico of the profile when the folder is ready, else the executable's crescent",
                model.Folder.IsReady ? desktop[0].IconPath == model.GameIconPath && model.GameIconPath is not null : desktop[0].IconPath == desktop[0].TargetPath);
            Check("the Play page says the shortcut exists and offers Replace", model.DesktopShortcutLabel == "Replace" && model.DesktopShortcutText.Contains("is on your desktop", StringComparison.Ordinal));
            model.DesktopShortcutCommand.Execute(null);
            Check("a second request asks before replacing", model.ConfirmVisible && model.Confirm is { ConfirmLabel: "Replace" });
            model.ConfirmNoCommand.Execute(null);
        }

        (IReadOnlyList<ShortcutSpec> start, string? startProblem) = model.PlanShortcuts(LauncherModel.ShortcutKind.StartMenu);
        if (startProblem is null)
        {
            model.StartMenuShortcutsCommand.Execute(null);
            Check("the Start-menu entries were written into the temporary folder", start.All(s => Shortcuts.Read(s.LinkPath) is { } info && info.TargetPath == s.TargetPath));
        }

        if (gameFolder is not null)
        {
            await model.SetGameFolderAsync(Path.GetDirectoryName(gameFolder.TrimEnd('\\', '/')) ?? gameFolder);
            Check("the parent of the game folder is resolved to the game folder", model.Folder.IsReady && model.Folder.Path == Path.GetFullPath(gameFolder));
            Check("game_dir.txt holds the folder", paths.ReadGameDirectory() == Path.GetFullPath(gameFolder));
            Check("the ready check is all good and Play is possible", model.ReadyAllGood && model.CanPlay);
            // package K23: the Arcueid icon was built from the player's files into the profile's icons folder, in every size, and the window shows it
            string? icon = model.GameIconPath;
            IReadOnlyList<Tsuki.Data.Icons.IconFrame> frames = icon is not null && File.Exists(icon) ? Tsuki.Data.Icons.FaceIcon.DecodeIco(File.ReadAllBytes(icon)) : [];
            Check($"the Arcueid icon was built into the profile ({icon ?? "none"}; {frames.Count} frames)",
                icon == Tsuki.Data.Icons.FaceIconCache.IconPath(paths.DataDirectory) && frames.Select(f => f.Size).SequenceEqual(Tsuki.Data.Icons.FaceIcon.Sizes) && Tsuki.Data.Icons.FaceIconCache.IsCurrent(paths.DataDirectory, gameFolder));
            Check("the launcher window takes the Arcueid icon", icon is not null && GameIcon.Load(icon) is { } loaded && loaded.Decoder?.Frames.Count == frames.Count);
            await model.SetGameFolderAsync(Path.Combine(paths.DataDirectory, "nowhere"));
            Check("a folder that is not the game is not remembered", paths.ReadGameDirectory() == Path.GetFullPath(gameFolder) && model.FolderMessage.Contains("not remembered", StringComparison.Ordinal));
        }

        await SaveChangeSelfTestAsync(model, paths, Check);
        return report.ToString();
    }

    /// <summary>
    /// v0.1.9: the settings pages follow what the game writes while the launcher is open, as the poll finds it. At its start the game takes the language and the
    /// window mode the launcher chose out of settings.ini and keeps them in save.dat's config block; its Config changes that block later. The pages then show
    /// what the game uses, never the save.dat of before the player's choice. A file another program holds when the poll reads it leaves the pages as they were
    /// (not the defaults) and is read at the next poll once it is free. Only into a profile with neither save.dat nor trophies.ini; the saves it writes are taken
    /// away again and settings.ini is put back as it was.
    /// </summary>
    private static async Task SettingsPagesFollowTheGameSelfTestAsync(LauncherModel model, LauncherPaths paths, Action<string, bool> check)
    {
        if (File.Exists(paths.SaveFile) || File.Exists(paths.TrophiesFile))
        {
            check("not a fresh profile: the step in which the game takes the launcher's language and window mode is left out (it would change the profile's save)", true);
            return;
        }

        string[] languages = ["Japanese", "English", "Simplified Chinese", "Traditional Chinese"];
        string Shows() => $"{languages[model.LanguageIndex]}, {(model.Fullscreen ? "fullscreen" : "windowed")}";

        // the footer names the language natively ("English", "日本語") and the window mode in English
        bool Footer(Tsuki.Engine.Contracts.Language language, bool fullscreen) =>
            model.StatusLine.Contains(LauncherProfile.LanguageName(language).Native, StringComparison.Ordinal)
            && model.StatusLine.Contains(fullscreen ? "Fullscreen" : "Windowed", StringComparison.Ordinal);
        DateTime saveTime = DateTime.UtcNow.AddMinutes(-10);
        void WriteSave(Tsuki.Engine.Contracts.Language language, bool fullscreen)
        {
            // as the game commits its config block; each with a time of its own (two commits within one tick of the file clock would share one, and the size is the same)
            var store = new Tsuki.Engine.Save.SaveStore();
            store.SystemData.Block.Config.Language = (short)language;
            store.SystemData.Block.Config.Bytes[Tsuki.App.Services.AppSettings.WindowModeOffset] = (byte)(fullscreen ? 1 : 0);
            Tsuki.Engine.Save.SaveFile.WriteAtomic(paths.SaveFile, store);
            saveTime = saveTime.AddSeconds(10);
            File.SetLastWriteTimeUtc(paths.SaveFile, saveTime);
        }

        string[] filesBefore = [.. Directory.EnumerateFiles(paths.DataDirectory).Select(f => Path.GetFileName(f))];
        Tsuki.App.Services.AppSettingsData settingsBefore = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        try
        {
            // the game saved its Config behind the launcher (Japanese, windowed): the Language and Display pages follow save.dat
            int saveReadings = model.SaveReadings;
            WriteSave(Tsuki.Engine.Contracts.Language.Ja, fullscreen: false);
            bool found = await model.PollAchievementsAsync();
            check($"the settings pages follow a save.dat the game wrote behind the launcher ({Shows()}), read once by the poll",
                found && model.LanguageIndex == 0 && !model.Fullscreen && Footer(Tsuki.Engine.Contracts.Language.Ja, false) && model.SaveReadings == saveReadings + 1 && model.Saves.Exists);

            // the player picks English and fullscreen on the launcher, then PLAY: the game takes both out of settings.ini and commits them into save.dat
            model.LanguageIndex = 1;
            model.Fullscreen = true;
            string chosen = Shows();
            bool taken = Tsuki.App.Services.AppSettings.Update(paths.SettingsFile, s => s with { GameLanguage = null, Fullscreen = null });
            WriteSave(Tsuki.Engine.Contracts.Language.En, fullscreen: true);
            Tsuki.App.Services.AppSettingsData left = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
            found = await model.PollAchievementsAsync();
            check($"after the game took the launcher's language and window mode at its start (chosen: {chosen}), the poll shows what the game uses ({Shows()}), and so does the footer",
                taken && left.GameLanguage is null && left.Fullscreen is null && found && model.LanguageIndex == 1 && model.Fullscreen && Footer(Tsuki.Engine.Contracts.Language.En, true));

            // the game's Config changes the language later, in save.dat alone
            WriteSave(Tsuki.Engine.Contracts.Language.Zc, fullscreen: true);
            found = await model.PollAchievementsAsync();
            check($"the game's Config changes the language in save.dat alone, and the Language page follows ({Shows()})", found && model.LanguageIndex == 2 && model.Fullscreen);

            // settings the player chose (none of them a default: vsync is on, the notice and its sound off until chosen), then a change by the game while another
            // program holds settings.ini without sharing: the pages keep what they showed
            model.VSync = false;
            model.PromptsIndex = 3;
            model.TrophyNotice = true;
            model.TrophySound = true;
            int settingsReadings = model.SettingsReadings;
            _ = Tsuki.App.Services.AppSettings.Update(paths.SettingsFile, s => s with { Prompts = Tsuki.Ui.PromptSetting.PlayStation });
            bool kept;
            using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool news = await model.PollAchievementsAsync();
                bool again = !await model.PollAchievementsAsync();
                kept = !news && again && !model.VSync && model.PromptsIndex == 3 && model.TrophyNotice && model.TrophySound && model.LanguageIndex == 2 && model.Fullscreen
                    && model.SettingsReadings == settingsReadings + 2;
            }

            found = await model.PollAchievementsAsync();
            check($"a settings.ini another program holds when the poll reads it leaves the switches as they were, not the defaults (vsync {OnOff(model.VSync)}, Notify {OnOff(model.TrophyNotice)}, sound {OnOff(model.TrophySound)}), and is read at the next poll once it is free (prompts {model.PromptsIndex}, PlayStation)",
                kept && found && model.PromptsIndex == 2 && !model.VSync && model.TrophyNotice && model.TrophySound && model.SettingsReadings == settingsReadings + 3);

            // the same for a save.dat that is held when the poll reads it: the pages keep what they showed, and the Saves page does not call it damaged
            saveReadings = model.SaveReadings;
            WriteSave(Tsuki.Engine.Contracts.Language.Zt, fullscreen: false);
            bool saveKept;
            using (new FileStream(paths.SaveFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                _ = await model.PollAchievementsAsync();
                saveKept = model.LanguageIndex == 2 && model.Fullscreen && model.Saves.Error is null && model.SaveReadings == saveReadings + 1;
            }

            found = await model.PollAchievementsAsync();
            check($"a save.dat another program holds when the poll reads it leaves the pages as they were (no damaged save shown), and is read at the next poll once it is free ({Shows()})",
                saveKept && found && model.LanguageIndex == 3 && !model.Fullscreen && model.Saves.Error is null && model.SaveReadings == saveReadings + 2);
        }
        finally
        {
            // what this step made goes again, and settings.ini says what it said
            try
            {
                foreach (string file in Directory.EnumerateFiles(paths.DataDirectory).Where(f => !filesBefore.Contains(Path.GetFileName(f)) && Path.GetFileName(f).StartsWith("save.dat", StringComparison.Ordinal)))
                {
                    File.Delete(file);
                }

                if (!Tsuki.App.Services.AppSettings.Save(paths.SettingsFile, settingsBefore))
                {
                    check("settings.ini was put back as it was before the step", false);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                check("what the step in which the game takes the launcher's settings made was taken away again (" + e.Message + ")", false);
            }

            await model.RefreshAsync();
        }
    }

    /// <summary>
    /// v0.1.9: a restore or an import of a save never removes an achievement. A save that implies achievements no record holds yet (a profile from before
    /// v0.1.9: one of Ciel's lessons seen and, told by the game folder's scene table, day 1 finished) is replaced as the Saves page does it: an import of a PS4
    /// save, then a restore of an older backup. Each must first record what the page showed in trophies.ini, the chapter only through the launcher's game
    /// folder. Only into a profile with neither save.dat nor trophies.ini; the save, the record, the image and the backups it made are taken away again.
    /// </summary>
    private static async Task SaveChangeSelfTestAsync(LauncherModel model, LauncherPaths paths, Action<string, bool> check)
    {
        if (File.Exists(paths.SaveFile) || File.Exists(paths.TrophiesFile))
        {
            check("not a fresh profile: the restore and import step is left out (it would change the profile's save and record)", true);
            return;
        }

        string? folder = model.Folder.State is FolderState.NotSet or FolderState.Missing ? null : model.Folder.Path;
        int? dayOne = null;
        if (model.Folder.IsReady && folder is not null)
        {
            try
            {
                using Tsuki.Data.GameFolderSource source = Tsuki.Data.GameFolderSource.Open(folder);
                dayOne = source.Eboot.GameFlow.TrophyScenes[0];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or Tsuki.Data.Eboot.EbootFormatException)
            {
                // the check below says what the page showed
            }
        }

        void WriteSave()
        {
            var store = new Tsuki.Engine.Save.SaveStore();
            void Set(int firstWord, int node) =>
                store.SystemData.Block.SetG(firstWord + (node / 16), (ushort)(store.SystemData.Block.GetG(firstWord + (node / 16)) | (1 << (node % 16))));
            Set(Tsuki.Engine.Save.SystemData.SceneSeenWord, TrophyTable.LessonNodeFirst);
            if (dayOne is int scene)
            {
                Set(Tsuki.Engine.Save.SystemData.SceneDoneWord, scene);
            }

            Tsuki.Engine.Save.SaveFile.WriteAtomic(paths.SaveFile, store);
        }

        static string Ids(IEnumerable<int> ids) => string.Join(", ", ids);
        int[] Shown() => [.. Achievements.Read(paths, folder).Rows.Where(r => r.Earned).Select(r => r.Id)];
        (int[] Ids, bool Dateless) Recorded()
        {
            TrophyLog record = TrophyLog.ReadFile(paths.TrophiesFile, out TrophyFileState state);
            return (state == TrophyFileState.Read ? [.. record.Ids] : [], record.Ids.All(id => record.EarnedUtc(id) is null));
        }

        int[] expected = dayOne is null ? [TrophyTable.FirstLesson] : [TrophyTable.FirstChapter, TrophyTable.FirstLesson];
        string told = dayOne is null ? "by the lessons alone, there being no game folder" : "the chapter told by the game folder";
        string[] filesBefore = [.. Directory.EnumerateFiles(paths.DataDirectory).Select(f => Path.GetFileName(f))];
        HashSet<string> backupsBefore = [.. SaveTools.List(paths).Select(b => b.Path)];
        string image = Path.Combine(paths.DataDirectory, "selftest-savedata000");
        try
        {
            // the Saves page's import of a PS4 save (an empty one) over that save
            WriteSave();
            int[] shown = Shown();
            File.WriteAllBytes(image, Tsuki.Engine.Save.SaveFile.ExportPs4Image(new Tsuki.Engine.Save.SaveStore()));
            var importWatch = new GameWatch();
            model.AskImport(image);
            model.ConfirmYesCommand.Execute(null);
            (int[] imported, bool importDateless) = Recorded();
            (bool game, bool mixed) = importWatch.Stop();
            if (mixed)
            {
                check($"a game started or ended on this machine during the import: the record is not judged (trophies.ini holds {Ids(imported)})", true);
            }
            else if (game)
            {
                check($"a game runs on this machine: an import leaves the record to it (trophies.ini holds {Ids(imported)})", imported.Length == 0);
            }
            else
            {
                check($"importing a PS4 save first records what the replaced save implied, {told} (the page showed {Ids(shown)}; trophies.ini holds {Ids(imported)}, without a date)",
                    shown.SequenceEqual(expected) && imported.SequenceEqual(shown) && importDateless && Shown().SequenceEqual(shown));
            }

            // the restore of an older backup (of that empty save, made on the Saves page) over the same save again
            File.Delete(paths.TrophiesFile);
            HashSet<string> beforeBackup = [.. model.Backups.Select(b => b.Info.Path)];
            model.BackupNowCommand.Execute(null);
            BackupRow? older = model.Backups.FirstOrDefault(b => !beforeBackup.Contains(b.Info.Path));
            WriteSave();
            shown = Shown();
            var restoreWatch = new GameWatch();
            older?.Restore.Execute(null);
            model.ConfirmYesCommand.Execute(null);
            (int[] restored, bool restoreDateless) = Recorded();
            (game, mixed) = restoreWatch.Stop();
            if (mixed)
            {
                check($"a game started or ended on this machine during the restore: the record is not judged (trophies.ini holds {Ids(restored)})", older is not null);
            }
            else if (game)
            {
                check($"a game runs on this machine: a restore leaves the record to it (trophies.ini holds {Ids(restored)})", older is not null && restored.Length == 0);
            }
            else
            {
                check($"restoring an older backup first records what the replaced save implied (the page showed {Ids(shown)}; trophies.ini holds {Ids(restored)}, without a date), and the page still shows them",
                    older is not null && shown.SequenceEqual(expected) && restored.SequenceEqual(shown) && restoreDateless && Shown().SequenceEqual(shown));
            }
        }
        finally
        {
            // what this step made goes again: a made-up achievement must not stay in a profile
            try
            {
                foreach (string file in Directory.EnumerateFiles(paths.DataDirectory).Where(f => !filesBefore.Contains(Path.GetFileName(f))))
                {
                    string name = Path.GetFileName(file);
                    if (name == Path.GetFileName(image) || name.StartsWith("save.dat", StringComparison.Ordinal) || name.StartsWith(TrophyLog.FileName, StringComparison.Ordinal))
                    {
                        File.Delete(file);
                    }
                }

                foreach (BackupInfo made in SaveTools.List(paths).Where(b => !backupsBefore.Contains(b.Path)))
                {
                    Directory.Delete(made.Path, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                check("what the restore and import step made was taken away again (" + e.Message + ")", false);
            }

            model.ShowToast(string.Empty);
            await model.RefreshAsync();
        }
    }

    /// <summary>
    /// Whether a game ran on this machine during one step of the self-test, looked at every 20 ms from its start to its end: a restore and an import leave
    /// trophies.ini to a running game, and they ask at moments of their own. Other programs start and end games while the self-test runs (the tests' own runs
    /// of the game among them, a game lives far longer than 20 ms), so a step during which a game started or ended is not judged, rather than judged by one
    /// look at its start.
    /// </summary>
    private sealed class GameWatch
    {
        private readonly System.Threading.Timer _timer;
        private int _running;
        private int _notRunning;

        public GameWatch()
        {
            Look();
            _timer = new System.Threading.Timer(_ => Look(), null, 20, 20);
        }

        private void Look() => Interlocked.Increment(ref GameLauncher.IsGameRunning() ? ref _running : ref _notRunning);

        /// <summary>Ends the watch: whether a game ran at every look, and whether it ran at some looks and not at others.</summary>
        public (bool Running, bool Mixed) Stop()
        {
            using (var stopped = new ManualResetEvent(false))
            {
                if (_timer.Dispose(stopped)) { stopped.WaitOne(); }
            }

            Look();
            int running = Volatile.Read(ref _running), notRunning = Volatile.Read(ref _notRunning);
            return (notRunning == 0, running > 0 && notRunning > 0);
        }
    }

    /// <summary>
    /// v0.1.9, the Achievements page: it is in the sidebar, lists the 30 achievements with none earned on a fresh profile, reads a sample trophies.ini back when the
    /// poll finds it (and reads nothing when the files did not change), keeps the chosen tab over that, and its two switches (the notice and its sound, both off
    /// while settings.ini says nothing) write trophy_notice and trophy_sound. The sample file is written only into a profile that has neither a trophies.ini nor a
    /// save.dat and is taken away again (also when a step in between fails), so the rest of the self-test sees the profile it expects and no made-up achievement
    /// stays in a profile; the two keys are put back as settings.ini had them, so no notice or sound is left turned on for the player's game.
    /// </summary>
    private static async Task AchievementsSelfTestAsync(LauncherModel model, LauncherPaths paths, Action<string, bool> check)
    {
        // the switches' keys as settings.ini has them now (absent on a fresh profile): every step below that turns a switch puts them back
        Tsuki.App.Services.AppSettingsData atStart = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        string[] sidebar = [.. model.NavItems.Select(n => n.Page.ToString())];
        check($"the sidebar has {sidebar.Length} pages, Achievements between Saves and Help ({string.Join(", ", sidebar)})",
            sidebar.SequenceEqual(Enum.GetNames<LauncherPage>()) && Array.IndexOf(sidebar, nameof(LauncherPage.Achievements)) == Array.IndexOf(sidebar, nameof(LauncherPage.Saves)) + 1);
        string Tabs() => string.Join(", ", model.AchievementTabs.Select(t => t.Label));

        // the pictures of the rows and of the summary card are for a page that was shown: until then the launcher draws no tile and no emblem and asks for no icon
        check($"before the Achievements page is shown nothing is made for it ({model.AchievementIconRequests} icons asked for, {model.AchievementRows.Count(r => r.Art != AchievementArt.None)} rows and {model.AchievementGrades.Count(g => g.Picture is not null)} grades with a picture)",
            model.AchievementIconRequests == 0 && model.AchievementRows.All(r => r.Art == AchievementArt.None && r.Picture is null) && model.AchievementGrades.All(g => g.Picture is null));
        if (File.Exists(paths.TrophiesFile) || File.Exists(paths.SaveFile))
        {
            // not a fresh profile: what it holds is shown, nothing is written into it
            check($"the Achievements page lists {model.AchievementRows.Count} rows of this profile ({model.AchievementCountLine}; {Tabs()})", model.AchievementRows.Count == Tsuki.Engine.Trophies.TrophyTable.Count);
        }
        else
        {
            check($"the Achievements page lists {model.AchievementRows.Count} rows, none earned on a fresh profile ({model.AchievementPercentText}, {model.AchievementCountLine}; {Tabs()})",
                model.AchievementRows.Count == 30 && model.AchievementRows.All(r => !r.Earned && r.StateLabel == LauncherTexts.AchievementLocked) && model.AchievementSummary.Earned == 0
                && model.AchievementPercentText == "0%" && Tabs() == "All (30), Earned (0), Locked (30)");

            int readings = model.AchievementReadings;
            bool again = await model.PollAchievementsAsync();
            check("a poll with unchanged files reads nothing", !again && model.AchievementReadings == readings);

            // the Earned tab is chosen before the file appears: the poll must fill it and leave it chosen
            model.SelectedAchievementTab = model.AchievementTabs[1];
            check("the Earned tab is empty and says so", model.AchievementRows.Count == 0 && model.NoAchievementRows && model.AchievementsEmptyText == LauncherTexts.AchievementsNothingEarned);
            var sample = new Tsuki.Engine.Trophies.TrophyLog();
            var when = new DateTime(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);
            _ = sample.Add(1, when);
            _ = sample.Add(2, when.AddMinutes(42));
            _ = sample.Add(5, null);

            // only the sample itself is ever deleted: a file that says anything else is the profile's own
            void RemoveSample()
            {
                try
                {
                    if (File.Exists(paths.TrophiesFile) && File.ReadAllText(paths.TrophiesFile) == sample.Format())
                    {
                        File.Delete(paths.TrophiesFile);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the checks that follow say what is still there
                }
            }

            Directory.CreateDirectory(paths.DataDirectory);
            sample.Write(paths.TrophiesFile);
            try
            {
                bool found = await model.PollAchievementsAsync();
                AchievementRowView[] earned = [.. model.AchievementRows];
                check($"a sample trophies.ini is read back by the poll ({model.AchievementCountLine}, {model.AchievementPercentText}; {Tabs()}; rows {string.Join(", ", earned.Select(r => $"{r.Id} {r.StateLabel}"))})",
                    found && model.AchievementReadings == readings + 1 && model.AchievementSummary.Earned == 3 && model.AchievementPercentText == "10%"
                    && Tabs() == "All (30), Earned (3), Locked (27)" && earned.Select(r => r.Id).SequenceEqual([1, 2, 5])
                    && earned[0].StateLabel == LauncherTexts.AchievementEarnedAt(when) && earned[1].StateLabel == LauncherTexts.AchievementEarnedAt(when.AddMinutes(42))
                    && earned[2].StateLabel == LauncherTexts.AchievementEarned);
                check("the poll kept the Earned tab chosen", model.SelectedAchievementTab.Filter == AchievementFilter.Earned);
                check($"the summary card counts the grades ({string.Join(", ", model.AchievementGrades.Select(g => $"{g.Label} {g.Count}"))})",
                    model.AchievementGrades.Select(g => g.Count).SequenceEqual(["0 / 1", "0 / 4", "1 / 21", "2 / 4"]));
                model.SelectedAchievementTab = model.AchievementTabs[2];
                check("the Locked tab lists the other 27", model.AchievementRows.Count == 27 && model.AchievementRows.All(r => !r.Earned));
                model.SelectedAchievementTab = model.AchievementTabs[0];

                // the page itself, never shown (as the render draws it): scrolled down its list, it stays there when a poll changes rows, and its tab control keeps its tab
                LauncherPage shown = model.Page;
                model.Page = LauncherPage.Achievements;
                var shell = new Shell { Animate = false, DataContext = model, Width = Width, Height = WindowHeight };
                using (new HwndSource(new HwndSourceParameters("TsukihimeLauncherSelfTest", Width, WindowHeight) { ParentWindow = MessageOnly, WindowStyle = 0 }) { RootVisual = shell })
                {
                    await LayoutAsync(shell, Width, WindowHeight);
                    ScrollViewer? scroll = Find<ScrollViewer>(shell, s => s.Name == "Scroll");
                    ListBox? tabs = Find<ListBox>(shell, l => ReferenceEquals(l.ItemsSource, model.AchievementTabs));
                    ItemsControl? list = Find<ItemsControl>(shell, l => ReferenceEquals(l.ItemsSource, model.AchievementRows));
                    ScreenReaderSelfTest(model, shell, tabs, check);
                    await SwitchesOnThePageSelfTestAsync(model, paths, shell, atStart, check);
                    await AchievementPicturesSelfTestAsync(model, paths, shell, list, sample, when.AddHours(3), check);
                    scroll?.ScrollToVerticalOffset(700);
                    await LayoutAsync(shell, Width, WindowHeight);
                    double offsetBefore = scroll?.VerticalOffset ?? -1;

                    // on the All tab a row's place is its id: achievement 10 is locked with and without the sample, achievement 1 is earned only with it
                    AchievementRowView stays = model.AchievementRows[10];
                    AchievementRowView changes = model.AchievementRows[1];
                    DependencyObject? drawn = list?.ItemContainerGenerator.ContainerFromIndex(10);
                    int asked = model.AchievementIconRequests;
                    RemoveSample();
                    bool gone = await model.PollAchievementsAsync();
                    await model.AchievementPicturesSettledAsync();
                    await LayoutAsync(shell, Width, WindowHeight);
                    double offsetAfter = scroll?.VerticalOffset ?? -2;
                    check("the sample file was taken away again and the page follows", gone && model.AchievementSummary.Earned == 0 && model.AchievementRows.Count == 30);
                    check("the achievements that are locked again show the locked tile, and no icon was asked for over that",
                        model.AchievementRows.All(r => r.Art == AchievementArt.Locked && r.Picture is not null) && model.AchievementIconRequests == asked);
                    check(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the list stayed where it was scrolled to over that refresh (offset {offsetBefore:0} before, {offsetAfter:0} after) and the tab control kept its tab"),
                        offsetBefore > 0 && offsetAfter == offsetBefore && tabs is not null && ReferenceEquals(tabs.SelectedItem, model.AchievementTabs[0]));
                    check("only the rows that changed were drawn anew: a row that stayed locked is the same row in the same place of the page",
                        drawn is not null && ReferenceEquals(model.AchievementRows[10], stays) && ReferenceEquals(list?.ItemContainerGenerator.ContainerFromIndex(10), drawn)
                        && !ReferenceEquals(model.AchievementRows[1], changes));
                }

                shell.DataContext = null;
                model.Page = shown;
            }
            finally
            {
                // a record of achievements only ever grows: a sample left behind by a failure above would be the profile's for good
                RemoveSample();
            }
        }

        // the summary card's four grades: the emblem of each, drawn by the port's own code (a page that was never shown has none yet); the same names either way
        AchievementGradeView[] grades = [.. model.AchievementGrades];
        string names = model.AchievementsNoGame ? "the port's own (no game folder)" : "the game's chapter titles";
        bool namesRight = model.AchievementRows.All(r => r.Name.Length > 0) && (!model.Folder.IsReady || !model.AchievementsNoGame);
        if (grades.All(g => g.Picture is null))
        {
            check($"the page was not shown, so the summary card has no emblem yet; the names are {names}", namesRight);
        }
        else
        {
            check($"the summary card shows the emblem of each grade, a cup drawn by the port's own code ({string.Join(", ", grades.Select(g => $"{g.Label} {(IsEmblem(g.Picture, g.Grade) ? "emblem" : "something else")}"))}); the names are {names}",
                namesRight && grades.Select(g => g.Grade).SequenceEqual([TrophyGrade.Platinum, TrophyGrade.Gold, TrophyGrade.Silver, TrophyGrade.Bronze])
                && grades.All(g => IsEmblem(g.Picture, g.Grade)) && grades.Select(g => g.Picture).Distinct().Count() == 4);
        }

        // the two switches through the model, on any profile: off while settings.ini does not say otherwise (D7), and each writes its own key and keeps the
        // other's. Both end off, where the next step starts
        Tsuki.App.Services.AppSettingsData Load() => Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        Tsuki.App.Services.AppSettingsData file = Load();
        bool noticeDefault = model.TrophyNotice == file.TrophyNoticeOn;
        bool soundDefault = model.TrophySound == file.TrophySoundOn;
        model.TrophyNotice = true;
        bool noticeOn = Load() is { TrophyNotice: true } a && a.TrophySound == file.TrophySound && model.TrophyNotice && model.TrophySound == file.TrophySoundOn;
        model.TrophyNotice = false;
        bool noticeOff = Load() is { TrophyNotice: false } b && b.TrophySound == file.TrophySound && !model.TrophyNotice;
        check("the Notify switch is off while settings.ini does not say otherwise, and writes trophy_notice=on and trophy_notice=off, leaving trophy_sound as it was", noticeDefault && noticeOn && noticeOff);
        model.TrophySound = true;
        bool soundOn = Load() is { TrophySound: true, TrophyNotice: false } && model.TrophySound && !model.TrophyNotice;
        model.TrophySound = false;
        bool soundOff = Load() is { TrophySound: false, TrophyNotice: false } && !model.TrophySound && !model.TrophyNotice;
        check("the sound switch is off while settings.ini does not say otherwise, and writes trophy_sound=on and trophy_sound=off, leaving trophy_notice as it was", soundDefault && soundOn && soundOff);
        await SwitchesFollowTheGameSelfTestAsync(model, paths, check);

        bool back = await PutSwitchesBackAsync(model, paths, atStart);
        check($"the switches' checks put trophy_notice and trophy_sound back as settings.ini had them ({SwitchKeys(paths)}), and the switches show them ({OnOff(model.TrophyNotice)}, {OnOff(model.TrophySound)})", back);
    }

    /// <summary>
    /// D8 P-2, the Achievements page as it opens (not scrolled), on the page itself as the render draws it. In the window the launcher opens with (1200 x 780)
    /// the summary, the whole notice card (both switch rows and the note where the record is kept) and each note the page's state shows (the record could not
    /// be read, no game folder; they come after the notice card, above the tabs) are in view, none cut by the footer. In the smallest window (960 x 600) the
    /// summary and both switch rows are, the notice's first; the line says where the record's note ends there. A fresh profile gets a trophies.ini that is no
    /// record for this step, so that the note of the record that could not be read shows too (without a game folder that is both notes, the most the page shows
    /// above its list), and that file is taken away again.
    /// </summary>
    private static async Task AchievementsPageFitSelfTestAsync(LauncherModel model, LauncherPaths paths, Action<string, bool> check)
    {
        const string NotARecord = "this is not a record of achievements\n";
        bool wrote = false;
        if (!File.Exists(paths.TrophiesFile) && !File.Exists(paths.SaveFile))
        {
            Directory.CreateDirectory(paths.DataDirectory);
            File.WriteAllText(paths.TrophiesFile, NotARecord);
            wrote = true;
        }

        LauncherPage shown = model.Page;
        var shell = new Shell { Animate = false, DataContext = model, Width = Width, Height = WindowHeight };
        try
        {
            _ = await model.PollAchievementsAsync();
            model.Page = LauncherPage.Achievements;
            await model.AchievementPicturesSettledAsync();
            using var source = new HwndSource(new HwndSourceParameters("TsukihimeLauncherSelfTest", Width, WindowHeight) { ParentWindow = MessageOnly, WindowStyle = 0 }) { RootVisual = shell };
            string notes = (model.AchievementsDamaged, model.AchievementsNoGame) switch
            {
                (true, true) => "the record could not be read, no game folder",
                (true, false) => "the record could not be read",
                (false, true) => "no game folder",
                _ => "none",
            };

            // the window the launcher opens with: the summary, the whole notice card and each note of the page's state
            await LayoutAsync(shell, Width, WindowHeight);
            if (AchievementsView.Of(shell) is not { } view)
            {
                check("the shell shows the Achievements page", false);
                return;
            }

            AchievementsPage page = view.Page;
            List<FrameworkElement> wanted = [page.SummaryCard, page.NoticeCard];
            if (model.AchievementsDamaged)
            {
                wanted.Add(page.DamagedNote);
            }

            if (model.AchievementsNoGame)
            {
                wanted.Add(page.NoGameNote);
            }

            bool notesFollow = (view.Span(page.DamagedNote) is not null) == model.AchievementsDamaged && (view.Span(page.NoGameNote) is not null) == model.AchievementsNoGame;
            FrameworkElement last = wanted.MaxBy(e => view.Span(e)?.Bottom ?? double.PositiveInfinity)!;
            check($"in the window the launcher opens with ({Width} x {WindowHeight}) the Achievements page shows, without scrolling, the summary, the whole NOTIFY card (both switch rows and the record's note) and the notes of its state ({notes}); the last of them {view.Ends(last)}",
                notesFollow && (!wrote || model.AchievementsDamaged) && view.Scroll.VerticalOffset == 0 && wanted.All(view.Shows));

            // the smallest window: the summary and both switch rows, the notice's first; the record's note may take a scroll there
            shell.Width = MainWindowMinWidth;
            shell.Height = MainWindowMinHeight;
            await LayoutAsync(shell, MainWindowMinWidth, MainWindowMinHeight);
            bool ordered = view.Span(page.SummaryCard) is { } summary && view.Span(page.NoticeRow) is { } notice && view.Span(page.SoundRow) is { } sound
                && summary.Bottom <= notice.Top && notice.Bottom <= sound.Top;
            string note = view.Shows(page.FileNote) ? "is in view too" : "takes a scroll (it " + view.Ends(page.FileNote) + ")";
            check($"in the smallest window ({MainWindowMinWidth} x {MainWindowMinHeight}) the Achievements page shows, without scrolling, the summary and both switch rows, the Notify row first; the sound row {view.Ends(page.SoundRow)}, and the record's note {note}",
                ordered && view.Scroll.VerticalOffset == 0 && view.Shows(page.SummaryCard) && view.Shows(page.NoticeRow) && view.Shows(page.SoundRow));
        }
        finally
        {
            shell.DataContext = null;
            model.Page = shown;
            if (wrote)
            {
                // only the step's own file is taken away: a file that says anything else is the profile's
                try
                {
                    if (File.Exists(paths.TrophiesFile) && File.ReadAllText(paths.TrophiesFile) == NotARecord)
                    {
                        File.Delete(paths.TrophiesFile);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    check("the trophies.ini that is no record was taken away again (" + e.Message + ")", false);
                }

                _ = await model.PollAchievementsAsync();
                check("the trophies.ini that is no record was taken away again, and the page no longer says the record could not be read", !File.Exists(paths.TrophiesFile) && !model.AchievementsDamaged);
            }
        }
    }

    /// <summary>
    /// v0.1.9: the game's Config writes trophy_notice and trophy_sound while the launcher is open. The page's poll sees settings.ini changed behind the launcher and
    /// each switch follows (the binding is told); it reads settings.ini alone (no probe file goes into the profile folder), and the launcher's own writes are not
    /// read back as the game's. Starts and ends with both off (the switches' own checks leave them so).
    /// </summary>
    private static async Task SwitchesFollowTheGameSelfTestAsync(LauncherModel model, LauncherPaths paths, Action<string, bool> check)
    {
        // the switches' own writes just now: nothing for the poll to read
        int readings = model.SettingsReadings;
        bool ownWrite = !await model.PollAchievementsAsync() && model.SettingsReadings == readings;

        var told = new List<string?>();
        void Listen(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => told.Add(e.PropertyName);
        bool Game(Func<Tsuki.App.Services.AppSettingsData, Tsuki.App.Services.AppSettingsData> change)
        {
            told.Clear();
            return Tsuki.App.Services.AppSettings.Update(paths.SettingsFile, change);
        }

        model.PropertyChanged += Listen;
        try
        {
            // as the game's Config does it, behind the launcher: Achievement Notify on
            bool written = Game(s => s with { TrophyNotice = true });
            DateTime folderTime = Directory.GetLastWriteTimeUtc(paths.DataDirectory);
            bool found = await model.PollAchievementsAsync();
            bool on = written && found && model.TrophyNotice && !model.TrophySound && told.Contains(nameof(LauncherModel.TrophyNotice)) && model.SettingsReadings == readings + 1;
            bool noProbe = Directory.GetLastWriteTimeUtc(paths.DataDirectory) == folderTime;
            bool once = !await model.PollAchievementsAsync() && model.SettingsReadings == readings + 1;

            // and off again, the same way
            written = Game(s => s with { TrophyNotice = false });
            bool off = written && await model.PollAchievementsAsync() && !model.TrophyNotice && told.Contains(nameof(LauncherModel.TrophyNotice));
            check($"the Notify switch follows settings.ini when the game's Config changes trophy_notice ({(on ? "on" : "not on")}, then {(off ? "off" : "not off")}); the poll reads settings.ini alone ({(noProbe ? "no probe file" : "the profile folder changed")}), once per change, and not the launcher's own writes",
                ownWrite && on && noProbe && once && off && model.SettingsReadings == readings + 2);

            // Achievement Sound, the row after it, on and off the same way: the sound switch follows, the notice switch stays as it was
            written = Game(s => s with { TrophySound = true });
            bool soundOn = written && await model.PollAchievementsAsync() && model.TrophySound && !model.TrophyNotice && told.Contains(nameof(LauncherModel.TrophySound));
            bool soundOnce = !await model.PollAchievementsAsync() && model.SettingsReadings == readings + 3;
            written = Game(s => s with { TrophySound = false });
            bool soundOff = written && await model.PollAchievementsAsync() && !model.TrophySound && !model.TrophyNotice && told.Contains(nameof(LauncherModel.TrophySound));
            check($"the sound switch follows settings.ini when the game's Config changes trophy_sound ({(soundOn ? "on" : "not on")}, then {(soundOff ? "off" : "not off")}), once per change, and the Notify switch stays as it was",
                soundOn && soundOnce && soundOff && model.SettingsReadings == readings + 4);
        }
        finally
        {
            model.PropertyChanged -= Listen;
        }
    }

    /// <summary>
    /// v0.1.9 (D7), the page's two switches as built: the notice and, in the next row of the same card, its sound. Each shows what the model says (off on a fresh
    /// profile, settings.ini saying nothing), a screen reader hears each by its label, and a toggle on the page (what a click or a screen reader does) writes the
    /// switch's own key and keeps the other's. Puts both keys back as they were.
    /// </summary>
    private static async Task SwitchesOnThePageSelfTestAsync(LauncherModel model, LauncherPaths paths, Shell shell, Tsuki.App.Services.AppSettingsData atStart, Action<string, bool> check)
    {
        CheckBox? Switch(string property) =>
            Find<CheckBox>(shell, c => BindingOperations.GetBindingExpression(c, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == property);
        CheckBox? noticeFound = Switch(nameof(LauncherModel.TrophyNotice));
        CheckBox? soundFound = Switch(nameof(LauncherModel.TrophySound));
        if (noticeFound is not { } notice || soundFound is not { } sound)
        {
            check($"the page has the Notify switch and the sound switch ({(noticeFound is null ? "no" : "a")} Notify switch, {(soundFound is null ? "no" : "a")} sound switch)", false);
            return;
        }

        static string Shows(CheckBox box) => OnOff(box.IsChecked == true);
        static string Heard(CheckBox box) => UIElementAutomationPeer.CreatePeerForElement(box)?.GetName() ?? string.Empty;
        bool Agree() => notice.IsChecked == model.TrophyNotice && sound.IsChecked == model.TrophySound;

        // the sound's row is the row after the notice's, in the same card
        bool next = notice.Parent is HeaderedContentControl noticeRow && sound.Parent is HeaderedContentControl soundRow && noticeRow.Parent is Panel card
            && ReferenceEquals(soundRow.Parent, card) && card.Children.IndexOf(soundRow) == card.Children.IndexOf(noticeRow) + 1;
        check($"the page shows the Notify switch and, in the next row, the sound switch ({Shows(notice)}, {Shows(sound)}; settings.ini: {SwitchKeys(paths)}), heard by their labels ({Heard(notice)}; {Heard(sound)})",
            next && Agree() && Heard(notice) == LauncherTexts.AchievementsNoticeLabel && Heard(sound) == LauncherTexts.AchievementsSoundLabel);

        // each switch toggled twice, the notice first: its own key changes, the other's is kept, and the page shows what the model says
        List<string> steps = [];
        bool each = true;
        foreach (CheckBox box in new[] { notice, sound, notice, sound })
        {
            bool isNotice = ReferenceEquals(box, notice);
            (bool wasNotice, bool wasSound) = (model.TrophyNotice, model.TrophySound);
            Tsuki.App.Services.AppSettingsData before = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
            if (UIElementAutomationPeer.CreatePeerForElement(box)?.GetPattern(PatternInterface.Toggle) is System.Windows.Automation.Provider.IToggleProvider toggle)
            {
                toggle.Toggle();
            }
            else
            {
                each = false;
            }

            Tsuki.App.Services.AppSettingsData after = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
            each &= Agree() && (isNotice
                ? after.TrophyNotice == !wasNotice && after.TrophySound == before.TrophySound && model.TrophyNotice == !wasNotice && model.TrophySound == wasSound
                : after.TrophySound == !wasSound && after.TrophyNotice == before.TrophyNotice && model.TrophySound == !wasSound && model.TrophyNotice == wasNotice);
            steps.Add($"{(isNotice ? "Notify" : "sound")} {Shows(box)}: {SwitchKeys(paths)}");
        }

        bool back = await PutSwitchesBackAsync(model, paths, atStart);
        check($"a toggle of a switch on the page writes its own key and keeps the other's ({string.Join("; ", steps)}), and both are put back as they were ({SwitchKeys(paths)}; the page shows {Shows(notice)}, {Shows(sound)})",
            each && back && Agree());
    }

    /// <summary>
    /// Puts trophy_notice and trophy_sound back as settings.ini had them (absent included), as the game would write them, and has the poll show them: the
    /// switches' checks leave the player's choice as they found it (a notice turned on by a self-test would show in the player's game).
    /// </summary>
    private static async Task<bool> PutSwitchesBackAsync(LauncherModel model, LauncherPaths paths, Tsuki.App.Services.AppSettingsData atStart)
    {
        bool written = Tsuki.App.Services.AppSettings.Update(paths.SettingsFile, s => s with { TrophyNotice = atStart.TrophyNotice, TrophySound = atStart.TrophySound });
        _ = await model.PollAchievementsAsync();
        Tsuki.App.Services.AppSettingsData now = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        return written && now.TrophyNotice == atStart.TrophyNotice && now.TrophySound == atStart.TrophySound
            && model.TrophyNotice == atStart.TrophyNoticeOn && model.TrophySound == atStart.TrophySoundOn;
    }

    /// <summary>What settings.ini says of the two switches' keys, as "trophy_notice on, trophy_sound unset" (the keys by their names: the switches are "Notify" and "sound").</summary>
    private static string SwitchKeys(LauncherPaths paths)
    {
        Tsuki.App.Services.AppSettingsData s = Tsuki.App.Services.AppSettings.Load(paths.SettingsFile);
        static string Word(bool? value) => value switch { true => "on", false => "off", null => "unset" };
        return $"trophy_notice {Word(s.TrophyNotice)}, trophy_sound {Word(s.TrophySound)}";
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>
    /// v0.1.9, the pictures of the rows, on the page as the self-test built it (the All tab, the sample's achievements 1, 2 and 5 earned): a locked row shows the
    /// locked tile and no game picture is ever asked for it; an earned row shows its icon when the game folder is there and the emblem of its grade (the summary
    /// card's bitmap of that grade) when it is not;
    /// the page draws what the rows hold; a poll that finds nothing new and Check again keep every bitmap; an achievement earned while the page is open gets its
    /// icon. Leaves the sample with achievement 14 added.
    /// </summary>
    private static async Task AchievementPicturesSelfTestAsync(
        LauncherModel model, LauncherPaths paths, Shell shell, ItemsControl? list, Tsuki.Engine.Trophies.TrophyLog sample, DateTime later, Action<string, bool> check)
    {
        static bool IsTile(ImageSource? picture) => picture is BitmapSource { PixelWidth: 240, PixelHeight: 240, IsFrozen: true };

        // a row that shows the emblem shows the summary card's bitmap of its grade: one object per grade, drawn by code
        bool IsCardEmblem(AchievementRowView row) => model.AchievementGrades.Single(g => g.Grade == row.Row.Grade).Picture is { } card && ReferenceEquals(row.Picture, card) && IsEmblem(card, row.Row.Grade);
        string Asked()
        {
            string[] ids = [.. Enumerable.Range(0, 30).Where(id => model.AchievementIconRequestsOf(id) > 0).Select(id => $"{id} x {model.AchievementIconRequestsOf(id)}")];
            return ids.Length == 0 ? "none" : string.Join(", ", ids);
        }

        await model.AchievementPicturesSettledAsync();
        await LayoutAsync(shell, Width, WindowHeight);
        AchievementRowView[] rows = [.. model.AchievementRows];
        AchievementRowView[] locked = [.. rows.Where(r => !r.Earned)];
        AchievementRowView[] earned = [.. rows.Where(r => r.Earned)];
        bool noFolder = model.Folder.State is FolderState.NotSet or FolderState.Missing;
        static string Arts(IEnumerable<AchievementRowView> of) => string.Join(", ", of.Select(r => $"{r.Id} {r.Art}"));

        int tiles = locked.Select(r => r.Picture).Distinct().Count();
        check($"the {locked.Length} locked rows show the locked tile of their grade ({tiles} tiles) and no game picture was asked for a locked achievement",
            locked.Length == 27 && locked.All(r => r.Art == AchievementArt.Locked && IsTile(r.Picture) && model.AchievementIconRequestsOf(r.Id) == 0)
            && tiles == locked.Select(r => r.Row.Grade).Distinct().Count()
            && locked.GroupBy(r => r.Row.Grade).All(g => g.All(r => ReferenceEquals(r.Picture, g.First().Picture))));
        if (noFolder)
        {
            check($"without a game folder the earned rows show the emblem of their grade, the summary card's bitmap of it ({Arts(earned)}) and no icon is asked for ({Asked()})",
                earned.Length == 3 && earned.All(r => r.Art == AchievementArt.Emblem && IsCardEmblem(r)) && model.AchievementIconRequests == 0);
        }
        else
        {
            check($"the earned rows show their icons, cut from the player's game pictures ({Arts(earned)}; asked for: {Asked()})",
                earned.Length == 3 && model.AchievementIconRequests == 3
                && earned.All(r => model.AchievementIconRequestsOf(r.Id) == 1 && (r.Art == AchievementArt.Icon ? IsTile(r.Picture) : r.Art == AchievementArt.Emblem && IsCardEmblem(r) && !model.Folder.IsReady))
                && earned.Where(r => r.Art == AchievementArt.Icon).Select(r => r.Picture).Distinct().Count() == earned.Count(r => r.Art == AchievementArt.Icon));
        }

        // the page itself: the square of each row is that row's bitmap, its one picture
        int squares = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (list?.ItemContainerGenerator.ContainerFromIndex(i) is { } container)
            {
                Image? image = Find<Image>(container, _ => true);
                bool right = rows[i].Picture is not null && ReferenceEquals(image?.Source, rows[i].Picture) && Find<Image>(container, other => !ReferenceEquals(other, image)) is null;
                squares += right ? 1 : 0;
            }
        }

        check($"the page draws the picture of each of its {rows.Length} rows ({squares} squares show their row's bitmap)", rows.Length == 30 && squares == rows.Length);

        // the game writes the file again with the same content: the poll reads it, finds nothing new and touches no row
        ImageSource?[] before = [.. rows.Select(r => r.Picture)];
        bool Kept(int except = -1) => model.AchievementRows.Count == rows.Length
            && Enumerable.Range(0, rows.Length).All(i => i == except || (ReferenceEquals(model.AchievementRows[i], rows[i]) && ReferenceEquals(rows[i].Picture, before[i])));
        int asked = model.AchievementIconRequests;
        int readings = model.AchievementReadings;
        File.SetLastWriteTimeUtc(paths.TrophiesFile, File.GetLastWriteTimeUtc(paths.TrophiesFile).AddSeconds(7));
        bool news = await model.PollAchievementsAsync();
        await model.AchievementPicturesSettledAsync();
        check("a poll that reads the same achievements again makes no picture again: every row is the same row with the same bitmap, and no icon is asked for",
            !news && model.AchievementReadings == readings + 1 && Kept() && model.AchievementIconRequests == asked);

        // Check again: what was kept of the game folder is forgotten, the icons are made again, and equal pixels keep the bitmap on show
        await model.LoadAchievementsAsync();
        await model.AchievementPicturesSettledAsync();
        check(
            noFolder
                ? "Check again without a game folder asks for no icon, and every row keeps its picture"
                : $"Check again forgets the icons and has them made again (asked for: {Asked()}); every row keeps its bitmap, the pixels being the same",
            Kept() && model.AchievementIconRequests == (noFolder ? 0 : 6) && earned.All(r => model.AchievementIconRequestsOf(r.Id) == (noFolder ? 0 : 2)));

        // the game earns one more while the page is open: the poll finds it, the row is earned and its icon is made
        _ = sample.Add(14, later);
        sample.Write(paths.TrophiesFile);
        bool more = await model.PollAchievementsAsync();
        AchievementArt first = model.AchievementRows.Count > 14 ? model.AchievementRows[14].Art : AchievementArt.None;
        await model.AchievementPicturesSettledAsync();
        AchievementRowView? gold = model.AchievementRows.Count > 14 ? model.AchievementRows[14] : null;
        bool others = more && gold is { Id: 14, Earned: true } && !ReferenceEquals(gold, rows[14]) && Kept(except: 14);
        if (noFolder)
        {
            check($"an achievement earned while the page is open (14) shows the emblem of its grade at once, there being no game folder ({first}, then {gold?.Art}); the other rows keep their bitmaps",
                others && first == AchievementArt.Emblem && gold!.Art == AchievementArt.Emblem && IsCardEmblem(gold!) && model.AchievementIconRequests == 0);
        }
        else
        {
            // by now the icon may have arrived already; what is never seen meanwhile is the emblem
            string seen = first == AchievementArt.Icon ? "the icon at once" : "the " + first + " tile first";
            check($"an achievement earned while the page is open (14) has its icon made: asked for once, never the emblem meanwhile ({seen}, then {gold?.Art}); the other rows keep their bitmaps",
                others && (first is AchievementArt.Blank or AchievementArt.Icon) && model.AchievementIconRequestsOf(14) == 1 && model.AchievementIconRequests == 7
                && (gold!.Art == AchievementArt.Icon ? IsTile(gold.Picture) : gold.Art == AchievementArt.Emblem && IsCardEmblem(gold!) && !model.Folder.IsReady));
        }
    }

    /// <summary>
    /// v0.1.9, what a screen reader hears of the Achievements page, read from the automation peers of the page as built (Narrator reads the same): each of the
    /// three tabs by its label with its count, the earned share by the number the page shows (no name of its own hides it), each grade of the summary card by
    /// its name and count, and each row by its name, grade and state (never a class name).
    /// </summary>
    private static void ScreenReaderSelfTest(LauncherModel model, Shell shell, ListBox? tabs, Action<string, bool> check)
    {
        static string[] Heard(UIElement? list) => list is not null && UIElementAutomationPeer.CreatePeerForElement(list)?.GetChildren() is { } items ? [.. items.Select(p => p.GetName())] : [];
        string[] heard = Heard(tabs);
        check($"a screen reader hears the three tabs by their labels ({string.Join(", ", heard)})",
            heard.Length == 3 && heard.SequenceEqual(model.AchievementTabs.Select(t => t.Label)));
        TextBlock? share = Find<TextBlock>(shell, t => BindingOperations.GetBindingExpression(t, TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(LauncherModel.AchievementPercentText));
        string? said = share is null ? null : UIElementAutomationPeer.CreatePeerForElement(share)?.GetName();
        check($"a screen reader hears the earned share as the page shows it ({said})", said is { Length: > 0 } && said == model.AchievementPercentText && said == share!.Text);

        string[] grades = Heard(Find<ItemsControl>(shell, l => ReferenceEquals(l.ItemsSource, model.AchievementGrades)));
        check($"a screen reader hears each grade of the summary card by its name and count ({string.Join("; ", grades)})",
            grades.Length == 4 && grades.SequenceEqual(model.AchievementGrades.Select(g => $"{g.Label}, {g.Count}")));

        // the first row only in the line: with a game folder the chapters' names are the game's words
        string[] rows = Heard(Find<ItemsControl>(shell, l => ReferenceEquals(l.ItemsSource, model.AchievementRows)));
        check($"a screen reader hears each of the {rows.Length} rows by its name, grade and state (the first: {rows.FirstOrDefault()})",
            rows.Length == model.AchievementRows.Count && rows.Length > 0 && rows.SequenceEqual(model.AchievementRows.Select(r => $"{r.Name}, {r.GradeLabel}, {r.StateLabel}")));
    }

    /// <summary>
    /// True when the picture is the emblem of the grade as the port draws it (Achievements.EmblemIcon, premultiplied as every picture of the page): a frozen
    /// 240 px bitmap with exactly those pixels, so neither a tile nor a picture of anybody's file passes.
    /// </summary>
    private static bool IsEmblem(ImageSource? picture, TrophyGrade grade)
    {
        if (picture is not BitmapSource { PixelWidth: 240, PixelHeight: 240, IsFrozen: true } bitmap)
        {
            return false;
        }

        BitmapSource drawn = LauncherArt.IconBitmap(Achievements.EmblemIcon(grade));
        byte[] shown = new byte[240 * 240 * 4];
        byte[] expected = new byte[shown.Length];
        bitmap.CopyPixels(shown, 240 * 4, 0);
        drawn.CopyPixels(expected, 240 * 4, 0);
        return bitmap.Format == PixelFormats.Pbgra32 && shown.AsSpan().SequenceEqual(expected);
    }

    /// <summary>The first element below <paramref name="root"/> of that type that <paramref name="match"/> accepts (the visual tree, depth first), or null.</summary>
    private static T? Find<T>(DependencyObject root, Func<T, bool> match)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found))
            {
                return found;
            }

            if (Find(child, match) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Lays the shell out at a size, as a window of that size would.</summary>
    private static async Task LayoutAsync(FrameworkElement root, int width, int height)
    {
        // let the loaded events, the bindings and the layout passes that wait for idle run
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        await root.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task DrawAsync(FrameworkElement root, string path, int width = Width, int? pictureHeight = null)
    {
        int height = pictureHeight ?? Height;
        await LayoutAsync(root, width, height);
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * Scale), (int)Math.Round(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
