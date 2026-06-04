using TZHJ.Core.Contracts;
using TZHJ.Core.Enums;
using TZHJ.Core.Models;

namespace TZHJ.Infrastructure.Sync;

/// <summary>
/// 补拉编排（手动补拉 / 登录补拉 / 会话内定时触发 共用同一套）。对某流程：
/// 取"已关闭（已到触发时刻）"的时间窗 → 本地已有则跳 → 审计命中则跳（已回传过，不重拉，D6） → 否则取数落本地。
/// 幂等：重复调用只会补真正缺的窗，故定时轮询安全。无 WPF 依赖，可跨平台单测（时间经 now 参数注入）。
/// </summary>
public sealed class BatchSyncService
{
    private readonly ILocalBatchStore _store;
    private readonly IDataGateway _data;
    private readonly IAuditGateway _audit;

    public BatchSyncService(ILocalBatchStore store, IDataGateway data, IAuditGateway audit)
    {
        _store = store;
        _data = data;
        _audit = audit;
    }

    /// <summary>
    /// 补齐该流程下"已关闭、本地无、审计未命中"的窗。<paramref name="now"/> 由调用方注入（一般 DateTime.Now）。
    /// <paramref name="delayMinutes"/> 表示窗口关闭后需等待多少分钟才触发（默认 1 分钟）。
    /// </summary>
    public async Task<BatchSyncResult> SyncAsync(
        FlowType flow, string employeeId, IReadOnlyList<CollectionWindow> windows, DateTime now, int delayMinutes = 1, CancellationToken ct = default)
    {
        var result = new BatchSyncResult();

        foreach (var (start, end) in ClosedWindows(windows, now, delayMinutes))
        {
            ct.ThrowIfCancellationRequested();

            if (_store.BatchExists(flow, employeeId, start, end))
            {
                result.SkippedLocal++;
                continue;
            }

            try
            {
                var audit = await _audit.ExistsAsync(flow, start, end, ct);
                if (audit.Exists)
                {
                    result.SkippedAudit++; // 已回传过 → 不重拉（D6）
                    continue;
                }

                var fetched = await _data.FetchBatchAsync(
                    new FetchRequest { EmployeeId = employeeId, Flow = flow, WindowStart = start, WindowEnd = end }, ct);

                if (fetched.Success)
                    result.NewBatches.Add(await _store.WriteFetchedBatchAsync(fetched, ct));
                else
                    result.Failed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                result.Failed++; // 单窗失败隔离：不影响其他窗
            }
        }

        return result;
    }

    /// <summary>
    /// 今天 + 昨天锚定下、**已到触发时刻（窗口关闭后 delayMinutes 分钟）** 的窗，按止时间倒序。
    /// 尚未关闭或未达缓冲时间的窗一律排除。
    /// </summary>
    public static IEnumerable<(DateTime Start, DateTime End)> ClosedWindows(
        IReadOnlyList<CollectionWindow> windows, DateTime now, int delayMinutes = 1)
    {
        var today = DateOnly.FromDateTime(now);
        var list = new List<(DateTime Start, DateTime End)>();
        foreach (var anchor in new[] { today, today.AddDays(-1) })
            foreach (var w in windows.Where(w => w.Enabled))
            {
                var triggerTime = anchor.ToDateTime(w.EndTime).AddMinutes(delayMinutes);
                if (triggerTime > now) continue; // 未到触发时刻
                list.Add(w.Resolve(anchor));
            }
        return list.OrderByDescending(x => x.End);
    }
}

/// <summary>补拉一轮的结果。</summary>
public sealed class BatchSyncResult
{
    public List<Batch> NewBatches { get; } = new();
    public int SkippedLocal { get; set; }
    public int SkippedAudit { get; set; }
    public int Failed { get; set; }

    public int Fetched => NewBatches.Count;
}
