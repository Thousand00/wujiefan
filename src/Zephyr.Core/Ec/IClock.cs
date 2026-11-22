namespace Zephyr.Core.Ec;

/// <summary>
/// 取当前时间。抽出来的唯一理由是采样时间戳: 测试要断言"这次采样是几点的",
/// 而 <see cref="DateTime.Now"/> 不可控, 断言只能放宽到"在某个区间内", 那就等于没断言。
///
/// 只暴露 <see cref="Now"/>: 本项目的时间戳全部是给人看的本地时间,
/// 没有需要 UTC / 单调时钟的地方。真出现超时判定要单调时钟时再扩, 不要提前设计。
/// </summary>
public interface IClock
{
    DateTime Now { get; }
}

/// <summary>
/// 默认实现, 直接取系统本地时间。行为与抽取前逐字一致。
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>共享实例。无状态, 没必要每个 FanReader 造一个。</summary>
    public static readonly SystemClock Instance = new();

    public DateTime Now => DateTime.Now;
}
