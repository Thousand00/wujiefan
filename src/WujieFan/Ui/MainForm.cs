using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;
using WujieFan.Core.Config;
using WujieFan.Core.Ec;
using WujieFan.Core.Hardware;
using WujieFan.Core.Providers;

namespace WujieFan.Ui;

/// <summary>
/// 主窗口: 显示两个风扇转速与 CPU 温度, 提供开机自启动、RTSS 供数与强冷开关。
///
/// 界面按 UI/WujieFan-UI-设计稿.html 实现 (2026-11-15): 深色圆角卡片、
/// 自绘标题栏、三列数据卡、整行可点的开关、语义色状态栏。
/// 所有布局坐标都是设计稿的 96dpi CSS px, 经 Dpi.Px 换算到实际设备像素。
/// </summary>
public sealed class MainForm : Form
{
    private const int DesignWidth = 432;
    private const int DesignTitleHeight = 50;
    private const int DesignCardHeight = 84;
    private const int DesignRowHeight = 44;

    private readonly bool _startMinimized;

    private readonly DataCard _fan1Card = new();
    private readonly DataCard _fan2Card = new();
    private readonly DataCard _tempCard = new();
    private readonly StatusBar _statusBar = new();
    private readonly ToggleRow _autoStartRow = new();
    private readonly ToggleRow _rtssRow = new();
    private readonly ToggleRow _strongCoolRow = new();
    private readonly Label _machineLabel = new();
    private readonly Label _footnoteLabel = new();

    private Rectangle _minRect;
    private Rectangle _closeRect;
    private bool _hoverMin;
    private bool _hoverClose;

    private readonly System.Windows.Forms.Timer _timer = new();

    private FanReader? _reader;
    private AidaSharedMemory? _aida;
    private AppSettings _settings = AppSettings.Load();
    private AutoStartManager? _autoStart;

    private int _consecutiveErrors;
    private bool _trayHintShown;

    /// <summary>正在回填界面初始状态, 此时不应把 CheckedChanged 当成用户操作。</summary>
    private bool _suppressAutoStartEvent;

    /// <summary>正在回填强冷勾选状态, 同理不能当成用户操作。</summary>
    private bool _suppressFanModeEvent;

    /// <summary>正在执行模式切换 (含读回校验), 期间忽略重入。</summary>
    private bool _fanModeBusy;

    /// <summary>连续多少次采样温度达到看门狗阈值。只在强冷模式下累计。</summary>
    private int _hotSamples;

    /// <summary>已经因过热自动交还过 EC, 本轮不再重复弹提示。</summary>
    private bool _emergencyRestored;

    /// <summary>上一次尝试重建 AIDA64 共享内存的刻度, 用来给重建加退避。</summary>
    private long _lastAidaTryTicks;

    /// <summary>
    /// 共享内存重建的最小间隔。失败时 OnTick 每秒都会想重建一次, 没有退避就会
    /// 每秒把同一条错误写进状态栏, 看起来像程序在抽搐。
    /// </summary>
    private static readonly long AidaRetryIntervalTicks = TimeSpan.FromSeconds(10).Ticks;

    /// <summary>
    /// 已经明确放弃自动重试 (机型不受支持且用户选择不再继续)。
    /// 没有这个标志时, 定时器每秒都会重开一次设备, 每次都会再弹一个"硬件不受支持"对话框,
    /// 在用户看来就是弹窗刷屏。
    /// </summary>
    private bool _giveUpOpening;

    private NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _hidingToTray;

    /// <summary>窗口已进入销毁流程。休眠恢复回调可能晚到, 用它挡住。</summary>
    private bool _disposedUi;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        BuildUi();

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
        Resize += OnFormResize;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    // ---------------------------------------------------------------- 界面构建

    private void BuildUi()
    {
        Text = "WujieFan - 无界14+ 风扇转速";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        BackColor = Theme.WinBg;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = Theme.RowLabel;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _machineLabel.AutoSize = false;
        _machineLabel.BackColor = Theme.WinBg;
        _machineLabel.ForeColor = Theme.Text2;
        _machineLabel.Font = Theme.Machine;
        _machineLabel.Text = "机器信息读取中...";

        _fan1Card.Label = "Fan1 (CPU)";
        _fan1Card.Unit = "RPM";
        _fan1Card.Value = "--";
        _fan1Card.DotColor = Theme.Text3;

        _fan2Card.Label = "Fan2 (GPU)";
        _fan2Card.Unit = "RPM";
        _fan2Card.Value = "--";
        _fan2Card.DotColor = Theme.Text3;

        _tempCard.Label = "CPU";
        _tempCard.Unit = "°C";
        _tempCard.Value = "--";
        _tempCard.BigValue = false;

        _autoStartRow.Text = "开机自动启动";
        _autoStartRow.Hint = "(需要管理员权限)";
        _autoStartRow.CheckedChanged += OnAutoStartChanged;

        _rtssRow.Text = "向 RTSS 提供数据";
        _rtssRow.Hint = "(AIDA64 通道)";
        _rtssRow.Checked = _settings.PublishToRtss;
        _rtssRow.CheckedChanged += (_, _) =>
        {
            _settings.PublishToRtss = _rtssRow.Checked;
            _settings.Save();
            ApplyPublishMode();
        };

        _strongCoolRow.Text = "强冷模式";
        _strongCoolRow.Hint = "(两个风扇满转)";
        _strongCoolRow.CheckedChanged += OnStrongCoolChanged;

        _footnoteLabel.AutoSize = false;
        _footnoteLabel.BackColor = Theme.WinBg;
        _footnoteLabel.ForeColor = Theme.Text3;
        _footnoteLabel.Font = Theme.Footnote;
        _footnoteLabel.Text = "点 × 不收进程, 只收进托盘继续读数与供数; 双击托盘图标唤回窗口。";

        _statusBar.SetStatus("正在初始化...", Theme.Text2, Color.Transparent);

        Controls.AddRange(new Control[]
        {
            _machineLabel, _fan1Card, _fan2Card, _tempCard,
            _autoStartRow, _rtssRow, _strongCoolRow, _statusBar, _footnoteLabel,
        });

        Relayout();
    }

