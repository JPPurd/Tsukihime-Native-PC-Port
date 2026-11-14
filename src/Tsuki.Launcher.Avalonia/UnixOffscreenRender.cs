using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tsuki.Launcher.Avalonia.Views;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// --render DIR: every page, then the Achievements page's tabs and its end, the question, a toast, the footer's gold button and the update overlay in its states,
/// drawn offscreen to PNG files with the names the Windows launcher's render check uses (play.png, update_offer.png …), so that the two can be laid side by side.
/// The Achievements page is drawn once the icons of the earned achievements are made. Nothing is asked of GitHub.
/// </summary>
internal static class UnixOffscreenRender
{
    /// <param name="model">The launcher's model.</param>
    /// <param name="folder">Where the PNG files go.</param>
    /// <param name="size">The window's size; null for the size the launcher opens with.</param>
    public static int Run(UnixLauncherModel model, string folder, (double Width, double Height)? size = null)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<UnixLauncherApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();
        return Pages(model, folder, size);
    }

    /// <summary>
    /// The pictures themselves, once Avalonia is set up (<see cref="Run"/> does that; a test's session has done it). A refresh one of whose steps failed
    /// ends the check with 1 and a line on the error stream before the first picture, as the error itself ended it before the refresh caught it. An error
    /// the launcher reported after that (the window coming to the front) ends it with 1 and such a line after the last picture (Program.ReportedFailure).
    /// </summary>
    internal static int Pages(UnixLauncherModel model, string folder, (double Width, double Height)? size)
    {
        var window = new MainWindow(model);
        if (size is { } wanted) { (window.Width, window.Height) = wanted; }
        Wait(model.RefreshAsync());
        if (Program.RefreshFailure(model) is { } failure)
        {
            Console.Error.WriteLine("RENDER FAIL: " + failure);
            return 1;
        }

        window.Show();
        foreach (LauncherPage page in Enum.GetValues<LauncherPage>())
        {
            model.Page = page;
            // the icons of the earned achievements are made once the page is shown: the pictures show them, not the blank tiles that stand in meanwhile
            if (page == LauncherPage.Achievements) { Wait(model.AchievementIconWork); }
            Draw(window, folder, page.ToString().ToLowerInvariant());
        }

        DrawAchievementsPage(window, model, folder);
        model.Page = LauncherPage.Saves;
        Task question = model.ImportAsync("savedata000");
        Draw(window, folder, "confirm");
        window.QuestionNo.Command?.Execute(null);
        Wait(question);
        model.Page = LauncherPage.Play;
        model.Notify("The game could not be started: example message for the render check.");
        Draw(window, folder, "toast");

        ReleaseVersion next = new(model.CurrentVersion.Major, model.CurrentVersion.Minor, model.CurrentVersion.Patch + 1);
        string asset = model.AssetName(next);
        var sample = new ReleaseInfo(
            next,
            next.Tag,
            $"Tsukihime PC port {next.Tag}",
            UpdateFeed.PlainText(
                "## Fixed\n\n- **Sample notes for the render check.** A release's notes appear here as plain text, scrollable when they are long.\n" +
                "- Headings, **bold** and `code` marks of GitHub's Markdown are taken out; [links](https://example.invalid) keep their words.\n\n" +
                "## How to update\n\nPress **Update now**: the launcher downloads the release and SHA256SUMS.txt, checks the checksum, replaces its files and restarts.\n\n" +
                "> Fan-made and unofficial. This contains no game data; you must own the game."),
            "https://github.com/" + UpdateSource.DefaultRepository + "/releases/tag/" + next.Tag,
            true,
            new ReleaseAsset(asset, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + asset), 110_268_124),
            new ReleaseAsset(UpdateSource.SumsAssetName, new Uri("https://github.com/" + UpdateSource.DefaultRepository + "/releases/download/" + next.Tag + "/" + UpdateSource.SumsAssetName), 98));
        model.Notify(string.Empty);
        model.PreviewUpdate(sample);
        Draw(window, folder, "update_footer");
        model.PreviewUpdateState(UnixUpdateStage.Offer, null, string.Empty);
        Draw(window, folder, "update_offer");
        model.PreviewUpdateState(UnixUpdateStage.Working, new UpdateProgress(UpdateStep.Downloading, new DownloadProgress(46_137_344, 110_268_124)), string.Empty);
        Draw(window, folder, "update_progress");
        model.PreviewUpdateState(UnixUpdateStage.Offer, null, LauncherTexts.UpdateGameRunning(PlatformServices.AppName));
        Draw(window, folder, "update_game_running");
        model.PreviewUpdateState(UnixUpdateStage.Failed, null, UnixUpdateChecks.Message(OperatingSystem.IsMacOS() ? "/Volumes/Games" : "/opt/Tsukihime", OperatingSystem.IsMacOS(), translocated: false, asset));
        Draw(window, folder, "update_not_writable");
        model.PreviewUpdateState(UnixUpdateStage.Failed, null, LauncherTexts.UpdateFailed($"the downloaded {asset} does not match {UpdateSource.SumsAssetName} (SHA-256 3f7a09c2d1e4...), so it was not installed"));
        Draw(window, folder, "update_failed");
        model.PreviewUpdateState(UnixUpdateStage.Hidden, null, string.Empty);
        window.Close();
        if (Program.ReportedFailure(model) is { } reported)
        {
            Console.Error.WriteLine("RENDER FAIL: " + reported);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// The Achievements page is longer than the window: its list, its two other tabs, and its end (the last rows; the switches are on the first picture, under
    /// the summary), as achievements_list.png, achievements_earned.png, achievements_locked.png and achievements_end.png in <paramref name="folder"/>.
    /// </summary>
    internal static void DrawAchievementsPage(MainWindow window, UnixLauncherModel model, string folder)
    {
        model.Page = LauncherPage.Achievements;
        Wait(model.AchievementIconWork);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        if (window.PageHost.Content is not AchievementsPage achievements) { return; }
        achievements.Scroll.Offset = new Vector(0, achievements.ListCard.Bounds.Top);
        Draw(window, folder, "achievements_list");
        model.AchievementTabIndex = 1;
        Draw(window, folder, "achievements_earned");
        model.AchievementTabIndex = 2;
        Draw(window, folder, "achievements_locked");
        model.AchievementTabIndex = 0;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        achievements.Scroll.ScrollToEnd();
        Draw(window, folder, "achievements_end");
    }

    /// <summary>How many times <see cref="Frame"/> lets the window's jobs run and forces a frame before it takes the picture (an animation never runs out of them).</summary>
    private const int SettleRounds = 10;

    /// <summary>How many pictures <see cref="Frame"/> takes at most, waiting for two in a row that are the same.</summary>
    private const int FrameTakes = 20;

    /// <summary>The pause between two of those pictures: time for a worker's result to reach the window's jobs.</summary>
    private static readonly TimeSpan FramePause = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The window as it is now, drawn. One CaptureRenderedFrame is not enough: Avalonia hands the compositor a change only once the batch before it was drawn,
    /// and the one frame it forces draws the batch already waiting. So after two changes with nothing drawn between them the picture is a step old. Whether
    /// something was drawn between them depended on the window's own render timer (1/60 s, run between the window's jobs once it is due): in the smallest
    /// window with nothing earned, achievements_end.png (the All tab chosen, then the page scrolled to its end) showed the All tab at the scroll the Earned tab
    /// had left in some runs and not in others. Here the jobs run and a frame is forced again for as long as a frame leaves jobs behind (the drawn batch's end,
    /// then the change it held back), and pictures are taken a moment apart until two in a row are the same; a window that keeps moving (an animation that
    /// never ends) gives its last one.
    /// </summary>
    internal static WriteableBitmap Frame(TopLevel window)
    {
        WriteableBitmap? last = null;
        byte[]? lastPixels = null;
        for (int take = 0; take < FrameTakes; take++)
        {
            if (take > 0) { Thread.Sleep(FramePause); }
            Settle(window);
            WriteableBitmap frame = window.GetLastRenderedFrame() ?? throw new InvalidOperationException("The headless renderer returned no frame.");
            byte[] pixels = Pixels(frame);
            bool same = lastPixels is not null && pixels.AsSpan().SequenceEqual(lastPixels);
            last?.Dispose();
            (last, lastPixels) = (frame, pixels);
            if (same) { break; }
        }

        return last!;
    }

    /// <summary>Runs the window's jobs, lays it out and forces a frame, again while that frame left jobs behind (at most <see cref="SettleRounds"/> times).</summary>
    private static void Settle(TopLevel window)
    {
        for (int round = 0; round < SettleRounds; round++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            // SystemIdle is the lowest priority RunJobs runs
            if (!Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.SystemIdle)) { return; }
        }
    }

    private static byte[] Pixels(WriteableBitmap frame)
    {
        using ILockedFramebuffer locked = frame.Lock();
        int row = ((locked.Size.Width * locked.Format.BitsPerPixel) + 7) / 8;
        byte[] pixels = new byte[row * locked.Size.Height];
        for (int y = 0; y < locked.Size.Height; y++) { Marshal.Copy(locked.Address + (y * locked.RowBytes), pixels, y * row, row); }
        return pixels;
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (!task.IsCompleted) { Thread.Sleep(5); }
        }

        task.GetAwaiter().GetResult();
    }

    private static void Draw(MainWindow window, string folder, string name)
    {
        string path = Path.Combine(folder, name + ".png");
        using WriteableBitmap frame = Frame(window);
        frame.Save(path);
        Console.WriteLine(path);
    }
}
