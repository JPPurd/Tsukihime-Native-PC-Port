// Connected game controllers by name (package K17; the probe was replaced in v0.1.9). It reads Windows' Raw Input device list, which only reads the list Windows
// keeps. The legacy joystick API used before ran the controller vendor's driver code inside the launcher, and a native fault there ended the launcher at start
// (twice in 11 days on the owner's PC); no try can catch that. A pad is a HID top-level collection of usage page 1 (generic desktop) with usage 4 (joystick),
// 5 (gamepad) or 8 (multi-axis controller); XInput pads are in the list too ("IG_" in their path). The collections of different kinds of one device are one pad,
// those of the same kind are one pad each (vJoy, multi-player adapters). Its name is the one Windows keeps in the registry for its VID / PID, else the product
// string the device reports, else "Game controller". The game itself uses SDL and finds pads on its own.
//
// The list is only ever a list of names (FIXES D12 P-1; no pad was at hand to try the probe on). A caller that finds a probe running gets the last list at once
// instead of waiting behind it, and the launcher reads the list beside its refresh (ControllerList): a device that is slow to name itself, or never does, or a
// probe that throws, costs the names on the Controls page and nothing else.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Tsuki.Launcher.Core;

/// <summary>Lists the game controllers that are plugged in.</summary>
public static partial class Controllers
{
    /// <summary>The name of a pad whose name Windows does not know.</summary>
    internal const string Fallback = "Game controller";

    internal const uint RimTypeMouse = 0;
    internal const uint RimTypeKeyboard = 1;
    internal const uint RimTypeHid = 2;
    internal const uint RidiDeviceName = 0x20000007;
    internal const uint RidiDeviceInfo = 0x2000000b;
    internal const ushort GenericDesktop = 0x01;
    internal const ushort Joystick = 0x04;
    internal const ushort Gamepad = 0x05;
    internal const ushort MultiAxisController = 0x08;

    private const uint Failed = uint.MaxValue;
    private const int ErrorInsufficientBuffer = 122;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    // USB allows 126 UTF-16 units and the terminator; a larger buffer makes some HID drivers refuse the call
    private const int ProductStringChars = 127;

    /// <summary>The turn of the probes of Windows' own list (<see cref="Connected"/>).</summary>
    private static readonly ProbeTurn Windows = new();

    /// <summary>One device of Windows' Raw Input list: its type (0 mouse, 1 keyboard, 2 HID), its path and, for a HID collection, its ids and usage.</summary>
    internal readonly record struct RawDevice(uint Type, string Path, uint VendorId, uint ProductId, ushort UsagePage, ushort Usage);

    /// <summary>One read of the list: how many devices Windows listed, and those whose details and path could be read.</summary>
    internal sealed record RawList(int Listed, IReadOnlyList<RawDevice> Read);

    /// <summary>
    /// Whose turn it is to probe, and the list the last finished probe gave (empty before the first). One probe runs at a time; a caller that finds one running
    /// does not wait behind it, so a device that never answers its name holds one worker thread, not one per refresh of the launcher.
    /// </summary>
    internal sealed class ProbeTurn
    {
        private volatile IReadOnlyList<string> _last = [];

        /// <summary>Held by the probe that runs.</summary>
        internal Lock Gate { get; } = new();

        /// <summary>The list of the last probe that finished.</summary>
        internal IReadOnlyList<string> Last
        {
            get => _last;
            set => _last = value;
        }
    }

    /// <summary>
    /// The names of the connected controllers, one per pad (empty when none is, or when Windows does not answer). One probe runs at a time; a caller that comes
    /// while one runs gets the last list at once.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Connected() => Probe(Windows, () => ReadRawList().Read, OemName, ProductString);

    /// <summary>
    /// The probe behind <see cref="Connected"/>, with its turn and its sources passed in. A caller that finds a probe running returns at once with the last
    /// list; an exception of a source other than a missing Windows call leaves the last list as it was and reaches the caller.
    /// </summary>
    internal static IReadOnlyList<string> Probe(ProbeTurn turn, Func<IReadOnlyList<RawDevice>> devices, Func<uint, uint, string?> oemName, Func<string, string?> productString)
    {
        if (!turn.Gate.TryEnter())
        {
            return turn.Last;
        }

        try
        {
            return turn.Last = Names(devices(), oemName, productString);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return turn.Last = [];
        }
        finally
        {
            turn.Gate.Exit();
        }
    }

