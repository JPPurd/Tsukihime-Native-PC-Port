using System.Diagnostics;
using System.Globalization;
using Tsuki.App.Services;
using Tsuki.Engine.Save;
using Tsuki.Launcher.Core;
using Tsuki.Tests.Synthetic.Save;

namespace Tsuki.Tests.Synthetic.Launcher;

/// <summary>Package K17: the saves page (summary, backup, restore, PS4 import) over a temporary %APPDATA%\Tsuki.</summary>
public sealed class SaveToolsTests : IDisposable
{
    private static readonly SlotDate T0 = new(2026, 10, 27, 9, 0, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-k17-saves-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public SaveToolsTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "Tsuki"));
        Directory.CreateDirectory(_paths.DataDirectory);
    }

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);                // a read-only profile file, and its copies in a backup
        }

        Directory.Delete(_root, recursive: true);
    }

    private static SaveStore Store(int manualSlots, short language = 1, bool fullscreen = false)
    {
        var store = new SaveStore();
        for (int i = 0; i < manualSlots; i++)
        {
            store.SaveManual(i, SaveStoreTests.Staging((ulong)(0x40 + i)), T0 with { Minute = i });
        }

        store.QuickSave(SaveStoreTests.Staging(0x70), T0 with { Minute = 30 });
        store.SystemData.Config.Language = language;
        store.SystemData.Config.Bytes[0xA9] = (byte)(fullscreen ? 1 : 0);
        return store;
    }

    private void WriteSave(int slots, short language = 1, bool fullscreen = false) =>
        SaveFile.WriteAtomic(_paths.SaveFile, Store(slots, language, fullscreen));

    [Fact]
    public void ASummaryCountsSlotsAndReadsTheConfig()
    {
        Assert.False(SaveTools.Summarize(_paths.SaveFile).Exists);
        Assert.Contains("No saves yet", SaveTools.Summarize(_paths.SaveFile).Describe(), StringComparison.Ordinal);

        WriteSave(3, language: 0, fullscreen: true);
        SaveSummary s = SaveTools.Summarize(_paths.SaveFile);
        Assert.True(s.Exists);
        Assert.Null(s.Error);
        Assert.Equal(4, s.Slots);                      // three manual and the quick slot
        Assert.Equal(3, s.ManualSlots);
        Assert.Equal(0, s.ConfigLanguage);
        Assert.Equal(1, s.WindowMode);
        Assert.NotNull(s.LastWrite);
        Assert.Equal("4 save slots", s.Describe());
    }

    [Fact]
    public void ADamagedFileIsReportedNotThrown()
    {
        File.WriteAllBytes(_paths.SaveFile, new byte[200]);
        SaveSummary s = SaveTools.Summarize(_paths.SaveFile);
        Assert.True(s.Exists);
        Assert.NotNull(s.Error);
        Assert.Contains("cannot be read", s.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABackupCopiesSaveAndSettingsIntoATimestampFolder()
    {
        WriteSave(2);
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        BackupInfo? backup = SaveTools.Backup(_paths, new DateTime(2026, 10, 27, 15, 30, 5));
        Assert.NotNull(backup);
        Assert.Equal("20261027-153005", backup.Name);
        Assert.Equal(Path.Combine(_paths.BackupsDirectory, "20261027-153005"), backup.Path);
        Assert.Equal(["save.dat", "settings.ini"], backup.Files);
        Assert.Equal(new DateTime(2026, 10, 27, 15, 30, 5), backup.Created);
        Assert.Equal(File.ReadAllBytes(_paths.SaveFile), File.ReadAllBytes(Path.Combine(backup.Path, "save.dat")));
        Assert.Equal("prompts=xbox\n", File.ReadAllText(Path.Combine(backup.Path, "settings.ini")));
        Assert.Equal(3, backup.Save.Slots);
        Assert.True(backup.Bytes > 0);
    }

    [Fact]
    public void ABackupInTheSameSecondGetsASuffixAndNothingIsOverwritten()
    {
        WriteSave(1);
        var t = new DateTime(2026, 10, 27, 15, 30, 5);
        BackupInfo a = SaveTools.Backup(_paths, t)!;
        BackupInfo b = SaveTools.Backup(_paths, t)!;
        Assert.NotEqual(a.Path, b.Path);
        Assert.EndsWith("-2", b.Name, StringComparison.Ordinal);
        Assert.Equal(2, SaveTools.List(_paths).Count);
    }

    [Fact]
    public void WithNothingToBackUpNothingIsMade()
    {
        Assert.Null(SaveTools.Backup(_paths, DateTime.Now));
        Assert.False(Directory.Exists(_paths.BackupsDirectory));
        Assert.Empty(SaveTools.List(_paths));
    }

    [Fact]
    public void BackupsAreListedNewestFirst()
    {
        WriteSave(1);
        SaveTools.Backup(_paths, new DateTime(2026, 10, 1, 8, 0, 0));
        SaveTools.Backup(_paths, new DateTime(2026, 10, 27, 8, 0, 0));
        SaveTools.Backup(_paths, new DateTime(2026, 10, 15, 8, 0, 0));
        Assert.Equal(["20261027-080000", "20261015-080000", "20261001-080000"], SaveTools.List(_paths).Select(b => b.Name));
    }

    [Fact]
    public void ABackupThatLosesFilesWhileItIsListedIsShownWithWhatIsLeftAndOneThatIsGoneIsLeftOut()
    {
        // a file manager, or the deletion of a whole profile, can take a backup's files between the listing of its folder and the reading of their sizes: the
        // listing shows what is still there and never throws (the Linux and macOS launcher lists the backups again when a game ends, in a method nobody awaits)
        WriteSave(1);
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        BackupInfo made = SaveTools.Backup(_paths, new DateTime(2026, 10, 27, 8, 0, 0))!;
        long settingsBytes = new FileInfo(Path.Combine(made.Path, "settings.ini")).Length;

        // save.dat goes after the folder was listed
        BackupInfo half = SaveTools.Describe(made.Path, folder =>
        {
            string[] listed = Directory.GetFiles(folder);
            File.Delete(Path.Combine(folder, "save.dat"));
            return listed;
        });
        Assert.Equal(["settings.ini"], half.Files);
        Assert.Equal(settingsBytes, half.Bytes);
        Assert.False(half.Save.Exists);
        Assert.Equal(["settings.ini"], Assert.Single(SaveTools.List(_paths)).Files);

        // the whole folder goes after the backups were listed: it lists no file, and the backups' list leaves it out
        string[] names = Directory.GetFiles(made.Path);
        Directory.Delete(made.Path, recursive: true);
        Assert.Empty(SaveTools.Describe(made.Path, _ => names).Files);
        Assert.Empty(SaveTools.Describe(made.Path, Directory.EnumerateFiles).Files);
        Assert.Empty(SaveTools.List(_paths));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABackupThatFailsHalfWayLeavesNoFolderBehind(bool readOnly)
    {
        // v0.1.9 FIXES D11 W-2: "Back up now" while another program holds trophies.ini without sharing. save.dat and settings.ini were copied before the copy
        // of the held file failed, and the folder with those two stayed in backups and was listed as a backup at the next refresh.
        // FIXES D13 S-3 (readOnly): the same in a profile whose save.dat and settings.ini are read-only. A copy takes the attribute along, and Windows deletes
        // neither a file that carries it nor the folder around it, so the folder with the two copies stayed after all
        WriteSave(1);
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        File.WriteAllText(_paths.TrophiesFile, "version=1\n");
        byte[] save = File.ReadAllBytes(_paths.SaveFile);
        var t = new DateTime(2026, 11, 13, 20, 0, 12);
        if (readOnly)
        {
            File.SetAttributes(_paths.SaveFile, FileAttributes.ReadOnly);
            File.SetAttributes(_paths.SettingsFile, FileAttributes.ReadOnly);
        }

        using (new FileStream(_paths.TrophiesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // the premise: this system refuses to copy a file another handle holds (Windows does, Linux does through the runtime's advisory lock; macOS
            // can clone a file without opening it, and then there is no failed backup to look at)
            string probe = Path.Combine(_root, "copy-of-the-held-file");
            try
            {
                File.Copy(_paths.TrophiesFile, probe);
            }
            catch (IOException)
            {
                // refused, as expected
            }

            if (File.Exists(probe))
            {
                Assert.Skip("this system copies a file that another handle holds without sharing");
            }

            _ = Assert.ThrowsAny<IOException>(() => SaveTools.Backup(_paths, t));        // the caller still hears why (the launchers say "Backup failed: ...")
            Assert.Empty(Directory.GetFileSystemEntries(_paths.BackupsDirectory));
            Assert.Empty(SaveTools.List(_paths));
        }

        // the profile is as it was, and the next backup of that second takes the plain name: nothing stayed in its way
        Assert.Equal(save, File.ReadAllBytes(_paths.SaveFile));
        Assert.Equal("prompts=xbox\n", File.ReadAllText(_paths.SettingsFile));
        Assert.Equal("version=1\n", File.ReadAllText(_paths.TrophiesFile));
        BackupInfo made = SaveTools.Backup(_paths, t)!;
        Assert.Equal("20261113-200012", made.Name);
        Assert.Equal(["save.dat", "settings.ini", "trophies.ini"], made.Files);
        Assert.Equal(made.Path, Assert.Single(SaveTools.List(_paths)).Path);

        // the clean-up cleared the attribute of the copies it deleted, nothing else: the profile's files and the copies of a backup that works keep it
        Assert.Equal(readOnly, File.GetAttributes(_paths.SaveFile).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(readOnly, File.GetAttributes(_paths.SettingsFile).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(readOnly, File.GetAttributes(Path.Combine(made.Path, "save.dat")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(readOnly, File.GetAttributes(Path.Combine(made.Path, "settings.ini")).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.GetAttributes(Path.Combine(made.Path, "trophies.ini")).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void TwoBackupsOfOneSecondStartedAtOnceLeaveAWholeBackup()
    {
        // v0.1.9 FIXES D13 S-3: two callers on one profile start a backup in the same second. Both find the name free and both make the folder; the one that
        // comes second to the first file fails on the other's copy. Its clean-up (D11 W-2) then removed the whole folder, with the backup the other was filling
        // or had filled, while that one was told "Backed up to ...". The clean-up now deletes only the files its own call copied (here none) and removes the
        // folder only when it is empty. Where the two do not meet (one sees the other's folder and takes the "-2" name) both are whole anyway
        WriteSave(2);
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        File.WriteAllText(_paths.TrophiesFile, "version=1\n");
        string[] names = ["save.dat", "settings.ini", "trophies.ini"];
        byte[][] profile = [.. names.Select(n => File.ReadAllBytes(Path.Combine(_paths.DataDirectory, n)))];
        bool Whole(string folder) => names.Zip(profile).All(f =>
            File.Exists(Path.Combine(folder, f.First)) && File.ReadAllBytes(Path.Combine(folder, f.First)).AsSpan().SequenceEqual(f.Second));

        for (int round = 0; round < 40; round++)
        {
            DateTime t = new DateTime(2026, 11, 14, 9, 0, 0).AddSeconds(round);
            var told = new BackupInfo?[2];
            var failed = new Exception?[2];
            int waiting = 0;
            bool go = false;
            Thread[] callers = [.. Enumerable.Range(0, 2).Select(k => new Thread(() =>
            {
                Interlocked.Increment(ref waiting);
                while (!Volatile.Read(ref go))
                {
                    Thread.SpinWait(1);                 // both leave this loop in the same instant (woken from a wait, they seldom met on Linux)
                }

                try
                {
                    told[k] = SaveTools.Backup(_paths, t);
                }
                catch (Exception e)
                {
                    failed[k] = e;
                }
            }))];
            Array.ForEach(callers, c => c.Start());
            SpinWait.SpinUntil(() => Volatile.Read(ref waiting) == 2);
            Volatile.Write(ref go, true);
            Array.ForEach(callers, c => c.Join());

            // a caller that failed says so the way the launchers report ("Backup failed: ..."), and at least one was told "Backed up to ..."
            Assert.All(failed.OfType<Exception>(), e => Assert.True(e is IOException or UnauthorizedAccessException, e.ToString()));
            BackupInfo[] done = [.. told.OfType<BackupInfo>()];
            Assert.NotEmpty(done);
            Assert.Equal(2, done.Length + failed.OfType<Exception>().Count());

            // every backup a caller was told about is whole, and nothing else of that second lies in the folder (no part of a backup, no empty folder)
            Assert.All(done, b => Assert.Equal(names, b.Files));
            Assert.All(done, b => Assert.True(Whole(b.Path), $"round {round}: {b.Name} is not the whole profile"));
            string[] folders = Directory.GetDirectories(_paths.BackupsDirectory, t.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "*");
            Assert.Equal(done.Select(b => b.Path).Order(StringComparer.Ordinal), folders.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ABackupsFolderThatCannotBeListedShowsNoBackups()
    {
        // v0.1.9 FIXES D11 W-2: the backups folder is there and its listing throws. On Windows a junction whose target is gone (as a link to a drive that is
        // unplugged), elsewhere a folder without any permission. The launchers list the backups in the middle of their refresh; an exception there left the
        // rest of the refresh undone
        WriteSave(1);
        BackupInfo made = SaveTools.Backup(_paths, new DateTime(2026, 11, 13, 20, 0, 0))!;
        string kept = _paths.BackupsDirectory + "-kept";
        if (OperatingSystem.IsWindows())
        {
            Directory.Move(_paths.BackupsDirectory, kept);
            string target = Path.Combine(_root, "target-that-goes");
            Directory.CreateDirectory(target);
            using (Process link = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{_paths.BackupsDirectory}\" \"{target}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                _ = link.StandardOutput.ReadToEnd();
                link.WaitForExit();
                if (link.ExitCode != 0 || !Directory.Exists(_paths.BackupsDirectory))
                {
                    Assert.Skip("a junction could not be made here");
                }
            }

            try
            {
                Directory.Delete(target);
                Assert.True(Directory.Exists(_paths.BackupsDirectory));                 // the link is still there
                _ = Assert.ThrowsAny<IOException>(() => Directory.GetDirectories(_paths.BackupsDirectory));
                Assert.Empty(SaveTools.List(_paths));
            }
            finally
            {
                Directory.Delete(_paths.BackupsDirectory);                              // the link itself
                Directory.Move(kept, _paths.BackupsDirectory);
            }
        }
        else
        {
            UnixFileMode mode = File.GetUnixFileMode(_paths.BackupsDirectory);
            try
            {
                File.SetUnixFileMode(_paths.BackupsDirectory, UnixFileMode.None);
                try
                {
                    _ = Directory.GetDirectories(_paths.BackupsDirectory);
                    Assert.Skip("this user lists a folder without any permission (root)");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the premise holds
                }

                Assert.Empty(SaveTools.List(_paths));
            }
            finally
            {
                File.SetUnixFileMode(_paths.BackupsDirectory, mode);
            }
        }

        // readable again, the backup is listed as before
        Assert.Equal(made.Path, Assert.Single(SaveTools.List(_paths)).Path);
    }

    [Fact]
    public void ABackupWhoseFolderCannotBeListedIsLeftOutOfTheList()
    {
        // v0.1.9 FIXES D11 W-2: one backup's folder cannot be listed (access denied, a device that is not ready): it lists no file, as one that is gone does, and
        // the backups' list leaves it out instead of throwing
        WriteSave(1);
        BackupInfo made = SaveTools.Backup(_paths, new DateTime(2026, 11, 13, 20, 0, 0))!;
        BackupInfo other = SaveTools.Backup(_paths, new DateTime(2026, 11, 13, 21, 0, 0))!;
        Assert.Empty(SaveTools.Describe(made.Path, _ => throw new UnauthorizedAccessException("Access to the path is denied.")).Files);
        Assert.Empty(SaveTools.Describe(made.Path, _ => throw new IOException("The device is not ready.")).Files);
        BackupInfo unlisted = SaveTools.Describe(made.Path, _ => throw new UnauthorizedAccessException());
        Assert.Equal(made.Name, unlisted.Name);
        Assert.Equal(0, unlisted.Bytes);

        if (!OperatingSystem.IsWindows())
        {
            // the real thing where a test can make it: the first backup's folder without any permission
            UnixFileMode mode = File.GetUnixFileMode(made.Path);
            try
            {
                File.SetUnixFileMode(made.Path, UnixFileMode.None);
                bool denied;
                try
                {
                    _ = Directory.GetFiles(made.Path);
                    denied = false;                                                     // root lists it anyway
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    denied = true;
                }

                string[] listed = denied ? [other.Path] : [other.Path, made.Path];
                Assert.Equal(listed, SaveTools.List(_paths).Select(b => b.Path));
            }
            finally
            {
                File.SetUnixFileMode(made.Path, mode);
            }
        }

        Assert.Equal([other.Path, made.Path], SaveTools.List(_paths).Select(b => b.Path));
    }

    [Fact]
    public void RestoringPutsTheOldSaveBackAndBacksUpTheCurrentOneFirst()
    {
        WriteSave(2, language: 0);
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 10, 1, 8, 0, 0))!;
        WriteSave(7, language: 3);                              // later play
        byte[] current = File.ReadAllBytes(_paths.SaveFile);

        BackupInfo? undo = SaveTools.Restore(_paths, old, new DateTime(2026, 10, 27, 9, 0, 0));

        Assert.NotNull(undo);                                   // the replaced save is in a backup of its own
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(undo.Path, "save.dat")));
        SaveSummary now = SaveTools.Summarize(_paths.SaveFile);
        Assert.Equal(3, now.Slots);
        Assert.Equal(0, now.ConfigLanguage);
        Assert.Equal(File.ReadAllBytes(Path.Combine(old.Path, "save.dat")), File.ReadAllBytes(_paths.SaveFile));
        Assert.False(File.Exists(_paths.SaveFile + ".restore.tmp"));
        Assert.Equal(2, SaveTools.List(_paths).Count);

        // and a restore can be undone
        SaveTools.Restore(_paths, undo, new DateTime(2026, 10, 27, 9, 5, 0));
        Assert.Equal(current, File.ReadAllBytes(_paths.SaveFile));
    }

    [Fact]
    public void RestoringKeepsTheSettingsAsTheyAre()
    {
        WriteSave(1);
        File.WriteAllText(_paths.SettingsFile, "prompts=keyboard\n");
        BackupInfo old = SaveTools.Backup(_paths, new DateTime(2026, 10, 1, 8, 0, 0))!;
        File.WriteAllText(_paths.SettingsFile, "prompts=xbox\n");
        SaveTools.Restore(_paths, old, DateTime.Now);
        Assert.Equal("prompts=xbox\n", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void ARestoreFromABackupWithoutASaveFails()
    {
        WriteSave(1);
        string folder = Path.Combine(_paths.BackupsDirectory, "20260101-000000");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.ini"), "x");
        BackupInfo hollow = SaveTools.List(_paths).Single();
        Assert.Throws<FileNotFoundException>(() => SaveTools.Restore(_paths, hollow, DateTime.Now));
        Assert.Single(SaveTools.List(_paths));                  // nothing was backed up or replaced on the way
    }

    // -- PS4 images ------------------------------------------------------------------------------------------------------

    private string WriteImage(string folder, string name, SaveStore store)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, SaveFile.ExportPs4Image(store));
        return path;
    }

    [Fact]
    public void APs4ImageReplacesTheSaveAfterABackupAndIsOnlyRead()
    {
        WriteSave(1, language: 1);
        string image = WriteImage(Path.Combine(_root, "shad"), "savedata000", Store(5, language: 3));
        File.SetAttributes(image, FileAttributes.ReadOnly);       // a write would throw
        byte[] imageBefore = File.ReadAllBytes(image);
        DateTime stamp = File.GetLastWriteTimeUtc(image);
        byte[] saveBefore = File.ReadAllBytes(_paths.SaveFile);

        (BackupInfo? backup, int slots) = SaveTools.ImportPs4(_paths, image, new DateTime(2026, 10, 27, 10, 0, 0));

        Assert.NotNull(backup);
        Assert.Equal(saveBefore, File.ReadAllBytes(Path.Combine(backup.Path, "save.dat")));
        Assert.Equal(6, slots);
        SaveSummary now = SaveTools.Summarize(_paths.SaveFile);
        Assert.Equal(6, now.Slots);
        Assert.Equal(3, now.ConfigLanguage);
        Assert.Equal(imageBefore, File.ReadAllBytes(image));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(image));
        File.SetAttributes(image, FileAttributes.Normal);
    }

    [Fact]
    public void ANotAnImageFileIsRefusedAndTheSaveStaysUntouched()
    {
        WriteSave(2);
        byte[] before = File.ReadAllBytes(_paths.SaveFile);
        string junk = Path.Combine(_root, "junk.bin");
        File.WriteAllBytes(junk, new byte[1000]);
        Assert.Throws<SaveFormatException>(() => SaveTools.ImportPs4(_paths, junk, DateTime.Now));
        string empty = Path.Combine(_root, "empty.bin");
        File.WriteAllBytes(empty, new byte[SaveFile.Ps4ImageSize]);                // right size, no system block
        Assert.Throws<SaveFormatException>(() => SaveTools.ImportPs4(_paths, empty, DateTime.Now));
        Assert.Equal(before, File.ReadAllBytes(_paths.SaveFile));
        Assert.Empty(SaveTools.List(_paths));
    }

    [Fact]
    public void ShadPs4sImageIsFoundByItsSizeUnderTheTitleFolder()
    {
        string appData = Path.Combine(_root, "Roaming");
        Assert.Null(SaveTools.FindShadPs4Image(appData));
        string folder = Path.Combine(appData, "shadPS4", "home", "1000", "savedata", "CUSA47999", "savedata000");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "other.bin"), new byte[10]);
        Assert.Null(SaveTools.FindShadPs4Image(appData));
        string image = WriteImage(folder, "sdimg_data", Store(1));
        Assert.Equal(image, SaveTools.FindShadPs4Image(appData));
        Assert.Null(SaveTools.FindShadPs4Image(appData, "CUSA00000"));
    }

    // -- the game folder file and the logs ---------------------------------------------------------------------------------

    [Fact]
    public void TheGameFolderIsRememberedInTheFileTheGameReads()
    {
        Assert.Null(_paths.ReadGameDirectory());
        Assert.True(_paths.WriteGameDirectory(@"D:\games\CUSA47999"));
        Assert.Equal(@"D:\games\CUSA47999", File.ReadAllText(Path.Combine(_paths.DataDirectory, "game_dir.txt")));
        Assert.Equal(@"D:\games\CUSA47999", _paths.ReadGameDirectory());
        File.WriteAllText(_paths.GameDirFile, "  \n");
        Assert.Null(_paths.ReadGameDirectory());
    }

    [Fact]
    public void TheLogToShowIsTheLastRunsThenTheCrashLog()
    {
        Assert.Null(_paths.LogToShow());
        File.WriteAllText(_paths.CrashLog, "x");
        Assert.Equal(_paths.CrashLog, _paths.LogToShow());
        File.WriteAllText(_paths.LastRunLog, "y");
        Assert.Equal(_paths.LastRunLog, _paths.LogToShow());
    }

    [Fact]
    public void SettingsWrittenByTheLauncherLandBesideTheSaveFile()
    {
        Assert.Equal(Path.Combine(_paths.DataDirectory, AppSettings.FileName), _paths.SettingsFile);
        Assert.Equal(Path.Combine(_paths.DataDirectory, "save.dat"), _paths.SaveFile);
    }
}
