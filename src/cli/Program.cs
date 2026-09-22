namespace JimengFpsFix;

/// <summary>Console-subsystem entry point (fpsfix.exe) for scripting and future batch jobs.</summary>
internal static class CliProgram
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch { }

        if (args.Length == 0)
        {
            Console.WriteLine("用法: fpsfix [选项] <文件或文件夹>...   （加 --help 查看全部选项）");
            Console.WriteLine("GUI 版请运行 帧率修复工具.exe");
            return 2;
        }

        try
        {
            // make sure --cli is present so the shared runner accepts the invocation
            var list = args.ToList();
            if (!list.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
                list.Insert(0, "--cli");
            return CliRunner.Run(list.ToArray());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("致命错误: " + ex.Message);
            return 2;
        }
    }
}
