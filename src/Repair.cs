using System.Globalization;
using System.Text;

namespace JimengFpsFix;

public enum MethodChoice { Auto, CopyOnly, ReencodeOnly }
public enum EncodeMode { Auto, KeepFrames, Conform }
public enum AudioMode { Auto, Copy, Tempo, FitLength }
public enum OutputMode { SiblingSuffix, OutputDir, Overwrite }
public enum UsedMethod { CopyRetime, ReencodeRetime, ReencodeConform }

public sealed class RepairOptions
{
    public Rational Fps = new(24, 1);
    public string FpsText = "24";
    public MethodChoice Method = MethodChoice.Auto;
    public EncodeMode Encode = EncodeMode.Auto;
    public AudioMode Audio = AudioMode.Auto;
    public int Crf = 16;
    public string Preset = "medium";
    public bool ForceYuv420p = false;
    public OutputMode Output = OutputMode.SiblingSuffix;
    public string OutputDir = "";
    public string Suffix = "_24fps";
    public bool Force = false;          // do not skip files that are already exact
    public bool DeepVerify = true;      // full decode pass after writing
    public int Parallel = 1;

    /// <summary>Audio speed correction is applied only when the video length changes by more than this.</summary>
    public const double TempoThreshold = 0.005;
}

public sealed class RepairResult
{
    public string Input = "";
    public string Output = "";
    public bool Ok;
    public bool Skipped;
    public UsedMethod? Method;
    public string Plan = "";
    public string Message = "";
    public double Seconds;
    public int SourceFrames = -1;
    public double SourceFps;
    public bool AudioKeptBitExact;
}

internal sealed class Plan
{
    public UsedMethod Method;
    public long Step;
    public int Frames;
    public double SourceSpan;
    public double TargetDuration;
    public double Tempo = 1.0;
    public bool ApplyTempo;
    public bool FitLength;
    public bool ReencodeAudio;
    public string Description = "";
}

public sealed class RepairEngine
{
    private readonly RepairOptions _o;
    private readonly Action<string> _log;

    public RepairEngine(RepairOptions options, Action<string> log)
    {
        _o = options;
        _log = log ?? (_ => { });
    }

    private void Log(string s) => _log(s);

    // ------------------------------------------------------------------ main

