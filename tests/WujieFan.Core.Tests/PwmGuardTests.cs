using WujieFan.Core.Ec;
using Xunit;

namespace WujieFan.Core.Tests;

/// <summary>
/// PWM 越界检查。这是全程序唯一"写错一步就让风扇停转"的地方, 而停转是静默的 ——
/// 没有报错、没有告警, 只有温度慢慢爬上去。所以每个边界值都要有断言。
///
/// 通过 <see cref="IEcTransport.WritePwm"/> 的默认接口实现来测, 而不是直接测内部的
/// PwmGuard: 默认实现是生产路径 (<see cref="EcAccess.WritePwm"/>) 与测试传输共用的同一份
/// 判定, 测它就等于测真实行为, 不需要测试专用后门。
/// </summary>
public class PwmGuardTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(-184)]
    [InlineData(int.MinValue)]
    [InlineData(185)]
    [InlineData(255)]
    [InlineData(int.MaxValue)]
    public void WritePwm_越界_抛EcException且不写入(int value)
    {
        IEcTransport ec = new FakeEc();
        var fake = (FakeEc)ec;

        var ex = Assert.Throws<EcException>(() => ec.WritePwm(EcRegisters.ManualPwm1, value));

        Assert.Empty(fake.Writes);   // 拒绝必须是"一个字节都没写", 不是写完了再报错
        Assert.Contains(EcRegisters.PwmMax.ToString(), ex.Message);
    }

    [Fact]
    public void WritePwm_越界文案说明会停转且带地址()
    {
        IEcTransport ec = new FakeEc();

        var ex = Assert.Throws<EcException>(() => ec.WritePwm(0x021E, 185));

        // 运维排障时这条文案就是唯一线索: 必须能定位到具体寄存器, 并说清后果。
        Assert.Contains("0x021E", ex.Message);
        Assert.Contains("会让风扇停转", ex.Message);
        Assert.Contains("184", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(92)]
    [InlineData(183)]
    [InlineData(184)]
    public void WritePwm_合法区间_原样写入(int value)
    {
        IEcTransport ec = new FakeEc();
        var fake = (FakeEc)ec;

        ec.WritePwm(EcRegisters.ManualPwm1, value);

        var (addr, written) = Assert.Single(fake.Writes);
        Assert.Equal(EcRegisters.ManualPwm1, addr);
        Assert.Equal(value, written);
    }

    [Fact]
    public void WritePwm_零是合法值_不拒绝()
    {
        // 0 表示 EC 允许风扇完全停 —— 拒绝它会让"合法停转"变成异常,
        // 而越界检查的本意只是挡住 >184。
        IEcTransport ec = new FakeEc();
        ec.WritePwm(EcRegisters.ManualPwm2, EcRegisters.PwmMin);

        Assert.Equal(0, ((FakeEc)ec).Reg(EcRegisters.ManualPwm2));
    }

    [Fact]
    public void WritePwm_满量程恰好是184不是255()
    {
        // 这个机型的 PWM 周期寄存器满量程是 184。误以为是 255 会写出停转值。
        Assert.Equal(184, EcRegisters.PwmMax);
        IEcTransport ec = new FakeEc();
        ec.WritePwm(EcRegisters.ManualPwm1, EcRegisters.PwmMax);
        Assert.Equal(184, ((FakeEc)ec).Reg(EcRegisters.ManualPwm1));
    }

    [Fact]
    public void WritePwm_两个风扇寄存器互不影响()
    {
        IEcTransport ec = new FakeEc();
        ec.WritePwm(EcRegisters.ManualPwm1, 184);
        ec.WritePwm(EcRegisters.ManualPwm2, 100);

        var fake = (FakeEc)ec;
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm1));
        Assert.Equal(100, fake.Reg(EcRegisters.ManualPwm2));
    }
}
