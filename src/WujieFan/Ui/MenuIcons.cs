using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WujieFan.Ui;

/// <summary>
/// 托盘右键菜单的图标。全部用 GDI+ 现画, 不引外部资源 ——
/// 四个小图标专门去放 png 反而更容易在发布包里漏掉。
/// 
/// 线条宽度按 size 缩放 (设计稿是 15px 里画 1.7px 线), 这样 150% 缩放下不会太细。
/// </summary>
internal static class TrayIcons
{
    /// <summary>显示主窗口: 一个窗口轮廓 + 标题栏。</summary>
    public static Image Show(int size) => Draw(size, (g, pen, w, h) =>
    {
        float m = w * 0.16f;
        var box = new RectangleF(m, m + h * 0.06f, w - m * 2, h - m * 2 - h * 0.06f);

        using var path = Round.Path(box, w * 0.13f);
        g.DrawPath(pen, path);
        g.DrawLine(pen, box.Left + w * 0.04f, box.Top + h * 0.26f, box.Right - w * 0.04f, box.Top + h * 0.26f);
    });

    /// <summary>恢复自动风扇: 一圈回转箭头。</summary>
    public static Image Restore(int size) => Draw(size, (g, pen, w, h) =>
    {
        float m = w * 0.18f;
        var box = new RectangleF(m, m, w - m * 2, h - m * 2);

        // 300 度弧, 留一个缺口给箭头
        g.DrawArc(pen, box, 40f, 280f);

        // 箭头画在弧的起点附近, 指向切线方向
        float ax = box.X + box.Width * 0.5f + (float)Math.Cos(40 * Math.PI / 180) * box.Width * 0.5f;
        float ay = box.Y + box.Height * 0.5f + (float)Math.Sin(40 * Math.PI / 180) * box.Height * 0.5f;
        float s = w * 0.15f;
        g.DrawLine(pen, ax, ay, ax - s, ay - s * 0.35f);
        g.DrawLine(pen, ax, ay, ax - s * 0.1f, ay + s);
    });

    /// <summary>重新检测机型: 放大镜。</summary>
    public static Image Rescan(int size) => Draw(size, (g, pen, w, h) =>
    {
        float d = w * 0.56f;
        var circle = new RectangleF(w * 0.15f, h * 0.15f, d, d);
        g.DrawEllipse(pen, circle);
        g.DrawLine(pen, circle.Right - d * 0.12f, circle.Bottom - d * 0.12f, w * 0.86f, h * 0.86f);
    });

    /// <summary>退出: 电源符号。</summary>
    public static Image Exit(int size) => Draw(size, (g, pen, w, h) =>
    {
        float m = w * 0.17f;
        var box = new RectangleF(m, m + h * 0.04f, w - m * 2, h - m * 2);

        // 缺口朝上的一段弧 + 上方一竖
        g.DrawArc(pen, box, 120f, 300f);
        g.DrawLine(pen, w * 0.5f, m * 0.4f, w * 0.5f, h * 0.46f);
    });

    private static Image Draw(int size, Action<Graphics, Pen, float, float> body)
    {
        int px = Math.Max(12, size);
        var bmp = new Bitmap(px, px);

        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 1.7/15 是设计稿的线宽比例
            float width = Math.Max(1f, px * 1.7f / 15f);

            using var pen = new Pen(Theme.Text1, width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            body(g, pen, px, px);
        }

        return bmp;
    }
}

/// <summary>
/// 深色托盘菜单的配色。只换颜色表还不够 —— 分隔线、文字色、选中态
/// 都要在渲染器里覆盖, 否则会有一两处仍然是系统浅色。
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer() : base(new Palette()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // 尊重每项自己的 ForeColor (退出项是红色), 只在没设过色时回退到 Text1。
        Color desired = e.Item is null ? Theme.Text1 : e.Item.ForeColor;
        e.TextColor = e.Item is not null && !e.Item.Enabled
            ? Theme.Text3
            : (desired.IsEmpty ? Theme.Text1 : desired);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Item is null) return;

        // y 必须带上该分隔项自身的 Top 偏移, 否则会画到菜单顶部压住边框。
        Rectangle bounds = e.Item.Bounds;
        int y = bounds.Top + bounds.Height / 2;
        Rectangle content = e.Item.ContentRectangle;
        using var pen = new Pen(Theme.LineSoft);
        e.Graphics.DrawLine(pen, content.Left + 8, y, content.Right - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text2;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // 设计稿里菜单是 1px 圆角描边 + 阴影。ToolStrip 只能画直角边框,
        // 但至少把它换成我们的线色, 不然默认是亮灰的。
        using var pen = new Pen(Theme.Line);
        Rectangle r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    private sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Panel2;

        public override Color MenuItemSelected => Theme.Panel2;
        public override Color MenuItemSelectedGradientBegin => Theme.Panel2;
        public override Color MenuItemSelectedGradientEnd => Theme.Panel2;

        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;

        public override Color SeparatorDark => Theme.LineSoft;
        public override Color SeparatorLight => Theme.LineSoft;
    }
}
