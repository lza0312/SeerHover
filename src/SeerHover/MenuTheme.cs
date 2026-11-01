using System.Drawing;
using System.Windows.Forms;

namespace SeerHover;

/// <summary>
/// 托盘右键菜单的主题渲染器。
/// 系统默认的 ContextMenuStrip 永远是浅色（深色模式下会突兀），这里全部自绘，
/// 颜色实时取 Theme，所以托盘菜单会跟着「深色模式」开关一起变；
/// 字号跟着 Theme.Scale 走，和设置窗口的「界面缩放」一致。
/// </summary>
internal sealed class MenuTheme : ToolStripProfessionalRenderer
{
    private static readonly MenuTheme Instance = new();

    /// <summary>每个菜单上次套用的缩放，避免每次弹出都重建字体。</summary>
    private static readonly Dictionary<ContextMenuStrip, float> Applied = new();

    private MenuTheme() : base(new Table()) { RoundedEdges = false; }

    public static void Apply(ContextMenuStrip menu)
    {
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = Instance;
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        ApplyScale(menu);
        menu.Invalidate();
    }

    /// <summary>按 Theme.Scale 重设菜单字号与条目间距（ToolStrip 会据此自动放大整体尺寸）。</summary>
    private static void ApplyScale(ContextMenuStrip menu)
    {
        float scale = Theme.Scale;
        if (Applied.TryGetValue(menu, out float last) && Math.Abs(last - scale) < 0.005f) return;
        Applied[menu] = scale;

        var baseFont = Theme.NewMenuFont();
        menu.Font = baseFont;

        int padV = (int)Math.Round(2 * scale);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator sep)
            {
                sep.AutoSize = false;
                sep.Height = Math.Max(5, (int)Math.Round(7 * scale));
                continue;
            }

            // 保留原本的加粗（「设置…」），只换尺寸
            item.Font = new Font(baseFont, item.Font?.Bold == true ? FontStyle.Bold : FontStyle.Regular);
            item.Padding = new Padding(0, padV, 0, padV);
        }
        menu.PerformLayout();
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = Theme.Scale;
        var r = new RectangleF(4 * s, 1, e.Item.Width - 9 * s, e.Item.Height - 3);
        Draw.FillRound(g, r, 5 * s, Theme.Hover);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = Theme.Scale;
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, (int)(10 * s), y, e.Item.Width - (int)(12 * s), y);
    }

    /// <summary>ToolStripProfessionalRenderer 的配色表（几乎都被上面的重写覆盖，留作兜底）。</summary>
    private sealed class Table : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Hover;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
    }
}
