using WujieFan.Core.Config;
using WujieFan.Core.Ec;
using Xunit;

namespace WujieFan.Core.Tests;

/// <summary>
/// 过热保护的两个可配置项：触发温度与连续采样次数。
///
/// 这一组测试的失败方式很不对称：把阈值调得太松（比如 100℃、60 次），程序不会崩、
/// 不会报错、界面也照常显示强冷，只是在真正过热时**不再收手**。用户唯一能察觉的
/// 时机就是风扇还在满转、机器已经烫到降频。所以越界处理必须钉死。
///
/// 特别注意本组断言的都是"越界回到默认值"，而不是"夹到边界"：
///   - 温度夹到上界 100 = 把保护关掉（EC 温度量程就是 1..100），方向正好反了；
///   - 次数夹到上界 60，在 1 秒轮询下是一分钟，也早已没有保护意义。
/// 谁要是把实现改成 Math.Clamp，这里的断言会立刻红。
/// </summary>
[Collection("RealSettingsFile")]
public class EmergencySettingsTests
{
    // ── 默认值 ────────────────────────────────────────────────────────────

    [Fact]
    public void 新建实例的两个默认值()
    {
        // 默认值必须等于 EcRegisters 里那两个"0.4.x 一直在用"的常量：
        // 不改设置就等于行为没变, 升级不该悄悄改变保护强度。
        var s = new AppSettings();

        Assert.Equal(92, s.EmergencyTemperatureC);
        Assert.Equal(3, s.EmergencyHoldSamples);
        Assert.Equal(EcRegisters.EmergencyTemperatureC, s.EmergencyTemperatureC);
        Assert.Equal(EcRegisters.EmergencyHoldSamples, s.EmergencyHoldSamples);
    }

    [Fact]
    public void 安全默认的两个默认值()
    {
        // 降级路径 (坏文件 / 读不到) 也必须有完整保护, 不能是 0 或未初始化。
        AppSettings s = AppSettings.SafeDefaults();

        Assert.Equal(92, s.EmergencyTemperatureC);
        Assert.Equal(3, s.EmergencyHoldSamples);
    }

    [Fact]
    public void 可设区间常量()
    {
        // 界内/界外的所有断言都以这四个常量为准, 它们本身写错会让下面一组测试全部失去意义。
        Assert.Equal(70, AppSettings.EmergencyTemperatureMin);
        Assert.Equal(100, AppSettings.EmergencyTemperatureMax);
        Assert.Equal(1, AppSettings.EmergencyHoldSamplesMin);
        Assert.Equal(60, AppSettings.EmergencyHoldSamplesMax);
    }

