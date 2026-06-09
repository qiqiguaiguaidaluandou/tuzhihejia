using System.Collections.Concurrent;
using TZHJ.Core.Enums;

namespace TZHJ.Gateway.Stores;

/// <summary>审计存储内存实现（骨架）。重启即清空——上线必须落 PostgreSQL（追溯刚需）。</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly ConcurrentBag<AuditRecord> _records = new();

    public string Record(FlowType flow, string employeeId, string batchKey, DateTime windowStart, DateTime windowEnd, string target, int rowCount)
    {
        var auditId = $"AUDIT-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..36];
        _records.Add(new AuditRecord
        {
            AuditId = auditId,
            Flow = flow,
            EmployeeId = employeeId,
            BatchKey = batchKey,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Target = target,
            RowCount = rowCount,
            SubmittedAt = DateTime.Now,
        });
        return auditId;
    }

    public (bool Exists, string? AuditId) Find(FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd)
    {
        var hit = _records.FirstOrDefault(r =>
            r.Flow == flow && r.EmployeeId == employeeId &&
            r.WindowStart == windowStart && r.WindowEnd == windowEnd);
        return hit is null ? (false, null) : (true, hit.AuditId);
    }
}
