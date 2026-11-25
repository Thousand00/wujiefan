using Zephyr.Core.Providers;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// AIDA64 共享内存里那段 XML 的生成。这份 XML 是**给别人解析的契约**:
/// RTSS 的那台极简状态机不支持属性、不支持自闭合标签, 只认这套固定结构。
/// 格式一坏, 叠加层就整块消失, 而程序自己不会报错 —— 所以锁死格式比锁死数值更重要。
/// </summary>
public class AidaXmlTests
{
    private static FanSnapshot TwoFans() => new()
    {
        Fans = new List<FanEntry>
        {
            new() { Id = "FAN1", Label = "Fan1 (CPU)", ValueText = "5615" },
            new() { Id = "FAN2", Label = "Fan2 (GPU)", ValueText = "5342" },
        },
    };

    [Fact]
    public void BuildXml_两个风扇的完整结构()
    {
        string xml = AidaSharedMemory.BuildXml(TwoFans());

        Assert.Equal(
            "<fan><id>ZEPHYR_FAN1</id><label>Fan1 (CPU)</label><value>5615</value></fan>" +
            "<fan><id>ZEPHYR_FAN2</id><label>Fan2 (GPU)</label><value>5342</value></fan>",
            xml);
    }

    [Fact]
    public void BuildXml_没有XML声明与根节点()
    {
        string xml = AidaSharedMemory.BuildXml(TwoFans());

        // RTSS 的解析器直接按 <fan> 顺序扫, 前面多一行 <?xml?> 会让它卡住。
        Assert.DoesNotContain("<?xml", xml);
        Assert.DoesNotContain("<sensors>", xml);
        Assert.StartsWith("<fan>", xml);
    }

    [Fact]
    public void BuildXml_一律带ZEPHYR前缀()
    {
        Assert.Equal("ZEPHYR_", AidaSharedMemory.IdPrefix);
        string xml = AidaSharedMemory.BuildXml(TwoFans());

        // 前缀避免和 AIDA 自己或其它插件的 id 撞车。
        Assert.Equal(2, CountOccurrences(xml, "<fan>"));
        Assert.Equal(2, CountOccurrences(xml, "<fan><id>ZEPHYR_"));
        Assert.Equal(2, CountOccurrences(xml, "<id>ZEPHYR_"));
    }

    [Fact]
    public void BuildXml_不再发温度节点()
    {
        // CPU 温度这个功能已经砍掉: 读它每轮要多跑 12 次驱动往返, 而它的唯一
        // 用途(过热保护)也一并废弃了。这里钉住"不写 temp 节点", 免得以后有人
        // 顺手加回去 —— RTSS 那边一旦发现 ZEPHYR_CPUT 消失, 用户画好的图层会变空。
        string xml = AidaSharedMemory.BuildXml(TwoFans());

        Assert.DoesNotContain("<temp>", xml);
        Assert.DoesNotContain("ZEPHYR_CPUT", xml);
        Assert.DoesNotContain("Temperature", xml);
    }

    [Fact]
    public void BuildXml_空快照返回空串()
    {
        // 一个风扇都没有: 整帧不发, RTSS 那边当作这帧没更新。
        string xml = AidaSharedMemory.BuildXml(new FanSnapshot());
        Assert.Equal("", xml);
    }

    [Fact]
    public void BuildXml_无有效读数时value为空而不是零()
    {
        // 读不到转速时留空, RTSS 会显示为无效; 写 0 会被当成"风扇停了"。
        string xml = AidaSharedMemory.BuildXml(new FanSnapshot
        {
            Fans = new List<FanEntry> { new() { Id = "FAN1", Label = "Fan1 (CPU)", ValueText = "" } },
        });

        Assert.Contains("<value></value>", xml);
        Assert.DoesNotContain("<value>0</value>", xml);
    }

    [Theory]
    [InlineData("a&b", "a&amp;b")]
    [InlineData("a<b", "a&lt;b")]
    [InlineData("a>b", "a&gt;b")]
    [InlineData("<script>", "&lt;script&gt;")]
    public void BuildXml_转义会破坏解析器的三个字符(string raw, string escaped)
    {
        string xml = AidaSharedMemory.BuildXml(new FanSnapshot
        {
            Fans = new List<FanEntry> { new() { Id = "FAN1", Label = raw, ValueText = "1" } },
        });

        Assert.Contains("<label>" + escaped + "</label>", xml);
    }

    [Fact]
    public void BuildXml_转义先处理与号避免二次转义()
    {
        // & 必须第一个替换, 否则 "&lt;" 会变成 "&amp;lt;"。
        string xml = AidaSharedMemory.BuildXml(new FanSnapshot
        {
            Fans = new List<FanEntry> { new() { Id = "FAN1", Label = "&<", ValueText = "1" } },
        });

        Assert.Contains("<label>&amp;&lt;</label>", xml);
        Assert.DoesNotContain("&amp;amp;", xml);
    }

    [Fact]
    public void BuildXml_正常标签不被转义()
    {
        string xml = AidaSharedMemory.BuildXml(TwoFans());
        Assert.Contains("<label>Fan1 (CPU)</label>", xml);   // 括号与空格无需转义
    }

    [Fact]
    public void BuildXml_远小于共享内存上限()
    {
        // MappingSize 是 64KB; 一次采样几百字节。真要逼近上限说明标签里混进了别的东西。
        string xml = AidaSharedMemory.BuildXml(TwoFans());
        Assert.True(xml.Length < 1024, $"XML 长度 {xml.Length} 异常");
        Assert.Equal(64 * 1024, AidaSharedMemory.MappingSize);
    }

    [Fact]
    public void BuildXml_输出为UTF8可表示字符()
    {
        string xml = AidaSharedMemory.BuildXml(new FanSnapshot
        {
            Fans = new List<FanEntry> { new() { Id = "FAN1", Label = "风扇1", ValueText = "5615" } },
        });

        // 写入共享内存时按 UTF-8 编码并以 \0 结尾; 中文标签必须能往返。
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        Assert.Equal(xml, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Contains("风扇1", xml);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
