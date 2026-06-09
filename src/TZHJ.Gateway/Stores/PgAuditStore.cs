using TZHJ.Core.Enums;

namespace TZHJ.Gateway.Stores;

/// <summary>审计存储 PostgreSQL 实现。</summary>
public sealed class PgAuditStore : IAuditStore
{
    private readonly TzhjDbContext _db;

    public PgAuditStore(TzhjDbContext db) => _db = db;

    public string Record(FlowType flow, string employeeId, string batchKey, DateTime windowStart, DateTime windowEnd, string target, int rowCount)
    {
        var auditId = $"AUDIT-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..36];
        var record = new AuditRecord
        {
            AuditId = auditId,
            Flow = flow,
            EmployeeId = employeeId,
            BatchKey = batchKey,
            WindowStart = windowStart.ToUniversalTime(),
            WindowEnd = windowEnd.ToUniversalTime(),
            Target = target,
            RowCount = rowCount,
            SubmittedAt = DateTime.UtcNow,
        };

        _db.AuditRecords.Add(record);
        _db.SaveChanges();
        return auditId;
    }

    public (bool Exists, string? AuditId) Find(FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd)
    {
        // 补拉判据：精确匹配流程、工号、窗口起止。
        // 注意：数据库存的是 UTC，查询时也应转为 UTC 比较。
        var ws = windowStart.ToUniversalTime();
        var we = windowEnd.ToUniversalTime();

        var hit = _db.AuditRecords.FirstOrDefault(r =>
            r.Flow == flow &&
            r.EmployeeId == employeeId &&
            r.WindowStart == ws &&
            r.WindowEnd == we);

        return hit is null ? (false, null) : (true, hit.AuditId);
    }
}