    public RepairResult Process(string file, CancellationToken ct, Action<double> progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new RepairResult { Input = file };
        string tmp = null;
        try
        {
            if (!File.Exists(file)) { r.Message = "文件不存在"; return r; }

            var info = Ff.Probe(file, out var perr);
            if (info == null) { r.Message = "探测失败: " + perr; return r; }
            var v = info.FirstVideo;
            if (v == null) { r.Message = "没有视频流"; return r; }
            var a = info.FirstAudio;

            var packets = Ff.ReadVideoPackets(file, out var pkerr);
            if (packets.Count == 0) { r.Message = "读不到视频帧" + (pkerr == null ? "" : ": " + pkerr); return r; }
            r.SourceFrames = packets.Count;
            r.SourceFps = v.AvgFrameRate.IsValid && v.AvgFrameRate.Value > 0
                        ? v.AvgFrameRate.Value
                        : (info.FormatDuration > 0 ? packets.Count / info.FormatDuration : 0);

            // ---- can we fix it losslessly by rewriting timestamps?
            // (lossless retiming needs the stream timebase to express exactly one frame,
            //  e.g. 1/15360 for 24 fps -> 640 ticks. Otherwise we re-encode.)
            var grid = Grid.Analyze(packets, v.TimeBase, _o.Fps);
            long step = grid.Step;

            if (!_o.Force && grid.Retimable && grid.AlreadyExact && v.AvgFrameRate.IsValid && v.AvgFrameRate.EqualsRate(_o.Fps))
            {
                r.Ok = true; r.Skipped = true; r.Message = $"源已是精确 {_o.Fps} fps，无需修复";
                r.Seconds = sw.Elapsed.TotalSeconds;
                return r;
            }

            var plan = BuildPlan(grid, packets, v, a, step);
            if (plan == null) { r.Message = grid.Retimable ? "无法生成修复方案" : grid.Reason; return r; }

            if (plan.Method != UsedMethod.CopyRetime && _o.Method == MethodChoice.CopyOnly)
            {
                r.Message = "源无法无损修复: " + grid.Reason;
                return r;
            }

            r.Plan = plan.Description;
            r.Method = plan.Method;
            r.Output = ResolveOutput(file);

            var dir = Path.GetDirectoryName(r.Output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            tmp = r.Output + ".part-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4";

            Log($"[{Path.GetFileName(file)}] {plan.Description}");

            string err;
            bool ok = Encode(file, tmp, plan, info, v, a, ct, progress, out err);
            if (ok) ok = Verify(file, tmp, plan, r, ct, out err);

            if (!ok && plan.Method == UsedMethod.CopyRetime && _o.Method == MethodChoice.Auto)
            {
                Log($"[{Path.GetFileName(file)}] 无损方案未通过校验（{r.Message}），自动改用重编码修复…");
                Cleanup(tmp);
                var fallback = BuildPlan(grid, packets, v, a, step, forceEncode: true);
                if (fallback != null)
                {
                    plan = fallback;
                    r.Plan = plan.Description;
                    r.Method = plan.Method;
                    r.Message = "";
                    ok = Encode(file, tmp, plan, info, v, a, ct, progress, out err);
                    if (ok) ok = Verify(file, tmp, plan, r, ct, out err);
                }
            }

            if (!ok)
            {
                Cleanup(tmp);
                r.Message = string.IsNullOrWhiteSpace(r.Message) ? (err ?? "修复失败") : r.Message;
                r.Ok = false;
                r.Seconds = sw.Elapsed.TotalSeconds;
                return r;
            }

            try
            {
                if (File.Exists(r.Output) && !string.Equals(r.Output, file, StringComparison.OrdinalIgnoreCase))
                    File.Delete(r.Output);
                File.Move(tmp, r.Output);
                tmp = null;
            }
            catch (Exception ex)
            {
                Cleanup(tmp);
                r.Message = "无法写入输出文件: " + ex.Message;
                return r;
            }

            r.Ok = true;
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }
        catch (OperationCanceledException)
        {
            Cleanup(tmp);
            r.Message = "已取消";
            return r;
        }
        catch (Exception ex)
        {
            Cleanup(tmp);
            r.Message = "异常: " + ex.Message;
            return r;
        }
    }

    private static void Cleanup(string tmp)
    {
        try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    // ------------------------------------------------------------------ plan

    private Plan BuildPlan(GridAnalysis grid, List<Ff.Packet> packets, StreamInfo v, StreamInfo a,
                           long step, bool forceEncode = false)
    {
        var p = new Plan { Frames = packets.Count, Step = step };
        if (!grid.Retimable && _o.Method == MethodChoice.CopyOnly) return null;

        p.SourceSpan = grid.Retimable ? grid.SpanSeconds
                                      : ComputeSpan(packets, v.TimeBase);
        p.TargetDuration = packets.Count * _o.Fps.Den / (double)_o.Fps.Num;

        double tempo = p.SourceSpan > 0 ? p.SourceSpan / p.TargetDuration : 1.0;
        if (tempo <= 0 || double.IsNaN(tempo)) tempo = 1.0;

        var audioCompatible = a == null || IsMp4Audio(a.CodecName);
        bool canCopy = grid.Retimable && _o.Method != MethodChoice.ReencodeOnly && !forceEncode;

        if (canCopy)
        {
            p.Method = UsedMethod.CopyRetime;
            p.Description = $"无损重写时间戳：{p.Frames} 帧全部保留，步长 {step} tick（时间基 1/{v.TimeBase.Den}），音频原样复制";
        }
        else
        {
            var mode = _o.Encode;
            if (mode == EncodeMode.Auto)
            {
                double eff = grid.Retimable ? grid.FrameRate : grid.EffectiveFps;
                mode = (eff > 0 && Math.Abs(eff - _o.Fps.Value) / _o.Fps.Value <= 0.015)
                     ? EncodeMode.KeepFrames : EncodeMode.Conform;
            }
            p.Method = mode == EncodeMode.KeepFrames ? UsedMethod.ReencodeRetime : UsedMethod.ReencodeConform;
            p.Description = mode == EncodeMode.KeepFrames
                ? $"重编码（保留全部 {p.Frames} 帧，按 {_o.Fps} fps 重新计时，CRF {_o.Crf}/{_o.Preset}）"
                : $"重编码（标准帧率转换到 {_o.Fps} fps，允许丢帧/复制帧，CRF {_o.Crf}/{_o.Preset}）";
            if (!grid.Retimable) p.Description += $"；原因：{grid.Reason}";
            if (p.Method == UsedMethod.ReencodeConform) tempo = 1.0;   // duration is preserved by the fps filter
        }

        // ---- audio strategy
        switch (_o.Audio)
        {
            case AudioMode.Copy: p.ReencodeAudio = !audioCompatible; break;
            case AudioMode.Tempo:
                p.ReencodeAudio = true; p.ApplyTempo = true; p.Tempo = tempo; break;
            case AudioMode.FitLength:
                p.ReencodeAudio = true; p.FitLength = true; p.Tempo = tempo;
                p.ApplyTempo = Math.Abs(tempo - 1.0) > RepairOptions.TempoThreshold;
                break;
            default: // Auto
                p.ApplyTempo = Math.Abs(tempo - 1.0) > RepairOptions.TempoThreshold;
                p.ReencodeAudio = !audioCompatible || p.ApplyTempo;
                p.Tempo = tempo;
                break;
        }

        if (p.ReencodeAudio && !audioCompatible && !p.ApplyTempo)
            Log("  音频编码 " + a.CodecName + " 与 MP4 不兼容，已改为 AAC 重编码");
        if (p.ApplyTempo)
            p.Description += $"；音频变速 {p.Tempo:0.#####}× 以保持同步";
        if (_o.Audio == AudioMode.Auto && a != null && !p.ApplyTempo && !p.ReencodeAudio)
            p.Description += "（时长变化 <0.5%，音频不做任何处理）";

        if (a == null) p.ReencodeAudio = false;
        return p;
    }

    private static double ComputeSpan(List<Ff.Packet> packets, Rational tb)
    {
        long min = packets.Min(x => x.Pts);
        long max = packets.Max(x => x.Pts + Math.Max(x.Duration, 0));
        return tb.Den > 0 ? (max - min) / (double)tb.Den : 0;
    }

    private static bool IsMp4Audio(string codec) => codec switch
    {
        "aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus" or "flac" => true,
        _ => false,
    };

    // ------------------------------------------------------------------ encode

    private bool Encode(string input, string output, Plan plan, MediaInfo info, StreamInfo v, StreamInfo a,
                        CancellationToken ct, Action<double> progress, out string error)
    {
        error = null;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "warning",
            "-stats_period", "0.5", "-progress", "pipe:1",
            "-i", input,
            "-map", "0:v:0",
        };
        if (a != null) args.AddRange(new[] { "-map", "0:a?" });
        args.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "0" });