    /// <summary>
    /// The game controllers among <paramref name="devices"/>, in their order, one name per pad. Collections of different kinds on one device (a gamepad and a
    /// joystick collection) are the parts of one pad and get one name. Collections of the same kind on one device are pads of their own, as the old list and the
    /// game count them: each stick of vJoy, each port of a two- or four-player adapter. So the n-th collection of each kind on a device belongs to its n-th pad.
    /// </summary>
    internal static IReadOnlyList<string> Names(IEnumerable<RawDevice> devices, Func<uint, uint, string?> oemName, Func<string, string?> productString)
    {
        var collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ofKind = new Dictionary<(string Device, ushort Usage), int>();
        var pads = new HashSet<(string Device, int Nth)>();
        var names = new List<string>();
        foreach (RawDevice device in devices)
        {
            if (!IsGameController(device) || !collections.Add(device.Path))
            {
                continue;
            }

            string key = DeviceKey(device.Path);
            int nth = ofKind.GetValueOrDefault((key, device.Usage));
            ofKind[(key, device.Usage)] = nth + 1;
            if (pads.Add((key, nth)))
            {
                names.Add(Clean(oemName(device.VendorId, device.ProductId)) ?? Clean(productString(device.Path)) ?? Fallback);
            }
        }

        return names;
    }

    /// <summary>A HID collection of a joystick, a gamepad or a multi-axis controller.</summary>
    internal static bool IsGameController(RawDevice device) =>
        device.Type == RimTypeHid && device.UsagePage == GenericDesktop && device.Usage is Joystick or Gamepad or MultiAxisController;

    /// <summary>
    /// The device a collection belongs to: its path without the interface class and, for one of several collections ("&amp;Col02" in the hardware id), without the
    /// collection's part (the last part of the instance id), upper case. <c>\\?\HID#VID_054C&amp;PID_09CC&amp;MI_03&amp;Col02#8&amp;2A6C1B5C&amp;0&amp;0001#{...}</c> gives
    /// <c>\\?\HID#VID_054C&amp;PID_09CC&amp;MI_03#8&amp;2A6C1B5C&amp;0</c>.
    /// </summary>
    internal static string DeviceKey(string path)
    {
        string upper = path.ToUpperInvariant();
        string[] parts = upper.Split('#');
        if (parts.Length < 3)
        {
            return upper;
        }

        string hardware = parts[1];
        string instance = parts[2];
        string single = CollectionPart().Replace(hardware, string.Empty);
        if (single.Length != hardware.Length)
        {
            int last = instance.LastIndexOf('&');
            instance = last > 0 ? instance[..last] : instance;
        }

        return parts[0] + "#" + single + "#" + instance;
    }

    /// <summary>A device as RIDI_DEVICEINFO describes it, with its path; the union's HID part is read only for a HID device. Null without a path.</summary>
    internal static RawDevice? Parse(in RidDeviceInfo info, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return info.Type == RimTypeHid
            ? new RawDevice(info.Type, path, info.VendorId, info.ProductId, info.UsagePage, info.Usage)
            : new RawDevice(info.Type, path, 0, 0, 0, 0);
    }

    /// <summary>The path RIDI_DEVICENAME wrote: up to its terminator, with the NT prefix <c>\??\</c> of old Windows versions made the Win32 one <c>\\?\</c>. Null when empty.</summary>
    internal static string? PathFrom(ReadOnlySpan<char> buffer)
    {
        string path = Terminated(buffer).Trim();
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = @"\\?\" + path[4..];
        }

