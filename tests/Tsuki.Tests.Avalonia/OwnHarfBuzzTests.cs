using System.Reflection;
using System.Runtime.InteropServices;
using Tsuki.Launcher.Avalonia;

namespace Tsuki.Tests.Avalonia;

/// <summary>
/// The Linux launcher loads its own libHarfBuzzSharp.so first, bound to itself (<see cref="OwnHarfBuzz"/>): which file that is, and that a start never depends
/// on it. What it is for is seen by UnixLauncherInstanceTests.TheWindowPathStartsWhenFontconfigScansAFontFolderInsideTheLauncher.
/// </summary>
public sealed class OwnHarfBuzzTests : IDisposable
{
    private readonly string _root = TestFolders.Temp("tsuki-v019-harfbuzz-");

    public OwnHarfBuzzTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void TheLibraryIsLookedForWhereTheRuntimeLooksAndThenInTheLaunchersFolder()
    {
        string first = Folder("first"), second = Folder("second"), launcher = Folder("launcher");
        // the host's list ends with a separator and may name a folder that is not there
        string search = string.Join(Path.PathSeparator, first, Path.Combine(_root, "missing"), second) + Path.PathSeparator;
        Assert.Null(OwnHarfBuzz.Find(search, launcher));
        Assert.Null(OwnHarfBuzz.Find(null, launcher));

        File.WriteAllText(Path.Combine(launcher, OwnHarfBuzz.FileName), "stand-in");
        Assert.Equal(Path.Combine(launcher, OwnHarfBuzz.FileName), OwnHarfBuzz.Find(search, launcher));
        Assert.Equal(Path.Combine(launcher, OwnHarfBuzz.FileName), OwnHarfBuzz.Find(null, launcher));
        Assert.Equal(Path.Combine(launcher, OwnHarfBuzz.FileName), OwnHarfBuzz.Find(string.Empty, launcher));

        // a development build keeps it in runtimes/linux-x64/native, which the host names: that one is the runtime's
        File.WriteAllText(Path.Combine(second, OwnHarfBuzz.FileName), "stand-in");
        Assert.Equal(Path.Combine(second, OwnHarfBuzz.FileName), OwnHarfBuzz.Find(search, launcher));
        File.WriteAllText(Path.Combine(first, OwnHarfBuzz.FileName), "stand-in");
        Assert.Equal(Path.Combine(first, OwnHarfBuzz.FileName), OwnHarfBuzz.Find(search, launcher));
    }

    [Fact]
    public void AMissingLibraryOrOneThatDoesNotLoadIsIgnored()
    {
        string launcher = Folder("launcher");
        Assert.Equal(IntPtr.Zero, OwnHarfBuzz.BindFirst(null, launcher));
        Assert.Equal(IntPtr.Zero, OwnHarfBuzz.BindFirst(Path.Combine(_root, "missing") + Path.PathSeparator, Path.Combine(_root, "missing")));

        // a file that is no library: dlopen refuses it on Linux and macOS, and Windows has no dlopen to ask; nothing is thrown anywhere
        File.WriteAllText(Path.Combine(launcher, OwnHarfBuzz.FileName), "not a library");
        Assert.Equal(IntPtr.Zero, OwnHarfBuzz.BindFirst(null, launcher));
    }

    [Fact]
    public void OnLinuxTheLauncherLoadsTheVeryLibraryTheRuntimeUses()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("only the Linux launcher loads it (ElsewhereNothingIsLoaded)"); }
        IntPtr own = OwnHarfBuzz.BindFirst();
        Assert.NotEqual(IntPtr.Zero, own);
        // the loader gives one handle per loaded library: what the runtime loads for HarfBuzzSharp's calls is the library that is there already, so its calls
        // go to the one that was bound to itself. Another handle would mean two copies, and the runtime's would be bound late as before
        Assert.True(NativeLibrary.TryLoad("libHarfBuzzSharp", Assembly.Load("HarfBuzzSharp"), null, out IntPtr runtime), "the runtime does not find libHarfBuzzSharp");
        Assert.Equal(own, runtime);
        Assert.Equal(own, OwnHarfBuzz.BindFirst());
    }

    [Fact]
    public void ElsewhereNothingIsLoaded()
    {
        if (OperatingSystem.IsLinux()) { Assert.Skip("Linux loads it (OnLinuxTheLauncherLoadsTheVeryLibraryTheRuntimeUses)"); }
        Assert.Equal(IntPtr.Zero, OwnHarfBuzz.BindFirst());
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
}
