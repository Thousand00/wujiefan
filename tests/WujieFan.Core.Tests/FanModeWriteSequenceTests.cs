using WujieFan.Core.Ec;
using Xunit;

namespace WujieFan.Core.Tests;

/// <summary>
/// SetMode 的写入序列。这是全程序唯一会改变风扇行为的代码, 而它的正确性完全
/// 依赖"先写哪个寄存器、失败后怎么回滚、什么时候回读"这些顺序细节。
///
/// 顺序错了不会崩、不会报错, 只会在某一瞬间让 EC 采信残留值 —— 上机盯着风扇
/// 根本看不出来。所以这一组测试是 t13 的核心: 用假 EC 把每次写入的地址与取值
/// 逐条钉死。
///
/// 注: FakeEc 必须声明成 IEcTransport 才能用 WritePwm (它是默认接口实现),
/// 这是 t12 留下的接口设计, 不是笔误。
/// </summary>
public class FanModeWriteSequenceTests
{
    /// <summary>一台"刚开机、EC 自己控制着"的机器。</summary>
    private static FakeEc IdleEc() => new FakeEc()
        .With(EcRegisters.ManualPwm1, 120)
        .With(EcRegisters.ManualPwm2, 118)
        .With(EcRegisters.ModeFlag1, EcRegisters.ModeAuto)
        .With(EcRegisters.ModeFlag2, EcRegisters.ModeAuto);

