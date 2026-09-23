using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JimengFpsFix;

/// <summary>Immediate rational number (as used by ffmpeg, e.g. "1/15360", "24000/1001").</summary>
public readonly struct Rational
{
    public readonly long Num;
    public readonly long Den;

    public Rational(long num, long den) { Num = num; Den = den <= 0 ? 1 : den; }

    public double Value => Den == 0 ? 0 : (double)Num / Den;
    public bool IsValid => Den > 0;

    public static Rational Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "N/A" || s == "0/0") return new Rational(0, 0);
        int slash = s.IndexOf('/');
        if (slash < 0)
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? FromDouble(v) : new Rational(0, 0);
        if (long.TryParse(s.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
            long.TryParse(s.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            return new Rational(n, d);
        return new Rational(0, 0);
    }

    public static Rational FromDouble(double v)
    {
        if (v <= 0) return new Rational(0, 0);
        // exact for the rates we care about, good enough for everything else
        foreach (var (num, den) in Ntsc)
            if (Math.Abs(v - (double)num / den) < 1e-6) return new Rational(num, den);
        long scale = 1000000;
        long n = (long)Math.Round(v * scale);
        while (n % 10 == 0 && n > 0) { n /= 10; scale /= 10; }
        return new Rational(n, scale);
    }

    private static readonly (long num, long den)[] Ntsc =
    {
        (24000, 1001), (30000, 1001), (60000, 1001), (120000, 1001),
        (24, 1), (25, 1), (30, 1), (50, 1), (60, 1), (120, 1),
    };

    public override string ToString() => Den == 1 ? Num.ToString(CultureInfo.InvariantCulture)
                                                  : $"{Num}/{Den}";
    public bool EqualsRate(Rational other) => Num * other.Den == other.Num * Den;
}

public sealed class StreamInfo
{
    public int Index;
    public string CodecType = "";
    public string CodecName = "";
    public Rational TimeBase;
    public Rational AvgFrameRate;
    public Rational RFrameRate;
    public double Duration = -1;
    public double StartTime = 0;
    public long NbFrames = -1;
    public int Width, Height;
    public int SampleRate, Channels;
    public string PixFmt = "";
    public bool IsVideo => CodecType == "video";
    public bool IsAudio => CodecType == "audio";
}

public sealed class MediaInfo
{
    public string Path = "";
    public double FormatDuration = -1;
    public double FormatStartTime = 0;
    public List<StreamInfo> Streams = new();

    public StreamInfo FirstVideo => Streams.FirstOrDefault(s => s.IsVideo);
    public StreamInfo FirstAudio => Streams.FirstOrDefault(s => s.IsAudio);
    public IEnumerable<StreamInfo> Audios => Streams.Where(s => s.IsAudio);
}

public sealed class ProcResult
{
    public int ExitCode;
    public string StdErr = "";
}

public static class Ff
{
    public static string Ffmpeg = "ffmpeg";
    public static string Ffprobe = "ffprobe";
    public static bool Located;

    // ---------------------------------------------------------------- process

    public static ProcResult Run(string exe, IReadOnlyList<string> args,
                                 Action<string> onStdoutLine = null,
                                 Action<string> onStderrLine = null,
                                 CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ffmpeg/ffprobe 在管道里一律输出 UTF-8；不指定就会按系统 ANSI(GBK) 解码，
            // 中文路径 / 中文元数据 / 中文错误信息会变成乱码
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var errTail = new Queue<string>();
        var res = new ProcResult();
        using var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errTail) { errTail.Enqueue(e.Data); while (errTail.Count > 60) errTail.Dequeue(); }
            onStderrLine?.Invoke(e.Data);
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onStdoutLine?.Invoke(e.Data); };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            res.ExitCode = -1;
            res.StdErr = $"无法启动 {exe}: {ex.Message}";
            return res;
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
        {
            p.WaitForExit();
        }
        res.ExitCode = p.ExitCode;
        lock (errTail) res.StdErr = string.Join(Environment.NewLine, errTail);
        return res;
    }

    // ---------------------------------------------------------------- probing

    public static MediaInfo Probe(string file, out string error)
    {
        var sb = new StringBuilder();
        var r = Run(Ffprobe, new[]
        {
            "-v", "error", "-print_format", "json",
            "-show_format", "-show_streams", file
        }, line => sb.AppendLine(line));
        if (r.ExitCode != 0)
        {
            error = string.IsNullOrWhiteSpace(r.StdErr) ? "ffprobe 探测失败" : r.StdErr.Trim();
            return null;
        }
        try
        {
            error = null;
            return ParseProbeJson(sb.ToString());
        }
        catch (Exception ex)
        {
            error = "解析 ffprobe 输出失败: " + ex.Message;
            return null;
        }
    }

    public static MediaInfo ParseProbeJson(string json)
    {
        var info = new MediaInfo();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var fmt))
        {
            info.FormatDuration = GetDouble(fmt, "duration", -1);
            info.FormatStartTime = GetDouble(fmt, "start_time", 0);
            if (fmt.TryGetProperty("filename", out var fn)) info.Path = fn.GetString() ?? "";
        }
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var si = new StreamInfo
                {
                    Index = (int)GetDouble(s, "index", 0),
                    CodecType = GetString(s, "codec_type"),
                    CodecName = GetString(s, "codec_name"),
                    TimeBase = Rational.Parse(GetString(s, "time_base")),
                    AvgFrameRate = Rational.Parse(GetString(s, "avg_frame_rate")),
                    RFrameRate = Rational.Parse(GetString(s, "r_frame_rate")),
                    Duration = GetDouble(s, "duration", -1),
                    StartTime = GetDouble(s, "start_time", 0),
                    Width = (int)GetDouble(s, "width", 0),
                    Height = (int)GetDouble(s, "height", 0),
                    SampleRate = (int)GetDouble(s, "sample_rate", 0),
                    Channels = (int)GetDouble(s, "channels", 0),
                    PixFmt = GetString(s, "pix_fmt"),
                };
                var nbf = GetString(s, "nb_frames");
                if (long.TryParse(nbf, out var n)) si.NbFrames = n;
                info.Streams.Add(si);
            }
        }
        return info;
    }

    private static string GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    private static double GetDouble(JsonElement e, string name, double def)
    {
        if (!e.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return def;
    }

    public sealed class Packet
    {
        public long Pts;
        public long Dts;
        public long Duration;
    }

    /// <summary>Reads video packets (decode order) of the first video stream.</summary>
    public static List<Ff.Packet> ReadVideoPackets(string file, out string error)
    {
        var list = new List<Ff.Packet>();
        var r = Run(Ffprobe, new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts,dts,duration", "-of", "csv=p=0", file
        }, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var f = line.Split(',');
            var p = new Ff.Packet { Pts = long.MinValue, Dts = long.MinValue, Duration = 0 };
            if (f.Length > 0 && long.TryParse(f[0], out var pts)) p.Pts = pts;
            if (f.Length > 1 && long.TryParse(f[1], out var dts)) p.Dts = dts;
            if (f.Length > 2 && long.TryParse(f[2], out var dur)) p.Duration = dur;
            if (p.Pts != long.MinValue) list.Add(p);
        });
        error = r.ExitCode != 0 ? (r.StdErr ?? "").Trim() : null;
        return list;
    }
}

