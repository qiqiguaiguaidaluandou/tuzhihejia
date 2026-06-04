using System.Windows.Threading;
using TZHJ.App.Services;
using TZHJ.Infrastructure.Sync;

namespace TZHJ.App.Services;

/// <summary>
/// 会话内取数调度（登录态触发的真正落地）。登录成功后 <see cref="Start"/>：
///   · 立即补一轮 = **登录补拉**（补离线期间已关闭的窗）；
///   · 之后每 120s 轮询一次 = **会话内定时触发**（补在线期间新关闭的窗）。
/// 两者与手动补拉共用 <see cref="BatchSyncService"/>；幂等故轮询安全。拉到新批次弹 Toast
/// （列表刷新由 BatchListViewModel 的 FileSystemWatcher 负责）。覆盖操作员 AllowedFlows 的各流程。
/// </summary>
public sealed class SessionSyncScheduler
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(120);

    private readonly ISession _session;
    private readonly BatchSyncService _sync;
    private readonly IDialogService _dialog;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public SessionSyncScheduler(ISession session, BatchSyncService sync, IDialogService dialog)
    {
        _session = session;
        _sync = sync;
        _dialog = dialog;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await RunPassAsync();
    }

    /// <summary>登录成功后调用：立即补一轮（登录补拉）并开启 120s 轮询（会话内定时）。</summary>
    public void Start()
    {
        if (!_session.IsAuthenticated) return;
        _timer.Start();
        _ = RunPassAsync(isInitial: true); // 立即一轮 = 登录补拉（后台，不卡界面，缓冲 1min）
    }

    public void Stop() => _timer.Stop();

    private async Task RunPassAsync(bool isInitial = false)
    {
        if (_busy || !_session.IsAuthenticated) return; // 并发护栏：上一轮没跑完就跳过本跳
        _busy = true;
        try
        {
            var emp = _session.Operator.EmployeeId;
            var newCount = 0;
            var delayMinutes = isInitial ? 1 : 30;

            foreach (var flow in _session.Operator.AllowedFlows)
            {
                try
                {
                    var windows = _session.Config.WindowsFor(flow);
                    var result = await _sync.SyncAsync(flow, emp, windows, DateTime.Now, delayMinutes);
                    newCount += result.Fetched;
                }
                catch
                {
                    // 单流程失败不阻断其他流程/后续轮询（自动取数不弹阻断错误）
                }
            }
            if (newCount > 0)
                _dialog.Info($"已自动取数：新增 {newCount} 个批次。");
        }
        finally { _busy = false; }
    }
}
