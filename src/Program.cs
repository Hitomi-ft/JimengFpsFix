using System.Runtime.InteropServices;
using System.Text;

namespace JimengFpsFix;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        bool cli = args.Any(a =>
            a.Equals("--cli", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
            a == "/?");

        if (cli)
        {
            SetupConsole();
            try { return CliRunner.Run(args); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("致命错误: " + ex.Message); } catch { }
                return 2;
            }
        }

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.Run(new MainForm(args));
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show("程序启动失败:\r\n" + ex.Message, "帧率修复工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            return 1;
        }
    }

    /// <summary>
    /// A WinExe has no console. If stdout was redirected (scripts) we must NOT call
    /// AttachConsole, because that would replace the inherited handle and swallow the output.
    /// </summary>
    private static void SetupConsole()
    {
        try
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            bool redirected = h != IntPtr.Zero && h != new IntPtr(-1);
            if (redirected)
            {
                try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
                return;
            }
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
            try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true }); } catch { }
            try { Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true }); } catch { }
        }
        catch { }
    }
}
