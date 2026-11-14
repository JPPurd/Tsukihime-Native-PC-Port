// v0.1.9: the Windows launcher's controller probe (src/Tsuki.Launcher/Core/Controllers.cs, compiled into these tests). The legacy joystick API it used ran the
// vendor's driver code inside the launcher and a native fault there ended it; the probe now reads Windows' Raw Input list. Here: the layouts of the two user32
// structs, the reading of what Windows writes, which collections are pads, one name per pad (the parts of one pad once, each pad of a multi-pad device on its
// own), the order of the names, one probe at a time and no caller waiting behind it (FIXES D12 P-1), the guard that no launcher file names the old API again,
// and (on Windows) the real list read on this machine, with the name asked from every HID device of it and from every controller key of its registry (FIXES
// D12 P-2: nobody had a pad to try, so the calls that open a device run on whatever HID devices the machine has; a keyboard or a mouse on another stack is
// never sent the HID request, and GitHub's hosted runner, whose devices nobody can look at, is not asked). The device records are made up; no device is needed.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Tsuki.Launcher.Core;
using RawDevice = Tsuki.Launcher.Core.Controllers.RawDevice;

namespace Tsuki.Tests.Synthetic.Launcher;

public sealed partial class ControllersTests
{
    private const string HidClass = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static RawDevice Hid(string path, ushort usage, ushort page = Controllers.GenericDesktop, uint vid = 0x054C, uint pid = 0x09CC) =>
        new(Controllers.RimTypeHid, path, vid, pid, page, usage);

    private static string HidPath(string hardware, string instance) => $@"\\?\HID#{hardware}#{instance}#{HidClass}";

    private static IReadOnlyList<string> NamesOf(params RawDevice[] devices) => Controllers.Names(devices, (_, _) => null, _ => null);