        return path.Length == 0 ? null : path;
    }

    /// <summary>Windows' Raw Input list: every device, with its details and path (user32 only; no device is opened).</summary>
    [SupportedOSPlatform("windows")]
    internal static RawList ReadRawList()
    {
        RawInputDeviceList[] list = RawHandles();
        var read = new List<RawDevice>(list.Length);
        foreach (RawInputDeviceList entry in list)
        {
            RidDeviceInfo info = EmptyInfo();
            uint size = info.Size;
            uint got = GetRawInputDeviceInfo(entry.Device, RidiDeviceInfo, ref info, ref size);
            if (got == Failed || got == 0)
            {
                continue;
            }

            if (Parse(info, DeviceName(entry.Device)) is { } device)
            {
                read.Add(device);
            }
        }

        return new RawList(list.Length, read);
    }

    /// <summary>A RID_DEVICE_INFO to be filled, its cbSize set to the struct's size: without it user32 refuses the call (with 0, Windows 11 read no device).</summary>
    internal static RidDeviceInfo EmptyInfo() => new() { Size = (uint)Marshal.SizeOf<RidDeviceInfo>() };

    /// <summary>The name Windows keeps for a VID / PID (HKLM, then HKCU), or null.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? OemName(uint vendorId, uint productId)
    {
        string key = $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vendorId:X4}&PID_{productId:X4}";
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using RegistryKey? oem = hive.OpenSubKey(key);
                if (Clean(oem?.GetValue("OEMName") as string) is { } name)
                {
                    return name;
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // the next hive, then the product string
            }
        }

        return null;
    }

    /// <summary>The product string the device reports (hid.dll), or null. The device is opened without read or write access and closed at once.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? ProductString(string path)
    {
        try
        {
            using SafeFileHandle device = CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (device.IsInvalid)
            {
                return null;
            }

            var buffer = new char[ProductStringChars];
            return HidD_GetProductString(device, buffer, (uint)(buffer.Length * sizeof(char))) ? Clean(Terminated(buffer)) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? Clean(string? name)
    {
        string? trimmed = name?.Trim().Trim('\0').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Terminated(ReadOnlySpan<char> buffer)
    {
        int end = buffer.IndexOf('\0');
        return (end >= 0 ? buffer[..end] : buffer).ToString();
    }

    private static RawInputDeviceList[] RawHandles()
    {
        uint entrySize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, entrySize) == Failed || count == 0)
            {
                return [];
            }

            // room for a device or two that arrive between the two calls
            var list = new RawInputDeviceList[count + 4];
            uint capacity = (uint)list.Length;
            uint got = GetRawInputDeviceList(list, ref capacity, entrySize);
            if (got != Failed)
            {
                return list[..(int)Math.Min(got, (uint)list.Length)];
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                return [];
            }
        }

        return [];
    }

    private static string? DeviceName(nint device)
    {
        // RIDI_DEVICENAME counts in characters, not bytes
        uint chars = 0;
        if (GetRawInputDeviceInfo(device, RidiDeviceName, null, ref chars) != 0 || chars == 0 || chars > 32768)
        {
            return null;
        }

        var buffer = new char[chars + 1];
        uint capacity = (uint)buffer.Length;
        return GetRawInputDeviceInfo(device, RidiDeviceName, buffer, ref capacity) == Failed ? null : PathFrom(buffer);
    }

    [GeneratedRegex(@"&COL[0-9A-F]+(?=&|$)", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionPart();

    /// <summary>RAWINPUTDEVICELIST: a handle and the device type (16 bytes in a 64-bit process, 8 in a 32-bit one).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDeviceList
    {
        public nint Device;
        public uint Type;
    }

    /// <summary>
    /// RID_DEVICE_INFO, 32 bytes: cbSize, dwType, then a union whose largest member is the keyboard's six DWORDs. Only the HID member is named here
    /// (dwVendorId, dwProductId, dwVersionNumber, usUsagePage, usUsage); it means something only when <see cref="Type"/> is <see cref="RimTypeHid"/>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct RidDeviceInfo
    {
        [FieldOffset(0)]
        public uint Size;
        [FieldOffset(4)]
        public uint Type;
        [FieldOffset(8)]
        public uint VendorId;
        [FieldOffset(12)]
        public uint ProductId;
        [FieldOffset(16)]
        public uint VersionNumber;
        [FieldOffset(20)]
        public ushort UsagePage;
        [FieldOffset(22)]
        public ushort Usage;
    }

    // DllImport, not LibraryImport: the launcher compiles without unsafe code (LauncherApp.NativeMethods)
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? list, ref uint count, uint entrySize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, ref RidDeviceInfo data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, [Out] char[]? data, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, [Out] char[] buffer, uint bufferBytes);
}

/// <summary>
/// The controller list as a launcher window shows it, read beside the window's refresh and never in its way (FIXES D12 P-1). A reading runs on a worker thread
/// of its own; its list is taken on the thread that asked for it (the window's) as soon as it is there, however late. Nothing a reading does can cost more
/// than the list: every exception of it is caught here and said once in the log, and the list stays what it was.
/// </summary>
/// <param name="probe">Reads the names (<see cref="Controllers.Connected"/>; a stand-in in the tests). It runs on a worker thread.</param>
/// <param name="log">Takes the one line about a reading that failed (the launcher's log).</param>
public sealed class ControllerList(Func<IReadOnlyList<string>> probe, Action<string> log)
{
    private int _failureSaid;

    /// <summary>
    /// How long a refresh waits at its end for the reading it started. On a normal PC the names are there long before, so whoever awaits the refresh (the
    /// launcher's --render and --selftest) finds them; a list that takes longer is taken when it comes.
    /// </summary>
    public TimeSpan RefreshWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The names of the last reading that ended well (empty before the first).</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Raised each time a reading's list was taken, on the thread that asked for the reading.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts a reading and returns at once. The task ends when the list was taken, or when the reading failed and the list was left as it was; it never
    /// faults because of the probe, nor because of a log that cannot take the line about it.
    /// </summary>
    public async Task ReadAsync()
    {
        try
        {
            // no ConfigureAwait(false): the list is taken, and Changed raised, on the thread that asked
            Names = await Task.Run(probe);
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            // every exception: a list of names is never worth a refresh, a box or a toast
            if (Interlocked.Exchange(ref _failureSaid, 1) == 0)
            {
                try
                {
                    log($"controllers: the list could not be read ({e.GetType().Name}: {e.Message}); the Controls page keeps the list it had. Said once per start.");
                }
                catch (Exception)
                {
                    // a log that throws too: the line is lost and nothing else; no refresh looks at this task, so a fault here would only ever be left behind
                }
            }
        }
    }

    /// <summary>The last step of a refresh: waits for the reading it started, but no longer than <see cref="RefreshWait"/>. A reading that failed does not make it throw.</summary>
    public Task WaitBrieflyAsync(Task reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.IsCompleted ? Task.CompletedTask : Task.WhenAny(reading, Task.Delay(RefreshWait));
    }
}