/// <summary>Result of checking whether a stream sits on an exact CFR grid.</summary>
public sealed class GridAnalysis
{
    public bool Retimable;          // every frame maps 1:1 onto the target grid
    public bool AlreadyExact;       // timestamps are already exactly on the grid
    public bool HasReorder;         // pts != dts somewhere (B-frames present)
    public int FrameCount;
    public long Step;               // ticks per frame in the stream timebase
    public double FrameRate;        // timebase/step based fps
    public double EffectiveFps;     // frameCount / real span
    public double SpanSeconds;      // span of the source timestamps
    public string Reason = "";
}

public static class Grid
{
    /// <summary>
    /// Checks whether a stream can be losslessly re-timed onto an exact CFR grid of
    /// <paramref name="fps"/>. This is the core decision: it proves the content already is
    /// frameCount frames of fps content and only the timestamps are rounded/jittered
    /// (exactly what the Jimeng/Dreamina muxer produces, reported as "24.08 fps").
    /// Start offsets are preserved so audio stays aligned.
    /// </summary>
    public static GridAnalysis Analyze(List<Ff.Packet> packets, Rational timeBase, Rational fps, long? forceStep = null)
    {
        var a = new GridAnalysis();
        if (packets == null || packets.Count == 0) { a.Reason = "没有视频帧"; return a; }
        if (!timeBase.IsValid || timeBase.Num != 1) { a.Reason = $"时间基 {timeBase} 不受支持"; return a; }
        if (!fps.IsValid || fps.Num <= 0) { a.Reason = "目标帧率无效"; return a; }

        long step;
        if (forceStep.HasValue)
        {
            step = forceStep.Value;
        }
        else
        {
            long stepNum = (long)timeBase.Den * fps.Den;
            if (stepNum % fps.Num != 0) { a.Reason = $"时间基 1/{timeBase.Den} 无法整除目标帧率 {fps}"; return a; }
            step = stepNum / fps.Num;
        }
        if (step <= 0) { a.Reason = "帧步长无效"; return a; }

        a.Step = step;
        a.FrameCount = packets.Count;
        a.FrameRate = (double)timeBase.Den / step;

        long minPts = packets.Min(p => p.Pts);
        long maxEnd = packets.Max(p => p.Pts + Math.Max(p.Duration, 0));
        a.SpanSeconds = Math.Max(0, maxEnd - minPts) / (double)timeBase.Den;
        a.EffectiveFps = a.SpanSeconds > 0 ? packets.Count / a.SpanSeconds : 0;

        var seen = new HashSet<long>();
        long minSlot = long.MaxValue, maxSlot = long.MinValue;
        long prevDts = long.MinValue;
        bool exact = true;

        foreach (var p in packets)
        {
            long n = (long)Math.Round(p.Pts / (double)step, MidpointRounding.AwayFromZero);
            if (!seen.Add(n))
            {
                a.Reason = $"多帧落在同一帧位 {n}（源并非等间隔 {fps} fps 内容）";
                return a;
            }
            minSlot = Math.Min(minSlot, n);
            maxSlot = Math.Max(maxSlot, n);

            if (p.Dts != long.MinValue)
            {
                long d = (long)Math.Round(p.Dts / (double)step, MidpointRounding.AwayFromZero);
                if (d > n) { a.Reason = "解码时间戳晚于显示时间戳"; return a; }
                if (prevDts != long.MinValue && d <= prevDts) { a.Reason = "解码时间戳非单调"; return a; }
                prevDts = d;
                if (p.Dts != d * step) exact = false;
                if (p.Pts != p.Dts) a.HasReorder = true;
            }
            if (p.Pts != n * step) exact = false;
        }

        if (maxSlot - minSlot != packets.Count - 1)
        {
            a.Reason = $"帧位不连续（占用 {seen.Count} 个帧位，跨度 {maxSlot - minSlot + 1} 帧）";
            return a;
        }
        // the source must not already be shifted by more than half a frame off the grid start
        if (Math.Abs(minPts - minSlot * step) > step / 2)
        {
            a.Reason = "起始时间戳偏离网格";
            return a;
        }

        a.AlreadyExact = exact;
        a.Retimable = true;
        return a;
    }
}