    private static Controllers.RidDeviceInfo Info(uint type, params (int Offset, uint Value)[] dwords)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), type);
        foreach ((int offset, uint value) in dwords)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        }

        return MemoryMarshal.Read<Controllers.RidDeviceInfo>(bytes);
    }

    // -- the structs and what Windows writes into them ----------------------------------------------------------------

    [Fact]
    public void TheTwoStructsHaveTheLayoutsOfUser32()
    {
        // RID_DEVICE_INFO: cbSize, dwType, then the union, whose largest member is the keyboard's six DWORDs: 32 bytes whatever the member
        Assert.Equal(32, Marshal.SizeOf<Controllers.RidDeviceInfo>());
        Assert.Equal(
            [0, 4, 8, 12, 16, 20, 22],
            new[] { "Size", "Type", "VendorId", "ProductId", "VersionNumber", "UsagePage", "Usage" }.Select(f => (int)Marshal.OffsetOf<Controllers.RidDeviceInfo>(f)));

        // cbSize is set before the call: with 0 Windows refuses it for every device (the real reading below fails then too, on a machine that has devices)
        Assert.Equal(32u, Controllers.EmptyInfo().Size);
        Assert.Equal(new Controllers.RidDeviceInfo { Size = 32 }, Controllers.EmptyInfo());

        // RAWINPUTDEVICELIST: a HANDLE and a DWORD, padded to the handle's alignment (16 bytes in a 64-bit process, 8 in a 32-bit one)
        Assert.Equal(2 * IntPtr.Size, Marshal.SizeOf<Controllers.RawInputDeviceList>());
        Assert.Equal(IntPtr.Size, (int)Marshal.OffsetOf<Controllers.RawInputDeviceList>("Type"));

        // winuser.h
        Assert.Equal((0x20000007u, 0x2000000bu), (Controllers.RidiDeviceName, Controllers.RidiDeviceInfo));
        Assert.Equal((0u, 1u, 2u), (Controllers.RimTypeMouse, Controllers.RimTypeKeyboard, Controllers.RimTypeHid));
    }

    [Fact]
    public void AHidRecordIsReadWithItsIdsAndUsage()
    {
        string path = HidPath("VID_054C&PID_09CC&MI_03", "8&2a6c1b5c&0&0000");

        // dwVendorId 0x054C, dwProductId 0x09CC, dwVersionNumber 0x0100, usUsagePage 1 and usUsage 5 in the DWORD at 20
        var info = Info(Controllers.RimTypeHid, (8, 0x054C), (12, 0x09CC), (16, 0x0100), (20, 0x0005_0001));

        Assert.Equal(new RawDevice(Controllers.RimTypeHid, path, 0x054C, 0x09CC, 1, 5), Controllers.Parse(info, path));
        Assert.True(Controllers.IsGameController(Controllers.Parse(info, path)!.Value));
        Assert.Null(Controllers.Parse(info, null));
        Assert.Null(Controllers.Parse(info, string.Empty));
    }

    [Fact]
    public void AKeyboardOrAMouseIsNeverReadAsAHidCollection()
    {
        // the union's bytes mean something else for them: a keyboard whose dwNumberOfFunctionKeys (offset 20) is 0x00050001 would read as page 1, usage 5
        string path = HidPath("VID_046D&PID_C31C&MI_00", "7&1f00a6b&0&0000");
        var keyboard = Info(Controllers.RimTypeKeyboard, (8, 4), (12, 0), (16, 1), (20, 0x0005_0001), (24, 3), (28, 101));
        var mouse = Info(Controllers.RimTypeMouse, (8, 0x054C), (12, 0x09CC), (16, 0x0100), (20, 0x0004_0001));

        Assert.Equal(new RawDevice(Controllers.RimTypeKeyboard, path, 0, 0, 0, 0), Controllers.Parse(keyboard, path));
        Assert.Equal(new RawDevice(Controllers.RimTypeMouse, path, 0, 0, 0, 0), Controllers.Parse(mouse, path));
        Assert.Empty(Controllers.Names([Controllers.Parse(keyboard, path)!.Value, Controllers.Parse(mouse, path)!.Value], (_, _) => "pad", _ => "pad"));
    }

    [Fact]
    public void ThePathIsReadUpToItsTerminatorAndTheNtPrefixIsMadeWin32s()
    {
        string path = HidPath("VID_045E&PID_02FF&IG_00", "9&3b6e2a1&0&0000");
        Assert.Equal(path, Controllers.PathFrom((path + "\0\0garbage").ToCharArray()));
        Assert.Equal(path, Controllers.PathFrom(path.ToCharArray()));
        Assert.Equal(path, Controllers.PathFrom((@"\??\" + path[4..] + "\0").ToCharArray()));
        Assert.Null(Controllers.PathFrom("\0path".ToCharArray()));
        Assert.Null(Controllers.PathFrom(new char[8]));
    }

    // -- which devices, and how many --------------------------------------------------------------------------------

    [Fact]
    public void OnlyJoysticksGamepadsAndMultiAxisControllersAreListed()
    {
        RawDevice[] devices =
        [
            new(Controllers.RimTypeMouse, HidPath("VID_046D&PID_C077", "7&aa&0&0000"), 0, 0, 0, 0),
            new(Controllers.RimTypeKeyboard, HidPath("VID_046D&PID_C31C&MI_00", "7&bb&0&0000"), 0, 0, 0, 0),
            Hid(HidPath("VID_1111&PID_0001", "7&01&0&0000"), usage: 0x01, page: 0x0C),   // consumer control
            Hid(HidPath("VID_1111&PID_0002", "7&02&0&0000"), usage: 0x06),               // a keyboard collection
            Hid(HidPath("VID_1111&PID_0003", "7&03&0&0000"), usage: 0x80),               // system control
            Hid(HidPath("VID_1111&PID_0004", "7&04&0&0000"), usage: 0x05, page: 0xFF00), // vendor page, usage 5
            Hid(HidPath("VID_2222&PID_0004", "7&05&0&0000"), usage: Controllers.Joystick),
            Hid(HidPath("VID_2222&PID_0005", "7&06&0&0000"), usage: Controllers.Gamepad),
            Hid(HidPath("VID_2222&PID_0008", "7&07&0&0000"), usage: Controllers.MultiAxisController),
            Hid(HidPath("VID_2222&PID_0009", "7&08&0&0000"), usage: 0x09),
        ];

        var named = Controllers.Names(devices, (_, _) => null, path => path.Split('#')[1]);

        Assert.Equal(["VID_2222&PID_0004", "VID_2222&PID_0005", "VID_2222&PID_0008"], named);
    }

    [Fact]
    public void ThePartsOfOnePadAreListedOnceAndTwoPadsOfAModelTwice()
    {
        // one pad, two top-level collections of different kinds (a gamepad and a joystick): the same instance but for the collection's part
        string col1 = HidPath("VID_054C&PID_09CC&MI_03&Col01", "8&2a6c1b5c&0&0000");
        string col2 = HidPath("VID_054C&PID_09CC&MI_03&Col02", "8&2a6c1b5c&0&0001");
        Assert.Equal(Controllers.DeviceKey(col1), Controllers.DeviceKey(col2));
        Assert.Equal(@"\\?\HID#VID_054C&PID_09CC&MI_03#8&2A6C1B5C&0", Controllers.DeviceKey(col1));
        Assert.Single(NamesOf(Hid(col1, Controllers.Gamepad), Hid(col2, Controllers.Joystick)));
        Assert.Single(NamesOf(Hid(col2, Controllers.Joystick), Hid(col1, Controllers.MultiAxisController)));

        // the same over Bluetooth (its hardware id has its own "&" parts), and the case Windows writes does not matter
        string bt1 = HidPath("{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&09cc&Col01", "9&1f2e3d4c&0&0000");
        string bt2 = HidPath("{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&09cc&Col02", "9&1f2e3d4c&0&0001").ToUpperInvariant();
        Assert.Equal(Controllers.DeviceKey(bt1), Controllers.DeviceKey(bt2));
        Assert.Single(NamesOf(Hid(bt1, Controllers.Gamepad), Hid(bt2, Controllers.Joystick)));

        // one collection listed twice (Windows' case differs) is still one pad
        Assert.Single(NamesOf(Hid(col1, Controllers.Gamepad), Hid(col1.ToUpperInvariant(), Controllers.Gamepad)));

        // two pads of one model are two devices: another instance
        string other = HidPath("VID_054C&PID_09CC&MI_03", "8&99d0e1f&0&0000");
        string first = HidPath("VID_054C&PID_09CC&MI_03", "8&2a6c1b5c&0&0000");
        Assert.Equal(["Pad", "Pad"], Controllers.Names([Hid(first, Controllers.Gamepad), Hid(other, Controllers.Gamepad)], (_, _) => "Pad", _ => null));

        // a single collection keeps its whole instance id; collections of two pads stay apart
        Assert.Equal(@"\\?\HID#VID_054C&PID_09CC&MI_03#8&2A6C1B5C&0&0000", Controllers.DeviceKey(first));
        Assert.NotEqual(Controllers.DeviceKey(col1), Controllers.DeviceKey(HidPath("VID_054C&PID_09CC&MI_03&Col01", "8&77aa&0&0000")));
        Assert.Equal(2, NamesOf(Hid(col1, Controllers.Gamepad), Hid(HidPath("VID_054C&PID_09CC&MI_03&Col02", "8&77aa&0&0001"), Controllers.Gamepad)).Count);
    }

    [Fact]
    public void EachPadOfADeviceWithSeveralPadsIsListed()
    {
        // vJoy: each virtual stick is a joystick collection of one root device; the old list, DirectInput and the game's SDL count each one
        RawDevice[] vJoy = Enumerable.Range(1, 4)
            .Select(i => Hid(HidPath($"HIDCLASS&Col{i:D2}", $"1&2d595ca7&0&{i - 1:D4}"), Controllers.Joystick, vid: 0x1234, pid: 0xBEAD))
            .ToArray();
        Assert.Single(vJoy.Select(d => Controllers.DeviceKey(d.Path)).Distinct());
        string? VJoyName(uint vid, uint pid) => (vid, pid) == (0x1234, 0xBEAD) ? "vJoy Device" : null;
        Assert.Equal(["vJoy Device", "vJoy Device", "vJoy Device", "vJoy Device"], Controllers.Names(vJoy, VJoyName, _ => null));

        // a two-player adapter: one interface, a joystick collection per port
        RawDevice twin1 = Hid(HidPath("VID_0810&PID_0001&Col01", "7&1a2b3c4d&0&0000"), Controllers.Joystick, vid: 0x0810, pid: 0x0001);
        RawDevice twin2 = Hid(HidPath("VID_0810&PID_0001&Col02", "7&1a2b3c4d&0&0001"), Controllers.Joystick, vid: 0x0810, pid: 0x0001);
        Assert.Equal(Controllers.DeviceKey(twin1.Path), Controllers.DeviceKey(twin2.Path));
        Assert.Equal(["Twin USB Joystick", "Twin USB Joystick"], Controllers.Names([twin1, twin2], (_, _) => null, _ => "Twin USB Joystick"));

        // two pads on one interface, each a gamepad and a joystick collection, in whatever order Windows lists them: two pads
        string Part(int col) => HidPath($"VID_2DC8&PID_3106&Col{col:D2}", $"8&5e6f7a8b&0&{col - 1:D4}");
        Assert.Equal(2, NamesOf(
            Hid(Part(3), Controllers.Gamepad), Hid(Part(2), Controllers.Joystick), Hid(Part(1), Controllers.Gamepad), Hid(Part(4), Controllers.Joystick)).Count);

        // three alike and one of another kind: three pads
        Assert.Equal(3, NamesOf(vJoy[0], vJoy[1] with { Usage = Controllers.Gamepad }, vJoy[2], vJoy[3]).Count);

        // the names come in the order their pads first appear in the list
        RawDevice xinput = Hid(HidPath("VID_045E&PID_02FF&IG_00", "9&3b6e2a1&0&0000"), Controllers.Gamepad, vid: 0x045E, pid: 0x02FF);
        Assert.Equal(
            ["Xbox", "vJoy Device", "Twin USB Joystick", "vJoy Device", "Twin USB Joystick"],
            Controllers.Names(
                [xinput, vJoy[0], twin1, vJoy[1], twin2],
                (vid, pid) => VJoyName(vid, pid) ?? (vid == 0x045E ? "Xbox" : null),
                path => path.Contains("VID_0810", StringComparison.Ordinal) ? "Twin USB Joystick" : null));
    }

    [Fact]
    public void AnXInputPadCountsLikeAnyOther()
    {
        string xinput = HidPath("VID_045E&PID_02FF&IG_00", "9&3b6e2a1&0&0000");
        Assert.Equal(["Controller (Xbox One For Windows)"],
            Controllers.Names([Hid(xinput, Controllers.Gamepad, vid: 0x045E, pid: 0x02FF)], (vid, pid) => vid == 0x045E && pid == 0x02FF ? "Controller (Xbox One For Windows)" : null, _ => null));
    }

    // -- records shaped like those of real pads (FIXES D12 P-3: no pad was at hand; the path shapes and ids are public knowledge) --

    /// <summary>A HID collection as Windows hands it over: the bytes of its RID_DEVICE_INFO and its path, read by the probe's own reader.</summary>
    private static RawDevice Collection(string path, uint vid, uint pid, ushort page, ushort usage)
    {
        RawDevice? read = Controllers.Parse(Info(Controllers.RimTypeHid, (8, vid), (12, pid), (16, 0x0100), (20, ((uint)usage << 16) | page)), path);
        Assert.NotNull(read);
        Assert.Equal((vid, pid, page, usage), (read.Value.VendorId, read.Value.ProductId, read.Value.UsagePage, read.Value.Usage));
        return read.Value;
    }

    [Fact]
    public void TheRecordsOfRealPadsGiveOneNameEach()
    {
        // a Bluetooth pad's hardware id is the HID service's GUID and its ids in Bluetooth's own spelling, not VID_ / PID_
        const string bluetooth = "{00001124-0000-1000-8000-00805f9b34fb}";
        (string Pad, RawDevice[] Collections)[] pads =
        [
            // DualSense: on USB the pad is the HID interface MI_03 of a composite device (the others are its audio); on Bluetooth one collection
            ("DualSense, USB", [Collection(HidPath("VID_054C&PID_0CE6&MI_03", "8&1d2c3b4a&0&0000"), 0x054C, 0x0CE6, Controllers.GenericDesktop, Controllers.Gamepad)]),
            ("DualSense, Bluetooth", [Collection(HidPath(bluetooth + "_VID&0002054c_PID&0ce6", "9&2b3c4d5e&0&0000"), 0x054C, 0x0CE6, Controllers.GenericDesktop, Controllers.Gamepad)]),
            ("DualShock 4, USB", [Collection(HidPath("VID_054C&PID_09CC&MI_03", "8&2a6c1b5c&0&0000"), 0x054C, 0x09CC, Controllers.GenericDesktop, Controllers.Gamepad)]),

            // an Xbox pad as XInput lists it: one gamepad collection whose hardware id carries IG_00
            ("Xbox pad, XInput", [Collection(HidPath("VID_045E&PID_02FF&IG_00", "9&3b6e2a1&0&0000"), 0x045E, 0x02FF, Controllers.GenericDesktop, Controllers.Gamepad)]),

            // Switch Pro Controller on Bluetooth: one collection, a pad whichever of the two usages its report descriptor names
            ("Switch Pro Controller, Bluetooth, a gamepad", [Collection(HidPath(bluetooth + "_VID&0002057e_PID&2009", "8&3c4d5e6f&0&0000"), 0x057E, 0x2009, Controllers.GenericDesktop, Controllers.Gamepad)]),
            ("Switch Pro Controller, Bluetooth, a joystick", [Collection(HidPath(bluetooth + "_VID&0002057e_PID&2009", "8&4d5e6f7a&0&0000"), 0x057E, 0x2009, Controllers.GenericDesktop, Controllers.Joystick)]),

            // a pad with two top-level collections: Col01 the gamepad, Col02 the maker's own (vendor-defined page), which is no pad
            (
                "a pad with a vendor-defined second collection",
                [
                    Collection(HidPath("VID_0F0D&PID_00C1&MI_00&Col01", "7&5e6f7a8b&0&0000"), 0x0F0D, 0x00C1, Controllers.GenericDesktop, Controllers.Gamepad),
                    Collection(HidPath("VID_0F0D&PID_00C1&MI_00&Col02", "7&5e6f7a8b&0&0001"), 0x0F0D, 0x00C1, 0xFF00, 0x01),
                ]),
        ];

        foreach ((string pad, RawDevice[] collections) in pads)
        {
            // exactly one name: the device's own, or the registry's for the ids Windows reported (not the ones spelled in a Bluetooth path)
            Assert.Equal([pad], Controllers.Names(collections, (_, _) => null, _ => pad));
            Assert.Equal([pad], Controllers.Names(collections.Reverse(), (_, _) => null, _ => pad));
            Assert.Equal([$"{collections[0].VendorId:X4}:{collections[0].ProductId:X4}"], Controllers.Names(collections, (vid, pid) => $"{vid:X4}:{pid:X4}", _ => null));
            Assert.Equal([Controllers.Fallback], NamesOf(collections));
        }

        // all of them plugged in at once, between a keyboard and a mouse: one name each, in Windows' order
        RawDevice[] together =
        [
            new(Controllers.RimTypeKeyboard, HidPath("VID_046D&PID_C31C&MI_00", "7&1f00a6b&0&0000"), 0, 0, 0, 0),
            .. pads.SelectMany(p => p.Collections),
            new(Controllers.RimTypeMouse, HidPath("VID_046D&PID_C077", "7&aa&0&0000"), 0, 0, 0, 0),
        ];
        string Own(string path) => pads.Single(p => p.Collections.Any(c => c.Path == path)).Pad;
        Assert.Equal(pads.Select(p => p.Pad), Controllers.Names(together, (_, _) => null, Own));
    }

    // -- the names ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheNameIsTheRegistrysThenTheDevicesOwnThenGameController()
    {
        RawDevice registered = Hid(HidPath("VID_054C&PID_09CC", "8&01&0&0000"), Controllers.Gamepad, vid: 0x054C, pid: 0x09CC);
        RawDevice blankInRegistry = Hid(HidPath("VID_045E&PID_0B12", "8&02&0&0000"), Controllers.Gamepad, vid: 0x045E, pid: 0x0B12);
        RawDevice unknown = Hid(HidPath("VID_1234&PID_5678", "8&03&0&0000"), Controllers.Joystick, vid: 0x1234, pid: 0x5678);
        RawDevice blankEverywhere = Hid(HidPath("VID_1234&PID_9ABC", "8&04&0&0000"), Controllers.MultiAxisController, vid: 0x1234, pid: 0x9ABC);
        var asked = new List<string>();

        string? Oem(uint vid, uint pid)
        {
            asked.Add($"oem {vid:X4}&{pid:X4}");
            return (vid, pid) switch
            {
                (0x054C, 0x09CC) => " Wireless Controller ",
                (0x045E, 0x0B12) => "   ",
                (0x1234, 0x9ABC) => string.Empty,
                _ => null,
            };
        }

        string? Product(string path)
        {
            asked.Add("product " + path);
            return path == blankInRegistry.Path ? "Xbox Wireless Controller  " : path == blankEverywhere.Path ? " \0" : null;
        }

        var names = Controllers.Names([registered, blankInRegistry, unknown, blankEverywhere], Oem, Product);

        Assert.Equal(["Wireless Controller", "Xbox Wireless Controller", Controllers.Fallback, Controllers.Fallback], names);
        Assert.Equal("Game controller", Controllers.Fallback);

        // the device is not opened for a pad the registry names
        Assert.Equal(
            ["oem 054C&09CC", "oem 045E&0B12", "product " + blankInRegistry.Path, "oem 1234&5678", "product " + unknown.Path, "oem 1234&9ABC", "product " + blankEverywhere.Path],
            asked);
    }

    // -- one probe at a time, no caller waiting behind it, and a Windows that does not answer --------------------------

    [Fact]
    public void OneProbeRunsAtATimeAndNoCallerWaitsBehindIt()
    {
        RawDevice pad = Hid(HidPath("VID_054C&PID_09CC", "8&01&0&0000"), Controllers.Gamepad);
        var turn = new Controllers.ProbeTurn();
        var count = new object();
        int inside = 0;
        int most = 0;
        IReadOnlyList<RawDevice> Slow()
        {
            lock (count)
            {
                most = Math.Max(most, ++inside);
            }

            Thread.Sleep(60);
            lock (count)
            {
                inside--;
            }

            return [pad];
        }

        using var go = new ManualResetEventSlim();
        var answers = new System.Collections.Concurrent.ConcurrentBag<IReadOnlyList<string>>();
        var callers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            go.Wait();
            answers.Add(Controllers.Probe(turn, Slow, (_, _) => "Pad", _ => null));
        })).ToList();
        callers.ForEach(t => t.Start());
        go.Set();
        Assert.All(callers, t => Assert.True(t.Join(TimeSpan.FromSeconds(20)), "a probe did not end"));

        // each caller has the list its own probe read, or the last list (none had finished before: empty) when it found a probe running
        Assert.Equal(1, most);
        Assert.Equal(4, answers.Count);
        Assert.All(answers, a => Assert.True(a.Count == 0 || a.SequenceEqual(["Pad"]), string.Join(", ", a)));
        Assert.Contains(answers, a => a.Count == 1);
        Assert.Equal(["Pad"], turn.Last);
    }

    [Fact]
    public async Task ACallerThatFindsAProbeRunningGetsTheLastListAtOnce()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        RawDevice pad = Hid(HidPath("VID_054C&PID_09CC", "8&01&0&0000"), Controllers.Gamepad);
        RawDevice xbox = Hid(HidPath("VID_045E&PID_02FF&IG_00", "9&3b6e2a1&0&0000"), Controllers.Gamepad, vid: 0x045E, pid: 0x02FF);
        static string? Name(uint vid, uint pid) => vid == 0x045E ? "Xbox" : "Pad";
        var turn = new Controllers.ProbeTurn();

        // before any probe has finished the last list is empty; then it is the list of the last probe
        Assert.Empty(turn.Last);
        Assert.Equal(["Pad"], Controllers.Probe(turn, () => [pad], Name, _ => null));
        Assert.Equal(["Pad"], turn.Last);

        // a probe in the hands of a device that does not answer
        using var started = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        Task<IReadOnlyList<string>> held = Task.Run(
            () => Controllers.Probe(
                turn,
                () =>
                {
                    started.Set();
                    answer.Wait(cancel);
                    return [pad, xbox];
                },
                Name,
                _ => null),
            cancel);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(20), cancel), "the probe did not start");

            // callers that come meanwhile have the last list at once and their sources are never asked (a caller that waited behind the first would not return
            // before the test lets the device answer, which it does after this)
            int asked = 0;
            for (int i = 0; i < 3; i++)
            {
                IReadOnlyList<string> last = await Task.Run(
                    () => Controllers.Probe(
                        turn,
                        () =>
                        {
                            Interlocked.Increment(ref asked);
                            return [xbox];
                        },
                        Name,
                        _ => null),
                    cancel).WaitAsync(TimeSpan.FromSeconds(20), cancel);
                Assert.Equal(["Pad"], last);
            }

            Assert.Equal(0, Volatile.Read(ref asked));
            Assert.False(held.IsCompleted);
            Assert.Equal(["Pad"], turn.Last);
        }
        finally
        {
            answer.Set();
        }

        // the probe that ran has its own list, which is the last list from then on; the next caller probes again
        Assert.Equal(["Pad", "Xbox"], await held.WaitAsync(TimeSpan.FromSeconds(20), cancel));
        Assert.Equal(["Pad", "Xbox"], turn.Last);
        Assert.Equal(["Xbox"], Controllers.Probe(turn, () => [xbox], Name, _ => null));
        Assert.Equal(["Xbox"], turn.Last);
    }

    [Fact]
    public async Task AProbeThatThrowsKeepsTheLastListAndGivesItsTurnBack()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        RawDevice pad = Hid(HidPath("VID_054C&PID_09CC", "8&01&0&0000"), Controllers.Gamepad);
        var turn = new Controllers.ProbeTurn();
        Assert.Equal(["Pad"], Controllers.Probe(turn, () => [pad], (_, _) => "Pad", _ => null));

        // an exception of the list, of the registry name or of the product string reaches the caller (the launcher's list catches every one); the last list stays
        Assert.Throws<InvalidOperationException>(() => Controllers.Probe(turn, () => throw new InvalidOperationException("the list"), (_, _) => "Pad", _ => null));
        Assert.Throws<UnauthorizedAccessException>(() => Controllers.Probe(turn, () => [pad], (_, _) => throw new UnauthorizedAccessException("the registry"), _ => null));
        Assert.Throws<IOException>(() => Controllers.Probe(turn, () => [pad], (_, _) => null, _ => throw new IOException("the device")));
        Assert.Equal(["Pad"], turn.Last);

        // and the turn is free again: the next probe runs, also one of another thread (a turn left taken would hand it the last list)
        RawDevice other = Hid(HidPath("VID_054C&PID_09CC", "8&02&0&0000"), Controllers.Gamepad);
        IReadOnlyList<string> next = await Task.Run(() => Controllers.Probe(turn, () => [pad, other], (_, _) => "Pad", _ => null), cancel).WaitAsync(TimeSpan.FromSeconds(20), cancel);
        Assert.Equal(["Pad", "Pad"], next);
        Assert.Equal(["Pad", "Pad"], turn.Last);
    }

    [Fact]
    public void AWindowsWithoutTheRawInputCallsAnswersNoController()
    {
        var turn = new Controllers.ProbeTurn();
        Assert.Equal(["Pad"], Controllers.Probe(turn, () => [Hid(HidPath("VID_054C&PID_09CC", "8&01&0&0000"), Controllers.Gamepad)], (_, _) => "Pad", _ => null));
        Assert.Empty(Controllers.Probe(turn, () => throw new DllNotFoundException("user32.dll"), (_, _) => "Pad", _ => null));
        Assert.Empty(turn.Last);
        Assert.Empty(Controllers.Probe(turn, () => throw new EntryPointNotFoundException("GetRawInputDeviceList"), (_, _) => "Pad", _ => null));
        Assert.Empty(Controllers.Probe(new Controllers.ProbeTurn(), () => throw new DllNotFoundException("user32.dll"), (_, _) => "Pad", _ => null));
    }

    // -- the old API stays out --------------------------------------------------------------------------------------

    [Fact]
    public void NoLauncherFileNamesTheLegacyJoystickApi()
    {
        var scanned = new List<string>();
        var hits = new List<string>();
        foreach (string project in new[] { "Tsuki.Launcher", "Tsuki.Launcher.Avalonia" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(global::Tsuki.Tests.Synthetic.Repo.Src, project), "*", SearchOption.AllDirectories))
            {
                string relative = global::Tsuki.Tests.Synthetic.Repo.Relative(file);
                if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned.Add(relative);
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (LegacyJoystick().IsMatch(lines[i]))
                    {
                        hits.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.Contains("src/Tsuki.Launcher/Core/Controllers.cs", scanned);
        Assert.Contains("src/Tsuki.Launcher/LauncherModel.cs", scanned);
        Assert.True(scanned.Count > 60, $"only {scanned.Count} launcher files were read");
        Assert.True(hits.Count == 0, "the legacy joystick API runs the vendor's driver code in the launcher (v0.1.9):" + Environment.NewLine + string.Join(Environment.NewLine, hits));

        // and the guard sees what it must
        Assert.Matches(LegacyJoystick(), "    [DllImport(\"WINMM.dll\")]");
        Assert.Matches(LegacyJoystick(), "private static extern int joyGetPosEx(int id, ref JoyInfoEx info);");
        Assert.Matches(LegacyJoystick(), "int count = joyGetNumDevs();");
        Assert.Matches(LegacyJoystick(), "EntryPoint = \"joyGetDevCapsW\"");
        Assert.DoesNotMatch(LegacyJoystick(), "GetRawInputDeviceList");
    }

    [GeneratedRegex(@"winmm|joyGetPosEx|joyGetNumDevs|joyGetDevCaps", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyJoystick();
}

/// <summary>
/// The real probe on this machine (Windows only): it returns, it reads Windows' list with the sizes user32 wants, a missing device answers null, and every
/// HID device and every controller key of the registry the machine has is asked for its name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsControllersTests
{
    /// <summary>How long one device may take to answer before the test names it and fails; the suite never waits for a device that does not answer.</summary>
    private static readonly TimeSpan DeviceLimit = TimeSpan.FromSeconds(30);
    [Fact]
    public async Task TheRealProbeReturnsOneCleanNamePerController()
    {
        WindowsOnly();
        IReadOnlyList<string>? names = null;
        var probe = new Thread(() => names = Controllers.Connected()) { IsBackground = true };
        probe.Start();
        Assert.True(probe.Join(TimeSpan.FromSeconds(60)), "Controllers.Connected did not return within 60 s");
        Assert.NotNull(names);
        Assert.All(names, n => Assert.True(n.Length > 0 && n == n.Trim(), $"name '{n}'"));

        // two at once: one probes, and the other probes after it or has the last list at once; both have the list
        CancellationToken cancel = TestContext.Current.CancellationToken;
        IReadOnlyList<string>[] both = await Task.WhenAll(Task.Run(() => Controllers.Connected(), cancel), Task.Run(() => Controllers.Connected(), cancel)).WaitAsync(TimeSpan.FromSeconds(60), cancel);
        Assert.Equal(names, both[0]);
        Assert.Equal(names, both[1]);
    }

    [Fact]
    public void TheRawListIsReadWithTheSizesUser32Wants()
    {
        WindowsOnly();

        // how many devices Windows lists, asked here with sizeof(RAWINPUTDEVICELIST) written out (a handle and a DWORD, padded): the probe must see them all
        uint windows = 0;
        Assert.NotEqual(uint.MaxValue, GetRawInputDeviceList(0, ref windows, (uint)(2 * IntPtr.Size)));
        Controllers.RawList list = Controllers.ReadRawList();
        Assert.InRange(list.Listed, (int)windows - 2, (int)windows + 2);
        if (list.Listed == 0)
        {
            // a session without input devices (a service, a hosted runner): nothing to read
            return;
        }

        // RIDI_DEVICEINFO fails for every device when cbSize or the size passed is not sizeof(RID_DEVICE_INFO), and RIDI_DEVICENAME when its count is wrong
        string seen = string.Join("; ", list.Read.Select(d => $"{d.Type} {d.Path}"));
        Assert.True(list.Read.Count * 2 >= list.Listed, $"{list.Read.Count} of {list.Listed} devices read: {seen}");
        Assert.All(list.Read, d =>
        {
            Assert.InRange(d.Type, Controllers.RimTypeMouse, Controllers.RimTypeHid);
            Assert.StartsWith(@"\\?\", d.Path, StringComparison.Ordinal);
            Assert.False(d.Path.Contains('\0', StringComparison.Ordinal), d.Path);
        });
        Assert.All(list.Read.Where(d => d.Type != Controllers.RimTypeHid), d => Assert.Equal((0u, 0u, (ushort)0, (ushort)0), (d.VendorId, d.ProductId, d.UsagePage, d.Usage)));
    }

    [Fact]
    public void ADeviceThatIsNotThereHasNoNameAndThrowsNothing()
    {
        WindowsOnly();
        Assert.Null(Controllers.ProductString($@"\\?\HID#VID_0000&PID_0000#0&0&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}"));
        Assert.Null(Controllers.ProductString(@"\\?\no-such-device"));
        Assert.Null(Controllers.OemName(0x0000, 0x0000));
    }

    // -- the calls that open a device, on every HID device of this machine (FIXES D12 P-2) --------------------------------

    [Fact]
    public void OnlyADeviceOnAHidStackIsAskedForItsProductString()
    {
        // made-up paths of the shapes Windows writes: the interface classes of keyboards and of mice, and the HID class
        const string keyboards = "{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
        const string mice = "{378de44c-56ef-11d1-bc8c-00a0c91405dd}";

        // a USB keyboard and a USB mouse are HID devices, and so is every HID collection, on USB or on Bluetooth, in whatever case Windows writes the path
        Assert.True(OnAHidStack($@"\\?\HID#VID_046D&PID_C31C&MI_00#7&1f00a6b&0&0000#{keyboards}"));
        Assert.True(OnAHidStack($@"\\?\hid#vid_046d&pid_c077#7&aa&0&0000#{mice}"));
        Assert.True(OnAHidStack(@"\\?\HID#VID_054C&PID_0CE6&MI_03#8&1d2c3b4a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.True(OnAHidStack(@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6#9&2b3c4d5e&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));

        // a PS/2 keyboard or touchpad and the keyboard and mouse of a remote session are not: their drivers do not define the HID request
        Assert.False(OnAHidStack($@"\\?\ACPI#PNP0303#4&1a2b3c4d&0#{keyboards}"));
        Assert.False(OnAHidStack($@"\\?\ACPI#PNP0F13#4&1a2b3c4d&0#{mice}"));
        Assert.False(OnAHidStack($@"\\?\Root#RDP_KBD#0000#{keyboards}"));
        Assert.False(OnAHidStack($@"\\?\Root#RDP_MOU#0000#{mice}"));

        // "HID#" elsewhere in a path does not make a HID device
        Assert.False(OnAHidStack($@"\\?\USB#VID_046D&PID_C31C#HID#{keyboards}"));
        Assert.False(OnAHidStack(@"HID#VID_046D&PID_C31C"));
        Assert.False(OnAHidStack(@"\\?\no-such-device"));
        Assert.False(OnAHidStack(string.Empty));
    }

    [Fact]
    public void EveryDeviceOfThisMachineAnswersItsProductStringOrNothing()
    {
        WindowsOnly();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            // the devices of GitHub's hosted Windows runner are those of its virtual machine, and nobody can look at them from here: a device there that
            // answers badly would be a red run nobody could explain. On a Windows desktop the test asks what the PC has
            Assert.Skip("asks the machine's devices for their names, and nobody can look at the devices of GitHub's hosted Windows runner from here; run it on a Windows desktop");
        }

        Controllers.RawList list = Controllers.ReadRawList();
        if (list.Read.Count == 0)
        {
            // a session without input devices (a service): nobody to ask
            return;
        }

        // every HID device, not only pads: HID collections of any usage, and the keyboards and mice that are HID devices. Each is opened without read or write
        // access, asked once, closed. A keyboard or a mouse on another stack (PS/2, a remote session's, a virtual one) is never sent the request: it is a HID
        // request, which only a HID stack defines (the launcher itself asks controller collections only, and those are HID)
        ITestOutputHelper? output = TestContext.Current.TestOutputHelper;
        int named = 0;
        int silent = 0;
        int shut = 0;
        int elsewhere = 0;
        double longest = 0;
        foreach (Controllers.RawDevice device in list.Read)
        {
            string who = Describe(device);
            if (!OnAHidStack(device.Path))
            {
                elsewhere++;
                output?.WriteLine($"{who}: not on a HID stack, not asked");
                continue;
            }

            string? name = null;
            Exception? thrown = null;
            double took = 0;
            var worker = new Thread(() =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    name = Controllers.ProductString(device.Path);
                }
                catch (Exception e)
                {
                    thrown = e;
                }

                took = clock.Elapsed.TotalMilliseconds;
            })
            { IsBackground = true };
            var waited = Stopwatch.StartNew();
            worker.Start();
            if (!worker.Join(DeviceLimit))
            {
                Assert.Fail($"{who} did not answer its product string within {waited.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s");
            }

            Assert.True(thrown is null, $"{who}: the product string threw {thrown}");
            longest = Math.Max(longest, took);
            string answer;
            if (name is not null)
            {
                named++;
                Assert.True(IsClean(name) && name.Length <= 126, $"{who} answered a name of {name.Length} characters that is not clean: {Printable(name)}");
                answer = $"'{name}'";
            }
            else if (Opens(device.Path, out int error))
            {
                silent++;
                answer = "opened, no name";
            }
            else
            {
                shut++;
                answer = "not opened (Windows error " + error.ToString(CultureInfo.InvariantCulture) + ")";
            }

            output?.WriteLine($"{who}: {answer}, {took.ToString("F1", CultureInfo.InvariantCulture)} ms");
        }

        int hid = list.Read.Count(d => d.Type == Controllers.RimTypeHid);
        output?.WriteLine(
            $"Windows listed {list.Listed} devices, {list.Read.Count} were read: {hid} HID collections ({list.Read.Count(Controllers.IsGameController)} of them game controllers), " +
            $"{list.Read.Count(d => d.Type == Controllers.RimTypeKeyboard)} keyboards, {list.Read.Count(d => d.Type == Controllers.RimTypeMouse)} mice. " +
            $"{list.Read.Count - elsewhere} are on a HID stack and were asked, {elsewhere} on another stack were not. " +
            $"{named} answered a name, {silent} opened without one, {shut} did not open; the longest call took {longest.ToString("F1", CultureInfo.InvariantCulture)} ms; nothing threw.");
    }

    [Fact]
    public void EveryControllerKeyInTheRegistryOfThisMachineGivesANameOrNothing()
    {
        WindowsOnly();
        const string oem = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
        ITestOutputHelper? output = TestContext.Current.TestOutputHelper;
        var counts = new List<string>();
        int named = 0;
        foreach ((string label, RegistryKey hive) in new[] { ("HKLM", Registry.LocalMachine), ("HKCU", Registry.CurrentUser) })
        {
            string[] keys;
            try
            {
                using RegistryKey? joysticks = hive.OpenSubKey(oem);
                keys = joysticks?.GetSubKeyNames() ?? [];
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // a hive this account may not read: the probe reads the other one
                keys = [];
            }

            int ids = 0;
            foreach (string key in keys)
            {
                Match id = VidPidKey().Match(key);
                if (!id.Success)
                {
                    continue;
                }

                ids++;
                uint vid = uint.Parse(id.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint pid = uint.Parse(id.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                // no exception: the name Windows keeps for these ids (either hive), or nothing
                string? name = Controllers.OemName(vid, pid);
                if (name is not null)
                {
                    named++;
                    Assert.True(IsClean(name), $"{label} VID_{vid:X4}&PID_{pid:X4}: a name of {name.Length} characters that is not clean: {Printable(name)}");
                }

                output?.WriteLine($"{label} VID_{vid:X4}&PID_{pid:X4}: {(name is null ? "no name" : $"'{name}'")}");
            }

            counts.Add($"{ids} in {label}");
        }

        output?.WriteLine($"VID / PID keys under Joystick\\OEM: {string.Join(", ", counts)}; {named} gave a name; nothing threw.");
    }

    /// <summary>A name as the launcher may show it: not empty, nothing to trim, no control character.</summary>
    private static bool IsClean(string name) => name.Length > 0 && name == name.Trim() && !name.Any(char.IsControl);

    private static string Printable(string name) =>
        string.Concat(name.Select(c => char.IsControl(c) ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString()));

    /// <summary>A device for a failure message: its kind, the hardware id of its path (the VID and PID of a USB or Bluetooth device) and, for a HID collection, its ids and usage.</summary>
    private static string Describe(Controllers.RawDevice device)
    {
        string[] parts = device.Path.Split('#');
        string hardware = parts.Length > 1 ? parts[1] : device.Path;
        return device.Type switch
        {
            Controllers.RimTypeHid => $"HID collection {hardware} (VID_{device.VendorId:X4}&PID_{device.ProductId:X4}, usage page {device.UsagePage:X4}, usage {device.Usage:X2})",
            Controllers.RimTypeKeyboard => "keyboard " + hardware,
            _ => "mouse " + hardware,
        };
    }

    /// <summary>
    /// A device of the HID class: its path starts with <c>\\?\HID#</c>, in whatever case. Only these are asked for a product string, which is a HID request; the
    /// driver of a PS/2 keyboard (<c>\\?\ACPI#PNP0303#...</c>) or of a remote session's keyboard (<c>\\?\Root#RDP_KBD#...</c>) does not define it.
    /// </summary>
    private static bool OnAHidStack(string path) => path.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Windows opens the device at all, the way the probe opens it (no read or write access; closed at once): "has no name" or "does not open".</summary>
    private static bool Opens(string path, out int error)
    {
        using SafeFileHandle device = CreateFile(path, 0, 3, 0, 3, 0, 0);
        error = device.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return !device.IsInvalid;
    }

    private static void WindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Raw Input is a Windows API.");
        }
    }

    [GeneratedRegex("^VID_([0-9A-F]{4})&PID_([0-9A-F]{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidKey();

    // the count query only (no list is passed, nothing is written): the test's own reading of how many devices Windows has
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(nint list, ref uint count, uint entrySize);

    // the test's own opening of a device, as the probe opens it: no access (0), shared for reading and writing (3), only when it is there (OPEN_EXISTING, 3)
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
