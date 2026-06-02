using TZHJ.Core.Contracts.Http;

namespace TZHJ.Core.Contracts;

/// <summary>
/// 用户操作日志网关：记录一次操作（回传/补回传成功后）+ 查询本人操作记录。
/// 集中上报后端——管理员在服务器侧查全部，操作员在 App 内只看自己（后端按令牌工号过滤）。
/// 由 HttpOperationLogGateway 实现（POST /api/oplog、GET /api/oplog/mine）。
/// </summary>
public interface IOperationLogGateway
{
    /// <summary>记一条操作日志。失败由调用方吞掉（fire-and-forget，不应影响主流程）。</summary>
    Task RecordAsync(OperationLogEntry entry, CancellationToken ct = default);

    /// <summary>查当前操作员本人的操作记录（新到旧）。</summary>
    Task<IReadOnlyList<OperationLogEntry>> ListMineAsync(CancellationToken ct = default);
}
