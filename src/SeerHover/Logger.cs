using System.Text;

namespace SeerHover;

/// <summary>
/// 轻量日志。热路径先判 <see cref="IsVerbose"/>，避免关日志时还去拼字符串；
/// 写文件保持「逐行追加」，这样日志可以被编辑器随时打开（常开句柄会让部分编辑器读取失败）。
/// 崩溃记录和正常运行日志统一放在 %APPDATA%\SeerHover，避免排查时两个地方都找不到。
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();
    private static string? _path;
    private static volatile bool _verbose;

    // 同一条错误在 2 秒内只落盘一次：异常循环时不会把日志刷爆。
    private const int ErrorThrottleMs = 2000;
    private static readonly Dictionary<string, long> LastErrors = new(StringComparer.Ordinal);

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeerHover");

    public static string CrashLogPath => Path.Combine(Directory, "crash.log");

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
        lock (Sync) WriteUnlocked("INFO", message);
    }

    public static void Error(string message)
    {
        lock (Sync)
        {
            long now = Environment.TickCount64;
            if (LastErrors.TryGetValue(message, out long last) && now - last < ErrorThrottleMs) return;
            LastErrors[message] = now;
            if (LastErrors.Count > 128) LastErrors.Clear();   // 异常文本带变量时防止字典无限增长
            WriteUnlocked("ERR ", message);
        }
    }

    private static void WriteUnlocked(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        try
        {
            if (_path != null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
        if (Environment.UserInteractive && _verbose) Console.WriteLine(line);
    }

    /// <summary>进程级崩溃兜底：不依赖 Init，任何时候都能落盘。</summary>
    public static void Crash(string source, Exception? ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}\n{ex}\n\n", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// 启动时取走上次崩溃记录并归档成 crash.log.1，避免每次启动重复提示。
    /// 返回 null 表示没有新崩溃。
    /// </summary>
    public static string? TakeCrashReport()
    {
        try
        {
            if (!File.Exists(CrashLogPath)) return null;
            string text = File.ReadAllText(CrashLogPath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            File.Move(CrashLogPath, CrashLogPath + ".1", true);
            return text.Length > 1000 ? text[..1000] + "…" : text;
        }
        catch { return null; }
    }
}
