using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;
using Zephyr.Core.Config;
using Zephyr.Core.Ec;
using Zephyr.Core.Hardware;

namespace Zephyr.Ui;

/// <summary>
/// 主窗口: 显示两个风扇转速与 CPU 温度, 提供强冷开关与设置入口。
///
/// 界面按 UI/Zephyr-UI-设计稿.html 实现 (2026-11-15): 深色圆角卡片、
/// 自绘标题栏、三列数据卡、整行可点的开关、语义色状态栏。
/// 所有布局坐标都是设计稿的 96dpi CSS px, 经 Dpi.Px 换算到实际设备像素。
///
/// 这里只留窗口骨架: 布局与绘制、控件接线、状态栏、开机自启动、电源/会话事件、
/// 释放顺序。轮询见 FanPollingController, 强冷状态机见 FanModeController,
/// 供数见 RtssPublisher, 托盘见 TrayHost。
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
    private readonly ToggleRow _strongCoolRow = new();
    private readonly ActionRow _settingsRow = new();
    private readonly Label _machineLabel = new();
    private readonly Label _footnoteLabel = new();

    private readonly TitleBarChrome _chrome;

    private readonly AppSettings _settings = AppSettings.Load();
    private AutoStartManager? _autoStart;


    private readonly RtssPublisher _publisher;
    private readonly TrayHost _tray;
    private readonly FanModeController _mode;
    private readonly FanPollingController _polling;

    /// <summary>全局快捷键宿主。整段生命周期里只建一个, 换键位就重新注册, 不重建窗口。</summary>
    private readonly GlobalHotKey _hotKey;

    private bool _exiting;

    /// <summary>启动时查到了改名前的旧计划任务, 要在状态提示里说一次。</summary>
    private bool _legacyTaskNotice;

    /// <summary>启动时查到了改名前的旧版进程还在跑。它当下就在抢 EC 锁, 比旧任务更要紧。</summary>
    private bool _legacyProcessNotice;

    /// <summary>窗口已进入销毁流程。休眠恢复回调可能晚到, 用它挡住。</summary>
    private bool _disposedUi;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        _chrome = new TitleBarChrome(this, DesignTitleHeight);

        _publisher = new RtssPublisher(_settings, SetStatus);
        _tray = new TrayHost(this, SetStatus);

        // 热键回调直接在 UI 线程上执行 (WM_HOTKEY 投递到我们自己建的隐藏窗口),
        // 但按下那一刻可能正在跑别的消息处理, 所以仍然排到消息队列尾部再切模式。
        _hotKey = new GlobalHotKey();
        _hotKey.Pressed += OnHotKeyPressed;

        // 三者互相看得见, 但构造顺序不能成环: 用 Func 延迟取值,
        // 读的是"当时那个实例", 而不是构造那一刻的快照。
        FanPollingController? polling = null;
        _mode = new FanModeController(_settings, () => polling?.Reader, _strongCoolRow, this, SetStatus);
        polling = new FanPollingController(
            this, _settings, _fan1Card, _fan2Card, _tempCard,
            _publisher, () => _mode, SetStatus);
        _polling = polling;

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
        Text = "Zephyr - 无界14+ 风扇转速";
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
        _tempCard.MeterColor = Theme.Hot;

        _strongCoolRow.Text = "强冷模式";
        _strongCoolRow.Hint = "(两个风扇满转)";
        _strongCoolRow.CheckedChanged += (_, _) => _mode.OnUserToggle();

        _settingsRow.Text = "设置";
        _settingsRow.Hint = "(自启动 / RTSS / 过热保护 / 快捷键)";
        _settingsRow.Click += (_, _) => OpenSettings();

        _footnoteLabel.AutoSize = false;
        _footnoteLabel.BackColor = Theme.WinBg;
        _footnoteLabel.ForeColor = Theme.Text3;
        _footnoteLabel.Font = Theme.Footnote;
        _footnoteLabel.Text = "自启动 / RTSS 供数 / 过热保护 / 全局快捷键都在「设置」里; 点 × 不收进程, 只收进托盘继续读数与供数。";

        _statusBar.SetStatus("正在初始化...", Theme.Text2, Color.Transparent);

        Controls.AddRange(new Control[]
        {
            _machineLabel, _fan1Card, _fan2Card, _tempCard,
            _strongCoolRow, _settingsRow, _statusBar, _footnoteLabel,
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

        _strongCoolRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
        y += rowH;
        _settingsRow.Bounds = new Rectangle(rowX, y, rowW, rowH);
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
        _chrome.Layout(w);

        // 用刚刚算出来的目标尺寸, 而不是 Width/Height。窗口最小化 (含收进托盘) 时
        // Width/Height 是系统给的占位矩形 (实测 237x39), 拿它裁 Region 会把这个小矩形
        // 写进窗口, 恢复后整块窗体只剩左上角一条可见。状态栏每秒都在 SetStatus →
        // Relayout → 这里, 只要有一拍赶上最小化就会中招, 所以症状时有时无、重启才好。
        UpdateRegion(w, y);
    }

    /// <summary>无边框窗口要自己把圆角裁出来, 否则四个角是直角深色块。</summary>
    private void UpdateRegion(int width, int height)
    {
        if (_disposedUi || IsDisposed || !IsHandleCreated) return;
        if (width <= 0 || height <= 0) return;

        using var path = Round.Path(new RectangleF(0, 0, width, height), Dpi.PxF(this, 12));
        Region?.Dispose();
        Region = new Region(path);
    }

    /// <summary>
    /// 不知道目标尺寸时用这个重载。最小化期间 ClientSize 会变成 0x0、Width/Height 变成
    /// 系统占位矩形, 从最小化切回 Normal 的那一瞬间窗口尺寸也可能还没更新, 所以取
    /// ClientSize 与 RestoreBounds 里较大的那个: 稳定状态下两者相等, 过渡期较小的那个
    /// 一定是过时值。
    /// </summary>
    private void UpdateRegion()
    {
        if (_disposedUi || IsDisposed || !IsHandleCreated) return;

        Size client = ClientSize;
        Size restore = RestoreBounds.Size;
        int width = Math.Max(client.Width, restore.Width);
        int height = Math.Max(client.Height, restore.Height);

        UpdateRegion(width, height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Win11 的原生圆角 + 阴影, 拿不到就靠上面的 Region 兜底。
        TitleBarChrome.ApplyRoundedCorners(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // Dpi.Px() 按 DeviceDpi 换算, 字号按 point 自动放大 —— 换到另一块
        // 缩放比不同的显示器时, 必须整窗重排一次, 否则子控件还停在旧比例上,
        // 而 Region 也还是旧尺寸, 会出现"内容比窗口小一圈"的错位。
        if (_disposedUi || IsDisposed) return;

        Relayout();
        Invalidate(true);
    }

    /// <summary>
    /// 句柄重建后把窗口外观重新套一遍: 原生圆角是绑在句柄上的, 改 ShowInTaskbar
    /// 或 Hide/Show 都会换一个新句柄, 新句柄默认没有圆角; 顺便按当前真实尺寸重算一次
    /// Region, 免得显示出来的是最小化期间算的那个尺寸。
    /// </summary>
    internal void RefreshChrome()
    {
        if (_disposedUi || IsDisposed) return;

        if (IsHandleCreated) TitleBarChrome.ApplyRoundedCorners(Handle);

        Relayout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        PaintWindowBackdrop(g);

        _chrome.Paint(g);

        int titleH = Dpi.Px(this, DesignTitleHeight);
        using var sep = new Pen(Theme.LineSoft);
        g.DrawLine(sep, 0, titleH, Width, titleH);

        // 图标区域之外再画一圈细描边, 让无边框窗口有边界感
        using var border = new Pen(Theme.Line);
        using var outline = Round.Path(Round.Inset(new RectangleF(0, 0, Width, Height), 0.5f), Dpi.PxF(this, 12));
        g.DrawPath(border, outline);
    }

    /// <summary>
    /// 窗口底: 上略亮、下略暗的竖向渐变, 再铺一层 22px 网格暗纹 (alpha 5%)。
    ///
    /// 网格是纯装饰, 不响应任何输入。它铺在最底层, 卡片和状态栏都盖在上面,
    /// 所以只在空隙里露出来 —— 那正是设计稿想要的效果: 有质感但不抢注意力。
    /// </summary>
    private void PaintWindowBackdrop(Graphics g)
    {
        var full = ClientRectangle;
        if (full.Width <= 0 || full.Height <= 0) return;

        using (var bg = new LinearGradientBrush(
            new Rectangle(0, 0, full.Width, Math.Max(full.Height, 1)),
            Theme.Panel, Theme.WinBg, LinearGradientMode.Vertical))
        {
            g.FillRectangle(bg, full);
        }

        int step = Dpi.Px(this, 22);
        if (step < 6) return;

        using var gridPen = new Pen(Theme.Grid);
        for (int x = 0; x < full.Width; x += step)
            g.DrawLine(gridPen, x, 0, x, full.Height);
        for (int y = 0; y < full.Height; y += step)
            g.DrawLine(gridPen, 0, y, full.Width, y);
    }

    // ---------------------------------------------------------------- 标题栏交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_chrome.OnMouseMove(e.Location))
            Invalidate(new Rectangle(0, 0, Width, _chrome.Height));

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_chrome.OnMouseLeave())
            Invalidate(new Rectangle(0, 0, Width, _chrome.Height));

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_chrome.HitsClose(e.Location))
        {
            Close();
            return;
        }

        if (_chrome.HitsMinimize(e.Location))
        {
            HideToTray();
            return;
        }

        if (e.Y < _chrome.Height)
        {
            _chrome.StartDrag(Handle);
            return;
        }

        base.OnMouseDown(e);
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnFormLoad(object? sender, EventArgs e)
    {
        _tray.Create(RestoreFromTray, () => _mode.RestoreAuto("托盘菜单"), _polling.RetryHardwareCheck, OpenSettings, ExitApplication);

        _autoStart = new AutoStartManager();

        // 开机自启的真相在计划任务里, 不在配置文件里, 所以这里不缓存状态 ——
        // 设置弹窗每次打开都重新问一遍。启动时只做一件必要的事: 修旧任务定义。
        //
        // 0.1.0 用 schtasks 模板建的旧任务带着"拔电源不启动"和"72 小时被强杀",
        // 用户不会为了升级再去勾一次自启动, 所以这里主动修。
        // 但只在确实不符合时才重写 —— 每次启动都跑 schtasks 既慢又在动系统。
        //
        // 只查一次: 原来 HasForeignTask + IsEnabled 各查一次、IsUpToDate 内部又查两次,
        // 四次同步 schtasks 串在 UI 线程上, 正常也要几百毫秒白屏。
        // 归属判定用同一次查询的结论, 只有确实属于我们时才去做"是否过期"的细查。
        AutoStartManager.TaskState startupTask = _autoStart.QueryState();
        if (startupTask == AutoStartManager.TaskState.Ours && !_autoStart.IsUpToDate())
        {
            AutoStartManager.Result fix = _autoStart.Enable();
            if (!fix.Ok)
                SetStatus("自启动任务更新失败: " + fix.Message.Split('\n')[0], Theme.Warn);
        }

        // 改名留下的旧计划任务。它指向旧目录里的旧启动器, 不清掉的话开机时旧版照样起来,
        // 与新版本抢同一个 EC 互斥量。这里只提示, 不替用户删 —— 见 LegacyTaskPresent 的注释。
        if (AutoStartManager.LegacyTaskPresent())
        {
            UiLog("检测到改名前的旧计划任务还在: " + RuntimeIdentity.LegacyTaskName);
            _legacyTaskNotice = true;
        }

        // 旧版进程还在跑: 两个版本的单实例互斥量不同名, 所以它们会同时活着抢 EC 锁。
        // 这不是"以后可能出问题", 是当下风扇已经控制不了 —— 优先级高于旧任务。
        if (AutoStartManager.LegacyProcessRunning())
        {
            UiLog("检测到改名前的旧版进程还在运行: " + RuntimeIdentity.LegacyProcessName + ".exe");
            _legacyProcessNotice = true;
        }

        _machineLabel.Text = DescribeMachine();

        // 打开设备内部已经按需建好共享内存, 这里不必再调一次。
        _polling.Open();

        // 按设置做一次模式归一化。放在 Open 之后、定时器启动之前:
        //   - 上次正常退出已经把模式交还自动了, 这里只是核对一遍;
        //   - 上次是被强杀/蓝屏留下的, 这里负责把状态拉回设置所说的样子。
        _mode.NormalizeOnStartup();

        _polling.Start();
        _polling.Poll();

        // 设置提示放在所有初始化状态写完之后, 否则会被 Open / Poll
        // 写的"正常"文案盖掉。用户改了设置却没生效, 必须让人看见一次。
        //
        // 两种情况必须分开说, 因为后果完全不同:
        //   - 降级: 文件读不出/损坏 → 用安全默认且**不接管风扇**, 这是警告色;
        //   - 归一化: 某个字段越界或过期被修正 → 程序照常接管风扇,
        //     只是值被改过。这种用警告色会让人以为出事了, 降到普通文字色。
        if (AppSettings.LoadDegraded)
        {
            string why = AppSettings.LoadWarning ?? "原因未知";
            SetStatus("设置文件已降级为安全默认 (不接管风扇): " + why, Theme.Warn);
            UiLog("AppSettings degraded: " + why);
        }
        else if (AppSettings.LoadNormalized)
        {
            string why = AppSettings.LoadWarning ?? "原因未知";
            SetStatus("设置中有字段越界或过期, 已自动修正: " + why, Theme.Text2);
            UiLog("AppSettings normalized: " + why);
        }
        else if (AppSettings.LoadMigrated)
        {
            // 改名后第一次启动。用户最直接的担心是"我的快捷键和阈值还在吗",
            // 所以这里必须说清楚是沿用了旧版本的设置, 而不是悄悄换了一份默认值。
            SetStatus("已从旧版本沿用设置 (快捷键、过热保护、RTSS 开关都在)。", Theme.Text2);
            UiLog("AppSettings migrated from " + RuntimeIdentity.LegacyDataDirectoryName);
        }
        else if (AppSettings.LoadMigrateError is { } migrateError)
        {
            // 旧配置在, 但没搬过来 —— 用户马上会发现快捷键与阈值全回默认。
            // 不说的话他只会以为"新版本把我的设置弄丢了", 而真正的原因 (权限/磁盘/占用)
            // 只躺在 LastError 里, 而 LastError 会被下一次 Save 冲掉。
            SetStatus("旧版本的设置没能沿用过来, 本次按默认设置启动: " + migrateError, Theme.Warn);
            UiLog("AppSettings migrate failed: " + migrateError);
        }

        // 旧计划任务的提示放在设置提示之后: 它比"设置被归一化"更需要用户动手,
        // 但比"设置降级"轻 —— 降级意味着这次不接管风扇, 那才是必须先看到的。
        if (_legacyTaskNotice)
        {
            SetStatus("检测到旧的 WujieFan 开机任务, 请按 README 用一条命令删除, 否则开机时新旧版本会抢 EC。", Theme.Warn);
        }

        // 旧版进程比旧任务更急: 旧任务只是下次开机才起作用, 旧进程现在就在抢 EC 锁,
        // 两条同时命中时优先说这条 —— 用户关掉旧进程之后, 下次启动才会只剩旧任务那条。
        if (_legacyProcessNotice)
        {
            SetStatus("检测到旧的 WujieFan 进程还在运行, 两个版本会抢 EC 锁导致都控制不了风扇。请先退出旧版。", Theme.Warn);
        }

        // 全局热键注册在这里, 有两个原因:
        //   1. 设备已经打开并轮询过一轮, 按下时 reader 一定就绪;
        //   2. 注册失败要写状态栏, 放在降级/归一化提示之后才不会被盖掉。
        ApplyHotKey();

        // 开机自启动时不弹窗打扰, 直接进托盘。手动启动则正常显示窗口,
        // 用户点关闭键时再按 MinimizeToTray 决定是缩托盘还是退出。

        if (_startMinimized) HideToTray();
    }

    private static string DescribeMachine()
    {
        HardwareIdentity id = HardwareIdentity.Read();
        if (!id.WasRead) return "机器信息不可读";
        return $"{id.SystemManufacturer} {id.SystemProductName} · BIOS {id.BiosVersion} · EC {id.EcVersionText}";
    }

    // ---------------------------------------------------------------- 全局热键

    /// <summary>
    /// 按设置重新注册全局热键。失败要说出来, 但不要让程序走不下去 ——
    /// 热键只是快捷方式, 主界面上的开关仍然可用。
    /// </summary>
    private void ApplyHotKey()
    {
        // 解析失败不能让后面静默地当成"没设热键": TryParse 返回 false 说明配置文件里
        // 是一段谁也不认的文本 (手改坏了, 或者旧版本写进去的不可表示形式)。
        // 只注销、报错, 不清空 —— 清空就真的丢了, 留着至少用户可以照着改。
        if (!HotKeyCombo.TryParse(_settings.StrongCoolHotKey, out HotKeyCombo? combo))
        {
            _hotKey.Unregister();
            SetStatus("全局快捷键的写法无法识别, 已暂时停用: " + _settings.StrongCoolHotKey, Theme.Warn);
            UiLog("GlobalHotKey: 无法解析的键位写法 \"" + _settings.StrongCoolHotKey + "\"");
            return;
        }

        if (!_hotKey.TryRegister(combo, out string? error))
        {
            // 注册失败不要清掉设置。曾经在这里把 StrongCoolHotKey 抹成空串, 理由是
            // "留着会让用户以为下次会生效" —— 但这个理由站不住, 而且代价很实在:
            // 升级/重启时旧实例还没退、仍占着同一个键, 新实例一启动就把用户的键位
            // 永久删了(0.5.5 上线时就真丢了用户的 Ctrl+Shift+End)。占用者下次启动
            // 时不一定还在, 所以正确的做法是保留键位、当场说清楚这次为什么没生效。
            SetStatus("全局快捷键不可用: " + error, Theme.Warn);
            UiLog("GlobalHotKey: " + error);
        }
    }

    /// <summary>
    /// 热键被按下。用 BeginInvoke 排到消息队列尾部, 而不是就地处理:
    /// WM_HOTKEY 可能在别的消息处理过程中到达, 就地切模式等于在别人的调用栈里写 EC。
    /// </summary>
    private void OnHotKeyPressed(object? sender, EventArgs e)
    {
        PostToUi(() =>
        {
            if (_disposedUi || IsDisposed || _exiting) return;

            (bool ok, string message) = _mode.ToggleFromHotKey();

            // 用户此时很可能在看别的程序, 甚至主窗口收在托盘里, 所以状态栏之外
            // 再补一条托盘气泡 —— 否则按下去像没反应。
            _tray.ShowBalloon(message, ok ? Theme.Ok : Theme.Warn);

            if (!ok) SetStatus(message, Theme.Warn);
        });
    }

    // ---------------------------------------------------------------- 设置

    /// <summary>
    /// 打开设置弹窗并落实用户的选择。
    ///
    /// 顺序有讲究: 先把值写回 _settings 并落盘, 成功了才去动计划任务和 RTSS 供数。
    /// 反过来做的话, 一旦落盘失败就会出现"自启动已经改了但配置里没记"的分裂状态,
    /// 下次启动又按旧配置纠正回来 —— 用户会觉得设置自己乱跳。
    /// </summary>
    private void OpenSettings()
    {
        if (_autoStart is null) return;

        // 打开前重新问一次计划任务的真实状态: 用户可能在程序之外用任务计划程序
        // 或别的工具动过它, 只信启动时那次快照会把界面显示错。
        //
        // 只查一次, 并且把结果复用到展示与决策两处。曾经是展示查一次、决策再查一次,
        // 两次之间任何抖动 (schtasks 超时、权限瞬时失败) 都会让结论前后矛盾 ——
        // 最坏的组合是"第一次说没开、第二次说开着", 于是判定需要关闭,
        // 把用户原本正常的自启动任务删掉。查不到 (Unknown) 时锁定并置灰,
        // 因为此时**无法确认**任何事, 动它就是瞎动。
        var state = new AutoStartState();
        AutoStartManager.TaskState taskState = _autoStart.QueryState();
        if (taskState == AutoStartManager.TaskState.Unknown)
        {
            state.Locked = true;
            state.Hint = "(查询计划任务失败, 已锁定)";
        }
        else if (taskState == AutoStartManager.TaskState.Foreign)
        {
            state.Locked = true;
            state.Hint = "(同名任务已存在, 已锁定)";
        }
        else
        {
            state.Enabled = taskState == AutoStartManager.TaskState.Ours;
        }

        // 打开弹窗之前先注销热键。两个原因:
        //   1. 用户在里面点"录制"要按组合键, 若当前组合键仍然注册着, 按下去会先被系统
        //      派发成一次真实的模式切换 —— 明明只是想改键位, 风扇却真的切了;
        //   2. 录制时希望按键老老实实进到弹窗的键盘消息里, 而不是被 WM_HOTKEY 截走。
        // 无论用户最后是按保存还是取消, 关掉弹窗后都会按当前设置重新注册。
        _hotKey.Unregister();

        // 弹窗直接把值写回 _settings 这个共享对象。万一落盘失败, 内存里已经是"新值"了,
        // 之后任何一次 Save (改风扇模式、托盘操作、退出) 都会把它悄悄落盘 ——
        // 用户看到的却是"本次修改不会生效"。所以先记下用户能改的那几项, 失败时还原。
        bool pubBefore = _settings.PublishToRtss;
        int tempBefore = _settings.EmergencyTemperatureC;
        int holdBefore = _settings.EmergencyHoldSamples;
        string hotKeyBefore = _settings.StrongCoolHotKey;

        if (!SettingsDialog.Show(this, _settings, state))
        {
            // 取消了: 设置没变, 但热键在打开弹窗时被注销了, 按原样装回去。
            ApplyHotKey();
            return;
        }

        // 弹窗只改了对象里的值, 落盘在这里。失败要让用户看见:
        // 悄悄失败等于让他以为设置生效了, 下次启动又变回去。
        if (!_settings.Save())
        {
            _settings.PublishToRtss = pubBefore;
            _settings.EmergencyTemperatureC = tempBefore;
            _settings.EmergencyHoldSamples = holdBefore;
            _settings.StrongCoolHotKey = hotKeyBefore;

            SetStatus("设置保存失败, 本次修改不会生效", Theme.Err);
            UiLog("SettingsDialog: 保存失败");
            DarkDialog.Show(this, "设置",
                "设置写入配置文件失败, 本次修改不会生效。请检查 %LOCALAPPDATA%\\Zephyr 是否可写。",
                DarkDialog.Tone.Error);
            // 设置没落盘, 热键按内存里(已还原的)值装回去, 别让它就这么一直哑着。
            ApplyHotKey();
            return;
        }

        // RTSS 供数开关是纯本地状态, 立刻生效。
        _publisher.Apply();

        // 热键换了就重新注册。放在落盘之后: 得先保证新值已经写进去了。
        ApplyHotKey();

        // 开机自启要动系统: 按用户选的目标态执行, 失败则把界面拉回真实状态。
        // 决策复用上面那一次查询的结果, 不再重复问 schtasks —— 见上方注释。
        bool wantAutoStart = state.Enabled && !state.Locked;
        bool currentlyOn = taskState == AutoStartManager.TaskState.Ours;
        if (!state.Locked && wantAutoStart != currentlyOn)
        {
            AutoStartManager.Result result = wantAutoStart ? _autoStart.Enable() : _autoStart.Disable();
            if (result.Ok)
            {
                SetStatus(result.Message.Split('\n')[0], Theme.Text2);
            }
            else
            {
                // 失败必须回滚成真实状态, 否则界面在撒谎: 用户以为已经开了。
                state.Enabled = _autoStart.IsEnabled();
                SetStatus(result.Message.Split('\n')[0], Theme.Warn);
                DarkDialog.Show(this, "开机自动启动", result.Message, DarkDialog.Tone.Error);
            }
        }
        else
        {
            SetStatus("设置已保存", Theme.Text2);
        }
    }

    // ---------------------------------------------------------------- 关闭与托盘

    /// <summary>
    /// 把动作排到 UI 队列末尾执行, 窗口已销毁/句柄已释放时静默放弃。
    ///
    /// 关闭与托盘流程里有好几处必须"延后到当前消息之后"才安全的操作 (改 ShowInTaskbar
    /// 会重建句柄, 在 FormClosing 里直接改会让 e.Cancel 失效), 而这类操作的触发时机
    /// 恰好与退出重叠: 用户连点 ×、关机、托盘退出都可能让句柄在我们排完队之后就没
    /// 了 —— 那时 BeginInvoke 抛 ObjectDisposedException / InvalidOperationException,
    /// 而它跑在消息循环里, 没人捕获就会变成未处理异常。这里统一吞掉这两种"对象已经
    /// 不在了"的异常; 其它异常照旧上抛, 不掩盖真正的 bug。
    /// </summary>
    private void PostToUi(Action action)
    {
        if (_disposedUi || IsDisposed) return;

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// 用户点 × 时收进托盘而不是退出 —— 但只在真的建出托盘图标时才这么做。
    /// 托盘没建起来还隐藏窗口, 用户就再也找不回这个程序了, 只能去任务管理器杀。
    /// </summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != CloseReason.UserClosing || !_settings.MinimizeToTray || !_tray.Available)
            return;

        e.Cancel = true;

        // 不能在 FormClosing 里直接改 ShowInTaskbar: 那会重建窗口句柄,
        // 重建之后 e.Cancel 就失效了, 进程会真的退出。
        // 必须延后到关闭流程结束之后再隐藏。
        PostToUi(HideToTray);
    }

    /// <summary>收进托盘。实现在 TrayHost, 这里留一个短名给窗口内部调用。</summary>
    private void HideToTray() => _tray.HideToTray();

    private void OnFormResize(object? sender, EventArgs e)
    {
        // Relayout 会改 ClientSize, 也会触发 Resize, 但那不是"用户最小化了窗口"。
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray && !_exiting)
        {
            PostToUi(HideToTray);
            return;
        }

        // 从最小化/隐藏恢复时再按真实尺寸裁一次圆角。Relayout 只在 SetStatus 时跑,
        // 恢复本身不一定触发它, 所以这里补一刀; 尺寸取 ClientSize 与 RestoreBounds
        // 的较大者, 过渡期的过时值不会把小矩形写进 Region。
        if (WindowState == FormWindowState.Normal && !_disposedUi && !IsDisposed)
            UpdateRegion();
    }

    /// <summary>把隐藏的窗口叫回来 (托盘双击、托盘菜单、以及第二次启动的唤醒事件都走这里)。</summary>
    public void RestoreFromTray() => _tray.Restore();

    private void ExitApplication()
    {
        // 退出前主动交还风扇控制权。冷启动确实会复位 EC, 但"重启" (软复位) 不会 ——
        // 不在退出时收手, 用户点了退出还以为风扇已经回到自动, 实际还在满转。
        // 这一步失败也不阻止退出: 卡在满转只是吵, 而不让用户退出才是真正的问题。
        //
        // 走 RestoreAutoOnExit 而不是 RestoreAuto: 后面 Close() → Dispose() 还会
        // 再调一次 RestoreAutoOnShutdown, 两条路共用一个一次性标志, 退出时只写一次 EC。
        _mode.RestoreAutoOnExit();

        _exiting = true;
        Close();
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
        _polling.Stop();
        try
        {
            _polling.Open(interactive: false);
            _publisher.Apply();
        }
        finally
        {
            _polling.Start();
        }

        _polling.Poll();
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        // 这个方法**故意不切回 UI 线程**, 与 OnPowerModeChanged 的写法不同, 理由有两条:
        //
        // 1. 关机/注销是有截止时间的。BeginInvoke 只是把工作排进消息队列, Windows 完全
        //    可能在它被执行之前就把进程结束掉 —— 那样风扇交还就悄无声息地没做, 而
        //    "退出时没交还"正是这个程序最不该出的问题。同步执行至少保证在返回前做完。
        // 2. 它做的三件事 (交还 EC、改设置、写日志) 全都不碰控件, 所以在哪个线程跑都
        //    不会崩。
        //
        // 代价是一条必须守住的约束: **谁在这里加任何与界面相关的调用, 都必须先切回 UI 线程**。
        // 这不是隐含假设, 是这个方法存在的条件。
        if (_disposedUi || IsDisposed) return;

        // 注销/关机前把风扇交还自动。用户下次开机时 EC 大概率已经掉电复位,
        // 但"重启"走的是软复位, EC RAM 不清 —— 那样下次开机就会出现满转风扇。
        _mode.RestoreAutoOnShutdown();

        // 同时把设置里记的模式改回自动:
        // 如果留着强冷, 下次开机会按设置重新拉满, 而用户刚才是关机, 不是要强冷。
        _settings.FanMode = FanMode.Auto;

        // 关机路径里不能弹框。存不进去的后果是下次开机按设置重新拉满风扇,
        // 所以这条必须在 ui.log 里留下书面记录, 方便事后解释"为什么开机就满转"。
        if (!_settings.Save())
            UiLog("退出时保存模式=自动 失败: " + (AppSettings.LastError ?? "未知原因"));
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposedUi = true;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;

            _polling.Stop();

            // 正常退出时也要交还风扇。托盘菜单的"退出"已经调过一次, 但
            // Dispose 是从别的路径进来的 (比如系统注销), 这里必须再兜一次。
            _mode.RestoreAutoOnShutdown();

            _polling.DisposeTimer();
            _polling.Close();

            _publisher.Dispose();
            _tray.Dispose();

            // 注销全局热键。进程退出后系统会自己回收, 但程序里有非正常退出路径
            // (系统注销、电源事件), 那几条路不走 FormClosed, 在这里兜住。
            _hotKey.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void UiLog(string message) => global::Zephyr.Program.UiLog(message);
}
