using System.Formats.Tar;
using Tsuki.Cli.Commands;
using Tsuki.Engine.Trophies;

namespace Tsuki.Tests.Data.Trophies;

/// <summary>
/// v0.1.9: a player's record of achievements is theirs, like a save: no release of any platform carries one, and neither does an update a launcher installs.
/// (These need no game folder; they live here because the release rules are the command line tool's.)
/// </summary>
public sealed class TrophyReleaseRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-trophyrelease-" + Guid.NewGuid().ToString("N"));

    public TrophyReleaseRuleTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("trophies.ini")]
    [InlineData("Trophies.INI")]
    [InlineData("profile/trophies.ini")]
    [InlineData("a\\b\\trophies.ini")]
    [InlineData("/trophies.ini")]
    [InlineData("Contents/Resources/trophies.ini")]
    [InlineData("Contents/MacOS/trophies.ini")]
    [InlineData("trophies.ini.0a1b2c3d.tmp")]                  // what a game stopped in the middle of a write leaves: a whole copy of the record
    [InlineData("profile/save.dat.0a1b2c3d.tmp")]
    [InlineData("save.dat.restore.tmp")]                        // the launcher's, in the middle of a restore
    [InlineData("Trophies.INI.0A1B2C3D.TMP")]
    [InlineData("save.dat.broken-20261112-063348")]             // a damaged file set aside: a whole copy of the save or of the record
    [InlineData("save.dat.broken-20261112-063348-2")]
    [InlineData("profile/trophies.ini.broken-20261112T063348Z")]
    [InlineData("Contents/Resources/Trophies.INI.BROKEN-20261112T063348Z")]
    public void TheRecordOfAchievementsIsRefusedWhereverItLies(string path)
    {
        foreach (bool macBundle in new[] { false, true })
        {
            string? why = ReleaseRules.Forbidden(path, macBundle);
            Assert.NotNull(why);
            Assert.Contains("is game data or a save", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheNameRefusedIsTheRecordsOwnAndTheTrophyFileOfTheGameIsRefusedToo()
    {
        Assert.NotNull(ReleaseRules.Forbidden(TrophyLog.FileName));
        Assert.NotNull(ReleaseRules.Forbidden("sce_sys/trophy/trophy00.trp"));

        // the rule is about the file, not about the word: a document on achievements is released like any other
        Assert.Null(ReleaseRules.Forbidden("docs/trophies.md"));
        Assert.Null(ReleaseRules.Forbidden("docs/formats/trophies.ini.md"));
        Assert.Null(ReleaseRules.Forbidden("docs/formats/save.dat.md"));
        Assert.Null(ReleaseRules.Forbidden("docs/formats/save.dat.broken.md"));
        Assert.Null(ReleaseRules.Forbidden("notes.tmp"));
        Assert.Null(ReleaseRules.Forbidden("notes.broken-20261112"));
    }

    [Fact]
    public void AFolderOrAnArchiveEntryThatCarriesTheRecordIsRefusedByBothRuleSets()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(Path.Combine(_root, "data", TrophyLog.FileName), new TrophyLog().Format());
        File.WriteAllText(Path.Combine(_root, "README.md"), "x");
        string problem = Assert.Single(ReleaseRules.ForbiddenFiles(_root));
        Assert.Contains(TrophyLog.FileName, problem, StringComparison.Ordinal);

        const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        foreach (ReleaseRuleSet rules in Enum.GetValues<ReleaseRuleSet>())
        {
            string? why = ReleaseRules.LinuxTarEntryProblem("data/" + TrophyLog.FileName, TarEntryType.RegularFile, plain, rules);
            Assert.NotNull(why);
            Assert.Contains("is game data or a save", why, StringComparison.Ordinal);
        }
    }
}
