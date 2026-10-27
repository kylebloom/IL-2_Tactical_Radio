using System.Buffers.Binary;
using Il2Radio.Core.Memory;

namespace Il2Radio.Core.Tests;

/// <summary>The read-only memory tools (Diag mem-*), against a fake process.</summary>
public class MemoryScanTests
{
    private const ulong ImageBase = 0x7FF6_0000_0000, HeapBase = 0x2000_0000;

    /// <summary>Two regions: a module image and a heap block, each backed by a byte array.</summary>
    private sealed class FakeMemory : IProcessMemory
    {
        public readonly byte[] Image = new byte[0x1000];
        public readonly byte[] Heap;

        public FakeMemory(int heapSize = 0x2000)
        {
            Heap = new byte[heapSize];
            Regions = [new MemoryRegion(HeapBase, (ulong)heapSize, true, false, null), new MemoryRegion(ImageBase, 0x1000, false, false, "Il-2.exe")];
        }

        public IReadOnlyList<MemoryRegion> Regions { get; }
        public IReadOnlyList<MemoryModule> Modules { get; } = [new MemoryModule("Il-2.exe", ImageBase, 0x1000)];
        public HashSet<ulong> Freed { get; } = [];

        public int Read(ulong address, Span<byte> buffer)
        {
            foreach (var (start, bytes) in new[] { (HeapBase, Heap), (ImageBase, Image) })
            {
                if (address < start || address + (ulong)buffer.Length > start + (ulong)bytes.Length)
                    continue;
                ulong end = address + (ulong)buffer.Length;
                if (Freed.Any(f => f >= address && f < end))
                    return 0;
                bytes.AsSpan((int)(address - start), buffer.Length).CopyTo(buffer);
                return buffer.Length;
            }
            return 0;
        }

