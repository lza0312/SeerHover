namespace SeerHover;

internal sealed class HoverTarget
{
    public string Path = string.Empty;
    public string Name = string.Empty;
    public IntPtr RootHwnd;
    public Native.RECT ItemRect;
    public bool IsDirectory;
    public override string ToString() => $"'{Name}' -> {Path}";
}

/// <summary>
/// 光标 → 文件绝对路径。
/// 1) 用 Win32 判断光标是否在资源管理器窗口内（排除桌面）；
/// 2) 用 UI Automation 拿到光标下的列表项（文件名 + 矩形）；
/// 3) 用 Shell.Application 取该窗口当前文件夹路径，拼出绝对路径；
/// 4) 同名无扩展名（隐藏扩展名）时按 name.* 回退匹配。
/// </summary>
internal sealed class HoverResolver
{
    private const string ExplorerClass = "CabinetWClass";
    private const string ExplorerProcess = "explorer";
    private const string DesktopClassProgman = "Progman";
    private const string DesktopClassWorkerW = "WorkerW";

    private readonly AppConfig _cfg;

    // 文件夹路径缓存：按句柄 + 短 TTL，避免每次移动都走一遍 Shell.Application（COM 调用很贵）
    private readonly Dictionary<IntPtr, (string Path, long At)> _folderCache = new();

    private object? _shell;

    public HoverResolver(AppConfig cfg) => _cfg = cfg;

    public static bool IsExplorerRoot(IntPtr root)
    {
        if (root == IntPtr.Zero) return false;
        var info = Native.InfoOf(root);
        return info.Class == ExplorerClass &&
               string.Equals(info.Process, ExplorerProcess, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDesktop(IntPtr root)
    {
        string cls = Native.InfoOf(root).Class;
        return cls == DesktopClassProgman || cls == DesktopClassWorkerW;
    }

    /// <summary>光标当前命中的文件（非文件/文件夹/被排除时返回 null）。</summary>
    public HoverTarget? Resolve(Native.POINT cursor) => ResolveCore(cursor, IntPtr.Zero, out _);

    /// <summary>带失败原因的诊断版本；root 传 0 时自行解析。</summary>
    public HoverTarget? ResolveCore(Native.POINT cursor, out string why)
        => ResolveCore(cursor, IntPtr.Zero, out why);

    /// <summary>
    /// root 为光标下顶层窗口（调用方通常已经算过，直接传进来省两次 P/Invoke）。
    /// </summary>
    public HoverTarget? ResolveCore(Native.POINT cursor, IntPtr root, out string why)
    {
        if (root == IntPtr.Zero) root = Native.RootAt(cursor, out _);
        if (root == IntPtr.Zero) { why = "no window under cursor"; return null; }

        if (IsDesktop(root)) { why = "desktop (excluded)"; return null; }
        if (!IsExplorerRoot(root))
        {
            why = Logger.IsVerbose ? "not explorer (" + Native.InfoOf(root).Class + ")" : "not explorer";
            return null;
        }

        var item = ExplorerItemCache.Shared.HitTest(root, cursor);
        if (item == null) { why = "no item rect at point"; return null; }

        string folder = GetFolderPath(root);
        if (string.IsNullOrEmpty(folder)) { why = "folder path unavailable"; return null; }

        string name = item.Name;
        if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) { why = "name has separator"; return null; }

        string candidate = Path.Combine(folder, name);
        string? resolved = null;
        bool isDir = false;

        if (File.Exists(candidate))
        {
            resolved = candidate;
        }
        else if (Directory.Exists(candidate))
        {
            if (!_cfg.PreviewFolders) { why = "folder item (skipped)"; return null; }
            resolved = candidate;
            isDir = true;
        }
        else
        {
            // 资源管理器隐藏已知扩展名时，UIA 名字不含后缀 → 按 name.* 唯一匹配
            try
            {
                var matches = Directory.EnumerateFiles(folder, name + ".*").Take(2).ToList();
                if (matches.Count == 1) resolved = matches[0];
            }
            catch { }
        }

        if (resolved == null) { why = $"path not found: {folder} + '{name}'"; return null; }
        if (_cfg.IsExcluded(resolved)) { why = "excluded by config"; return null; }

        why = "ok";
        return new HoverTarget
        {
            Path = resolved,
            Name = name,
            RootHwnd = root,
            ItemRect = item.Rect,
            IsDirectory = isDir
        };
    }

    /// <summary>取资源管理器窗口当前所在文件夹（Shell.Application 晚期绑定，结果缓存 2 秒）。</summary>
    private string GetFolderPath(IntPtr root)
    {
        long now = Environment.TickCount64;
        if (_folderCache.TryGetValue(root, out var cached) && now - cached.At < 2000)
            return cached.Path;

        string path = QueryFolderPath(root);
        if (path.Length > 0) _folderCache[root] = (path, now);
        else _folderCache.Remove(root);
        return path;
    }

    private string QueryFolderPath(IntPtr root)
    {
        try
        {
            if (_shell == null)
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return string.Empty;
                _shell = Activator.CreateInstance(t);
            }
            if (_shell == null) return string.Empty;

            dynamic shell = _shell;
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic w = windows.Item(i);
                int hwnd;
                try { hwnd = (int)w.HWND; }
                catch { continue; }
                if (new IntPtr(hwnd) != root) continue;

                try
                {
                    dynamic self = w.Document.Folder.Self;
                    string p = (string)self.Path;
                    if (!string.IsNullOrEmpty(p)) return p;
                }
                catch { return string.Empty; }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("shell folder query failed: " + ex.Message);
            _shell = null;
        }
        return string.Empty;
    }

    public void Invalidate()
    {
        _folderCache.Clear();
        ExplorerItemCache.Shared.Invalidate();
    }

    public string DebugFolderPath(IntPtr hwnd) => QueryFolderPath(hwnd);
}
