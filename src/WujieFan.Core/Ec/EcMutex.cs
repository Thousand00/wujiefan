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

    /// <summary>
    /// 实际使用的锁名。默认是上面那两个业界约定的名字; 测试传入自己的名字,
    /// 以免测试进程与真实的 HWiNFO / LHM / 正在运行的 WujieFan 抢同一把锁 ——
    /// 一条断言失败就顺手把 EC 访问锁死, 是测试最不该付出的代价。
    /// </summary>
    private readonly string[] _names;

    private readonly List<IntPtr> _handles = new();

    /// <summary>用默认的业界约定名。</summary>
    public EcMutex() : this(Names) { }

    /// <summary>用指定的锁名 (供测试隔离)。</summary>
    public EcMutex(IReadOnlyList<string> names)
    {
        if (names.Count == 0) throw new ArgumentException("至少要给一个锁名。", nameof(names));
        _names = names.ToArray();
    }

    /// <summary>
    /// 本次已拿到的锁, 连同**拿它的那个线程**与**重入深度**。
    ///
    /// 必须记线程: Win32 互斥量的归属是线程而不是进程, 只有 Wait 成功的那个线程
    /// 调 ReleaseMutex 才算数。早先只记句柄, 于是
    ///   线程 A Acquire → 线程 B Acquire(以为已持有, 跳过 Wait, 直接进临界区)
    /// B 进临界区时其实没有任何锁保护, 交错时序照样发生; 更糟的是 B 的 Release
    /// 会失败 (B 不是持有者), 而旧代码失败后仍把句柄从 _owned 拿走, 那把锁
    /// 自此再也没人还, HWiNFO / LHM 读不到 EC。
    ///
    /// 必须记深度: 同一线程重入时我们没有再 Wait (Win32 的 Wait 是重入计数, 而
    /// Release 只调一次会留下一个计数永不归还), 于是内层 Release 会把外层的锁
    /// 直接还掉 —— 外层还以为自己在临界区里, 实际上已经被别人进来了。
    /// 当前调用图没有嵌套, 但这属于公开类型的契约, 不能靠"现在没人这么用"维系。
    /// </summary>
    private readonly List<OwnedLock> _owned = new();

    /// <summary>一把归属于某线程的锁, 以及本线程重入了几层。</summary>
    private sealed class OwnedLock
    {
        public OwnedLock(IntPtr handle, int threadId)
        {
            Handle = handle;
            ThreadId = threadId;
            Depth = 1;
        }

        public IntPtr Handle { get; }
        public int ThreadId { get; }
        public int Depth { get; set; }
    }

    private bool _disposed;

    /// <summary>互斥量句柄只创建一次, 之后反复 Acquire/Release 复用。</summary>
    private bool EnsureHandles()
    {
        // 从已建好的数量续建: 中途失败时不能从头再来, 否则同一把锁会拿到两个句柄。
        for (int i = _handles.Count; i < _names.Length; i++)
        {
            IntPtr h = CreateMutex(IntPtr.Zero, false, _names[i]);
            if (h == IntPtr.Zero) return false;
            _handles.Add(h);
        }

        return _handles.Count == _names.Length;
    }

    /// <summary>
    /// 获取全部互斥量。只有**全部拿到**才返回 true。
    ///
    /// 部分成功 (拿到了其中一把、另一把超时) 时, 已经把到手的那些原样还回去,
    /// 然后返回 false —— 调用方看到 false 就可以放心走失败分支, 不需要再 Release。
    ///
    /// 这一点至关重要: 早先的实现留着部分持有的句柄返回 false, 调用方据此跳过 Release,
    /// 结果 Global\Access_ISABUS 被本进程长期占住, HWiNFO / LHM 再也读不到 EC;
    /// 更糟的是 _owned 里留着句柄, 下一次 Acquire 会因为"已持有"而跳过 Wait,
    /// 让本实例此后永远以为自己拿着锁。
    ///
    /// 调用方应当在**每次 I2EC 访问前后**成对调用 Acquire/Release, 而不是一直持锁:
    /// 一把锁被某个进程长期占住, HWiNFO / LibreHardwareMonitor 这些同样守约定的软件
    /// 就再也读不到 EC, 那已经超出"互斥"的本意, 变成独占了。
    /// </summary>
    public bool Acquire(int timeoutMs = 250)
    {
        if (_disposed) return false;

        if (!EnsureHandles()) return false;

        // 记下进来时每把锁的深度。失败回滚只撤销**本次调用新增**的那些,
        // 不能像旧实现那样 Release() 一把全还 —— 那会把外层的持有也一并还掉。
        int[] depthBefore = new int[_handles.Count];
        for (int i = 0; i < _handles.Count; i++)
            depthBefore[i] = DepthOf(_handles[i]);

        foreach (IntPtr h in _handles)
        {
            // 已由**本线程**持有的不重复 Wait: 同一线程对同一互斥量再次 Wait 是重入计数,
            // 而 Release 只调一次就会留下一个计数没还, 那把锁之后就永远归本进程了。
            // 换成别的线程时不能跳过 —— 那把锁的归属是原来那个线程, 本线程进去等于没锁。
            OwnedLock? mine = OwnedByCurrentThread(h);
            if (mine is not null)
            {
                mine.Depth++;
                continue;
            }

            uint wait = WaitForSingleObject(h, (uint)timeoutMs);
            if (wait is 0 or 0x80)   // WAIT_OBJECT_0 / WAIT_ABANDONED
            {
                _owned.Add(new OwnedLock(h, Environment.CurrentManagedThreadId));
                continue;
            }

            // 拿不全就一把都不留, 避免"半持有"这种既锁不住别人、又占着锁的状态。
            RollbackTo(depthBefore);
            return false;
        }

        return true;
    }

    /// <summary>本线程在这把锁上的重入深度, 没持有则为 0。</summary>
    private int DepthOf(IntPtr h)
    {
        int me = Environment.CurrentManagedThreadId;
        foreach (OwnedLock owned in _owned)
            if (owned.Handle == h && owned.ThreadId == me) return owned.Depth;
        return 0;
    }

    /// <summary>把本线程在各把锁上的深度退回到 <paramref name="depthBefore"/> 记录的值。</summary>
    private void RollbackTo(int[] depthBefore)
    {
        for (int i = 0; i < _handles.Count && i < depthBefore.Length; i++)
        {
            IntPtr h = _handles[i];
            while (true)
            {
                OwnedLock? mine = OwnedByCurrentThread(h);
                if (mine is null || mine.Depth <= depthBefore[i]) break;
                if (!ReleaseOne(mine)) break;
            }
        }
    }

    /// <summary>这把锁是不是当前线程拿的; 是则返回记录, 否则 null。</summary>
    private OwnedLock? OwnedByCurrentThread(IntPtr h)
    {
        int me = Environment.CurrentManagedThreadId;
        foreach (OwnedLock owned in _owned)
            if (owned.Handle == h && owned.ThreadId == me) return owned;
        return null;
    }

    /// <summary>把本线程的持有深度减一; 深度归零才真正 ReleaseMutex。成功返回 true。</summary>
    private bool ReleaseOne(OwnedLock owned)
    {
        if (owned.Depth > 1)
        {
            owned.Depth--;
            return true;
        }

        try
        {
            if (!ReleaseMutex(owned.Handle)) return false;
        }
        catch
        {
            return false;
        }

        _owned.Remove(owned);
        return true;
    }

    /// <summary>释放本次 Acquire 拿到的锁。可以安全地重复调用。</summary>
    public void Release()
    {
        int me = Environment.CurrentManagedThreadId;

        // 复制一份再遍历: ReleaseOne 会从 _owned 里移除自己。
        foreach (OwnedLock owned in _owned.ToArray())
        {
            // 不是自己的锁不能在这里还: ReleaseMutex 会失败, 而失败恰好说明
            // "这把锁还归别的线程", 记录必须留着等它自己 Release。
            // 旧代码无条件 Clear, 于是那个线程再也没有机会还锁, 锁就锁死了。
            if (owned.ThreadId != me) continue;

            ReleaseOne(owned);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release();

        foreach (IntPtr h in _handles)
        {
            try { CloseHandle(h); } catch { }
        }
        _handles.Clear();
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
