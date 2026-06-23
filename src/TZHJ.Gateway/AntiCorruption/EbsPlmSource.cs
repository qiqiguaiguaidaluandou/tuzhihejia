using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TZHJ.Core.Enums;
using TZHJ.Core.Schemas;

namespace TZHJ.Gateway.AntiCorruption;

/// <summary>
/// 真实 EBS 取数实现（路线图 B1）。两个接口同一端点，靠 P_IFACE_CODE 区分：
///   核价 CUX_AI_DRW_COST（带 GROUP_NAME 分组）、挑图 CUX_AI_MACH_DRW（不分组）。
/// 时间窗口由调用方（DataIngestionService）按项目规则给定，这里只负责取数与字段映射。
///
/// 注意：图纸文件与"是否变更"(hasChange) 来自 PLM，本类暂不涉及——
///   OpenDrawingAsync 返回 null（UI 标"缺失"），hasChange 字段留空，待 PLM 接口文档到位后补（见 TODO）。
/// </summary>
public sealed class EbsPlmSource : IEbsPlmSource
{
    private readonly HttpClient _http;
    private readonly EbsOptions _opt;
    private readonly EbsTokenProvider _token;
    private readonly ILogger<EbsPlmSource> _logger;

    public EbsPlmSource(HttpClient http, EbsOptions opt, EbsTokenProvider token, ILogger<EbsPlmSource> logger)
    {
        _http = http;
        _opt = opt;
        _token = token;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SourceRow>> FetchRowsAsync(
        FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd, CancellationToken ct = default)
    {
        var ifaceCode = flow == FlowType.Pricing ? _opt.PricingIfaceCode : _opt.DrawingIfaceCode;
        var url = flow == FlowType.Pricing ? _opt.PricingUrl : _opt.DrawingUrl;

        // P_BATCH_NUMBER = "AI" + 调用时刻时间戳；P_REQUEST_DATA 是一段 JSON 字符串。
        var batchNumber = "AI" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var requestData = $"{{\"DATETIME_FROM\":\"{windowStart:yyyy-MM-dd HH:mm:ss}\",\"DATETIME_TO\":\"{windowEnd:yyyy-MM-dd HH:mm:ss}\"}}";

        var bodyJson = JsonSerializer.Serialize(new
        {
            P_BATCH_NUMBER = batchNumber,
            P_IFACE_CODE = ifaceCode,
            P_REQUEST_DATA = requestData,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(_opt.AuthScheme, _token.Create());

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("EBS 取数 HTTP {Status}：{Body}", (int)resp.StatusCode, Truncate(raw));
            throw new HttpRequestException($"EBS 取数失败 HTTP {(int)resp.StatusCode}");
        }

        var dataArray = ParseEnvelope(raw, batchNumber);
        return flow == FlowType.Pricing ? MapPricing(dataArray) : MapDrawing(dataArray);
    }

    /// <summary>图纸字节来自 PLM，EBS 接口不提供。PLM 接口到位前返回 null（→404 / UI 标"缺失"）。</summary>
    public Task<byte[]?> OpenDrawingAsync(
        FlowType flow, string employeeId, DateTime windowStart, DateTime windowEnd, string drawingId, CancellationToken ct = default)
    {
        // TODO(PLM): 接 PLM 接口，按物料编码取图纸字节。
        return Task.FromResult<byte[]?>(null);
    }

    // ===== 解析：外层 OutputParameters → X_RETURN_CODE 判错 → X_RESPONSE_DATA(字符串)二次解析 → DATA 数组 =====

    private JsonElement ParseEnvelope(string raw, string batchNumber)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("OutputParameters", out var op))
            throw new InvalidOperationException("EBS 响应缺少 OutputParameters。");

        var returnCode = Str(op, "X_RETURN_CODE");
        if (!string.Equals(returnCode, "S", StringComparison.OrdinalIgnoreCase))
        {
            var msg = Str(op, "X_RETURN_MESG");
            _logger.LogError("EBS 返回失败 code={Code} msg={Msg} batch={Batch}", returnCode, msg, batchNumber);
            throw new InvalidOperationException($"EBS 返回非成功：code={returnCode}, msg={msg}");
        }

        var responseData = Str(op, "X_RESPONSE_DATA");
        if (string.IsNullOrWhiteSpace(responseData))
            return default; // 无数据

        // X_RESPONSE_DATA 本身是 JSON 字符串，需二次解析。clone 以便 doc 释放后仍可用。
        using var inner = JsonDocument.Parse(responseData);
        if (!inner.RootElement.TryGetProperty("DATA", out var data) || data.ValueKind != JsonValueKind.Array)
            return default;
        return data.Clone();
    }

