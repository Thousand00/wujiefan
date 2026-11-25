namespace Zephyr.Core.Ec;

/// <summary>单个风扇的一次读数。</summary>
public sealed class FanReading
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public int RawCount { get; init; }

    /// <summary>换算后的转速。无法换算时为 0。</summary>
    public int Rpm { get; init; }

    /// <summary>读数异常时的说明, 正常为空。</summary>
    public string Note { get; init; } = "";

    /// <summary>PWM 硬件输出占空比 0..184。</summary>
    public int PwmOut { get; init; }

    /// <summary>
    /// 占空比百分比 0..100。
    ///
    /// 显示层也要夹取: 这里读的是 EC 的**硬件输出**寄存器, 不是本程序写进去的值。
    /// 若 EC 或其它软件把周期寄存器配到了别的值, PwmOut 可能大于 184,
    /// 不夹取就会显示成 120% 这种数字 —— 用户看到只会更困惑, 显示 100% 才符合
    /// "占空比不可能超过满量程"的直觉。
    /// </summary>
    public double PwmPercent
    {
        get
        {
            int clamped = Math.Clamp(PwmOut, EcRegisters.PwmMin, EcRegisters.PwmMax);
            return clamped * 100.0 / EcRegisters.PwmMax;
        }
    }

    /// <summary>读数可信 (没有异常说明)。</summary>
    public bool IsValid => Note.Length == 0;

    /// <summary>
    /// 计数器为 0 = 风扇确实不动了 (停转或还没起转), 这与"读不到信号"是两件事。
    /// 界面和 RTSS 都该显示 0, 而不是留空 —— 留空会把"风扇停了"显示成"读取失败"。
    /// </summary>
    public bool IsStopped => RawCount == 0;
}

/// <summary>一次完整采样的结果。</summary>
public sealed class FanSample
{
    public DateTime Timestamp { get; init; }
    public IReadOnlyList<FanReading> Fans { get; init; } = Array.Empty<FanReading>();
    public string? Error { get; init; }

    /// <summary>
    /// 本次失败是"别人临时占着 EC", 不是"设备坏了"。
    ///
    /// 这两件事在界面上都表现为读不到数据, 但对重连决策的意义完全相反:
    /// 争用下一拍就会自己好, 重连只会白拆一次设备 (还要在 UI 线程上重新加载
    /// 驱动、探测端口), 而重连成功又会把退避计数清零 —— 于是"争用 → 重连 →
    /// 清零 → 再次争用"变成一个自转的循环。所以上层必须能分辨它们:
    /// 瞬时失败照常显示"暂时读不到", 但**不**计入连续错误次数。
    /// </summary>
    public bool Transient { get; init; }

    /// <summary>采样时 EC 里实际的风扇模式。界面据此显示真实状态, 而不是程序以为的状态。</summary>
    public FanModeState Mode { get; init; } = FanModeState.Unknown;

    public bool HasData => Error is null && Fans.Count > 0;

    /// <summary>把转速计数器换算成 RPM。规则与已验证实现一致。</summary>
    public static (int Rpm, string Note) ConvertRpm(int count)
    {
        if (count <= 0)
            return (0, "");   // 停转: RPM 就是 0, 不是异常
        if (count >= 0x4000)
            return (0, "计数值越界(无有效信号)");
        if (count < 0x80)
            // 阈值按 TachConstant=2156250 算出: count=0x80(128) 时是 16845 RPM,
            // count 再小一档就超过它。早先写的是 16700, 那是照着一台机器的实测值
            // 抄的, 和这里真正用的常数对不上 —— 文案写错方向的话, 用户拿它去对照
            // 别的软件读数时会以为某一方坏了。
            return (9999, "超量程(>16845RPM)");
        return (EcRegisters.TachConstant / count, "");
    }
}
