using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Il2Radio.Core.Radio;

namespace Il2Radio.Core.Osd;

public enum RtssWriteResult
{
    Written,
    NotRunning,  // no valid 'RTSS' v2 header (server not running, or shutting down)
    NoFreeSlot,  // every third-party OSD slot is owned by another client
    Busy,        // the renderer holds the OSD lock; try again on the next update
}

/// <summary>
/// RivaTuner Statistics Server's documented shared-memory OSD interface ("RTSSSharedMemoryV2"), as
/// used by the RTSS SDK sample, MSI Afterburner and other monitoring tools. A client claims an OSD
/// slot by writing its owner name, puts text in it, bumps the global OSD frame, and clears the slot
/// when it is done. RTSS then draws the text inside the game's own frames, which is why it shows in
/// exclusive fullscreen. All methods work on a view of the whole mapping, so they can be tested
/// against a plain byte array.
/// </summary>
public static class RtssOsd
{
    public const string MappingName = "RTSSSharedMemoryV2";

    // RTSS_SHARED_MEMORY header (RTSSSharedMemory.h)
    public const uint Signature = 0x52545353;          // 'RTSS'
    public const int SignatureOffset = 0, VersionOffset = 4;
    public const int OsdEntrySizeOffset = 20, OsdArrOffsetOffset = 24, OsdArrSizeOffset = 28, OsdFrameOffset = 32, BusyOffset = 36;
    public const int HeaderSize = 40;

    // RTSS_SHARED_MEMORY_OSD_ENTRY
    public const int OsdTextOffset = 0, OsdTextSize = 256;
    public const int OwnerOffset = 256, OwnerSize = 256;
    public const int OsdExOffset = 512, OsdExSize = 4096;

    public const uint V2 = 0x00020000, V2_7_OsdEx = 0x00020007, V2_11_FormatTags = 0x0002000B, V2_14_BusyLock = 0x0002000E;

    /// <summary>Text encoding of OSD slots: single-byte ANSI. Latin-1 covers German and Western names.</summary>
    public static readonly Encoding TextEncoding = Encoding.Latin1;

    /// <summary>Shared-memory version if the header is valid, else null.</summary>
    public static uint? Version(ReadOnlySpan<byte> memory)
    {
        if (memory.Length < HeaderSize || U32(memory, SignatureOffset) != Signature)
            return null;
        uint version = U32(memory, VersionOffset);
        return version >= V2 ? version : null;
    }

    public static bool SupportsFormatTags(uint version) => version >= V2_11_FormatTags;

    /// <summary>Largest text this server accepts in one slot, including the terminating zero.</summary>
    public static int TextCapacity(uint version) => version >= V2_7_OsdEx ? OsdExSize : OsdTextSize;

    /// <summary>
    /// Writes <paramref name="text"/> to the slot owned by <paramref name="owner"/>, claiming the
    /// first free slot if it has none. Slot 0 is left to the primary client (Afterburner).
    /// </summary>
    public static RtssWriteResult Write(Span<byte> memory, string owner, string text)
    {
        if (Version(memory) is not { } version || Slots(memory) is not { } slots)
            return RtssWriteResult.NotRunning;

        int slot = FindSlot(memory, slots, owner);
        if (slot < 0)
        {
            slot = FindSlot(memory, slots, "");
            if (slot < 0)
                return RtssWriteResult.NoFreeSlot;
            WriteString(memory.Slice(slots.Offset(slot) + OwnerOffset, OwnerSize), owner);
        }

        bool locking = version >= V2_14_BusyLock;
        if (locking && (Interlocked.Or(ref Busy(memory), 1) & 1) != 0)
            return RtssWriteResult.Busy; // bit was already set: the renderer is reading
        try
        {
            var entry = memory.Slice(slots.Offset(slot), slots.EntrySize);
            if (version >= V2_7_OsdEx)
                WriteString(entry.Slice(OsdExOffset, OsdExSize), text);
            else
                WriteString(entry.Slice(OsdTextOffset, OsdTextSize), text);
        }
        finally
        {
            if (locking)
                Interlocked.And(ref Busy(memory), ~1);
        }

        BumpFrame(memory);
        return RtssWriteResult.Written;
    }