    /// <summary>
    /// 按设计稿的纵向节奏排一遍。状态栏高度随文案变, 所以每次 SetStatus 之后都要重排。
    /// </summary>
    private void Relayout()
    {
        int w = Dpi.Px(this, DesignWidth);
        int padX = Dpi.Px(this, 18);
        int innerW = w - padX * 2;

        int titleH = Dpi.Px(this, DesignTitleHeight);
        int y = titleH + Dpi.Px(this, 10);

        int machineH = Dpi.Px(this, 20);
        _machineLabel.Bounds = new Rectangle(padX, y, innerW, machineH);
        y += machineH + Dpi.Px(this, 6);

        // 三列: 1fr 1fr .9fr, 间隙 10
        int gap = Dpi.Px(this, 10);
        int usable = innerW - gap * 2;
        int c1 = (int)Math.Round(usable / 2.9);
        int c2 = c1;
        int c3 = usable - c1 - c2;
        int cardH = Dpi.Px(this, DesignCardHeight);

        _fan1Card.Bounds = new Rectangle(padX, y, c1, cardH);
        _fan2Card.Bounds = new Rectangle(padX + c1 + gap, y, c2, cardH);
        _tempCard.Bounds = new Rectangle(padX + c1 + c2 + gap * 2, y, c3, cardH);
        y += cardH + Dpi.Px(this, 10);

        int rowH = Dpi.Px(this, DesignRowHeight);
        int rowW = innerW - Dpi.Px(this, 16);
        int rowX = padX + Dpi.Px(this, 8);

        _autoStartRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
        y += rowH;
        _rtssRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
        y += rowH;
        _strongCoolRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
        y += rowH + Dpi.Px(this, 8);

        int statusH = _statusBar.MeasureHeight(innerW);
        _statusBar.Bounds = new Rectangle(padX, y, innerW, statusH);
        y += statusH + Dpi.Px(this, 10);

        int footH = Dpi.Px(this, 32);
        _footnoteLabel.Bounds = new Rectangle(padX, y, innerW, footH);
        y += footH + Dpi.Px(this, 12);

        if (ClientSize.Height != y || ClientSize.Width != w)
            ClientSize = new Size(w, y);

        // 标题栏右侧两个按钮
        int btn = Dpi.Px(this, 30);
        int btnTop = (titleH - btn) / 2;
        _closeRect = new Rectangle(w - Dpi.Px(this, 13) - btn, btnTop, btn, btn);
        _minRect = new Rectangle(_closeRect.Left - Dpi.Px(this, 6) - btn, btnTop, btn, btn);

        UpdateRegion();
    }

    /// <summary>无边框窗口要自己把圆角裁出来, 否则四个角是直角深色块。</summary>
    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;

