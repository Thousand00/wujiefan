using WujieFan.Ui;

namespace WujieFan;

internal static class Program
{
    /// <summary>单实例互斥量。程序会在开机自启动与手动启动之间重复拉起, 必须只留一个。</summary>
    private const string SingleInstanceMutexName = @"Global\WujieFan_SingleInstance";

    /// <summary>唤醒事件名。二次启动的进程 Set 一下就走, 主实例的后台线程负责把窗口拽回前台。</summary>
    private const string ShowWindowEventName = @"Global\WujieFan_ShowWindow";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out bool isFirst);

        if (!isFirst)
        {
            // 已经有实例在跑: 用命名事件让它把窗口带到前台, 自己退出。
            // 走事件而不是 PostMessage(HWND_BROADCAST): 两个进程都是提权的, UIPI 会拦下广播消息。
            //
            // 事件是主实例在 Application.Run 之前建的。互斥量已经存在、事件却还没建好,
            // 这个窗口期虽然只有几十毫秒, 但开机时计划任务和用户双击撞在一起就会踩上。
            // 那时候直接退出, 用户看到的就是"双击了没反应"。所以这里等一下再放弃。
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowWindowEventName);
                    ev.Set();
                    return;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(100);
                }
            }

            // 等了一秒还是没有: 主实例可能正卡在启动阶段, 本次启动安静退出, 不弹第二个窗口。
            return;
        }

        ApplicationConfiguration.Initialize();

        bool minimize = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var form = new MainForm(minimize);
        StartWakeListener(form);

        Application.Run(form);
    }

    /// <summary>后台线程等唤醒事件, 收到就把窗口拽出来。用 Invoke 回到 UI 线程。</summary>
    private static void StartWakeListener(MainForm form)
    {
        // 先把事件建出来再让第二个实例有机会进来 —— 它的重试循环最多等 1 秒。
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var closed = new ManualResetEvent(false);

        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { wake, closed };
            while (true)
            {
                int index;
                try { index = WaitHandle.WaitAny(handles); }
                catch { return; }

                if (index != 0)
                {
                    return;
                }

                // 不能用"没有句柄就跳过"的写法: 窗口还没创建时收到唤醒, 那次唤醒就白丢了,
                // 用户双击 exe 看到的是"什么也没发生"。这里等句柄建好再处理。
                // 早到的唤醒只有开机那一下子才会碰到, 等一个很短的时间就够了。
                for (int spin = 0; spin < 50 && !form.IsDisposed && !form.IsHandleCreated; spin++)
                {
                    Thread.Sleep(20);
                }

                try
                {
                    if (!form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke(new Action(form.RestoreFromTray));
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 窗口在等待期间被关掉了, 监听线程到此为止。
                    return;
                }
                catch (InvalidOperationException)
                {
                    // 句柄在检查与 BeginInvoke 之间被销毁。这是一次错过, 不是结束 ——
                    // 直接 return 会把这个线程永久关掉, 之后第二次启动就再也叫不回窗口了。
                    continue;
                }
            }
        })
        { IsBackground = true, Name = "WujieFanWake" };

        thread.Start();

        form.FormClosed += (_, _) =>
        {
            try { closed.Set(); } catch { }
            try { wake.Close(); } catch { }
        };
    }
}
