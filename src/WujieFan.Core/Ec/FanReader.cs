using WujieFan.Core.Hardware;
using WujieFan.Core.Native;

namespace WujieFan.Core.Ec;

/// <summary>
/// 读取风扇转速与 CPU 温度, 并可切换"自动 / 强冷"两档模式。
///
/// 生命周期: 构造 -> Open() -> 循环 Read() -> Dispose()。
/// 打开后一直复用同一个 PawnIO 句柄与端口, 不反复探测, 避免 I/O 抖动。
///
/// 写入只发生在显式调用 SetMode() 时, 且只写两个风扇的手动 PWM 值与模式标志,
/// 绝不碰温度阈值表、充电限制或任何其它寄存器。
/// </summary>
public sealed class FanReader : IDisposable
{
    private readonly byte[] _lpcIoBlob;
    private PawnIo? _io;
    private EcAccess? _ec;
    private EcMutex? _mutex;
    private bool _disposed;

    /// <summary>
    /// 等待 EC 互斥量的超时。
    /// 这把锁是"君子协定": 拿到就规规矩矩地读; 拿不到也不放弃采样 ——
    /// 界面每停一秒不刷新, 用户都会以为程序挂了, 而只读采样本身有防撕裂保护兜底。
    /// </summary>
    private const int MutexTimeoutMs = 300;

    /// <summary>
    /// 写入时等锁的时间要比读取长一些。
    /// 读失败大不了这一秒不刷新; 写失败会让界面以为切换成功、EC 实际没变,
    /// 那种"界面显示强冷但风扇没动"的不一致比多等一会儿糟得多。
    /// </summary>
    private const int WriteMutexTimeoutMs = 1500;

    public bool IsOpen => _ec is not null;
    public string? PawnIoVersion { get; private set; }

    /// <summary>是否成功创建过互斥量。为 false 时仍可只读采样, 但可能与其它监控软件互相干扰。</summary>
    public bool HasMutex => _mutex is not null;

    /// <summary>打开前读取的机器身份 (只读 SMBIOS, 不碰硬件)。</summary>
    public HardwareIdentity? Identity { get; private set; }

    /// <summary>硬件校验结果。未通过时 Open() 会抛 HardwareNotSupportedException。</summary>
    public SupportVerdict? Verdict { get; private set; }
    public ushort PortBase => _ec?.PortBase ?? 0;
    public int Slot => _ec?.Slot ?? -1;

    /// <param name="lpcIoBlob">LpcIO.bin 的字节内容 (LGPL 2.1, 随程序分发)。</param>
    public FanReader(byte[] lpcIoBlob) => _lpcIoBlob = lpcIoBlob;

    /// <summary>
    /// 打开设备并探测端口。失败会抛出带可读说明的异常。
    ///
    /// 打开顺序是刻意安排的: 先做机型/固件校验, 通过了才去碰 PawnIO 和 EC。
    /// 寄存器地址是特定固件逆向出来的, 机型不符时读出来的数字看着合理但完全错误,
    /// 所以宁可直接拒绝, 也不"尽力而为"。
    /// </summary>
    /// <param name="force">跳过机型校验。仅供用户明确知情后使用。</param>
    public void Open(bool force = false)
    {
        if (_ec is not null) return;

        if (!force)
        {
            Identity = HardwareIdentity.Read();
            Verdict = SupportedHardware.Check(Identity);
            if (!Verdict.IsSupported)
                throw new HardwareNotSupportedException(Verdict.Title, Verdict.Detail);
        }

        _io = new PawnIo();
        PawnIoVersion = _io.Version;

        _mutex = new EcMutex();

        try
        {
            _ec = new EcAccess(_io);
            _ec.LoadModule(_lpcIoBlob);

            // 端口探测要反复读寄存器, 这段必须持锁。
            _mutex.Acquire(MutexTimeoutMs);
            try { _ec.DetectPort(); }
            finally { _mutex.Release(); }
        }
        catch
        {
            _ec = null;
            _io.Dispose();
            _io = null;
            _mutex?.Dispose();
            _mutex = null;
            throw;
        }
    }

    /// <summary>采一次样。读失败时返回带 Error 的对象, 不抛异常 (供界面持续刷新)。</summary>
    public FanSample Read()
    {
        if (_ec is null)
            return new FanSample { Timestamp = DateTime.Now, Error = "设备未打开" };

        try
        {
            // 一把锁只覆盖一次完整采样: 拿不到就等下一轮。
            // 长期持锁会让 HWiNFO / LHM 这类同样守约定的软件彻底读不到 EC。
            if (_mutex is not null) _mutex.Acquire(MutexTimeoutMs);

            int? temp;
            var fans = new List<FanReading>(2);
            FanModeState mode;
            try
            {
                temp = _ec.Read(EcRegisters.CpuTemperature);
                if (temp is < 1 or > 100) temp = null;

                fans.Add(ReadFan(1, "Fan1 (CPU)", EcRegisters.Tach1, EcRegisters.PwmOut1));
                fans.Add(ReadFan(2, "Fan2 (GPU)", EcRegisters.Tach2, EcRegisters.PwmOut2));

                mode = ReadModeStateLocked();
            }
            finally
            {
                _mutex?.Release();
            }

            return new FanSample
            {
                Timestamp = DateTime.Now,
                Temperature = temp,
                Fans = fans,
                Mode = mode,
            };
        }
        catch (Exception ex)
        {
            return new FanSample { Timestamp = DateTime.Now, Error = ex.Message };
        }
    }

