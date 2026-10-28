using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace QuestCastStream;

/// <summary>
/// Live pipe mode: the sender reads an Annex-B stream from stdin and packetizes it as it arrives,
/// the receiver writes reassembled access units to stdout. Statistics go to stderr, so stdout stays
/// a clean elementary stream that can be piped straight into a decoder.
///
/// The access-unit splitter is deliberately simple: a new frame starts at a VCL NAL whose
/// first_slice_segment_in_pic_flag is set (bit 7 of the byte after the two-byte NAL header),
/// with parameter sets and SEI belonging to the frame that follows them. That is enough for
/// streams produced by our own encoder and avoids a full HEVC parser.
/// </summary>
public static class Live
{
    private const int MaxAu = 4 << 20;      // a single access unit above 4 MB means the splitter lost sync

    // ---------------- sender ----------------

    public static int SendFromStdin(string host, int port, int mtu, double fps, int seconds)
    {
        var payloadMax = mtu - PacketHeader.Size;
        if (payloadMax < 64) { Console.Error.WriteLine("mtu too small"); return 2; }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SendBufferSize = 8 << 20;
        var endpoint = new IPEndPoint(IPAddress.Parse(host), port);

        var stdin = Console.OpenStandardInput();
        var chunk = new byte[1 << 16];
        var datagram = new byte[mtu];
        var buf = new List<byte>(1 << 20);
        var scan = 0;            // next offset to look for a start code
        var auStart = -1;        // where the current access unit begins in buf
        var seq = (ushort)0;
        long frames = 0, packets = 0, bytes = 0;
        var clock = Stopwatch.StartNew();
        var deadline = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
        var period = fps > 0 ? TimeSpan.FromSeconds(1.0 / fps) : TimeSpan.Zero;
        var next = TimeSpan.Zero;

        void SendAu(ReadOnlySpan<byte> au)
        {
            if (au.Length == 0) return;
            var count = (ushort)((au.Length + payloadMax - 1) / payloadMax);
            var isKey = IsIrap(au);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            for (ushort i = 0; i < count; i++)
            {
                var offset = i * payloadMax;
                var len = Math.Min(payloadMax, au.Length - offset);
                var header = new PacketHeader
                {
                    Flags = (byte)(isKey ? PacketHeader.FlagKeyframe : 0),
                    Stream = 0,
                    Seq = seq++,
                    Frame = (uint)frames,
                    Index = i,
                    Count = count,
                    Length = (ushort)len,
                    TimestampUs = ts,
                };
                header.Write(datagram);
                au.Slice(offset, len).CopyTo(datagram.AsSpan(PacketHeader.Size));
                try { udp.Send(datagram, PacketHeader.Size + len, endpoint); }
                catch (SocketException) { /* a full socket buffer must not kill the pipeline */ }
                packets++;
                bytes += PacketHeader.Size + len;
            }

            frames++;
            // fps 0 = живой режим: отправляем сразу, как только кадр собрался.
            // Пейсинг нужен только для проигрывания файла; в живом потоке он копит очередь.
            if (period > TimeSpan.Zero)
            {
                next += period;
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(1)) Thread.Sleep(wait);
            }
        }

        var eof = false;
        while (!eof && clock.Elapsed < deadline)
        {
            var n = stdin.Read(chunk, 0, chunk.Length);
            if (n <= 0) { eof = true; }
            else buf.AddRange(new ArraySegment<byte>(chunk, 0, n));

            while (true)
            {
                var sc = FindStartCode(buf, scan);
                if (sc < 0 || sc + 5 >= buf.Count) break;
                var nal = sc + (buf[sc + 2] == 1 ? 3 : 4);
                if (nal + 2 >= buf.Count) break;

                var type = (buf[nal] >> 1) & 0x3F;
                var firstSlice = (buf[nal + 2] & 0x80) != 0;

                if (type < 32 && firstSlice)
                {
                    if (auStart < 0) auStart = sc;
                    else if (sc > auStart)
                    {
                        SendAu(buf.GetRange(auStart, sc - auStart).ToArray());
                        auStart = sc;
                    }
                }
                else if (auStart < 0)
                {
                    auStart = sc;
                }

                scan = nal;
                if (auStart > 0)
                {
                    buf.RemoveRange(0, auStart);
                    scan -= auStart;
                    auStart = 0;
                }
                if (buf.Count > MaxAu)
                {
                    Console.Error.WriteLine("splitter lost sync (access unit over 4 MB)");
                    return 3;
                }
            }
        }

        if (auStart >= 0 && auStart < buf.Count) SendAu(buf.GetRange(auStart, buf.Count - auStart).ToArray());