    // ===== 字段映射（EBS 名 → 项目内部 key，见 docs/接口文档.md）=====

    private static IReadOnlyList<SourceRow> MapPricing(JsonElement data)
    {
        var rows = new List<SourceRow>();
        if (data.ValueKind != JsonValueKind.Array) return rows;

        foreach (var r in data.EnumerateArray())
        {
            var code = Str(r, "ITEM_CODE") ?? "";
            rows.Add(new SourceRow
            {
                RowKey = code,
                GroupName = Str(r, "GROUP_NAME"),   // 分组依据
                Values = new Dictionary<string, string?>
                {
                    [FieldSchemas.PricingKeys.MaterialCode] = code,
                    [FieldSchemas.PricingKeys.Model] = Str(r, "ITEM_MODEL"),
                    [FieldSchemas.PricingKeys.Name] = Str(r, "ITEM_NAME"),
                    [FieldSchemas.PricingKeys.MaterialDesc] = Str(r, "ITEM_DESC"),
                    [FieldSchemas.PricingKeys.DemandQty] = Str(r, "TTL_QTY"),
                    [FieldSchemas.PricingKeys.HasChange] = null,   // TODO(PLM)
                    [FieldSchemas.PricingKeys.TargetPrice] = null, // 手填
                },
            });
        }
        return rows;
    }

    private static IReadOnlyList<SourceRow> MapDrawing(JsonElement data)
    {
        var rows = new List<SourceRow>();
        if (data.ValueKind != JsonValueKind.Array) return rows;

        foreach (var r in data.EnumerateArray())
        {
            var seqId = Str(r, "SEQ_ID") ?? "";
            rows.Add(new SourceRow
            {
                RowKey = seqId,
                GroupName = null, // 挑图不分组
                Values = new Dictionary<string, string?>
                {
                    [FieldSchemas.DrawingKeys.EbsId] = seqId,
                    [FieldSchemas.DrawingKeys.InvOrg] = Str(r, "ORGANIZATION_CODE"),
                    [FieldSchemas.DrawingKeys.SourceNo] = Str(r, "SOURCE_DOC_NUMBER"),
                    [FieldSchemas.DrawingKeys.Project] = Str(r, "PROJECT_CODE"),
                    [FieldSchemas.DrawingKeys.ProductLine] = Str(r, "PROD_CODE"),
                    [FieldSchemas.DrawingKeys.PlanNo] = Str(r, "INTER_PROJECT_CODE"),
                    [FieldSchemas.DrawingKeys.DeptDesc] = Str(r, "DEPARTMENT"), // 接口暂未返回 → 容错为 null
                    [FieldSchemas.DrawingKeys.MaterialCode] = Str(r, "ITEM_CODE"),
                    [FieldSchemas.DrawingKeys.MaterialDesc] = Str(r, "ITEM_DESC"),
                    [FieldSchemas.DrawingKeys.CurrentQty] = Str(r, "OFFSET_CUR_QTY"),
                    [FieldSchemas.DrawingKeys.CreateDate] = Str(r, "CREATION_DATE"),
                    [FieldSchemas.DrawingKeys.DemandDate] = Str(r, "NEED_BY_DATE"),
                    [FieldSchemas.DrawingKeys.Applicant] = Str(r, "EMPLOYEE_NAME"),
                    [FieldSchemas.DrawingKeys.Remark] = Str(r, "REMARKS"),
                    [FieldSchemas.DrawingKeys.HasChange] = null,  // TODO(PLM)
                    [FieldSchemas.DrawingKeys.CanMachine] = null, // 手填
                },
            });
        }
        return rows;
    }

    /// <summary>安全读字符串字段：缺失/null 都返回 null；数值等非字符串也转成字符串。</summary>
    private static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            _ => v.ToString(),
        };
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
