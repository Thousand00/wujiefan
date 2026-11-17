using System.Drawing;
using WujieFan.Core.Config;
using WujieFan.Core.Ec;

namespace WujieFan.Ui;

/// <summary>
/// 强冷模式状态机: 勾选框 ↔ EC 写入 ↔ 回读校准 ↔ 看门狗。
///
/// 这里集中了"我们是否仍主张强冷"这件事的全部判断依据。分成两层:
///   - 意图 (_strongCoolIntent): 由用户操作或启动归一化确认, 是本会话的保护授权;
///   - 回读 (FanSample.Mode): 只用来同步界面和告警, 不参与"该不该继续监控"。
/// 看门狗必须按意图判断, 不能按回读判断 —— 它要防的正是「EC 里的标志已被外部改回自动、
/// 而用户仍以为强冷在生效」这类情况。
/// </summary>
internal sealed class FanModeController
{
    private readonly AppSettings _settings;
    private readonly Func<FanReader?> _reader;
    private readonly ToggleRow _row;
    private readonly Form _owner;
    private readonly Action<string, Color> _setStatus;

    /// <summary>正在回填强冷勾选状态, 此时不应把 CheckedChanged 当成用户操作。</summary>
    private bool _suppressEvent;

    /// <summary>正在执行模式切换 (含读回校验), 期间忽略重入。</summary>
    private bool _busy;

    /// <summary>连续多少次采样温度达到看门狗阈值。只在强冷模式下累计。</summary>
    private int _hotSamples;

    /// <summary>已经因过热自动交还过 EC, 本轮不再重复弹提示。</summary>
    private bool _emergencyRestored;

    /// <summary>
    /// 本会话是否仍持有"强冷"意图 —— 由用户操作或启动归一化确认, 与 EC 回读的实时模式无关。
    /// </summary>
    private bool _strongCoolIntent;

    /// <summary>强冷意图期间连续读失败的采样数, 达到阈值就主动交还自动。</summary>
    private int _readFailSamples;

    /// <summary>本轮连续读失败的起点, 给阈值加一条最短持续时间, 防止轮询间隔很短时误判。</summary>
    private DateTime _firstReadFailAt;

    /// <summary>模式被外部改掉的告警只发一次, 避免每秒刷屏; 模式回到强冷或意图清除后复位。</summary>
    private bool _modeDiverged;

    /// <summary>强冷意图期间连续拿到无效温度的采样数, 与读失败共用阈值。</summary>
    private int _tempUnknownSamples;

    /// <summary>本轮连续无效温度的起点, 与读失败同样加一条最短持续时间。</summary>
    private DateTime _firstTempUnknownAt;

    /// <summary>
    /// 强冷期间连续读失败几次就交还自动。取 3: 默认 1 秒轮询下约 3 秒。
    /// 单次失败是正常的 (与 HWiNFO / LHM 抢 EC 互斥量、一次 16 位读抖动),
    /// 连续 3 次则说明 EC 通路已经坏了 —— 这时我们既确认不了温度也确认不了模式,
    /// 继续把手动满转留在机器上是不负责任的。
    /// </summary>
    private const int ReadFailRestoreSamples = 3;

    /// <summary>
    /// 交还自动前要求读失败至少持续这么久。轮询间隔可配到 200ms, 只数次数会在
    /// 0.6 秒内就交还, 对一次驱动重启来说太急了。
    /// </summary>
    private static readonly TimeSpan ReadFailMinDuration = TimeSpan.FromSeconds(2);

    /// <summary>交还原因串。交还流程用它挑选文案, 也写进日志。</summary>
    private const string ReadFailReason = "EC 读取持续失败";

    /// <summary>另一条"确认不了"的路径: EC 读得到, 但温度寄存器给不出有效值。</summary>
    private const string TempUnknownReason = "温度读数无效";

