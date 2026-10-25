using System.Buffers.Binary;
using System.Text;

namespace Il2Radio.Core.Telemetry;

/// <summary>
/// UDP packets sent by IL-2 when <c>[KEY = motiondevice]</c> / <c>[KEY = telemetrydevice]</c> are
/// enabled in startup.cfg. Both streams describe only the player's own vehicle.
/// </summary>
public abstract record TelemetryPacket(uint Tick);

/// <summary>
/// Motion packet (id 0x494C0100). Layout from the official IL2_MotionOutput document as used by
/// community consumers (IL2Shaker): yaw, pitch, roll (rad), spin x/y/z (rad/s), acceleration x/y/z.
/// There is no world position in this packet.
/// </summary>
public sealed record MotionPacket(uint Tick, float Yaw, float Pitch, float Roll, float SpinX, float SpinY, float SpinZ, float AccX, float AccY, float AccZ)
    : TelemetryPacket(Tick)
{
    /// <summary>Heading in degrees 0-360. Community decoders report yaw 0 = north; sign convention unverified.</summary>
    public double HeadingDegrees => ((Yaw * 180.0 / Math.PI) % 360 + 360) % 360;
}

/// <summary>State + events packet (id 0x54000101). Indicator and event IDs per community reverse engineering.</summary>
public sealed record StatePacket(uint Tick, bool Paused, IReadOnlyDictionary<ushort, float[]> Indicators, IReadOnlyList<TelemetryEvent> Events)
    : TelemetryPacket(Tick)
{
    public const ushort IndicatedAirspeedIndicator = 6;
    public const ushort AboveGroundLevelIndicator = 10;

    public float? IndicatedAirspeedMs => Indicators.TryGetValue(IndicatedAirspeedIndicator, out var v) && v.Length > 0 ? v[0] : null;
    public float? AboveGroundLevelM => Indicators.TryGetValue(AboveGroundLevelIndicator, out var v) && v.Length > 0 ? v[0] : null;
}

public sealed record TelemetryEvent(ushort Type, byte[] Data)
{
    public const ushort VehicleNameType = 0;

    /// <summary>For event type 0: the player's vehicle name (e.g. "Bf 109 F-4").</summary>
    public string? VehicleName =>
        Type == VehicleNameType && Data.Length > 0 && Data[0] < Data.Length
            ? Encoding.ASCII.GetString(Data, 1, Data[0]).TrimEnd('\0')
            : null;
}

public static class TelemetryDecoder
{
    public const uint MotionPacketId = 0x494C0100;
    public const uint StatePacketId = 0x54000101;

    /// <summary>Decodes a datagram; returns null for unknown or malformed packets instead of throwing.</summary>
    public static TelemetryPacket? Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8)
            return null;

        try
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(packet) switch
            {
                MotionPacketId => DecodeMotion(packet),
                StatePacketId => DecodeState(packet),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // truncated packet
        }
    }

    private static MotionPacket DecodeMotion(ReadOnlySpan<byte> p)
    {
        var f = new float[9];
        for (int i = 0; i < f.Length; i++)
            f[i] = BinaryPrimitives.ReadSingleLittleEndian(p[(8 + i * 4)..]);
        return new MotionPacket(BinaryPrimitives.ReadUInt32LittleEndian(p[4..]), f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8]);
    }

    private static StatePacket DecodeState(ReadOnlySpan<byte> p)
    {
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        uint tick = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        int indicatorCount = p[10];
        int offset = 11;

        var indicators = new Dictionary<ushort, float[]>();
        for (int i = 0; i < indicatorCount; i++)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
            int count = p[offset + 2];
            offset += 3;
            var values = new float[count];
            for (int j = 0; j < count; j++)
                values[j] = BinaryPrimitives.ReadSingleLittleEndian(p[(offset + j * 4)..]);
            indicators[id] = values;
            offset += count * 4;
        }

        var events = new List<TelemetryEvent>();
        if (offset < p.Length)
        {
            int eventCount = p[offset++];
            for (int i = 0; i < eventCount; i++)
            {
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
                int length = p[offset + 2];
                offset += 3;
                events.Add(new TelemetryEvent(type, p.Slice(offset, length).ToArray()));
                offset += length;
            }
        }

        return new StatePacket(tick, size == 12, indicators, events);
    }
}
