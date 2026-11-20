using WujieFan.Core.Config;
using Xunit;

namespace WujieFan.Core.Tests;

/// <summary>
/// 全局快捷键的键位模型与它的配置归一化。
///
/// 这一组的失败方式和过热保护那组不同：不会让程序崩、也不会关掉保护，而是
/// **静默失效** —— 界面显示"Ctrl+Alt+F9"，实际什么都没注册，用户按下去毫无反应，
/// 还以为是键盘或者程序卡了。所以两条边界必须钉死：
///   1. 手改坏的字符串要当成"没配"清理掉，不能留一个看起来设了、其实注册不了的键位；
///   2. 规范写法要唯一，否则同一个键会以两种写法各存一份，比较与去重都会出错。
/// </summary>
[Collection("RealSettingsFile")]
public class HotKeyComboTests
{
    // ── 解析：合法写法 ────────────────────────────────────────────────────

    [Fact]
    public void 解析标准的组合键()
    {
        Assert.True(HotKeyCombo.TryParse("Ctrl+Alt+F9", out HotKeyCombo? combo));
        Assert.NotNull(combo);
        // 0x70 是 F1, 所以 F9 = 0x78。这里断言具体数值而不是 s_vkToName，
        // 因为虚拟键码就是最终交给 RegisterHotKey 的东西，错一位就注册到别的键。
        Assert.Equal(0x78, combo!.VirtualKey);
        Assert.Equal(HotKeyModifiers.Control | HotKeyModifiers.Alt, combo.Modifiers);
    }

    [Theory]
    [InlineData("ctrl+alt+f9")]     // 全小写
    [InlineData("CTRL+ALT+F9")]     // 全大写
    [InlineData("Ctrl + Alt + F9")] // 带空格
    [InlineData("control+alt+F9")]  // Control 的别名
    public void 解析大小写与别名不敏感(string text)
    {
        Assert.True(HotKeyCombo.TryParse(text, out HotKeyCombo? combo));
        Assert.Equal("Ctrl+Alt+F9", combo!.ToSettingString());
    }

    [Fact]
    public void 空值表示没有配置()
    {
        // "没配"是一个合法状态，不是错误：默认就不占用任何热键。
        Assert.True(HotKeyCombo.TryParse(null, out HotKeyCombo? a));
        Assert.Null(a);
        Assert.True(HotKeyCombo.TryParse("", out HotKeyCombo? b));
        Assert.Null(b);
    }

    // ── 解析：坏写法必须被拒 ──────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+F9+Alt")]     // 修饰键跑到按键后面
    [InlineData("F9")]              // 没有修饰键: 会抢走正常打字
    [InlineData("Ctrl+Ctrl+F9")]    // 重复修饰键
    [InlineData("Ctrl+Alt")]        // 只有修饰键, 没有具体的键
    [InlineData("Ctrl+Nope")]       // 白名单里没有的键名
    [InlineData("Ctrl+")]           // 结尾多一个分隔符
    [InlineData("+F9")]             // 开头多一个分隔符
    [InlineData("Ctrl+Alt+F25")]    // F 键只到 F24
    public void 坏写法一律拒绝(string text)
    {
        // 返回 false 的意义是"这段文本不可接受"，调用方据此把它清成空串并记一次归一化。
        // 若哪天实现改成"尽力猜"，这些断言会红 —— 那正是想要的：猜出来的键位无法预测。
        Assert.False(HotKeyCombo.TryParse(text, out HotKeyCombo? combo));
        Assert.Null(combo);
    }

    // ── 规范写法 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HotKeyModifiers.Control | HotKeyModifiers.Alt, "Ctrl+Alt+")]
    [InlineData(HotKeyModifiers.Shift, "Shift+")]
    [InlineData(HotKeyModifiers.Win, "Win+")]
    [InlineData(HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift | HotKeyModifiers.Win, "Ctrl+Alt+Shift+Win+")]
    public void 修饰键顺序固定(HotKeyModifiers modifiers, string expected)
    {
        // 顺序固定之后，同一个组合键只有一种写法，比较和去重才有意义。
        Assert.Equal(expected, HotKeyCombo.ModifiersToString(modifiers));
    }

    [Fact]
    public void 没有按键时规范写法是空串()
    {
        // VirtualKey == 0 表示还没选键。这时不能输出 "Ctrl+Alt+" 这种半成品。
        var combo = new HotKeyCombo { Modifiers = HotKeyModifiers.Control, VirtualKey = 0 };
        Assert.Equal("", combo.ToSettingString());
    }

    [Fact]
    public void 解析后再输出得到同一个规范写法()
    {
        // 归一化就是靠这个幂等性把配置文件里的各种写法统一掉。
        HotKeyCombo.TryParse("ctrl+shift+t", out HotKeyCombo? combo);
        Assert.Equal("Ctrl+Shift+T", combo!.ToSettingString());
    }

    // ── 系统键拦截 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Alt+Enter")]
    [InlineData("Win+L")]
    [InlineData("Win+Tab")]
    public void 系统占用的组合被拦下(string text)
    {
        // 这些键要么注册必然失败，要么会顶掉系统功能。在录制阶段就拒绝，
        // 比等到 Save 之后 RegisterHotKey 失败再回滚要好 —— 那时用户已经离开了设置界面。
        Assert.True(HotKeyCombo.TryParse(text, out HotKeyCombo? combo));
        Assert.NotNull(HotKeyCombo.DescribeProblem(combo!));
    }

