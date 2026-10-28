using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace QuestCastStream;

/// <summary>
/// Stage-1 sender: takes an HEVC Annex-B stream, cuts it into frames, slices each frame
/// into datagrams under the MTU and paces them at the target frame rate. This is the
/// stand-in for the future "capture -> AMF -> transport" path: everything except the
/// encoder itself is ours, and the encoder is ffmpeg's AMF wrapper for now.
/// </summary>
public static class UdpSender
{
    public sealed class Options
    {
        public required string File { get; init; }
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 5001;
        public int Mtu { get; init; } = 1200;
        public double Fps { get; init; } = 60;
        public double DropRate { get; init; }
        public bool Loop { get; init; }
        public int Seconds { get; init; }
    }

    public static int Run(Options o)
    {
        var payloadMax = o.Mtu - PacketHeader.Size;
        if (payloadMax < 64) { Console.Error.WriteLine("mtu too small"); return 2; }

        var raw = File.ReadAllBytes(o.File);
        var frames = AnnexB.SplitAccessUnits(raw);
        if (frames.Count == 0) { Console.Error.WriteLine("no frames found in input"); return 2; }

        if (o.DropRate > 0.5) { Console.Error.WriteLine("drop rate above 50% is not a test, it is a denial of service"); return 2; }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = false };
        udp.Client.SendBufferSize = 4 << 20;
        var endpoint = new IPEndPoint(IPAddress.Parse(o.Host), o.Port);

        var rng = new Random(12345);
        var seq = (ushort)0;
        long framesSent = 0, packetsSent = 0, bytesSent = 0, dropped = 0;
        var period = TimeSpan.FromSeconds(1.0 / o.Fps);
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        var deadline = o.Seconds > 0 ? TimeSpan.FromSeconds(o.Seconds) : TimeSpan.MaxValue;

        var buffer = new byte[o.Mtu];

        while (clock.Elapsed < deadline)
        {
            foreach (var frame in frames)
            {
                var count = (ushort)((frame.Length + payloadMax - 1) / payloadMax);
                var isKey = IsKeyframe(frame);
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

                for (ushort i = 0; i < count; i++)
                {
                    var offset = i * payloadMax;
                    var len = Math.Min(payloadMax, frame.Length - offset);
                    if (o.DropRate > 0 && rng.NextDouble() < o.DropRate) { dropped++; continue; }

                    var header = new PacketHeader
                    {
                        Flags = (byte)(isKey ? PacketHeader.FlagKeyframe : 0),
                        Stream = 0,
                        Seq = seq++,
                        Frame = (uint)framesSent,
                        Index = i,
                        Count = count,
                        Length = (ushort)len,
                        TimestampUs = ts,
                    };
                    header.Write(buffer);
                    Buffer.BlockCopy(frame, offset, buffer, PacketHeader.Size, len);

                    udp.Send(buffer, PacketHeader.Size + len, endpoint);
                    packetsSent++;
                    bytesSent += PacketHeader.Size + len;
                }

                framesSent++;

                // pace on an absolute schedule so a slow frame does not push the rest behind
                next += period;
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                if (clock.Elapsed >= deadline) break;
            }

            if (!o.Loop) break;
        }

        var elapsed = clock.Elapsed.TotalSeconds;
        Console.WriteLine($$"""
        {
          "frames": {{framesSent}},
          "packets": {{packetsSent}},
          "dropped": {{dropped}},
          "bytes": {{bytesSent}},
          "seconds": {{elapsed.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}},
          "mbit": {{(bytesSent * 8.0 / 1e6 / Math.Max(elapsed, 0.001)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}},
          "packets_per_frame": {{(framesSent == 0 ? 0 : (double)packetsSent / framesSent).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}},
          "payload_bytes": {{raw.Length}}
        }
        """);
        return 0;
    }

    private static bool IsKeyframe(byte[] frame)
    {
        // HEVC: NAL types 16..23 are IRAP pictures (BLA/IDR/CRA)
        for (var i = 0; i + 4 < frame.Length; i++)
        {
            if (frame[i] != 0 || frame[i + 1] != 0) continue;
            var start = frame[i + 2] == 1 ? i + 3 : (frame[i + 2] == 0 && frame[i + 3] == 1 ? i + 4 : -1);
            if (start < 0) continue;
            var type = (frame[start] >> 1) & 0x3F;
            if (type is >= 16 and <= 23) return true;
            if (type < 32) return false;
        }
        return false;
    }
}
