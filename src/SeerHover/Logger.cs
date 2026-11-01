using System.Text;

namespace SeerHover;

/// <summary>
/// 轻量日志。热路径先判 <see cref="IsVerbose"/>，避免关日志时还去拼字符串；
/// 写文件保持「逐行追加」，这样日志可以被编辑器随时打开（常开句柄会让部分编辑器读取失败）。
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();
    private static string? _path;
    private static volatile bool _verbose;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeerHover");

    /// <summary>关日志时调用方应先判这个，省掉无谓的字符串拼接与 ToString。</summary>
    public static bool IsVerbose => _verbose;

    public static void Init(bool verbose)
    {
        _verbose = verbose;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            _path = Path.Combine(Directory, "seerhover.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 2 * 1024 * 1024)
                File.Move(_path, _path + ".1", true);
        }
        catch { _path = null; }
    }

    public static void EnableVerbose(bool on) => _verbose = on;

    public static void Info(string message)
    {
        if (!_verbose) return;
        Write("INFO", message);
    }

    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        lock (Sync)
        {
            try
            {
                if (_path != null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        if (Environment.UserInteractive && _verbose) Console.WriteLine(line);
    }
}
