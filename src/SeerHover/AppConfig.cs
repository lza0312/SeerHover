using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeerHover;

internal sealed class GeometryConfig
{
    /// <summary>cursor = 跟随光标/文件项旁边；fixed = 固定坐标；corner = 屏幕角落</summary>
    public string Mode { get; set; } = "cursor";

    /// <summary>fixed = 由本工具设定宽高；auto = 按原图/原视频比例最大化；seer = 不干预尺寸，只用 Seer 自己的大小</summary>
    public string SizeMode { get; set; } = "fixed";

    /// <summary>窗口不遮挡鼠标（自动选择空间更大的方向弹出）</summary>
    public bool AvoidCursor { get; set; } = true;

    /// <summary>宽度（物理像素），0 = 保持 Seer 自身尺寸</summary>
    public int Width { get; set; } = 900;

    /// <summary>高度（物理像素），0 = 保持 Seer 自身尺寸。Seer 有最小尺寸（150% 缩放下约 645）</summary>
    public int Height { get; set; } = 700;

    /// <summary>与文件项的间距（cursor 模式）</summary>
    public int Gap { get; set; } = 16;

    /// <summary>fixed 模式坐标</summary>
    public int X { get; set; } = 200;
    public int Y { get; set; } = 200;

    /// <summary>corner 模式：topleft / topright / bottomleft / bottomright</summary>
    public string Corner { get; set; } = "topright";
    public int Margin { get; set; } = 24;

    /// <summary>窗口最多占屏幕比例（0.1 - 1.0）</summary>
    public double MaxScreenRatio { get; set; } = 0.9;
}

