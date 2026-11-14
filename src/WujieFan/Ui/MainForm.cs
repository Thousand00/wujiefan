using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Windows.Forms;
using WujieFan.Core.Config;
using WujieFan.Core.Ec;
using WujieFan.Core.Hardware;
using WujieFan.Core.Providers;

namespace WujieFan.Ui;

/// <summary>
/// 主窗口: 显示两个风扇转速与 CPU 温度, 提供开机自启动与 RTSS 供数开关。
///
/// 刻意做得朴素: 这个程序的价值是把正确的数字送到 RTSS,
/// 界面只要让人一眼看清状态、能开关自启动就够了。
/// </summary>
public sealed class MainForm : Form
{
    private readonly bool _startMinimized;

    private readonly Label _fan1Value = new();
    private readonly Label _fan2Value = new();
    private readonly Label _tempValue = new();
    private readonly Label _statusLabel = new();
    private readonly Label _fanModeState = new();
    private readonly CheckBox _autoStartCheck = new();
    private readonly CheckBox _strongCoolCheck = new();

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

    private void BuildUi()
    {
        Text = "WujieFan - 无界14+ 风扇转速";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(420, 340);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "风扇转速",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Location = new Point(20, 16),
            AutoSize = true,
        };

        var model = new Label
        {
            Name = "modelLabel",
            Text = "机器信息读取中...",
            ForeColor = Color.Gray,
            Location = new Point(20, 46),
            Size = new Size(380, 32),
        };

        _fan1Value.Text = "--";
        _fan1Value.Font = new Font("Consolas", 26F, FontStyle.Bold);
        _fan1Value.Location = new Point(20, 86);
        _fan1Value.AutoSize = true;

        var fan1Caption = new Label
        {
            Text = "Fan1 (CPU)",
            ForeColor = Color.Gray,
            Location = new Point(20, 148),
            AutoSize = true,
        };

        _fan2Value.Text = "--";
        _fan2Value.Font = new Font("Consolas", 26F, FontStyle.Bold);
        _fan2Value.Location = new Point(200, 86);
        _fan2Value.AutoSize = true;

        var fan2Caption = new Label
        {
            Text = "Fan2 (GPU)",
            ForeColor = Color.Gray,
            Location = new Point(200, 148),
            AutoSize = true,
        };

        _tempValue.Text = "--";
        _tempValue.Font = new Font("Consolas", 16F);
        _tempValue.Location = new Point(330, 96);
        _tempValue.AutoSize = true;

        var tempCaption = new Label
        {
            Text = "CPU °C",
            ForeColor = Color.Gray,
            Location = new Point(330, 130),
            AutoSize = true,
        };

        _autoStartCheck.Text = "开机自动启动 (需要管理员权限)";
        _autoStartCheck.Location = new Point(20, 182);
        _autoStartCheck.AutoSize = true;
        _autoStartCheck.CheckedChanged += OnAutoStartChanged;

        var rtssCheck = new CheckBox
        {
            Text = "向 RTSS 提供数据 (AIDA64 通道)",
            Location = new Point(20, 208),
            AutoSize = true,
            Checked = _settings.PublishToRtss,
        };
        rtssCheck.CheckedChanged += (_, _) =>
        {
            _settings.PublishToRtss = rtssCheck.Checked;
            _settings.Save();
            ApplyPublishMode();
        };

        _strongCoolCheck.Text = "强冷模式 (两个风扇满转)";
        _strongCoolCheck.Location = new Point(20, 234);
        _strongCoolCheck.AutoSize = true;
        _strongCoolCheck.CheckedChanged += OnStrongCoolChanged;

        // 实际模式由 EC 寄存器回读得出, 与服务端设置分开显示:
        // 用户勾了强冷却因为被别的程序改写而实际是自动时, 这里必须看得出来。
        _fanModeState.Text = "";
        _fanModeState.ForeColor = Color.Gray;
        // 单独占右侧一列: 勾选框文字是 AutoSize 的, 让它自己排版会和勾选框重叠。
        _fanModeState.Location = new Point(288, 236);
        _fanModeState.Size = new Size(112, 20);
        _fanModeState.TextAlign = ContentAlignment.MiddleRight;
        _fanModeState.AutoSize = false;

        _statusLabel.Text = "正在初始化...";
        _statusLabel.ForeColor = Color.Gray;
        _statusLabel.Location = new Point(20, 262);
        _statusLabel.Size = new Size(380, 60);