    public FanModeController(
        AppSettings settings,
        Func<FanReader?> reader,
        ToggleRow row,
        Form owner,
        Action<string, Color> setStatus)
    {
        _settings = settings;
        _reader = reader;
        _row = row;
        _owner = owner;
        _setStatus = setStatus;
    }

    /// <summary>正在切换模式。轮询那边据此暂停"按回读校准界面", 免得把写入过程中的中间态画出来。</summary>
    public bool Busy => _busy;

    /// <summary>本会话是否仍主张强冷。退出路径用它判断要不要写 EC。</summary>
    public bool HasIntent => _strongCoolIntent;

    /// <summary>勾选框的 CheckedChanged 出口。</summary>
    public void OnUserToggle()
    {
        if (_suppressEvent) return;

        SetMode(_row.Checked ? FanMode.StrongCool : FanMode.Auto, userInitiated: true);
    }

    /// <summary>
    /// 启动时把 EC 拉到"设置里记着的模式"。
    ///
    /// 这条规则同时覆盖了几种历史残留:
    ///   设置=自动  -> 若 EC 还停在上次的 0xDC, 写回 0x00 (强杀、蓝屏、手动杀的残留)
    ///   设置=强冷  -> 重新写一遍满转 (软重启不清 EC RAM, 但我们要保证值是对的)
    /// 换句话说: 开机后是什么状态, 由设置决定, 而不是由"上次进程死得干不干净"决定。
    /// </summary>
    public void NormalizeOnStartup()
    {
        // 启动时的意图由设置决定, 与接下来读到的 EC 状态无关 —— 这是看门狗的唯一授权来源。
        _strongCoolIntent = _settings.FanMode == FanMode.StrongCool;

        FanReader? reader = _reader();
        if (reader is null || !reader.IsOpen)
        {
            UpdateUi(FanModeState.Unknown);
            return;
        }

        FanModeState actual = reader.ReadModeState();
        FanMode want = _settings.FanMode;

        _suppressEvent = true;
        try { _row.SetCheckedSilently(want == FanMode.StrongCool); }
        finally { _suppressEvent = false; }

        // 值和意愿已经一致, 不必再写 EC —— 少一次写入就少一次出错机会。
        bool matches = want == FanMode.StrongCool
            ? actual == FanModeState.StrongCool
            : actual == FanModeState.Auto;

        if (matches)
        {
            UpdateUi(actual);
            return;
        }

        // 归一化失败不该弹框打断开机, 但必须留下可见痕迹。
        SetMode(want, userInitiated: false, reason: "已按设置恢复自动模式 (清理上次残留)。");
    }

