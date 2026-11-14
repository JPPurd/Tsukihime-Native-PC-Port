using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.App;

/// <summary>
/// FIXES D13 S-5: a replace that stopped half way. Windows documents a failure of the replace in which the old file already carries the backup's name and the new
/// one was not moved (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2): no save.dat is there then, and the commit's temporary file is the only copy of the newest
/// generation. <see cref="SaveFile.WriteAtomic"/> removed that file as after any other failure; at the last commit of a run the next start found only the
/// backup, began a new profile, and that profile's second commit wrote over the backup. Nobody has made a real replace stop there, so these tests make it
/// through <see cref="SaveFile.BeforeRename"/>.
/// </summary>
public sealed class SaveHalfWayReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-d13-halfway-" + Guid.NewGuid().ToString("N"));

    public SaveHalfWayReplaceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SaveFile.BeforeRename = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SavePath => Path.Combine(_dir, "save.dat");

    private string BackupPath => SavePath + ".bak";

    private static SaveStore StoreWith(ushort mark)
    {
        SaveStore store = AppRig.NewStore();
        store.SystemData.Block.SetG(10, mark);
        return store;
    }

    private static int Mark(string path) => SaveFile.FromBytes(File.ReadAllBytes(path)).SystemData.Block.GetG(10);

    private string[] FolderFiles() => Directory.GetFileSystemEntries(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    // the error Windows has for a replace that renamed the old file and did not move the new one
    private static IOException StoppedHalfWay() =>
        new("Unable to move the replacement file to the file to be replaced. The file to be replaced has been renamed using the backup name.", unchecked((int)0x80070499));

    // From now on a commit of this folder goes like this: a step while the save file is there is its replace, which does its first rename (the old file takes
    // the backup's name) and throws `replace`; a step without it (the move that follows, or the move of a commit that found no save file) throws `move` when
    // there is one. The steps of this folder's commits are counted; other tests' commits pass by.
    private Func<int> StopTheReplaceHalfWay(Exception replace, Exception? move = null, bool removeInsteadOfRename = false)
    {
        int steps = 0;
        SaveFile.BeforeRename = (tmp, full) =>
        {
            if (Path.GetDirectoryName(full) != _dir)
            {
                return;
            }

            Assert.Equal(SavePath, full);
            Assert.Matches("^save\\.dat\\.[0-9a-f]{8}\\.tmp$", Path.GetFileName(tmp));
            steps++;
            if (File.Exists(full))
            {
                if (removeInsteadOfRename)
                {
                    File.Delete(full);
                }
                else
                {
                    File.Move(full, full + ".bak", overwrite: true);
                }

                throw replace;
            }

            if (move is not null)
            {
                throw move;
            }
        };
        return () => steps;
    }

    [Fact]
    public void ACommitWhoseReplaceStoppedHalfWayIsWrittenAllTheSame()
    {
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        SaveFile.WriteAtomic(SavePath, StoreWith(2));

        // no save file and the newest generation only under its temporary name: that file takes the name, and the commit says nothing
        Func<int> steps = StopTheReplaceHalfWay(StoppedHalfWay());
        SaveFile.WriteAtomic(SavePath, StoreWith(3));
        Assert.Equal(2, steps());                                                   // the replace, then the move
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        Assert.Equal(3, Mark(SavePath));
        Assert.Equal(2, Mark(BackupPath));                                          // the generation before it, as after every commit
        Assert.Equal(3, SaveFile.ReadWithFallback(SavePath, out bool backup)!.SystemData.Block.GetG(10));
        Assert.False(backup);

        // through the services: the commit of a running game says it was written
        var rig = new AppRig(savePath: SavePath, store: StoreWith(4));
        var log = new List<string>();
        int failures = 0;
        rig.Services.Logged += log.Add;
        rig.Services.SaveFailed += (_, _) => failures++;
        Assert.True(rig.Services.Commit());
        Assert.Equal(4, steps());
        Assert.DoesNotContain(log, l => l.StartsWith("save file not written", StringComparison.Ordinal));
        Assert.Equal(0, failures);
        Assert.Equal(4, Mark(SavePath));
        Assert.Equal(3, Mark(BackupPath));

        // and the next commit goes on as ever
        SaveFile.BeforeRename = null;
        SaveFile.WriteAtomic(SavePath, StoreWith(5));
        Assert.Equal(5, Mark(SavePath));
        Assert.Equal(4, Mark(BackupPath));
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
    }

    [Fact]
    public void WhenTheMoveIsRefusedTooTheNewestSaveStaysForTheNextStartToFinish()
    {
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        SaveFile.WriteAtomic(SavePath, StoreWith(2));

        // the commit fails with the replace's own error, and its temporary file, the only copy of the newest generation, is not removed
        IOException replace = StoppedHalfWay();
        Func<int> steps = StopTheReplaceHalfWay(replace, move: new UnauthorizedAccessException("the move is refused too"));
        Assert.Same(replace, Assert.Throws<IOException>(() => SaveFile.WriteAtomic(SavePath, StoreWith(3))));
        Assert.Equal(2, steps());
        string tmp = Assert.Single(Directory.GetFiles(_dir, "save.dat.*.tmp"));
        Assert.Equal(new[] { Path.GetFileName(tmp), "save.dat.bak" }.Order(StringComparer.Ordinal).ToArray(), FolderFiles());
        Assert.Equal(3, Mark(tmp));
        Assert.Equal(2, Mark(BackupPath));
        Assert.Null(SaveFile.ReadWithFallback(SavePath, out _));                    // a reader finds no save file until the game's next start

        // which finishes the commit: the newest generation is the save file, the one before it the backup
        SaveFile.BeforeRename = null;
        Assert.Equal(tmp, SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        Assert.Equal(3, Mark(SavePath));
        Assert.Equal(2, Mark(BackupPath));

        // through the services: the game says once that the save was not written, and its next commit writes the save file again
        var rig = new AppRig(savePath: SavePath, store: StoreWith(4));
        var log = new List<string>();
        var failures = new List<string>();
        rig.Services.Logged += log.Add;
        rig.Services.SaveFailed += (path, _) => failures.Add(path);
        steps = StopTheReplaceHalfWay(StoppedHalfWay(), move: new IOException("the move is refused too"));
        Assert.False(rig.Services.Commit());
        Assert.Equal(2, steps());
        Assert.Single(log, l => l.StartsWith("save file not written: ", StringComparison.Ordinal));
        Assert.Equal([SavePath], failures);
        string kept = Assert.Single(Directory.GetFiles(_dir, "save.dat.*.tmp"));
        Assert.Equal(4, Mark(kept));
        Assert.False(File.Exists(SavePath));
        Assert.Equal(3, Mark(BackupPath));

        SaveFile.BeforeRename = null;
        Assert.True(rig.Services.Commit());
        Assert.Equal(4, Mark(SavePath));
        Assert.Equal(3, Mark(BackupPath));

        // the refused commit's file stays where it is (nothing removes such files in 0.1.9), and a start that finds the save file touches nothing
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(new[] { Path.GetFileName(kept), "save.dat", "save.dat.bak" }.Order(StringComparer.Ordinal).ToArray(), FolderFiles());
        Assert.Equal(4, Mark(kept));
    }

    [Fact]
    public void EveryOtherCommitThatFailsRemovesItsTemporaryFile()
    {
        // a folder of the save file's name beside a backup: no move can finish that, at this commit or at a start, and none is tried after the commit's own
        Directory.CreateDirectory(SavePath);
        File.WriteAllBytes(BackupPath, SaveFile.ToBytes(StoreWith(1)));
        Func<int> steps = StopTheReplaceHalfWay(StoppedHalfWay());
        Exception? refused = Record.Exception(() => SaveFile.WriteAtomic(SavePath, StoreWith(2)));
        Assert.True(refused is IOException or UnauthorizedAccessException, $"{refused}");
        Assert.Equal(1, steps());
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        Assert.True(Directory.Exists(SavePath));
        Assert.Equal(1, Mark(BackupPath));
        Directory.Delete(SavePath);

        // only the backup (a save file somebody removed: a new profile) and the move of a commit that has nothing to replace is refused: no replace stopped
        // half way there, so the temporary file goes as after every other failure, beside the backup too, and the move is not tried again
        var once = new IOException("the move is refused");
        steps = StopTheReplaceHalfWay(StoppedHalfWay(), move: once);
        Assert.Same(once, Assert.Throws<IOException>(() => SaveFile.WriteAtomic(SavePath, StoreWith(2))));
        Assert.Equal(1, steps());
        Assert.Equal(["save.dat.bak"], FolderFiles());
        Assert.Equal(1, Mark(BackupPath));
        SaveFile.BeforeRename = null;
        File.Delete(BackupPath);

        // a folder takes the save file's name while the replace runs (the old file already has the backup's name): no move can finish that, and none is tried
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        SaveFile.WriteAtomic(SavePath, StoreWith(2));
        int tries = 0;
        IOException halfWay = StoppedHalfWay();
        SaveFile.BeforeRename = (tmp, full) =>
        {
            if (full == SavePath && ++tries == 1)
            {
                File.Move(full, full + ".bak", overwrite: true);
                Directory.CreateDirectory(full);
                throw halfWay;
            }
        };
        Assert.Same(halfWay, Assert.Throws<IOException>(() => SaveFile.WriteAtomic(SavePath, StoreWith(3))));
        Assert.Equal(1, tries);
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        Assert.True(Directory.Exists(SavePath));
        Assert.Equal(2, Mark(BackupPath));
        SaveFile.BeforeRename = null;
        Directory.Delete(SavePath);
        File.Delete(BackupPath);

        // no backup (a profile's second commit), the save file taken away under the commit, and the move refused: a start could not finish that (it wants the
        // backup), so the temporary file goes as after every failure, and the first error is the one reported
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        var gone = new FileNotFoundException("the file to be replaced is not there");
        steps = StopTheReplaceHalfWay(gone, move: new IOException("the move is refused too"), removeInsteadOfRename: true);
        Assert.Same(gone, Assert.Throws<FileNotFoundException>(() => SaveFile.WriteAtomic(SavePath, StoreWith(2))));
        Assert.Equal(2, steps());
        Assert.Empty(FolderFiles());

        // (the same without the refusal: the commit is written)
        SaveFile.BeforeRename = null;
        SaveFile.WriteAtomic(SavePath, StoreWith(1));
        steps = StopTheReplaceHalfWay(gone, removeInsteadOfRename: true);
        SaveFile.WriteAtomic(SavePath, StoreWith(2));
        Assert.Equal(2, steps());
        Assert.Equal(["save.dat"], FolderFiles());
        Assert.Equal(2, Mark(SavePath));
        SaveFile.BeforeRename = null;
        File.Delete(SavePath);

        // a store that cannot be written (a thumbnail without a format): its temporary file never was a whole save, and it does not become the save file
        var rig = new AppRig();
        rig.Begin();
        Assert.True(rig.Services.Save(3));
        rig.Store.SetSlot(3, rig.Store.GetSlot(3)!, new SlotThumbnail(0, [1]));
        Assert.Throws<ArgumentException>(() => SaveFile.WriteAtomic(SavePath, rig.Store));
        Assert.Empty(FolderFiles());
    }
}
