// v0.1.9: the Windows launcher's verification modes (--render, --selftest, --update-selftest, --stamp-selftest) never open a box. Nobody watches those runs,
// so a modal box waits for a click on the screen of whoever started them, and the run hangs (package R1 kept the usage error out of them; an exception nobody
// caught still opened one). The launcher is a WPF program these tests cannot load, and a test that ran it into a box would put that box on the screen, so the
// rules are read from src/Tsuki.Launcher/LauncherApp.xaml.cs: one place opens a box and it says nothing in a verification mode, the modes are read before
// the arguments are parsed, and an exception nobody caught there goes to the error stream, into the --data profile only, and ends the run with a code that
// is not 0.
using System.Text.RegularExpressions;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed partial class WindowsLauncherBoxTests
{
    private static readonly string AppFile = Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher", "LauncherApp.xaml.cs");

    /// <summary>The text between the braces of the first member (or statement) whose declaration starts with <paramref name="declaration"/>.</summary>
    internal static string Body(string code, string declaration)
    {
        int at = code.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(at >= 0, $"LauncherApp.xaml.cs has no '{declaration}'");
        int open = code.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            depth += code[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return code[(open + 1)..i];
            }
        }

        throw new InvalidOperationException($"the braces of '{declaration}' do not close");
    }

    [Fact]
    public void TheWindowsLauncherOpensABoxInOnePlaceAndNeverInAVerificationMode()
    {
        var hits = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, "Tsuki.Launcher"), "*", SearchOption.AllDirectories))
        {
            string relative = global::Tsuki.Tests.Synthetic.Repo.Relative(file);
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || !(relative.EndsWith(".cs", StringComparison.Ordinal) || relative.EndsWith(".xaml", StringComparison.Ordinal)))
            {
                continue;
            }

            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (OpensABox().IsMatch(lines[i]))
                {
                    hits.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(scanned > 40, $"only {scanned} launcher files were read");
        Assert.True(hits.Count == 1, "the launcher opens a box through LauncherApp.Box only:" + Environment.NewLine + string.Join(Environment.NewLine, hits));
        Assert.StartsWith("src/Tsuki.Launcher/LauncherApp.xaml.cs:", hits[0], StringComparison.Ordinal);

        string box = Body(File.ReadAllText(AppFile), "private void Box(");
        int quiet = box.IndexOf("if (_unattended)", StringComparison.Ordinal);
        int ret = box.IndexOf("return;", StringComparison.Ordinal);
        int show = box.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        Assert.True(quiet >= 0 && quiet < ret && ret < show, "Box returns in a verification mode before it shows anything:" + Environment.NewLine + box);

        // and the guard sees what it must
        Assert.Matches(OpensABox(), "MessageBox.Show(message, \"Tsukihime Launcher\");");
        Assert.Matches(OpensABox(), "_ = System.Windows.MessageBox.Show (text)");
        Assert.DoesNotMatch(OpensABox(), "Box(message, MessageBoxImage.None);");
    }

    [Fact]
    public void TheVerificationModesAreReadBeforeTheArgumentsAndAreTheUsageLinesFour()
    {
        string code = File.ReadAllText(AppFile);
        Match modes = Regex.Match(code, @"VerificationModes = \[(?<list>[^\]]*)\];");
        Assert.True(modes.Success, "LauncherApp names its verification modes in one list");
        string[] listed = [.. Regex.Matches(modes.Groups["list"].Value, "\"(--[a-z-]+)\"").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal)];
        Assert.Equal(["--render", "--selftest", "--stamp-selftest", "--update-selftest"], listed);

        string usage = Regex.Match(code, "private const string Usage = \"(?<u>[^\"]*)\";").Groups["u"].Value;
        foreach (string mode in listed)
        {
            Assert.Contains($"[{mode} ", usage, StringComparison.Ordinal);
        }

        // read from the raw arguments before anything else: an exception or a bad argument is handled by then, and a --render given without its folder counts
        string startup = Body(code, "protected override void OnStartup(");
        int read = startup.IndexOf("_unattended = e.Args.Any(a => VerificationModes.Contains(a, StringComparer.Ordinal));", StringComparison.Ordinal);
        int handler = startup.IndexOf("DispatcherUnhandledException +=", StringComparison.Ordinal);
        int parse = startup.IndexOf("switch (e.Args[i])", StringComparison.Ordinal);
        Assert.True(read >= 0 && read < handler && handler < parse, "the verification modes are read first, then the handler, then the arguments");

        // the usage error of a verification mode goes to the error stream and the mode's own file
        int bad = startup.IndexOf("if (bad is not null)", StringComparison.Ordinal);
        string usageError = startup[bad..startup.IndexOf("Shutdown(64);", bad, StringComparison.Ordinal)];
        Assert.Matches(@"if \(_unattended\)\s*\{\s*Console\.Error\.WriteLine\(message\);\s*WriteUsageError\(", usageError);
    }

    [Fact]
    public void AnExceptionNobodyCaughtInAVerificationModeOpensNoBoxStaysOutOfThePlayersProfileAndEndsTheRunWithAnError()
    {
        string code = File.ReadAllText(AppFile);
        string report = Body(code, "private void Report(Exception e)");
        Assert.Contains("LauncherPaths? paths = _unattended ? _dataProfile : _paths;", report, StringComparison.Ordinal);
        int quiet = report.IndexOf("if (_unattended)", StringComparison.Ordinal);
        int crashed = report.IndexOf("_crashed = true;", StringComparison.Ordinal);
        int stderr = report.IndexOf("Console.Error.WriteLine(", StringComparison.Ordinal);
        int ret = report.IndexOf("return;", quiet < 0 ? 0 : quiet, StringComparison.Ordinal);
        int box = report.IndexOf("Box(", StringComparison.Ordinal);
        Assert.True(quiet >= 0 && quiet < crashed && crashed < ret && stderr < ret && ret < box, "Report says it on the error stream and returns before any box in a verification mode:" + Environment.NewLine + report);

        // the --data profile only: the field is set from --data, never from the player's own profile
        string startup = Body(code, "protected override void OnStartup(");
        Assert.Contains("_dataProfile = data is null ? null : paths;", startup, StringComparison.Ordinal);
        Assert.Matches(@"DispatcherUnhandledException \+= \(_, args\) =>\s*\{\s*Report\(args\.Exception\);\s*args\.Handled = true;\s*\};", startup);

        // a run that met one does not end with 0
        string exit = Body(code, "protected override void OnExit(");
        Assert.Matches(@"if \(_crashed && e\.ApplicationExitCode == 0\)\s*\{\s*e\.ApplicationExitCode = 70;", exit);
    }

    [GeneratedRegex(@"MessageBox\s*\.\s*Show\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex OpensABox();
}
