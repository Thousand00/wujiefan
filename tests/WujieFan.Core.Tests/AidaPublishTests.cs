using System.Reflection;
using System.Runtime.InteropServices;
using WujieFan.Core.Providers;
using Xunit;

namespace WujieFan.Core.Tests;

/// <summary>
/// AIDA 共享内存的发布语义。
///
/// 用注入的映射名而不是 RTSS 约定的那个：测试进程与正在运行的 WujieFan 会共用
/// 同一个命名映射，写它等于替用户的覆盖层做决定 —— 发布语义的回归测试不该把
/// 用户屏幕上正在显示的转速抹成空白。
///
/// 核心行为是"空快照一个字节都不写"。旧实现会零填充写入，把 RTSS 里上一次的
/// 转速/温度抹掉：消费者不是丢一帧，而是直接看不到数。
/// </summary>
public class AidaPublishTests
{
    private static string UniqueMappingName() =>
        "WujieFanTests_Aida_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N");

    /// <summary>取映射视图的首地址。生产类型没有暴露它，测试用反射拿，不为测试扩生产接口。</summary>
    private static IntPtr ViewOf(AidaSharedMemory aida)
    {
        FieldInfo field = typeof(AidaSharedMemory).GetField(
            "_pView", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AidaSharedMemory._pView 字段不存在。");
        return (IntPtr)field.GetValue(aida)!;
    }

    private static FanSnapshot Empty() => new()
    {
        Fans = Array.Empty<FanEntry>(),
        Temperature = null,
    };

    private static FanSnapshot Full() => new()
    {
        Temperature = 55,
        Fans = new[]
        {
            new FanEntry { Id = "fan1", Label = "Fan1", ValueText = "3000" },
        },
    };

    [Fact]
    public void 空快照不写入共享内存()
    {
        using var aida = new AidaSharedMemory(UniqueMappingName());
        aida.Open();

        Assert.True(aida.IsOpen);

        // 先写一帧真实数据，让映射里有一个非零的首字节（'<'）。
        aida.Publish(Full());

        IntPtr view = ViewOf(aida);
        byte before = Marshal.ReadByte(view);
        Assert.NotEqual(0, before);

        aida.Publish(Empty());

        // 空帧 = "这一帧没有数据"，不是"把数据清空"。首字节若被零填充抹掉，
        // RTSS 读到的就是一个空串，用户覆盖层上的数值会凭空消失。
        Assert.Equal("", aida.LastXml);
        Assert.Equal(before, Marshal.ReadByte(view));
    }

    [Fact]
    public void 空快照之后仍然可以发布正常帧()
    {
        using var aida = new AidaSharedMemory(UniqueMappingName());
        aida.Open();

        aida.Publish(Empty());

        aida.Publish(Full());

        // 空帧只是跳过这一次写入，不能把对象弄成不可用状态。
        Assert.Contains("<fan>", aida.LastXml);
        Assert.Contains("3000", aida.LastXml);
        Assert.Contains("<temp>", aida.LastXml);
    }

    [Fact]
    public void Dispose之后Open抛ObjectDisposed()
    {
        var aida = new AidaSharedMemory(UniqueMappingName());
        aida.Open();
        aida.Dispose();

        // 静默重新映射会泄漏一个 64KB 视图，且没人再释放它。
        Assert.Throws<ObjectDisposedException>(() => aida.Open());
    }

    [Fact]
    public void 两个实例用不同映射名_互不影响()
    {
        using var mine = new AidaSharedMemory(UniqueMappingName());
        using var other = new AidaSharedMemory(UniqueMappingName());

        mine.Open();
        other.Open();

        mine.Publish(new FanSnapshot
        {
            Temperature = 61,
            Fans = Array.Empty<FanEntry>(),
        });

        // 这正是测试隔离要保证的：写自己的映射不会污染别人。
        Assert.Null(other.LastXml);
        Assert.Contains("61", mine.LastXml);
    }
}
