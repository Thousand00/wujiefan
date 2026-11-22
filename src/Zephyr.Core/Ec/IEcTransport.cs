namespace Zephyr.Core.Ec;

/// <summary>
/// EC 寄存器访问的最小面。真实实现是 <see cref="EcAccess"/> (经 PawnIO 走 I2EC 时序),
/// 测试实现是内存里的假 EC。
///
/// 抽这一层的目的只有一个: 让 <see cref="FanReader"/> 的**写序列**能在没有 EC 硬件的
/// 机器上被断言。写序列是本程序唯一会改变风扇行为的东西, 而它的正确性依赖
/// "先写哪个寄存器、失败后怎么回滚、何时回读"这些顺序细节 —— 这些细节靠上机肉眼看
/// 风扇转不转是验不出来的, 只能靠假的传输层记录调用顺序。
///
/// 签名刻意与 <see cref="EcAccess"/> 完全一致, 调用点不需要任何改写。
/// </summary>
public interface IEcTransport
{
    /// <summary>读一个字节。真实实现会做 I2EC 选寄存器 -> 提交 -> 读数据三步。</summary>
    int Read(ushort address);

    /// <summary>
    /// 写一个字节。**不做范围校验** —— 它是通用通道, 校验属于上层的职责
    /// (PWM 请走 <see cref="WritePwm"/>)。
    /// </summary>
    void Write(ushort address, byte value);

    /// <summary>
    /// 读 16 位计数器。按 高-低-高 顺序读, 两次高字节一致才认为可信。
    /// <paramref name="stable"/> 为 false 时返回的数值不可信 (撕裂值可能恰好落在正常区间)。
    /// </summary>
    int Read16Stable(ushort loAddress, ushort hiAddress, out bool stable);

    /// <summary>
    /// 写 PWM 占空比。这是 PWM 的唯一合法入口, 有效区间 [0, 184]。
    ///
    /// 提供默认实现而不是要求每个传输层自己写: 越界检查是**安全属性**,
    /// 不该因为换了个传输实现就消失。假 EC 因此也自动获得同一套校验,
    /// 测试里"越界值被拒绝"与生产行为天然一致。
    /// </summary>
    void WritePwm(ushort address, int value)
    {
        PwmGuard.Validate(address, value);
        Write(address, (byte)value);
    }
}

/// <summary>
/// PWM 占空比的越界检查。生产路径 (<see cref="EcAccess.WritePwm"/>) 与接口默认实现
/// 共用同一份判定与同一条错误文案, 免得两边漂移。
/// </summary>
internal static class PwmGuard
{
    /// <summary>
    /// 越界抛 <see cref="EcException"/>, **不做静默夹取**: 夹取会把调用方的计算错误藏起来,
    /// 让"程序以为写的是 184、实际写进去的是别的值"这种 bug 活到用户机器上。
    /// 超过 184 会让风扇直接停转, 而且是静默停转 —— 没有告警, 只有温度慢慢爬上去。
    /// </summary>
    internal static void Validate(ushort address, int value)
    {
        if (value < EcRegisters.PwmMin || value > EcRegisters.PwmMax)
        {
            throw new EcException(
                $"拒绝写入 PWM 值 {value}: 有效区间是 {EcRegisters.PwmMin}..{EcRegisters.PwmMax} " +
                $"(地址 0x{address:X4})。超过 {EcRegisters.PwmMax} 会让风扇停转。");
        }
    }
}