    // ── 界内保留 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(70)]    // 下界: 合法, 不能被当成越界
    [InlineData(71)]
    [InlineData(92)]
    [InlineData(99)]
    [InlineData(100)]   // 上界: 合法。用户坚持要 100 是他自己的选择, 程序可以保留
    public void 温度在区间内_原样保留(int input)
    {
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"SchemaVersion\": 1, \"EmergencyTemperatureC\": {input} }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(input, s.EmergencyTemperatureC);
        // 界内值不该被"归一化"一遍: 否则界面会平白提示"设置已归一化"。
        Assert.Null(AppSettings.LoadWarning);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Theory]
    [InlineData(1)]     // 下界: 单次达标就收手
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(59)]
    [InlineData(60)]    // 上界
    public void 连续次数在区间内_原样保留(int input)
    {
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"SchemaVersion\": 1, \"EmergencyHoldSamples\": {input} }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(input, s.EmergencyHoldSamples);
        Assert.Null(AppSettings.LoadWarning);
        Assert.False(AppSettings.LoadDegraded);
    }

    // ── 越界回默认（重点）─────────────────────────────────────────────────

    [Theory]
    [InlineData(69, 92)]     // 差 1℃ 越界 → 回 92, 不是夹到 70
    [InlineData(0, 92)]      // 0 = 字段没写或写坏了
    [InlineData(-5, 92)]
    [InlineData(101, 92)]    // 超 1℃ → 回 92, 不是夹到 100
    [InlineData(200, 92)]    // 手误
    [InlineData(1000, 92)]
    public void 温度越界_回到92而不是夹到边界(int input, int expected)
    {
        // 这条是整组的关键。若实现改成 Math.Clamp(input, 70, 100):
        //   69 会变成 70 (assert 期待 92 → 红, 正确暴露)
        //   101/200 会变成 100 —— 而 100 等于关闭保护, 是最危险的那种"看起来正常"。
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"SchemaVersion\": 1, \"EmergencyTemperatureC\": {input} }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(expected, s.EmergencyTemperatureC);
        // 仍然是一个真的保护, 而不是被顶到量程尽头。
        Assert.True(s.EmergencyTemperatureC <= AppSettings.EmergencyTemperatureMax);
    }

    [Theory]
    [InlineData(0, 3)]       // 0 次 = 保护失效 → 回 3, 不是夹到 1
    [InlineData(-5, 3)]
    [InlineData(61, 3)]      // 超 1 → 回 3, 不是夹到 60
    [InlineData(600, 3)]
    [InlineData(int.MaxValue, 3)]
    public void 连续次数越界_回到3而不是夹到边界(int input, int expected)
    {
        // 同样的不对称: 夹到 60 在 1 秒轮询下是一分钟, 已经没有保护意义,
        // 所以越界一律回 3 —— 与 0.4.x 的表现一致。
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"SchemaVersion\": 1, \"EmergencyHoldSamples\": {input} }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(expected, s.EmergencyHoldSamples);
        Assert.True(s.EmergencyHoldSamples <= AppSettings.EmergencyHoldSamplesMax);
    }

    [Fact]
    public void 两个字段同时越界_都回默认()
    {
        // 一个坏文件可能两个都写坏; 分别处理, 不要因为先命中一个就跳过另一个。
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 1, \"EmergencyTemperatureC\": 10, \"EmergencyHoldSamples\": 999 }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(92, s.EmergencyTemperatureC);
        Assert.Equal(3, s.EmergencyHoldSamples);
    }

    [Fact]
    public void 越界归一化会给出提示()
    {
        // 归一化是"悄悄改了用户的设定", 至少要让他知道。
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 1, \"EmergencyTemperatureC\": 200 }");

        AppSettings.Load();

        Assert.Contains("归一化", AppSettings.LoadWarning!);

        // 归一化 ≠ 降级。文件本身是好的, 只是某个值越界被修正, 程序照常接管风扇,
        // 所以界面绝不能说"不接管风扇" —— 这个区分由 LoadNormalized 承载。
        Assert.True(AppSettings.LoadNormalized);
        Assert.False(AppSettings.LoadDegraded);
    }

    // ── 旧配置兼容 ────────────────────────────────────────────────────────

    [Fact]
    public void 零四版旧配置没有这两个键_按默认加载且不降级()
    {
        // 这是 0.4.x 用户升级后真实会遇到的文件内容 (逐字取自 %LOCALAPPDATA%\WujieFan\settings.json)。
        // 它没有 SchemaVersion、也没有过热保护的两个键。
        // 升级后绝不能因此告诉用户"设置文件已降级" —— 那会让人以为配置被破坏了。
        RealSettingsFileFixture.WriteSettings(
            "{\n" +
            "  \"PollIntervalMs\": 1000,\n" +
            "  \"MinimizeToTray\": true,\n" +
            "  \"PublishToRtss\": true,\n" +
            "  \"ForceUnsupported\": false,\n" +
            "  \"FanMode\": 0\n" +
            "}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(92, s.EmergencyTemperatureC);
        Assert.Equal(3, s.EmergencyHoldSamples);
        Assert.False(AppSettings.LoadDegraded);
        Assert.Null(AppSettings.LastError);
        // 用户原本的其他设定也不能被动到。
        Assert.Equal(1000, s.PollIntervalMs);
        Assert.True(s.MinimizeToTray);
        Assert.True(s.PublishToRtss);
        Assert.Equal(FanMode.Auto, s.FanMode);
    }

    [Fact]
    public void 新版文件缺这两个键_完全不触发归一化()
    {
        // 与上一条的区别: 这里 SchemaVersion 已经是 1, 没有版本迁移带来的 changed。
        // 于是这条能精确证明"缺字段本身不会触发归一化警告"——
        // 如果实现把缺失值当成 0 再按越界处理, 这里会红。
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 1, \"PollIntervalMs\": 1000, \"FanMode\": 0 }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(92, s.EmergencyTemperatureC);
        Assert.Equal(3, s.EmergencyHoldSamples);
        Assert.Null(AppSettings.LoadWarning);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 存盘再读回_自定义值不变()
    {
        // 用户改过的值必须能落盘并原样回来, 否则设置界面等于摆设。
        RealSettingsFileFixture.DeleteSettings();

        var saved = new AppSettings { EmergencyTemperatureC = 85, EmergencyHoldSamples = 5 };
        Assert.True(saved.Save(), AppSettings.LastError);

        AppSettings reloaded = AppSettings.Load();

        Assert.Equal(85, reloaded.EmergencyTemperatureC);
        Assert.Equal(5, reloaded.EmergencyHoldSamples);
        Assert.False(AppSettings.LoadDegraded);
        Assert.Null(AppSettings.LoadWarning);
    }

    [Fact]
    public void 存盘的是越界值_读回时回到默认()
    {
        // 手改配置文件 / 旧版本写坏 —— 落盘内容不可信, 读回来仍要过关。
        RealSettingsFileFixture.DeleteSettings();

        var bad = new AppSettings { EmergencyTemperatureC = 150, EmergencyHoldSamples = 0 };
        // Save() 不做归一化 (它只负责把当前值写下去), 所以这里预期能写成功。
        bad.Save();

        AppSettings reloaded = AppSettings.Load();

        Assert.Equal(92, reloaded.EmergencyTemperatureC);
        Assert.Equal(3, reloaded.EmergencyHoldSamples);
    }
}
