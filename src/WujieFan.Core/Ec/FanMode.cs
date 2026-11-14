namespace WujieFan.Core.Ec;

/// <summary>
/// 风扇的两档模式。
///
/// 刻意只有两档: 自动 (完全交还 EC 固件) 与强冷 (手动 PWM 拉满)。
/// 不做"降速"档是安全决定 —— 让风扇慢于 EC 固件的判断, 是唯一可能真正把机器烧热的操作;
/// 而"只升不降"下最坏的结果只是吵。
/// </summary>
public enum FanMode
{
    /// <summary>EC 固件自己按温度曲线调转速 (模式标志 0x00)。</summary>
    Auto = 0,

    /// <summary>手动 PWM 拉满 184 (100% 占空比, 约 5700 RPM), 模式标志 0xDC。</summary>
    StrongCool = 1,
}

/// <summary>两档模式的实际状态。界面要区分"我以为的模式"和"EC 里真的是这个模式"。</summary>
public enum FanModeState
{
    /// <summary>两个风扇的标志都是 0x00。</summary>
    Auto,

    /// <summary>两个风扇的标志都是 0xDC。</summary>
    StrongCool,

    /// <summary>两个风扇标志不一致, 或落在本程序不认识的取值上 (0xA5/0xCC 等)。</summary>
    Mixed,

    /// <summary>还没读过。</summary>
    Unknown,
}

/// <summary>一次模式切换的结果。</summary>
public sealed class FanModeResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public FanModeState State { get; init; } = FanModeState.Unknown;

    public static FanModeResult Success(string message, FanModeState state) =>
        new() { Ok = true, Message = message, State = state };

    public static FanModeResult Failure(string message, FanModeState state = FanModeState.Unknown) =>
        new() { Ok = false, Message = message, State = state };
}
