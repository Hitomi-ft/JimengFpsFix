using System.Text;

namespace JimengFpsFix;

/// <summary>Finds a usable ffmpeg/ffprobe pair: configured path, next to the exe, PATH, common install dirs.</summary>
public static class FfLocator
{
    public static string FfmpegPath = "";
    public static string FfprobePath = "";
    public static string Version = "";

    public static bool Apply(string configured, out string error)
    {
        error = null;
        var dirs = new List<string>();
        void AddDir(string d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            try { d = Path.GetFullPath(Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'))); }
            catch { return; }
            if (!dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                if (File.Exists(configured)) AddDir(Path.GetDirectoryName(Path.GetFullPath(configured)));
                else AddDir(configured);
            }
            catch { }
        }

        var baseDir = AppContext.BaseDirectory;
        AddDir(baseDir);
        AddDir(Path.Combine(baseDir, "bin"));
        AddDir(Path.Combine(baseDir, "ffmpeg"));
        AddDir(Path.Combine(baseDir, "ffmpeg", "bin"));
        AddDir(Path.Combine(baseDir, "tools"));
        AddDir(Path.Combine(baseDir, "tools", "ffmpeg", "bin"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(p)) AddDir(p);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddDir(Path.Combine(home, "scoop", "shims"));
        AddDir(Path.Combine(home, "scoop", "apps", "ffmpeg", "current", "bin"));
        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin"));
        AddDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));

        string firstFf = null, firstFp = null;
        foreach (var d in dirs)
        {
            var ff = Path.Combine(d, "ffmpeg.exe");
            var fp = Path.Combine(d, "ffprobe.exe");
            bool hasFf = File.Exists(ff), hasFp = File.Exists(fp);
            if (hasFf && firstFf == null) firstFf = ff;
            if (hasFp && firstFp == null) firstFp = fp;
            if (hasFf && hasFp)
            {
                FfmpegPath = ff; FfprobePath = fp;
                return Finish(out error);
            }
        }

        // last resort: ffmpeg found somewhere, look for a matching ffprobe next to it
        if (firstFf != null)
        {
            FfmpegPath = firstFf;
            var sibling = Path.Combine(Path.GetDirectoryName(firstFf) ?? "", "ffprobe.exe");
            FfprobePath = File.Exists(sibling) ? sibling : (firstFp ?? "");
        }
        if (string.IsNullOrEmpty(FfmpegPath) || string.IsNullOrEmpty(FfprobePath))
        {
            error = "未找到 ffmpeg.exe / ffprobe.exe，请点击「选择 ffmpeg」指定位置，或把 ffmpeg 放进本程序所在目录。";
            return false;
        }
        return Finish(out error);
    }

    private static bool Finish(out string error)
    {
        error = null;
        Ff.Ffmpeg = FfmpegPath;
        Ff.Ffprobe = FfprobePath;
        Ff.Located = true;
        var sb = new StringBuilder();
        var r = Ff.Run(FfmpegPath, new[] { "-hide_banner", "-version" }, l => sb.AppendLine(l));
        var first = sb.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase));
        Version = first?.Trim() ?? "";
        if (r.ExitCode != 0 && string.IsNullOrEmpty(Version))
        {
            error = "ffmpeg 无法运行: " + r.StdErr;
            Ff.Located = false;
            return false;
        }
        return true;
    }
}