internal sealed class AppConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>光标停稳多久触发（毫秒）</summary>
    public int DwellMs { get; set; } = 300;

    /// <summary>光标离开文件项后多久收窗（毫秒）</summary>
    public int HideGraceMs { get; set; } = 250;

    /// <summary>轮询间隔（毫秒）</summary>
    public int PollMs { get; set; } = 55;

    /// <summary>两次触发的最小间隔，Seer IPC 硬性要求 >= 200ms</summary>
    public int MinInvokeIntervalMs { get; set; } = 220;

    /// <summary>目录项是否触发预览</summary>
    public bool PreviewFolders { get; set; } = false;

    /// <summary>按住 Ctrl/Shift/Alt 时不触发（避免干扰多选）</summary>
    public bool SuppressOnModifiers { get; set; } = true;

    /// <summary>不参与悬停预览的扩展名（不含点，小写）</summary>
    public List<string> ExcludeExtensions { get; set; } = new();

    /// <summary>不参与悬停预览的目录（前缀匹配）</summary>
    public List<string> ExcludePaths { get; set; } = new();

    /// <summary>窗口自动关闭（点击文件/滚动/切换窗口时）</summary>
    public bool HideOnClick { get; set; } = true;

    /// <summary>触发后回查 Seer 实际预览的文件，发现不一致就收窗（防止显示错的文件）</summary>
    public bool VerifyPreview { get; set; } = true;

    /// <summary>回查延迟（毫秒），要大于 Seer 内部跟随选中项的刷新间隔</summary>
    public int VerifyDelayMs { get; set; } = 700;

    public bool VerboseLog { get; set; } = false;

    /// <summary>资源管理器文件列表空白处双击 = 回到上一层（等同 Alt+↑）</summary>
    public bool DoubleClickUp { get; set; } = true;

    /// <summary>设置窗口深色模式；null = 跟随系统</summary>
    public bool? DarkMode { get; set; }

    /// <summary>界面缩放百分比（设置窗口 + 托盘右键菜单），100 = 标准大小</summary>
    public int UiScale { get; set; } = 100;

    /// <summary>设置窗口上次的尺寸（0 = 用默认值，关闭窗口时自动记录）</summary>
    public int SettingsWidth { get; set; }
    public int SettingsHeight { get; set; }

    public GeometryConfig Geometry { get; set; } = new();

    [JsonIgnore] public static string Path { get; private set; } = string.Empty;

    /// <summary>界面缩放系数（1.0 = 100%）。</summary>
    [JsonIgnore] public float Scale => UiScale / 100f;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig LoadOrCreate()
    {
        string dir = AppContext.BaseDirectory;
        string path = System.IO.Path.Combine(dir, "config.json");

        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        catch { }

        Path = path;

        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(text, Options);
                if (cfg != null) return cfg.Normalize();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("config read failed: " + ex.Message);
        }

        var fresh = new AppConfig();
        try { fresh.Save(); } catch { }
        return fresh;
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(Path)) return;
            string json = JsonSerializer.Serialize(this, Options);

            // 先写临时文件再替换：写一半崩掉也不会留下半个 config.json
            string tmp = Path + ".tmp";
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, Path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                File.WriteAllText(Path, json);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("config save failed: " + ex.Message);
        }
    }

    /// <summary>深拷贝（设置窗口用副本编辑，取消时不影响运行中的配置）。</summary>
    public AppConfig Clone()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, Options);
            return JsonSerializer.Deserialize<AppConfig>(json, Options)?.Normalize() ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    /// <summary>把另一份配置的内容整体覆盖进来（保持对象引用不变）。不经过 JSON，省一次序列化。</summary>
    public void CopyFrom(AppConfig src)
    {
        if (src == null || ReferenceEquals(src, this)) return;

        Enabled = src.Enabled;
        DwellMs = src.DwellMs;
        HideGraceMs = src.HideGraceMs;
        PollMs = src.PollMs;
        MinInvokeIntervalMs = src.MinInvokeIntervalMs;
        PreviewFolders = src.PreviewFolders;
        SuppressOnModifiers = src.SuppressOnModifiers;
        ExcludeExtensions = new List<string>(src.ExcludeExtensions ?? new List<string>());
        ExcludePaths = new List<string>(src.ExcludePaths ?? new List<string>());
        HideOnClick = src.HideOnClick;
        VerifyPreview = src.VerifyPreview;
        VerifyDelayMs = src.VerifyDelayMs;
        VerboseLog = src.VerboseLog;
        DoubleClickUp = src.DoubleClickUp;
        DarkMode = src.DarkMode;
        UiScale = src.UiScale;
        SettingsWidth = src.SettingsWidth;
        SettingsHeight = src.SettingsHeight;

        var g = src.Geometry ?? new GeometryConfig();
        Geometry = new GeometryConfig
        {
            Mode = g.Mode,
            SizeMode = g.SizeMode,
            AvoidCursor = g.AvoidCursor,
            Width = g.Width,
            Height = g.Height,
            Gap = g.Gap,
            X = g.X,
            Y = g.Y,
            Corner = g.Corner,
            Margin = g.Margin,
            MaxScreenRatio = g.MaxScreenRatio
        };

        _extSet = null;
    }

    public AppConfig Normalize()
    {
        if (DwellMs < 80) DwellMs = 80;
        if (DwellMs > 5000) DwellMs = 5000;
        if (HideGraceMs < 0) HideGraceMs = 0;
        if (PollMs < 25) PollMs = 25;
        if (PollMs > 500) PollMs = 500;
        if (MinInvokeIntervalMs < 200) MinInvokeIntervalMs = 200;
        // 界面缩放：80% 以下字太小、160% 以上设置窗口在 1080p 上塞不下
        if (UiScale < 80) UiScale = 80;
        if (UiScale > 160) UiScale = 160;
        if (SettingsWidth < 0) SettingsWidth = 0;
        if (SettingsHeight < 0) SettingsHeight = 0;
        if (SettingsWidth > 0 && SettingsWidth < 700) SettingsWidth = 0;
        if (SettingsHeight > 0 && SettingsHeight < 460) SettingsHeight = 0;
        if (ExcludeExtensions == null) ExcludeExtensions = new List<string>();
        ExcludeExtensions = ExcludeExtensions
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0).ToList();
        if (ExcludePaths == null) ExcludePaths = new List<string>();
        Geometry ??= new GeometryConfig();
        if (Geometry.MaxScreenRatio < 0.1) Geometry.MaxScreenRatio = 0.1;
        if (Geometry.MaxScreenRatio > 1.0) Geometry.MaxScreenRatio = 1.0;
        if (string.IsNullOrWhiteSpace(Geometry.SizeMode)) Geometry.SizeMode = "fixed";
        Geometry.SizeMode = Geometry.SizeMode.Trim().ToLowerInvariant() switch
        {
            "seer" => "seer",
            "auto" => "auto",
            _ => "fixed"
        };
        Geometry.Mode = (Geometry.Mode ?? "cursor").Trim().ToLowerInvariant() switch
        {
            "fixed" => "fixed",
            "corner" => "corner",
            _ => "cursor"
        };

        // Seer 有最小窗口尺寸（150% 缩放下 765x645），比它小的设置会被立刻夹回，
        // 反复设置就会产生「越设越小、然后被弹回」的抖动。这里直接抬到可用值。
        if (Geometry.Height > 0 && Geometry.Height < 645)
        {
            Logger.Info($"config Height={Geometry.Height} 小于 Seer 最小窗口高度，已提升为 700");
            Geometry.Height = 700;
        }
        if (Geometry.Width > 0 && Geometry.Width < 765)
        {
            Logger.Info($"config Width={Geometry.Width} 小于 Seer 最小窗口宽度，已提升为 900");
            Geometry.Width = 900;
        }

        // 上限 8K（7680x4320），足够 4K / 5K2K 带鱼屏；再大没有意义，还会让 Seer 摆到屏幕外
        if (Geometry.Width > 7680) Geometry.Width = 7680;
        if (Geometry.Height > 4320) Geometry.Height = 4320;

        _extSet = null;   // 排除列表变了，集合缓存作废
        return this;
    }

    public bool IsExcluded(string path)
    {
        string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0 && ExtSet.Contains(ext)) return true;
        foreach (var p in ExcludePaths)
        {
            if (p.Length == 0) continue;
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 排除扩展名用集合查询（每次悬停命中都会走，列表推导一次就够）
    private volatile HashSet<string>? _extSet;

    private HashSet<string> ExtSet
    {
        get
        {
            var set = _extSet;
            if (set != null) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in ExcludeExtensions)
            {
                string v = (e ?? string.Empty).Trim().TrimStart('.');
                if (v.Length > 0) set.Add(v);
            }
            _extSet = set;
            return set;
        }
    }
}
