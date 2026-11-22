namespace Zephyr.Core.Providers;

/// <summary>供数用的简化快照。与 EC 读取层解耦, 便于单独测试 XML 生成。</summary>
public sealed class FanSnapshot
{
    public IReadOnlyList<FanEntry> Fans { get; init; } = Array.Empty<FanEntry>();
    public int? Temperature { get; init; }
}

public sealed class FanEntry
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";

    /// <summary>要写进 XML 的数值文本。无有效读数时为空串 (RTSS 会显示为无效, 而不是 0)。</summary>
    public string ValueText { get; init; } = "";
}