        if (plan.Method == UsedMethod.CopyRetime)
        {
            var expr = $"setts=pts=round(PTS/{plan.Step})*{plan.Step}:dts=round(DTS/{plan.Step})*{plan.Step}:duration={plan.Step}";
            args.AddRange(new[] { "-c:v", "copy", "-bsf:v", expr });
        }
        else
        {
            // KeepFrames : re-time every frame onto the target grid (no frames lost, audio follows via atempo)
            // Conform    : real rate conversion - the explicit fps filter reproduces the source duration
            //              exactly, while plain -r adds phantom tail frames.
            string vf;
            if (plan.Method == UsedMethod.ReencodeRetime)
            {
                long num = _o.Fps.Num, den = _o.Fps.Den;
                vf = den == 1 ? $"setpts=N/({num}*TB)" : $"setpts=N*{den}/({num}*TB)";
            }
            else
            {
                vf = "fps=" + _o.Fps.ToString();
            }
            args.AddRange(new[] { "-vf", vf, "-fps_mode", "cfr", "-r", _o.Fps.ToString() });
            args.AddRange(new[] { "-c:v", "libx264", "-crf", _o.Crf.ToString(CultureInfo.InvariantCulture),
                                   "-preset", _o.Preset });
            if (_o.ForceYuv420p) args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        }

