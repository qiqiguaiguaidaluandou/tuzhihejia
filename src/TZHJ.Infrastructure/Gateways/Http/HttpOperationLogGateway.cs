using System.Net.Http.Json;
using TZHJ.Core.Contracts;
using TZHJ.Core.Contracts.Http;

namespace TZHJ.Infrastructure.Gateways.Http;

/// <summary>
/// 真 HTTP 操作日志网关：POST /api/oplog 上报、GET /api/oplog/mine 查本人。
/// 身份以令牌为准（工号由后端盖章/过滤）。
/// </summary>
public sealed class HttpOperationLogGateway : IOperationLogGateway
{
    private readonly HttpClient _http;

    public HttpOperationLogGateway(HttpClient http) => _http = http;

    public async Task RecordAsync(OperationLogEntry entry, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/api/oplog", entry, HttpJson.Options, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ListMineAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetFromJsonAsync<OperationLogListResponse>("/api/oplog/mine", HttpJson.Options, ct);
        return resp?.Items ?? new List<OperationLogEntry>();
    }
}
