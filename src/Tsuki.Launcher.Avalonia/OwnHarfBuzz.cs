using System.Runtime.InteropServices;

namespace Tsuki.Launcher.Avalonia;

/// <summary>
/// Linux: the launcher's own libHarfBuzzSharp.so (HarfBuzz 8.3.1) is loaded before anything else, with every symbol bound at once. Its inner calls go through
/// the loader, and the loader looks at the libraries every program part shares before it looks at the library itself. FreeType 2.14 and newer loads the
/// system's libharfbuzz.so.0 into that shared set the first time it is used, which happens inside the launcher when fontconfig has to scan a font folder it has
/// no cache for. Loaded after that, the launcher's library bound its own calls to the system's HarfBuzz, handed it an 8.3.1 object, and glibc ended the
/// launcher ("free(): invalid pointer", exit 134, no window, no crash log). Loaded first and bound at once, it calls itself, whatever arrives later. It is not
/// put into the shared set, so the system's FreeType keeps the system's HarfBuzz. Not on macOS, whose libraries name the library a symbol comes from.
/// </summary>
internal static class OwnHarfBuzz
{
    /// <summary>The library's file name in the launcher's folder.</summary>
    internal const string FileName = "libHarfBuzzSharp.so";

    /// <summary>dlopen's RTLD_NOW: every symbol is bound before dlopen returns. RTLD_GLOBAL (0x100) is left out.</summary>
    private const int BindNow = 2;

    /// <summary>
    /// Loads the library, first thing in Main. Zero when there is nothing to do or it could not be done (another system, no such file, a C library without
    /// dlopen): the launcher then starts as it always did.
    /// </summary>
    internal static IntPtr BindFirst() =>
        OperatingSystem.IsLinux() ? BindFirst(AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string, AppContext.BaseDirectory) : IntPtr.Zero;

    internal static IntPtr BindFirst(string? searchDirectories, string baseDirectory)
    {
        try
        {
            return Find(searchDirectories, baseDirectory) is { } file ? DlOpen(file, BindNow) : IntPtr.Zero;
        }
        catch (Exception)
        {
            // glibc before 2.34 keeps dlopen in libdl (such a system has no FreeType 2.14 either). Whatever else it is: this is Main's first statement, before
            // any crash hook, and a launcher that starts as it always did is the worst that may follow
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// The file the runtime loads for HarfBuzzSharp: in the folders the host gives the runtime for native libraries (the launcher's own folder in a release,
    /// runtimes/linux-x64/native below it in a development build), then in the launcher's folder. Null when it is in none of them.
    /// </summary>
    internal static string? Find(string? searchDirectories, string baseDirectory) =>
        (searchDirectories ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(baseDirectory)
            .Select(directory => Path.Combine(directory, FileName))
            .FirstOrDefault(File.Exists);

    [DllImport("libc", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr DlOpen(string file, int mode);
}
