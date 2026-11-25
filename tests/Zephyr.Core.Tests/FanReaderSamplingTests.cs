using Zephyr.Core.Ec;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 用假 EC 把 <see cref="FanReader.Read"/> 的采样路径跑一遍。这条路径平时只在实机上
/// 被"看一眼界面对不对"验证过, 而它包含三处会安静出错的判断:
///   温度越界归一化、读数不稳定丢弃、两个标志不一致判 Mixed。
/// </summary>
public class FanReaderSamplingTests
{
    private static FakeEc HealthyEc() => new FakeEc()
        .With(EcRegisters.CpuTemperature, 55)
        .With(EcRegisters.PwmOut1, 92)
        .With(EcRegisters.PwmOut2, 46)
        .With(EcRegisters.ModeFlag1, EcRegisters.ModeAuto)
        .With(EcRegisters.ModeFlag2, EcRegisters.ModeAuto);

    [Fact]
    public void Read_正常采样_两个风扇都有值()
    {
        var fake = HealthyEc();
        fake.TachCount = 4000;
        using var reader = new FanReader(fake);

        FanSample s = reader.Read();

        Assert.Null(s.Error);
        Assert.True(s.HasData);
        Assert.Equal(2, s.Fans.Count);
        Assert.Equal("Fan1 (CPU)", s.Fans[0].Label);
        Assert.Equal("Fan2 (GPU)", s.Fans[1].Label);
        Assert.Equal(539, s.Fans[0].Rpm);
        Assert.Equal(FanModeState.Auto, s.Mode);
    }

    [Fact]
    public void Read_不再读CPU温度寄存器()
    {
        // 温度功能已砍: 每轮少一次 12 次驱动往返的读, 也少一次与 HWiNFO/LHM
        // 争 EC 总线时序的机会。用"读它就抛"的传输层证明真的没读。
        var fake = HealthyEc();
        fake.ForbidReadOf(EcRegisters.CpuTemperature);
        using var reader = new FanReader(fake);

        FanSample s = reader.Read();

        Assert.Null(s.Error);
        Assert.Equal(2, s.Fans.Count);
    }

    [Fact]
    public void Read_关掉PwmOut时少读两个寄存器且占空比为零()
    {
        // 生产路径用这个: 界面与 RTSS 都不显示占空比, 那两次读纯属白跑。
        var fake = HealthyEc();
        fake.ForbidReadOf(EcRegisters.PwmOut1);
        fake.ForbidReadOf(EcRegisters.PwmOut2);
        using var reader = new FanReader(fake);

        FanSample s = reader.Read(readPwmOut: false);

        Assert.Null(s.Error);
        Assert.Equal(2, s.Fans.Count);
        // 0 表示"本程序不知道占空比", 不是"占空比是 0%"。
        Assert.Equal(0.0, s.Fans[0].PwmPercent);
        Assert.Equal(0.0, s.Fans[1].PwmPercent);
    }

    [Fact]
    public void Read_默认仍然读PwmOut_测试与实验工具不受影响()
    {
        var fake = HealthyEc().With(EcRegisters.PwmOut1, 92);
        using var reader = new FanReader(fake);

        Assert.Equal(50.0, reader.Read().Fans[0].PwmPercent);
    }

    [Fact]
    public void Read_读数不稳定_丢弃本次计数并给出说明()
    {
        var fake = HealthyEc();
        fake.Stable = false;
        using var reader = new FanReader(fake);

        FanSample s = reader.Read();

        FanReading fan = s.Fans[0];
        Assert.False(fan.IsValid);
        Assert.Contains("不稳定", fan.Note);
    }

    [Fact]
    public void Read_PWM输出越界_百分比仍然夹在100以内()
    {
        var fake = HealthyEc().With(EcRegisters.PwmOut1, 250);
        using var reader = new FanReader(fake);

        FanSample s = reader.Read();

        // 读的是 EC 硬件输出寄存器, 别的软件可能把它配成别的周期值。
        Assert.Equal(100.0, s.Fans[0].PwmPercent);
    }

    [Theory]
    [InlineData(EcRegisters.ModeAuto, EcRegisters.ModeAuto, FanModeState.Auto)]
    [InlineData(EcRegisters.ModeManualPwm, EcRegisters.ModeManualPwm, FanModeState.StrongCool)]
    [InlineData(EcRegisters.ModeManualPwm, EcRegisters.ModeAuto, FanModeState.Mixed)]
    [InlineData(EcRegisters.ModeCustomCurve, EcRegisters.ModeCustomCurve, FanModeState.Mixed)]
    [InlineData(EcRegisters.ModeCustomTemp, EcRegisters.ModeCustomTemp, FanModeState.Mixed)]
    [InlineData(EcRegisters.ModeAuto, 0x11, FanModeState.Mixed)]
    public void Read_模式标志组合_如实报告(byte flag1, byte flag2, FanModeState expected)
    {
        var fake = HealthyEc()
            .With(EcRegisters.ModeFlag1, flag1)
            .With(EcRegisters.ModeFlag2, flag2);
        using var reader = new FanReader(fake);

        Assert.Equal(expected, reader.Read().Mode);
    }

    [Fact]
    public void Read_传输层抛异常_返回带Error的样本而不是崩溃()
    {
        // 界面每秒调一次 Read, 一次 I/O 抖动不该让程序退出。
        var throwing = new ThrowingEc();
        using var reader = new FanReader(throwing);

        FanSample s = reader.Read();

        Assert.NotNull(s.Error);
        Assert.False(s.HasData);
    }

    [Fact]
    public void ReadModeState_单独调用也能读到当前状态()
    {
        var fake = HealthyEc()
            .With(EcRegisters.ModeFlag1, EcRegisters.ModeManualPwm)
            .With(EcRegisters.ModeFlag2, EcRegisters.ModeManualPwm);
        using var reader = new FanReader(fake);

        Assert.Equal(FanModeState.StrongCool, reader.ReadModeState());
    }

    [Fact]
    public void ReadModeState_读失败返回Unknown而不抛()
    {
        using var reader = new FanReader(new ThrowingEc());
        Assert.Equal(FanModeState.Unknown, reader.ReadModeState());
    }

    [Fact]
    public void 注入传输即视为已打开()
    {
        var fake = HealthyEc();
        using var reader = new FanReader(fake);

        // 注入路径不走 PawnIo / 机型校验 / 互斥量, 因此不需要管理员权限。
        Assert.True(reader.IsOpen);
        Assert.Equal(0, reader.PortBase);   // 真实硬件路径才有的信息
        Assert.Equal(-1, reader.Slot);
        Assert.False(reader.HasMutex);
    }

    [Fact]
    public void 未注入也未打开_Read返回设备未打开()
    {
        using var reader = new FanReader(Array.Empty<byte>());

        Assert.False(reader.IsOpen);
        FanSample s = reader.Read();
        Assert.Equal("设备未打开", s.Error);
    }

    [Fact]
    public void Dispose后_再读返回设备未打开()
    {
        var fake = HealthyEc();
        var reader = new FanReader(fake);
        reader.Dispose();

        Assert.False(reader.IsOpen);
        Assert.Equal("设备未打开", reader.Read().Error);
    }

    private sealed class ThrowingEc : IEcTransport
    {
        public int Read(ushort address) => throw new EcException("模拟 EC 读取失败");
        public void Write(ushort address, byte value) => throw new EcException("模拟 EC 写入失败");
        public int Read16Stable(ushort lo, ushort hi, out bool stable) => throw new EcException("模拟 EC 读取失败");
    }
}
