using System.Drawing;
using Zephyr.Core.Config;
using Zephyr.Core.Ec;
using Zephyr.Core.Providers;

namespace Zephyr.Ui;

/// <summary>
/// 向 RTSS / AIDA64 共享内存供数。
///
/// 这块职责很小但状态不少: 映射可能根本没建 (设备没打开)、建了又失效 (AIDA64 退出重开)、
/// 用户勾掉又勾回来。所有"什么时候该建、什么时候该丢"的规则都收在这里, 免得散在
/// 轮询、休眠恢复、勾选框三个入口各写一遍且慢慢走样。
/// </summary>
internal sealed class RtssPublisher : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string, Color> _setStatus;

    private AidaSharedMemory? _aida;

    /// <summary>
    /// 上一次成功发布时的风扇身份 (Id + 标签)。
    ///
    /// 读失败时要发布一帧"值全空"的数据, 而那时手里没有 FanSample, 只能靠这份记忆
    /// 还原出 RTSS 认识的槽位。没有它就只能整帧不发, 覆盖层会一直停在最后一次成功
    /// 读到的温度和转速 —— 那是安全感知上最不该出现的"静默陈旧"。
    /// </summary>
    private IReadOnlyList<FanEntry>? _lastFanEntries;

    /// <summary>上一次尝试重建 AIDA64 共享内存的刻度, 用来给重建加退避。</summary>
    private long _lastTryTicks;

    /// <summary>
    /// 共享内存重建的最小间隔。失败时轮询每秒都会想重建一次, 没有退避就会
    /// 每秒把同一条错误写进状态栏, 看起来像程序在抽搐。
    /// </summary>
    private static readonly long RetryIntervalTicks = TimeSpan.FromSeconds(10).Ticks;

    public RtssPublisher(AppSettings settings, Action<string, Color> setStatus)
    {
        _settings = settings;
        _setStatus = setStatus;
    }

    /// <summary>共享内存是否已就绪。轮询里的退避判断要用。</summary>
    public bool IsOpen => _aida is { IsOpen: true };

    /// <summary>
    /// 按设置开/关共享内存。幂等 —— 打开设备、休眠恢复、重连、改勾选都会调它。
    /// </summary>
    public void Apply()
    {
        if (_settings.PublishToRtss)
        {
            _lastTryTicks = DateTime.UtcNow.Ticks;
            try
            {
                _aida ??= new AidaSharedMemory();
                _aida.Open();
            }
            catch (Exception ex)
            {
                _lastTryTicks = DateTime.UtcNow.Ticks;

                // Open() 抛异常时 _aida 已经是一个"存在但没打开"的实例。
                // 不扔掉它, 轮询每秒都会 Publish 一次并抛 "共享内存尚未打开",
                // 状态栏就被这条假错误一直刷屏, 真正的失败原因反而看不见了。
                _aida?.Dispose();
                _aida = null;
                _setStatus("打不开 AIDA64 共享内存: " + ex.Message, Theme.Warn);
            }
        }
        else
        {
            _aida?.Dispose();
            _aida = null;
        }
    }

    /// <summary>
    /// 每次成功采样后调一次: 需要时重建映射, 然后把这一轮数据推出去。
    /// 顺序与拆分前一致 —— 先退避重建, 再发布。
    /// </summary>
    public void Publish(FanSample sample)
    {
        if (!_settings.PublishToRtss) return;

        if (EnsureOpen() is not { } aida) return;

        try
        {
            FanSnapshot snapshot = ToSnapshot(sample);
            aida.Publish(snapshot);
        }
        catch (Exception ex)
        {
            // 写失败说明映射已经不可用, 丢掉, 下一轮在这里重建。
            aida.Dispose();
            _aida = null;
            _setStatus("向 RTSS 供数失败: " + ex.Message, Theme.Warn);
        }
    }

    /// <summary>
    /// 读失败时调一次: 把"没有数据"这件事明确推给消费端。
    ///
    /// 不发帧的话, 覆盖层会一直显示上一次读到的温度和转速 —— EC 掉线、休眠后句柄失效
    /// 这些情况下, 用户看到的是一个稳定但已经完全失真的数字, 比显示空白危险得多。
    /// 这里发的是"槽位都在、值全空"的一帧: RTSS 那边显示成空/N/A, 而不是旧值。
    ///
    /// 还没成功发布过任何一帧时 (刚启动就失败) 手里没有槽位, 什么都不发 ——
    /// 那时覆盖层本来也没有旧值可清。
    /// </summary>
    public void PublishUnavailable()
    {
        if (!_settings.PublishToRtss) return;
        if (EnsureOpen() is not { } aida) return;
        if (_lastFanEntries is null) return;

        try
        {
            aida.Publish(new FanSnapshot
            {
                Fans = _lastFanEntries.Select(f => new FanEntry
                {
                    Id = f.Id,
                    Label = f.Label,
                    ValueText = "",
                }).ToArray(),
                Temperature = null,
            });
        }
        catch (Exception ex)
        {
            aida.Dispose();
            _aida = null;
            _setStatus("向 RTSS 供数失败: " + ex.Message, Theme.Warn);
        }
    }

    /// <summary>
    /// 确认共享内存可用, 必要时按退避重建。两条发布路径都先过这里。
    ///
    /// 为什么不能只在 <see cref="Publish"/> 里重建: 写失败会把 _aida 扔掉, 而重建
    /// 原先只挂在"成功采样"这条路上。一旦 EC 也同时读不到, Publish 根本不会被调用,
    /// PublishUnavailable 又直接 return —— 供数就永久停了, 直到某次采样意外成功。
    /// 两条路各写一份重建逻辑迟早会走样, 所以收成一个入口。
    ///
    /// 返回实例而不是 bool, 是为了让调用方拿到非空引用后, 编译器能据此做流分析。
    /// </summary>
    private AidaSharedMemory? EnsureOpen()
    {
        if (_aida is { IsOpen: true }) return _aida;

        // Apply 只在打开设备/从休眠恢复/改勾选时才跑, 轮询里没人再建, 所以这里必须能自建。
        if (DateTime.UtcNow.Ticks - _lastTryTicks >= RetryIntervalTicks)
        {
            _lastTryTicks = DateTime.UtcNow.Ticks;
            Apply();
        }

        return _aida is { IsOpen: true } ? _aida : null;
    }

    private FanSnapshot ToSnapshot(FanSample s)
    {
        var snapshot = new FanSnapshot
        {
            Fans = s.Fans.Select(f => new FanEntry
            {
                Id = $"FAN{f.Index}",
                Label = f.Label,
                ValueText = f.IsValid ? (f.IsStopped ? "0" : f.Rpm.ToString()) : "",
            }).ToArray(),
            Temperature = s.Temperature,
        };

        // 只在有槽位时记住它。空集合交给下面那句判空挡住, 免得把"上次的槽位"
        // 覆盖成空的, 让之后的失败帧再也没东西可发。
        if (snapshot.Fans.Count > 0) _lastFanEntries = snapshot.Fans;
        return snapshot;
    }

    public void Dispose()
    {
        _aida?.Dispose();
        _aida = null;
    }
}
