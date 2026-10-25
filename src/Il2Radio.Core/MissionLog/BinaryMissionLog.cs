using System.Buffers.Binary;
using System.Text;
using Il2Radio.Core.Geometry;

namespace Il2Radio.Core.MissionLog;

/// <summary>
/// Decoder for IL-2 Great Battles binary mission reports (<c>data\FlightLogs\missionReport(...).mlg</c>).
/// </summary>
/// <remarks>
/// The format is undocumented. The layout below was derived by comparing real .mlg files with the
/// documented text-log fields, and checked by decoding complete files with every record length and
/// terminator consistent:
/// <code>
/// record  := tick:u32  atype:u8  length:u16  payload[length]  0x0A
/// string  := length:u32  bytes[length]
/// position:= x:f32 y:f32 z:f32     (IL-2 world coordinates, y = altitude)
/// </code>
/// All integers are little-endian.
/// </remarks>
public static class BinaryMissionLog
{
    public const int HeaderSize = 7;
    private const byte Terminator = 0x0A;

    public static IReadOnlyList<MissionLogEvent> DecodeAll(ReadOnlySpan<byte> data)
    {
        var events = new List<MissionLogEvent>();
        int consumed = DecodeAvailable(data, events);
        if (consumed != data.Length)
            throw new FormatException($"Trailing {data.Length - consumed} bytes do not form a complete record.");
        return events;
    }

    /// <summary>
    /// Decodes as many complete records as are present and returns the number of bytes consumed.
    /// A partially written trailing record is left unconsumed so it can be completed by a later read.
    /// </summary>
    public static int DecodeAvailable(ReadOnlySpan<byte> data, List<MissionLogEvent> output)
    {
        int offset = 0;
        while (data.Length - offset >= HeaderSize)
        {
            uint tick = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            byte atype = data[offset + 4];
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 5)..]);
            int end = offset + HeaderSize + length;
            if (end >= data.Length)
                break; // record (or its terminator) not fully written yet

            if (data[end] != Terminator)
                throw new FormatException($"Missing record terminator at offset {end} (AType {atype}, length {length}).");

            output.Add(DecodeRecord(tick, atype, data.Slice(offset + HeaderSize, length)));
            offset = end + 1;
        }

        return offset;
    }

    public static MissionLogEvent DecodeRecord(long tick, int atype, ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        return atype switch
        {
            0 => new MissionStarted(tick, ReadDate(ref r), r.String()),
            1 => new HitEvent(tick, r.String(), r.Id(), r.Id()),
            2 => new DamageEvent(tick, r.Float(), r.Id(), r.Id(), r.Position()),
            3 => new KillEvent(tick, r.Id(), r.Id(), r.Position()),
            4 => DecodeSortieEnd(tick, ref r),
            5 => new TakeOffEvent(tick, r.Id(), r.Position()),
            6 => new LandingEvent(tick, r.Id(), r.Position()),
            7 => new MissionEndedEvent(tick),
            10 => DecodePlaneSpawn(tick, ref r),
            11 => new GroupInitEvent(tick, r.Id(), ParseIdList(r.String()), r.Id()),
            12 => new ObjectSpawnedEvent(tick, r.Id(), r.String(), ParseInt(r.String()), CleanName(r.String()), r.Id(),
                r.Remaining >= 12 ? r.Position() : null),
            16 => new BotDeinitEvent(tick, r.Id(), r.Position()),
            17 => new PositionEvent(tick, r.Id(), r.Position()),
            18 => new BotEjectedEvent(tick, r.Id(), r.Id(), r.Position()),
            _ => new UnhandledEvent(tick, atype),
        };
    }

    private static SortieEndedEvent DecodeSortieEnd(long tick, ref PayloadReader r)
    {
        long plane = r.Id();
        long bot = r.Id();
        r.Skip(16); // BUL, SH, BOMB, RCT
        return new SortieEndedEvent(tick, plane, bot, r.Position());
    }

    private static PlaneSpawnedEvent DecodePlaneSpawn(long tick, ref PayloadReader r)
    {
        long plane = r.Id();
        long bot = r.Id();
        r.Skip(16); // BUL, SH, BOMB, RCT
        WorldPoint position = r.Position();
        r.String(); // IDS (profile id)
        string login = r.String();
        r.String(); // NAME (empty for AI)
        string type = r.String();
        int country = ParseInt(r.String());
        int form = r.Int();
        long field = r.Id();
        int inAir = r.Int();
        long parent = r.Id();
        bool isPlayer = r.Int() == 1;
        return new PlaneSpawnedEvent(tick, plane, bot, position, type, country, form, field, inAir, parent, isPlayer, login);
    }

    private static DateTime? ReadDate(ref PayloadReader r)
    {
        int year = r.Int(), month = r.Int(), day = r.Int(), hour = r.Int(), minute = r.Int(), second = r.Int();
        try
        {
            return new DateTime(year, month, day, hour, minute, second);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<long> ParseIdList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(long.Parse)
            .ToArray();

    internal static int ParseInt(string text) => int.TryParse(text, out int value) ? value : -1;

    /// <summary>Pilot names in AType 12 can carry a leading control byte; strip non-printing characters.</summary>
    internal static string CleanName(string name) => new(name.Where(c => !char.IsControl(c)).ToArray());

    private ref struct PayloadReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public readonly int Remaining => _data.Length - _offset;

        public int Int()
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return value;
        }

        public long Id() => Int();

        public float Float()
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(_data[_offset..]);
            _offset += 4;
            return value;
        }

        public WorldPoint Position()
        {
            float x = Float(), y = Float(), z = Float();
            return WorldPoint.FromIl2(x, y, z);
        }

        public string String()
        {
            int length = Int();
            string value = Encoding.UTF8.GetString(_data.Slice(_offset, length));
            _offset += length;
            return value;
        }

        public void Skip(int count) => _offset += count;
    }
}

/// <summary>
/// Incrementally reads a .mlg file that IL-2 may still be writing. Each call returns only events
/// appended since the previous call.
/// </summary>
public sealed class BinaryMissionLogTail : IDisposable
{
    private readonly FileStream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private int _buffered;

    public BinaryMissionLogTail(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public string Path { get; }

    /// <summary>Total bytes decoded into events so far.</summary>
    public long BytesConsumed { get; private set; }

    public IReadOnlyList<MissionLogEvent> ReadNew()
    {
        var events = new List<MissionLogEvent>();
        while (true)
        {
            if (_buffered == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);

            int read = _stream.Read(_buffer, _buffered, _buffer.Length - _buffered);
            if (read == 0)
                break;
            _buffered += read;
        }

        int consumed = BinaryMissionLog.DecodeAvailable(_buffer.AsSpan(0, _buffered), events);
        Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _buffered - consumed);
        _buffered -= consumed;
        BytesConsumed += consumed;
        return events;
    }

    public void Dispose() => _stream.Dispose();
}
