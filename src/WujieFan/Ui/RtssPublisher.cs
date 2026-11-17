using System.Drawing;
using WujieFan.Core.Config;
using WujieFan.Core.Ec;
using WujieFan.Core.Providers;

namespace WujieFan.Ui;

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

        // 上一轮写失败把映射扔掉之后必须在这里重建, 否则供数会永久停掉:
        // Apply 只在打开设备/从休眠恢复/改勾选时才跑, 轮询里没人再建。
        if (_aida is not { IsOpen: true } &&
            DateTime.UtcNow.Ticks - _lastTryTicks >= RetryIntervalTicks)
        {
            _lastTryTicks = DateTime.UtcNow.Ticks;
            Apply();
        }

        if (_aida is not { IsOpen: true }) return;

        try
        {
            _aida.Publish(ToSnapshot(sample));
        }
        catch (Exception ex)
        {
            // 写失败说明映射已经不可用, 丢掉, 下一轮在这里重建。
            _aida.Dispose();
            _aida = null;
            _setStatus("向 RTSS 供数失败: " + ex.Message, Theme.Warn);
        }
    }

    private static FanSnapshot ToSnapshot(FanSample s) => new()
    {
        Fans = s.Fans.Select(f => new FanEntry
        {
            Id = $"FAN{f.Index}",
            Label = f.Label,
            ValueText = f.IsValid ? (f.IsStopped ? "0" : f.Rpm.ToString()) : "",
        }).ToArray(),
        Temperature = s.Temperature,
    };

    public void Dispose()
    {
        _aida?.Dispose();
        _aida = null;
    }
}
