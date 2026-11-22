using Zephyr.Core.Ec;
using Xunit;

namespace Zephyr.Core.Tests;

/// <summary>
/// EC 互斥量的归属与重入语义。
///
/// 这个类是"不给别的监控软件添麻烦"的唯一闸门，出错的方式都很隐蔽：
/// 锁没还干净，HWiNFO / LibreHardwareMonitor 从此读不到 EC，而本程序一切正常 ——
/// 用户只会发现"装了 Zephyr 之后 AIDA 读不到风扇了"，却没有任何日志指向这里。
///
/// 两个测试约定：
///   1. 用注入的锁名而不是默认的 Global\Access_* —— 一条断言失败不该顺手把真实的
///      EC 访问锁死。名字带进程 id 与随机后缀，同机并发跑多个测试进程也互不干扰。
///   2. Win32 互斥量的归属是**线程**：Acquire 与 Release 必须在同一个线程上完成。
///      所以每个场景整体跑在一个线程池线程里（用 <see cref="OnOneThread{T}(Func{T})"/>），
///      绝不在 await 前后分别取放 —— 那会换线程，Release 必然失败。
/// </summary>
public class EcMutexTests
{
    private static string[] UniqueNames() => new[]
    {
        @"Local\ZephyrTests_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N"),
        @"Local\ZephyrTests_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N"),
    };

    /// <summary>把整个场景放到一个线程池线程上同步跑完，保证取锁与还锁同线程。</summary>
    private static T OnOneThread<T>(Func<T> body) => Task.Run(body).GetAwaiter().GetResult();

    /// <summary>
    /// 在一个**独占的新线程**上试着拿锁；拿到立刻还，返回是否拿到。
    ///
    /// 这里必须用 new Thread 而不是 Task.Run：线程池可能把工作项安排到调用者
    /// 正在使用的那个线程上（或直接内联执行），而 Win32 互斥量对**同一线程**
    /// 是可递归的 —— 同一个线程去 Wait 自己已持有的锁会立刻成功。那样测出来的
    /// "别人也能拿到"其实是自己拿到了，断言会随机通过/失败。
    /// </summary>
    private static bool TryAcquireElsewhere(string[] names, int timeoutMs)
    {
        bool got = false;
        int callerThread = Environment.CurrentManagedThreadId;
        var thread = new Thread(() =>
        {
            // 线程号必须真的不同，否则这条断言测的是递归而不是互斥。
            Assert.NotEqual(callerThread, Environment.CurrentManagedThreadId);

            using var other = new EcMutex(names);
            got = other.Acquire(timeoutMs);
            if (got) other.Release();
        })
        {
            IsBackground = true,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "竞争线程没有在 10 秒内结束。");
        return got;
    }

    [Fact]
    public void 正常取用与释放()
    {
        Assert.True(OnOneThread<int>(() =>
        {
            using var mutex = new EcMutex(UniqueNames());

            if (!mutex.Acquire(1000)) return 1;
            mutex.Release();

            // 还掉之后必须还能再拿一次 —— 否则一次成功就把锁永久占住了。
            if (!mutex.Acquire(1000)) return 2;
            mutex.Release();
            return 0;
        }) == 0);
    }

    [Fact]
    public void 释放可以多调一次而不出错()
    {
        Assert.True(OnOneThread<int>(() =>
        {
            using var mutex = new EcMutex(UniqueNames());

            if (!mutex.Acquire(1000)) return 1;
            mutex.Release();
            mutex.Release();   // 幂等: 调用方在 finally 里重复还锁不该炸

            if (!mutex.Acquire(1000)) return 2;
            mutex.Release();
            return 0;
        }) == 0);
    }

    /// <summary>
    /// 重入的核心断言：内层释放不能把外层的锁还掉。
    ///
    /// 旧实现里同线程第二次 Acquire 会跳过 Wait（避免 Win32 重入计数泄漏），
    /// 但释放没有计数，于是内层 Release 直接 ReleaseMutex —— 外层还以为自己
    /// 在临界区里，实际上别人已经能进来了。这里用"另一个线程拿不到"来证明
    /// 外层仍然持有，是从行为上验证，不依赖内部字段。
    /// </summary>
    [Fact]
    public void 同线程重入后_内层释放不影响外层持有()
    {
        string[] names = UniqueNames();

        int step = OnOneThread<int>(() =>
        {
            using var outer = new EcMutex(names);

            if (!outer.Acquire(1000)) return 1;
            if (!outer.Acquire(1000)) return 2;   // 重入: 深度 2

            outer.Release();                       // 只退一层, 外层仍应持有

            // 别的线程此时必须拿不到: 外层还在临界区里。
            if (TryAcquireElsewhere(names, 200)) return 3;

            outer.Release();                       // 再释放一次才算真的还干净
            return 0;
        });

        Assert.Equal(0, step);

        // 还干净之后，别的线程必须能拿到。
        Assert.True(TryAcquireElsewhere(names, 1000));
    }

    [Fact]
    public void 别的线程持有期间_本线程拿不到()
    {
        string[] names = UniqueNames();

        using var held = new ManualResetEventSlim(false);
        using var done = new ManualResetEventSlim(false);

        // 占锁线程也用独占线程，理由同 TryAcquireElsewhere：
        // 线程池可能把"占锁"和"试探"安排到同一个线程上，而同一线程对同一把
        // Win32 互斥量是可递归的，试探会立刻成功，测试就失去了意义。
        var holder = new Thread(() =>
        {
            using var other = new EcMutex(names);
            if (!other.Acquire(1000)) return;
            held.Set();
            done.Wait(TimeSpan.FromSeconds(10));
            other.Release();
        })
        {
            IsBackground = true,
        };

        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)), "占锁线程没有拿到锁。");

        Assert.False(TryAcquireElsewhere(names, 200), "别人还持有时不该拿到锁。");

        done.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(10)), "占锁线程没有在 10 秒内退出。");

        // 对方还掉之后必须能拿到: 否则说明锁归还有问题。
        Assert.True(TryAcquireElsewhere(names, 1000));
    }

    [Fact]
    public void 释放后重新创建实例_名字相同则互斥()
    {
        // 互斥是按名字在内核对象上生效的，不是按 .NET 实例。
        string[] names = UniqueNames();

        Assert.True(OnOneThread<int>(() =>
        {
            using (var first = new EcMutex(names))
            {
                if (!first.Acquire(1000)) return 1;
                first.Release();
            }

            using var second = new EcMutex(names);
            if (!second.Acquire(1000)) return 2;
            second.Release();
            return 0;
        }) == 0);
    }

    [Fact]
    public void 释放之后再取用_不残留旧句柄()
    {
        string[] names = UniqueNames();

        Assert.True(OnOneThread<int>(() =>
        {
            using var mutex = new EcMutex(names);

            // 拿放若干轮，最后仍要能拿到：如果 _owned 里堆着已经还给系统的记录，
            // Acquire 会以为"已持有"跳过 Wait，从而在没有任何锁的情况下进临界区。
            for (int i = 0; i < 5; i++)
            {
                if (!mutex.Acquire(1000)) return i + 1;
                mutex.Release();
            }

            return 0;
        }) == 0);

        Assert.True(TryAcquireElsewhere(names, 1000));
    }
}
