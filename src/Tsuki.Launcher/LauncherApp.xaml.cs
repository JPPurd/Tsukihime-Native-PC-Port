// TsukihimeLauncher.exe entry (package K17; package K20 renamed the program from TsukiLauncher.exe).
//
//   TsukihimeLauncher.exe [--game DIR] [--data DIR]       the launcher window (--game replaces the remembered folder, --data the profile folder %APPDATA%\Tsuki)
//   TsukihimeLauncher.exe --selftest FILE --data DIR [--game DIR]
//                                                    drives the view model like the pages do over a temporary profile and writes the checks to FILE (verification mode)
//   TsukihimeLauncher.exe --render OUTDIR [--game DIR] [--data DIR] [--page NAME] [--height PIXELS] [--scale PERCENT]
//                                                    draws every page offscreen to PNG files and exits; no window is ever shown (verification mode).
//                                                    --scale 150 draws them as on a screen at 150 % (100 to 300)
//   TsukihimeLauncher.exe --update-selftest FILE --data DIR --update-api http://127.0.0.1:PORT/ [--update-current X.Y.Z]
//                                                    package K40: checks a loopback stand-in of GitHub, installs the update it offers over the launcher's own folder
//                                                    and restarts into the new launcher, which runs --selftest FILE.after.txt (verification mode, no window)
//   --after-update PID --updated-from X.Y.Z          given by an update to the launcher it restarts into: wait for the old one to end, delete its .old files
//   --update-api URL [--update-current X.Y.Z]        a loopback address only: the check asks that server instead of api.github.com (tests)
//   TsukihimeLauncher.exe --stamp-selftest FILE --data DIR [--game DIR] [--stamp-off]
//                                                    package K43: builds the Arcueid icon from the game folder into the profile DIR, writes it (or, with --stamp-off,
//                                                    the port's crescent) into Tsukihime.exe and TsukihimeLauncher.exe of the launcher's own folder exactly as the
//                                                    window does, and writes ok / FAIL lines to FILE (verification mode, no window; run it on a COPY of a release)
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Tsuki.Launcher.Core;

namespace Tsuki.Launcher;

/// <summary>The WPF application.</summary>
public partial class LauncherApp : Application
{
    private const string Usage = "usage: TsukihimeLauncher [--game DIR] [--data DIR] [--render OUTDIR [--page NAME] [--height PIXELS] [--scale PERCENT]] [--selftest FILE] [--update-selftest FILE --update-api URL [--update-current X.Y.Z]] [--stamp-selftest FILE --data DIR [--stamp-off]]";

    /// <summary>The arguments an update does not pass on to the launcher it restarts into (each takes one value).</summary>
    private static readonly string[] UpdateOnlyArguments = ["--after-update", "--updated-from", "--update-current", "--update-selftest", "--selftest", "--render", "--page", "--height", "--scale"];

    /// <summary>The verification modes. Nobody watches them, so they never open a box: it would wait for a click on the screen of whoever started them (package R1).</summary>
    private static readonly string[] VerificationModes = ["--render", "--selftest", "--update-selftest", "--stamp-selftest"];

    private LauncherPaths _paths = LauncherPaths.ForUser();

    /// <summary>True when the command line names a verification mode, also one given without its value. Read before the arguments are parsed.</summary>
    private bool _unattended;

    /// <summary>The profile --data named, once it is read: the only place a verification mode writes its crash log (never the player's own profile).</summary>
    private LauncherPaths? _dataProfile;