        if (a != null)
        {
            if (plan.ReencodeAudio)
            {
                var filters = new List<string>();
                if (plan.ApplyTempo) filters.Add(AtempoChain(plan.Tempo));
                if (plan.FitLength)
                {
                    string dur = plan.TargetDuration.ToString("0.######", CultureInfo.InvariantCulture);
                    filters.Add($"apad=whole_dur={dur}");
                    filters.Add($"atrim=end={dur}");
                    filters.Add("asetpts=N/SR/TB");
                }
                if (filters.Count > 0) args.AddRange(new[] { "-af", string.Join(",", filters) });
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
            }
            else
            {
                args.AddRange(new[] { "-c:a", "copy" });
            }
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", output });

        double expected = plan.TargetDuration > 0 ? plan.TargetDuration : info.FormatDuration;
        double lastPct = -1;
        var r = Ff.Run(Ff.Ffmpeg, args, line =>
        {
            if (progress == null) return;
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            var key = line.Substring(0, eq);
            var val = line.Substring(eq + 1);
            if (key == "out_time_us" && long.TryParse(val, out var us) && expected > 0)
            {
                double pct = Math.Clamp(us / 1_000_000.0 / expected, 0, 1);
                if (pct - lastPct >= 0.005 || pct >= 1) { lastPct = pct; progress(pct); }
            }
        }, null, ct);

        if (r.ExitCode != 0)
        {
            error = Summarize(r.StdErr, r.ExitCode);
            return false;
        }
        return true;
    }

