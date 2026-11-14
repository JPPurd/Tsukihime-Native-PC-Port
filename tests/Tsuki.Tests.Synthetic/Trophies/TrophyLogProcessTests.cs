using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tsuki.Engine.Trophies;

namespace Tsuki.Tests.Synthetic.Trophies;

/// <summary>
/// v0.1.9: the game writes trophies.ini while a launcher, another program, reads it. Here a second process does the launcher's part: this test program started
/// once more, hidden, to run one method (<see cref="HelperProcess"/>) that its environment tells what to do. The two talk through files in a folder beside the
/// record's: "ready" (the helper is at work), "stop" (the test is done writing) and the helper's report.
/// </summary>
public sealed class TrophyLogProcessTests : IDisposable
{
    private const string RoleVariable = "TSUKI_TEST_TROPHYLOG_ROLE";
    private const string FolderVariable = "TSUKI_TEST_TROPHYLOG_FOLDER";
    private const int HelperSeconds = 60;

    private static readonly DateTime T1 = new(2026, 11, 12, 13, 3, 11, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsuki-v019-trophylog2-" + Guid.NewGuid().ToString("N"));

    public TrophyLogProcessTests()
    {
        // the second process works in the folder the test made and needs none of its own (a helper that is stopped half way would leave it behind)
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RoleVariable)))
        {
            Directory.CreateDirectory(ProfileOf(_dir));
            Directory.CreateDirectory(ControlOf(_dir));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // a helper that was stopped may still be letting go of the folder
        }
    }

    private static string ProfileOf(string folder) => Path.Combine(folder, "profile");

    private static string ControlOf(string folder) => Path.Combine(folder, "control");

    private static string RecordOf(string folder) => Path.Combine(ProfileOf(folder), TrophyLog.FileName);

    private static string ReadyOf(string folder) => Path.Combine(ControlOf(folder), "ready");

    private static string StopOf(string folder) => Path.Combine(ControlOf(folder), "stop");

    private static string ReportOf(string folder) => Path.Combine(ControlOf(folder), "report.txt");

    // the reader's word that it has seen a record of that many trophies: one empty file per count, never written over
    private static string SeenOf(string folder, int count) => Path.Combine(ControlOf(folder), "seen-" + count.ToString(CultureInfo.InvariantCulture));

    // -- the second process's part ----------------------------------------------------------------------------------------

    /// <summary>
    /// What the second process runs. In an ordinary run of the tests nobody asked for a part and this does nothing; started by one of the tests below it reads
    /// the record over and over as a launcher's page does ("reader"), or holds it open with a reader's sharing ("holder"), until the test says stop.
    /// </summary>
    [Fact]
    public void HelperProcess()
    {
        string? role = Environment.GetEnvironmentVariable(RoleVariable);
        string? folder = Environment.GetEnvironmentVariable(FolderVariable);
        if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(folder) || !Directory.Exists(ControlOf(folder)))
        {
            return;
        }

        long end = Environment.TickCount64 + (HelperSeconds * 1000);
        var report = new StringBuilder();
        if (role == "reader")
        {
            int readings = 0;
            int faults = 0;
            int grew = 0;
            int last = 0;
            string first = string.Empty;
            while (!File.Exists(StopOf(folder)) && Environment.TickCount64 < end)
            {
                TrophyLog seen = TrophyLog.Read(RecordOf(folder), out bool damaged);
                readings++;
                if (damaged || seen.Count < last)
                {
                    faults++;
                    first = first.Length == 0 ? $"reading {readings}: damaged {damaged}, {seen.Count} earned after {last}" : first;
                }
                else
                {
                    bool more = seen.Count > last;
                    grew += more && last != 0 ? 1 : 0;
                    last = seen.Count;
                    if (more)
                    {
                        File.WriteAllText(SeenOf(folder, last), string.Empty);
                    }
                }

                if (readings == 1)
                {
                    File.WriteAllText(ReadyOf(folder), string.Empty);
                }

                Thread.SpinWait(3000);                              // a page reads now and then, not without a pause
            }

            report.Append(CultureInfo.InvariantCulture, $"readings={readings}\nfaults={faults}\ngrew={grew}\nlast={last}\nfirst={first}\n");
        }
        else if (role == "holder")
        {
            // the handle a reader has while it reads (TrophyLog.Read's sharing), kept for as long as the test wants
            using var stream = new FileStream(RecordOf(folder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            File.WriteAllText(ReadyOf(folder), string.Empty);
            while (!File.Exists(StopOf(folder)) && Environment.TickCount64 < end)
            {
                Thread.Sleep(5);
            }

            using var text = new StreamReader(stream, Encoding.UTF8);
            report.Append(text.ReadToEnd());
        }
        else
        {
            Assert.Fail($"no such part: {role}");
        }

        // the report appears whole or not at all
        string draft = ReportOf(folder) + ".draft";
        File.WriteAllText(draft, report.ToString(), new UTF8Encoding(false));
        File.Move(draft, ReportOf(folder));
    }

    // -- the tests --------------------------------------------------------------------------------------------------------

    private sealed class Helper(Process process, StringBuilder output) : IDisposable
    {
        public Process Process { get; } = process;

        public string Output
        {
            get
            {
                lock (output)
                {
                    return output.ToString();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    // only ever the process this test started
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10_000);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // it ended by itself meanwhile
            }

            Process.Dispose();
        }
    }

    // Starts this test program again, without a window, to run HelperProcess in the given part. The program is the tests' own executable beside the test
    // assembly; where a platform has none, the assembly is run through "dotnet exec".
    private Helper Start(string role)
    {
        string assembly = typeof(TrophyLogProcessTests).Assembly.Location;
        string program = Path.Combine(Path.GetDirectoryName(assembly)!, Path.GetFileNameWithoutExtension(assembly) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _dir,
        };
        if (File.Exists(program))
        {
            start.FileName = program;
        }
        else
        {
            start.FileName = "dotnet";
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(assembly);
        }

        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(TrophyLogProcessTests).FullName}.{nameof(HelperProcess)}");
        start.ArgumentList.Add("-noLogo");
        start.ArgumentList.Add("-noColor");
        start.Environment[RoleVariable] = role;
        start.Environment[FolderVariable] = _dir;

        var output = new StringBuilder();
        void Keep(object sender, DataReceivedEventArgs e)
        {
            lock (output)
            {
                output.AppendLine(e.Data);
            }
        }

        var process = new Process { StartInfo = start };
        process.OutputDataReceived += Keep;
        process.ErrorDataReceived += Keep;
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new Helper(process, output);
    }

    private void WaitUntilReady(Helper helper)
    {
        long end = Environment.TickCount64 + (HelperSeconds * 1000);
        while (!File.Exists(ReadyOf(_dir)))
        {
            Assert.False(helper.Process.HasExited, "the helper process ended before it was ready:\n" + helper.Output);
            Assert.True(Environment.TickCount64 < end, "the helper process did not get ready:\n" + helper.Output);
            Thread.Sleep(10);
        }
    }

    private void WaitUntilSeen(Helper helper, int count)
    {
        long end = Environment.TickCount64 + (HelperSeconds * 1000);
        while (!File.Exists(SeenOf(_dir, count)))
        {
            Assert.False(helper.Process.HasExited, $"the helper process ended before it saw {count} trophies:\n" + helper.Output);
            Assert.True(Environment.TickCount64 < end, $"the helper process did not see {count} trophies:\n" + helper.Output);
            Thread.Sleep(1);
        }
    }

    private string StopAndReadReport(Helper helper)
    {
        File.WriteAllText(StopOf(_dir), string.Empty);
        Assert.True(helper.Process.WaitForExit(HelperSeconds * 1000), "the helper process did not end:\n" + helper.Output);
        Assert.True(helper.Process.ExitCode == 0, $"the helper process ended with {helper.Process.ExitCode}:\n{helper.Output}");
        Assert.True(File.Exists(ReportOf(_dir)), "the helper process left no report:\n" + helper.Output);
        return File.ReadAllText(ReportOf(_dir));
    }

    [Fact]
    public void WithoutAPartTheHelperDoesNothing()
    {
        // what an ordinary run of the tests sees of it
        Assert.Null(Environment.GetEnvironmentVariable(RoleVariable));
        HelperProcess();
        Assert.Empty(Directory.EnumerateFileSystemEntries(ControlOf(_dir)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ProfileOf(_dir)));
    }

    [Fact]
    public void TheGamesWritesGoThroughWhileAnotherProcessReadsTheRecordAndEveryReadingIsWhole()
    {
        string record = RecordOf(_dir);
        var log = new TrophyLog();
        log.Add(0, null);
        log.Write(record);

        var failed = new List<string>();
        int writes = 0;
        string report;
        using (Helper helper = Start("reader"))
        {
            WaitUntilReady(helper);
            for (int round = 0; round < 6; round++)
            {
                for (int id = 1; id < TrophyTable.Count; id++)
                {
                    log.Add(id, T1);
                    try
                    {
                        log.Write(record);
                        writes++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        failed.Add($"write {writes + failed.Count + 1}: {e.GetType().Name}: {e.Message}");
                        continue;
                    }

                    // The first round, in which the record grows, waits after each write until the other process has read it: that it read while the record
                    // grew is then certain, not a matter of how the machine shares its time (a loaded machine may give the other process none for a while).
                    // The later rounds write the same record again and again, as fast as they can, while the other process reads.
                    if (round == 0)
                    {
                        WaitUntilSeen(helper, log.Count);
                    }
                }
            }

            report = StopAndReadReport(helper);
        }

        // the reader in the other process never cost the game a write
        Assert.True(failed.Count == 0, $"{failed.Count} writes failed: {string.Join("; ", failed.Take(3))}");
        Assert.Equal(174, writes);

        // and what it read was always a whole record that only grew
        Dictionary<string, string> said = report.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
        Assert.True(said["faults"] == "0", $"{said["faults"]} of {said["readings"]} readings in the other process: {said["first"]}");
        Assert.True(int.Parse(said["readings"], CultureInfo.InvariantCulture) >= 20, report);
        Assert.True(int.Parse(said["grew"], CultureInfo.InvariantCulture) >= 3, "the other process did not read while the record grew: " + report);
        Assert.Equal("30", said["last"]);

        TrophyLog end = TrophyLog.Read(record, out bool damaged);
        Assert.False(damaged);
        Assert.Equal(30, end.Count);
        Assert.Equal([TrophyLog.FileName], Directory.EnumerateFileSystemEntries(ProfileOf(_dir)).Select(Path.GetFileName));
    }

    [Fact]
    public void AWriteGoesThroughWhileAnotherProcessHoldsTheRecordOpenAsAReaderDoes()
    {
        // On Windows a move over a file that is open anywhere is refused, whatever the holder shares: a reader that is slow (a virus scanner, a page on a
        // slow disk) must still not cost the game its write.
        string record = RecordOf(_dir);
        var old = new TrophyLog();
        old.Add(1, T1);
        old.Write(record);
        string oldText = old.Format();

        string held;
        using (Helper helper = Start("holder"))
        {
            WaitUntilReady(helper);
            var next = new TrophyLog();
            next.Add(1, T1);
            next.Add(2, null);
            next.Write(record);
            Assert.Equal([1, 2], TrophyLog.Read(record, out bool damaged).Ids);
            Assert.False(damaged);

            // and again, while the other process still holds what is now the file before the previous one
            next.Add(3, T1);
            next.Write(record);
            Assert.Equal([1, 2, 3], TrophyLog.Read(record, out damaged).Ids);
            Assert.False(damaged);

            held = StopAndReadReport(helper);
        }

        // the holder read the file it had opened, whole
        Assert.Equal(oldText, held);
        Assert.Equal([1, 2, 3], TrophyLog.Read(record, out _).Ids);
        Assert.Equal([TrophyLog.FileName], Directory.EnumerateFileSystemEntries(ProfileOf(_dir)).Select(Path.GetFileName));
    }
}
