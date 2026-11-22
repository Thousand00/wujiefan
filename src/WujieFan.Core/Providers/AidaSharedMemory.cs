using System.Runtime.InteropServices;
using System.Text;

namespace WujieFan.Core.Providers;

/// <summary>
/// 向 RTSS 的 AIDA64 通道供数。
///
/// RTSS 的 OverlayEditor 通过命名共享内存 "AIDA64_SensorValues" 读取外部传感器,
/// 它不校验写入者身份, 因此任何进程创建同名映射并写入约定格式的文本即可。
/// 这些数据会出现在 OverlayEditor 的 "AIDA64" 数据源列表里, 可以像原生数据
/// 一样拖进布局成为图层。
///
/// 格式约束 (来自 RTSS 自带的 AIDA64Parser.cpp 状态机, 必须严格遵守):
///   * 不能有 XML 声明 &lt;?xml ...?&gt; —— 解析器遇到 &lt; 后紧跟 ? 会失败
///   * 不能有注释、属性、自闭合标签 —— 状态机不支持
///   * 不能有根节点包裹 —— 顶层标签就是传感器类型, 出现根节点会让深度判断错位
///   * 文本必须 null 结尾 —— RTSS 侧用 CString strCache = pMapAddr; 直接按字符串读
///
/// 传感器类型决定 RTSS 显示的单位:
///   fan  -> "Fan tachometer" / RPM
///   temp -> "Temperature"    / °C
///   duty -> "Fan speed"      / %
///   volt -> "Voltage"        / V
///   pwr  -> "Power"          / W
///
/// 每个传感器三个子标签: id / label / value。
///   id    = RTSS 侧数据源 ID, 用户在 OverlayEditor 里选中的就是它
///   label = 显示名
///   value = 纯数字。不要带单位后缀 —— RTSS 用 sscanf_s("%f") 解析, 非数字会变成无效值。
/// </summary>
public sealed class AidaSharedMemory : IDisposable
{
    /// <summary>RTSS 与 AIDA64 约定的共享内存名。</summary>
    public const string MappingName = "AIDA64_SensorValues";

    /// <summary>数据源 ID 前缀, 便于用户在列表里识别。</summary>
    public const string IdPrefix = "WUJIE_";

    public const int MappingSize = 64 * 1024;

    /// <summary>
    /// 实际使用的映射名。默认是 RTSS 约定的那个; 测试传入自己的名字,
    /// 以免测试进程写坏正在运行的 WujieFan 发布给 RTSS 的数据 ——
    /// 发布语义的测试不该把用户的覆盖层弄成空白。
    /// </summary>
    private readonly string _mappingName;

    private IntPtr _hMapping = IntPtr.Zero;
    private IntPtr _pView = IntPtr.Zero;
    private bool _disposed;
    private int _lastLength;

    /// <summary>用 RTSS 约定的映射名。</summary>
    public AidaSharedMemory() : this(MappingName) { }

    /// <summary>用指定的映射名 (供测试隔离)。</summary>
    public AidaSharedMemory(string mappingName)
    {
        if (string.IsNullOrWhiteSpace(mappingName))
            throw new ArgumentException("映射名不能为空。", nameof(mappingName));
        _mappingName = mappingName;
    }

    public bool IsOpen => _pView != IntPtr.Zero;
    public string? LastXml { get; private set; }

    /// <summary>创建或打开共享内存。已存在同名映射时复用。</summary>
    public void Open()
    {
        // Dispose 之后这个实例已经没有可用状态, 再 Open 会重新建映射而没人释放它。
        // 与其静默泄漏一个 64KB 的映射视图, 不如让调用方知道它用错了对象。
        if (_disposed) throw new ObjectDisposedException(nameof(AidaSharedMemory));

        if (IsOpen) return;

        // 注意参数顺序: dwMaximumSizeHigh 在前, dwMaximumSizeLow 在后。
        // 写反会请求 64K * 4GB 的映射, 直接报 Win32 错误 8 (内存不足)。
        _hMapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero,
            PageReadWrite, 0, (uint)MappingSize, _mappingName);

        if (_hMapping == IntPtr.Zero)
            throw new AidaException($"CreateFileMapping({_mappingName}) 失败，Win32 错误 {Marshal.GetLastWin32Error()}");

