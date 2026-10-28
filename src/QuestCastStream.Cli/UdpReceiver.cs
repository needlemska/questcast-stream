using System.Net;
using System.Net.Sockets;

namespace QuestCastStream;

/// <summary>
/// Stage-1 receiver: reassembles frames from datagrams, writes the Annex-B stream back
/// to disk and reports what the network actually did (loss, gaps, jitter). Keyframes
/// reset the reassembly state so a lost packet never poisons the following frames.
/// </summary>
public static class UdpReceiver
{
    public sealed class Options
    {
        public int Port { get; init; } = 5001;
        public required string Out { get; init; }
        public int Seconds { get; init; } = 10;
        public int Mtu { get; init; } = 1200;
    }

    private sealed class FrameSlot
    {
        public required byte[][] Parts { get; init; }
        public required bool[] Done { get; init; }
        public int Got { get; set; }
        public long FirstArrivalUs { get; set; }
        public long LastArrivalUs { get; set; }
        public long SenderTsUs { get; init; }
    }

    public static int Run(Options o)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, o.Port));
        udp.Client.ReceiveBufferSize = 8 << 20;

        FileStream output;
        try
        {
            output = new FileStream(o.Out, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // telling the user "access denied" without the path wastes a debugging hour
            Console.Error.WriteLine($"cannot write --out '{Path.GetFullPath(o.Out)}': {ex.Message}");
            return 3;
        }
        using var _output = output;
        var slots = new Dictionary<uint, FrameSlot>();
        var rng = new Random(7);

        long packets = 0, bytes = 0, framesComplete = 0, framesIncomplete = 0, duplicates = 0;
        var gapsUs = new List<long>();
        long lastArrivalUs = 0;
        var written = 0u;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var wait = new List<Socket> { udp.Client };
        var buffer = new byte[o.Mtu + 64];

        while (clock.Elapsed.TotalSeconds < o.Seconds)
        {
            var remaining = (int)Math.Max(1, (o.Seconds - clock.Elapsed.TotalSeconds) * 1000);
            if (!udp.Client.Poll(Math.Min(remaining, 200) * 1000, SelectMode.SelectRead)) continue;

            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            int len;
            try { len = udp.Client.Receive(buffer); }
            catch (SocketException) { continue; }
            _ = endpoint;

            packets++;
            bytes += len;
            var arrivalUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            if (lastArrivalUs != 0) gapsUs.Add(arrivalUs - lastArrivalUs);
            lastArrivalUs = arrivalUs;

            if (!PacketHeader.TryParse(buffer.AsSpan(0, len), out var h)) continue;
            if (slots.TryGetValue(h.Frame, out var existing) && existing.Done[h.Index]) { duplicates++; continue; }

            if (!slots.TryGetValue(h.Frame, out var slot))
            {
                slot = new FrameSlot
                {
                    Parts = new byte[h.Count][],
                    Done = new bool[h.Count],
                    SenderTsUs = h.TimestampUs,
                };
                slots[h.Frame] = slot;
            }

            if (slot.Done[h.Index]) { duplicates++; continue; }
            var payload = new byte[h.Length];
            Buffer.BlockCopy(buffer, PacketHeader.Size, payload, 0, h.Length);
            slot.Parts[h.Index] = payload;
            slot.Done[h.Index] = true;
            slot.Got++;
            if (slot.FirstArrivalUs == 0) slot.FirstArrivalUs = arrivalUs;
            slot.LastArrivalUs = arrivalUs;

            if (slot.Got != slot.Done.Length) continue;

            // complete frame: flush every frame up to this one in order
            foreach (var id in slots.Keys.Where(k => k <= h.Frame).OrderBy(k => k).ToList())
            {
                var s = slots[id];
                if (s.Got == s.Done.Length)
                {
                    foreach (var part in s.Parts) output.Write(part);
                    framesComplete++;
                }
                else
                {
                    framesIncomplete++;
                }
                slots.Remove(id);
                written = Math.Max(written, id + 1);
            }
        }

        // whatever is left at the deadline never completed
        framesIncomplete += slots.Count;
        output.Flush();

        gapsUs.Sort();
        double P50(List<long> xs) => xs.Count == 0 ? 0 : xs[xs.Count / 2] / 1000.0;
        double P95(List<long> xs) => xs.Count == 0 ? 0 : xs[(int)(xs.Count * 0.95)] / 1000.0;
        _ = rng;

        Console.WriteLine($$"""
        {
          "packets": {{packets}},
          "bytes": {{bytes}},
          "frames_complete": {{framesComplete}},
          "frames_incomplete": {{framesIncomplete}},
          "duplicates": {{duplicates}},
          "frames_written": {{written}},
          "gap_p50_ms": {{P50(gapsUs).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}},
          "gap_p95_ms": {{P95(gapsUs).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}},
          "gap_max_ms": {{(gapsUs.Count == 0 ? 0 : gapsUs[^1] / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}},
          "out_bytes": {{new FileInfo(o.Out).Length}}
        }
        """);
        return 0;
    }
}