    /// <summary>Clears every slot owned by <paramref name="owner"/>. Returns how many were released.</summary>
    public static int Release(Span<byte> memory, string owner)
    {
        if (Version(memory) is null || Slots(memory) is not { } slots || owner.Length == 0)
            return 0;
        int released = 0;
        for (int slot; (slot = FindSlot(memory, slots, owner)) >= 0; released++)
            memory.Slice(slots.Offset(slot), slots.EntrySize).Clear();
        if (released > 0)
            BumpFrame(memory);
        return released;
    }

    // RTSS_SHARED_MEMORY_APP_ENTRY (one per hooked 3D application)
    public const int AppEntrySizeOffset = 8, AppArrOffsetOffset = 12, AppArrSizeOffset = 16;
    public const int AppProcessIdOffset = 0, AppNameOffset = 4, AppNameSize = 260, AppOsdPixelOffset = 324;

    /// <summary>
    /// The OSD zoom ("pixel zooming ratio", dwOSDPixel) RTSS applies to the hooked application whose
    /// executable is <paramref name="exeName"/>, or null if that application isn't hooked (yet).
    /// </summary>
    public static int? AppOsdZoom(ReadOnlySpan<byte> memory, string exeName)
    {
        if (Version(memory) is null)
            return null;
        long entrySize = U32(memory, AppEntrySizeOffset), arrOffset = U32(memory, AppArrOffsetOffset), count = U32(memory, AppArrSizeOffset);
        if (entrySize < AppOsdPixelOffset + 4 || arrOffset < HeaderSize || arrOffset + entrySize * count > memory.Length)
            return null;
        for (long i = 0; i < count; i++)
        {
            var entry = memory.Slice((int)(arrOffset + i * entrySize), (int)entrySize);
            if (U32(entry, AppProcessIdOffset) == 0)
                continue;
            string name = ReadString(entry.Slice(AppNameOffset, AppNameSize));
            if (Path.GetFileName(name.Replace('\\', '/')).Equals(exeName, StringComparison.OrdinalIgnoreCase))
                return U32(entry, AppOsdPixelOffset) is var zoom and > 0 and < 16 ? (int)zoom : null;
        }
        return null;
    }