    /// <summary>只读当前模式 (单独调用时自己拿锁)。</summary>
    public FanModeState ReadModeState()
    {
        if (_ec is null) return FanModeState.Unknown;

        try
        {
            if (_mutex is not null) _mutex.Acquire(MutexTimeoutMs);
            try { return ReadModeStateLocked(); }
            finally { _mutex?.Release(); }
        }
        catch
        {
            return FanModeState.Unknown;
        }
    }

    /// <summary>调用方必须已持有互斥量。</summary>
    private FanModeState ReadModeStateLocked()
    {
        if (_ec is null) return FanModeState.Unknown;

        int f1 = _ec.Read(EcRegisters.ModeFlag1);
        int f2 = _ec.Read(EcRegisters.ModeFlag2);

        if (f1 == 0x00 && f2 == 0x00) return FanModeState.Auto;
        if (f1 == 0xDC && f2 == 0xDC) return FanModeState.StrongCool;

        // 0xA5 / 0xCC 以及两个风扇不一致的情形都归到 Mixed:
        // 这些状态下 EC 的行为不由本程序决定, 界面只能如实报告, 不能假装是自动。
        return FanModeState.Mixed;
    }

    /// <summary>
    /// 切到指定模式。这是本程序唯一会写 EC 的地方。
    ///
    /// 顺序是硬要求:
    ///   进强冷 -> 先写手动 PWM 值, 再写模式标志。反过来的话, 标志一旦变成 0xDC,
    ///             EC 会立刻采信**上一次残留**的手动 PWM 值, 中间那一瞬转速是错的。
    ///   回自动 -> 只写模式标志 0x00, 不碰手动 PWM 值。EC 在自动模式下根本不读它,
    ///            留着旧值无害, 而多写一次就多一次出错机会。
    /// </summary>
    public FanModeResult SetMode(FanMode mode)
    {
        if (_ec is null)
            return FanModeResult.Failure("设备未打开, 无法切换风扇模式。");

        bool locked = false;
        try
        {
            if (_mutex is not null) locked = _mutex.Acquire(WriteMutexTimeoutMs);

            // 拿不到锁就不写。只读采样拿不到锁可以照旧进行, 写入不行:
            // 别的软件的 I2EC 时序正插在中间时写进去, 可能落到错误地址。
            if (_mutex is not null && !locked)
                return FanModeResult.Failure("EC 正被其它监控软件占用, 请稍后重试。");

            if (mode == FanMode.StrongCool)
            {
                _ec.Write(EcRegisters.ManualPwm1, (byte)EcRegisters.PwmMax);
                _ec.Write(EcRegisters.ManualPwm2, (byte)EcRegisters.PwmMax);
                _ec.Write(EcRegisters.ModeFlag1, 0xDC);
                _ec.Write(EcRegisters.ModeFlag2, 0xDC);
            }
            else
            {
                _ec.Write(EcRegisters.ModeFlag1, 0x00);
                _ec.Write(EcRegisters.ModeFlag2, 0x00);
            }

            // 回读确认。写完不看一眼就等于赌 EC 收下了 ——
            // 之前踩过"界面说切换成功、EC 里其实还是旧值"的坑, 这里必须核对。
            FanModeState actual = ReadModeStateLocked();

            return mode switch
            {
                FanMode.StrongCool when actual == FanModeState.StrongCool =>
                    FanModeResult.Success("已切到强冷模式 (两个风扇满转)。", actual),

                FanMode.StrongCool =>
                    FanModeResult.Failure($"写入后回读的模式是 {Describe(actual)}, EC 没有接受强冷。", actual),

                _ when actual == FanModeState.Auto =>
                    FanModeResult.Success("已交还自动模式, 由 EC 按温度曲线调速。", actual),

                _ =>
                    FanModeResult.Failure($"写入后回读的模式是 {Describe(actual)}, EC 没有回到自动。", actual),
            };
        }
        catch (Exception ex)
        {
            return FanModeResult.Failure("切换风扇模式失败: " + ex.Message);
        }
        finally
        {
            if (locked) _mutex?.Release();
        }
    }

    /// <summary>模式的中文说明, 界面和日志共用。</summary>
    public static string Describe(FanModeState state) => state switch
    {
        FanModeState.Auto => "自动",
        FanModeState.StrongCool => "强冷",
        FanModeState.Mixed => "混合/未知标志",
        _ => "未知",
    };

    private FanReading ReadFan(int index, string label, (ushort Lo, ushort Hi) tach, ushort pwmOutAddr)
    {
        int count = _ec!.Read16Stable(tach.Lo, tach.Hi, out bool stable);
        var (rpm, note) = FanSample.ConvertRpm(count);

        // 防撕裂读失败: 这次数值可能是高低字节来自不同时刻拼出来的, 丢弃。
        if (!stable) note = "读取不稳定(已丢弃本次计数)";

        return new FanReading
        {
            Index = index,
            Label = label,
            RawCount = count,
            Rpm = rpm,
            Note = note,
            PwmOut = _ec.Read(pwmOutAddr),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ec?.Dispose();
        _ec = null;
        _io?.Dispose();
        _io = null;
        _mutex?.Dispose();
        _mutex = null;
    }
}

/// <summary>
/// 机器型号或 EC 固件版本与本程序验证过的不一致。
/// 捕获这个异常后应当向用户完整展示 Title 与 Detail, 而不是简单报"打开失败"。
/// </summary>
public sealed class HardwareNotSupportedException : Exception
{
    public string Reason { get; }
    public string Guidance { get; }

    public HardwareNotSupportedException(string reason, string guidance)
        : base($"{reason}\n{guidance}")
    {
        Reason = reason;
        Guidance = guidance;
    }
}
