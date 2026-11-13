using System.Runtime.InteropServices;

namespace WujieFan.Core.Ec;

/// <summary>
/// 硬件监控软件之间约定的全局命名互斥量。
///
/// 同一时刻只应有一个进程对 EC / ISA 总线做 I2EC 时序访问:
/// 一次读取由多条端口写组成 (选寄存器 -> 提交 -> 选择 -> 读数据),
/// 两个进程交错执行会打乱时序, 读到错值甚至让 EC 状态异常。
///
/// 名字沿用 HWiNFO / LibreHardwareMonitor / fanprobe 的约定, 以获得互斥效果。
/// 拿不到锁不阻塞太久 —— 只读采样本身还有"高字节读两次"的防撕裂保护。
/// </summary>
public sealed class EcMutex : IDisposable
{
    private static readonly string[] Names =
    {
        @"Global\Access_ISABUS.HTP.Method",
        @"Global\Access_EC",
    };

    private readonly List<IntPtr> _handles = new();
    private readonly List<IntPtr> _lockHandles = new();
    private bool _acquired;

    /// <summary>获取全部互斥量。返回是否全部成功 (部分成功也会持有已拿到的)。</summary>
    public bool Acquire(int timeoutMs = 2000)
    {
        if (_acquired) return true;

        bool all = true;
        foreach (string name in Names)
        {
            IntPtr h = CreateMutex(IntPtr.Zero, false, name);
            if (h == IntPtr.Zero) { all = false; continue; }

            _handles.Add(h);
            uint wait = WaitForSingleObject(h, (uint)timeoutMs);
            if (wait is 0 or 0x80)   // WAIT_OBJECT_0 / WAIT_ABANDONED
                _lockHandles.Add(h);
            else
                all = false;
        }

        _acquired = true;
        return all;
    }

    public void Dispose()
    {
        foreach (IntPtr h in _lockHandles)
        {
            try { ReleaseMutex(h); } catch { }
        }
        _lockHandles.Clear();

        foreach (IntPtr h in _handles)
        {
            try { CloseHandle(h); } catch { }
        }
        _handles.Clear();
        _acquired = false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateMutex(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReleaseMutex(IntPtr hMutex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
