using System.Globalization;
using System.Text;

namespace JimengFpsFix;

/// <summary>Head-less batch mode so the same exe can be scripted later (拖到 exe 上或命令行调用).</summary>
public static class CliRunner
{
    public static int Run(string[] args)
    {
        var files = new List<string>();
        var s = new AppSettings();
        string logFile = null, fpsText = null;
        bool recursive = s.Recursive, quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--cli": case "-c": break;
                case "--help": case "-h": case "/?": PrintHelp(); return 0;
                case "--fps": fpsText = Next(); break;
                case "--method":
                    var m = (Next() ?? "").ToLowerInvariant();
                    s.MethodIndex = m.StartsWith("copy") ? 1 : m.StartsWith("enc") || m.StartsWith("re") ? 2 : 0;
                    break;
                case "--encode":
                    var e = (Next() ?? "").ToLowerInvariant();
                    s.EncodeIndex = e.StartsWith("keep") ? 1 : e.StartsWith("conf") ? 2 : 0;
                    break;
                case "--audio":
                    var au = (Next() ?? "").ToLowerInvariant();
                    s.AudioIndex = au.StartsWith("copy") ? 1 : au.StartsWith("tempo") ? 2 : au.StartsWith("fit") ? 3 : 0;
                    break;
                case "--out": s.OutputDir = Next(); s.OutputIndex = 1; break;
                case "--suffix": s.Suffix = Next() ?? ""; break;
                case "--overwrite": s.OutputIndex = 2; break;
                case "--crf": if (int.TryParse(Next(), out var crf)) s.Crf = crf; break;
                case "--preset": s.Preset = Next() ?? s.Preset; break;
                case "--parallel": if (int.TryParse(Next(), out var par)) s.Parallel = par; break;
                case "--force": s.Force = true; break;
                case "--no-yuv420p": s.ForceYuv420p = false; break;
                case "--no-deep-verify": s.DeepVerify = false; break;
                case "--no-recursive": recursive = false; break;
                case "--ffmpeg": s.FfmpegPath = Next() ?? ""; break;
                case "--log": logFile = Next(); break;
                case "--quiet": case "-q": quiet = true; break;
                default:
                    if (a.StartsWith("-")) { Console.Error.WriteLine("未知参数: " + a); PrintHelp(); return 2; }
                    files.Add(a);
                    break;
            }
        }

        if (fpsText != null) s.Fps = fpsText;

        var sw = new StringBuilder();
        void Say(string line)
        {
            sw.AppendLine(line);
            if (!quiet) Console.WriteLine(line);
        }

        void FlushLog()
        {
            if (logFile != null)
            {
                try { File.AppendAllText(logFile, sw.ToString(), Encoding.UTF8); } catch { }
            }
        }

        if (files.Count == 0)
        {
            PrintHelp();
            return 2;
        }

        if (!FfLocator.Apply(s.FfmpegPath, out var ffErr))
        {
            Say("错误: " + ffErr);
            FlushLog();
            return 2;
        }
        Say($"ffmpeg: {FfLocator.FfmpegPath}");
        Say($"ffprobe: {FfLocator.FfprobePath}");

        var opts = s.ToOptions(out var fpsErr);
        if (fpsErr != null) { Say("错误: " + fpsErr); FlushLog(); return 2; }
        Say($"目标帧率: {opts.Fps}   修复方式: {s.MethodIndex}   输出: {s.OutputIndex}");

        var list = FileCollect.Expand(files, recursive);
        if (list.Count == 0) { Say("没有找到可处理的视频文件"); FlushLog(); return 2; }
        Say($"待处理文件: {list.Count} 个");
        Say(new string('-', 78));

        int ok = 0, skipped = 0, failed = 0;
        var engine = new RepairEngine(opts, line => { sw.AppendLine("    " + line); if (!quiet) Console.WriteLine("    " + line); });
        foreach (var f in list)
        {
            var res = engine.Process(f, CancellationToken.None);
            if (res.Ok && res.Skipped) skipped++;
            else if (res.Ok) ok++;
            else failed++;

            string tag = res.Skipped ? "SKIP" : res.Ok ? " OK " : "FAIL";
            var sb = new StringBuilder();
            sb.Append($"[{tag}] {Path.GetFileName(f)}");
            if (res.SourceFrames >= 0) sb.Append($"  {res.SourceFrames}帧 {res.SourceFps:0.####}fps");
            if (res.Ok && !res.Skipped) sb.Append($" -> {opts.Fps} fps  {res.Seconds:0.00}s");
            sb.Append("  ").Append(res.Ok ? (res.Skipped ? res.Message : Path.GetFileName(res.Output) + (res.AudioKeptBitExact ? "  [音频原样]" : "")) : res.Message);
            Say(sb.ToString());
        }

        Say(new string('-', 78));
        Say($"完成: 成功 {ok}，跳过 {skipped}，失败 {failed}");
        FlushLog();
        return failed == 0 ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"帧率修复工具 (JimengFpsFix) - 批量修复异常帧率为精确 CFR 帧率

用法:
  JimengFpsFix.exe --cli [选项] <文件或文件夹>...

选项:
  --fps <值>          目标帧率，默认 24（支持 24 / 23.976 / 24000/1001）
  --method <模式>     auto(默认) | copy(仅无损) | encode(强制重编码)
  --encode <模式>     重编码时: auto(默认) | keep(保留全部帧) | conform(标准丢帧/复制)
  --audio <模式>      auto(默认) | copy(原样) | tempo(变速对齐) | fit(与视频等长)
  --out <目录>        输出目录（默认与源文件同目录）
  --suffix <后缀>     输出文件名后缀，默认 _24fps
  --overwrite         校验通过后用结果覆盖源文件
  --crf <0-40>        重编码质量，默认 16（越小越好）
  --preset <名称>     x264 preset，默认 medium
  --parallel <N>      并行处理的文件数，默认 1
  --force             已达标文件也重新处理
  --no-yuv420p        重编码时不强制 yuv420p
  --no-deep-verify    跳过完整解码校验（更快）
  --no-recursive      添加文件夹时不包含子文件夹
  --ffmpeg <路径>     指定 ffmpeg.exe
  --log <文件>        额外把日志写入文件
  -q, --quiet         不输出到控制台

退出码: 0 全部成功，1 有文件失败，2 参数或环境错误");
    }
}
