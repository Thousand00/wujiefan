using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Zephyr.Ui;

/// <summary>
/// 自绘标题栏: 品牌标记、两个窗口按钮、悬停高亮、拖动窗口。
///
/// 从 MainForm 里搬出来的原因是它自成一摊 —— 有自己的三个矩形、两个悬停标志、
/// 自己的绘制和命中测试, 与风扇/EC/RTSS 毫无关系。这里不是"绘图接口":
/// 就一个具体类, 谁需要窗口 chrome 就 new 它, 没有可替换实现。
/// </summary>
internal sealed class TitleBarChrome
{
    private readonly Form _form;
    private readonly int _designHeight;

    private Rectangle _minRect;
    private Rectangle _closeRect;
    private bool _hoverMin;
    private bool _hoverClose;

    public TitleBarChrome(Form form, int designHeight)
    {
        _form = form;
        _designHeight = designHeight;
    }

    /// <summary>标题栏高度(设备像素)。鼠标命中、重绘区域都用它。</summary>
    public int Height => Dpi.Px(_form, _designHeight);

    /// <summary>按窗口宽度重算两个按钮的位置。窗口尺寸变了就要重调。</summary>
    public void Layout(int clientWidth)
    {
        int btn = Dpi.Px(_form, 30);
        int btnTop = (Height - btn) / 2;

        _closeRect = new Rectangle(clientWidth - Dpi.Px(_form, 13) - btn, btnTop, btn, btn);
        _minRect = new Rectangle(_closeRect.Left - Dpi.Px(_form, 6) - btn, btnTop, btn, btn);
    }

