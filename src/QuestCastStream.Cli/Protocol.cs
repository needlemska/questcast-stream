using System.Buffers.Binary;

namespace QuestCastStream;

/// <summary>
/// Wire format of one datagram. Stage 1 keeps it deliberately small: a fixed header
/// and a slice of one encoded frame. FEC and acknowledgements arrive in stage 2, and
/// they need a version bump here rather than a rewrite of the receiver.
/// </summary>
public readonly struct PacketHeader
{
    /// <summary>"QCS1" - catches stray traffic on the port before it eats memory.</summary>
    public const uint Magic = 0x31435351; // 'Q','C','S','1' little-endian

    public const int Size = 26;

    public const int FlagKeyframe = 0b0000_0001;
    public const int FlagConfig = 0b0000_0010;

    public byte Flags { get; init; }
    public byte Stream { get; init; }
    public ushort Seq { get; init; }
    public uint Frame { get; init; }
    public ushort Index { get; init; }
    public ushort Count { get; init; }
    public ushort Length { get; init; }

    /// <summary>Sender clock, microseconds since Unix epoch.</summary>
    public long TimestampUs { get; init; }

    public bool IsKeyframe => (Flags & FlagKeyframe) != 0;

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst[..4], Magic);
        dst[4] = Flags;
        dst[5] = Stream;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..8], Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..12], Frame);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[12..14], Index);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[14..16], Count);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[16..18], Length);
        BinaryPrimitives.WriteInt64LittleEndian(dst[18..26], TimestampUs);
    }

    public static bool TryParse(ReadOnlySpan<byte> src, out PacketHeader header)
    {
        header = default;
        if (src.Length < Size) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(src[..4]) != Magic) return false;

        var h = new PacketHeader
        {
            Flags = src[4],
            Stream = src[5],
            Seq = BinaryPrimitives.ReadUInt16LittleEndian(src[6..8]),
            Frame = BinaryPrimitives.ReadUInt32LittleEndian(src[8..12]),
            Index = BinaryPrimitives.ReadUInt16LittleEndian(src[12..14]),
            Count = BinaryPrimitives.ReadUInt16LittleEndian(src[14..16]),
            Length = BinaryPrimitives.ReadUInt16LittleEndian(src[16..18]),
            TimestampUs = BinaryPrimitives.ReadInt64LittleEndian(src[18..26]),
        };

        // a truncated or lying datagram must never index past the buffer
        if (h.Length != src.Length - Size) return false;
        if (h.Count == 0 || h.Index >= h.Count) return false;

        header = h;
        return true;
    }
}

/// <summary>
/// Splits an HEVC Annex-B byte stream into access units. The encoder does not mark
/// frames for us (no AUD by default), so a new picture starts at the first VCL NAL
/// that carries first_slice_segment_in_pic_flag, and VPS/SPS/PPS stay attached to the
/// frame that follows them.
/// </summary>
public static class AnnexB
{
    public static List<byte[]> SplitAccessUnits(byte[] stream)
    {
        var frames = new List<byte[]>();
        var frame = new MemoryStream();
        var configStaged = false;

        int i = 0;
        while (i < stream.Length)
        {
            var start = FindStartCode(stream, i, out var scLen);
            if (start < 0) break;
            var nalStart = start + scLen;
            var next = FindStartCode(stream, nalStart, out _);
            var nalEnd = next < 0 ? stream.Length : next;
            if (nalEnd <= nalStart) { i = nalStart; continue; }

            int type = (stream[nalStart] >> 1) & 0x3F;
            bool isVcl = type < 32;
            bool firstSlice = isVcl && nalStart + 2 < nalEnd && (stream[nalStart + 2] & 0x80) != 0;

            if (isVcl && firstSlice && frame.Length > 0)
            {
                frames.Add(frame.ToArray());
                frame = new MemoryStream();
            }
            if (!isVcl && type is 32 or 33 or 34)
            {
                configStaged = true;
            }

            frame.Write(stream, start, nalEnd - start);
            i = nalEnd;
        }

        if (frame.Length > 0) frames.Add(frame.ToArray());
        _ = configStaged;
        return frames;
    }

    private static int FindStartCode(byte[] data, int from, out int length)
    {
        length = 0;
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1) { length = 3; return i; }
            if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1) { length = 4; return i; }
        }
        return -1;
    }
}