        using var path = Round.Path(new RectangleF(0, 0, Width, Height), Dpi.PxF(this, 12));
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Win11 的原生圆角 + 阴影, 拿不到就靠上面的 Region 兜底。
        try
        {
            int pref = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Theme.WinBg))
            g.FillRectangle(bg, ClientRectangle);

        DrawTitleBar(g);

        int titleH = Dpi.Px(this, DesignTitleHeight);
        using var sep = new Pen(Theme.LineSoft);
        g.DrawLine(sep, 0, titleH, Width, titleH);

        // 图标区域之外再画一圈细描边, 让无边框窗口有边界感
        using var border = new Pen(Theme.Line);
        using var outline = Round.Path(Round.Inset(new RectangleF(0, 0, Width, Height), 0.5f), Dpi.PxF(this, 12));
        g.DrawPath(border, outline);
    }

    private void DrawTitleBar(Graphics g)
    {
        int padX = Dpi.Px(this, 16);
        int titleH = Dpi.Px(this, DesignTitleHeight);

        // 品牌框: 28x28 圆角 7, 底色 panel-2, 里面画风扇
        int mark = Dpi.Px(this, 28);
        int markY = (titleH - mark) / 2;
        var markRect = new RectangleF(padX, markY, mark, mark);

        using (var markPath = Round.Path(markRect, Dpi.PxF(this, 7)))
        using (var markFill = new SolidBrush(Theme.Panel2))
        {
            g.FillPath(markFill, markPath);
        }

        DrawFanGlyph(g, new RectangleF(markRect.X + mark * 0.18f, markRect.Y + mark * 0.18f, mark * 0.64f, mark * 0.64f));

        // 品牌名 + 副标题
        int textX = (int)markRect.Right + Dpi.Px(this, 10);
        TextRenderer.DrawText(g, "WujieFan", Theme.BrandName,
            new Rectangle(textX, Dpi.Px(this, 11), Dpi.Px(this, 160), Dpi.Px(this, 18)),
            Theme.Text1, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, "无界14+ 风扇转速", Theme.BrandTag,
            new Rectangle(textX, Dpi.Px(this, 27), Dpi.Px(this, 160), Dpi.Px(this, 16)),
            Theme.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        DrawTitleButton(g, _minRect, _hoverMin, false);
        DrawTitleButton(g, _closeRect, _hoverClose, true);
    }

    /// <summary>三叶风扇符号。用 Bezier 画, 不依赖任何图标字体。</summary>
    private static void DrawFanGlyph(Graphics g, RectangleF box)
    {
        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;
        float r = box.Width / 2f;

        using var brush = new SolidBrush(Theme.Accent);

        for (int i = 0; i < 3; i++)
        {
            float start = -90f + i * 120f;
            double a0 = start * Math.PI / 180.0;
            double a1 = (start + 120f) * Math.PI / 180.0;

            var p0 = new PointF(cx, cy);
            var p1 = new PointF(
                (float)(cx + Math.Cos(a0) * r * 1.05),
                (float)(cy + Math.Sin(a0) * r * 1.05));
            var p2 = new PointF(
                (float)(cx + Math.Cos(a1) * r * 1.05),
                (float)(cy + Math.Sin(a1) * r * 1.05));

            using var path = new GraphicsPath();
            path.AddBezier(p0, p1, p2, p0);
            path.CloseFigure();
            g.FillPath(brush, path);
        }

        float hub = Math.Max(1.5f, r * 0.26f);
        using var hubBrush = new SolidBrush(Theme.Text1);
        g.FillEllipse(hubBrush, cx - hub, cy - hub, hub * 2, hub * 2);
    }

    private void DrawTitleButton(Graphics g, Rectangle rect, bool hover, bool isClose)
    {
        if (rect.IsEmpty) return;

        if (hover)
        {
            using var hoverPath = Round.Path(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), Dpi.PxF(this, 7));
            using var hoverBrush = new SolidBrush(isClose ? Theme.Err : Theme.Panel2);
            g.FillPath(hoverBrush, hoverPath);
        }

        Color ink = hover && isClose ? Color.White : (hover ? Theme.Text1 : Theme.Text2);
        using var pen = new Pen(ink, Dpi.PxF(this, 1.4f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        float cxp = rect.X + rect.Width / 2f;
        float cyp = rect.Y + rect.Height / 2f;
        float half = Dpi.PxF(this, 4.5f);

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

    // ---------------------------------------------------------------- 标题栏交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hm = _minRect.Contains(e.Location);
        bool hc = _closeRect.Contains(e.Location);

        if (hm != _hoverMin || hc != _hoverClose)
        {
            _hoverMin = hm;
            _hoverClose = hc;
            Invalidate(new Rectangle(0, 0, Width, Dpi.Px(this, DesignTitleHeight)));
        }

        Cursor = (hm || hc) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverMin || _hoverClose)
        {
            _hoverMin = false;
            _hoverClose = false;
            Invalidate(new Rectangle(0, 0, Width, Dpi.Px(this, DesignTitleHeight)));
        }
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_closeRect.Contains(e.Location))
        {
            Close();
            return;
        }

        if (_minRect.Contains(e.Location))
        {
            HideToTray();
            return;
        }

        // 标题栏其余部分 = 拖动窗口。走系统非客户区命中, 手感与原生窗口一致。
        if (e.Y < Dpi.Px(this, DesignTitleHeight))
        {
            ReleaseCapture();
            _ = SendMessage(Handle, 0x00A1, 0x0002, 0); // WM_NCLBUTTONDOWN / HTCAPTION
            return;
        }

        base.OnMouseDown(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---------------------------------------------------------------- 托盘

    private void CreateTrayIcon()
    {
        try
        {
            _trayIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(32),
                Text = "WujieFan - 无界14+ 风扇转速",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu(),
            };

            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch (Exception ex)
        {
            UiLog("CreateTrayIcon FAILED: " + ex);
            // 托盘建不出来时至少要把窗口留住, 否则关掉窗口就再也找不回来了。
            _settings.MinimizeToTray = false;
            _trayIcon = null;
            SetStatus("托盘图标创建失败, 已改为关窗口即退出: " + ex.Message, Theme.Warn);
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Panel,
            ForeColor = Theme.Text1,
            Font = Theme.RowLabel,
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = true,
        };

        menu.Items.Add(new ToolStripMenuItem("显示主窗口", TrayIcons.Show(16), (_, _) => RestoreFromTray()));

        // 逃生口: 不论程序内部状态如何, 点一下就把 EC 交还自动。
        // 面板上那个勾选框万一因为界面卡住点不动, 这里是最后一条路。
        menu.Items.Add(new ToolStripMenuItem("恢复自动风扇", TrayIcons.Restore(16), (_, _) => EmergencyRestoreAuto("托盘菜单")));
        menu.Items.Add(new ToolStripMenuItem("重新检测机型", TrayIcons.Rescan(16), (_, _) => RetryHardwareCheck()));
        var sep = new ToolStripSeparator { Margin = new Padding(8, 4, 8, 4) };
        menu.Items.Add(sep);

        var exitItem = new ToolStripMenuItem("退出 WujieFan", TrayIcons.Exit(16), (_, _) => ExitApplication());
        exitItem.ForeColor = Theme.Err;
        menu.Items.Add(exitItem);

        // 注意: 这一轮只给普通项上色, 不要把 exitItem 的红色覆盖回去。
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator || ReferenceEquals(item, exitItem)) continue;
            item.ForeColor = Theme.Text1;
        }

        return menu;
    }

    /// <summary>从嵌入资源取出多尺寸 .ico, 按需要的尺寸建图标 (托盘与任务栏尺寸不同)。</summary>
    private static Icon LoadAppIcon(int size)
    {
        var asm = typeof(MainForm).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("wujiefan.ico", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using Stream? s = asm.GetManifestResourceStream(name);
                if (s is null) continue;

                using var buffer = new MemoryStream();
                s.CopyTo(buffer);
                buffer.Position = 0;
                return new Icon(buffer, size, size);
            }
            catch
            {
                // 这一档尺寸在 .ico 里不存在时会抛异常; 不能因此让整个窗口起不来。
                continue;
            }
        }

        // 兜底: 没有嵌入资源就用 exe 自己的图标; 连这个都拿不到就用系统图标。
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    /// <summary>把隐藏的窗口叫回来 (托盘双击、托盘菜单、以及第二次启动的唤醒事件都走这里)。</summary>
    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (!Visible) Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        // 退出前主动交还风扇控制权。冷启动确实会复位 EC, 但"重启" (软复位) 不会 ——
        // 不在退出时收手, 用户点了退出还以为风扇已经回到自动, 实际还在满转。
        // 这一步失败也不阻止退出: 卡在满转只是吵, 而不让用户退出才是真正的问题。
        if (_reader is { IsOpen: true } && _reader.ReadModeState() == FanModeState.StrongCool)
            EmergencyRestoreAuto("程序退出");

        _exiting = true;
        Close();
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnFormLoad(object? sender, EventArgs e)
    {
        CreateTrayIcon();

        _autoStart = new AutoStartManager();

        // 回填勾选状态必须屏蔽 CheckedChanged: 赋值本身会触发事件,
        // 于是 OnAutoStartChanged 又跑一次 Enable() —— 白白多起一个 schtasks 进程,
        // 还会把状态栏改写成"已启用", 盖掉后面 OpenReader 写的真实状态。
        _suppressAutoStartEvent = true;
        try
        {
            if (_autoStart.HasForeignTask())
            {
                _autoStartRow.Enabled = false;
                _autoStartRow.Text = "开机自动启动";
                _autoStartRow.Hint = "(同名任务已存在, 已锁定)";
            }
            else
            {
                bool enabled = _autoStart.IsEnabled();
                _autoStartRow.SetCheckedSilently(enabled);

                // 已启用时还要检查任务定义是不是当前版本的要求。
                // 0.1.0 用 schtasks 模板建的旧任务带着"拔电源不启动"和"72 小时被强杀",
                // 用户不会为了升级再去勾一次自启动, 所以这里主动修。
                // 但只在确实不符合时才重写 —— 每次启动都跑 schtasks 既慢又在动系统。
                if (enabled && !_autoStart.IsUpToDate())
                {
                    AutoStartManager.Result fix = _autoStart.Enable();
                    if (!fix.Ok)
                        SetStatus("自启动任务更新失败: " + fix.Message.Split('\n')[0], Theme.Warn);
                }
            }
        }
        finally
        {
            _suppressAutoStartEvent = false;
        }

        _machineLabel.Text = DescribeMachine();

        _timer.Interval = Math.Clamp(_settings.PollIntervalMs, 200, 10000);
        _timer.Tick += OnTick;

        // OpenReader 内部已经按需建好共享内存, 这里不必再调一次。
        OpenReader();

        // 按设置做一次模式归一化。放在 OpenReader 之后、定时器启动之前:
        //   - 上次正常退出已经把模式交还自动了, 这里只是核对一遍;
        //   - 上次是被强杀/蓝屏留下的, 这里负责把状态拉回设置所说的样子。
        NormalizeFanModeOnStartup();

        _timer.Start();
        OnTick(null, EventArgs.Empty);

        // 开机自启动时不弹窗打扰, 直接进托盘。手动启动则正常显示窗口,
        // 用户点关闭键时再按 MinimizeToTray 决定是缩托盘还是退出。
        if (_startMinimized) HideToTray();
    }

    /// <summary>
    /// 启动时把 EC 拉到"设置里记着的模式"。
    ///
    /// 这条规则同时覆盖了几种历史残留:
    ///   设置=自动  -> 若 EC 还停在上次的 0xDC, 写回 0x00 (强杀、蓝屏、手动杀的残留)
    ///   设置=强冷  -> 重新写一遍满转 (软重启不清 EC RAM, 但我们要保证值是对的)
    /// 换句话说: 开机后是什么状态, 由设置决定, 而不是由"上次进程死得干不干净"决定。
    /// </summary>
    private void NormalizeFanModeOnStartup()
    {
        if (_reader is null || !_reader.IsOpen)
        {
            UpdateFanModeUi(FanModeState.Unknown);
            return;
        }

        FanModeState actual = _reader.ReadModeState();
        FanMode want = _settings.FanMode;

        _suppressFanModeEvent = true;
        try { _strongCoolRow.SetCheckedSilently(want == FanMode.StrongCool); }
        finally { _suppressFanModeEvent = false; }

        // 值和意愿已经一致, 不必再写 EC —— 少一次写入就少一次出错机会。
        bool matches = want == FanMode.StrongCool
            ? actual == FanModeState.StrongCool
            : actual == FanModeState.Auto;

        if (matches)
        {
            UpdateFanModeUi(actual);
            return;
        }

        // 归一化失败不该弹框打断开机, 但必须留下可见痕迹。
        SetFanMode(want, userInitiated: false, reason: "已按设置恢复自动模式 (清理上次残留)。");
    }

    private static string DescribeMachine()
    {
        HardwareIdentity id = HardwareIdentity.Read();
        if (!id.WasRead) return "机器信息不可读";
        return $"{id.SystemManufacturer} {id.SystemProductName} · BIOS {id.BiosVersion} · EC {id.EcVersionText}";
    }

    /// <param name="interactive">
    /// 是否允许弹对话框。只有用户主动触发 (启动、托盘菜单重试) 时才为 true,
    /// 定时器里的自动重连必须为 false —— 否则会每秒弹一个对话框。
    /// </param>
    private bool OpenReader(bool interactive = true)
    {
        CloseReader();

        try
        {
            _reader = new FanReader(LoadLpcIoBlob());
            _reader.Open(force: _settings.ForceUnsupported);
            _giveUpOpening = false;
            _consecutiveErrors = 0;

            SetStatus(
                _settings.ForceUnsupported
                    ? $"已连接 · PawnIO {_reader.PawnIoVersion} · 已跳过机型校验"
                    : $"已连接 · PawnIO {_reader.PawnIoVersion}",
                _settings.ForceUnsupported ? Theme.Warn : Theme.Ok);

            // 重连成功后要补建共享内存: 如果启动时设备没打开, 之前那次 ApplyPublishMode
            // 根本不会执行, _aida 一直是 null, 之后即使读到数据也永远不会往 RTSS 送。
            // 放在 SetStatus 之后, 这样"共享内存打不开"的提示不会被"已连接"覆盖掉。
            // ApplyPublishMode 自身幂等, 重复调用没有副作用。
            ApplyPublishMode();
            return true;
        }
        catch (HardwareNotSupportedException ex)
        {
            _reader?.Dispose();
            _reader = null;

            // 只有用户主动拒绝(interactive 时选"否")才算放弃。
            // 定时器里的自动重连失败不能设这个标志: 读 SMBIOS 有可能一次偶然失败,
            // 那会让程序从此再也不采样, 而界面上只留一句"机型不受支持"。
            _giveUpOpening = interactive;

            if (interactive) ShowBlockingMessage(ex.Reason, ex.Guidance);
            SetStatus(_settings.ForceUnsupported
                ? "机型不受支持 (已选择强行读取), 读取失败"
                : "机型不受支持, 已停止读取", Theme.Err);
            return false;
        }
        catch (Exception ex)
        {
            _reader?.Dispose();
            _reader = null;
            SetStatus("打不开硬件: " + ex.Message, Theme.Err);
            return false;
        }
    }

    /// <summary>询问用户是否在机型不符的情况下继续。用户点"继续"就直接重新打开。</summary>
    private void ShowBlockingMessage(string reason, string guidance)
    {
        bool go = DarkDialog.Ask(this, "硬件不受支持",
            reason + "\n\n" + guidance + "\n\n是否仍然继续? (可能显示错误的风扇转速)",
            yes: "继续读取", no: "取消", tone: DarkDialog.Tone.Warning);

        if (!go) return;

        _settings.ForceUnsupported = true;
        _settings.Save();

        // 重新打开一次。这一步失败也不再弹第二个对话框。
        _giveUpOpening = false;
        OpenReader(interactive: false);
    }

    /// <summary>清掉"跳过机型校验"的标志并重新检测。托盘菜单用, 给用户一个反悔的入口。</summary>
    private void RetryHardwareCheck()
    {
        _settings.ForceUnsupported = false;
        _settings.Save();
        _giveUpOpening = false;
        CloseReader();
        OpenReader(interactive: true);
        OnTick(null, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_reader is null || !_reader.IsOpen)
        {
            // 已经明确放弃 (机型不受支持且用户选了不再继续) 就什么也不做,
            // 否则会每秒重开一次设备、每秒再弹一个对话框。
            if (_giveUpOpening) return;

            // 设备可能因为休眠/驱动重启而失效, 定期尝试重连。
            // 这里绝不能弹对话框: 定时器每秒跑一次。
            if (!OpenReader(interactive: false)) return;
        }

        FanSample sample = _reader!.Read();

        if (sample.Error is not null)
        {
            _consecutiveErrors++;
            if (_consecutiveErrors >= 5)
            {
                // 连续失败多半是句柄失效, 重连一次。
                // 先记住原因: OpenReader 成功会把状态改成"已连接", 失败会写它自己的错误,
                // 两种情况都不该把"为什么重连"这条线索丢掉, 所以重连完再补写一次。
                string why = "读取异常: " + sample.Error;
                OpenReader(interactive: false);
                if (_reader is null || !_reader.IsOpen)
                    SetStatus(why, Theme.Err);
                _consecutiveErrors = 0;
            }
            return;
        }

        _consecutiveErrors = 0;

        if (sample.Fans.Count >= 2)
        {
            ApplyFanCard(_fan1Card, sample.Fans[0]);
            ApplyFanCard(_fan2Card, sample.Fans[1]);
        }

        _tempCard.Value = sample.Temperature?.ToString() ?? "--";

        // 每秒用 EC 回读的真实模式校准界面。
        // 别的程序 (或用户自己用实验工具) 把标志改掉时, 这里能立刻反映出来,
        // 而不是让勾选框一直停在一个不存在的状态上。
        if (!_fanModeBusy)
        {
            UpdateFanModeUi(sample.Mode);
            _suppressFanModeEvent = true;
            try { _strongCoolRow.SetCheckedSilently(sample.Mode == FanModeState.StrongCool); }
            finally { _suppressFanModeEvent = false; }
        }

        CheckThermalWatchdog(sample);

        if (_settings.PublishToRtss)
        {
            // 上一轮写失败把 _aida 扔掉之后必须在这里重建, 否则供数会永久停掉:
            // ApplyPublishMode 只在打开设备/从休眠恢复/改勾选时才跑, 定时器里没人再建。
            if (_aida is not { IsOpen: true } &&
                DateTime.UtcNow.Ticks - _lastAidaTryTicks >= AidaRetryIntervalTicks)
            {
                _lastAidaTryTicks = DateTime.UtcNow.Ticks;
                ApplyPublishMode();
            }

            if (_aida is { IsOpen: true })
            {
                try { _aida.Publish(ToSnapshot(sample)); }
                catch (Exception ex)
                {
                    // 写失败说明映射已经不可用, 丢掉, 下一轮在这里重建。
                    _aida.Dispose();
                    _aida = null;
                    SetStatus("向 RTSS 供数失败: " + ex.Message, Theme.Warn);
                }
            }
        }
    }

    /// <summary>把一次读数刷到卡片上, 同时按可信度决定小圆点颜色。</summary>
    private static void ApplyFanCard(DataCard card, FanReading f)
    {
        card.Value = FormatRpm(f);

        // 圆点语义: 有可信读数=绿; 停转=灰 (正常但值得注意); 不可信=橙。
        card.DotColor = !f.IsValid ? Theme.Warn : (f.IsStopped ? Theme.Text3 : Theme.Ok);
    }

    /// <summary>
    /// 温度看门狗。强冷是"只升不降", 所以它不会制造过热 ——
    /// 这个看门狗防的是另一类意外: EC 不接受手动 PWM、或者别的软件把标志改回自动后
    /// 我们仍在界面上显示"强冷中"。
    ///
    /// 触发条件必须连续多次达到阈值: 单次尖峰 (编译、游戏加载) 不该打断用户的强冷。
    /// </summary>
    private void CheckThermalWatchdog(FanSample sample)
    {
        // 已经交还过一次就不再反复触发, 否则每 3 秒弹一个框。
        if (_emergencyRestored) return;

        if (sample.Mode != FanModeState.StrongCool || sample.Temperature is not int t)
        {
            _hotSamples = 0;
            return;
        }

        if (t < EcRegisters.EmergencyTemperatureC)
        {
            _hotSamples = 0;
            return;
        }

        _hotSamples++;

        if (_hotSamples >= EcRegisters.EmergencyHoldSamples)
        {
            _hotSamples = 0;
            UiLog($"过热保护触发: {t}C 连续 {EcRegisters.EmergencyHoldSamples} 次, 交还自动模式");
            EmergencyRestoreAuto("过热保护");
        }
    }

    private static string FormatRpm(FanReading f)
    {
        // 读不稳定/越界优先报 "--"; 只有在读数可信时,"0" 才真的代表风扇停了。
        if (!f.IsValid) return "--";
        if (f.IsStopped) return "0";
        return f.Rpm >= 9999 ? ">9999" : f.Rpm.ToString();
    }

    private static FanSnapshot ToSnapshot(FanSample s) => new()
    {
        Fans = s.Fans.Select(f => new FanEntry
        {
            Id = $"FAN{f.Index}",
            Label = f.Label,
            ValueText = f.IsValid ? (f.IsStopped ? "0" : f.Rpm.ToString()) : "",
        }).ToArray(),
        Temperature = s.Temperature,
    };

    private void ApplyPublishMode()
    {
        if (_settings.PublishToRtss)
        {
            _lastAidaTryTicks = DateTime.UtcNow.Ticks;
            try
            {
                _aida ??= new AidaSharedMemory();
                _aida.Open();
            }
            catch (Exception ex)
            {
                _lastAidaTryTicks = DateTime.UtcNow.Ticks;

                // Open() 抛异常时 _aida 已经是一个"存在但没打开"的实例。
                // 不扔掉它, OnTick 每秒都会 Publish 一次并抛 "共享内存尚未打开",
                // 状态栏就被这条假错误一直刷屏, 真正的失败原因反而看不见了。
                _aida?.Dispose();
                _aida = null;
                SetStatus("打不开 AIDA64 共享内存: " + ex.Message, Theme.Warn);
            }
        }
        else
        {
            _aida?.Dispose();
            _aida = null;
        }
    }

    // ---------------------------------------------------------------- 开机自启动

    private void OnAutoStartChanged(object? sender, EventArgs e)
{
        if (_suppressAutoStartEvent) return;
        if (_autoStart is null) return;

        AutoStartManager.Result result = _autoStartRow.Checked ? _autoStart.Enable() : _autoStart.Disable();

        if (result.Ok)
        {
            SetStatus(result.Message.Split('\n')[0], Theme.Text2);
            return;
        }

        SetStatus(result.Message.Split('\n')[0], Theme.Warn);
        DarkDialog.Show(this, "开机自启动", result.Message, DarkDialog.Tone.Error);

        // 失败必须把勾选拨回真实状态, 否则界面在撒谎: 用户以为已经开了。
        // 用 SetCheckedSilently 而不是直接赋 Checked, 避免又触发一次 CheckedChanged。
        _suppressAutoStartEvent = true;
        try { _autoStartRow.SetCheckedSilently(_autoStart.IsEnabled()); }
        finally { _suppressAutoStartEvent = false; }
    }

    // ---------------------------------------------------------------- 交还自动

    /// <summary>
    /// 立刻把风扇交还 EC 自动模式, 并同步设置里记的意愿。
    ///
    /// 这是所有"收手"路径的唯一出口: 托盘菜单、过热保护、程序退出、关机。
    /// 集中在一处是为了保证每条路径都做同样的事 —— 尤其都要把 _settings.FanMode
    /// 也改回 Auto, 否则下次启动的归一化会把风扇又拉满。
    /// </summary>
    private void EmergencyRestoreAuto(string why)
{
        // 先改设置再动硬件: 万一 SetMode 抛异常, 至少下次启动不会再拉满。
        _settings.FanMode = FanMode.Auto;
        _settings.Save();

        _suppressFanModeEvent = true;
        try { _strongCoolRow.SetCheckedSilently(false); }
        finally { _suppressFanModeEvent = false; }

        if (_reader is { IsOpen: true })
        {
            FanModeResult r = _reader.SetMode(FanMode.Auto);
            if (!r.Ok)
            {
                UiLog("EmergencyRestoreAuto (" + why + ") FAILED: " + r.Message);
                SetStatus("恢复自动风扇失败: " + r.Message, Theme.Err);
                return;
            }

            UpdateFanModeUi(FanModeState.Auto);
        }

        UiLog("EmergencyRestoreAuto (" + why + ") ok");

        if (why == "过热保护")
        {
            // 这种情况必须让用户知道: 他们以为强冷在跑, 实际已经被撤掉了。
            _emergencyRestored = true;
            SetStatus("温度过高, 已自动交还风扇控制给 EC (强冷模式已取消)。", Theme.Warn);
            DarkDialog.Show(this, "过热保护",
                $"CPU 温度连续 {EcRegisters.EmergencyHoldSamples} 次达到 {EcRegisters.EmergencyTemperatureC}C, 已把风扇交还 EC 自动控制。" +
                "\n\n这通常意味着强冷没有生效 (EC 没有接受手动 PWM)。建议检查散热环境, 或先关闭强冷。",
                DarkDialog.Tone.Warning);
        }
        else if (why == "程序退出")
        {
            // 用户正要退出, 不需要再告诉他什么。
        }
        else
        {
            SetStatus($"已交还自动模式 ({why})。", Theme.Text2);
        }
    }
    // ---------------------------------------------------------------- 风扇模式

    private void OnStrongCoolChanged(object? sender, EventArgs e)
    {
        if (_suppressFanModeEvent) return;

        SetFanMode(_strongCoolRow.Checked ? FanMode.StrongCool : FanMode.Auto, userInitiated: true);
    }

    /// <param name="userInitiated">
    /// 用户亲手点的 (要弹失败提示、失败要回滚勾选) 还是程序自己做的
    /// (启动归一化、过热保护、退出恢复 —— 静默进行, 失败只写状态栏)。
    /// </param>
    private void SetFanMode(FanMode mode, bool userInitiated, string? reason = null)
    {
        if (_fanModeBusy) return;

        _fanModeBusy = true;
        try
        {
            if (_reader is null || !_reader.IsOpen)
            {
                if (userInitiated)
                {
                    DarkDialog.Show(this, "风扇模式", "设备还没打开, 现在不能切换风扇模式。", DarkDialog.Tone.Warning);
                    RevertFanModeCheck();
                }
                return;
            }

            FanModeResult result = _reader.SetMode(mode);

            // 无论成败都记住用户的选择:
            //   成功 -> 下次开机按这个模式归一化;
            //   失败 -> 用户确实想要这个模式, 下次启动会再试一次。
            // 但"用户选择"和"EC 实际状态"是两件事, 徽章只信后者。
            _settings.FanMode = mode;
            _settings.Save();

            UpdateFanModeUi(result.State);

            if (result.Ok)
            {
                if (mode == FanMode.StrongCool)
                {
                    // 重新进入强冷, 让看门狗重新有机会工作。
                    _emergencyRestored = false;
                    _hotSamples = 0;
                    SetStatus("强冷已开启: 两个风扇满转 (约 5700 RPM), 温度会明显下降。", Theme.Ok);
                }
                else
                {
                    SetStatus(reason ?? "已交还自动模式, 由 EC 按温度曲线调速。", Theme.Text2);
                }
            }
            else
            {
                SetStatus(result.Message, Theme.Warn);

                if (userInitiated)
                {
                    DarkDialog.Show(this, "风扇模式切换失败", result.Message, DarkDialog.Tone.Error);
                    RevertFanModeCheck();
                }
            }
        }
        finally
        {
            _fanModeBusy = false;
        }
    }

    /// <summary>把勾选状态拉回 EC 的真实状态。切换失败时必须调, 否则界面会撒谎。</summary>
    private void RevertFanModeCheck()
    {
        FanModeState actual = _reader?.ReadModeState() ?? FanModeState.Unknown;
        UpdateFanModeUi(actual);

        _suppressFanModeEvent = true;
        try { _strongCoolRow.SetCheckedSilently(actual == FanModeState.StrongCool); }
        finally { _suppressFanModeEvent = false; }
    }

    /// <summary>徽章永远显示 EC 回读出来的真实状态, 而不是我们以为自己设置的状态。</summary>
    private void UpdateFanModeUi(FanModeState state)
    {
        switch (state)
        {
            case FanModeState.Auto:
                _strongCoolRow.SetBadgeTone("实际: 自动", Theme.Text2, Theme.Panel2, Theme.LineSoft);
                break;

            case FanModeState.StrongCool:
                // 绿色 10% 透明底色: 与设计稿 .badge.cool 的 rgba 效果一致。
                _strongCoolRow.SetBadgeTone("实际: 强冷", Theme.Ok, Blend(Theme.Panel, Theme.Ok, 0.10f), Blend(Theme.Line, Theme.Ok, 0.45f));
                break;

            case FanModeState.Mixed:
                // 两路标志不一致, 或者落在 0xA5/0xCC 这些我们没实现的模式上。
                _strongCoolRow.SetBadgeTone("实际: 异常标志", Theme.Warn, Blend(Theme.Panel, Theme.Warn, 0.10f), Blend(Theme.Line, Theme.Warn, 0.45f));
                break;

            default:
                // 设备没打开, 我们不知道真实状态, 就不显示任何断言。
                _strongCoolRow.SetBadgeTone("", Theme.Text3, Color.Transparent, Color.Transparent);
                break;
        }
    }

    /// <summary>把 b 按 t 的比例混进 a。用来做设计稿里那种"语义色 10% 底"。</summary>
    private static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    // ---------------------------------------------------------------- 关闭与托盘

    /// <summary>
    /// 用户点 × 时收进托盘而不是退出 —— 但只在真的建出托盘图标时才这么做。
    /// 托盘没建起来还隐藏窗口, 用户就再也找不回这个程序了, 只能去任务管理器杀。
    /// </summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != CloseReason.UserClosing || !_settings.MinimizeToTray || _trayIcon is null)
            return;

        e.Cancel = true;

        // 不能在 FormClosing 里直接改 ShowInTaskbar: 那会重建窗口句柄,
        // 重建之后 e.Cancel 就失效了, 进程会真的退出。
        // 必须延后到关闭流程结束之后再隐藏。
        BeginInvoke(new Action(HideToTray));
    }

    private void HideToTray()
    {
        if (_trayIcon is null) return;
        if (_hidingToTray) return;

        _hidingToTray = true;
        try
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Hide();

            // 只在第一次收进托盘时提示一次, 之后每天开机都弹就是骚扰了。
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.BalloonTipTitle = "WujieFan 仍在后台运行";
                _trayIcon.BalloonTipText = "风扇转速照常读数并送往 RTSS。双击托盘图标可以重新打开窗口, 右键可以退出。";
                _trayIcon.ShowBalloonTip(4000);
            }
        }
        finally
        {
            _hidingToTray = false;
        }
    }

    private void OnFormResize(object? sender, EventArgs e)
    {
        // Relayout 会改 ClientSize, 也会触发 Resize, 但那不是"用户最小化了窗口"。
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray && !_exiting)
            BeginInvoke(new Action(HideToTray));
    }

    // ---------------------------------------------------------------- 电源事件

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // 这个事件来自系统线程, 必须切回 UI 线程再动控件。
        if (e.Mode != PowerModes.Resume) return;
        if (_disposedUi || IsDisposed) return;

        try
        {
            if (InvokeRequired) BeginInvoke(new Action(OnResume));
            else OnResume();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnResume()
    {
        if (_disposedUi || IsDisposed) return;

        // 休眠恢复后 EC 句柄通常已经作废, 定时器会一直读到错误。直接重连一次。
        _timer.Stop();
        try
        {
            OpenReader(interactive: false);
            ApplyPublishMode();
        }
        finally
        {
            _timer.Start();
        }

        OnTick(null, EventArgs.Empty);
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // 注销/关机前把风扇交还自动。用户下次开机时 EC 大概率已经掉电复位,
        // 但"重启"走的是软复位, EC RAM 不清 —— 那样下次开机就会出现满转风扇。
        RestoreAutoOnShutdown();

        // 同时把设置里记的模式改回自动:
        // 如果留着强冷, 下次开机会按设置重新拉满, 而用户刚才是关机, 不是要强冷。
        _settings.FanMode = FanMode.Auto;
        _settings.Save();
    }

    private void RestoreAutoOnShutdown()
    {
        if (_reader is not { IsOpen: true }) return;
        if (_reader.ReadModeState() != FanModeState.StrongCool) return;

        FanModeResult r = _reader.SetMode(FanMode.Auto);
        UiLog("RestoreAutoOnShutdown: " + (r.Ok ? "ok" : r.Message));
    }

    // ---------------------------------------------------------------- 状态栏

    /// <summary>写状态栏。颜色只允许传语义色的那几种, 圆的颜色由它推导。</summary>
    private void SetStatus(string text, Color color)
    {
        Color dot = color == Theme.Ok || color == Theme.Warn || color == Theme.Err
            ? color
            : Color.Transparent;

        _statusBar.SetStatus(text, color, dot);

        // 文案变长时状态栏要变高, 窗口跟着长 —— 不重排就会被裁掉。
        Relayout();
    }

    // ---------------------------------------------------------------- 释放

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposedUi = true;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;

            _timer.Stop();

            // 正常退出时也要交还风扇。托盘菜单的"退出"已经调过一次, 但
            // Dispose 是从别的路径进来的 (比如系统注销), 这里必须再兜一次。
            RestoreAutoOnShutdown();

            _timer.Dispose();
            CloseReader();

            _aida?.Dispose();
            _aida = null;

            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        base.Dispose(disposing);
    }

    private static byte[] LoadLpcIoBlob()
    {
        var asm = typeof(FanReader).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith("LpcIO.bin", StringComparison.OrdinalIgnoreCase)) continue;

            using Stream? s = asm.GetManifestResourceStream(name);
            if (s is null) continue;

            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return buffer.ToArray();
        }

        throw new FileNotFoundException("找不到 LpcIO.bin 嵌入资源。");
    }

    private static void UiLog(string message) => global::WujieFan.Program.UiLog(message);
}
