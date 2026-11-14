using System.Buffers.Binary;
using Tsuki.Engine.Contracts;

namespace Tsuki.Engine.Save;

/// <summary>A save file or image that cannot be read: truncated, damaged, from a newer version or not a save at all.</summary>
public sealed class SaveFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    public SaveFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Our own save file (<c>%APPDATA%/Tsuki/…</c>, the path is the app's choice) and the PS4 save image import / export.
/// </summary>
/// <remarks>
/// <para>File layout, version 1, all integers little-endian:</para>
/// <code>
/// 0x00  char[8] "TSUKISAV"
/// 0x08  u32 version (1)       0x0C u32 flags (0)
/// 0x10  u32 systemSize (0x18AC)   0x14 u32 slotSize (0x7108)
/// 0x18  u32 slotRecords           0x1C u32 CRC-32 of bytes 0x00..0x1B
/// 0x20  system block (0x18AC bytes, the PS4 layout of adv_state.md 7.5) + u32 CRC-32 of those bytes
/// then slotRecords records:  u16 slot index (strictly ascending, 0..139), u16 thumbnail format (0 = none),
///       u32 thumbnail length, slot (0x7108 bytes, the PS4 layout of adv_state.md 7.4), thumbnail bytes,
///       u32 CRC-32 of the whole record before the CRC
/// footer: char[8] "TSUKIEND", u32 total file length, u32 CRC-32 of every byte before the footer's CRC
/// </code>
/// <para>
/// The system block and each slot are byte-for-byte the PS4 blocks, so a PS4 save image maps onto this file
/// (<see cref="ImportPs4Image"/>) and back (<see cref="ExportPs4Image"/>). Only stored slots are written.
/// </para>
/// </remarks>
public static class SaveFile
{
    /// <summary>The file magic.</summary>
    public static ReadOnlySpan<byte> Magic => "TSUKISAV"u8;

    private static ReadOnlySpan<byte> FooterMagic => "TSUKIEND"u8;

    /// <summary>The format version this code writes and reads.</summary>
    public const int Version = 1;

    private const int HeaderSize = 0x20;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;
    private const int MaxFileBytes = 256 * 1024 * 1024;

    /// <summary>Size of a PS4 save image (4 MiB).</summary>
    public const int Ps4ImageSize = 0x400000;

    private const int Ps4SlotBase = 0x1A00;
    private const int Ps4SlotStride = 0x7140;

    // -- writing -----------------------------------------------------------------------------------------------------

    /// <summary>The file's bytes.</summary>
    public static byte[] ToBytes(SaveStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        using var ms = new MemoryStream();
        Write(ms, store);
        return ms.ToArray();
    }

    /// <summary>Writes the store to a stream (not atomic: use <see cref="WriteAtomic"/> for files).</summary>
    public static void Write(Stream stream, SaveStore store)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(store);
        var body = new MemoryStream();
        var slots = store.StoredSlots().ToList();

        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x10..], SystemBlock.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..], SlotImage.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x18..], (uint)slots.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x1C..], Crc32.Compute(header[..0x1C]));
        body.Write(header);

        SystemBlock system = store.SystemData.Block;
        system.Magic = SystemBlock.MagicValue;
        if (system.Used == 0)
        {
            system.Used = 1;
        }

        body.Write(system.Bytes);
        WriteU32(body, Crc32.Compute(system.Bytes));

        byte[] headBytes = new byte[8];
        foreach (int slot in slots)
        {
            SlotImage image = store.GetSlot(slot)!;
            SlotThumbnail? thumb = store.GetThumbnail(slot);
            if (thumb is { Data.Length: > MaxThumbnailBytes } || thumb is { Format: 0 })
            {
                throw new ArgumentException($"slot {slot}: a thumbnail needs a non-zero format and at most {MaxThumbnailBytes} bytes");
            }

            long start = body.Position;
            Span<byte> head = headBytes;
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)slot);
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], thumb?.Format ?? 0);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)(thumb?.Data.Length ?? 0));
            body.Write(head);
            body.Write(image.Bytes);
            if (thumb is not null)
            {
                body.Write(thumb.Data);
            }

            uint crc = Crc32.Compute(body.GetBuffer().AsSpan((int)start, (int)(body.Position - start)));
            WriteU32(body, crc);
        }

        body.Write(FooterMagic);
        WriteU32(body, checked((uint)(body.Length + 8)));
        uint total = Crc32.Compute(body.GetBuffer().AsSpan(0, (int)body.Length));
        WriteU32(body, total);
        stream.Write(body.GetBuffer().AsSpan(0, (int)body.Length));
    }

    /// <summary>
    /// Writes the store to <paramref name="path"/> atomically: the bytes go to a temporary file in the same folder, are
    /// flushed to disk, and replace the old file in one step; the previous generation stays as <c>path + ".bak"</c>.
    /// A write that fails throws and leaves the old file and no temporary file. The one exception is a replace that stopped after it gave the old file the
    /// backup's name, when the new file cannot be moved into place either: its temporary file stays beside the backup for <see cref="FinishInterruptedCommit"/>.
    /// </summary>
    public static void WriteAtomic(string path, SaveStore store)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(store);
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        bool replacing = false;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Write(fs, store);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                replacing = true;       // (the temporary file is a whole save now, flushed and closed)
                ReplaceKeepingBackup(tmp, full);
            }
            else
            {
                BeforeRename?.Invoke(tmp, full);
                File.Move(tmp, full);
            }
        }
        catch
        {
            // A replace that stopped half way gave the old file the backup's name and did not bring the new one (Windows documents that failure): no save file
            // is there, and the temporary file is the only copy of the newest generation. It takes the name, which is the step that was missing, and the commit
            // is written. When that move is refused too, the temporary file stays beside the backup, where the next start finishes the commit
            // (FinishInterruptedCommit, which needs the backup), and this commit fails with the replace's own error. Every other failure removes it, as before,
            // also the move of a commit that found no save file (nothing was replaced there, and the move is not tried again).
            bool keep = false;
            if (replacing && !File.Exists(full) && !Directory.Exists(full))
            {
                try
                {
                    BeforeRename?.Invoke(tmp, full);
                    File.Move(tmp, full);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    keep = File.Exists(full + ".bak");
                }
            }

            if (!keep)
            {
                TryDelete(tmp);
            }

            throw;
        }
    }

    // how long a commit on Windows keeps trying its replace while somebody has the file or its backup open
    private const int ReplaceMilliseconds = 200;

    // The new file takes the old one's place and the old one becomes the backup: File.Replace. On Windows that step is refused while anybody has the backup open,
    // whatever sharing the holder asked for ("Unable to remove the file to be replaced"; the file, its backup and the temporary file stay as they were), and a
    // reader that opened the file one commit ago holds what is the backup now. A reader has it only for its read, so the step is tried again for a moment.
    // When the holder does not let go, the temporary file is moved over the file instead: one rename, which never leaves the name without a file and changes
    // nothing when it is refused (measured; a File.Replace without a backup name goes through under more holders, but takes the old file away before it
    // brings the new one). The held backup then stays one generation older until the next commit. The move is refused while the file itself is open elsewhere
    // too: then the commit fails with the replace's own error, as it always did. Elsewhere nobody's open file stops a replace: one try, as before.
    private static void ReplaceKeepingBackup(string tmp, string full)
    {
        string backup = full + ".bak";
        if (!OperatingSystem.IsWindows())
        {
            Replace(tmp, full, backup);
            return;
        }

        long start = Environment.TickCount64;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Replace(tmp, full, backup);
                return;
            }
            catch (IOException) when (File.Exists(tmp) && File.Exists(full))
            {
                if (Environment.TickCount64 - start < ReplaceMilliseconds)
                {
                    // the first tries follow each other closely (the moment a reader lets go is the one to catch); the later ones are a millisecond apart
                    if (attempt < 16)
                    {
                        Thread.SpinWait(200);
                        Thread.Yield();
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }

                    continue;
                }

                try
                {
                    File.Move(tmp, full, overwrite: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the replace's refusal is the one to report
                }

                throw;
            }
        }
    }

    private static void Replace(string tmp, string full, string backup)
    {
        BeforeRename?.Invoke(tmp, full);
        File.Replace(tmp, full, backup, ignoreMetadataErrors: true);
    }

    /// <summary>
    /// For the tests, which cannot make a real replace stop half way: called with the temporary file and the save file before a commit's replace (each try),
    /// before the move of a commit that found no save file, and before the move that follows a replace that stopped half way. What it throws is that step's
    /// failure. Nothing else sets it.
    /// </summary>
    internal static Action<string, string>? BeforeRename { get; set; }

    /// <summary>
    /// Finishes a commit that was cut off between its two renames. On Windows the replace of <see cref="WriteAtomic"/> first gives the old file the backup's name
    /// and then the new file the file's (measured: a reader looking in between finds no file). A process that ends there (killed, a crash, the power) leaves
    /// no save file, the previous generation as the backup, and the newest generation, whole and flushed, under its temporary name; the next start then found
    /// "no save file", began a new profile, and its second commit wrote that profile over the backup. Only when <paramref name="path"/> is not there and its
    /// backup is: the newest temporary file of a commit that is a whole save and not older than the backup takes the file's name, which is the rename that
    /// was missing. The backup and every other file stay as they are. With no such file nothing is done (a save file that was simply removed is a new
    /// profile, as before), and so without the backup, which the first of the two renames always leaves: a temporary file an earlier commit left behind is
    /// then no save to bring back, whatever its age. Nothing is thrown for a file or a folder that cannot be read. For the program that owns the profile only
    /// (the game at its start, which holds the profile's guard), never for a reader: while a commit is under way its files look like this for an instant.
    /// </summary>
    /// <param name="path">The save file.</param>
    /// <returns>The temporary file that is the save file now, or null when nothing was done.</returns>
    public static string? FinishInterruptedCommit(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            var bak = new FileInfo(full + ".bak");       // one look: that it is there, and when it was written
            if (dir is null || File.Exists(full) || Directory.Exists(full) || !bak.Exists)
            {
                return null;
            }

            // a temporary file older than the backup is not that commit's: a commit ended while it waited for a held backup leaves one too, beside its save file
            string name = Path.GetFileName(full);
            DateTime backup = bak.LastWriteTimeUtc;
            foreach (string tmp in Directory.EnumerateFiles(dir, name + ".*.tmp")
                .Where(f => IsCommitTemporary(Path.GetFileName(f), name))
                .Select(f => (File: f, Written: File.GetLastWriteTimeUtc(f)))
                .Where(f => f.Written >= backup)
                .OrderByDescending(f => f.Written)
                .Select(f => f.File)
                .ToList())
            {
                try
                {
                    FromBytes(ReadAndClose(tmp).Span);
                }
                catch (SaveFormatException)
                {
                    continue;       // not a whole save (a commit cut off while it wrote): the next one
                }

                File.Move(tmp, full);
                return tmp;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a folder that cannot be listed, a file that cannot be read or moved: nothing was done, as before
        }

        return null;
    }

    // the name WriteAtomic gives its temporary file: the save file's name, a dot, eight hexadecimal digits, ".tmp" (not the launcher's "save.dat.restore.tmp")
    private static bool IsCommitTemporary(string file, string name) =>
        file.Length == name.Length + 13
        && file.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
        && file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        && file.AsSpan(name.Length + 1, 8).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    /// <summary>
    /// SaveImage_Commit: copies G[0..499] of the interpreter's variables into the system data, then writes the file
    /// atomically (<see cref="WriteAtomic"/>). Call it after every manual, quick or auto save and when the config closes.
    /// </summary>
    /// <param name="path">The save file.</param>
    /// <param name="store">The save state.</param>
    /// <param name="vars">The interpreter's variables, or null when G did not change.</param>
    public static void Commit(string path, SaveStore store, Vars? vars)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (vars is not null)
        {
            store.SystemData.CaptureGlobalsFrom(vars);
        }

        WriteAtomic(path, store);
    }

    // -- reading -----------------------------------------------------------------------------------------------------

    /// <summary>Reads a store from a stream (it is read to its end).</summary>
    /// <exception cref="SaveFormatException">The data is not a complete, undamaged save file of a known version.</exception>
    public static SaveStore Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return FromBytes(ReadToEnd(stream).Span);
    }

    // every byte from the stream's position to its end; more than any valid save holds is refused
    private static ReadOnlyMemory<byte> ReadToEnd(Stream stream)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaxFileBytes)
        {
            throw new SaveFormatException("the file is larger than any valid save");
        }

        using var ms = new MemoryStream();
        byte[] chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (ms.Length > MaxFileBytes - n)
            {
                throw new SaveFormatException("the file is larger than any valid save");
            }

            ms.Write(chunk, 0, n);
        }

        return ms.GetBuffer().AsMemory(0, (int)ms.Length);
    }

    /// <summary>Parses the bytes of a save file.</summary>
    /// <exception cref="SaveFormatException">The data is not a complete, undamaged save file of a known version.</exception>
    public static SaveStore FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxFileBytes)
        {
            throw new SaveFormatException("the file is larger than any valid save");
        }

        if (data.Length < HeaderSize + 4)
        {
            throw new SaveFormatException($"the file is too short ({data.Length} bytes)");
        }

        if (!data[..8].SequenceEqual(Magic))
        {
            throw new SaveFormatException("not a Tsuki save file (bad magic)");
        }

        if (Crc32.Compute(data[..0x1C]) != BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]))
        {
            throw new SaveFormatException("the header is damaged (CRC mismatch)");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (version != Version)
        {
            throw new SaveFormatException(version > Version
                ? $"the save file has version {version}, newer than this game understands ({Version})"
                : $"unknown save file version {version}");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]) != SystemBlock.Size
            || BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]) != SlotImage.Size)
        {
            throw new SaveFormatException("the block sizes in the header do not match this version");
        }

        uint records = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);
        if (records > SaveStore.SlotCount)
        {
            throw new SaveFormatException($"{records} slot records (at most {SaveStore.SlotCount})");
        }

        // footer first: the whole-file CRC catches truncation and damage anywhere
        const int footerSize = 16;
        if (data.Length < HeaderSize + SystemBlock.Size + 4 + footerSize)
        {
            throw new SaveFormatException("the file is truncated");
        }

        ReadOnlySpan<byte> footer = data[^footerSize..];
        if (!footer[..8].SequenceEqual(FooterMagic))
        {
            throw new SaveFormatException("the file is truncated (no end marker)");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]) != (uint)data.Length)
        {
            throw new SaveFormatException("the file length differs from the length it records");
        }

        if (Crc32.Compute(data[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]))
        {
            throw new SaveFormatException("the file is damaged (CRC mismatch)");
        }

        int pos = HeaderSize;
        ReadOnlySpan<byte> systemBytes = data.Slice(pos, SystemBlock.Size);
        pos += SystemBlock.Size;
        if (Crc32.Compute(systemBytes) != BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]))
        {
            throw new SaveFormatException("the system block is damaged (CRC mismatch)");
        }

        pos += 4;
        var system = new SystemBlock();
        system.CopyFrom(systemBytes);
        ValidateSystem(system);
        var store = new SaveStore(new SystemData(system));

        int end = data.Length - footerSize;
        int previous = -1;
        for (uint r = 0; r < records; r++)
        {
            if (pos + 8 > end)
            {
                throw new SaveFormatException("a slot record is truncated");
            }

            int start = pos;
            int index = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            uint thumbLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            pos += 8;
            if (index >= SaveStore.SlotCount || index <= previous)
            {
                throw new SaveFormatException($"slot record {r} has the bad or repeated index {index}");
            }

            previous = index;
            if (thumbLength > MaxThumbnailBytes || (format == 0) != (thumbLength == 0)
                || pos + SlotImage.Size + (long)thumbLength + 4 > end)
            {
                throw new SaveFormatException($"slot {index}: bad thumbnail header or truncated record");
            }

            byte[] slotBytes = data.Slice(pos, SlotImage.Size).ToArray();
            pos += SlotImage.Size;
            byte[]? thumbBytes = thumbLength == 0 ? null : data.Slice(pos, (int)thumbLength).ToArray();
            pos += (int)thumbLength;
            if (Crc32.Compute(data[start..pos]) != BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]))
            {
                throw new SaveFormatException($"slot {index} is damaged (CRC mismatch)");
            }

            pos += 4;
            var image = new SlotImage(slotBytes);
            ValidateSlot(index, image);
            store.SetSlot(index, image, thumbBytes is null ? null : new SlotThumbnail(format, thumbBytes));
        }

        if (pos != end)
        {
            throw new SaveFormatException("unexpected bytes after the last slot record");
        }

        return store;
    }

    /// <summary>
    /// Opens a save file for reading the way every reader must (v0.1.9): the handle shares reading, writing and deleting, so that a reader never stands in the way
    /// of the writer. <see cref="WriteAtomic"/> replaces the file in one step (<c>File.Replace</c>), and on Windows that step fails while another handle is open
    /// without the delete share: a launcher that read save.dat with <c>File.OpenRead</c> at the moment of the game's commit made the commit fail. A reader that
    /// holds this handle through a replace keeps reading the file it opened, whole. Hold it only for the read itself: the file it opened becomes the backup, and
    /// on Windows a handle still open at the commit after that one keeps the backup from being replaced (that commit waits a moment for the reader, then leaves
    /// the backup one generation older).
    /// </summary>
    /// <param name="path">The save file, or its backup.</param>
    public static FileStream OpenForReading(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    /// Reads a save file; when it is damaged, tries the previous generation (<c>path + ".bak"</c>). The files are opened with <see cref="OpenForReading"/> and
    /// closed again before anything is parsed.
    /// </summary>
    /// <param name="path">The save file.</param>
    /// <param name="usedBackup">True when the backup was read.</param>
    /// <returns>The store, or null when there is no save file at all.</returns>
    /// <exception cref="SaveFormatException">The file (and the backup, if any) is damaged.</exception>
    public static SaveStore? ReadWithFallback(string path, out bool usedBackup)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        usedBackup = false;
        string backup = path + ".bak";
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return FromBytes(ReadAndClose(path).Span);
        }
        catch (SaveFormatException primary) when (File.Exists(backup))
        {
            try
            {
                SaveStore store = FromBytes(ReadAndClose(backup).Span);
                usedBackup = true;
                return store;
            }
            catch (SaveFormatException e)
            {
                // the file's own fault is the one to report; the backup's goes with it (R1-app)
                throw new SaveFormatException($"{primary.Message}; the backup: {e.Message}");
            }
        }
    }

    // The bytes of a save file. The handle is closed before anything is parsed: a reader has the file only for the read itself (OpenForReading), and checking
    // and unpacking a save takes far longer than reading it.
    private static ReadOnlyMemory<byte> ReadAndClose(string path)
    {
        ReadOnlyMemory<byte> bytes;
        using (FileStream stream = OpenForReading(path))
        {
            bytes = ReadToEnd(stream);
        }

        ReadAndClosed?.Invoke(path);
        return bytes;
    }

    /// <summary>For the tests: called with the path once a reading has a save file's bytes and has closed the file, before it parses them.</summary>
    internal static Action<string>? ReadAndClosed { get; set; }

    private static void ValidateSystem(SystemBlock system, bool checkLatestSlot = true)
    {
        if (system.Magic != SystemBlock.MagicValue || system.Used == 0)
        {
            throw new SaveFormatException("the system block has no magic");
        }

        if (checkLatestSlot && system.LatestSlot > SaveStore.AutoSaveLast)
        {
            throw new SaveFormatException($"latest slot {system.LatestSlot} is out of range");
        }
    }

    private static void ValidateSlot(int index, SlotImage slot)
    {
        if (!slot.IsStored)
        {
            throw new SaveFormatException($"slot {index} is not a complete save (magic or end marker missing)");
        }

        if (slot.HasStateB is < 0 or > 1)
        {
            throw new SaveFormatException($"slot {index}: state-B flag {slot.HasStateB}");
        }

        if (!slot.Date.IsPlausible)
        {
            throw new SaveFormatException($"slot {index}: implausible date {slot.Date}");
        }

        if (slot.StateA.Serial < 0)
        {
            throw new SaveFormatException($"slot {index}: state A has a negative serial");
        }
    }

    // -- PS4 images (adv_state.md 7, tools/eboot/savedata.py) ------------------------------------------------------------

    /// <summary>
    /// Imports a PS4 save image (<c>savedata000</c>, 4 MiB: the system block at 0, slot i at <c>0x1A00 + i * 0x7140</c>).
    /// Only slots with the magic, a used flag and the end marker are taken. The thumbnails are not part of the image. A latest-slot
    /// word that names no stored slot is recomputed from the dates.
    /// </summary>
    /// <exception cref="SaveFormatException">The image has the wrong size or no system block.</exception>
    public static SaveStore ImportPs4Image(ReadOnlySpan<byte> image)
    {
        if (image.Length != Ps4ImageSize)
        {
            throw new SaveFormatException($"a PS4 save image is 0x{Ps4ImageSize:X} bytes, got 0x{image.Length:X}");
        }

        var system = new SystemBlock();
        system.CopyFrom(image[..SystemBlock.Size]);
        ValidateSystem(system, checkLatestSlot: false);
        var store = new SaveStore(new SystemData(system));
        for (int i = 0; i < SaveStore.SlotCount; i++)
        {
            int off = Ps4SlotBase + (i * Ps4SlotStride);
            var slot = new SlotImage(image.Slice(off, SlotImage.Size).ToArray());
            if (slot.IsStored)
            {
                ValidateSlot(i, slot);
                store.SetSlot(i, slot, null);
            }
        }

        // a console image can carry a stale latest-slot word (it is only refreshed by the saves that set it): "Continue" must not point nowhere
        if (system.LatestSlot > SaveStore.AutoSaveLast || !store.HasSave(system.LatestSlot))
        {
            store.UpdateLatestSlot();
        }

        return store;
    }

    /// <summary>The PS4 image of a store (4 MiB; slots that hold no save carry the magic only, as the console writes them).</summary>
    public static byte[] ExportPs4Image(SaveStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        byte[] image = new byte[Ps4ImageSize];
        store.SystemData.Block.Bytes.CopyTo(image);
        for (int i = 0; i < SaveStore.SlotCount; i++)
        {
            int off = Ps4SlotBase + (i * Ps4SlotStride);
            if (store.GetSlot(i) is { } slot)
            {
                slot.Bytes.CopyTo(image.AsSpan(off));
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off), SlotImage.Magic);
            }
        }

        return image;
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static void WriteU32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320), the checksum of the file's blocks.</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        /// <summary>CRC-32 of <paramref name="data"/>.</summary>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                t[i] = c;
            }

            return t;
        }
    }
}
