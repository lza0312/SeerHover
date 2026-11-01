using System.Drawing;
using System.Drawing.Drawing2D;

namespace SeerHover;

/// <summary>设置窗口的配色与字体，支持浅色 / 深色，可在运行时切换。</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static Color Bg { get; private set; }
    public static Color Surface { get; private set; }
    public static Color SurfaceAlt { get; private set; }
    public static Color Border { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentSoft { get; private set; }
    public static Color Track { get; private set; }
    public static Color Hover { get; private set; }

    /// <summary>界面缩放系数（来自设置里的「界面缩放」，1.0 = 100%）。</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>字体族名（托盘菜单也用它，保证和设置窗口一致）。</summary>
    public static string Family { get; private set; } = "Segoe UI";

    public static Font Body { get; private set; } = null!;
    public static Font BodyStrong { get; private set; } = null!;
    public static Font Small { get; private set; } = null!;
    public static Font H1 { get; private set; } = null!;
    public static Font H2 { get; private set; } = null!;
    public static Font Mono { get; private set; } = null!;

    private static string _monoFamily = "Consolas";
    private const float BodyPt = 9.25f;
    private const float SmallPt = 8.5f;
    private const float H1Pt = 12.5f;
    private const float H2Pt = 10f;
    private const float MonoPt = 9f;

    static Theme()
    {
        Family = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
        _monoFamily = PickFamily("Consolas", "Courier New");
        BuildFonts();
        Apply(DetectSystemDark());
    }

    /// <summary>
    /// 调整界面缩放：重建全部字体。调用后需要重建使用旧字体的窗口（设置窗口为此整体重排）。
    /// 旧字体不 Dispose —— 可能还有控件持有它们，交给 GC 回收。
    /// </summary>
    public static void SetScale(float scale)
    {
        scale = Math.Clamp(scale, 0.7f, 2f);
        if (Math.Abs(scale - Scale) < 0.005f) return;
        Scale = scale;
        BuildFonts();
    }

    private static void BuildFonts()
    {
        Body = new Font(Family, BodyPt * Scale);
        BodyStrong = new Font(Family, BodyPt * Scale, FontStyle.Bold);
        Small = new Font(Family, SmallPt * Scale);
        H1 = new Font(Family, H1Pt * Scale, FontStyle.Bold);
        H2 = new Font(Family, H2Pt * Scale, FontStyle.Bold);
        Mono = new Font(_monoFamily, MonoPt * Scale);
    }

    /// <summary>托盘菜单用的基准字体（系统菜单大小 9pt × 缩放）。</summary>
    public static Font NewMenuFont(FontStyle style = FontStyle.Regular) => new(Family, 9f * Scale, style);


    public static void Apply(bool dark)
    {
        IsDark = dark;
        if (dark)
        {
            Bg = Color.FromArgb(0x1E, 0x1F, 0x22);
            Surface = Color.FromArgb(0x27, 0x29, 0x2E);
            SurfaceAlt = Color.FromArgb(0x2F, 0x32, 0x38);
            Border = Color.FromArgb(0x3C, 0x40, 0x48);
            Text = Color.FromArgb(0xE9, 0xEB, 0xEF);
            Muted = Color.FromArgb(0x9B, 0xA2, 0xAD);
            Accent = Color.FromArgb(0x5A, 0x96, 0xFF);
            AccentSoft = Color.FromArgb(0x23, 0x31, 0x4D);
            Track = Color.FromArgb(0x40, 0x45, 0x4E);
            Hover = Color.FromArgb(0x32, 0x36, 0x3C);
        }
        else
        {
            Bg = Color.FromArgb(0xF4, 0xF6, 0xFA);
            Surface = Color.White;
            SurfaceAlt = Color.FromArgb(0xF6, 0xF8, 0xFB);
            Border = Color.FromArgb(0xE2, 0xE7, 0xEF);
            Text = Color.FromArgb(0x18, 0x1C, 0x24);
            Muted = Color.FromArgb(0x6B, 0x74, 0x83);
            Accent = Color.FromArgb(0x2F, 0x6F, 0xEB);
            AccentSoft = Color.FromArgb(0xE6, 0xEF, 0xFE);
            Track = Color.FromArgb(0xDC, 0xE2, 0xEC);
            Hover = Color.FromArgb(0xEC, 0xF0, 0xF7);
        }
    }

    /// <summary>读注册表 AppsUseLightTheme；读不到按浅色。</summary>
    public static bool DetectSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) return light == 0;
            if (key?.GetValue("AppsUseLightTheme") is long l) return l == 0;
        }
        catch { }
        return false;
    }

    private static string PickFamily(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                using var f = new FontFamily(n);
                return n;
            }
            catch { }
        }
        return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }
}

internal static class Draw
{
    /// <summary>DPI 缩放 × 界面缩放（所有自绘尺寸都走这里，和布局用的 S() 保持一致）。</summary>
    public static float Scale(Control c) => c.DeviceDpi / 96f * Theme.Scale;

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float max = Math.Min(r.Width, r.Height) / 2f;
        if (radius > max) radius = max;
        if (radius <= 0.5f)
        {
            p.AddRectangle(r);
            return p;
        }
        float d = radius * 2f;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = Round(r, radius);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
    {
        using var path = Round(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}