        _pView = MapViewOfFile(_hMapping, FileMapAccess.AllAccess, 0, 0, (UIntPtr)MappingSize);
        if (_pView == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(_hMapping);
            _hMapping = IntPtr.Zero;
            throw new AidaException($"MapViewOfFile 失败，Win32 错误 {err}");
        }
    }

    /// <summary>
    /// 按当前采样写入共享内存。任一风扇无有效读数时该条目的 value 留空。
    ///
    /// 关于撕裂读 (2026-11 实测结论, 勿再尝试 seqlock):
    /// RTSS 侧是 <c>CString s = pMapAddr;</c> 一次性取串, 不读序号也不重读, 所以
    /// seqlock 这类"读者配合"的协议在这里没有意义 —— 序号写得再对, 也没有人会去读它。
    /// 布局同样不能扩展: 标签集合、无根节点、无属性是 RTSS 状态机的硬约束,
    /// 在 64KB 映射的尾部空区放序号能通过编译, 但改变不了 RTSS 看到的任何一个字节。
    ///
    /// 实测的窗口大小 (217 字节, Marshal.Copy 单次): 中位 1.20us / p99 3.70us。
    /// 按 1Hz 发布、RTSS 60fps 读取计算, 每小时约 0.8 次读到撕裂内容。
    /// 实测撕裂内容落在两类, 都不是"错误转速":
    ///   * 结构损坏 (如 &lt;/valulue&gt;) —— RTSS 解析失败, 该帧该源显示无效;
    ///   * 两路传感器分别来自相邻两帧 —— 各自的数值都真实存在过, 只是不是同一时刻。
    /// 在 800/1200 与 800/900 两组对抗性压测 (共 1.6e7 次读) 中, 未出现任何
    /// 从未被写入过的合成数值, 即不会凭空显示一个看似合法的错误转速。
    /// 因此这里保持单次 Marshal.Copy: 它是当前布局下窗口最小的写法, 再加任何
    /// "改进"(如先清零首字节再写回) 反而会把窗口从一个 memcpy 拉长到多次写入。
    /// </summary>
    public void Publish(FanSnapshot snapshot)
    {
        if (!IsOpen) throw new AidaException("共享内存尚未打开。");

        string xml = BuildXml(snapshot);
        LastXml = xml;

        // 空 XML 表示"这一帧没有数据", 不是"把数据清空"。
        // RTSS 侧是一次性读整串的消费者, 看到空串会当作没有更新、保留上一次的值;
        // 若这里照旧零填充写入, 就变成了主动抹掉 —— 消费者丢数值而不是保持。
        // 语义写在这里而不是调用方, 因为调用方有两种 (轮询成功/失败), 都该一致。
        if (xml.Length == 0) return;

        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        int capacity = MappingSize;

        if (bytes.Length + 1 > capacity)
            throw new AidaException($"XML 长度 {bytes.Length} 超出共享内存容量 {capacity}");

        // 用零填充缓冲区一次拷入: 写入本次数据 + 一个 null 结尾,
        // 并顺带把上次较长写入的残留覆盖为零, 避免尾部脏字符。
        int dirty = Math.Max(_lastLength, bytes.Length) + 1;
        byte[] payload = new byte[dirty];
        Array.Copy(bytes, payload, bytes.Length);
        // payload[bytes.Length] 保持 0, 即 null 结尾。
        Marshal.Copy(payload, 0, _pView, dirty);
        _lastLength = bytes.Length;
    }

    /// <summary>生成符合解析器约束的 XML。单独暴露以便单元测试。</summary>
    public static string BuildXml(FanSnapshot s)
    {
        // 完全没有任何数据时整帧不发: RTSS 那边就当作这一帧没有更新。
        // 只要还有东西要发, 温度节点就按"保留节点 + value 留空"的规则处理 (见下)。
        if (s.Fans.Count == 0 && s.Temperature is null) return "";

        var sb = new StringBuilder(512);

        foreach (FanEntry f in s.Fans)
        {
            sb.Append("<fan>");
            sb.Append("<id>").Append(IdPrefix).Append(Escape(f.Id)).Append("</id>");
            sb.Append("<label>").Append(Escape(f.Label)).Append("</label>");
            sb.Append("<value>").Append(Escape(f.ValueText)).Append("</value>");
            sb.Append("</fan>");
        }

        // 温度未知时也要发这个节点, 只是 value 留空 —— 与无效风扇值的处理一致。
        //
        // 原来这里是「温度未知就整块省略」。那样 RTSS 的 overlay 会继续显示上一次
        // 成功读到的温度, 看起来像当前温度其实早就不是了。而温度正是本程序唯一
        // 的安全信号 (过热看门狗靠它), 显示一个过期的数字比显示空白危险得多。
        sb.Append("<temp>");
        sb.Append("<id>").Append(IdPrefix).Append("CPUT</id>");
        sb.Append("<label>CPU Temperature</label>");
        sb.Append("<value>");
        if (s.Temperature is int t) sb.Append(t);
        sb.Append("</value>");
        sb.Append("</temp>");

        return sb.ToString();
    }

    /// <summary>
    /// 转义会破坏 RTSS 那个极简状态机的字符。
    /// 当前写进去的都是固定标签与数字, 但这份 XML 是给别人解析的契约, 不该依赖"恰好没有特殊字符"。
    /// 解析器不支持属性与自闭合标签, 所以只处理文本内容里的三个基本实体。
    /// </summary>
    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { '&', '<', '>' }) < 0) return s;

        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pView != IntPtr.Zero) { UnmapViewOfFile(_pView); _pView = IntPtr.Zero; }
        if (_hMapping != IntPtr.Zero) { CloseHandle(_hMapping); _hMapping = IntPtr.Zero; }
    }

    // ---------------------------------------------------------------- P/Invoke

    private const uint PageReadWrite = 0x04;

    [Flags]
    private enum FileMapAccess : uint
    {
        AllAccess = 0xF001F,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr lpAttributes,
        uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject,
        FileMapAccess dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

public sealed class AidaException : Exception
{
    public AidaException(string message) : base(message) { }
}
