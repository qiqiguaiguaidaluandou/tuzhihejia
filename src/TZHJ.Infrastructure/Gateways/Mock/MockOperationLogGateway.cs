using System.Collections.Concurrent;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;

namespace TZHJ.Infrastructure.Gateways.Mock;

/// <summary>
/// 占位操作日志网关：进程内内存留存，当前会话内"操作日志"页可见自己刚才的记录。
/// 离线模式单操作员，ListMine 直接返回全部（新到旧）。重启即清空——真接入后用 HttpOperationLogGateway。
/// </summary>
public sealed class MockOperationLogGateway : IOperationLogGateway
{
    private readonly ConcurrentQueue<OperationLogEntry> _entries = new();

    public Task RecordAsync(OperationLogEntry entry, CancellationToken ct = default)
    {
        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OperationLogEntry>> ListMineAsync(CancellationToken ct = default)
    {
        IReadOnlyList<OperationLogEntry> list = _entries.OrderByDescending(e => e.OperatedAt).ToList();
        return Task.FromResult(list);
    }
}
