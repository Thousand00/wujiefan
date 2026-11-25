using Zephyr.Core.Config;
using Zephyr.Core.Ec;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// 设置的读取归一化与原子写入。
///
/// 这些分支的共同点是"出错时绝不能回退成强冷": 一次读配置失败如果把风扇钉在
/// 5700 RPM 上, 用户第二天开机就会听到一台一直在吼的机器。所以每条降级路径都要断言
/// FanMode == Auto。
/// </summary>
[Collection("RealSettingsFile")]
public class AppSettingsTests
{
    [Fact]
    public void 文件不存在_返回安全默认()
    {
        // 本集合共用一份真实配置文件, 前一条用例可能刚写过它。
        // 这条要测的是"文件不存在"分支, 必须自己把文件清掉, 不能靠执行顺序。
        RealSettingsFileFixture.DeleteSettings();

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.Auto, s.FanMode);          // 绝不回退成 StrongCool
        Assert.False(s.ForceUnsupported);
        Assert.Equal(1000, s.PollIntervalMs);
        Assert.True(s.MinimizeToTray);
        Assert.True(s.PublishToRtss);
        Assert.False(AppSettings.LoadDegraded);
        Assert.Null(AppSettings.LastError);
    }

    [Fact]
    public void 空文件_降级且不接管风扇()
    {
        RealSettingsFileFixture.WriteSettings("");

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.Auto, s.FanMode);
        Assert.True(AppSettings.LoadDegraded);
        Assert.Contains("安全默认", AppSettings.LoadWarning!);
    }

    [Fact]
    public void 损坏JSON_降级到自动并记录原因()
    {
        RealSettingsFileFixture.WriteSettings("{ \"FanMode\": 1, \"PollIntervalMs\": ");

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.Auto, s.FanMode);
        Assert.True(AppSettings.LoadDegraded);
        Assert.Contains("损坏", AppSettings.LoadWarning!);
        Assert.NotNull(AppSettings.LastError);
    }

    [Fact]
    public void 损坏文件里写着强冷_仍然回退到自动()
    {
        // 这是最关键的一条: 坏文件里的 "FanMode": 1 不能被信任。
        RealSettingsFileFixture.WriteSettings("{ \"FanMode\": 1, oops }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.Auto, s.FanMode);
    }

    [Fact]
    public void 旧版文件没有SchemaVersion_按当前版本迁移()
    {
        // 0.4.0 及更早写出的文件没有这个字段 (反序列化得到 0)。
        RealSettingsFileFixture.WriteSettings(
            "{ \"PollIntervalMs\": 500, \"MinimizeToTray\": false, \"PublishToRtss\": true, \"FanMode\": 0 }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Equal(500, s.PollIntervalMs);         // 正常字段要保留
        Assert.False(s.MinimizeToTray);
        Assert.Equal(FanMode.Auto, s.FanMode);
    }

    [Theory]
    [InlineData(0, 250)]        // 太小会把 EC 打爆
    [InlineData(1, 250)]
    [InlineData(249, 250)]
    [InlineData(250, 250)]      // 边界: 下界合法
    [InlineData(10000, 10000)]  // 边界: 上界合法
    [InlineData(10001, 10000)]
    [InlineData(60000, 10000)]
    [InlineData(-5, 250)]
    public void PollIntervalMs_越界夹到250至10000(int input, int expected)
    {
        RealSettingsFileFixture.WriteSettings($"{{ \"PollIntervalMs\": {input}, \"FanMode\": 0 }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(expected, s.PollIntervalMs);
    }

    [Theory]
    [InlineData(99)]     // 枚举越界 = 手改坏了
    [InlineData(-1)]
    [InlineData(2)]
    public void FanMode枚举越界_归一化为自动(int raw)
    {
        RealSettingsFileFixture.WriteSettings($"{{ \"FanMode\": {raw}, \"PollIntervalMs\": 1000 }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.Auto, s.FanMode);
    }

    [Fact]
    public void FanMode为强冷_正常保留()
    {
        RealSettingsFileFixture.WriteSettings("{ \"FanMode\": 1, \"PollIntervalMs\": 1000 }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.StrongCool, s.FanMode);
    }

    [Fact]
    public void SchemaVersion高于支持版本_降级但保留无害字段()
    {
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 99, \"FanMode\": 1, \"ForceUnsupported\": true, \"PollIntervalMs\": 700, \"MinimizeToTray\": false }");

        AppSettings s = AppSettings.Load();

        // 来自更新版本的文件: 我们不知道字段语义, 只信三个无害项。
        Assert.Equal(FanMode.Auto, s.FanMode);          // 不信任强冷
        Assert.False(s.ForceUnsupported);               // 不信任机型校验绕过
        Assert.Equal(700, s.PollIntervalMs);            // 无害: 保留
        Assert.False(s.MinimizeToTray);                 // 无害: 保留
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.True(AppSettings.LoadDegraded);
    }

    [Fact]
    public void SchemaVersion更高的越界间隔_也要夹取()
    {
        // 这条分支在常规 Math.Clamp 之前就 return 了, 单独夹一次才不会漏。
        RealSettingsFileFixture.WriteSettings("{ \"SchemaVersion\": 99, \"PollIntervalMs\": 999999 }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(10000, s.PollIntervalMs);
    }

    [Fact]
    public void 旧版配置里的过热保护字段_被当未知字段忽略()
    {
        // schema 1 -> 2 的原因是砍掉了过热保护: 那两个字段在 2 里已经没有对应属性。
        // 旧文件里还留着它们, 反序列化必须只是忽略, 不能因此判定文件损坏 ——
        // 否则每个从 0.6.x 升上来的用户第一次启动都会被"设置文件损坏"吓一跳。
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 1, \"EmergencyTemperatureC\": 98, \"EmergencyHoldSamples\": 5, " +
            "\"PollIntervalMs\": 1000, \"StrongCoolHotKey\": \"Ctrl+Shift+End\" }");

        AppSettings s = AppSettings.Load();

        Assert.False(AppSettings.LoadDegraded);      // 不是损坏, 只是旧字段
        Assert.Null(AppSettings.LastError);
        Assert.Equal("Ctrl+Shift+End", s.StrongCoolHotKey);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void 旧版程序读到新版配置_降级为不接管风扇()
    {
        // 反过来的方向: 0.6.x 的代码看到 SchemaVersion 2 会走"版本更高"分支。
        // 这里用 SafeDefaults + 人为调高版本号来模拟"更新版本写出的文件",
        // 断言的结果就是那时对用户的交代: 不接管风扇, 而不是按 1 的语义乱读。
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 3, \"FanMode\": 1, \"PollIntervalMs\": 1000 }");

        AppSettings s = AppSettings.Load();

        Assert.Equal(FanMode.Auto, s.FanMode);
        Assert.True(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 旧版ForceUnsupported无时间戳_补有效期并警告()
    {
        RealSettingsFileFixture.WriteSettings("{ \"ForceUnsupported\": true, \"PollIntervalMs\": 1000 }");

        AppSettings s = AppSettings.Load();

        Assert.True(s.ForceUnsupported);                       // 保留用户意图
        Assert.NotNull(s.ForceUnsupportedConfirmedAt);         // 但只给 12 小时
        Assert.True(AppSettings.LoadNormalized);
        Assert.False(AppSettings.LoadDegraded);                // 文件是好的, 不算降级
        Assert.Contains("12 小时", AppSettings.LoadWarning!);
    }

    [Fact]
    public void ForceUnsupported确认已过期_自动失效()
    {
        var expired = DateTimeOffset.Now - AppSettings.ForceUnsupportedValidity - TimeSpan.FromHours(1);
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"ForceUnsupported\": true, \"ForceUnsupportedConfirmedAt\": \"{expired:O}\", \"PollIntervalMs\": 1000 }}");

        AppSettings s = AppSettings.Load();

        Assert.False(s.ForceUnsupported);                       // 不静默跳过机型校验
        Assert.Null(s.ForceUnsupportedConfirmedAt);
        Assert.Contains("自动恢复机型校验", AppSettings.LoadWarning!);
    }

    [Fact]
    public void ForceUnsupported确认仍在有效期内_保留()
    {
        var fresh = DateTimeOffset.Now - TimeSpan.FromHours(1);
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"ForceUnsupported\": true, \"ForceUnsupportedConfirmedAt\": \"{fresh:O}\", \"PollIntervalMs\": 1000 }}");

        AppSettings s = AppSettings.Load();

        Assert.True(s.ForceUnsupported);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 未开启ForceUnsupported_清掉残留时间戳()
    {
        var old = DateTimeOffset.Now - TimeSpan.FromHours(2);
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"ForceUnsupported\": false, \"ForceUnsupportedConfirmedAt\": \"{old:O}\", \"PollIntervalMs\": 1000 }}");

        AppSettings s = AppSettings.Load();

        Assert.False(s.ForceUnsupported);
        Assert.Null(s.ForceUnsupportedConfirmedAt);
    }

    [Fact]
    public void Save_写入文件且包含当前SchemaVersion()
    {
        var s = new AppSettings { PollIntervalMs = 800, MinimizeToTray = false, FanMode = FanMode.StrongCool };

        Assert.True(s.Save(), AppSettings.LastError ?? "");

        string json = File.ReadAllText(RealSettingsFileFixture.SettingsPath);
        Assert.Contains($"\"SchemaVersion\": {AppSettings.CurrentSchemaVersion}", json);
        Assert.Contains("\"FanMode\": 1", json);
        Assert.Contains("\"PollIntervalMs\": 800", json);
        Assert.Null(AppSettings.LastError);
    }

    [Fact]
    public void Save_不残留临时文件()
    {
        // 原子写的中间产物必须清理干净, 否则配置目录会慢慢堆满 settings.json.*.tmp。
        new AppSettings().Save();

        string[] leftovers = Directory.GetFiles(RealSettingsFileFixture.ConfigDir, "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Save_覆盖写不会产生半截文件()
    {
        new AppSettings { PollIntervalMs = 1000 }.Save();
        new AppSettings { PollIntervalMs = 2000 }.Save();

        AppSettings reloaded = AppSettings.Load();

        Assert.Equal(2000, reloaded.PollIntervalMs);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void Save_ForceUnsupported回填确认时间()
    {
        var s = new AppSettings { ForceUnsupported = true, ForceUnsupportedConfirmedAt = null };

        s.Save();

        Assert.NotNull(s.ForceUnsupportedConfirmedAt);
        AppSettings reloaded = AppSettings.Load();
        Assert.True(reloaded.ForceUnsupported);   // 刚写的, 在有效期内
    }

    [Fact]
    public void Save_未开启ForceUnsupported清空确认时间()
    {
        var s = new AppSettings
        {
            ForceUnsupported = false,
            ForceUnsupportedConfirmedAt = DateTimeOffset.Now,
        };

        s.Save();

        Assert.Null(s.ForceUnsupportedConfirmedAt);
    }

    [Fact]
    public void Save后Load_往返一致()
    {
        var original = new AppSettings
        {
            PollIntervalMs = 1500,
            MinimizeToTray = false,
            PublishToRtss = false,
            FanMode = FanMode.StrongCool,
            ForceUnsupported = false,
        };

        Assert.True(original.Save());
        AppSettings loaded = AppSettings.Load();

        Assert.Equal(original.PollIntervalMs, loaded.PollIntervalMs);
        Assert.Equal(original.MinimizeToTray, loaded.MinimizeToTray);
        Assert.Equal(original.PublishToRtss, loaded.PublishToRtss);
        Assert.Equal(original.FanMode, loaded.FanMode);
        Assert.False(loaded.ForceUnsupported);
    }

    [Fact]
    public void SafeDefaults_永远是不接管风扇的形态()
    {
        AppSettings s = AppSettings.SafeDefaults();

        Assert.Equal(FanMode.Auto, s.FanMode);
        Assert.False(s.ForceUnsupported);
        Assert.Null(s.ForceUnsupportedConfirmedAt);
        Assert.Equal(1000, s.PollIntervalMs);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void 有效期是12小时()
    {
        Assert.Equal(TimeSpan.FromHours(12), AppSettings.ForceUnsupportedValidity);
    }

    [Fact]
    public void LogDirectory_指向配置文件所在目录()
    {
        Assert.Equal(RealSettingsFileFixture.ConfigDir, AppSettings.LogDirectory);
    }
}
