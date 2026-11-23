using System.Drawing;
using System.Drawing.Drawing2D;

namespace Zephyr.Ui;

/// <summary>
/// 设计 token。取值来自 UI/Zephyr-UI-设计稿-仪表风.html 的 :root 变量,
/// 保持一一对应, 改设计稿时这里跟着改。
///
/// 字号一律按"设计稿 px"书写, 内部换算成 point (px * 0.75)。
/// 用 point 而不是 pixel 是有意的: PerMonitorV2 下 Font 会跟着 DPI 自动放大,
/// 布局坐标由 Dpi.Px() 手工缩放, 两者配合才是清晰的。
/// </summary>
internal static class Theme
{
    // 冷峻硬件仪表风: 只替换中性底板与 accent, 语义色 (Ok/Warn/Err) 保持原值不动。
    public static readonly Color AppBg = Rgb(0x0A, 0x0C, 0x10);
    public static readonly Color WinBg = Rgb(0x11, 0x14, 0x1A);
    public static readonly Color Panel = Rgb(0x18, 0x1C, 0x23);
    public static readonly Color Panel2 = Rgb(0x20, 0x25, 0x2E);
    public static readonly Color Line = Rgb(0x2E, 0x35, 0x40);
    public static readonly Color LineSoft = Rgb(0x22, 0x27, 0x2F);
    public static readonly Color Text1 = Rgb(0xE6, 0xEC, 0xF2);
    public static readonly Color Text2 = Rgb(0x93, 0xA1, 0xAF);
    public static readonly Color Text3 = Rgb(0x5D, 0x68, 0x75);
    public static readonly Color Accent = Rgb(0x4C, 0xC2, 0xE8);
    public static readonly Color Ok = Rgb(0x63, 0xAF, 0x7B);
    public static readonly Color Warn = Rgb(0xDD, 0xA6, 0x3F);
    public static readonly Color Err = Rgb(0xD6, 0x5F, 0x5B);

    /// <summary>
    /// 强冷暖橙。只用于温度刻度条与录制态强调, 不参与语义表达 ——
    /// 强冷=成功仍然是 Ok 绿, 别拿它当"强冷色"。
    /// </summary>
    public static readonly Color Hot = Rgb(0xF0, 0x8A, 0x4B);

    /// <summary>抬升层的顶部高光, 让卡片不是一块死平面。alpha 很低的纯白。</summary>
    public static readonly Color Sheen = Color.FromArgb(10, 255, 255, 255);

    /// <summary>窗口底部的 22px 网格暗纹。alpha 5% 的 accent。</summary>
    public static readonly Color Grid = Color.FromArgb(13, 0x4C, 0xC2, 0xE8);

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(r, g, b);

    private const string SansFamily = "Microsoft YaHei UI";
    private const string MonoFamily = "Consolas";

    // 标题栏
    public static readonly Font BrandName = Sans(13.5f, FontStyle.Bold);
    public static readonly Font BrandTag = Sans(11f);

    // 机型信息
    public static readonly Font Machine = Sans(12f);

    // 数据卡
    public static readonly Font CardLabel = Sans(11.5f);
    public static readonly Font CardValue = Mono(27f, FontStyle.Bold);
    public static readonly Font CardValueTemp = Mono(23f, FontStyle.Bold);
    public static readonly Font CardUnit = Sans(11f);

    // 控件行
    public static readonly Font RowLabel = Sans(13f);
    public static readonly Font RowHint = Sans(12f);
    public static readonly Font Badge = Sans(12f);

    // 状态栏 / 脚注
    public static readonly Font Status = Sans(12.5f);
    public static readonly Font Footnote = Sans(11.5f);

    // 弹窗
    public static readonly Font DlgTitle = Sans(14f, FontStyle.Bold);
    public static readonly Font DlgBody = Sans(12.5f);
    public static readonly Font DlgButton = Sans(13f);

    private static Font Sans(float px, FontStyle style = FontStyle.Regular)
        => new(SansFamily, px * 0.75f, style, GraphicsUnit.Point);

    private static Font Mono(float px, FontStyle style = FontStyle.Regular)
        => new(MonoFamily, px * 0.75f, style, GraphicsUnit.Point);
}

/// <summary>
/// 设计稿用的是 96dpi 的 CSS px, WinForms 是设备像素。
/// 所有布局坐标都要过这里换算, 否则 150% 缩放下窗口会比设计稿小一半。
/// </summary>
internal static class Dpi
{
    public static int Px(Control c, float designPx) => (int)Math.Round(designPx * c.DeviceDpi / 96.0);
    public static float PxF(Control c, float designPx) => (float)(designPx * c.DeviceDpi / 96.0);
}

/// <summary>圆角矩形路径。设计稿里卡片 8px、窗口 12px、徽章胶囊。</summary>
internal static class Round
{
    public static GraphicsPath Path(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(0f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));

        if (d <= 0.5f)
        {
            p.AddRectangle(r);
            p.CloseFigure();
            return p;
        }

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>把矩形向内收缩 1px, 让 1px 描边落在像素格内, 避免半透明发虚。</summary>
    public static RectangleF Inset(RectangleF r, float by) =>
        new(r.X + by, r.Y + by, Math.Max(0f, r.Width - by * 2), Math.Max(0f, r.Height - by * 2));
}

/// <summary>
/// 竖向渐变填充。GDI+ 没有 CSS 的 linear-gradient, 但 LinearGradientBrush 够了:
/// 卡片"上亮下暗"、标题栏"顶部微光"都是靠它, 比纯色多一层立体感而不用贴图。
/// </summary>
internal static class Fill
{
    public static void Vertical(Graphics g, GraphicsPath path, RectangleF bounds, Color top, Color bottom)
    {
        if (bounds.Width <= 0f || bounds.Height <= 0f) return;

        // 用整段高度做渐变轴, 否则窄条上的渐变会挤成一条线。
        using var brush = new LinearGradientBrush(
            new RectangleF(bounds.X, bounds.Y, bounds.Width, Math.Max(bounds.Height, 1f)),
            top, bottom, LinearGradientMode.Vertical);
        g.FillPath(brush, path);
    }
}