    /// <summary>
    /// 每次成功读到样本后调一次: 用回读校准界面, 再跑温度看门狗。
    /// 顺序与拆分前一致 (校准 → 看门狗), 不要调换。
    /// </summary>
    public void Observe(FanSample sample)
    {
        // 这一轮读到了, 失败累计归零。看门狗因而只在真正连续的失败段里累计。
        _readFailSamples = 0;

        // 每秒用 EC 回读的真实模式校准界面。
        // 别的程序 (或用户自己用实验工具) 把标志改掉时, 这里能立刻反映出来,
        // 而不是让勾选框一直停在一个不存在的状态上。
        if (!_busy)
        {
            UpdateUi(sample.Mode);
            _suppressEvent = true;
            try { _row.SetCheckedSilently(sample.Mode == FanModeState.StrongCool); }
            finally { _suppressEvent = false; }

            // 意图仍是强冷、EC 里却已经不是强冷: 说明被外部改掉了 (别的软件、EC 自己
            // 或一次失败的写入)。这是必须让用户知道的状态漂移, 但不能每秒刷屏。
            if (_strongCoolIntent && sample.Mode != FanModeState.StrongCool && !_modeDiverged)
            {
                _modeDiverged = true;
                UiLog($"强冷意图仍然有效, 但 EC 实际模式是 {sample.Mode}");
                _setStatus(
                    "EC 里的强冷标志已被改回自动 (可能被其他软件重置), 界面已同步为真实状态。",
                    Theme.Warn);
            }
            else if (sample.Mode == FanModeState.StrongCool)
            {
                _modeDiverged = false;
            }
        }

        CheckThermalWatchdog(sample);
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
        // 没有强冷意图就没有需要兜底的手动满转, 直接复位计数。
        if (!_strongCoolIntent)
        {
            _hotSamples = 0;
            return;
        }

        // 已经交还过一次就不再反复触发, 否则每 3 秒弹一个框。
        if (_emergencyRestored) return;

        if (sample.Temperature is not int t)
        {
            // 采样成功但温度被过滤成 null (寄存器值落在 1..100 之外): 同样确认不了温度。
            // 这里不能保留进度 —— "连续 N 次高温"必须是真的连续, 留着旧计数会让很久
            // 之后的一次高温读数单独引爆保护, 那是误判而不是保护。
            _hotSamples = 0;

            // 但也不能就这么算了: 读失败走的是 CheckReadError, 而这条路径
            // sample.Error 为 null, 永远到不了那里。持续读不到温度 = 无法确认是否过热,
            // 与读失败等价, 用同一个阈值收手 (次数 + 最短持续时间, 理由见那边)。
            if (_tempUnknownSamples == 0) _firstTempUnknownAt = DateTime.UtcNow;
            _tempUnknownSamples++;

            bool enough = _tempUnknownSamples >= ReadFailRestoreSamples
                       && DateTime.UtcNow - _firstTempUnknownAt >= ReadFailMinDuration;

            if (enough)
            {
                _tempUnknownSamples = 0;
                UiLog($"看门狗: 连续 {ReadFailRestoreSamples} 次拿不到有效温度 (持续 {(DateTime.UtcNow - _firstTempUnknownAt).TotalSeconds:F1}s), 交还自动模式");
                RestoreAuto(TempUnknownReason);
            }
            return;
        }

        _tempUnknownSamples = 0;

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
            RestoreAuto("过热保护");
        }
    }

    /// <summary>
    /// 读失败时的看门狗: 连续 N 次读不到 EC 且持续足够久, 就主动交还自动。
    ///
    /// EC 读不到意味着温度与模式都无法确认 —— 此时保留"手动满转"其实无害 (满转只会更吵),
    /// 但保留"我们以为还在强冷"的假象有害, 而且如果之前的写入只成功了一半 (两路标志不一致),
    /// 继续放着就可能停在一个没人验证过的状态上。所以宁可收手。
    ///
    /// 阈值取 3 次 / 至少 2 秒: 单次失败是正常的 (与 HWiNFO、LHM 抢 EC 互斥量,
    /// 一次 16 位读数抖动), 连续 3 次说明通路已经坏了; 但轮询间隔可配到 200ms,
    /// 只数次数会在 0.6 秒内就收手, 对一次驱动重启来说太急, 因此再加一条时间下限。
    /// </summary>
    public void CheckReadError()
    {
        if (!_strongCoolIntent) return;
        if (_emergencyRestored) return;

        if (_readFailSamples == 0) _firstReadFailAt = DateTime.UtcNow;
        _readFailSamples++;

        bool enough = _readFailSamples >= ReadFailRestoreSamples
                   && DateTime.UtcNow - _firstReadFailAt >= ReadFailMinDuration;

        if (!enough) return;

        _readFailSamples = 0;
        UiLog($"看门狗: 连续 {ReadFailRestoreSamples} 次 EC 读取失败 (持续 {(DateTime.UtcNow - _firstReadFailAt).TotalSeconds:F1}s), 交还自动模式");

        // 交还本身会写 EC, 这又是一次可能失败的操作 —— 但它的失败是可接受的:
        // RestoreAuto 内部已经先落盘设置再动硬件, 即便写失败, 下次启动也不会再拉满。
        RestoreAuto(ReadFailReason);
    }

    /// <param name="userInitiated">
    /// 用户亲手点的 (要弹失败提示、失败要回滚勾选) 还是程序自己做的
    /// (启动归一化、过热保护、退出恢复 —— 静默进行, 失败只写状态栏)。
    /// </param>
    public void SetMode(FanMode mode, bool userInitiated, string? reason = null)
    {
        if (_busy) return;

        _busy = true;
        try
        {
            FanReader? reader = _reader();
            if (reader is null || !reader.IsOpen)
            {
                if (userInitiated)
                {
                    DarkDialog.Show(_owner, "风扇模式", "设备还没打开, 现在不能切换风扇模式。", DarkDialog.Tone.Warning);
                    RevertCheck();
                }
                return;
            }

            FanModeResult result = reader.SetMode(mode);

            // 无论成败都记住用户的选择:
            //   成功 -> 下次开机按这个模式归一化;
            //   失败 -> 用户确实想要这个模式, 下次启动会再试一次。
            // 但"用户选择"和"EC 实际状态"是两件事, 徽章只信后者。
            _settings.FanMode = mode;
            _settings.Save();

            UpdateUi(result.State);

            if (result.Ok)
            {
                // 只在这里建立意图: 硬件确实接受了, 我们才主张"强冷在跑"。
                // 失败分支不建立, 否则看门狗会守着一个从未生效的模式。
                _strongCoolIntent = mode == FanMode.StrongCool;
                _readFailSamples = 0;
                _tempUnknownSamples = 0;
                _modeDiverged = false;

                if (mode == FanMode.StrongCool)
                {
                    // 重新进入强冷, 让看门狗重新有机会工作。
                    _emergencyRestored = false;
                    _hotSamples = 0;
                    _setStatus("强冷已开启: 两个风扇满转 (约 5700 RPM), 温度会明显下降。", Theme.Ok);
                }
                else
                {
                    _setStatus(reason ?? "已交还自动模式, 由 EC 按温度曲线调速。", Theme.Text2);
                }
            }
            else
            {
                // 硬件没接受, 意图跟着撤销: 界面和看门狗都不能停在"已强冷"上。
                _strongCoolIntent = false;
                _hotSamples = 0;
                _readFailSamples = 0;
                _tempUnknownSamples = 0;

                // 不确定状态: 切换失败且回滚也失败, 两个风扇可能各停在一个模式上,
                // 我们也不知道寄存器里到底是什么。这比普通失败严重一个量级, 必须
                // 用最醒目的语义色, 且绝不能顺手把界面画成"已自动" —— 那是最坏的谎。
                if (result.Indeterminate)
                {
                    UiLog("SetMode INDETERMINATE: " + result.Message);

                    // 徽章只能如实说"不知道", 让 Unknown 分支清空断言。
                    UpdateUi(FanModeState.Unknown);

                    // 勾选框也不能停在"开": 我们已经撤销了意图, 再显示勾上就是另一种谎。
                    // 这里不去回读 EC —— 就是回读失败才走到这个分支的。
                    _suppressEvent = true;
                    try { _row.SetCheckedSilently(false); }
                    finally { _suppressEvent = false; }

                    _setStatus(result.Message, Theme.Err);

                    if (userInitiated)
                    {
                        // 文案直接用 Core 的, 它已经写清了「回滚未完成」「可能处于不确定状态」
                        // 「请立即确认 CPU 温度」; 上层重写容易丢掉这些关键提示。
                        DarkDialog.Show(_owner, "风扇状态不确定", result.Message, DarkDialog.Tone.Error);
                    }
                }
                else
                {
                    _setStatus(result.Message, Theme.Warn);

                    if (userInitiated)
                    {
                        DarkDialog.Show(_owner, "风扇模式切换失败", result.Message, DarkDialog.Tone.Error);
                        RevertCheck();
                    }
                }
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>把勾选状态拉回 EC 的真实状态。切换失败时必须调, 否则界面会撒谎。</summary>
    private void RevertCheck()
    {
        FanModeState actual = _reader()?.ReadModeState() ?? FanModeState.Unknown;
        UpdateUi(actual);

        _suppressEvent = true;
        try { _row.SetCheckedSilently(actual == FanModeState.StrongCool); }
        finally { _suppressEvent = false; }
    }

    /// <summary>徽章永远显示 EC 回读出来的真实状态, 而不是我们以为自己设置的状态。</summary>
    public void UpdateUi(FanModeState state)
    {
        switch (state)
        {
            case FanModeState.Auto:
                _row.SetBadgeTone("实际: 自动", Theme.Text2, Theme.Panel2, Theme.LineSoft);
                break;

            case FanModeState.StrongCool:
                // 绿色 10% 透明底色: 与设计稿 .badge.cool 的 rgba 效果一致。
                _row.SetBadgeTone("实际: 强冷", Theme.Ok, Blend(Theme.Panel, Theme.Ok, 0.10f), Blend(Theme.Line, Theme.Ok, 0.45f));
                break;

            case FanModeState.Mixed:
                // 两路标志不一致, 或者落在 0xA5/0xCC 这些我们没实现的模式上。
                _row.SetBadgeTone("实际: 异常标志", Theme.Warn, Blend(Theme.Panel, Theme.Warn, 0.10f), Blend(Theme.Line, Theme.Warn, 0.45f));
                break;

            default:
                // 设备没打开, 我们不知道真实状态, 就不显示任何断言。
                _row.SetBadgeTone("", Theme.Text3, Color.Transparent, Color.Transparent);
                break;
        }
    }

    /// <summary>
    /// 立刻把风扇交还 EC 自动模式, 并同步设置里记的意愿。
    ///
    /// 这是所有"收手"路径的唯一出口: 托盘菜单、过热保护、程序退出、关机、看门狗。
    /// 集中在一处是为了保证每条路径都做同样的事 —— 尤其都要把 _settings.FanMode
    /// 也改回 Auto, 否则下次启动的归一化会把风扇又拉满。
    /// </summary>
    public void RestoreAuto(string why)
    {
        // 意图先撤: 后面无论硬件写成功还是失败, 我们都已经不再主张"强冷中"。
        // 否则看门狗会拿一条已经放弃的意图继续累计, 反复触发交还。
        _strongCoolIntent = false;
        _readFailSamples = 0;
        _tempUnknownSamples = 0;
        _modeDiverged = false;

        // 先改设置再动硬件: 万一 SetMode 抛异常, 至少下次启动不会再拉满。
        _settings.FanMode = FanMode.Auto;
        _settings.Save();

        _suppressEvent = true;
        try { _row.SetCheckedSilently(false); }
        finally { _suppressEvent = false; }

        FanReader? reader = _reader();
        if (reader is { IsOpen: true })
        {
            FanModeResult r = reader.SetMode(FanMode.Auto);
            if (!r.Ok)
            {
                UiLog("RestoreAuto (" + why + ") FAILED: " + r.Message);

                // 连"回滚到自动"都失败: 我们不能假装已经恢复。徽章改为"未知",
                // 并且复用 _emergencyRestored 把看门狗按住, 不要每轮再叠一次写。
                if (r.Indeterminate)
                {
                    UiLog("RestoreAuto INDETERMINATE: " + r.Message);
                    _emergencyRestored = true;
                    UpdateUi(FanModeState.Unknown);
                    _setStatus(r.Message, Theme.Err);

                    // 读失败触发时 EC 本来就通不了, 这时弹模态框只会把界面锁死在
                    // 每秒一次失败的循环里; 状态栏的红色错误已经足够醒目。
                    if (why != ReadFailReason && why != TempUnknownReason && why != "程序退出")
                        DarkDialog.Show(_owner, "风扇状态不确定", r.Message, DarkDialog.Tone.Error);
                    return;
                }

                _setStatus("恢复自动风扇失败: " + r.Message, Theme.Err);
                return;
            }

            UpdateUi(FanModeState.Auto);
        }

        UiLog("RestoreAuto (" + why + ") ok");

        if (why == "过热保护")
        {
            // 这种情况必须让用户知道: 他们以为强冷在跑, 实际已经被撤掉了。
            _emergencyRestored = true;
            _setStatus("温度过高, 已自动交还风扇控制给 EC (强冷模式已取消)。", Theme.Warn);
            DarkDialog.Show(_owner, "过热保护",
                $"CPU 温度连续 {EcRegisters.EmergencyHoldSamples} 次达到 {EcRegisters.EmergencyTemperatureC}C, 已把风扇交还 EC 自动控制。" +
                "\n\n这通常意味着强冷没有生效 (EC 没有接受手动 PWM)。建议检查散热环境, 或先关闭强冷。",
                DarkDialog.Tone.Warning);
        }
        else if (why == "程序退出")
        {
            // 用户正要退出, 不需要再告诉他什么。
        }
        else if (why == ReadFailReason)
        {
            // 读失败可能每轮都发生, 绝不能弹模态框 —— 否则会变成每秒一个对话框,
            // 用户连点确定都来不及。只走状态栏 + 日志, 让人知道保护动作发生过。
            _emergencyRestored = true;
            _setStatus(
                $"连续 {ReadFailRestoreSamples} 次读不到 EC, 已把风扇交还自动控制 (无法确认温度时不再保持手动满转)。",
                Theme.Warn);
        }
        else if (why == TempUnknownReason)
        {
            // 同属"周期性触发"的一类, 理由同上一分支: 只写状态栏, 不弹框。
            _emergencyRestored = true;
            _setStatus(
                $"连续 {ReadFailRestoreSamples} 次拿不到有效温度, 已把风扇交还自动控制 (无法确认温度时不再保持手动满转)。",
                Theme.Warn);
        }
        else
        {
            _setStatus($"已交还自动模式 ({why})。", Theme.Text2);
        }
    }

    /// <summary>
    /// 关机/注销/退出前把 EC 收回自动。只在 EC 确实停在强冷时才写, 少一次无谓的写。
    /// </summary>
    public void RestoreAutoOnShutdown()
    {
        FanReader? reader = _reader();
        if (reader is not { IsOpen: true }) return;
        if (reader.ReadModeState() != FanModeState.StrongCool) return;

        FanModeResult r = reader.SetMode(FanMode.Auto);

        // 交还自动失败且"回滚也没成功"时, 风扇可能停在一个不确定的状态
        // (一个手动满转、一个交还 EC, 或标志没生效而 EC 仍在读残留 PWM)。
        // 关机前再试一次: SetMode 是幂等的, 成功一次就足够。
        if (!r.Ok && r.Indeterminate)
        {
            UiLog("RestoreAutoOnShutdown: 首次交还失败且回滚未完成, 重试一次: " + r.Message);
            FanModeResult retry = reader.SetMode(FanMode.Auto);
            r = retry;
        }

        if (r.Ok)
        {
            UiLog("RestoreAutoOnShutdown: ok");
        }
        else if (r.Indeterminate)
        {
            // 这里没有用户可看的界面 (正在关机), 只能留日志;
            // 下一句话是给排查日志的人看的, 不要改成中性的描述。
            UiLog("RestoreAutoOnShutdown: 失败且回滚未完成, 风扇状态不确定 —— "
                  + "若下次开机听到风扇狂转或没有转速, 请启动 WujieFan 手动切回自动。"
                  + " 详情: " + r.Message);
        }
        else
        {
            UiLog("RestoreAutoOnShutdown: " + r.Message);
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

    private static void UiLog(string message) => global::WujieFan.Program.UiLog(message);
}