    internal static string AtempoChain(double r)
    {
        var parts = new List<string>();
        int guard = 0;
        while (r < 0.49 && guard++ < 8) { parts.Add("atempo=0.5"); r /= 0.5; }
        while (r > 2.01 && guard++ < 8) { parts.Add("atempo=2.0"); r /= 2.0; }
        parts.Add("atempo=" + r.ToString("0.######", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    private static string Summarize(string stderr, int code)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return $"ffmpeg 退出码 {code}";
        var lines = stderr.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var tail = lines.TakeLast(4).ToList();
        return string.Join(" | ", tail);
    }

    // ------------------------------------------------------------------ verify

    /// <summary>Independent verification of the produced file; fills r.Message on failure.</summary>
    private bool Verify(string original, string output, Plan plan, RepairResult r, CancellationToken ct, out string error)
    {
        error = null;
        if (!File.Exists(output)) { r.Message = "输出文件不存在"; return false; }

        var src = Ff.Probe(original, out _);
        var info = Ff.Probe(output, out var perr);
        if (info == null) { r.Message = "输出探测失败: " + perr; return false; }
        var v = info.FirstVideo;
        if (v == null) { r.Message = "输出没有视频流"; return false; }

        if (!v.AvgFrameRate.IsValid || !v.AvgFrameRate.EqualsRate(_o.Fps))
        {
            r.Message = $"输出帧率不是精确 {_o.Fps} fps（实际 {v.AvgFrameRate}）";
            return false;
        }

        var pk = Ff.ReadVideoPackets(output, out _);
        if (pk.Count == 0) { r.Message = "输出读不到视频帧"; return false; }
        if (plan.Method != UsedMethod.ReencodeConform && pk.Count != plan.Frames)
        {
            r.Message = $"输出帧数 {pk.Count} ≠ 源帧数 {plan.Frames}";
            return false;
        }

        var g = Grid.Analyze(pk, v.TimeBase, _o.Fps);
        if (!g.Retimable || !g.AlreadyExact)
        {
            r.Message = "输出时间戳未落在精确 " + _o.Fps + " fps 网格上" + (g.Retimable ? "" : "（" + g.Reason + "）");
            return false;
        }

        double expectDur = pk.Count * _o.Fps.Den / (double)_o.Fps.Num;
        if (v.Duration > 0 && Math.Abs(v.Duration - expectDur) > 1.5 * _o.Fps.Den / _o.Fps.Num + 0.001)
        {
            r.Message = $"输出时长 {v.Duration:0.###}s 与预期 {expectDur:0.###}s 不符";
            return false;
        }

        if (src?.FirstVideo != null && Math.Abs(v.StartTime - src.FirstVideo.StartTime) > 0.05)
        {
            r.Message = $"输出视频起始时间 {v.StartTime:0.###}s 与源 {src.FirstVideo.StartTime:0.###}s 不一致";
            return false;
        }

        // ---- audio
        var sa = src?.FirstAudio;
        var oa = info.FirstAudio;
        if (sa != null)
        {
            if (oa == null) { r.Message = "输出丢失了音频流"; return false; }
            if (Math.Abs(oa.StartTime - sa.StartTime) > 0.02)
            {
                r.Message = $"音频起始时间偏移 {Math.Abs(oa.StartTime - sa.StartTime) * 1000:0.#} ms（源 {sa.StartTime:0.###}s，输出 {oa.StartTime:0.###}s）";
                return false;
            }
            if (!plan.ReencodeAudio)
            {
                if (sa.Duration > 0 && oa.Duration > 0 && Math.Abs(oa.Duration - sa.Duration) > 0.05)
                {
                    r.Message = "音频时长发生变化（应为原样复制）";
                    return false;
                }
                r.AudioKeptBitExact = true;
            }
            else if (plan.ApplyTempo && sa.Duration > 0 && oa.Duration > 0)
            {
                double want = sa.Duration / plan.Tempo;
                if (Math.Abs(oa.Duration - want) > Math.Max(0.25, want * 0.05))
                {
                    r.Message = $"音频变速后时长 {oa.Duration:0.###}s 与预期 {want:0.###}s 不符";
                    return false;
                }
            }
            double slack = _o.Fps.Den / (double)_o.Fps.Num + 0.15;
            if (oa.Duration > 0 && v.Duration > 0 && oa.Duration < v.Duration - slack)
            {
                r.Message = $"音频比视频短 {v.Duration - oa.Duration:0.###}s，可能出现不同步";
                return false;
            }
        }

        if (_o.DeepVerify)
        {
            var d = Ff.Run(Ff.Ffmpeg, new[]
            {
                "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-err_detect", "explode",
                "-i", output, "-map", "0:v:0", "-f", "null", "-"
            }, null, null, ct);
            if (d.ExitCode != 0)
            {
                r.Message = "输出完整解码校验失败: " + Summarize(d.StdErr, d.ExitCode);
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ paths

    private string ResolveOutput(string input)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(input);
        if (_o.Output == OutputMode.OutputDir && !string.IsNullOrWhiteSpace(_o.OutputDir))
            dir = _o.OutputDir;
        if (_o.Output == OutputMode.Overwrite)
            return Path.GetFullPath(input);
        var suffix = string.IsNullOrWhiteSpace(_o.Suffix) ? "" : _o.Suffix;
        var outPath = Path.Combine(dir, baseName + suffix + ".mp4");
        if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            outPath = Path.Combine(dir, baseName + suffix + "_fixed.mp4");
        return outPath;
    }
}

public static class FileCollect
{
    private static readonly string[] Exts = { ".mp4", ".mov", ".mkv", ".m4v", ".avi", ".webm", ".ts", ".flv", ".wmv", ".mpg", ".mpeg" };

    public static List<string> Expand(IEnumerable<string> paths, bool recursive)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (Directory.Exists(p))
            {
                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(p, "*", opt))
                    if (Exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && seen.Add(f)) result.Add(f);
            }
            else if (File.Exists(p) && seen.Add(p))
            {
                result.Add(p);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
