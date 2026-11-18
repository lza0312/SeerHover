using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace SeerHover;

/// <summary>
/// Seer 的 V2 rect 只会把「比窗口大的图」缩小填满，永远不会把小图放大。
/// 想让小图按原比例放大显示，只能先生成一份高质量放大的临时副本再交给 Seer——
/// 对 Seer 来说它是一张大图，于是会缩小填满我们给的 rect，视觉上就是原比例放大且没有黑边。
///
/// 副本按 源路径 + 修改时间 + 目标尺寸 缓存；只在确实需要放大时才生成。
/// </summary>
internal static class PreviewUpscaler
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    // 视频没有静态帧可重采样；GIF 重采样后会丢动画。这些交给 Seer 自己处理。
    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".rmvb", ".3gp"
    };

    private static string Dir => Path.Combine(Path.GetTempPath(), "SeerHover", "Upscaled");

    /// <summary>需要放大时返回临时副本路径；不需要或失败返回 null。</summary>
    public static string? Get(string path, int width, int height)
    {
        if (width <= 0 || height <= 0 || !File.Exists(path)) return null;
        if (SkipExt.Contains(Path.GetExtension(path))) return null;
        try
        {
            var (srcW, srcH) = MediaSize.Probe(path);
            if (srcW <= 0 || srcH <= 0) return null;
            if (width <= srcW && height <= srcH) return null;   // 原图比窗口大，Seer 自己会缩小

            string stamp;
            try { stamp = File.GetLastWriteTimeUtc(path).Ticks.ToString(); }
            catch { return null; }
            string key = $"{path}|{stamp}|{width}x{height}";

            lock (Sync)
            {
                if (Cache.TryGetValue(key, out var hit) && File.Exists(hit)) return hit;
            }

            Directory.CreateDirectory(Dir);
            string outPath = Path.Combine(Dir, Hash(key) + ".jpg");
            if (!File.Exists(outPath))
            {
                string tmp = outPath + ".tmp";
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var source = Image.FromStream(fs, false, false))
                {
                    ApplyOrientation(source);
                    using var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(target))
                    {
                        // PNG 透明区域转 JPEG 会变黑，先铺白底
                        using var bg = new SolidBrush(Color.White);
                        g.FillRectangle(bg, 0, 0, width, height);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(source, new Rectangle(0, 0, width, height),
                            new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
                    }
                    var jpeg = ImageCodecInfo.GetImageEncoders()
                        .FirstOrDefault(c => c.MimeType == "image/jpeg");
                    if (jpeg == null) return null;
                    using var pars = new EncoderParameters(1);
                    pars.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                    target.Save(tmp, jpeg, pars);
                }
                File.Move(tmp, outPath);
            }

            lock (Sync) Cache[key] = outPath;
            if (Logger.IsVerbose)
                Logger.Info($"upscaled {Path.GetFileName(path)} {srcW}x{srcH} -> {width}x{height}");
            return outPath;
        }
        catch (Exception ex)
        {
            Logger.Error("upscale preview: " + ex.Message);
            return null;
        }
    }

    /// <summary>GDI+ 不会自动应用 EXIF 方向；不处理会画出横躺/倒置的副本。</summary>
    private static void ApplyOrientation(Image img)
    {
        try
        {
            foreach (int id in img.PropertyIdList)
            {
                if (id != 0x0112) continue;
                var prop = img.GetPropertyItem(id);
                if (prop?.Value is not { Length: > 0 } bytes) return;
                var flip = bytes[0] switch
                {
                    2 => RotateFlipType.RotateNoneFlipX,
                    3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.RotateNoneFlipY,
                    5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone,
                    7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone,
                    _ => RotateFlipType.RotateNoneFlipNone
                };
                if (flip != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(flip);
                return;
            }
        }
        catch { }
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    /// <summary>启动时清理两天前的副本，避免长期运行堆积。</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            long cutoff = DateTime.UtcNow.AddDays(-2).Ticks;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.jpg*"))
            {
                try { if (File.GetCreationTimeUtc(f).Ticks < cutoff) File.Delete(f); }
                catch { }
            }
        }
        catch { }
    }
}