        Controls.AddRange(new Control[]
        {
            title, model, _fan1Value, fan1Caption, _fan2Value, fan2Caption,
            _tempValue, tempCaption, _autoStartCheck, rtssCheck,
            _strongCoolCheck, _fanModeState, _statusLabel,
        });
    }

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
            SetStatus("托盘图标创建失败, 已改为关窗口即退出: " + ex.Message, Color.DarkOrange);
        }
    }

    /// <summary>把异常写进 %LOCALAPPDATA%\WujieFan\ui.log, 便于排查"程序突然没了"这类问题。</summary>
    internal static void UiLog(string message) => WujieFan.Program.UiLog(message);
    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());

        // 逃生口: 不论程序内部状态如何, 点一下就把 EC 交还自动。
        // 面板上那个勾选框万一因为界面卡住点不动, 这里是最后一条路。
        menu.Items.Add("恢复自动风扇", null, (_, _) => EmergencyRestoreAuto("托盘菜单"));
        menu.Items.Add("重新检测机型", null, (_, _) => RetryHardwareCheck());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 WujieFan", null, (_, _) => ExitApplication());
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
                _autoStartCheck.Enabled = false;
                _autoStartCheck.Text = "开机自动启动 (同名任务已存在, 已锁定)";
            }
            else
            {
                bool enabled = _autoStart.IsEnabled();
                _autoStartCheck.Checked = enabled;

                // 已启用时还要检查任务定义是不是当前版本的要求。
                // 0.1.0 用 schtasks 模板建的旧任务带着"拔电源不启动"和"72 小时被强杀",
                // 用户不会为了升级再去勾一次自启动, 所以这里主动修。
                // 但只在确实不符合时才重写 —— 每次启动都跑 schtasks 既慢又在动系统。
                if (enabled && !_autoStart.IsUpToDate())
                {
                    AutoStartManager.Result fix = _autoStart.Enable();
                    if (!fix.Ok)
                        SetStatus("自启动任务更新失败: " + fix.Message.Split('\n')[0], Color.DarkOrange);
                }
            }
        }
        finally
        {
            _suppressAutoStartEvent = false;
        }

        (Controls["modelLabel"] as Label)!.Text = DescribeMachine();

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
        try { _strongCoolCheck.Checked = want == FanMode.StrongCool; }
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
                _settings.ForceUnsupported ? Color.DarkOrange : Color.Green);

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
                : "机型不受支持, 已停止读取", Color.Firebrick);
            return false;
        }
        catch (Exception ex)
        {
            _reader?.Dispose();
            _reader = null;
            SetStatus("打不开硬件: " + ex.Message, Color.Firebrick);
            return false;
        }
    }

    /// <summary>询问用户是否在机型不符的情况下继续。用户点"是"就直接重新打开。</summary>
    private void ShowBlockingMessage(string reason, string guidance)
    {
        DialogResult r = MessageBox.Show(this,
            reason + "\n\n" + guidance + "\n\n是否仍然继续? (可能显示错误的风扇转速)",
            "WujieFan - 硬件不受支持",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (r != DialogResult.Yes)
            return;

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
                    SetStatus(why, Color.Firebrick);
                _consecutiveErrors = 0;
            }
            return;
        }

        _consecutiveErrors = 0;

        if (sample.Fans.Count >= 2)
        {
            _fan1Value.Text = FormatRpm(sample.Fans[0]);
            _fan2Value.Text = FormatRpm(sample.Fans[1]);
        }

        _tempValue.Text = sample.Temperature?.ToString() ?? "--";

        // 每秒用 EC 回读的真实模式校准界面。
        // 别的程序 (或用户自己用实验工具) 把标志改掉时, 这里能立刻反映出来,
        // 而不是让勾选框一直停在一个不存在的状态上。
        if (!_fanModeBusy)
        {
            UpdateFanModeUi(sample.Mode);
            _suppressFanModeEvent = true;
            try { _strongCoolCheck.Checked = sample.Mode == FanModeState.StrongCool; }
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
                    SetStatus("向 RTSS 供数失败: " + ex.Message, Color.DarkOrange);
                }
            }
        }
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
                SetStatus("打不开 AIDA64 共享内存: " + ex.Message, Color.DarkOrange);
            }
        }
        else
        {
            _aida?.Dispose();
            _aida = null;
        }
    }

    /// <summary>
    /// 用户勾选/取消强冷。
    ///
    /// 失败时必须把勾选状态回滚 —— 界面显示"强冷中"而 EC 其实还在自动,
    /// 是这套功能里最危险的假象。
    /// </summary>
    private void OnStrongCoolChanged(object? sender, EventArgs e)
    {
        if (_suppressFanModeEvent) return;
        if (_fanModeBusy) return;

        bool want = _strongCoolCheck.Checked;
        SetFanMode(want ? FanMode.StrongCool : FanMode.Auto, userInitiated: true);
    }

    /// <summary>
    /// 执行一次模式切换并同步界面。返回是否成功。
    /// </summary>
    /// <param name="userInitiated">
    /// 用户点击时为 true, 失败要弹框说明; 启动归一化/看门狗为 false, 只写状态栏。
    /// </param>
    private bool SetFanMode(FanMode mode, bool userInitiated, string? reason = null)
    {
        if (_fanModeBusy) return false;

        if (_reader is null || !_reader.IsOpen)
        {
            if (userInitiated)
                MessageBox.Show(this, "设备还没打开, 现在不能切换风扇模式。", "WujieFan",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RevertFanModeCheck();
            return false;
        }

        _fanModeBusy = true;
        try
        {
            FanModeResult r = _reader.SetMode(mode);

            // 无论成败都按设置里的真实意图落盘: 失败时保留用户的选择,
            // 下次启动的归一化会再试一次, 而不是把用户的意愿悄悄忘掉。
            _settings.FanMode = mode;
            _settings.Save();

            UpdateFanModeUi(r.State);

            if (r.Ok)
            {
                _hotSamples = 0;
                if (mode == FanMode.StrongCool)
                {
                    _emergencyRestored = false;
                    SetStatus("强冷已开启: 两个风扇满转 (约 5700 RPM), 温度会明显下降。", Color.DarkGreen);
                }
                else
                {
                    SetStatus(reason is null ? "已交还自动模式, 由 EC 按温度曲线调速。" : reason, Color.DimGray);
                }

                return true;
            }

            SetStatus(r.Message, Color.DarkOrange);

            if (userInitiated)
                MessageBox.Show(this, r.Message, "WujieFan - 风扇模式切换失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            RevertFanModeCheck();
            return false;
        }
        finally
        {
            _fanModeBusy = false;
        }
    }

    /// <summary>把勾选框拉回 EC 的真实状态, 而不是设置里的意愿。</summary>
    private void RevertFanModeCheck()
    {
        FanModeState actual = _reader?.ReadModeState() ?? FanModeState.Unknown;
        UpdateFanModeUi(actual);

        _suppressFanModeEvent = true;
        try { _strongCoolCheck.Checked = actual == FanModeState.StrongCool; }
        finally { _suppressFanModeEvent = false; }
    }

    /// <summary>按 EC 回读到的真实模式刷新文字与勾选。</summary>
    private void UpdateFanModeUi(FanModeState state)
    {
        switch (state)
        {
            case FanModeState.Auto:
                _fanModeState.Text = "实际: 自动";
                _fanModeState.ForeColor = Color.Gray;
                break;

            case FanModeState.StrongCool:
                _fanModeState.Text = "实际: 强冷";
                _fanModeState.ForeColor = Color.DarkGreen;
                break;

            case FanModeState.Mixed:
                _fanModeState.Text = "实际: 异常标志";
                _fanModeState.ForeColor = Color.DarkOrange;
                break;

            default:
                _fanModeState.Text = "";
                _fanModeState.ForeColor = Color.Gray;
                break;
        }
    }

    /// <summary>
    /// 强制交还自动模式。托盘菜单、过热看门狗、退出程序都走这里。
    ///
    /// 与 SetFanMode 的区别: 它不看 _settings、不回读校验失败就重试,
    /// 而且即使设备已经打不开也照样把设置改成 Auto —— 下次启动会再归一化一次。
    /// 逃生口必须比"正常路径"更难失败。
    /// </summary>
    private void EmergencyRestoreAuto(string why)
    {
        _settings.FanMode = FanMode.Auto;
        _settings.Save();

        _suppressFanModeEvent = true;
        try { _strongCoolCheck.Checked = false; }
        finally { _suppressFanModeEvent = false; }

        if (_reader is { IsOpen: true })
        {
            FanModeResult r = _reader.SetMode(FanMode.Auto);
            UpdateFanModeUi(r.State);

            if (!r.Ok)
            {
                UiLog("EmergencyRestoreAuto FAILED (" + why + "): " + r.Message);
                SetStatus("恢复自动风扇失败: " + r.Message, Color.Firebrick);
                return;
            }
        }
        else
        {
            UpdateFanModeUi(FanModeState.Unknown);
        }

        UiLog("EmergencyRestoreAuto (" + why + ") ok");

        if (why == "过热保护")
        {
            SetStatus("温度过高, 已自动交还风扇控制权给 EC。", Color.DarkOrange);
            _emergencyRestored = true;
            if (!_settings.MinimizeToTray || Visible)
            {
                MessageBox.Show(this,
                    $"CPU 温度连续 {EcRegisters.EmergencyHoldSamples} 次达到 {EcRegisters.EmergencyTemperatureC}°C 以上, " +
                    "已自动关闭强冷模式, 把风扇交还给 EC 固件调速。\n\n" +
                    "如果温度仍然偏高, 请检查散热口是否被挡住。",
                    "WujieFan - 过热保护", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else if (why == "程序退出")
        {
            // 退出时写状态栏没意义, 窗口马上就没了, 只记日志。
        }
        else
        {
            SetStatus("已交还自动模式 (" + why + ")。", Color.DimGray);
        }
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_autoStart is null) return;

        // 初始化时回填勾选状态不该被当成用户操作。
        if (_suppressAutoStartEvent) return;

        AutoStartManager.Result r = _autoStartCheck.Checked ? _autoStart.Enable() : _autoStart.Disable();

        if (!r.Ok)
        {
            MessageBox.Show(this, r.Message, "WujieFan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            // 回滚勾选状态, 避免界面与实际不符。
            _autoStartCheck.CheckedChanged -= OnAutoStartChanged;
            _autoStartCheck.Checked = _autoStart.IsEnabled();
            _autoStartCheck.CheckedChanged += OnAutoStartChanged;
            return;
        }

        SetStatus(r.Message.Split('\n')[0], Color.DimGray);
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {

        // 点右上角关闭 = 收进托盘继续在后台供数; 只有托盘菜单的"退出"才真的结束进程。
        // 没有托盘图标时绝不能隐藏窗口 —— 那会让进程彻底失去入口, 只能去任务管理器杀。
        if (_exiting
            || e.CloseReason != CloseReason.UserClosing
            || !_settings.MinimizeToTray
            || _trayIcon is null)
            return;

        e.Cancel = true;

        // 关键: 不能在 FormClosing 里直接改 ShowInTaskbar —— 那会重建窗口句柄,
        // 让这次的"取消关闭"失效, 进程会被真的关掉。必须等关闭流程走完再隐藏。
        BeginInvoke(new Action(HideToTray));
    }

    private void HideToTray()
    {
        // 没有托盘图标就无处可回, 宁可留着窗口也不隐藏。
        if (_trayIcon is null) return;

        // WindowState 变化会再次触发 Resize, 用标志位挡住重入。
        if (_hidingToTray) return;
        _hidingToTray = true;

        try
        {
            // 先隐藏再改 ShowInTaskbar: 句柄重建发生在不可见状态下, 不会闪一下。
            WindowState = FormWindowState.Minimized;
            Hide();
            ShowInTaskbar = false;

            if (!_trayHintShown && _trayIcon is not null)
            {
                _trayHintShown = true;
                _trayIcon.BalloonTipTitle = "WujieFan 仍在后台运行";
                _trayIcon.BalloonTipText = "风扇转速照常读数并送往 RTSS。双击托盘图标可以重新打开窗口, 右键可以退出。";
                _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
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
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray && !_exiting)
            BeginInvoke(new Action(HideToTray));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || IsDisposed) return;

        // SystemEvents.PowerModeChanged 在系统事件线程上触发, 不是 UI 线程。
        // 直接在这里 OpenReader() 会跨线程碰控件 (SetStatus) 和 WinForms 定时器, 必须转回 UI 线程。
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(OnResume)); } catch (ObjectDisposedException) { }
            return;
        }

        OnResume();
    }

    /// <summary>
    /// 注销 / 关机。这是最后一个能写 EC 的时机 —— 之后的复位是冷启动,
    /// 但"重启"不算冷启动, 会把手动标志留在 RAM 里。
    /// 这里主动交还, 让重启后的表现也可预期。
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (_disposedUi) return;

        // 系统事件线程上不能碰控件, 但既然窗口马上要销毁, 直接写 EC 就够了。
        RestoreAutoOnShutdown();

        _settings.FanMode = FanMode.Auto;
        _settings.Save();
    }

    private void OnResume()
    {
        if (_disposedUi) return;

        // 从休眠/睡眠恢复后 EC 句柄可能失效, 主动重连一次。
        _timer.Stop();
        OpenReader(interactive: false);
        ApplyPublishMode();
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    /// <summary>
    /// 关闭时最后一次交还自动模式。不更新界面 (窗口正在销毁), 只写日志。
    /// </summary>
    private void RestoreAutoOnShutdown()
    {
        try
        {
            if (_reader is not { IsOpen: true }) return;
            if (_reader.ReadModeState() != FanModeState.StrongCool) return;

            FanModeResult r = _reader.SetMode(FanMode.Auto);
            UiLog("RestoreAutoOnShutdown: " + (r.Ok ? "ok" : r.Message));
        }
        catch (Exception ex)
        {
            UiLog("RestoreAutoOnShutdown EXCEPTION: " + ex.Message);
        }
    }

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

            // 兜底: 走到 Dispose 说明监听的窗口已经关了, 无论如何不该把 EC 留在手动模式。
            // 正常情况下 ExitApplication 已经交还过了, 这里再核对一次是幂等且便宜的。
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
            if (name.EndsWith("LpcIO.bin", StringComparison.OrdinalIgnoreCase))
            {
                using Stream s = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        throw new FileNotFoundException("找不到 LpcIO.bin 嵌入资源。");
    }
}
