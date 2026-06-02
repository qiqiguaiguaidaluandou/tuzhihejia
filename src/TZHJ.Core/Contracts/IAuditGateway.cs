using TZHJ.Core.Contracts.Http;
using TZHJ.Core.Enums;

namespace TZHJ.Core.Contracts;

/// <summary>
/// 审计查询网关（第 5 个网关契约，只读）：查某窗口是否已成功回传过。
/// 供补拉判据——本地无该窗 + 审计命中（已处理过）→ 不补拉，避免把已完成的窗重新拉回/重传。
/// 由 HttpAuditGateway 实现（GET /api/audit/exists）。
/// </summary>
public interface IAuditGateway
{
    Task<AuditExistsResponse> ExistsAsync(
        FlowType flow, DateTime windowStart, DateTime windowEnd, CancellationToken ct = default);
}
