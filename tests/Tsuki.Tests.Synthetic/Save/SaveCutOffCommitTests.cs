using Tsuki.Engine.Save;

namespace Tsuki.Tests.Synthetic.Save;

/// <summary>
/// A commit that was cut off between its two renames (FIXES D11, the checker of G-1). On Windows the replace of <see cref="SaveFile.WriteAtomic"/> gives the old
/// file the backup's name before it gives the new file the file's; a process that ends in between leaves no save.dat, the previous generation as save.dat.bak
/// and the newest one, whole, as save.dat.(8 hex digits).tmp. The next start found no save file and began a new profile, and that profile's second commit
/// wrote over the backup. <see cref="SaveFile.FinishInterruptedCommit"/> does the missing rename; these tests make the states by hand.
/// </summary>
public sealed class SaveCutOffCommitTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-d11-cutoff-" + Guid.NewGuid().ToString("N"));

    public SaveCutOffCommitTests()
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

    private static byte[] Save(ushort mark)
    {
        var store = new SaveStore();
        store.SystemData.Block.SetG(10, mark);
        return SaveFile.ToBytes(store);
    }

    // a file of the folder with these bytes, last written `seconds` after T0
    private string Put(string name, byte[] bytes, int seconds)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, T0.AddSeconds(seconds));
        return path;
    }

    private static int Mark(string path) => SaveFile.FromBytes(File.ReadAllBytes(path)).SystemData.Block.GetG(10);

    private string[] FolderFiles() => Directory.GetFileSystemEntries(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    // every file of the folder with its bytes: "nothing was touched" is said by comparing two of these
    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(_dir).ToDictionary(f => Path.GetFileName(f)!, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    [Fact]
    public void TheFilesACommitCutOffBetweenItsRenamesLeftAreTheSaveAgain()
    {
        Put("save.dat.bak", Save(1), 0);
        string tmp = Put("save.dat.0a1b2c3d.tmp", Save(2), 1);
        Assert.Null(SaveFile.ReadWithFallback(SavePath, out _));                    // what the start saw: no save file, a new profile

        Assert.Equal(tmp, SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
        Assert.Equal(2, Mark(SavePath));                                            // the newest generation is the save file
        Assert.Equal(1, Mark(SavePath + ".bak"));                                   // and the backup is the generation before it, untouched
        Assert.Equal(2, SaveFile.ReadWithFallback(SavePath, out bool backup)!.SystemData.Block.GetG(10));
        Assert.False(backup);

        // done once: the save file is there now
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));

        // the next commit goes on as ever
        var next = new SaveStore();
        next.SystemData.Block.SetG(10, 3);
        SaveFile.WriteAtomic(SavePath, next);
        Assert.Equal(3, Mark(SavePath));
        Assert.Equal(2, Mark(SavePath + ".bak"));
        Assert.Equal(["save.dat", "save.dat.bak"], FolderFiles());
    }

    [Fact]
    public void WithoutTheBackupNoTemporaryFileIsTakenWhateverItsAge()
    {
        // FIXES D13 S-1a. The first of a commit's two renames makes the backup, so a commit cut off between them always leaves one. No save file and no backup is
        // a profile somebody reset by removing both, and a temporary file a commit left behind before that (a game killed while it saved: nothing removes those
        // files) is no save to bring back. Without a backup to compare with, one of any age was taken, and after it the next one, a start each.
        Put("save.dat.0a1b2c3d.tmp", Save(7), -90 * 86400);                         // left by a kill three months ago
        Put("save.dat.FFEEDDCC.tmp", Save(8), 0);                                   // one written this instant (a profile's first commit, ended before its rename)
        Put("save.dat.00000001.tmp", Save(9), 10 * 365 * 86400);                    // and one whose time is still to come (a clock that was set wrong)
        string[] names = FolderFiles();
        Dictionary<string, string> before = Snapshot();
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(names, FolderFiles());
        Assert.Equal(before, Snapshot());
        Assert.Null(SaveFile.ReadWithFallback(SavePath, out _));                    // what the start reads: no save file, a new profile

        // a folder of the backup's name is no backup
        Directory.CreateDirectory(SavePath + ".bak");
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.False(File.Exists(SavePath));
        Assert.Equal(before, Snapshot());

        // the same files beside a backup, as the first rename leaves it: the newest whole one that is not older than it is the save
        Directory.Delete(SavePath + ".bak");
        Put("save.dat.bak", Save(6), -10);
        Assert.Equal(Path.Combine(_dir, "save.dat.00000001.tmp"), SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(9, Mark(SavePath));
        Assert.Equal(6, Mark(SavePath + ".bak"));
        Assert.Equal(["save.dat", "save.dat.0a1b2c3d.tmp", "save.dat.FFEEDDCC.tmp", "save.dat.bak"], FolderFiles());
    }

    [Fact]
    public void TheNewestWholeTemporaryFileNotOlderThanTheBackupIsTheOneTaken()
    {
        Put("save.dat.bak", Save(1), 0);
        Put("save.dat.00000004.tmp", Save(4), -10);                                 // older than the backup: an earlier commit's stray, not this one's
        Put("save.dat.00000005.tmp", Save(5), 1);
        string newest = Put("save.dat.00000006.tmp", Save(6), 3);
        Put("save.dat.00000009.tmp", Save(9)[..^40], 5);                            // the newest of all, cut off while it was written: not a save
        Dictionary<string, string> before = Snapshot();

        Assert.Equal(newest, SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(6, Mark(SavePath));
        Assert.Equal(["save.dat", "save.dat.00000004.tmp", "save.dat.00000005.tmp", "save.dat.00000009.tmp", "save.dat.bak"], FolderFiles());
        Dictionary<string, string> after = Snapshot();
        Assert.Equal(before["save.dat.00000006.tmp"], after["save.dat"]);           // moved, byte for byte
        foreach (string name in new[] { "save.dat.00000004.tmp", "save.dat.00000005.tmp", "save.dat.00000009.tmp", "save.dat.bak" })
        {
            Assert.Equal(before[name], after[name]);
        }

        // a temporary file written in the backup's own instant counts as not older
        File.Delete(SavePath);
        File.Delete(Path.Combine(_dir, "save.dat.00000005.tmp"));
        string same = Put("save.dat.0000000a.tmp", Save(10), 0);
        Assert.Equal(same, SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(10, Mark(SavePath));
    }

    [Fact]
    public void WithoutSuchATemporaryFileNothingIsDone()
    {
        // a folder that is not there is not made
        string missing = Path.Combine(_dir, "none", "save.dat");
        Assert.Null(SaveFile.FinishInterruptedCommit(missing));
        Assert.False(Directory.Exists(Path.Combine(_dir, "none")));

        // an empty profile
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Empty(FolderFiles());

        // only the backup: a save file that was removed is a new profile, as it always was
        Put("save.dat.bak", Save(1), 0);
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Null(SaveFile.ReadWithFallback(SavePath, out _));

        // files that are not a commit's whole temporary file, each a newer whole save or a commit's name without a save in it
        byte[] save = Save(2);
        Put("save.dat.0a1b2c3d.tmp", save[..^1], 1);                                // a commit's name, not a whole save
        Put("save.dat.1a1b2c3d.tmp", [], 1);
        Put("save.dat.2a1b2c3d.tmp", Save(3), -1);                                  // a whole save of a commit, older than the backup
        Put("save.dat.restore.tmp", save, 1);                                       // the launcher's restore
        Put("save.dat.tmp", save, 1);
        Put("save.dat.0a1b2c3.tmp", save, 1);                                       // seven digits
        Put("save.dat.0a1b2c3dd.tmp", save, 1);                                     // nine
        Put("save.dat.0a1b2c3g.tmp", save, 1);                                      // not hexadecimal
        Put("save.dat.0a1b-c3d.tmp", save, 1);
        Put("other.dat.0a1b2c3d.tmp", save, 1);                                     // another file's
        Put("save.dat.0a1b2c3d.tmp.old", save, 1);
        Put("save.dat.broken-20261113-200000", save, 1);
        Directory.CreateDirectory(Path.Combine(_dir, "save.dat.3a1b2c3d.tmp"));     // a folder of a commit's name
        string[] names = FolderFiles();
        Dictionary<string, string> before = Snapshot();
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(names, FolderFiles());
        Assert.Equal(before, Snapshot());
        Assert.Throws<ArgumentException>(() => SaveFile.FinishInterruptedCommit(string.Empty));
    }

    [Fact]
    public void ASaveFileThatIsThereIsNeverTouched()
    {
        // a commit that ended while it waited for a held backup leaves its temporary file beside the save file: that file is newer and whole, and it stays
        Put("save.dat", Save(1), 0);
        Put("save.dat.bak", Save(0), -5);
        Put("save.dat.0a1b2c3d.tmp", Save(9), 5);
        Dictionary<string, string> before = Snapshot();
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(before, Snapshot());
        Assert.Equal(1, Mark(SavePath));

        // a damaged save file is the start's own matter (it is set aside and the backup read), not this one's
        File.WriteAllBytes(SavePath, [1, 2, 3]);
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(SavePath));
        Assert.True(File.Exists(Path.Combine(_dir, "save.dat.0a1b2c3d.tmp")));

        // a folder of the save file's name
        File.Delete(SavePath);
        Directory.CreateDirectory(SavePath);
        Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
        Assert.True(Directory.Exists(SavePath));
        Assert.True(File.Exists(Path.Combine(_dir, "save.dat.0a1b2c3d.tmp")));
    }

    [Fact]
    public void ATemporaryFileSomebodyHoldsIsLeftAndNoOlderOneIsTakenInItsPlace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;     // only Windows refuses a read or a rename because of somebody's open file
        }

        Put("save.dat.bak", Save(1), 0);
        Put("save.dat.00000002.tmp", Save(2), 1);                                   // an earlier commit's stray: whole, not older than the backup
        string newest = Put("save.dat.00000003.tmp", Save(3), 2);
        Dictionary<string, string> before = Snapshot();

        // held without any sharing: it cannot be read. Nothing is thrown, nothing is done, and the older generation is not made the save in its place
        using (new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
            Assert.False(File.Exists(SavePath));
        }

        // held by a reader that does not share its removal (File.OpenRead): it reads, and the rename is refused. The same
        using (new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Null(SaveFile.FinishInterruptedCommit(SavePath));
            Assert.False(File.Exists(SavePath));
        }

        Assert.Equal(before, Snapshot());

        // the holder has let go
        Assert.Equal(newest, SaveFile.FinishInterruptedCommit(SavePath));
        Assert.Equal(3, Mark(SavePath));
        Assert.Equal(["save.dat", "save.dat.00000002.tmp", "save.dat.bak"], FolderFiles());
    }
}
