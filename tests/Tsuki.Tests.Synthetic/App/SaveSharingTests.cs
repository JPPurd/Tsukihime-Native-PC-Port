using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// v0.1.9: the save file is read with a handle that shares reading, writing and deleting (<see cref="SaveFile.OpenForReading"/>). The launcher's pages read
/// save.dat while the game runs (the Achievements page every two seconds); a reader that opened it with <c>File.OpenRead</c> at the moment of the game's commit
/// made the commit's <c>File.Replace</c> fail on Windows, and a read at the moment of a write failed the other way round.
/// </summary>
public sealed class SaveSharingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-v019-share-" + Guid.NewGuid().ToString("N"));

    public SaveSharingTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SavePath => Path.Combine(_dir, "save.dat");

    private static SaveStore StoreWith(ushort mark)
    {
        SaveStore store = AppRig.NewStore();
        store.SystemData.Block.SetG(10, mark);
        return store;
    }

    [Fact]
    public void AReaderThatHoldsTheSaveFileOpenDoesNotStopTheGamesCommit()
    {
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        byte[] first = File.ReadAllBytes(SavePath);

        using (FileStream reader = SaveFile.OpenForReading(SavePath))
        {
            // the game commits while the reader has the file open: the replace goes through, the new file is there and the old one is the backup
            SaveFile.WriteAtomic(SavePath, StoreWith(2));
            Assert.Equal(2, SaveFile.ReadWithFallback(SavePath, out bool backup)!.SystemData.Block.GetG(10));
            Assert.False(backup);

            // and the reader still reads the file it opened, whole
            byte[] read = new byte[first.Length];
            reader.ReadExactly(read);
            Assert.Equal(first, read);
            Assert.Equal(1, SaveFile.FromBytes(read).SystemData.Block.GetG(10));
        }

        // (the reader has let go: the commit after that one finds the backup free)
        SaveFile.WriteAtomic(SavePath, StoreWith(3));
        Assert.Equal(3, SaveFile.FromBytes(File.ReadAllBytes(SavePath)).SystemData.Block.GetG(10));
        Assert.Equal(2, SaveFile.FromBytes(File.ReadAllBytes(SavePath + ".bak")).SystemData.Block.GetG(10));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // through the services: the commit of a running game says it was written
        var rig = new AppRig(savePath: SavePath, store: StoreWith(4));
        var log = new List<string>();
        rig.Services.Logged += log.Add;
        using (SaveFile.OpenForReading(SavePath))
        {
            Assert.True(rig.Services.Commit());
        }

        Assert.DoesNotContain(log, l => l.StartsWith("save file not written", StringComparison.Ordinal));
        Assert.Equal(4, SaveFile.ReadWithFallback(SavePath, out _)!.SystemData.Block.GetG(10));
    }

    private static int Mark(string path) => SaveFile.FromBytes(File.ReadAllBytes(path)).SystemData.Block.GetG(10);

    private string[] FolderFiles() => Directory.GetFileSystemEntries(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void AReaderStillHoldingTheFileItReadDoesNotStopTheCommitAfterTheNext()
    {
        // The file a reader opened becomes the backup at the next commit, and on Windows nobody can replace a backup that is open, whatever its holder shares:
        // the commit after that one failed ("Unable to remove the file to be replaced") while the reader had not let go. An auto-save point commits twice in a row.
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        byte[] first = File.ReadAllBytes(SavePath);
        using (FileStream reader = SaveFile.OpenForReading(SavePath))
        {
            SaveFile.WriteAtomic(SavePath, StoreWith(2));
            SaveFile.WriteAtomic(SavePath, StoreWith(3));

            // the file is the newest generation, whole; the backup is a whole generation too: the reader's own on Windows, where it could not be replaced
            Assert.Equal(3, SaveFile.ReadWithFallback(SavePath, out bool backup)!.SystemData.Block.GetG(10));
            Assert.False(backup);
            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, Mark(SavePath + ".bak"));
            Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());

            // and the reader still reads the file it opened, whole
            byte[] read = new byte[first.Length];
            reader.ReadExactly(read);
            Assert.Equal(first, read);
            Assert.Equal(-1, reader.ReadByte());
        }

        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        Assert.Equal(3, Mark(SavePath));

        // once the reader has let go the backup follows again
        SaveFile.WriteAtomic(SavePath, StoreWith(4));
        Assert.Equal(4, Mark(SavePath));
        Assert.Equal(3, Mark(SavePath + ".bak"));
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());

        // through the services: both commits of a running game say they were written
        var rig = new AppRig(savePath: SavePath, store: StoreWith(5));
        var log = new List<string>();
        rig.Services.Logged += log.Add;
        using (SaveFile.OpenForReading(SavePath))
        {
            Assert.True(rig.Services.Commit());
            Assert.True(rig.Services.Commit());
        }

        Assert.DoesNotContain(log, l => l.StartsWith("save file not written", StringComparison.Ordinal));
        Assert.Equal(5, Mark(SavePath));
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
    }

    [Fact]
    public void AReaderThatLetsGoWhileTheCommitWaitsCostsTheBackupNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // elsewhere an open file stops no replace: the commit waits for nobody
        }

        // The commit tries its replace again for a fifth of a second before it leaves the backup behind: a reader that is only slow has let go by then, and the
        // previous generation becomes the backup as always. The helper lets go 80 ms after the commit began; a round in which the machine held the helper back
        // past the commit's wait proves nothing about the wait and is done again.
        for (int round = 0; ; round++)
        {
            File.Delete(SavePath);
            File.Delete(SavePath + ".bak");
            SaveFile.WriteAtomic(SavePath, StoreWith(1));
            FileStream reader = SaveFile.OpenForReading(SavePath);
            SaveFile.WriteAtomic(SavePath, StoreWith(2));
            var helper = new Thread(() =>
            {
                Thread.Sleep(80);
                reader.Dispose();
            });
            helper.Start();
            SaveFile.WriteAtomic(SavePath, StoreWith(3));
            helper.Join();
            Assert.Equal(3, Mark(SavePath));
            int kept = Mark(SavePath + ".bak");
            if (kept == 2 || round == 4)
            {
                Assert.Equal(2, kept);
                break;
            }
        }

        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
    }

    [Fact]
    public void ACommitThatIsRefusedLeavesThePreviousSaveInPlaceAndNoTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // only Windows refuses a replace because of somebody's open file
        }

        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        using (SaveFile.OpenForReading(SavePath))
        {
            // this reader's file is the backup now, and it does not let go
            SaveFile.WriteAtomic(SavePath, StoreWith(2));

            // A second program has the file itself open and does not let go either, sharing everything as a reader does: the backup cannot be replaced, and the
            // one rename the commit falls back on is refused too. The commit fails as it did before, with the replace's own words, and nothing was touched.
            using (SaveFile.OpenForReading(SavePath))
            {
                IOException refused = Assert.Throws<IOException>(() => SaveFile.WriteAtomic(SavePath, StoreWith(3)));
                Assert.Equal(unchecked((int)0x80070497), refused.HResult);          // ERROR_UNABLE_TO_REMOVE_REPLACED
                Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
            }

            Assert.Equal(2, Mark(SavePath));
            Assert.Equal(1, Mark(SavePath + ".bak"));

            // the file held without sharing its removal (File.OpenRead): the same
            using (new FileStream(SavePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => SaveFile.WriteAtomic(SavePath, StoreWith(3)));
                Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
            }

            Assert.Equal(2, Mark(SavePath));
            Assert.Equal(1, Mark(SavePath + ".bak"));

            // through the services: said once, and the game goes on
            var rig = new AppRig(savePath: SavePath, store: StoreWith(4));
            var log = new List<string>();
            rig.Services.Logged += log.Add;
            using (SaveFile.OpenForReading(SavePath))
            {
                Assert.False(rig.Services.Commit());
            }

            Assert.Single(log, l => l.StartsWith("save file not written: ", StringComparison.Ordinal));
            Assert.Equal(2, Mark(SavePath));
            Assert.True(rig.Services.Commit());         // the second program let go: the one rename goes through under the holder of the backup
            Assert.Equal(4, Mark(SavePath));
            Assert.Equal(1, Mark(SavePath + ".bak"));
            Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        }
    }

    [Fact]
    public void AReadingHasClosedTheFileBeforeItParsesIt()
    {
        // The launchers read save.dat while the game plays. A reading that kept the file open while it checked and unpacked it held it many times longer than
        // the read takes, and a file still open at the commit after the next is the backup that cannot be replaced. At the hook the reading has the bytes and
        // has not parsed them: nobody has the file open then, so it opens without any sharing (on Windows that is refused while another handle is open).
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        SaveFile.WriteAtomic(SavePath, StoreWith(2));
        byte[] damaged = File.ReadAllBytes(SavePath);
        damaged[damaged.Length / 2] ^= 0x40;
        var closed = new List<string>();
        var refused = new List<string>();
        SaveFile.ReadAndClosed = path =>
        {
            if (Path.GetDirectoryName(path) != _dir)
            {
                return;     // another test's reading
            }

            closed.Add(Path.GetFileName(path));
            try
            {
                using var alone = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e)
            {
                refused.Add(Path.GetFileName(path) + ": " + e.Message);
            }
        };
        try
        {
            Assert.Equal(2, SaveFile.ReadWithFallback(SavePath, out bool backup)!.SystemData.Block.GetG(10));
            Assert.False(backup);
            Assert.Equal(["save.dat"], closed);

            // the same for the backup, when the file itself is damaged
            closed.Clear();
            File.WriteAllBytes(SavePath, damaged);
            Assert.Equal(1, SaveFile.ReadWithFallback(SavePath, out backup)!.SystemData.Block.GetG(10));
            Assert.True(backup);
            Assert.Equal(["save.dat", "save.dat.bak"], closed);
            Assert.Empty(refused);
        }
        finally
        {
            SaveFile.ReadAndClosed = null;
        }
    }

    [Fact]
    public void TheSaveFileIsReadWhileAnotherHandleHasItOpenForWriting()
    {
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        SaveFile.WriteAtomic(SavePath, StoreWith(2));                           // a backup exists too
        byte[] damaged = File.ReadAllBytes(SavePath);
        damaged[damaged.Length / 2] ^= 0x40;

        // a handle with write access (a tool that edits the profile, a sync program): a reader that shares only reading is refused on Windows
        using (new FileStream(SavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        using (new FileStream(SavePath + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(2, SaveFile.ReadWithFallback(SavePath, out bool backup)!.SystemData.Block.GetG(10));
            Assert.False(backup);

            // the backup is opened the same way when the file itself is damaged
            using (var writer = new FileStream(SavePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                writer.Write(damaged);
            }

            Assert.Equal(1, SaveFile.ReadWithFallback(SavePath, out backup)!.SystemData.Block.GetG(10));
            Assert.True(backup);
        }

        Assert.Throws<ArgumentException>(() => SaveFile.OpenForReading(string.Empty));
        Assert.Throws<FileNotFoundException>(() => SaveFile.OpenForReading(Path.Combine(_dir, "none.dat")));
    }
}
