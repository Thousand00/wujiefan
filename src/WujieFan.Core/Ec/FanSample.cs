namespace WujieFan.Core.Ec;

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

    /// <summary>占空比百分比 0..100。</summary>
    public double PwmPercent => PwmOut * 100.0 / EcRegisters.PwmMax;

    public bool IsValid => Note.Length == 0;
}

/// <summary>一次完整采样的结果。</summary>
public sealed class FanSample
{
    public DateTime Timestamp { get; init; }
    public int? Temperature { get; init; }
    public IReadOnlyList<FanReading> Fans { get; init; } = Array.Empty<FanReading>();
    public string? Error { get; init; }

    public bool HasData => Error is null && Fans.Count > 0;

    /// <summary>把转速计数器换算成 RPM。规则与已验证实现一致。</summary>
    public static (int Rpm, string Note) ConvertRpm(int count)
    {
        if (count <= 0)
            return (0, "无信号或停转");
        if (count >= 0x4000)
            return (0, "计数值越界(无有效信号)");
        if (count < 0x80)
            return (9999, "超量程(>16700RPM)");
        return (EcRegisters.TachConstant / count, "");
    }
}