    [Theory]
    [InlineData("Alt+Esc")]
    [InlineData("Ctrl+Esc")]
    public void 带Esc的组合一律不可用(string text)
    {
        // Esc 本来就不在白名单里，所以这条大概率停在解析阶段；DescribeProblem 里对
        // Alt+Esc / Ctrl+Esc 的分支是给"哪天 Esc 被加回白名单"留的兜底。
        // 两道关任一生效都算拦住了，这里断言的是结果而不是走哪条路。
        Assert.True(IsRejected(text), $"{text} 不应该能被用作全局热键");
    }

    /// <summary>解析拒绝，或者解析通过但被 DescribeProblem 拦下 —— 两种都算不可用。</summary>
    private static bool IsRejected(string text)
    {
        if (!HotKeyCombo.TryParse(text, out HotKeyCombo? combo)) return true;
        return HotKeyCombo.DescribeProblem(combo!) is not null;
    }

    [Fact]
    public void 正常的组合键没有意见()
    {
        Assert.True(HotKeyCombo.TryParse("Ctrl+Alt+F9", out HotKeyCombo? combo));
        Assert.Null(HotKeyCombo.DescribeProblem(combo!));
    }

    // ── 白名单的刻意缺席 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Num+")]
    [InlineData("Ctrl+Num-")]
    public void 刻意不收的键(string text)
    {
        // Esc 是录制时的取消手势；Num+ / Num- 的名字里带分隔符，会与 "+" 撞车。
        // 这两条不是"忘了加"，是有意为之：改实现时不希望它们悄悄出现在白名单里。
        bool accepted = HotKeyCombo.TryParse(text, out HotKeyCombo? combo);
        Assert.False(accepted && HotKeyCombo.DescribeProblem(combo!) is null);
    }

    // ── 配置持久化 ────────────────────────────────────────────────────────

    [Fact]
    public void 默认不占用任何快捷键()
    {
        // 不设等于没变：0.5.2 的行为里没有热键，升级之后也不该凭空多出一个。
        var s = new AppSettings();
        Assert.Equal("", s.StrongCoolHotKey);
        Assert.Equal("", AppSettings.SafeDefaults().StrongCoolHotKey);
    }

    [Fact]
    public void 存盘再读回_键位不变()
    {
        RealSettingsFileFixture.DeleteSettings();

        var saved = new AppSettings { StrongCoolHotKey = "Ctrl+Alt+F9" };
        Assert.True(saved.Save(), AppSettings.LastError);

        AppSettings reloaded = AppSettings.Load();

        Assert.Equal("Ctrl+Alt+F9", reloaded.StrongCoolHotKey);
        // 规范写法存进去的，读回来不该被"归一化"一遍。
        Assert.Null(AppSettings.LoadWarning);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 非规范写法读回时被改写成规范写法()
    {
        // 用户手改配置写成了小写，读回时统一成规范写法并记一次归一化提示。
        RealSettingsFileFixture.WriteSettings(
            "{ \"SchemaVersion\": 1, \"StrongCoolHotKey\": \"ctrl+alt+f9\" }");

        AppSettings s = AppSettings.Load();

        Assert.Equal("Ctrl+Alt+F9", s.StrongCoolHotKey);
        Assert.Contains("归一化", AppSettings.LoadWarning!);
        Assert.True(AppSettings.LoadNormalized);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Theory]
    [InlineData("F9")]              // 没有修饰键
    [InlineData("Ctrl+Ctrl+F9")]    // 重复修饰键
    [InlineData("Ctrl+Alt")]        // 只有修饰键
    [InlineData("Ctrl+Nope")]       // 白名单外的键名
    [InlineData("随便写的")]         // 完全不是组合键
    public void 手改坏的键位_清成空串而不是留着(string broken)
    {
        // 关键取舍：这里清成空串，而不是保留原文本。
        // 留着一个注册不了的字符串，界面会显示"看起来设了、其实不生效"，
        // 而每次启动都会因为同样的原因注册失败 —— 用户永远查不出原因。
        RealSettingsFileFixture.WriteSettings(
            $"{{ \"SchemaVersion\": 1, \"StrongCoolHotKey\": \"{broken}\" }}");

        AppSettings s = AppSettings.Load();

        Assert.Equal("", s.StrongCoolHotKey);
        // 归一化 ≠ 降级：文件本身是好的，程序照常工作。
        Assert.True(AppSettings.LoadNormalized);
        Assert.False(AppSettings.LoadDegraded);
    }

    [Fact]
    public void 旧配置没有这个键_按默认加载且不降级()
    {
        // 0.5.x 用户的真实文件内容，没有 StrongCoolHotKey。
        RealSettingsFileFixture.WriteSettings(
            "{\n" +
            "  \"PollIntervalMs\": 1000,\n" +
            "  \"MinimizeToTray\": true,\n" +
            "  \"PublishToRtss\": true,\n" +
            "  \"ForceUnsupported\": false,\n" +
            "  \"FanMode\": 0\n" +
            "}");

        AppSettings s = AppSettings.Load();

        Assert.Equal("", s.StrongCoolHotKey);
        // 缺字段本身不是"被归一化"，不该弹提示。
        Assert.Null(AppSettings.LoadWarning);
        Assert.False(AppSettings.LoadDegraded);
    }
}