    /// <summary>A verification mode met an exception nobody caught: its run does not end with 0.</summary>
    private bool _crashed;
    private Mutex? _instance;
    private string[] _args = [];
    private LauncherModel? _windowModel;

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _unattended = e.Args.Any(a => VerificationModes.Contains(a, StringComparer.Ordinal));
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception);
            args.Handled = true;
        };

        string? game = null;
        string? data = null;
        string? render = null;
        string? only = null;
        string? selfTest = null;
        string? updateSelfTest = null;
        string? updateApi = null;
        string? updateCurrent = null;
        int afterUpdate = 0;
        string? updatedFrom = null;
        string? stampSelfTest = null;
        bool stampOff = false;
        string? bad = null;
        _args = e.Args;
        for (int i = 0; i < e.Args.Length && bad is null; i++)
        {
            switch (e.Args[i])
            {
                case "--update-selftest" when i + 1 < e.Args.Length:
                    updateSelfTest = e.Args[++i];
                    break;
                case "--update-api" when i + 1 < e.Args.Length:
                    updateApi = e.Args[++i];
                    break;
                case "--update-current" when i + 1 < e.Args.Length:
                    updateCurrent = e.Args[++i];
                    break;
                case "--after-update" when i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid):
                    afterUpdate = pid;
                    i++;
                    break;
                case "--updated-from" when i + 1 < e.Args.Length:
                    updatedFrom = e.Args[++i];
                    break;
                case "--stamp-selftest" when i + 1 < e.Args.Length:
                    stampSelfTest = e.Args[++i];
                    break;
                case "--stamp-off":
                    stampOff = true;
                    break;
                case "--game" when i + 1 < e.Args.Length:
                    game = e.Args[++i];
                    break;
                case "--data" when i + 1 < e.Args.Length:
                    data = e.Args[++i];
                    break;
                case "--render" when i + 1 < e.Args.Length:
                    render = e.Args[++i];
                    break;
                case "--page" when i + 1 < e.Args.Length:
                    only = e.Args[++i];
                    break;
                case "--selftest" when i + 1 < e.Args.Length:
                    selfTest = e.Args[++i];
                    break;
                case "--height" when i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int height) && height is >= 400 and <= 4000:
                    OffscreenRender.Height = height;
                    i++;
                    break;
                case "--scale" when i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && percent is >= 100 and <= 300:
                    OffscreenRender.Scale = percent / 100.0;
                    i++;
                    break;
                default:
                    bad = e.Args[i];
                    break;
            }
        }

        // package K40: an update source other than GitHub is only ever the loopback address (a test's stand-in), and only then may the current version be pretended
        UpdateSource? source = null;
        ReleaseVersion pretend = default;
        if (bad is null && updateApi is not null)
        {
            source = Uri.TryCreate(updateApi, UriKind.Absolute, out Uri? api) && HostPolicy.Loopback.Allows(api) ? UpdateSource.ForLoopback(api) : null;
            bad = source is null ? "--update-api takes a loopback address (http://127.0.0.1:PORT/) only" : null;
        }

        if (bad is null && updateCurrent is not null && (source is null || !ReleaseVersion.TryParse(updateCurrent, out pretend)))
        {
            bad = "--update-current takes X.Y.Z and needs --update-api";
        }

        if (bad is null && updateSelfTest is not null && source is null)
        {
            bad = "--update-selftest needs --update-api (the self-test never talks to GitHub)";
        }

        if (bad is null && updatedFrom is not null && !ReleaseVersion.TryParse(updatedFrom, out _))
        {
            bad = "--updated-from takes X.Y.Z";
        }

        // package K43: the stamp self-test changes the programs of the launcher's folder, so it only ever runs on a profile of its own (never the player's)
        if (bad is null && stampSelfTest is not null && data is null)
        {
            bad = "--stamp-selftest needs --data DIR (a temporary profile)";
        }

        if (bad is null && stampOff && stampSelfTest is null)
        {
            bad = "--stamp-off belongs to --stamp-selftest";
        }

        if (bad is not null)
        {
            // the verification modes run with nobody watching: a modal box there would hang the harness that started them (package R1)
            string message = $"{Usage}{Environment.NewLine}unknown or incomplete argument: {bad}";
            if (_unattended)
            {
                Console.Error.WriteLine(message);
                WriteUsageError(render, selfTest ?? updateSelfTest ?? stampSelfTest, message);
            }
            else
            {
                Box(message, MessageBoxImage.None);
            }

            Shutdown(64);
            return;
        }

        LauncherPaths paths = data is null ? LauncherPaths.ForUser() : new LauncherPaths(Path.GetFullPath(data));
        _paths = paths;
        _dataProfile = data is null ? null : paths;
        var model = new LauncherModel(paths, AppContext.BaseDirectory, game);
        if (source is not null)
        {
            model.UpdateSource = source;
            model.CurrentVersionOverride = updateCurrent is null ? null : pretend;
        }

        // package K40: the launcher an update restarted into waits for the old one to end, then deletes the .old files the update left (also at every other start,
        // when an earlier cleanup found a file still in use). The window waits for its single-instance mutex first, so that a second start never cleans up under
        // a launcher that is downloading an update.
        int left = selfTest is not null || updateSelfTest is not null || render is not null ? AfterUpdate(afterUpdate, model) : 0;
        ReleaseVersion? from = updatedFrom is not null && ReleaseVersion.TryParse(updatedFrom, out ReleaseVersion f) ? f : null;
        if (selfTest is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                string report;
                try
                {
                    report = await OffscreenRender.SelfTestAsync(model, paths, game, from is { } v ? (v, left) : null);
                }
                catch (Exception ex)
                {
                    // v0.1.9 FIXES D11 W-1: an exception stayed in this lambda's task, so no report was written, Shutdown was never called and whoever started the
                    // self-test waited for ever (a trophies.ini that is a folder, a game folder that cannot be listed)
                    report = "FAIL  the self-test stopped: " + ex + Environment.NewLine;
                }

                EndSelfTest(selfTest, report, !report.Contains("FAIL", StringComparison.Ordinal));
            });
            return;
        }

        if (stampSelfTest is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string reportPath = Path.GetFullPath(stampSelfTest);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                string report;
                try
                {
                    report = await IconStampSelfTest.RunAsync(model, paths, arcueid: !stampOff, reportPath);
                }
                catch (Exception ex)
                {
                    // a WinExe has no console: the reason goes into the report
                    report = "FAIL  the stamp self-test stopped: " + ex + Environment.NewLine;
                }

                EndSelfTest(reportPath, report, !report.Contains("FAIL", StringComparison.Ordinal));
            });
            return;
        }

        if (updateSelfTest is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string reportPath = Path.GetFullPath(updateSelfTest);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                string? restartInto = null;
                ReleaseVersion updatedFromVersion = default;
                model.RestartRequested = (exe, old) =>
                {
                    restartInto = exe;
                    updatedFromVersion = old;
                };
                string report;
                try
                {
                    report = await OffscreenRender.UpdateSelfTestAsync(model);
                }
                catch (Exception ex)
                {
                    // v0.1.9 FIXES D11 W-1: as in --selftest, an exception here left the launcher running without a report
                    report = "FAIL  the self-test stopped: " + ex + Environment.NewLine;
                }

                bool ok = !report.Contains("FAIL", StringComparison.Ordinal) && restartInto is not null;
                if (ok)
                {
                    // the new launcher proves it runs: its own self-test on the same profile, after it has cleaned up behind this one
                    var info = new ProcessStartInfo(restartInto!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restartInto)! };
                    foreach (string a in new[] { "--selftest", reportPath + ".after.txt", "--data", paths.DataDirectory, "--after-update", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--updated-from", updatedFromVersion.ToString() })
                    {
                        info.ArgumentList.Add(a);
                    }

                    try
                    {
                        using Process? started = Process.Start(info);
                        report += $"ok    the new launcher was started (process {started?.Id.ToString(CultureInfo.InvariantCulture) ?? "?"}): {restartInto}{Environment.NewLine}";
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        report += $"FAIL  the new launcher could not be started: {ex.Message}{Environment.NewLine}";
                        ok = false;
                    }
                }

                EndSelfTest(reportPath, report, ok);
            });
            return;
        }

        if (render is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                int code = 0;
                try
                {
                    await OffscreenRender.RunAsync(model, Path.GetFullPath(render), only);
                }
                catch (Exception ex)
                {
                    // a WinExe has no console: the reason goes beside the pictures
                    Directory.CreateDirectory(render);
                    File.WriteAllText(Path.Combine(render, "render_error.txt"), ex.ToString());
                    code = 1;
                }

                Shutdown(code);
            });
            return;
        }

        // one launcher window per user: a second start brings the first one forward and leaves (package R1)
        _instance = new Mutex(true, @"Local\TsukihimeLauncher", out bool first);
        if (!first)
        {
            _instance.Dispose();
            _instance = null;
            if (!ActivateOther())
            {
                Box("The Tsukihime launcher is already open.", MessageBoxImage.None);
            }

            Shutdown(0);
            return;
        }

        left = AfterUpdate(afterUpdate, model);
        var window = new MainWindow(model);
        MainWindow = window;
        model.RestartRequested = RestartInto;
        // package K43: the window writes Arcueid (or the crescent, Help & about) into the two programs of its folder once the game folder is read, off its thread.
        // Only in a release folder (a self-contained publish, as every release zip is): a build output of the source would keep a stamped program over later
        // builds, which copy the apphost only when it is newer (review of K43)
        _windowModel = model;
        if (ExeIconStamp.IsSelfContained(AppContext.BaseDirectory))
        {
            model.StampProgramIcons = true;
        }
        else
        {
            model.Log.Write("program icons: this folder is a build of the source, not a published release (no hostfxr.dll beside the launcher); its programs are left as they are");
        }

        window.Show();
        _ = model.RefreshAsync();
        if (from is { } updated)
        {
            model.SayUpdated(updated);
        }

        model.CheckOnStart();
        if (left > 0)
        {
            // an .old file was still in use (the old launcher took long to end): one more try a little later, off the window's thread
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                int still = UpdateInstaller.Cleanup(AppContext.BaseDirectory);
                model.Log.Write($"update cleanup again: {still.ToString(CultureInfo.InvariantCulture)} file(s) left for the next start");
                if (still == 0 && model.StampProgramIcons)
                {
                    // package K43, review: the first stamp of this start left the folder alone while update-staging was there; the programs the update brought
                    // with the crescent get the icon the switch asks for now, not only at the next start
                    _ = Dispatcher.InvokeAsync(() => model.StampIconsAsync("the last update's clean-up finished"));
                }
            });
        }
    }

    /// <summary>Waits for the launcher an update replaced (up to 30 s), then removes what the update left. Returns how many .old files are still there.</summary>
    private static int AfterUpdate(int pid, LauncherModel model)
    {
        if (pid > 0)
        {
            try
            {
                using Process old = Process.GetProcessById(pid);
                if (!old.WaitForExit(30_000))
                {
                    model.Log.Write($"update: the old launcher (process {pid.ToString(CultureInfo.InvariantCulture)}) has not ended after 30 s");
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // it has ended already
            }
        }

        if (!Directory.Exists(new UpdateStaging(AppContext.BaseDirectory).Root))
        {
            return 0;
        }

        int left = UpdateInstaller.Cleanup(AppContext.BaseDirectory);
        for (int attempt = 0; left > 0 && pid > 0 && attempt < 10; attempt++)
        {
            Thread.Sleep(300);
            left = UpdateInstaller.Cleanup(AppContext.BaseDirectory);
        }

        model.Log.Write(left == 0 ? "update cleanup: the .old files and the staging folder are gone" : $"update cleanup: {left.ToString(CultureInfo.InvariantCulture)} file(s) still in use, removed at the next start");
        return left;
    }

    /// <summary>Starts the new launcher an update installed, with this launcher's arguments, and ends this one (package K40).</summary>
    private void RestartInto(string exe, ReleaseVersion from)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        for (int i = 0; i < _args.Length; i++)
        {
            if (UpdateOnlyArguments.Contains(_args[i], StringComparer.Ordinal))
            {
                i++;                                    // the option and its value
                continue;
            }

            info.ArgumentList.Add(_args[i]);
        }

        info.ArgumentList.Add("--after-update");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--updated-from");
        info.ArgumentList.Add(from.ToString());
        _instance?.Dispose();                           // the new launcher takes the single-instance mutex
        _instance = null;
        try
        {
            using Process? started = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Box($"The update is installed, but the new launcher could not be started ({e.Message}). Start TsukihimeLauncher.exe again.", MessageBoxImage.None);
        }

        Shutdown(0);
    }

    /// <summary>
    /// The end of every self-test (v0.1.9 FIXES D11 W-1): writes the report to <paramref name="file"/> and ends the run, with 0 only when <paramref name="ok"/> holds and
    /// the report was written. A report that cannot be written (its folder is not there, the file is held) is said on the error stream and the run ends with 1:
    /// thrown from here it would stay in the task of the lambda that ran the self-test, and the launcher would stay without a report for whoever waits for it.
    /// </summary>
    private void EndSelfTest(string file, string report, bool ok)
    {
        try
        {
            WriteReport(Path.GetFullPath(file), report);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"the self-test's report could not be written to {file}: {e.Message}");
            ok = false;
        }

        Shutdown(ok ? 0 : 1);
    }

    /// <summary>Writes a report through a temporary file, so that a reader never sees half of it.</summary>
    private static void WriteReport(string path, string report)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, report);
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        if (_windowModel is { StampProgramIcons: true } model)
        {
            // package K43, review: a stamp of the program icons that runs now ends before the process does (its worker is a background thread, which the exit
            // would cut anywhere, also between the launcher's two renames); then a program a failed stamp left missing is put back. Before the single-instance
            // mutex goes, so that a launcher started meanwhile never stamps beside this one.
            try
            {
                _ = model.ProgramIconsWork.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // the stamp's own failures are in launcher.log; the programs are the files they were
            }

            try
            {
                _ = ExeIconStamp.CleanLeftovers(AppContext.BaseDirectory, deleteCopies: false);
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException)
            {
                // the next stamp tries again
            }
        }

        if (_crashed && e.ApplicationExitCode == 0)
        {
            e.ApplicationExitCode = 70;                 // EX_SOFTWARE: the checks may all have passed, but something nobody caught went wrong
        }

        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void WriteUsageError(string? render, string? selfTest, string message)
    {
        try
        {
            if (render is not null)
            {
                Directory.CreateDirectory(render);
                File.WriteAllText(Path.Combine(render, "render_error.txt"), message);
            }

            if (selfTest is not null)
            {
                File.WriteAllText(Path.GetFullPath(selfTest), "FAIL  " + message + Environment.NewLine);
            }
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // the console line is all that can be said
        }
    }

    /// <summary>Brings the window of the launcher that is already running to the front; false when none could be found.</summary>
    private static bool ActivateOther()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();
            foreach (Process p in Process.GetProcessesByName(self.ProcessName))
            {
                using (p)
                {
                    if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    _ = NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SwRestore);
                    return NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // the other launcher is leaving or cannot be reached
        }

        return false;
    }

    /// <summary>An exception nobody caught: into launcher_crash.log, then a box. In a verification mode no box: the error stream instead, the log only in the
    /// profile --data named, and the run does not end with 0.</summary>
    private void Report(Exception e)
    {
        LauncherPaths? paths = _unattended ? _dataProfile : _paths;
        string? log = null;
        if (paths is not null)
        {
            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                log = Path.Combine(paths.DataDirectory, "launcher_crash.log");
                File.AppendAllText(log, $"{DateTime.Now:s}  {e}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException)
            {
                log = null;
            }
        }

        if (_unattended)
        {
            _crashed = true;
            Console.Error.WriteLine($"unhandled exception: {e}");
            return;
        }

        Box(e.Message + (log is null ? string.Empty : "\n\nDetails: " + log), MessageBoxImage.Warning);
    }

    /// <summary>The launcher's one way to open a box. Never in a verification mode.</summary>
    private void Box(string text, MessageBoxImage image)
    {
        if (_unattended)
        {
            Console.Error.WriteLine(text);
            return;
        }

        MessageBox.Show(text, "Tsukihime Launcher", MessageBoxButton.OK, image);
    }

    private static class NativeMethods
    {
        public const int SwRestore = 9;

        // DllImport, not LibraryImport: the launcher compiles without unsafe code
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
