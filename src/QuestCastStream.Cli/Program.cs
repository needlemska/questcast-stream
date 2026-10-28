using System.Globalization;

namespace QuestCastStream;

public static class Program
{
    private const string Usage = """
    questcast-stream - СЃС‚РµРЅРґ Рё РїСЂРѕС‚РѕС‚РёРї РєРѕРЅРІРµР№РµСЂР° РґР»СЏ Quest 3 (СЌС‚Р°Рї 1)

      send --file <hevc> [--host 127.0.0.1] [--port 5001] [--mtu 1200]
           [--fps 60] [--drop 0.02] [--loop] [--seconds N]
          СЂРµР¶РµС‚ Annex-B РїРѕС‚РѕРє РЅР° РєР°РґСЂС‹ Рё РѕС‚РїСЂР°РІР»СЏРµС‚ РїР°РєРµС‚Р°РјРё РЅРµ РґР»РёРЅРЅРµРµ MTU

      live-send --stdin [--host 127.0.0.1] [--port 5001] [--mtu 1200] [--fps 60] [--seconds N]
          читает Annex-B из stdin и отправляет кадры по мере поступления (для живой цепи)

      live-recv --port 5001 [--seconds N]
          собирает кадры и пишет их в stdout (дальше - в декодер)

      recv --out <hevc> [--port 5001] [--seconds 10]
          СЃРѕР±РёСЂР°РµС‚ РєР°РґСЂС‹ РѕР±СЂР°С‚РЅРѕ, РїРёС€РµС‚ РїРѕС‚РѕРє Рё РїРµС‡Р°С‚Р°РµС‚ СЃС‚Р°С‚РёСЃС‚РёРєСѓ СЃРµС‚Рё

      info --file <hevc>
          СЃРєРѕР»СЊРєРѕ РєР°РґСЂРѕРІ, СЃРєРѕР»СЊРєРѕ РїР°РєРµС‚РѕРІ РїСЂРё Р·Р°РґР°РЅРЅРѕРј MTU, СЃСЂРµРґРЅРёР№ СЂР°Р·РјРµСЂ РєР°РґСЂР°

    РљРѕРґС‹ РІРѕР·РІСЂР°С‚Р°: 0 - СѓСЃРїРµС…, 2 - РЅРµРІРµСЂРЅС‹Рµ Р°СЂРіСѓРјРµРЅС‚С‹.
    """;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var opts = Parse(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "send" => UdpSender.Run(new UdpSender.Options
                {
                    File = Need(opts, "file"),
                    Host = Str(opts, "host", "127.0.0.1"),
                    Port = Int(opts, "port", 5001),
                    Mtu = Int(opts, "mtu", 1200),
                    Fps = Dbl(opts, "fps", 60),
                    DropRate = Dbl(opts, "drop", 0),
                    Loop = Flag(opts, "loop"),
                    Seconds = Int(opts, "seconds", 0),
                }),
                "recv" => UdpReceiver.Run(new UdpReceiver.Options
                {
                    Out = Need(opts, "out"),
                    Port = Int(opts, "port", 5001),
                    Seconds = Int(opts, "seconds", 10),
                    Mtu = Int(opts, "mtu", 1200),
                }),
                "live-send" => Live.SendFromStdin(Str(opts, "host", "127.0.0.1"), Int(opts, "port", 5001), Int(opts, "mtu", 1200), Dbl(opts, "fps", 60), Int(opts, "seconds", 0)),
                "live-recv" => Live.ReceiveToStdout(Int(opts, "port", 5001), Int(opts, "seconds", 10)),
                "info" => Info(Need(opts, "file"), Int(opts, "mtu", 1200)),
                _ => Unknown(args[0]),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    private static int Info(string path, int mtu)
    {
        var raw = File.ReadAllBytes(path);
        var frames = AnnexB.SplitAccessUnits(raw);
        var payload = mtu - PacketHeader.Size;
        var packets = frames.Sum(f => (f.Length + payload - 1) / payload);
        var avg = frames.Count == 0 ? 0 : frames.Average(f => f.Length);

        Console.WriteLine($$"""
        {
          "file_bytes": {{raw.Length}},
          "frames": {{frames.Count}},
          "avg_frame_bytes": {{avg.ToString("0", CultureInfo.InvariantCulture)}},
          "max_frame_bytes": {{(frames.Count == 0 ? 0 : frames.Max(f => f.Length))}},
          "mtu": {{mtu}},
          "payload": {{payload}},
          "packets": {{packets}},
          "avg_packets_per_frame": {{(frames.Count == 0 ? 0 : (double)packets / frames.Count).ToString("0.0", CultureInfo.InvariantCulture)}},
          "header_overhead_pct": {{(packets * PacketHeader.Size * 100.0 / Math.Max(raw.Length, 1)).ToString("0.00", CultureInfo.InvariantCulture)}}
        }
        """);
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            var token = list[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument: {token}");

            var name = token[2..];
            // a flag is a flag only when the next token is another option or there is none:
            // otherwise "--drop 0.02" would consume the value and break the following option
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[name] = list[++i];
            }
            else
            {
                map[name] = "true";
            }
        }
        return map;
    }

    private static string Need(Dictionary<string, string> o, string key)
        => o.TryGetValue(key, out var v) && v != "true" ? v : throw new ArgumentException($"--{key} is required");

    private static string Str(Dictionary<string, string> o, string key, string fallback)
        => o.TryGetValue(key, out var v) ? v : fallback;

    private static int Int(Dictionary<string, string> o, string key, int fallback)
        => o.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static double Dbl(Dictionary<string, string> o, string key, double fallback)
        => o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static bool Flag(Dictionary<string, string> o, string key)
        => o.TryGetValue(key, out var v) && v == "true";
}