        var elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        Console.Error.WriteLine($$"""
        {"frames":{{frames}},"packets":{{packets}},"bytes":{{bytes}},"seconds":{{elapsed.ToString("0.000", CultureInfo.InvariantCulture)}},"mbit":{{(bytes * 8.0 / 1e6 / elapsed).ToString("0.0", CultureInfo.InvariantCulture)}},"fps":{{(frames / elapsed).ToString("0.0", CultureInfo.InvariantCulture)}}}
        """);
        return 0;
    }

    private static int FindStartCode(List<byte> b, int from)
    {
        for (var i = Math.Max(0, from); i + 3 < b.Count; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0) continue;
            if (b[i + 2] == 1) return i;
            if (b[i + 2] == 0 && b[i + 3] == 1) return i;
        }
        return -1;
    }

    private static bool IsIrap(ReadOnlySpan<byte> au)
    {
        for (var i = 0; i + 4 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0) continue;
            var start = au[i + 2] == 1 ? i + 3 : (au[i + 2] == 0 && au[i + 3] == 1 ? i + 4 : -1);
            if (start < 0) continue;
            var type = (au[start] >> 1) & 0x3F;
            if (type is >= 16 and <= 23) return true;
            if (type < 32) return false;
        }
        return false;
    }

    // ---------------- receiver ----------------

    public static int ReceiveToStdout(int port, int seconds)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        udp.Client.ReceiveBufferSize = 8 << 20;
        udp.Client.ReceiveTimeout = 500;

        var stdout = Console.OpenStandardOutput();
        var datagram = new byte[2048];
        var pending = new Dictionary<uint, Frame>();
        var expected = 0u;
        long packets = 0, bytes = 0, outFrames = 0, broken = 0, latePackets = 0, dropped = 0;
        var sinkDead = false;
        // возраст кадра в момент выдачи в декодер: сколько мс прошло с момента отправки.
        // Это отделяет «наш транспорт» от «захват+энкод» и «декод+запись».
        var ages = new List<double>(8192);
        var clock = Stopwatch.StartNew();
        var deadline = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;

        // Декодер за stdout может оказаться медленнее сети: пишем в отдельном потоке через
        // ограниченную очередь. Если очередь полна — выбрасываем кадр, задержка важнее полноты.
        const int QueueDepth = 8;
        var queue = new System.Collections.Concurrent.BlockingCollection<Frame>(QueueDepth);
        var writer = new Thread(() =>
        {
            foreach (var f in queue.GetConsumingEnumerable())
            {
                ages.Add((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L - f.TimestampUs) / 1000.0);
                try { foreach (var part in f.Parts) stdout.Write(part, 0, part.Length); }
                catch (IOException) { sinkDead = true; }
            }
        }) { IsBackground = true, Name = "qcs-writer" };
        writer.Start();

        void Emit(Frame f)
        {
            if (queue.TryAdd(f)) outFrames++; else dropped++;
        }

        // кадр, у которого потерялся пакет, не должен держать очередь вечно
        var lastGapCheck = TimeSpan.Zero;

        void Flush(bool force)
        {
            while (true)
            {
                if (pending.TryGetValue(expected, out var f))
                {
                    if (!f.Complete)
                    {
                        if (!force && (clock.Elapsed - f.CreatedAt) < TimeSpan.FromMilliseconds(80)) return;
                        broken++;
                        pending.Remove(expected);
                        expected++;
                        continue;
                    }

                    Emit(f);
                    pending.Remove(expected);
                    expected++;
                    continue;
                }

                // кадр, о котором не пришло ни одного пакета, тоже надо перешагнуть,
                // иначе всё, что за ним, навсегда останется в очереди
                if (pending.Count == 0) return;
                if (!force && (clock.Elapsed - lastGapCheck) < TimeSpan.FromMilliseconds(50)) return;
                lastGapCheck = clock.Elapsed;

                var head = expected;
                foreach (var k in pending.Keys) if (k < head) head = k;
                if (head == expected) return;
                broken += head - expected;
                expected = head;
                if (!force) return;
            }
        }

        while (clock.Elapsed < deadline)
        {
            int n;
            try
            {
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                n = udp.Client.ReceiveFrom(datagram, ref any);
            }
            catch (SocketException) { continue; } // receive timeout: keep waiting until the deadline

            packets++;
            bytes += n;
            if (!PacketHeader.TryParse(datagram.AsSpan(0, n), out var h)) continue;

            if (!pending.TryGetValue(h.Frame, out var frame))
            {
                if (h.Count > 4096 || h.Index >= h.Count) continue;
                if (h.Frame < expected) { latePackets++; continue; }
                frame = new Frame(h.Count, h.TimestampUs, h.Flags) { CreatedAt = clock.Elapsed };
                pending[h.Frame] = frame;
            }

            if (h.Index < frame.Parts.Length && frame.Parts[h.Index] is null && h.Length > 0)
            {
                frame.Parts[h.Index] = datagram.AsSpan(PacketHeader.Size, h.Length).ToArray();
                frame.Remaining--;
            }

            Flush(false);
        }

        Flush(true);
        queue.CompleteAdding();
        writer.Join();
        var elapsed = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        ages.Sort();
        double At(double q) => ages.Count == 0 ? 0 : ages[Math.Min(ages.Count - 1, (int)(ages.Count * q))];
        Console.Error.WriteLine($$"""
        {"frames":{{outFrames}},"broken":{{broken}},"late":{{latePackets}},"dropped":{{dropped}},"sink_dead":{{(sinkDead ? "true" : "false")}},"packets":{{packets}},"bytes":{{bytes}},"seconds":{{elapsed.ToString("0.000", CultureInfo.InvariantCulture)}},"mbit":{{(bytes * 8.0 / 1e6 / elapsed).ToString("0.0", CultureInfo.InvariantCulture)}},"age_p50_ms":{{At(0.50).ToString("0.0", CultureInfo.InvariantCulture)}},"age_p95_ms":{{At(0.95).ToString("0.0", CultureInfo.InvariantCulture)}},"age_max_ms":{{At(1.0).ToString("0.0", CultureInfo.InvariantCulture)}}}
        """);
        return 0;
    }

    private sealed class Frame
    {
        public Frame(ushort count, long ts, byte flags)
        {
            Parts = new byte[count][];
            Remaining = count;
            TimestampUs = ts;
            Keyframe = (flags & PacketHeader.FlagKeyframe) != 0;
        }

        public byte[][] Parts { get; }
        public int Remaining { get; set; }
        public TimeSpan CreatedAt { get; set; }
        public long TimestampUs { get; }
        public bool Keyframe { get; }
        public bool Complete => Remaining == 0;
    }
}
