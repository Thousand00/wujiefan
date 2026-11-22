using WujieFan.Core.Ec;

namespace WujieFan.Core.Tests;

/// <summary>
/// 内存里的假 EC。记录每一次写入的顺序与取值, 并让读取返回"刚写进去的值",
/// 这样 <see cref="FanReader"/> 写完之后的回读确认也会得到真实的结果。
///
/// 刻意实现 <see cref="IEcTransport"/> 而不是继承什么基类: 生产代码只认这个接口,
/// 假实现越薄, 它与真实实现的差异就越少 —— 差异正是"测试通过但实机不对"的来源。
/// </summary>
internal sealed class FakeEc : IEcTransport
{
    private readonly Dictionary<ushort, int> _regs = new();

    /// <summary>按调用先后记录的写入 (地址, 值)。</summary>
    public List<(ushort Address, int Value)> Writes { get; } = new();

    /// <summary>按调用先后记录的读取地址。</summary>
    public List<ushort> Reads { get; } = new();

    /// <summary>
    /// 把读和写放进同一条时间线。Reads/Writes 两个独立列表看不出交错顺序,
    /// 于是"快照必须在写入之前"这类断言写出来是假的 —— 把快照挪到写之后照样绿。
    /// 需要判先后的时候用这个。
    /// </summary>
    public List<(bool IsRead, ushort Address, int Value)> Ops { get; } = new();

    /// <summary>这些地址的写入会抛异常, 模拟 EC 拒绝/总线错误。</summary>
    public HashSet<ushort> FailWritesOn { get; } = new();

    /// <summary>这些地址只在第一次写入时失败 (用于验证"失败一次、回滚成功"的路径)。</summary>
    public HashSet<ushort> FailFirstWriteOnlyOn { get; } = new();

    /// <summary>
    /// 这些地址的写入会被"吞掉": 调用不抛异常, 但寄存器值不变。
    /// 真实 EC 有时候就是这样 —— 收下了时序但没改值, 只能靠回读发现。
    /// </summary>
    public HashSet<ushort> SwallowWritesOn { get; } = new();


    private readonly HashSet<ushort> _alreadyFailedOnce = new();

    /// <summary>这些地址的读取会抛异常, 模拟回读时总线出错/EC 无响应。</summary>
    public HashSet<ushort> FailReadsOn { get; } = new();

    /// <summary>
    /// 为 true 时, <see cref="FailReadsOn"/> 只有在发生过至少一次写入之后才生效。
    /// 用来造"四个写入全部成功、只有回读那一下失败"的场景 ——
    /// 写入前的快照读取必须正常, 否则测到的是快照阶段就失败, 不是回读失败。
    /// </summary>
    public bool FailReadsOnlyAfterWrites { get; set; }

    /// <summary>Tach 计数值, 供 Read16Stable 返回。</summary>
    public int TachCount { get; set; } = 4000;

    /// <summary>Read16Stable 的 stable 标志。</summary>
    public bool Stable { get; set; } = true;

    public int Read(ushort address)
    {
        Reads.Add(address);
        Ops.Add((true, address, 0));
        if (FailReadsOn.Contains(address) && (!FailReadsOnlyAfterWrites || Writes.Count > 0))
            throw new EcException($"模拟回读失败 0x{address:X4}");
        return _regs.TryGetValue(address, out int v) ? v : 0;
    }

    public void Write(ushort address, byte value)
    {
        if (FailWritesOn.Contains(address))
            throw new EcException($"模拟 EC 拒绝写入 0x{address:X4}");

        if (FailFirstWriteOnlyOn.Contains(address) && _alreadyFailedOnce.Add(address))
            throw new EcException($"模拟 EC 首次写入 0x{address:X4} 失败");

        Writes.Add((address, value));
        Ops.Add((false, address, value));
        if (!SwallowWritesOn.Contains(address)) _regs[address] = value;
    }

    public int Read16Stable(ushort loAddress, ushort hiAddress, out bool stable)
    {
        unstableCalls++;
        stable = Stable;
        return TachCount;
    }

    public int unstableCalls;

    /// <summary>预置寄存器初值 (不经 Write, 不产生写入记录)。</summary>
    public FakeEc With(ushort address, int value)
    {
        _regs[address] = value;
        return this;
    }

    public int Reg(ushort address) => _regs.TryGetValue(address, out int v) ? v : 0;
}