        public void PutDouble(byte[] block, int offset, double v) => BinaryPrimitives.WriteDoubleLittleEndian(block.AsSpan(offset), v);
        public void PutPointer(byte[] block, int offset, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset), v);
    }

    [Fact]
    public void A_typed_float_matches_to_the_precision_it_was_written_with()
    {
        var memory = new FakeMemory();
        memory.PutDouble(memory.Heap, 0x100, 123456.74);
        memory.PutDouble(memory.Heap, 0x200, 123457.6);

        var whole = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.F64, "123457"));
        var tenth = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.F64, "123456.7"));

        Assert.Equal([HeapBase + 0x100], whole.Hits.Select(h => h.Address));
        Assert.Equal([HeapBase + 0x100], tenth.Hits.Select(h => h.Address));
    }

    [Fact]
    public void Ranges_find_every_value_inside_and_skip_misaligned_copies()
    {
        var memory = new FakeMemory();
        memory.PutDouble(memory.Heap, 0x40, -20);
        memory.PutDouble(memory.Heap, 0x80, 35.5);
        memory.PutDouble(memory.Heap, 0x104, 10); // not on 8 bytes

        var hits = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.F64, "-50..-1")).Hits.Concat(
            MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.F64, "1..50")).Hits);
        var loose = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.F64, "5..15"), new ScanOptions { Alignment = 4 }).Hits;

        Assert.Equal([HeapBase + 0x40, HeapBase + 0x80], hits.Select(h => h.Address));
        Assert.Equal([HeapBase + 0x104], loose.Select(h => h.Address));
    }

    [Fact]
    public void A_value_straddling_two_read_chunks_is_still_found()
    {
        var memory = new FakeMemory(MemoryScanner.ChunkSize + 0x1000);
        System.Text.Encoding.Unicode.GetBytes("Gert Knoll").CopyTo(memory.Heap, MemoryScanner.ChunkSize - 6);

        var hits = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.Utf16, "Gert Knoll")).Hits;

        Assert.Equal([HeapBase + MemoryScanner.ChunkSize - 6], hits.Select(h => h.Address));
    }

    [Fact]
    public void Module_scans_only_look_in_that_image()
    {
        var memory = new FakeMemory();
        BinaryPrimitives.WriteInt32LittleEndian(memory.Heap.AsSpan(0x10), 0x5B3FF);
        BinaryPrimitives.WriteInt32LittleEndian(memory.Image.AsSpan(0x20), 0x5B3FF);

        var all = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.I32, "0x5B3FF")).Hits;
        var image = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.I32, "0x5B3FF"), new ScanOptions { Module = "il-2.exe" }).Hits;

        Assert.Equal(2, all.Count);
        Assert.Equal([ImageBase + 0x20], image.Select(h => h.Address));
    }

    [Fact]
    public void Follow_up_scans_narrow_by_change_and_drop_freed_memory()
    {
        var memory = new FakeMemory();
        memory.PutDouble(memory.Heap, 0x00, 100); // will move up
        memory.PutDouble(memory.Heap, 0x08, 100); // stays
        memory.PutDouble(memory.Heap, 0x10, 100); // will move down
        memory.PutDouble(memory.Heap, 0x18, 100); // freed
        var first = MemoryScanner.Scan(memory, ScanPattern.Parse(ScanType.F64, "100")).Hits;

        memory.PutDouble(memory.Heap, 0x00, 140);
        memory.PutDouble(memory.Heap, 0x10, 60);
        memory.Freed.Add(HeapBase + 0x18);

        ulong[] Kept(string filter) => MemoryScanner.Refine(memory, first, ScanType.F64, RefineFilter.Parse(filter, ScanType.F64)).Select(h => h.Address - HeapBase).ToArray();
        Assert.Equal([0x00UL, 0x10], Kept("changed"));
        Assert.Equal([0x08UL], Kept("unchanged"));
        Assert.Equal([0x00UL], Kept("increased"));
        Assert.Equal([0x10UL], Kept("decreased"));
        Assert.Equal([0x00UL], Kept("130..150"));
        Assert.Equal(140, BitConverter.ToDouble(MemoryScanner.Refine(memory, first, ScanType.F64, new RefineFilter(RefineKind.Increased))[0].Value));
    }

    [Fact]
    public void Pointer_search_finds_structures_holding_the_target()
    {
        var memory = new FakeMemory();
        ulong position = HeapBase + 0x540;
        memory.PutPointer(memory.Image, 0x30, HeapBase + 0x500); // a static pointer to the start of the structure
        memory.PutPointer(memory.Heap, 0x10, HeapBase + 0x100);  // unrelated

        var hits = MemoryScanner.Scan(memory, ScanPattern.PointerTo(position, 0x400)).Hits;

        var hit = Assert.Single(hits);
        Assert.Equal(ImageBase + 0x30, hit.Address);
        Assert.Equal("Il-2.exe+0x30", new AddressBook(memory).Name(hit.Address));
    }

    [Fact]
    public void Address_expressions_read_modules_pointers_offsets_and_saved_results()
    {
        var memory = new FakeMemory();
        memory.PutPointer(memory.Image, 0x30, HeapBase + 0x500);
        memory.PutPointer(memory.Heap, 0x518, HeapBase + 0x900);
        ulong? Result(int i) => i == 2 ? HeapBase + 0x40 : null;

        Assert.Equal(ImageBase + 0x3C0, AddressExpression.Evaluate("Il-2.exe+3C0", memory));
        Assert.Equal(ImageBase + 0x3C0, AddressExpression.Evaluate("il-2.EXE + 0x3c0", memory));
        Assert.Equal(HeapBase + 0x940, AddressExpression.Evaluate("[[Il-2.exe+30]+18]+40", memory));
        Assert.Equal(HeapBase + 0x30, AddressExpression.Evaluate("r2-10", memory, Result));
        Assert.Equal(HeapBase + 0x40, AddressExpression.Evaluate("@2", memory, Result));
        Assert.Throws<FormatException>(() => AddressExpression.Evaluate("r7", memory, Result));
        Assert.Throws<FormatException>(() => AddressExpression.Evaluate("[Il-2.exe+30", memory));
        Assert.Throws<FormatException>(() => AddressExpression.Evaluate("[1234]", memory)); // unreadable
    }

    [Fact]
    public void A_saved_scan_round_trips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mem-scan-{Guid.NewGuid():N}.bin");
        try
        {
            var start = new DateTime(2026, 10, 27, 18, 25, 4, DateTimeKind.Utc);
            new ScanState(4242, start, ScanType.F64, "F64 123", [new ScanHit(0x1234, [1, 2, 3, 4, 5, 6, 7, 8])]).Save(path);

            var loaded = ScanState.Load(path)!;

            Assert.Equal((4242, start, ScanType.F64, "F64 123"), (loaded.ProcessId, loaded.ProcessStartUtc, loaded.Type, loaded.Description));
            var hit = Assert.Single(loaded.Hits);
            Assert.Equal(0x1234UL, hit.Address);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, hit.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dumps_show_numbers_and_name_pointers()
    {
        var memory = new FakeMemory();
        memory.PutDouble(memory.Heap, 0x00, 123456.789);
        memory.PutPointer(memory.Heap, 0x08, ImageBase + 0x30);

        var lines = MemoryDump.Format(HeapBase, memory.Heap.AsSpan(0, 16), new AddressBook(memory)).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Contains("123456.789", lines[0]);
        Assert.Contains("→ Il-2.exe+0x30", lines[1]);
        Assert.DoesNotContain("→", lines[0]);
    }
}
