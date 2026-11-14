using System.Formats.Tar;
using Tsuki.Cli.Commands;

namespace Tsuki.Tests.Data.Cli;

/// <summary>
/// v0.1.9: no release of any platform, and no update a launcher installs, carries an audio file. The game's sounds come from the player's game folder and the
/// port makes its one sound of its own, the achievement chime, at run time; a copy of the owner's reference chime (a .wav) or a test's reference sound must not go
/// out. (These need no game folder; they live here because the release rules are the command line tool's.)
/// </summary>
public sealed class AudioReleaseRuleTests : IDisposable
{
    // every audio extension the rules refuse, in the order of ReleaseRules.AudioExtensions
    public static TheoryData<string> AudioExtensions => [".wav", ".ogg", ".mp3", ".flac", ".opus", ".m4a", ".aac", ".wma", ".aif", ".aiff"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsuki-v019-audiorelease-" + Guid.NewGuid().ToString("N"));

    public AudioReleaseRuleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(AudioExtensions))]
    public void AnAudioFileIsRefusedWhereverItLiesAndWhateverTheCaseOfItsExtension(string extension)
    {
        foreach (string path in new[]
        {
            "achievement_chime" + extension, "sounds/achievement_chime" + extension, "a\\b\\chime" + extension, "/chime" + extension,
            "CHIME" + extension.ToUpperInvariant(), "Contents/Resources/chime" + extension, "Contents/MacOS/chime" + extension,
        })
        {
            foreach (bool macBundle in new[] { false, true })
            {
                string? why = ReleaseRules.Forbidden(path, macBundle);
                Assert.NotNull(why);
                Assert.Contains("is an audio file; a release carries none", why, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheRuleIsAboutTheFilesTypeNotAboutTheWordsOfItsName()
    {
        // the audio libraries, the media assembly, a document on sound and a name that only holds an extension's letters are released like any other file
        Assert.Null(ReleaseRules.Forbidden("libswresample.so.7"));
        Assert.Null(ReleaseRules.Forbidden("libavcodec.so.63"));
        Assert.Null(ReleaseRules.Forbidden("Tsuki.Media.dll"));
        Assert.Null(ReleaseRules.Forbidden("docs/sound.wav.md"));
        Assert.Null(ReleaseRules.Forbidden("licenses/FFmpeg-LGPL.txt"));
        Assert.Null(ReleaseRules.Forbidden("wav"));
        Assert.Null(ReleaseRules.Forbidden("notes.wave"));
    }

    [Fact]
    public void AFolderAMacBundleOrALinuxArchiveThatCarriesASoundIsRefused()
    {
        // a folder to release: the sound is the one problem, said by its path
        Directory.CreateDirectory(Path.Combine(_root, "sounds"));
        File.WriteAllBytes(Path.Combine(_root, "sounds", "achievement_chime.wav"), "RIFF\0\0\0\0WAVE"u8.ToArray());
        File.WriteAllText(Path.Combine(_root, "README.md"), "x");
        string problem = Assert.Single(ReleaseRules.ForbiddenFiles(_root)).Replace('\\', '/');
        Assert.StartsWith("sounds/achievement_chime.wav is an audio file", problem, StringComparison.Ordinal);

        // a sound in a Mac bundle's resources (said by its path below them, as a picture there is)
        string bundle = Path.Combine(_root, "Tsukihime.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllBytes(Path.Combine(bundle, "Contents", "Resources", "chime.flac"), "fLaC"u8.ToArray());
        Assert.StartsWith("chime.flac is an audio file", Assert.Single(ReleaseRules.ForbiddenFiles(bundle, macBundle: true)), StringComparison.Ordinal);

        // an entry of the Linux archive, for a release and for an update a launcher installs
        const UnixFileMode plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        foreach (ReleaseRuleSet rules in Enum.GetValues<ReleaseRuleSet>())
        {
            string? why = ReleaseRules.LinuxTarEntryProblem("sounds/achievement_chime.ogg", TarEntryType.RegularFile, plain, rules);
            Assert.NotNull(why);
            Assert.Contains("is an audio file", why, StringComparison.Ordinal);
        }
    }
}