    /// <summary>Lines and widest line (in characters) of OSD text once the tags are removed.</summary>
    public static (int Lines, int Columns) VisibleSize(string text)
    {
        if (text.Length == 0)
            return (0, 0);
        var lines = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "").Split('\n');
        return (lines.Length, lines.Max(l => l.Length));
    }

    /// <summary>Owner names of all claimed third-party slots, for diagnostics.</summary>
    public static IReadOnlyList<string> Owners(ReadOnlySpan<byte> memory)
    {
        var owners = new List<string>();
        if (Version(memory) is null || Slots(memory) is not { } slots)
            return owners;
        for (int i = 0; i < slots.Count; i++)
            if (ReadString(memory.Slice(slots.Offset(i) + OwnerOffset, OwnerSize)) is { Length: > 0 } name)
                owners.Add(name);
        return owners;
    }

    /// <summary>
    /// Formats visible chat lines as OSD text, wrapped to <paramref name="wrapColumns"/> (RTSS does
    /// not wrap). With tags, each line takes IL-2's message colour with the SDK's &lt;C=RRGGBB&gt;
    /// ... &lt;C&gt; syntax and fading lines are dimmed; tags need shared memory v2.11+. Output is
    /// trimmed to fit <paramref name="capacity"/> bytes and <paramref name="maxRows"/> screen rows,
    /// dropping the oldest lines first.
    /// </summary>
    public static string Format(IReadOnlyList<VisibleChatLine> lines, bool tags, int capacity = OsdExSize, int wrapColumns = 72, string prefix = "", int maxRows = int.MaxValue)
    {
        var blocks = lines.Select(v => FormatLine(v, tags, wrapColumns)).ToList();
        string Text() => prefix + string.Join("\n", blocks);
        while (blocks.Count > 0 && (TextEncoding.GetByteCount(Text()) >= capacity || VisibleSize(Text()).Lines > maxRows))
            blocks.RemoveAt(0);
        return blocks.Count == 0 ? "" : Text();
    }

    private static string FormatLine(VisibleChatLine visible, bool tags, int wrapColumns)
    {
        var line = visible.Line;
        string speaker = line.Speaker.Length > 0 ? Clean(line.Speaker) + ": " : "";
        var wrapped = Wrap(Clean(line.Text), wrapColumns - speaker.Length);
        string indent = new(' ', speaker.Length);
        string body = string.Join("\n" + indent, wrapped);
        if (!tags)
            return speaker + body;

        // IL-2's message line is one colour throughout, speaker included (see Il2ChatStyle).
        return $"<C={Dim(Il2ChatStyle.Colour(line).ToString("X6"), visible.Alpha)}>{speaker}{body}<C>";
    }

    /// <summary>RTSS colour tags have no alpha, so fading is approximated by darkening.</summary>
    private static string Dim(string rgb, float alpha)
    {
        float k = 0.25f + 0.75f * Math.Clamp(alpha, 0, 1);
        int c = Convert.ToInt32(rgb, 16);
        int r = (int)(((c >> 16) & 0xFF) * k), g = (int)(((c >> 8) & 0xFF) * k), b = (int)((c & 0xFF) * k);
        return $"{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>'&lt;' would start an RTSS tag; line breaks are ours to place; dashes outside Latin-1 would show as '?'.</summary>
    private static string Clean(string s) =>
        s.Replace('<', '(').Replace('>', ')').Replace('\r', ' ').Replace('\n', ' ').Replace('—', '-').Replace('–', '-');

    private static List<string> Wrap(string text, int width)
    {
        width = Math.Max(width, 20);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0 || lines.Count == 0)
            lines.Add(current.ToString());
        return lines;
    }

    private readonly record struct SlotTable(int ArrOffset, int EntrySize, int Count)
    {
        public int Offset(int slot) => ArrOffset + slot * EntrySize;
    }

    /// <summary>The OSD slot table, if it lies inside the mapping and its entries can hold the text field this version uses.</summary>
    private static SlotTable? Slots(ReadOnlySpan<byte> memory)
    {
        long entrySize = U32(memory, OsdEntrySizeOffset), arrOffset = U32(memory, OsdArrOffsetOffset), count = U32(memory, OsdArrSizeOffset);
        uint version = U32(memory, VersionOffset);
        long minimum = version >= V2_7_OsdEx ? OsdExOffset + OsdExSize : OwnerOffset + OwnerSize;
        if (entrySize < minimum || arrOffset < HeaderSize || count == 0 || arrOffset + entrySize * count > memory.Length)
            return null;
        return new SlotTable((int)arrOffset, (int)entrySize, (int)count);
    }

    private static int FindSlot(ReadOnlySpan<byte> memory, SlotTable slots, string owner)
    {
        for (int i = 1; i < slots.Count; i++)
            if (ReadString(memory.Slice(slots.Offset(i) + OwnerOffset, OwnerSize)) == owner)
                return i;
        return -1;
    }

    private static ref int Busy(Span<byte> memory) => ref MemoryMarshal.AsRef<int>(memory.Slice(BusyOffset, 4));

    private static void BumpFrame(Span<byte> memory) => Interlocked.Increment(ref MemoryMarshal.AsRef<int>(memory.Slice(OsdFrameOffset, 4)));

    private static uint U32(ReadOnlySpan<byte> memory, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(memory.Slice(offset, 4));

    /// <summary>Zero-terminated, truncated to fit; the rest of the field is cleared.</summary>
    private static void WriteString(Span<byte> field, string value)
    {
        field.Clear();
        byte[] bytes = TextEncoding.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
    }

    private static string ReadString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return TextEncoding.GetString(end < 0 ? field : field[..end]);
    }
}
