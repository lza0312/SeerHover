using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace SeerHover;

/// <summary>
/// 取图片 / 视频的原始像素尺寸，用于「原比例最大化」定位。
/// 图片常见格式走 GDI+ 读头，冷门格式（HEIC/WebP 等）退回 WIC；视频走 Shell 属性。
///
/// 性能取舍：
/// - 以前每次探测都新建一根 STA 线程 + 每次新建 Shell.Application（COM 实例 + 线程各 ~1-10ms，
///   快速划过一屏图片时会堆出几十根线程）。
/// - 现在固定一根后台 STA 工作线程 + 一个 Shell 实例，任务排队执行；同一文件不会重复排，
///   排队过久（用户已经划走）的预热任务直接丢弃。
/// - 结果按 路径 + 修改时间 缓存 10 分钟；缓存命中是纯内存查表。
/// </summary>
internal static class MediaSize
{
    private sealed record Entry(int W, int H, DateTime Stamp, long At);

    private sealed class Job
    {
        public string Path = string.Empty;
        public DateTime Stamp;
        public readonly ManualResetEventSlim Done = new(false);
        public int W;
        public int H;
    }

    private const int SyncWaitMs = 600;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<Job> Queue = new();

    private static readonly string[] VideoExt =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".rmvb", ".3gp"
    };

    private static readonly Lazy<bool> WorkerStarted = new(StartWorker, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>同步探测（有缓存时几乎零开销）。取不到返回 (0,0)。</summary>
    public static (int W, int H) Probe(string path)
    {
        if (TryCached(path, out var cached)) return cached;

        Job? job = null;
        bool waited = false;
        try
        {
            job = Enqueue(path, wait: true);
            if (job == null) return (0, 0);
            waited = true;
            if (!job.Done.Wait(SyncWaitMs))
            {
                if (Logger.IsVerbose) Logger.Info("media probe timeout: " + Path.GetFileName(path));
                return (0, 0);
            }
            return (job.W, job.H);
        }
        catch (Exception ex)
        {
            Logger.Info("media probe failed: " + ex.Message);
            return (0, 0);
        }
        finally
        {
            // 只在确实等过的情况下释放事件（工作线程的 Set 已容错，释放竞态不会炸线程）
            if (waited) job?.Done.Dispose();
        }
    }

    /// <summary>悬停候选阶段提前预热，等真正触发时已在缓存里，避免窗口先按旧尺寸出现再调整。</summary>
    public static void Prefetch(string path)
    {
        // 快速划过一屏图片时别把队列堆满：排太多了后面的多半已经用不上
        if (Queue.Count > 8) return;
        if (TryCached(path, out _)) return;
        try { Enqueue(path, wait: false); }
        catch { }
    }

    private static bool TryCached(string path, out (int W, int H) size)
    {
        size = (0, 0);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            DateTime stamp = info.LastWriteTimeUtc;

            lock (Sync)
            {
                if (Cache.TryGetValue(path, out var e) && e.Stamp == stamp &&
                    Environment.TickCount64 - e.At < 600_000)
                {
                    size = (e.W, e.H);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static Job? Enqueue(string path, bool wait)
    {
        _ = WorkerStarted.Value;

        DateTime stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            stamp = info.LastWriteTimeUtc;
        }
        catch { return null; }

        Job? job = null;
        lock (Sync)
        {
            if (Cache.TryGetValue(path, out var e) && e.Stamp == stamp) return null;
            if (!wait)
            {
                if (InFlight.Contains(path)) return null;   // 已有预热在排队/执行
                InFlight.Add(path);
            }
            job = new Job { Path = path, Stamp = stamp };
        }

        if (!Queue.TryAdd(job))
        {
            lock (Sync) InFlight.Remove(path);
            return null;
        }
        return job;
    }

    private static bool StartWorker()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "media-probe",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    private static void Loop()
    {
        object? shell = null;
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            try
            {
                var (w, h) = OnStaThread(job.Path, ref shell);
                lock (Sync)
                {
                    Cache[job.Path] = new Entry(w, h, job.Stamp, Environment.TickCount64);
                    PruneLocked();
                }
                job.W = w;
                job.H = h;
            }
            catch (Exception ex)
            {
                Logger.Info("media probe (sta) failed: " + ex.Message);
            }
            finally
            {
                lock (Sync) InFlight.Remove(job.Path);
                try { job.Done.Set(); } catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>缓存清理：条目过多时按最久未用淘汰（不依赖时间戳，避免大量新文件时不收缩）。</summary>
    private static void PruneLocked()
    {
        if (Cache.Count <= 400) return;
        long now = Environment.TickCount64;
        var stale = Cache.Where(kv => now - kv.Value.At > 1_800_000)
                         .Select(kv => kv.Key).Take(100).ToList();
        if (stale.Count == 0)
            stale = Cache.OrderBy(kv => kv.Value.At).Take(100).Select(kv => kv.Key).ToList();
        foreach (var k in stale) Cache.Remove(k);
    }

    private static (int W, int H) OnStaThread(string path, ref object? shell)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        // 视频：Shell 属性（对 mp4/mkv/mov 等有效；取不到再退回 WIC，某些容器里其实是封面帧）
        if (VideoExt.Contains(ext))
        {
            var v = ShellSize(path, "System.Video.FrameWidth", "System.Video.FrameHeight", ref shell);
            if (v.W > 0 && v.H > 0) return v;
        }

        var img = ImageSize(path);
        if (img.W > 0 && img.H > 0) return img;

        return ShellSize(path, "System.Image.HorizontalSize", "System.Image.VerticalSize", ref shell);
    }

    /// <summary>
    /// 先用 GDI+ 只读头（PNG/JPEG/GIF/BMP/TIFF），命中就不加载 WPF/WIC 那套程序集；
    /// GDI+ 不认识的格式再退回 WIC，保证 HEIC/WebP 这类依赖系统编解码器的格式仍能取到尺寸。
    /// </summary>
    private static (int W, int H) ImageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var img = System.Drawing.Image.FromStream(fs,
                useEmbeddedColorManagement: false, validateImageData: false);
            if (img.Width > 0 && img.Height > 0) return (img.Width, img.Height);
        }
        catch { }

        return WicSize(path);
    }

    private static (int W, int H) WicSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            if (w <= 0 || h <= 0) return (0, 0);
            return (w, h);
        }
        catch { return (0, 0); }
    }

    /// <summary>Shell 属性读取；Shell.Application 实例按线程复用（放在 ref 参数里，避免每次探测新建 COM 对象）。</summary>
    private static (int W, int H) ShellSize(string path, string wKey, string hKey, ref object? shell)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return (0, 0);

            if (shell == null)
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return (0, 0);
                shell = Activator.CreateInstance(t);
                if (shell == null) return (0, 0);
            }

            dynamic s = shell;
            dynamic folder = s.Namespace(dir);
            if (folder == null) return (0, 0);
            dynamic item = folder.ParseName(Path.GetFileName(path));
            if (item == null) return (0, 0);

            int w = ToInt(item.ExtendedProperty(wKey));
            int h = ToInt(item.ExtendedProperty(hKey));
            return w > 0 && h > 0 ? (w, h) : (0, 0);
        }
        catch
        {
            shell = null;   // COM 出错就丢掉，下次重建
            return (0, 0);
        }
    }

    private static int ToInt(object? v)
    {
        try
        {
            return v switch
            {
                null => 0,
                int i => i,
                uint u => (int)u,
                long l => (int)l,
                string str => int.TryParse(new string(str.Where(char.IsDigit).ToArray()), out int n) ? n : 0,
                _ => Convert.ToInt32(v)
            };
        }
        catch { return 0; }
    }
}
