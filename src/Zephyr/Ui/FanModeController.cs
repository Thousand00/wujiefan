using System.Drawing;
using Zephyr.Core.Config;
using Zephyr.Core.Ec;

namespace Zephyr.Ui;

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

    /// <summary>
    /// 已经因为"读不到 EC"交还过自动, 本轮不再重复触发。
    ///
    /// 早先它还承担"过热保护只报一次"的职责 —— 温度链路取消后, 只剩读失败这一种
    /// 周期性触发, 语义反而更单纯了。
    /// </summary>
    private bool _emergencyRestored;

    /// <summary>
    /// 本会话是否仍持有"强冷"意图 —— 由用户操作或启动归一化确认, 与 EC 回读的实时模式无关。
    ///
    /// 注意"无关"的边界: 单次回读不同不会改它 (那可能只是我们自己的中间态), 但连续
    /// <see cref="DivergedSamplesToGiveUp"/> 次确认 EC 已不是强冷时会主动撤销,
    /// 因为看门狗把意图当作"现在确实在手动满转"的授权。
    /// </summary>
    private bool _strongCoolIntent;

    /// <summary>强冷意图期间连续读失败的采样数, 达到阈值就主动交还自动。</summary>
    private int _readFailSamples;

    /// <summary>本轮连续读失败的起点, 给阈值加一条最短持续时间, 防止轮询间隔很短时误判。</summary>
    private DateTime _firstReadFailAt;

    /// <summary>模式被外部改掉的告警只发一次, 避免每秒刷屏; 模式回到强冷或意图清除后复位。</summary>
    private bool _modeDiverged;

    /// <summary>连续几次回读不是强冷才认定"被外部改掉", 见 <see cref="DivergedSamplesToGiveUp"/>。</summary>
    private int _divergedSamples;

    /// <summary>本轮连续回读不是强冷的起点, 给次数阈值配一条时间下限。</summary>
    private DateTime _firstDivergedAt;

    /// <summary>关机交还只做一次。0=未做, 1=已做; 用 <see cref="Interlocked.Exchange"/> 抢。</summary>
    private int _shutdownRestoreStarted;

    /// <summary>
    /// 强冷期间连续读失败几次就交还自动。取 3: 默认 1 秒轮询下约 3 秒。
    /// 单次失败是正常的 (与 HWiNFO / LHM 抢 EC 互斥量、一次 16 位读抖动),
    /// 连续 3 次则说明 EC 通路已经坏了 —— 这时我们确认不了模式,
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

    /// <summary>
    /// 连续几次回读不是强冷, 才认定意图已被外部改掉并撤销。
    ///
    /// 取 2 而不是 1: 我们自己的写序是 PWM1 → PWM2 → Flag1 → Flag2, 中间任何一刻被
    /// 采样线程插进来都会读到"一个强冷一个自动"的 Mixed。那是我们自己的中间态,
    /// 不是外部改掉 —— 一次就撤意图, 用户会看到强冷莫名其妙地被放弃。
    ///
    /// 两次之外还要压一条时间下限: 轮询间隔可配到 250ms, 光数次数会在 0.5 秒内
    /// 就撤掉意图, 而 0.5 秒连一次完整的写序都未必跑完。理由与
    /// <see cref="ReadFailMinDuration"/> 相同 —— 判据里凡是"连续 N 次"的东西,
    /// 都必须配一条与轮询频率无关的时间地板。
    /// </summary>
    private static readonly TimeSpan DivergedMinDuration = TimeSpan.FromSeconds(2);

    /// <summary>连续几次回读不是强冷才认定"被外部改掉", 上限见 <see cref="DivergedMinDuration"/>。</summary>
    private const int DivergedSamplesToGiveUp = 2;

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

    /// <summary>勾选框的 CheckedChanged 出口。</summary>
    public void OnUserToggle()
    {
        if (_suppressEvent) return;

        SetMode(_row.Checked ? FanMode.StrongCool : FanMode.Auto, userInitiated: true);
    }

    /// <summary>
    /// 全局热键触发的切换。目标与点击那一行一致, 但有两处不同:
    ///
    /// 1) 依据是 <c>_strongCoolIntent</c> 而不是勾选框 —— 热键可能在窗口收进托盘、
    ///    甚至从未显示过的时候按下, 那时勾选框未必被 Observe 校准到位。
    /// 2) 不弹模态对话框 (userInitiated: false)。用户按热键时人很可能在别的程序里,
    ///    抢焦点弹窗比什么都不做更打扰; 结果由调用方用托盘气泡说明。
    /// </summary>
    /// <returns>是否达到目标状态, 以及一句可以拿去显示的话。</returns>
    public (bool Ok, string Message) ToggleFromHotKey()
    {
        if (_busy) return (false, "正在切换风扇模式, 请稍候再按。");

        // 设备没开时要单独说清楚。SetMode 在这种情况下一声不响地返回,
        // 若不在这里拦, 用户会收到"切换失败" —— 听起来像硬件坏了。
        FanReader? reader = _reader();
        if (reader is null || !reader.IsOpen)
            return (false, "EC 设备还没打开, 现在不能切换。");

        // 取反的依据是"用户看见的状态", 不是"上一拍的意图"。
        //
        // 两者在正常情况下一致, 但 Observe() 里有几条路径会让它们分叉:
        // EC 里的强冷标志被别的软件改回自动、且连续两次回读确认后, 界面已经同步成
        // 自动, _strongCoolIntent 也被撤销了 —— 这一支是同步的, 用哪个结果都一样。
        // 真正要防的是**勾选框还没被 Observe 校准**的那一小段: 用户在托盘里按热键时,
        // 勾选框是最后一次 UI 更新的结果, 也就是用户眼里的当前状态。按它取反,
        // 用户按一下看到的就一定与自己预期一致。
        FanMode target = _row.Checked ? FanMode.Auto : FanMode.StrongCool;

        FanModeResult result = SetMode(target, userInitiated: false);

        // 必须按 SetMode 的真实结果判成败。
        //
        // 旧实现用"意图是否等于目标"反推: target 是自动时, 失败分支会把意图清成 false,
        // 而 "target == StrongCool" 也是 false —— 两边相等, 于是失败被报成成功,
        // 托盘弹"已交还自动模式", 实际风扇还在手动 184 满转。这是最坏的一种谎。
        if (!result.Ok)
        {
            return (false, result.Message.Length > 0 ? result.Message : "风扇模式切换失败。");
        }

        return target == FanMode.StrongCool
            ? (true, "强冷已开启: 两个风扇满转。")
            : (true, "已交还自动模式, 由 EC 按温度曲线调速。");
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
        FanReader? reader = _reader();
        if (reader is null || !reader.IsOpen)
        {
            // 设备打不开时不能授予意图: 看门狗拿它当"现在确实在手动满转"的授权,
            // 而此刻我们连 EC 都读不到, 更没写过任何寄存器。授权一个不存在的手动
            // 满转, 只会在后续读失败时触发一条莫名其妙的"已交还自动模式"。
            _strongCoolIntent = false;
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
            // 意图必须跟着**真实状态**走: 设置说强冷、EC 也确认是强冷, 才算授权成立。
            // 设置说强冷而 EC 回读不出来 (Unknown) 会落到下面的 SetMode, 由它重写一遍
            // 并在成功后建立意图 —— 那条路径本来就有回读确认。
            _strongCoolIntent = want == FanMode.StrongCool && actual == FanModeState.StrongCool;
            UpdateUi(actual);
            return;
        }

        // 归一化失败不该弹框打断开机, 但必须留下可见痕迹。
        // SetMode 成功时会自己建立意图, 失败时保持 false。
        SetMode(want, userInitiated: false, reason: "已按设置恢复自动模式 (清理上次残留)。");
    }

    /// <summary>
    /// 每次成功读到样本后调一次: 用回读校准界面, 再检查模式漂移。
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
            if (_strongCoolIntent && sample.Mode != FanModeState.StrongCool)
            {
                // 要连续两次才认。一次 Mixed 可能只是我们自己的两路标志写入之间被读了
                // 一眼 (Fan1 已 0xDC / Fan2 还是 0x00), 那不是"外部改掉", 撤意图就等于
                // 因为一次抖动把用户的强冷悄悄放弃掉 —— 与"静默丢数据"是同一类错误。
                if (_divergedSamples == 0) _firstDivergedAt = DateTime.UtcNow;
                _divergedSamples++;

                // 除了次数还要压一条时间下限: 轮询间隔可配到 250ms, 光数次数会在
                // 0.5 秒内就撤掉用户的强冷, 而那时候一次完整写序都未必跑完。
                bool enough = _divergedSamples >= DivergedSamplesToGiveUp
                           && DateTime.UtcNow - _firstDivergedAt >= DivergedMinDuration;

                if (!_modeDiverged && enough)
                {
                    _modeDiverged = true;

                    // 意图跟着撤: 看门狗的唯一授权来源就是它。留着一条已经和现实不符的
                    // 意图, 等于让读失败保护为"实际已经不存在的强冷"持续累计、并占用状态栏。
                    _strongCoolIntent = false;

                    UiLog($"强冷意图被撤销: EC 连续 {_divergedSamples} 次回读为 {sample.Mode}"
                        + $" (持续 {(DateTime.UtcNow - _firstDivergedAt).TotalSeconds:F1}s)");
                    _setStatus(
                        "EC 里的强冷标志已被改回自动 (可能被其他软件重置), 界面已同步为真实状态。",
                        Theme.Warn);
                }
            }
            else
            {
                _divergedSamples = 0;

                if (sample.Mode == FanModeState.StrongCool && _modeDiverged)
                {
                    // 恢复后要把一次性的警告状态清掉, 否则下次再漂移就不会告警了,
                    // 而状态栏会一直停在红字上, 让人以为强冷已经坏了。
                    _modeDiverged = false;
                    _setStatus("强冷标志已恢复, 界面与 EC 一致。", Theme.Text2);
                }
            }
        }
    }

    /// <summary>
    /// 读失败时的看门狗: 连续 N 次读不到 EC 且持续足够久, 就主动交还自动。
    ///
    /// EC 读不到意味着转速与模式都无法确认 —— 此时保留"手动满转"其实无害 (满转只会更吵),
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
    /// (启动归一化、退出恢复 —— 静默进行, 失败只写状态栏)。
    /// </param>
    /// <returns>真实结果。调用方必须按它判定成败, <b>不能用意图反推</b>。</returns>
    public FanModeResult SetMode(FanMode mode, bool userInitiated, string? reason = null)
    {
        if (_busy)
            return FanModeResult.Failure("正在切换风扇模式, 请稍候再试。");

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
                return FanModeResult.Failure("EC 设备还没打开, 现在不能切换。");
            }

            FanModeResult result = reader.SetMode(mode);

            // 只在硬件接受之后才把模式记进设置。
            //
            // 旧实现无论成败都写 _settings.FanMode = mode, 于是"切换失败"也被记成
            // 用户想要强冷 —— 下次开机 NormalizeOnStartup 会再试一次这个从未成功过的动作,
            // 每一轮启动都要撞一次 K 硬件拒绝。设置里存的应该是"已经生效的状态",
            // 而不是"用户点过但没成功"的意图。
            bool remembered = result.Ok || (mode == FanMode.Auto && !result.Indeterminate);
            if (remembered)
            {
                _settings.FanMode = mode;
                if (!_settings.Save())
                    UiLog($"保存风扇模式失败: {AppSettings.LastError ?? "未知原因"}");
            }

            UpdateUi(result.State);

            if (result.Ok)
            {
                // 只在这里建立意图: 硬件确实接受了, 我们才主张"强冷在跑"。
                // 失败分支不建立, 否则看门狗会守着一个从未生效的模式。
                _strongCoolIntent = mode == FanMode.StrongCool;
                _readFailSamples = 0;
                _modeDiverged = false;
                _divergedSamples = 0;

                if (mode == FanMode.StrongCool)
                {
                    // 重新进入强冷, 让读失败看门狗重新有机会工作。
                    _emergencyRestored = false;
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
                _readFailSamples = 0;

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

            return result;
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
    /// 这是所有"收手"路径的唯一出口: 托盘菜单、程序退出、关机、读失败看门狗。
    /// 集中在一处是为了保证每条路径都做同样的事 —— 尤其都要把 _settings.FanMode
    /// 也改回 Auto, 否则下次启动的归一化会把风扇又拉满。
    /// </summary>
    public void RestoreAuto(string why)
    {
        // 意图先撤: 后面无论硬件写成功还是失败, 我们都已经不再主张"强冷中"。
        // 否则看门狗会拿一条已经放弃的意图继续累计, 反复触发交还。
        _strongCoolIntent = false;
        _readFailSamples = 0;
        _modeDiverged = false;
        _divergedSamples = 0;

        // 先改设置再动硬件: 万一 SetMode 抛异常, 至少下次启动不会再拉满。
        _settings.FanMode = FanMode.Auto;

        // 失败只记日志: 这条路径是退出/看门狗兜底, 此刻弹框会挡住关机或打断轮询。
        // 真正的风险是"设置里写着自动、磁盘上还是强冷", 所以至少要在 ui.log 留痕。
        if (!_settings.Save())
            UiLog("保存模式=自动 失败: " + (AppSettings.LastError ?? "未知原因"));

        _suppressEvent = true;
        try { _row.SetCheckedSilently(false); }
        finally { _suppressEvent = false; }

        FanReader? reader = _reader();
        if (reader is not { IsOpen: true })
        {
            // 设备没打开 = 一个字也没写进 EC。这时绝不能报"已交还自动模式":
            // 用户从托盘点的这一下, 看到的应该是"现在连不上设备", 而不是一句
            // 听上去已经完成的话 —— 否则风扇真在满转时, 他会以为问题解决了。
            UiLog("RestoreAuto (" + why + "): 设备未打开, 没有可交还的对象");
            _setStatus("EC 设备当前没有打开, 没能交还自动模式。", Theme.Warn);
            return;
        }

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
                if (why != ReadFailReason && why != "程序退出")
                    DarkDialog.Show(_owner, "风扇状态不确定", r.Message, DarkDialog.Tone.Error);
                return;
            }

            _setStatus("恢复自动风扇失败: " + r.Message, Theme.Err);
            return;
        }

        UpdateUi(FanModeState.Auto);

        UiLog("RestoreAuto (" + why + ") ok");

        if (why == "程序退出")
        {
            // 用户正要退出, 不需要再告诉他什么。
        }
        else if (why == ReadFailReason)
        {
            // 读失败可能每轮都发生, 绝不能弹模态框 —— 否则会变成每秒一个对话框,
            // 用户连点确定都来不及。只走状态栏 + 日志, 让人知道保护动作发生过。
            _emergencyRestored = true;
            _setStatus(
                $"连续 {ReadFailRestoreSamples} 次读不到 EC, 已把风扇交还自动控制 (无法确认风扇状态时不再保持手动满转)。",
                Theme.Warn);
        }
        else
        {
            _setStatus($"已交还自动模式 ({why})。", Theme.Text2);
        }
    }

    /// <summary>
    /// 托盘「退出」走这里: 与 <see cref="RestoreAutoOnShutdown"/> 共用同一个一次性标志。
    ///
    /// 以前 ExitApplication 直接调 RestoreAuto("程序退出"), 然后 Close() → Dispose()
    /// 又调 RestoreAutoOnShutdown。标志只在后者里, 于是退出时对 EC 写了两次自动 ——
    /// 写自动是幂等的, 但"进程退出时到底写了几次"这种事没法靠幂等糊过去:
    /// 两次读改之间只要 EC 被别的软件动过, 后一次就会基于中间态做判断。
    /// </summary>
    public void RestoreAutoOnExit()
    {
        if (!EnsureSingleRestore("RestoreAutoOnExit")) return;

        // 已经确认是自动就不用再写一次。原来这句判断在 MainForm.ExitApplication 里,
        // 收进这里是为了让"退出时会不会写 EC"只有一个决定点。
        // ReadModeState 内部自己带锁 (Acquire/Release), 读不到返回 Unknown,
        // 而 Unknown 不跳过 —— 无法确认干净就按没干净处理。
        FanReader? reader = _reader();
        if (reader is { IsOpen: true } && reader.ReadModeState() == FanModeState.Auto)
        {
            UiLog("RestoreAutoOnExit: EC 已经是自动模式, 不需要再写");
            return;
        }

        RestoreAuto("程序退出");
    }

    /// <summary>
    /// 退出/关机这两条路共用的"只交还一次"守卫。
    ///
    /// 两个方法原本各写了一遍 <see cref="Interlocked.Exchange(ref int, int)"/> 判重,
    /// 逻辑一样但措辞不同。合并成一个是因为这不再是"两处相似代码"的问题:
    /// 这个标志同时挡住 ExitApplication → Dispose → RestoreAutoOnShutdown 的重复写入,
    /// 任何一处改错都会让退出时对 EC 多写或少写一次, 而这两种错都不会有人报上来。
    /// 判重与日志只在这里, 调用方各自保留"是否需要写"的判据。
    /// </summary>
    /// <returns>true 表示这次该继续; false 表示已经有人做过了, 调用方直接返回。</returns>
    private bool EnsureSingleRestore(string caller)
    {
        if (Interlocked.Exchange(ref _shutdownRestoreStarted, 1) != 0)
        {
            UiLog(caller + ": 已经执行过, 本次跳过");
            return false;
        }
        return true;
    }

    /// <summary>
    /// 关机/注销/退出前把 EC 收回自动。
    ///
    /// 判据是"确认处于自动才跳过", 而不是"只有强冷才交还":
    /// Mixed (两路标志不一致, 例如 Fan1=0xDC / Fan2=0x00) 恰恰是最该收手的状态 ——
    /// 一个风扇还在按手动 184 满转, 而另一个已经交还 EC, 这种分裂残留如果留在
    /// 关机时刻, 下次开机就是"一个满转一个不转"的怪状态。
    ///
    /// Unknown 同样要写一次: 读不出来等于**无法确认**它是干净的, 而写 Auto 是幂等的。
    /// 关机这一刻赌错的代价是下次开机一个风扇满转, 多写一次的成本是几毫秒。
    /// </summary>
    public void RestoreAutoOnShutdown()
    {
        // 关机路径会被调两次: SessionEnding (SystemEvents 的回调线程) 一次,
        // Dispose (UI 线程) 又一次。两次都去读改 EC, 除了多写一遍, 还可能让
        // 后一次读到前一次写了一半的中间态而误报"状态不确定"。
        if (!EnsureSingleRestore("RestoreAutoOnShutdown")) return;

        FanReader? reader = _reader();
        if (reader is not { IsOpen: true }) return;

        FanModeState before = reader.ReadModeState();

        // 只有"确认是自动"才跳过。Unknown 曾经也被跳过, 理由是"读不出来就没得做" ——
        // 但写一次 Auto 是幂等的, 而读不出来恰恰说明**无法确认**安全: 关闭窗口那一刻
        // 若 PWM 仍停在 184, 重启后 EC 不清 RAM 就还是一个风扇满转。
        // 宁可多写一次已经被自动覆盖的寄存器, 也不要赌它是干净的。
        if (before == FanModeState.Auto) return;

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
                  + "若下次开机听到风扇狂转或没有转速, 请启动 Zephyr 手动切回自动。"
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

    private static void UiLog(string message) => global::Zephyr.Program.UiLog(message);
}
