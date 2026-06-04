using TZHJ.Core.Enums;
using TZHJ.Core.Schemas;
using TZHJ.Infrastructure.Sync;

namespace TZHJ.Tests.Sync;

public class BatchSyncServiceTests
{
    private const string Emp = "10086";

    // 下午两点：核价应只补"已关闭"窗——今天下午批(09:31~15:30)未关闭、不补。
    private static readonly DateTime Now = new(2026, 5, 27, 14, 0, 0);
    private static readonly DateTime TodayPmStart = new(2026, 5, 27, 9, 31, 0); // 今天下午批起（应被排除）

    [Fact]
    public void ClosedWindows_respects_custom_delay_minutes()
    {
        // 15:45 时，刚好有一个 15:30 结束的窗。
        var now = new DateTime(2026, 5, 27, 15, 45, 0);
        
        // 默认 1min 缓冲：应该包含 15:30 结束的窗。
        var delay1 = BatchSyncService.ClosedWindows(CollectionSchedules.Pricing, now, delayMinutes: 1).ToList();
        Assert.Contains(delay1, w => w.End == new DateTime(2026, 5, 27, 15, 30, 0));

        // 30min 缓冲：应该排除 15:30 结束的窗（需等到 16:00）。
        var delay30 = BatchSyncService.ClosedWindows(CollectionSchedules.Pricing, now, delayMinutes: 30).ToList();
        Assert.DoesNotContain(delay30, w => w.End == new DateTime(2026, 5, 27, 15, 30, 0));
    }

    private static (BatchSyncService svc, FakeLocalBatchStore store, FakeDataGateway data, FakeAuditGateway audit) Build()
    {
        var store = new FakeLocalBatchStore();
        var data = new FakeDataGateway();
        var audit = new FakeAuditGateway();
        return (new BatchSyncService(store, data, audit), store, data, audit);
    }

    [Fact]
    public void ClosedWindows_excludes_unclosed_and_future_windows()
    {
        var windows = BatchSyncService.ClosedWindows(CollectionSchedules.Pricing, Now).ToList();

        // 14:00 时核价已关闭窗：今天上午批 + 昨天上午批 + 昨天下午批 = 3。
        Assert.Equal(3, windows.Count);
        // 今天下午批（09:31~15:30，未关闭）必须不在内。
        Assert.DoesNotContain(windows, w => w.Start == TodayPmStart);
        // 全部 End 都 <= now。
        Assert.All(windows, w => Assert.True(w.End <= Now));
    }

    [Fact]
    public async Task Sync_fetches_all_closed_windows_when_local_empty_and_audit_miss()
    {
        var (svc, store, data, _) = Build();

        var result = await svc.SyncAsync(FlowType.Pricing, Emp, CollectionSchedules.Pricing, Now);

        Assert.Equal(3, result.Fetched);
        Assert.Equal(3, store.Written.Count);
        Assert.Equal(0, result.SkippedLocal);
        Assert.Equal(0, result.SkippedAudit);
        Assert.Equal(0, result.Failed);
        // 没有任何取数请求落在"今天下午批"这个未关闭窗上。
        Assert.DoesNotContain(data.Requests, r => r.WindowStart == TodayPmStart);
    }

    [Fact]
    public async Task Sync_skips_window_already_present_locally()
    {
        var (svc, store, data, audit) = Build();
        var closed = BatchSyncService.ClosedWindows(CollectionSchedules.Pricing, Now).ToList();
        store.Seed(FlowType.Pricing, Emp, closed[0].Start, closed[0].End); // 本地已有第一个窗

        var result = await svc.SyncAsync(FlowType.Pricing, Emp, CollectionSchedules.Pricing, Now);

        Assert.Equal(1, result.SkippedLocal);
        Assert.Equal(2, result.Fetched);
        // 本地已有的窗不查审计、不取数。
        Assert.DoesNotContain(data.Requests, r => r.WindowStart == closed[0].Start && r.WindowEnd == closed[0].End);
        Assert.Equal(2, audit.Calls);
    }

    [Fact]
    public async Task Sync_skips_window_when_audit_hits_and_does_not_refetch()
    {
        var (svc, store, data, audit) = Build();
        var closed = BatchSyncService.ClosedWindows(CollectionSchedules.Pricing, Now).ToList();
        var hit = closed[0];
        audit.Hit = (_, ws, we) => ws == hit.Start && we == hit.End; // 该窗已回传过

        var result = await svc.SyncAsync(FlowType.Pricing, Emp, CollectionSchedules.Pricing, Now);

        Assert.Equal(1, result.SkippedAudit);
        Assert.Equal(2, result.Fetched);
        Assert.DoesNotContain(data.Requests, r => r.WindowStart == hit.Start && r.WindowEnd == hit.End); // 命中即不重拉
        Assert.DoesNotContain(store.Written, w => w.WindowStart == hit.Start && w.WindowEnd == hit.End);
    }

    [Fact]
    public async Task Sync_isolates_per_window_failure()
    {
        var (svc, store, data, _) = Build();
        var closed = BatchSyncService.ClosedWindows(CollectionSchedules.Pricing, Now).ToList();
        var bad = closed[1];
        data.Responder = req =>
        {
            if (req.WindowStart == bad.Start && req.WindowEnd == bad.End)
                throw new InvalidOperationException("取数炸了");
            return new TZHJ.Core.Contracts.FetchResult
            {
                Success = true, Flow = req.Flow, EmployeeId = req.EmployeeId,
                WindowStart = req.WindowStart, WindowEnd = req.WindowEnd,
            };
        };

        var result = await svc.SyncAsync(FlowType.Pricing, Emp, CollectionSchedules.Pricing, Now);

        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Fetched); // 其余窗不受影响
        Assert.DoesNotContain(store.Written, w => w.WindowStart == bad.Start && w.WindowEnd == bad.End);
    }

    [Fact]
    public async Task Sync_treats_fetch_success_false_as_failure_not_throw()
    {
        var (svc, _, data, _) = Build();
        data.Responder = req => new TZHJ.Core.Contracts.FetchResult
        {
            Success = false, Flow = req.Flow, EmployeeId = req.EmployeeId,
            WindowStart = req.WindowStart, WindowEnd = req.WindowEnd, Message = "源系统忙",
        };

        var result = await svc.SyncAsync(FlowType.Pricing, Emp, CollectionSchedules.Pricing, Now);

        Assert.Equal(0, result.Fetched);
        Assert.Equal(3, result.Failed);
    }
}
