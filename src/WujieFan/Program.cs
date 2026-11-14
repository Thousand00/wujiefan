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
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowWindowEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 主实例还没来得及建事件, 本次启动就当失败退出, 不弹第二个窗口。
            }

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

                try
                {
                    if (form.IsHandleCreated && !form.IsDisposed)
                    {
                        form.BeginInvoke(new Action(form.RestoreFromTray));
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
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