    /// <summary>画标题栏内容。背景与分隔线由窗口自己画。</summary>
    public void Paint(Graphics g)
    {
        int padX = Dpi.Px(_form, 16);

        // 品牌框: 28x28 圆角 7, 上亮下暗 + 一圈内描边, 里面画风扇
        int mark = Dpi.Px(_form, 28);
        int markY = (Height - mark) / 2;
        var markRect = new RectangleF(padX, markY, mark, mark);

        using (var markPath = Round.Path(markRect, Dpi.PxF(_form, 7)))
        using (var markPen = new Pen(Theme.Line))
        {
            Fill.Vertical(g, markPath, markRect, BlendPanel2(0.10), Theme.Panel2);
            g.DrawPath(markPen, markPath);
        }

        DrawFanGlyph(g, new RectangleF(
            markRect.X + mark * 0.16f, markRect.Y + mark * 0.16f, mark * 0.68f, mark * 0.68f));

        // 品牌名 + 副标题
        int textX = (int)markRect.Right + Dpi.Px(_form, 10);
        TextRenderer.DrawText(g, "Zephyr", Theme.BrandName,
            new Rectangle(textX, Dpi.Px(_form, 11), Dpi.Px(_form, 160), Dpi.Px(_form, 18)),
            Theme.Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, "无界14+ 风扇转速", Theme.BrandTag,
            new Rectangle(textX, Dpi.Px(_form, 27), Dpi.Px(_form, 160), Dpi.Px(_form, 16)),
            Theme.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        DrawButton(g, _minRect, _hoverMin, false);
        DrawButton(g, _closeRect, _hoverClose, true);
    }

    /// <summary>
    /// 三叶风扇符号: 三片圆头叶片 + 外圈轨道弧 (两端各一个圆点) + 中心白点。
    /// 形态对齐 assets/zephyr.ico, 用 Bezier/Arc 画, 不依赖任何图标字体。
    ///
    /// 叶片用"从中心放射的圆头楔形"而不是细长三角: 小尺寸下三角会退化成三条线,
    /// 看着像故障图标。圆头在 18px 宽时仍能读出"这是风扇"。
    /// </summary>
    private static void DrawFanGlyph(Graphics g, RectangleF box)
    {
        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;
        float r = box.Width / 2f;

        // 外圈轨道: 只画约 300 度, 留一个缺口, 两端各一个圆点
        float orbit = r * 1.02f;
        var orbitRect = new RectangleF(cx - orbit, cy - orbit, orbit * 2f, orbit * 2f);
        using (var orbitPen = new Pen(Color.FromArgb(150, Theme.Accent), Math.Max(1f, r * 0.12f)))
        {
            g.DrawArc(orbitPen, orbitRect, -58f, 296f);
        }

        using (var nodeBrush = new SolidBrush(Color.FromArgb(170, Theme.Accent)))
        {
            float node = Math.Max(1.1f, r * 0.15f);
            // 缺口两端 (与上面的 -58/296 对应)
            foreach (float deg in new[] { -58f, 238f })
            {
                double a = deg * Math.PI / 180.0;
                float nx = cx + (float)Math.Cos(a) * orbit;
                float ny = cy + (float)Math.Sin(a) * orbit;
                g.FillEllipse(nodeBrush, nx - node, ny - node, node * 2f, node * 2f);
            }
        }

        // 三片叶片: 每片是一个从中心向外的圆头楔形
        float bladeR = r * 0.80f;
        using var brush = new SolidBrush(Theme.Accent);
        for (int i = 0; i < 3; i++)
        {
            float start = -90f + i * 120f;
            using var path = new GraphicsPath();
            // 从中心附近出发, 沿一条贝塞尔鼓到外缘, 再沿另一条贝塞尔收回中心
            float a0 = (start - 30f) * (float)Math.PI / 180f;
            float a1 = (start + 30f) * (float)Math.PI / 180f;
            var p0 = new PointF(cx + (float)Math.Cos(a0) * bladeR * 0.30f,
                                cy + (float)Math.Sin(a0) * bladeR * 0.30f);
            var p3 = new PointF(cx + (float)Math.Cos(a1) * bladeR * 0.30f,
                                cy + (float)Math.Sin(a1) * bladeR * 0.30f);

            var c1 = new PointF(cx + (float)Math.Cos(a0 - 18f * Math.PI / 180f) * bladeR,
                                cy + (float)Math.Sin(a0 - 18f * Math.PI / 180f) * bladeR);
            var c2 = new PointF(cx + (float)Math.Cos(a1 + 18f * Math.PI / 180f) * bladeR,
                                cy + (float)Math.Sin(a1 + 18f * Math.PI / 180f) * bladeR);

            path.AddBezier(p0, c1, c2, p3);
            path.AddLine(p3, p0);
            path.CloseFigure();
            g.FillPath(brush, path);
        }

        // 中心: 深色环 + 白点, 与图标一致 (白点让叶片在深底上不糊成一团)
        float hubOuter = Math.Max(1.6f, r * 0.30f);
        using (var hubBg = new SolidBrush(Theme.WinBg))
            g.FillEllipse(hubBg, cx - hubOuter, cy - hubOuter, hubOuter * 2f, hubOuter * 2f);

        float hub = Math.Max(1.1f, r * 0.22f);
        using var hubBrush = new SolidBrush(Theme.Text1);
        g.FillEllipse(hubBrush, cx - hub, cy - hub, hub * 2f, hub * 2f);
    }

    /// <summary>把 Panel2 往白里提一点, 作为品牌块的顶部色。</summary>
    private static Color BlendPanel2(double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        Color b = Theme.Panel2;
        return Color.FromArgb(
            (int)Math.Round(b.R + (255 - b.R) * amount),
            (int)Math.Round(b.G + (255 - b.G) * amount),
            (int)Math.Round(b.B + (255 - b.B) * amount));
    }

    private void DrawButton(Graphics g, Rectangle rect, bool hover, bool isClose)
    {
        if (rect.IsEmpty) return;

        if (hover)
        {
            using var hoverPath = Round.Path(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), Dpi.PxF(_form, 7));
            using var hoverBrush = new SolidBrush(isClose ? Theme.Err : Theme.Panel2);
            g.FillPath(hoverBrush, hoverPath);
        }

        Color ink = hover && isClose ? Color.White : (hover ? Theme.Text1 : Theme.Text2);
        using var pen = new Pen(ink, Dpi.PxF(_form, 1.4f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        float cxp = rect.X + rect.Width / 2f;
        float cyp = rect.Y + rect.Height / 2f;
        float half = Dpi.PxF(_form, 4.5f);

        if (isClose)
        {
            g.DrawLine(pen, cxp - half, cyp - half, cxp + half, cyp + half);
            g.DrawLine(pen, cxp + half, cyp - half, cxp - half, cyp + half);
        }
        else
        {
            g.DrawLine(pen, cxp - half, cyp, cxp + half, cyp);
        }
    }

    /// <summary>
    /// 处理鼠标移动: 更新悬停态与光标, 需要重画时返回 true。
    /// 只重画标题栏那一条, 不整窗重绘 —— 窗口内容里有三张卡片, 整窗重绘会闪。
    /// </summary>
    public bool OnMouseMove(Point p)
    {
        bool hm = _minRect.Contains(p);
        bool hc = _closeRect.Contains(p);

        _form.Cursor = (hm || hc) ? Cursors.Hand : Cursors.Default;

        if (hm == _hoverMin && hc == _hoverClose) return false;

        _hoverMin = hm;
        _hoverClose = hc;
        return true;
    }

    /// <summary>鼠标离开窗口: 清悬停态并还原光标, 需要重画时返回 true。</summary>
    public bool OnMouseLeave()
    {
        _form.Cursor = Cursors.Default;

        if (!_hoverMin && !_hoverClose) return false;

        _hoverMin = false;
        _hoverClose = false;
        return true;
    }

    public bool HitsMinimize(Point p) => _minRect.Contains(p);

    public bool HitsClose(Point p) => _closeRect.Contains(p);

    /// <summary>标题栏其余部分 = 拖动窗口。走系统非客户区命中, 手感与原生窗口一致。</summary>
    public void StartDrag(IntPtr handle)
    {
        ReleaseCapture();
        _ = SendMessage(handle, 0x00A1, 0x0002, 0); // WM_NCLBUTTONDOWN / HTCAPTION
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Win11 的原生圆角 + 阴影。拿不到就靠窗口自己裁的 Region 兜底。</summary>
    public static void ApplyRoundedCorners(IntPtr handle)
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, 33, ref pref, sizeof(int));
        }
        catch { }
    }
}
