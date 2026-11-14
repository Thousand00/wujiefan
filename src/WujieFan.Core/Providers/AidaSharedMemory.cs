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

    private IntPtr _hMapping = IntPtr.Zero;
    private IntPtr _pView = IntPtr.Zero;
    private bool _disposed;
    private int _lastLength;

    public bool IsOpen => _pView != IntPtr.Zero;
    public string? LastXml { get; private set; }

    /// <summary>创建或打开共享内存。已存在同名映射时复用。</summary>
    public void Open()
    {
        if (IsOpen) return;

        // 注意参数顺序: dwMaximumSizeHigh 在前, dwMaximumSizeLow 在后。
        // 写反会请求 64K * 4GB 的映射, 直接报 Win32 错误 8 (内存不足)。
        _hMapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero,
            PageReadWrite, 0, (uint)MappingSize, MappingName);

        if (_hMapping == IntPtr.Zero)
            throw new AidaException($"CreateFileMapping({MappingName}) 失败，Win32 错误 {Marshal.GetLastWin32Error()}");

        _pView = MapViewOfFile(_hMapping, FileMapAccess.AllAccess, 0, 0, (UIntPtr)MappingSize);
        if (_pView == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(_hMapping);
            _hMapping = IntPtr.Zero;
            throw new AidaException($"MapViewOfFile 失败，Win32 错误 {err}");
        }
    }

    /// <summary>按当前采样写入共享内存。任一风扇无有效读数时该条目的 value 留空。</summary>
    public void Publish(FanSnapshot snapshot)
    {
        if (!IsOpen) throw new AidaException("共享内存尚未打开。");

        string xml = BuildXml(snapshot);
        LastXml = xml;

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
        var sb = new StringBuilder(512);

        foreach (FanEntry f in s.Fans)
        {
            sb.Append("<fan>");
            sb.Append("<id>").Append(IdPrefix).Append(Escape(f.Id)).Append("</id>");
            sb.Append("<label>").Append(Escape(f.Label)).Append("</label>");
            sb.Append("<value>").Append(Escape(f.ValueText)).Append("</value>");
            sb.Append("</fan>");
        }

        if (s.Temperature is int t)
        {
            sb.Append("<temp>");
            sb.Append("<id>").Append(IdPrefix).Append("CPUT</id>");
            sb.Append("<label>CPU Temperature</label>");
            sb.Append("<value>").Append(t).Append("</value>");
            sb.Append("</temp>");
        }

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
