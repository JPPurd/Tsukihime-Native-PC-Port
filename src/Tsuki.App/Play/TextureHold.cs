// How long the game keeps the previous picture on the screen while the textures of the next frame are decoded (issue #13).
namespace Tsuki.App.Play;

/// <summary>
/// The rule of the hold for textures. A frame whose textures are not all resident is not drawn: the picture before it stays, so that no half-loaded picture is
/// shown. That hold had no end: a frame whose textures could never all be resident (more of them than the cache's cap, or a tile that fails to decode in a
/// damaged game file) froze the game on the picture before it. Now the hold ends after a limit, the frame is drawn with what is there, and the log says so.
/// </summary>
internal static class TextureHold
{
    /// <summary>The longest hold while tiles are still being decoded or uploaded (a scene change on a slow disk takes a second or two).</summary>
    public const double LimitMs = 8000;

    /// <summary>The longest hold while nothing is on its way: the missing textures will not come.</summary>
    public const double IdleLimitMs = 1000;

    /// <summary>The most one pass of the loop adds to a hold's age: time in which the loop did not run (the computer slept, the title bar was held) is not time waited for textures.</summary>
    public const double PassCapMs = 100;

    /// <summary>A frame above the cap is logged again for the same script once it is this much larger than the one logged.</summary>
    public const long OverCapStepBytes = 32L << 20;

    /// <summary>The age of a hold after a pass of the loop that came <paramref name="sincePassMs"/> after the pass before it.</summary>
    public static double Age(double ageMs, double sincePassMs) => ageMs + Math.Clamp(sincePassMs, 0, PassCapMs);

    /// <summary>Whether a frame of <paramref name="frameBytes"/> is logged as above the cap: the first of a script, and again when it has grown by <see cref="OverCapStepBytes"/>.</summary>
    public static bool SayOverCap(long frameBytes, long capacityBytes, int script, int loggedScript, long loggedBytes) =>
        frameBytes > capacityBytes && (script != loggedScript || frameBytes >= loggedBytes + OverCapStepBytes);

    /// <summary>Whether a hold of <paramref name="heldMs"/> with <paramref name="pending"/> tiles queued, being decoded or waiting for their upload is given up.</summary>
    public static bool GiveUp(double heldMs, int pending) => heldMs >= (pending == 0 ? IdleLimitMs : LimitMs);

    /// <summary>The log line of a hold that was given up.</summary>
    public static string GaveUpLine(double heldMs, int missing, int pending, int script, long residentBytes, long capacityBytes) =>
        $"gave up holding for textures after {heldMs:F0} ms: {missing} of this frame's textures are not resident and {(pending == 0 ? "none is on its way" : $"{pending} tiles are still in flight")} " +
        $"(script {script}, cache {residentBytes >> 20} of {capacityBytes >> 20} MiB); the frame is drawn without them";

    /// <summary>The log line of a frame whose textures are larger than the cache's cap.</summary>
    public static string OverCapLine(long frameBytes, long capacityBytes, int script) =>
        $"this frame draws {frameBytes >> 20} MiB of textures, more than the cache's {capacityBytes >> 20} MiB cap: they all stay resident for it (script {script})";
}
