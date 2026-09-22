using System.Text.Json;
using System.Text.Json.Serialization;

namespace JimengFpsFix;

public sealed class AppSettings
{
    public string Fps { get; set; } = "24";
    public int MethodIndex { get; set; } = 0;      // 0 auto, 1 lossless only, 2 re-encode
    public int EncodeIndex { get; set; } = 0;      // 0 auto, 1 keep frames, 2 conform
    public int AudioIndex { get; set; } = 0;       // 0 auto, 1 copy, 2 tempo, 3 fit length
    public int OutputIndex { get; set; } = 0;      // 0 sibling, 1 output dir, 2 overwrite
    public string OutputDir { get; set; } = "";
    public string Suffix { get; set; } = "_24fps";
    public int Crf { get; set; } = 16;
    public string Preset { get; set; } = "medium";
    public bool ForceYuv420p { get; set; } = true;
    public bool DeepVerify { get; set; } = true;
    public bool Force { get; set; } = false;
    public int Parallel { get; set; } = 1;
    public bool Recursive { get; set; } = true;
    public string FfmpegPath { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string SettingsFile { get; } = ResolvePath();

    private static string ResolvePath()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "JimengFpsFix.settings.json");
            var probe = p + ".test";
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return p;
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JimengFpsFix");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts)); } catch { }
    }

    public RepairOptions ToOptions(out string fpsError)
    {
        fpsError = null;
        var o = new RepairOptions
        {
            FpsText = Fps,
            Method = (MethodChoice)Math.Clamp(MethodIndex, 0, 2),
            Encode = (EncodeMode)Math.Clamp(EncodeIndex, 0, 2),
            Audio = (AudioMode)Math.Clamp(AudioIndex, 0, 3),
            Output = (OutputMode)Math.Clamp(OutputIndex, 0, 2),
            OutputDir = OutputDir,
            Suffix = Suffix,
            Crf = Math.Clamp(Crf, 0, 40),
            Preset = string.IsNullOrWhiteSpace(Preset) ? "medium" : Preset,
            ForceYuv420p = ForceYuv420p,
            DeepVerify = DeepVerify,
            Force = Force,
            Parallel = Math.Clamp(Parallel, 1, 8),
        };
        if (!TryParseFps(Fps, out var fps)) { fpsError = $"无法识别的帧率：{Fps}"; return o; }
        o.Fps = fps;
        return o;
    }

    public static bool TryParseFps(string text, out Rational fps)
    {
        fps = new Rational(24, 1);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Contains('/'))
        {
            var parts = text.Split('/');
            if (parts.Length == 2 && long.TryParse(parts[0], out var n) && long.TryParse(parts[1], out var d) && n > 0 && d > 0)
            {
                fps = new Rational(n, d);
                return true;
            }
            return false;
        }
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 1000) return false;
        foreach (var (num, den, label) in Ntsc)
        {
            if (Math.Abs(v - (double)num / den) < 0.005) { fps = new Rational(num, den); return true; }
        }
        fps = Rational.FromDouble(v);
        return true;
    }

    private static readonly (long num, long den, string label)[] Ntsc =
    {
        (24000, 1001, "23.976"), (30000, 1001, "29.97"), (60000, 1001, "59.94"), (120000, 1001, "119.88"),
    };
}