    [Fact]
    public void 模式标志写成功但PWM被吞掉_不能报成功()
    {
        // 最阴的一种失败: EC 收下了时序, 模式标志也确实变成了 0xDC,
        // 但手动 PWM 值没进去, 停在初值 0 —— 于是一个风扇直接停转。
        // 只回读模式标志的老实现会在这里报"已切到强冷模式 (两个风扇满转)",
        // 用户看着界面以为好了, 实际 CPU 风扇不转。
        var fake = IdleEc();
        fake.SwallowWritesOn.Add(EcRegisters.ManualPwm1);
        fake.SwallowWritesOn.Add(EcRegisters.ManualPwm2);
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok, "PWM 没写进去就不能报成功");
        Assert.True(result.Indeterminate, "模式已切手动、风扇却在按残留值转, 状态是不确定的");
        Assert.Contains("手动 PWM", result.Message);
        // 残留值确实是 0 (IdleEc 里预设的是 120/118, 但被吞掉后不会变)。
        Assert.Equal(120, fake.Reg(EcRegisters.ManualPwm1));
    }

    [Fact]
    public void 一个风扇的PWM被吞掉_也不能报成功()
    {
        // 单侧失败比两侧更容易被忽略: 另一个风扇确实满转, 听起来风扇是响的。
        var fake = IdleEc();
        fake.SwallowWritesOn.Add(EcRegisters.ManualPwm2);
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok);
        Assert.True(result.Indeterminate);
        Assert.Contains("Fan2=118", result.Message);
    }

    [Fact]
    public void PWM回读失败_同样报不确定而不是成功()
    {
        // 标志读得出来、PWM 读不出来。既然无法确认 PWM 生效, 就不能说满转。
        var fake = IdleEc();
        fake.FailReadsOn.Add(EcRegisters.ManualPwm1);
        fake.FailReadsOnlyAfterWrites = true;
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok);
        Assert.True(result.Indeterminate);
        Assert.Contains("回读确认失败", result.Message);
    }

    [Fact]
    public void 切强冷_先写PWM再写模式标志()
    {
        var fake = IdleEc();
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(FanModeState.StrongCool, result.State);

        // 顺序是硬要求: 标志变成 0xDC 的那一刻, EC 立刻按 ManualPwm 调速。
        // 如果先写标志, 中间那一瞬会用上一次残留的 PWM 值。
        Assert.Equal(new[]
        {
            (EcRegisters.ManualPwm1, 184),
            (EcRegisters.ManualPwm2, 184),
            (EcRegisters.ModeFlag1, (int)EcRegisters.ModeManualPwm),
            (EcRegisters.ModeFlag2, (int)EcRegisters.ModeManualPwm),
        }, fake.Writes);
    }

    [Fact]
    public void 切强冷_两个风扇的PWM都是满量程184()
    {
        var fake = IdleEc();
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm1));
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm2));
        Assert.Equal(EcRegisters.ModeManualPwm, (byte)fake.Reg(EcRegisters.ModeFlag1));
        Assert.Equal(EcRegisters.ModeManualPwm, (byte)fake.Reg(EcRegisters.ModeFlag2));
    }

    [Fact]
    public void 切强冷_写完后回读确认模式()
    {
        var fake = IdleEc();
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        // 回读是"写完不看一眼等于赌 EC 收下了"的补丁, 必须发生在这四个写入之后。
        Assert.Contains(EcRegisters.ModeFlag1, fake.Reads);
        Assert.Contains(EcRegisters.ModeFlag2, fake.Reads);
    }

    [Fact]
    public void 切自动_只写模式标志不碰手动PWM()
    {
        var fake = IdleEc()
            .With(EcRegisters.ManualPwm1, 184)   // 模拟刚从强冷回来, 残留满量程值
            .With(EcRegisters.ManualPwm2, 184)
            .With(EcRegisters.ModeFlag1, EcRegisters.ModeManualPwm)
            .With(EcRegisters.ModeFlag2, EcRegisters.ModeManualPwm);
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.Auto);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[]
        {
            (EcRegisters.ModeFlag1, (int)EcRegisters.ModeAuto),
            (EcRegisters.ModeFlag2, (int)EcRegisters.ModeAuto),
        }, fake.Writes);

        // 自动模式下 EC 不读 ManualPwm, 残留值无害; 多写一次就多一次出错机会。
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm1));
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm2));
    }

    [Fact]
    public void 写入前先抓四个寄存器的快照()
    {
        var fake = IdleEc();
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        // 快照必须在任何写入之前: 拿到"我们进来时 EC 本来是什么样",
        // 否则回滚会把我们自己写进去的值当成原始值还原。
        //
        // 这条断言原来写的是 fake.Reads.Count / Contains —— 两个都只看"读过这些地址",
        // 把快照挪到写入之后照样能过, 等于什么都没验证。用 Ops 这条同一时间线才判得动先后。
        //
        // 只看"第一次"读: 写完之后还有一次 PWM 回读确认 (0.6 新增的假成功防护),
        // 那次读本来就在写入之后, 不能拿它当快照。
        int firstReadOfSnapshot = -1;
        int firstWrite = -1;
        for (int i = 0; i < fake.Ops.Count; i++)
        {
            var (isRead, address, _) = fake.Ops[i];
            if (isRead && address is EcRegisters.ManualPwm1 or EcRegisters.ManualPwm2 && firstReadOfSnapshot < 0)
                firstReadOfSnapshot = i;
            if (!isRead && firstWrite < 0)
                firstWrite = i;
        }

        Assert.True(firstReadOfSnapshot >= 0, "应该读过 ManualPwm1 / ManualPwm2");
        Assert.True(firstWrite >= 0, "应该发生过写入");
        Assert.True(firstReadOfSnapshot < firstWrite,
            $"快照读 (第 {firstReadOfSnapshot} 次操作) 必须早于第一次写入 (第 {firstWrite} 次操作)");
    }

    [Fact]
    public void 设备未打开_拒绝切换且不写任何寄存器()
    {
        // 注入 null 传输 = 未打开。构造一个未打开的 reader: 用真实构造需要 blob,
        // 这里直接用 "读不到 EC" 的路径 —— _ec 为 null。
        using var reader = new FanReader(Array.Empty<byte>());

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok);
        Assert.Contains("设备未打开", result.Message);
    }

    [Fact]
    public void 写入抛异常_回滚到自动且还原快照PWM()
    {
        var fake = IdleEc();
        // 第二个 PWM 写入失败: 此时第一个 PWM 已经是 184, 标志还没动。
        // 用"只失败一次"而不是永久失败 —— 永久失败会把回滚时对同一地址的还原写入
        // 一起堵死, 那验的就不是"写失败后回滚成功", 而是"回滚也失败"了。
        fake.FailFirstWriteOnlyOn.Add(EcRegisters.ManualPwm2);
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok);
        Assert.False(result.Indeterminate);        // 回滚成功 = 状态确定
        Assert.Equal(FanModeState.Auto, result.State);

        // 回滚顺序: 先把两个标志撤成自动 (止损), 再还原快照里的 PWM。
        var rollback = fake.Writes.Skip(1).ToArray();   // 跳过最初那次成功的 ManualPwm1=184
        Assert.Contains((EcRegisters.ModeFlag1, (int)EcRegisters.ModeAuto), rollback);
        Assert.Contains((EcRegisters.ModeFlag2, (int)EcRegisters.ModeAuto), rollback);
        Assert.Contains((EcRegisters.ManualPwm1, 120), rollback);   // 快照值
        Assert.Contains((EcRegisters.ManualPwm2, 118), rollback);
    }

    [Fact]
    public void 回滚后两个标志都是自动_不留下分裂状态()
    {
        var fake = IdleEc();
        fake.FailWritesOn.Add(EcRegisters.ManualPwm2);
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        // 分裂状态 (一个 0xDC 一个 0x00) 是本程序最需要避免的残留:
        // 它不受任何后续逻辑管理, 用户只能听到一个风扇狂转。
        Assert.Equal(EcRegisters.ModeAuto, (byte)fake.Reg(EcRegisters.ModeFlag1));
        Assert.Equal(EcRegisters.ModeAuto, (byte)fake.Reg(EcRegisters.ModeFlag2));
    }

    [Fact]
    public void 回滚顺序_先撤标志再还原PWM()
    {
        var fake = IdleEc();
        fake.FailWritesOn.Add(EcRegisters.ManualPwm2);
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        int flag1Idx = fake.Writes.FindIndex(w => w == (EcRegisters.ModeFlag1, (int)EcRegisters.ModeAuto));
        int pwm1Idx = fake.Writes.FindIndex(w => w == (EcRegisters.ManualPwm1, 120));

        Assert.True(flag1Idx >= 0 && pwm1Idx >= 0);
        // 标志离开 0xDC 是止损第一步; 之后再还原 PWM, 免得残留 184 将来被别的软件
        // 切成 0xDC 时立刻满转。
        Assert.True(flag1Idx < pwm1Idx, "回滚应先撤模式标志, 再还原手动 PWM");
    }

    [Fact]
    public void 回滚本身失败_标记为不确定状态()
    {
        // 起点就是一个"半个标志已经是手动"的机器, 模拟上次异常退出留下的残留。
        var fake = new FakeEc()
            .With(EcRegisters.ManualPwm1, 120)
            .With(EcRegisters.ManualPwm2, 118)
            .With(EcRegisters.ModeFlag1, EcRegisters.ModeManualPwm)
            .With(EcRegisters.ModeFlag2, EcRegisters.ModeAuto);
        fake.FailWritesOn.Add(EcRegisters.ManualPwm2);
        // 回滚时撤 ModeFlag1 的写入被 EC 收下但不生效 —— 回滚后两个标志会不一致。
        // (若只是"写不进去", 寄存器反而停在初值 0x00, 回读仍是自动, 造不出不确定态。)
        fake.SwallowWritesOn.Add(EcRegisters.ModeFlag1);
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok);
        Assert.True(result.Indeterminate, "回滚失败必须让上层显著提示用户");
        Assert.Contains("不确定状态", result.Message);
    }

    [Fact]
    public void EC吞掉写入_回读发现模式未变_报告失败()
    {
        var fake = IdleEc();
        fake.SwallowWritesOn.Add(EcRegisters.ModeFlag1);   // 收下但不生效
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        // 这正是"界面说切换成功、EC 里其实还是旧值"那个坑。
        Assert.False(result.Ok);
        Assert.Equal(FanModeState.Mixed, result.State);
    }

    [Fact]
    public void 两个标志不一致_回读判为Mixed并标记不确定()
    {
        var fake = IdleEc();
        // 一个收下 0xDC, 另一个不收 —— 典型的分裂残留。
        fake.SwallowWritesOn.Add(EcRegisters.ModeFlag2);
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        Assert.False(result.Ok);
        Assert.True(result.Indeterminate);
    }

    [Fact]
    public void 回读抛异常_写入已成功_标记为不确定而非普通失败()
    {
        // 场景: 四个寄存器全部写成功, 只有回读那一下读不出来。
        // 曾经的实现把这里当成普通失败 (Indeterminate=false), 等于向上一层宣告
        // "状态是确定的" —— 方向正好反了: 真实的确定性才是未知的那个。
        var fake = IdleEc();
        fake.FailReadsOn.Add(EcRegisters.ModeFlag1);
        fake.FailReadsOnlyAfterWrites = true;   // 写入前的快照读取必须正常
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.StrongCool);

        // 四个写入必须全部发生: 这是"已经写进去了"的前提。
        Assert.Equal(new[]
        {
            (EcRegisters.ManualPwm1, 184),
            (EcRegisters.ManualPwm2, 184),
            (EcRegisters.ModeFlag1, (int)EcRegisters.ModeManualPwm),
            (EcRegisters.ModeFlag2, (int)EcRegisters.ModeManualPwm),
        }, fake.Writes);

        Assert.False(result.Ok);
        Assert.True(result.Indeterminate, "回读失败时状态是未知的, 不能报成确定状态");
        Assert.Equal(FanModeState.Unknown, result.State);

        // 文案不能让用户以为写入一定失败 —— 风扇很可能已经在满转。
        Assert.Contains("可能已经生效", result.Message);
        Assert.Contains("查看当前转速与温度", result.Message);
        Assert.DoesNotContain("切换风扇模式失败", result.Message);
    }

    [Fact]
    public void 回读抛异常_不回滚_强冷指令保留在EC里()
    {
        // 回滚是给"写入没成功"用的。写入已经成功时回滚, 等于把用户刚要的强冷撤掉,
        // 反而制造出他并不想要的状态。
        var fake = IdleEc();
        fake.FailReadsOn.Add(EcRegisters.ModeFlag1);
        fake.FailReadsOnlyAfterWrites = true;
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        // 只有那四个写入, 没有任何回滚写入 (没有 0x00, 没有还原快照 PWM)。
        Assert.Equal(4, fake.Writes.Count);
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm1));
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm2));
        Assert.Equal(EcRegisters.ModeManualPwm, (byte)fake.Reg(EcRegisters.ModeFlag1));
        Assert.Equal(EcRegisters.ModeManualPwm, (byte)fake.Reg(EcRegisters.ModeFlag2));
    }

    [Fact]
    public void 切自动时回读抛异常_同样标记为不确定()
    {
        var fake = IdleEc()
            .With(EcRegisters.ManualPwm1, 184)
            .With(EcRegisters.ManualPwm2, 184)
            .With(EcRegisters.ModeFlag1, EcRegisters.ModeManualPwm)
            .With(EcRegisters.ModeFlag2, EcRegisters.ModeManualPwm);
        fake.FailReadsOn.Add(EcRegisters.ModeFlag2);
        fake.FailReadsOnlyAfterWrites = true;
        using var reader = new FanReader(fake);

        FanModeResult result = reader.SetMode(FanMode.Auto);

        // 切自动方向也一致: 两个标志已写成功, 只是无法确认。
        Assert.Equal(2, fake.Writes.Count);
        Assert.False(result.Ok);
        Assert.True(result.Indeterminate);
        Assert.Contains("可能已经生效", result.Message);
    }

    [Fact]
    public void 快照里是越界PWM_回滚仍能原样还原()
    {
        // 别的软件可能留下 200 这种越界值 (那时风扇本来就是停的)。
        // 还原一个既存状态不该受"命令值必须合法"的约束, 否则会把越界值留住。
        var fake = new FakeEc()
            .With(EcRegisters.ManualPwm1, 200)
            .With(EcRegisters.ManualPwm2, 210)
            .With(EcRegisters.ModeFlag1, EcRegisters.ModeAuto)
            .With(EcRegisters.ModeFlag2, EcRegisters.ModeAuto);
        fake.FailWritesOn.Add(EcRegisters.ManualPwm2);
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);

        Assert.Equal(200, fake.Reg(EcRegisters.ManualPwm1));
        Assert.Equal(210, fake.Reg(EcRegisters.ManualPwm2));
    }

    [Fact]
    public void 反复切换_每次都回到确定状态()
    {
        var fake = IdleEc();
        using var reader = new FanReader(fake);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(reader.SetMode(FanMode.StrongCool).Ok);
            Assert.Equal(FanModeState.StrongCool, reader.ReadModeState());

            Assert.True(reader.SetMode(FanMode.Auto).Ok);
            Assert.Equal(FanModeState.Auto, reader.ReadModeState());
        }
    }

    [Fact]
    public void 自动切强冷再切回_标志归零而PWM保留()
    {
        var fake = IdleEc();
        using var reader = new FanReader(fake);

        reader.SetMode(FanMode.StrongCool);
        reader.SetMode(FanMode.Auto);

        Assert.Equal(EcRegisters.ModeAuto, (byte)fake.Reg(EcRegisters.ModeFlag1));
        Assert.Equal(EcRegisters.ModeAuto, (byte)fake.Reg(EcRegisters.ModeFlag2));
        // 自动模式不碰手动值: 留着 184 无害, 少一次写入就少一次出错机会。
        Assert.Equal(184, fake.Reg(EcRegisters.ManualPwm1));
    }
}
